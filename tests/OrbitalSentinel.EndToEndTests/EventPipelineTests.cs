using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using OrbitalSentinel.Api;
using OrbitalSentinel.Core;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Propagation;
using OrbitalSentinel.Runtime;
using OrbitalSentinel.Workers;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using Xunit;

namespace OrbitalSentinel.EndToEndTests;

public sealed class EventPipelineTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly KafkaContainer kafka = new KafkaBuilder("apache/kafka:4.0.1").WithKRaft().Build();
    private NpgsqlDataSource dataSource = null!;
    private KafkaOptions options = null!;
    private IProducer<string, string> producer = null!;
    private OutboxPublisher publisher = null!;
    private PropagationWorker propagation = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(database.StartAsync(), kafka.StartAsync());
        dataSource = NpgsqlDataSource.Create(database.GetConnectionString());
        await new DatabaseMigrator(dataSource).MigrateAsync(CancellationToken.None);
        options = new KafkaOptions { BootstrapServers = kafka.GetBootstrapAddress() };
        await KafkaTopics.EnsureAsync(options, true, CancellationToken.None);
        producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers, EnableIdempotence = true, Acks = Acks.All,
            MessageTimeoutMs = 10000
        }).Build();
        publisher = new OutboxPublisher(new OutboxRepository(dataSource), producer, Options.Create(options),
            Options.Create(new PredictionOptions { PrecomputeTrajectories = true }), NullLogger<OutboxPublisher>.Instance);
        propagation = new PropagationWorker(new CatalogRepository(dataSource), new TrajectoryRepository(dataSource),
            new ConsumerFailureRepository(dataSource), new Sgp4OrbitPropagator(), Options.Create(options),
            Options.Create(new PredictionOptions { PrecomputeTrajectories = true, HorizonHours = 1, StepSeconds = 60 }), NullLogger<PropagationWorker>.Instance);
        await publisher.StartAsync(CancellationToken.None);
        await propagation.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task On_demand_visualization_works_without_orbit_events_or_trajectory_consumption()
    {
        await propagation.StopAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync();
        var clock = new MotionClock { Now = DateTimeOffset.UtcNow };
        using var factory = new WebApplicationFactory<ApiAssemblyMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", database.GetConnectionString());
            builder.UseSetting("Kafka:BootstrapServers", options.BootstrapServers);
            builder.UseSetting("Prediction:PrecomputeTrajectories", "false");
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock));
        });
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<CatalogRepository>();
        await catalog.UpsertAsync(new CatalogImport(id, null, "On-demand spacecraft", null, "Unknown",
            new OrbitalElements(DateTimeOffset.UtcNow, 15.5, 0.0004, 51.64, 20, 30, 40, 0.00005, 0, 0)));
        var path = await client.GetFromJsonAsync<Trajectory>($"/api/orbital-objects/{id}/trajectory");
        Assert.NotNull(path);
        Assert.Equal(1441, path.Points.Count);
        Assert.All(path.Points, p => Assert.True(p.SpeedKilometersPerSecond > 0));
        var positions = await client.GetFromJsonAsync<ObjectPosition[]>("/api/orbital-objects/positions");
        Assert.Equal(id, Assert.Single(positions!).OrbitalObjectId);
        var motionResponse = await client.GetAsync("/api/orbital-objects/motion");
        var motion = await motionResponse.Content.ReadFromJsonAsync<CatalogMotion>();
        Assert.Equal(id, Assert.Single(motion!.Objects).OrbitalObjectId);
        Assert.Equal(30, motion.Objects[0].States.Length);
        Assert.All(path.Points, p => Assert.NotNull(p.VelocityEcefMetersPerSecond));
        Assert.All(path.Points, p => Assert.NotNull(p.OrbitView));
        // Concurrent viewers share the same generated buffer, including its identity.
        var copies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.GetFromJsonAsync<CatalogMotion>("/api/orbital-objects/motion")));
        Assert.All(copies, copy => Assert.Equal(copies[0]!.Id, copy!.Id));
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/orbital-objects/motion");
        conditional.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{copies[0]!.Id}\""));
        var unchanged = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        clock.Now = clock.Now.AddMinutes(1);
        using var refresh = new HttpRequestMessage(HttpMethod.Get, "/api/orbital-objects/motion");
        refresh.Headers.IfNoneMatch.Add(conditional.Headers.IfNoneMatch.Single());
        var refreshed = await client.SendAsync(refresh);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var next = (await refreshed.Content.ReadFromJsonAsync<CatalogMotion>())!;
        Assert.NotEqual(motion.Id, next.Id);
        Assert.Equal(motion.StartUtc.AddMinutes(1), next.StartUtc);
        Assert.Equal(motion.Objects[0].States.Skip(12), next.Objects[0].States.Take(18));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM trajectory_predictions"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM outbox_messages"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM processed_messages"));
    }

    [Fact]
    public async Task Orbit_update_traverses_outbox_and_Kafka_into_idempotent_trajectory_API_and_SignalR()
    {
        var id = Guid.NewGuid();
        using var factory = new WebApplicationFactory<ApiAssemblyMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", database.GetConnectionString());
            builder.UseSetting("Kafka:BootstrapServers", options.BootstrapServers);
            builder.UseSetting("Prediction:PrecomputeTrajectories", "true");
        });
        using var client = factory.CreateClient();
        await using var hub = new HubConnectionBuilder().WithUrl(new Uri(client.BaseAddress!, "/hubs/traffic"), transport =>
        {
            transport.Transports = HttpTransportType.LongPolling;
            transport.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
        }).Build();
        var notification = new TaskCompletionSource<TrajectoryAvailable>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = hub.On<TrajectoryAvailable>("TrajectoryAvailable", message =>
        {
            if (message.OrbitalObjectId == id) notification.TrySetResult(message);
        });
        using var hubTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await hub.StartAsync(hubTimeout.Token);
        // The relay uses a unique Kafka group with Latest offsets. Wait until it can receive a fact,
        // rather than assuming that HTTP or SignalR startup means the broker assignment is ready.
        await WaitForRelayAsync(hub, hubTimeout.Token);

        var epoch = DateTimeOffset.UtcNow;
        var catalog = new CatalogRepository(dataSource);
        var trajectories = new TrajectoryRepository(dataSource);
        // Synthetic orbital inputs are test-only, never silently seeded into the live catalog.
        await catalog.UpsertAsync(new CatalogImport(id, null, "Integration test spacecraft", null, "Unknown",
            new OrbitalElements(epoch, 15.5, 0.0004, 51.64, 20, 30, 40, 0.00005, 0, 0)));

        var trajectory = await EventuallyAsync(async () => await trajectories.GetAsync(id));
        Assert.Equal(61, trajectory.Points.Count);
        Assert.Equal(1, trajectory.OrbitVersion);
        var delivered = await notification.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(id, delivered.OrbitalObjectId);
        Assert.Equal(trajectory.OrbitVersion, delivered.OrbitVersion);
        Assert.Equal(trajectory.StartUtc, delivered.StartUtc);
        Assert.Equal(trajectory.EndUtc, delivered.EndUtc);

        await using var connection = await dataSource.OpenConnectionAsync();
        var payload = await connection.QuerySingleAsync<string>(
            "SELECT payload::text FROM outbox_messages WHERE orbital_object_id=@id AND event_type='orbit.updated.v1'", new { id });
        var eventId = JsonSerializer.Deserialize<OrbitUpdatedEvent>(payload, Serialization.Options)!.EventId;
        var duplicate = await producer.ProduceAsync(options.OrbitTopic, new Message<string, string> { Key = id.ToString("D"), Value = payload });
        // Query the group's committed offset without subscribing this observer. This proves that the
        // actual background consumer completed the redelivery before checking durable idempotency.
        using var offsetObserver = KafkaTopics.CreateConsumer(options, options.PropagationGroup, AutoOffsetReset.Earliest);
        await EventuallyAsync(() => Task.FromResult(offsetObserver.Committed([duplicate.TopicPartition],
            TimeSpan.FromSeconds(3))[0].Offset > duplicate.Offset));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM processed_messages WHERE event_id=@eventId", new { eventId }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM outbox_messages WHERE orbital_object_id=@id AND event_type='trajectory.updated.v1'", new { id }));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        var returned = await client.GetFromJsonAsync<Trajectory>($"/api/orbital-objects/{id}/trajectory");
        Assert.Equal(id, returned!.OrbitalObjectId);
        Assert.All(returned.Points, point => Assert.NotNull(point.OrbitView));
        Assert.All(returned.Points, point => Assert.InRange(point.SpeedKilometersPerSecond!.Value, 7, 8));
        var details = await client.GetFromJsonAsync<JsonElement>($"/api/orbital-objects/{id}/details");
        Assert.Equal(id, details.GetProperty("orbitalObjectId").GetGuid());
        Assert.Equal(trajectory.OrbitVersion, details.GetProperty("orbitVersion").GetInt64());
        Assert.Equal(JsonValueKind.Null, details.GetProperty("metadata").ValueKind);
        Assert.InRange(details.GetProperty("meanOrbit").GetProperty("perigeeKilometers").GetDouble(), 400, 430);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/orbital-objects/{Guid.NewGuid()}/details")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/orbital-objects?pageSize=999")).StatusCode);
        var notFound = await client.GetAsync($"/api/orbital-objects/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("application/problem+json", notFound.Content.Headers.ContentType!.MediaType);
        var openApi = await client.GetStringAsync("/openapi/v1.json");
        Assert.Contains("/api/orbital-objects", openApi);

        // TrajectoryUpdated is a durable Kafka fact for later conjunction processing, not merely UI decoration.
        using var subscriber = KafkaTopics.CreateConsumer(options, $"test-observer-{Guid.NewGuid()}", AutoOffsetReset.Earliest);
        subscriber.Subscribe(options.TrajectoryTopic);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var record = subscriber.Consume(timeout.Token);
            var published = JsonSerializer.Deserialize<TrajectoryUpdatedEvent>(record.Message.Value, Serialization.Options)!;
            if (published.OrbitalObjectId != id) continue;
            Assert.Equal(id.ToString("D"), record.Message.Key);
            Assert.Equal(1, published.SchemaVersion);
            break;
        }
        subscriber.Close();
    }

    [Fact]
    public async Task Poison_record_is_quarantined_and_does_not_block_following_valid_work()
    {
        var id = Guid.NewGuid();
        var key = id.ToString("D");
        var sent = await producer.ProduceAsync(options.OrbitTopic, new Message<string, string> { Key = key, Value = "not-json" });
        await EventuallyAsync(async () =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT payload FROM consumer_failures WHERE topic=@topic AND partition=@partition AND offset_position=@offset",
                new { topic = sent.Topic, partition = sent.Partition.Value, offset = sent.Offset.Value });
        });
        // Repeating durable quarantine is safe after a crash between DB commit and Kafka offset commit.
        var quarantine = new ConsumerFailureRepository(dataSource);
        await quarantine.QuarantineAsync(sent.Topic, sent.Partition.Value, sent.Offset.Value, key, "not-json", "invalid", CancellationToken.None);
        await using var db = await dataSource.OpenConnectionAsync();
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM consumer_failures WHERE message_key=@key", new { key }));

        // Reuse the exact partition key so the valid record must advance past the poison record.
        await new CatalogRepository(dataSource).UpsertAsync(new CatalogImport(id, null, "After poison", null, "Unknown",
            new OrbitalElements(DateTimeOffset.UtcNow, 15.4, .001, 52, 30, 40, 50, .00001, 0, 0)));
        await EventuallyAsync(async () => await new TrajectoryRepository(dataSource).GetAsync(id));
    }

    private async Task WaitForRelayAsync(HubConnection hub, CancellationToken cancellationToken)
    {
        var probeId = Guid.NewGuid();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = hub.On<TrajectoryAvailable>("TrajectoryAvailable", message =>
        {
            if (message.OrbitalObjectId == probeId) ready.TrySetResult();
        });
        while (!ready.Task.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var probe = new TrajectoryUpdatedEvent(Guid.NewGuid(), TrajectoryUpdatedEvent.TypeName,
                now, Guid.NewGuid(), 1, probeId, 1, now, now.AddHours(1));
            await producer.ProduceAsync(options.TrajectoryTopic, new Message<string, string>
            {
                Key = probeId.ToString("D"), Value = JsonSerializer.Serialize(probe, Serialization.Options)
            }, cancellationToken);
            await Task.WhenAny(ready.Task, Task.Delay(300, cancellationToken));
        }
        await ready.Task;
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!await condition()) await Task.Delay(200, timeout.Token);
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> read) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            var result = await read();
            if (result is not null) return result;
            await Task.Delay(200, timeout.Token);
        }
    }

    public async Task DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (propagation is not null) { await propagation.StopAsync(timeout.Token); propagation.Dispose(); }
        if (publisher is not null) { await publisher.StopAsync(timeout.Token); publisher.Dispose(); }
        producer?.Dispose();
        if (dataSource is not null) await dataSource.DisposeAsync();
        await Task.WhenAll(database.DisposeAsync().AsTask(), kafka.DisposeAsync().AsTask());
    }
    private sealed class MotionClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
