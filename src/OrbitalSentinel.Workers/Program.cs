using Microsoft.Extensions.Options;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Runtime;
using OrbitalSentinel.Workers;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddOrbitalRuntime(builder.Configuration);
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<PropagationWorker>();
builder.Services.AddHostedService<PredictionRunCoordinator>();
builder.Services.AddHostedService<PredictionRunConsumer>();
builder.Services.AddHostedService<ConjunctionWorker>();
var app = builder.Build();
await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync(app.Lifetime.ApplicationStopping);
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapHealthChecks("/health/ready");
await app.RunAsync();

public partial class Program;
