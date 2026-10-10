namespace OrbitalSentinel.Core;

public sealed record CartesianVector(double X, double Y, double Z);

/// <summary>SGP4 output in TEME. Positions are kilometers; velocities are kilometers per second.</summary>
public sealed record StateVector(
    DateTimeOffset TimestampUtc,
    CartesianVector PositionKm,
    CartesianVector VelocityKmPerSecond);

/// <summary>
/// Position only, in approximate Earth-fixed coordinates and meters for globe rendering.
/// The GMST rotation assumes UT1 = UTC and neglects polar motion; this is not precise ITRF.
/// </summary>
public sealed record VisualizationPoint(DateTimeOffset TimestampUtc, CartesianVector PositionEcefMeters);

/// <summary>A bounded set of visualization samples, including both requested endpoints.</summary>
public sealed record Trajectory(
    Guid OrbitalObjectId,
    long OrbitVersion,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int StepSeconds,
    IReadOnlyList<VisualizationPoint> Points);

public interface IOrbitPropagator
{
    StateVector Propagate(OrbitSnapshot orbit, DateTimeOffset timestampUtc);

    /// <summary>
    /// Creates an evaluator owned by one calculation. Implementations may reuse propagation
    /// initialization; callers must not share an evaluator between concurrent calculations.
    /// </summary>
    IOrbitEvaluator CreateEvaluator(OrbitSnapshot orbit) => new DirectOrbitEvaluator(this, orbit);

    Trajectory Predict(
        OrbitSnapshot orbit,
        DateTimeOffset startUtc,
        TimeSpan horizon,
        TimeSpan step,
        CancellationToken cancellationToken);
}

public interface IOrbitEvaluator
{
    StateVector Evaluate(DateTimeOffset timestampUtc);
}

internal sealed class DirectOrbitEvaluator(IOrbitPropagator propagator, OrbitSnapshot orbit) : IOrbitEvaluator
{
    public StateVector Evaluate(DateTimeOffset timestampUtc) => propagator.Propagate(orbit, timestampUtc);
}

/// <summary>Expected failure to propagate a particular orbit; preserves diagnostic context for workers.</summary>
public sealed class OrbitPropagationException(
    Guid orbitalObjectId,
    DateTimeOffset timestampUtc,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public Guid OrbitalObjectId { get; } = orbitalObjectId;
    public DateTimeOffset TimestampUtc { get; } = timestampUtc;
}
