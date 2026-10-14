using Microsoft.Extensions.Options;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Runtime;
using OrbitalSentinel.Workers;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddOrbitalRuntime(builder.Configuration);
builder.Services.AddOrbitalWorkers(builder.Configuration);
var app = builder.Build();
await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync(app.Lifetime.ApplicationStopping);
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapHealthChecks("/health/ready");
await app.RunAsync();

public partial class Program;
