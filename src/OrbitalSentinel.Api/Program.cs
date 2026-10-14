using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OrbitalSentinel.Api;
using OrbitalSentinel.Core;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Runtime;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024);
builder.Services.AddOrbitalRuntime(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddOptions<TrafficOptions>().Bind(builder.Configuration.GetSection("SignalR"))
    .ValidateDataAnnotations().Validate(x => x.ClientTimeoutSeconds > x.KeepAliveSeconds * 2,
        "Client timeout must exceed twice the keep-alive interval.").ValidateOnStart();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 4096);
builder.Services.AddOptions<Microsoft.AspNetCore.SignalR.HubOptions>().Configure<IOptions<TrafficOptions>>((options, traffic) =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(traffic.Value.KeepAliveSeconds);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(traffic.Value.ClientTimeoutSeconds);
});
builder.Services.AddHostedService<TrafficNotifications>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("read", limiter =>
    {
        limiter.PermitLimit = 600;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("simulation", limiter =>
    {
        limiter.PermitLimit = 6;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await Results.Problem(statusCode: 429, title: "Request limit reached. Try again shortly.")
            .ExecuteAsync(context.HttpContext);
    };
});

var app = builder.Build();
await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync(app.Lifetime.ApplicationStopping);
app.UseExceptionHandler();
app.UseResponseCompression();
app.UseStatusCodePages();
// Default known-proxy restrictions remain intact. Configure trusted proxies at a TLS ingress when deploying.
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
app.UseRateLimiter();
app.MapOpenApi();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapHealthChecks("/health/ready");
app.MapHub<TrafficHub>("/hubs/traffic");

var api = app.MapGroup("/api").RequireRateLimiting("read");
api.MapGet("/status", () => Results.Ok(new
{
    stage = "conjunction-screening",
    source = "Real",
    coordinateFrame = "TEME rotated by GMST to approximate Earth-fixed; metres",
    scientificUse = "Educational visualization; not for spacecraft operations."
}));

api.MapGet("/orbital-objects", async (CatalogRepository catalog, int? page, int? pageSize, string? search, CancellationToken ct) =>
{
    var pageNumber = page ?? 1;
    var size = pageSize ?? 50;
    if (pageNumber is < 1 or > 10000 || size is < 1 or > 500 || search?.Length > 100)
        return Results.Problem(statusCode: 400, title: "Use page 1–10000, pageSize 1–500, and a search of at most 100 characters.");
    return Results.Ok(await catalog.ListAsync(pageNumber, size, search?.Trim(), ct));
}).WithName("ListOrbitalObjects");

api.MapGet("/orbital-objects/positions", async (TrajectoryRepository trajectories, CatalogVisualization visualization,
    IOptions<PredictionOptions> prediction, CancellationToken ct) =>
    Results.Ok(prediction.Value.PrecomputeTrajectories
        ? await trajectories.ListPositionsAsync(DateTimeOffset.UtcNow, ct) : await visualization.GetPositionsAsync(ct)))
    .WithName("GetCurrentPositions");

api.MapGet("/orbital-objects/motion", async (CatalogVisualization visualization, HttpContext context, CancellationToken ct) =>
{
    var motion = await visualization.GetMotionAsync(ct);
    var etag = $"\"{motion.Id}\"";
    context.Response.Headers.ETag = etag;
    context.Response.Headers.CacheControl = "private, no-cache";
    if (context.Request.Headers.IfNoneMatch.Any(value => value == etag))
        return Results.StatusCode(StatusCodes.Status304NotModified);
    return Results.Ok(motion);
}).WithName("GetCatalogMotion");

api.MapGet("/orbital-objects/{id:guid}", async (Guid id, CatalogRepository catalog, CancellationToken ct) =>
    await catalog.GetAsync(id, ct) is { } orbit ? Results.Ok(orbit) : Results.Problem(statusCode: 404, title: "Orbital object not found."))
    .WithName("GetOrbitalObject");

api.MapGet("/orbital-objects/{id:guid}/details", async (Guid id, CatalogRepository catalog,
    CatalogMetadataRepository metadata, HttpContext context, CancellationToken ct) =>
{
    var orbit = await catalog.GetAsync(id, ct);
    if (orbit is null) return Results.Problem(statusCode: 404, title: "Orbital object not found.");
    var record = orbit.Object.NoradCatalogId is { } norad ? await metadata.GetAsync(norad, ct) : null;
    context.Response.Headers.CacheControl = "private, max-age=60";
    return Results.Ok(new
    {
        orbitalObjectId = id, orbitVersion = orbit.Version,
        meanOrbit = OrbitDisplayGeometry.MeanShape(orbit.Elements), metadata = record,
        elementsSource = "CelesTrak GP (OMM)"
    });
}).WithName("GetObjectDetails");

api.MapGet("/orbital-objects/{id:guid}/trajectory", async (Guid id, CatalogRepository catalog, TrajectoryRepository trajectories,
    CatalogVisualization visualization, IOptions<PredictionOptions> prediction, HttpContext context, CancellationToken ct) =>
{
    var orbit = await catalog.GetAsync(id, ct);
    if (orbit is null) return Results.Problem(statusCode: 404, title: "Orbital object not found.");
    if (!prediction.Value.PrecomputeTrajectories)
    {
        try
        {
            var path = await visualization.GetTrajectoryAsync(orbit, ct);
            context.Response.Headers.CacheControl = "private, max-age=30";
            return Results.Ok(path);
        }
        catch (OrbitPropagationException)
        {
            return Results.Problem(statusCode: 422, title: "These orbital elements cannot produce a path for the requested prediction window.");
        }
    }
    var trajectory = await trajectories.GetAsync(id, ct);
    if (trajectory is null || trajectory.OrbitVersion != orbit.Version || trajectory.EndUtc <= DateTimeOffset.UtcNow)
    {
        context.Response.Headers.RetryAfter = "10";
        return Results.Json(new { status = "pending" }, statusCode: 202);
    }
    context.Response.Headers.CacheControl = "private, max-age=30";
    return Results.Ok(trajectory);
}).WithName("GetTrajectory");

api.MapConjunctionEndpoints();

await app.RunAsync();
public partial class Program;
