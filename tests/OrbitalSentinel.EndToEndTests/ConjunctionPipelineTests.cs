using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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

public sealed class ConjunctionPipelineTests
{
    [Fact]
    public async Task Simulation_traverses_Kafka_scan_persistence_API_and_revision_events_without_duplicate_encounters()
    {
        await using var database = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await using var kafka = new KafkaBuilder("apache/kafka:4.0.1").WithKRaft().Build();
        await Task.WhenAll(database.StartAsync(), kafka.StartAsync());
        await using var dataSource = NpgsqlDataSource.Create(database.GetConnectionString());
        await new DatabaseMigrator(dataSource).MigrateAsync();
        var kafkaOptions = new KafkaOptions { BootstrapServers = kafka.GetBootstrapAddress() };
        await KafkaTopics.EnsureAsync(kafkaOptions, false, CancellationToken.None);
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = kafkaOptions.BootstrapServers, EnableIdempotence = true, Acks = Acks.All
        }).Build();
        var repository = new ConjunctionRepository(dataSource);
        using var publisher = new OutboxPublisher(new OutboxRepository(dataSource, publishTrajectories: false), producer,
            Options.Create(kafkaOptions), Options.Create(new PredictionOptions()), NullLogger<OutboxPublisher>.Instance);
        using var consumer = new PredictionRunConsumer(repository, new ConsumerFailureRepository(dataSource),
            Options.Create(kafkaOptions), Options.Create(new PredictionOptions()), NullLogger<PredictionRunConsumer>.Instance);
        using var worker = new ConjunctionWorker(repository, new ConjunctionScanner(), new Sgp4OrbitPropagator(),
            Options.Create(new ConjunctionOptions()), NullLogger<ConjunctionWorker>.Instance);
        await publisher.StartAsync(CancellationToken.None);
        await consumer.StartAsync(CancellationToken.None);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var factory = new WebApplicationFactory<ApiAssemblyMarker>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", database.GetConnectionString());
                builder.UseSetting("Kafka:BootstrapServers", kafkaOptions.BootstrapServers);
                builder.UseSetting("Prediction:PrecomputeTrajectories", "false");
            });
            using var client = factory.CreateClient();
            var missingKey = await client.PostAsync("/api/simulations", null);
            Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
            Assert.Equal("application/problem+json", missingKey.Content.Headers.ContentType?.MediaType);
            var key = Guid.NewGuid();
            var runId = await StartSimulation(client, key);
            await Eventually(async () => (await repository.GetRunAsync(runId))?.Status == "Completed");
            Assert.Equal(runId, await StartSimulation(client, key));
            var first = await repository.ListAsync(new ConjunctionQuery(Source: "Simulation", PredictionRunId: runId, PageSize: 100));
            Assert.NotEmpty(first.Items);
            var inferredSource = await client.GetFromJsonAsync<Page<ConjunctionSummary>>($"/api/conjunctions?predictionRunId={runId}", Serialization.Options);
            Assert.NotNull(inferredSource);
            Assert.NotEmpty(inferredSource.Items);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/conjunctions?to=2020-01-01T00:00:00Z")).StatusCode);
            Assert.All(first.Items, x => Assert.InRange(x.LatestAssessment.MissDistanceMeters, 0, 10_000));
            await using var db = await dataSource.OpenConnectionAsync();
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM orbital_objects"));
            var firstCount = (int)first.TotalCount;

            // Replay the exact broker fact, then wait for the consumer's committed offset to advance.
            var payload = await db.QuerySingleAsync<string>("SELECT payload::text FROM outbox_messages WHERE event_type=@type AND payload->>'predictionRunId'=@id",
                new { type = PredictionRunCompletedEvent.TypeName, id = runId.ToString() });
            var duplicate = await producer.ProduceAsync(kafkaOptions.PredictionRunTopic,
                new Message<string, string> { Key = runId.ToString(), Value = payload });
            using var observer = KafkaTopics.CreateConsumer(kafkaOptions, kafkaOptions.ConjunctionGroup, AutoOffsetReset.Earliest);
            await Eventually(() => Task.FromResult(observer.Committed([duplicate.TopicPartition], TimeSpan.FromSeconds(3))[0].Offset > duplicate.Offset));
            Assert.Equal(firstCount, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM conjunction_assessments"));

            var secondRun = await StartSimulation(client, Guid.NewGuid());
            await Eventually(async () => (await repository.GetRunAsync(secondRun))?.Status == "Completed");
            var second = await repository.ListAsync(new ConjunctionQuery(Source: "Simulation", PredictionRunId: secondRun, PageSize: 100));
            Assert.Equal(first.Items.Select(x => x.Id).Order(), second.Items.Select(x => x.Id).Order());
            Assert.Equal(firstCount, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM conjunctions"));
            Assert.Equal(firstCount * 2, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM conjunction_assessments"));
            Assert.Equal(firstCount, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM outbox_messages WHERE event_type=@type", new { type = ConjunctionIdentifiedEvent.TypeName }));
            Assert.Equal(firstCount * 2, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM outbox_messages WHERE event_type=@type", new { type = ConjunctionAssessmentCreatedEvent.TypeName }));
            await Eventually(async () => await db.ExecuteScalarAsync<int>("SELECT count(*) FROM outbox_messages WHERE published_at IS NULL") == 0);

            using var eventReader = KafkaTopics.CreateConsumer(kafkaOptions, "conjunction-e2e-observer-" + Guid.NewGuid(), AutoOffsetReset.Earliest);
            eventReader.Subscribe(kafkaOptions.ConjunctionTopic);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var eventTypes = new HashSet<string>();
            while (eventTypes.Count < 2)
            {
                var record = eventReader.Consume(timeout.Token);
                using var json = JsonDocument.Parse(record.Message.Value);
                eventTypes.Add(json.RootElement.GetProperty("eventType").GetString()!);
            }
            Assert.Contains(ConjunctionIdentifiedEvent.TypeName, eventTypes);
            Assert.Contains(ConjunctionAssessmentCreatedEvent.TypeName, eventTypes);
            eventReader.Close();

            var assessment = first.Items[0].LatestAssessment;
            var visual = await client.GetFromJsonAsync<ConjunctionVisualization>($"/api/conjunction-assessments/{assessment.Id}/visualization", Serialization.Options);
            Assert.NotNull(visual);
            Assert.Equal(runId, visual.PredictionRunId);
            Assert.Equal("Simulation", visual.Source);
            Assert.Contains(visual.PrimaryTrajectory.Points, x => x.TimestampUtc == assessment.TimeOfClosestApproachUtc && x.PositionEcefMeters == visual.PrimaryPositionEcefMeters);
            Assert.Contains(visual.SecondaryTrajectory.Points, x => x.TimestampUtc == assessment.TimeOfClosestApproachUtc && x.PositionEcefMeters == visual.SecondaryPositionEcefMeters);
            var a = visual.PrimaryPositionEcefMeters;
            var b = visual.SecondaryPositionEcefMeters;
            var distance = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
            Assert.InRange(Math.Abs(distance - assessment.MissDistanceMeters), 0, 0.00001);
            var real = await client.GetFromJsonAsync<Page<ConjunctionSummary>>("/api/conjunctions", Serialization.Options);
            Assert.NotNull(real);
            Assert.Empty(real.Items);
            var history = await repository.ListAssessmentsAsync(first.Items[0].Id);
            Assert.Equal(2, history.TotalCount);
            using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafkaOptions.BootstrapServers }).Build();
            var topics = admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(x => x.Topic).ToArray();
            Assert.DoesNotContain(kafkaOptions.OrbitTopic, topics);
            Assert.DoesNotContain(kafkaOptions.TrajectoryTopic, topics);
            Assert.Contains(kafkaOptions.PredictionRunTopic, topics);
            Assert.Contains(kafkaOptions.ConjunctionTopic, topics);
            Assert.Contains(kafkaOptions.DeadLetterTopic, topics);
            foreach (var query in new[] { "page=0", "pageSize=101", "source=invalid", "maxMissDistanceMeters=NaN", "from=not-a-date", "objectId=invalid" })
                Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/conjunctions?" + query)).StatusCode);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await worker.StopAsync(stop.Token);
            await consumer.StopAsync(stop.Token);
            await publisher.StopAsync(stop.Token);
        }
    }

    private static async Task<Guid> StartSimulation(HttpClient client, Guid key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/simulations");
        request.Headers.Add("Idempotency-Key", key.ToString());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!await condition()) await Task.Delay(200, timeout.Token);
    }
}
