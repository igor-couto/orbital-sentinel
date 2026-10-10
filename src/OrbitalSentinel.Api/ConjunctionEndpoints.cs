using System.Globalization;
using Microsoft.Extensions.Options;
using OrbitalSentinel.Infrastructure;
using OrbitalSentinel.Runtime;

namespace OrbitalSentinel.Api;

public static class ConjunctionEndpoints
{
    public static void MapConjunctionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/conjunctions", (HttpRequest request, ConjunctionRepository repository, CancellationToken ct) =>
            List(request, repository, null, null, ct));
        api.MapGet("/orbital-objects/{id:guid}/conjunctions", async (Guid id, HttpRequest request,
            CatalogRepository catalog, ConjunctionRepository repository, CancellationToken ct) =>
                await catalog.GetAsync(id, ct) is null ? Missing("Orbital object") : await List(request, repository, id, null, ct));
        api.MapGet("/prediction-runs/{id:guid}/conjunctions", async (Guid id, HttpRequest request,
            ConjunctionRepository repository, CancellationToken ct) =>
                await repository.GetRunAsync(id, ct) is null ? Missing("Prediction run") : await List(request, repository, null, id, ct));
        api.MapGet("/conjunctions/{id:guid}", async (Guid id, ConjunctionRepository repository, CancellationToken ct) =>
        {
            var item = await repository.GetAsync(id, ct);
            if (item is null) return Missing("Conjunction");
            var history = await repository.ListAssessmentsAsync(id, 1, 50, ct);
            return Results.Ok(new { item.Id, item.Source, item.ScenarioId, item.PrimaryObjectId, item.SecondaryObjectId,
                item.PrimaryName, item.SecondaryName, item.PrimaryNoradId, item.SecondaryNoradId,
                item.FirstIdentifiedAtUtc, item.LatestAssessment, assessments = history.Items, assessmentCount = history.TotalCount });
        });
        api.MapGet("/conjunctions/{id:guid}/assessments", async (Guid id, HttpRequest request,
            ConjunctionRepository repository, CancellationToken ct) =>
        {
            if (!TryPage(request, out var page, out var size)) return Invalid("Use page 1–10000 and pageSize 1–100.");
            if (await repository.GetAsync(id, ct) is null) return Missing("Conjunction");
            return Results.Ok(await repository.ListAssessmentsAsync(id, page, size, ct));
        });
        api.MapGet("/prediction-runs/latest", async (HttpRequest request, ConjunctionRepository repository, CancellationToken ct) =>
        {
            var source = request.Query["source"].FirstOrDefault() ?? "Real";
            if (!ValidSource(source)) return Invalid("Source must be Real or Simulation.");
            var runs = await repository.ListRunsAsync(source, 1, 1, ct);
            return runs.Items.FirstOrDefault() is { } run ? Results.Ok(RunView(run)) : Missing("Prediction run");
        });
        api.MapGet("/prediction-runs/{id:guid}", async (Guid id, ConjunctionRepository repository, CancellationToken ct) =>
            await repository.GetRunAsync(id, ct) is { } run ? Results.Ok(RunView(run)) : Missing("Prediction run"));
        api.MapGet("/conjunction-assessments/{id:guid}/visualization", async (Guid id,
            ConjunctionApplication application, CancellationToken ct) =>
        {
            try
            {
                return await application.GetVisualizationAsync(id, ct) is { } view ? Results.Ok(view) : Missing("Assessment");
            }
            catch (NotSupportedException ex) { return Results.Problem(statusCode: 409, title: ex.Message); }
        });
        api.MapPost("/simulations", async (HttpRequest request, ConjunctionApplication application,
            IOptions<ConjunctionOptions> options, CancellationToken ct) =>
        {
            if (!options.Value.Enabled) return Results.Problem(statusCode: 503, title: "Conjunction processing is disabled.");
            // Only a bounded, predefined scenario is public. No client-supplied orbit data enters the catalog.
            if (!Guid.TryParse(request.Headers["Idempotency-Key"], out var key) || key == Guid.Empty)
                return Invalid("Supply a UUID Idempotency-Key header so retries reuse the same simulation.");
            var run = await application.CreateSimulationAsync(key, ct);
            return Results.Accepted($"/api/prediction-runs/{run.Id:D}", RunView(run));
        }).RequireRateLimiting("simulation");
    }

    public static object RunView(PredictionRunRecord run) => new
    {
        run.Id, run.Context.Source, run.Context.ScenarioId, run.Status,
        run.Context.CreatedAtUtc, run.Context.PredictionStartUtc, run.Context.PredictionEndUtc,
        clockUtc = run.Context.Source == "Simulation" ? run.Context.PredictionStartUtc : DateTimeOffset.UtcNow,
        objectCount = run.Context.Objects.Count, error = run.LastError, run.Attempts, run.CompletedAtUtc,
        run.Context.AlgorithmVersion, run.Context.PropagatorVersion, run.Context.Settings, run.Metrics
    };

    private static async Task<IResult> List(HttpRequest request, ConjunctionRepository repository,
        Guid? objectOverride, Guid? runOverride, CancellationToken ct)
    {
        if (!TryPage(request, out var page, out var size)) return Invalid("Use page 1–10000 and pageSize 1–100.");
        var source = request.Query["source"].FirstOrDefault() ?? "Real";
        if (!TryGuid(request, "objectId", out var objectId) || !TryGuid(request, "predictionRunId", out var runId))
            return Invalid("Object and prediction-run identifiers must be nonempty UUIDs.");
        if (!TryDate(request, "from", out var from) || !TryDate(request, "to", out var to) || from > to)
            return Invalid("Use valid UTC timestamps with from no later than to.");
        double? maxDistance = null;
        if (request.Query.ContainsKey("maxMissDistanceMeters"))
        {
            if (!double.TryParse(request.Query["maxMissDistanceMeters"], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var distance) || !double.IsFinite(distance) || distance < 0)
                return Invalid("Maximum miss distance must be a finite, nonnegative number of metres.");
            maxDistance = distance;
        }
        var targetRun = runOverride ?? runId;
        if (targetRun is { } run)
        {
            var context = await repository.GetRunAsync(run, ct);
            if (context is null) return Missing("Prediction run");
            if (!request.Query.ContainsKey("source")) source = context.Context.Source;
        }
        if (!ValidSource(source)) return Invalid("Source must be Real or Simulation.");
        var effectiveFrom = from ?? (source == "Real" && targetRun is null && to is null ? DateTimeOffset.UtcNow : (DateTimeOffset?)null);
        var query = new ConjunctionQuery(source, objectOverride ?? objectId, targetRun,
            effectiveFrom, to,
            maxDistance, page, size);
        return Results.Ok(await repository.ListAsync(query, ct));
    }

    private static bool ValidSource(string source) => source is "Real" or "Simulation";
    private static IResult Missing(string name) => Results.Problem(statusCode: 404, title: $"{name} not found.");
    private static IResult Invalid(string title) => Results.Problem(statusCode: 400, title: title);
    private static bool TryPage(HttpRequest request, out int page, out int size)
    {
        page = 1;
        size = 20;
        return (!request.Query.ContainsKey("page") || int.TryParse(request.Query["page"], out page)) &&
            (!request.Query.ContainsKey("pageSize") || int.TryParse(request.Query["pageSize"], out size)) &&
            page is >= 1 and <= 10000 && size is >= 1 and <= 100;
    }
    private static bool TryGuid(HttpRequest request, string key, out Guid? result)
    {
        result = null;
        if (!request.Query.ContainsKey(key)) return true;
        if (!Guid.TryParse(request.Query[key], out var value) || value == Guid.Empty) return false;
        result = value;
        return true;
    }
    private static bool TryDate(HttpRequest request, string key, out DateTimeOffset? result)
    {
        result = null;
        if (!request.Query.ContainsKey(key)) return true;
        if (!DateTimeOffset.TryParse(request.Query[key], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)) return false;
        result = value;
        return true;
    }
}
