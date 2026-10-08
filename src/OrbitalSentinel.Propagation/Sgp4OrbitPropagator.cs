using OrbitalSentinel.Core;
using SGPdotNET.Exception;
using SGPdotNET.Observation;
using SGPdotNET.Parsers;

namespace OrbitalSentinel.Propagation;

/// <summary>
/// SGP.NET 1.6 adapter. Numerical OMM mapping retains input precision (no TLE text roundtrip).
/// The dependency uses WGS84 constants, which differ from canonical WGS72 GP verification data.
/// Each operation owns its Satellite instance: the dependency's deep-space state is mutable.
/// </summary>
public sealed class Sgp4OrbitPropagator : IOrbitPropagator
{
    public const int MaximumPredictionPoints = 10_001;
    public static readonly TimeSpan MaximumPredictionHorizon = TimeSpan.FromHours(48);

    public StateVector Propagate(OrbitSnapshot orbit, DateTimeOffset timestampUtc)
    {
        ValidateOrbit(orbit);
        return Propagate(CreateSatellite(orbit), orbit.Object.Id, timestampUtc.ToUniversalTime());
    }

    public Trajectory Predict(
        OrbitSnapshot orbit,
        DateTimeOffset startUtc,
        TimeSpan horizon,
        TimeSpan step,
        CancellationToken cancellationToken)
    {
        ValidateOrbit(orbit);
        cancellationToken.ThrowIfCancellationRequested();
        if (horizon <= TimeSpan.Zero || horizon > MaximumPredictionHorizon)
            throw new ArgumentOutOfRangeException(nameof(horizon), "Prediction horizon must be positive and at most 48 hours.");
        if (step < TimeSpan.FromSeconds(1) || step > horizon || step.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(step), "Step must be a whole number of seconds between one second and the horizon.");

        var intervals = (horizon.Ticks + step.Ticks - 1) / step.Ticks;
        if (intervals + 1 > MaximumPredictionPoints)
            throw new ArgumentOutOfRangeException(nameof(step), $"Prediction must contain at most {MaximumPredictionPoints} points.");

        startUtc = startUtc.ToUniversalTime();
        var endUtc = startUtc.Add(horizon);
        var satellite = CreateSatellite(orbit);
        var points = new List<VisualizationPoint>((int)intervals + 1);
        for (long index = 0; index <= intervals; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Include the exact end even when the horizon is not divisible by the interval.
            var timestamp = startUtc.AddTicks(Math.Min(index * step.Ticks, horizon.Ticks));
            points.Add(TemeToEarthFixed.Convert(Propagate(satellite, orbit.Object.Id, timestamp)));
        }

        return new Trajectory(orbit.Object.Id, orbit.Version, startUtc, endUtc,
            (int)step.TotalSeconds, points.AsReadOnly());
    }

    private static void ValidateOrbit(OrbitSnapshot orbit)
    {
        ArgumentNullException.ThrowIfNull(orbit);
        ArgumentNullException.ThrowIfNull(orbit.Object);
        ArgumentNullException.ThrowIfNull(orbit.Elements);
        orbit.Elements.Validate();
    }

    private static Satellite CreateSatellite(OrbitSnapshot orbit)
    {
        var elements = orbit.Elements;
        // The library requires a uint metadata field but never uses it in orbital calculations.
        // Always use a local placeholder: an absent or larger external ID cannot block propagation.
        var omm = new OmmData
        {
            ObjectName = orbit.Object.Name,
            ObjectID = orbit.Object.InternationalDesignator ?? string.Empty,
            NoradCatID = 0,
            Epoch = elements.EpochUtc.UtcDateTime,
            MeanMotion = elements.MeanMotionRevolutionsPerDay,
            Eccentricity = elements.Eccentricity,
            Inclination = elements.InclinationDegrees,
            RAOfAscNode = elements.RightAscensionDegrees,
            ArgOfPericenter = elements.ArgumentOfPericenterDegrees,
            MeanAnomaly = elements.MeanAnomalyDegrees,
            BStar = elements.BStar,
            MeanMotionDot = elements.MeanMotionDot,
            MeanMotionDDot = elements.MeanMotionDdot,
            CenterName = "EARTH",
            RefFrame = "TEME",
            TimeSystem = "UTC",
            MeanElementTheory = "SGP4",
            EphemerisType = 0
        };

        try
        {
            return new Satellite(omm);
        }
        catch (Exception exception) when (exception is SatellitePropagationException or DecayedException)
        {
            throw new OrbitPropagationException(orbit.Object.Id, elements.EpochUtc,
                "The orbital elements could not initialize SGP4.", exception);
        }
    }

    private static StateVector Propagate(Satellite satellite, Guid objectId, DateTimeOffset timestampUtc)
    {
        try
        {
            var predicted = satellite.Predict(timestampUtc.UtcDateTime);
            var position = new CartesianVector(predicted.Position.X, predicted.Position.Y, predicted.Position.Z);
            var velocity = new CartesianVector(predicted.Velocity.X, predicted.Velocity.Y, predicted.Velocity.Z);
            if (!IsFinite(position) || !IsFinite(velocity))
                throw new OrbitPropagationException(objectId, timestampUtc, "SGP4 returned a non-finite state vector.");

            return new StateVector(timestampUtc, position, velocity);
        }
        catch (Exception exception) when (exception is SatellitePropagationException or DecayedException)
        {
            throw new OrbitPropagationException(objectId, timestampUtc,
                "SGP4 could not propagate this orbit at the requested timestamp.", exception);
        }
    }

    private static bool IsFinite(CartesianVector vector) =>
        double.IsFinite(vector.X) && double.IsFinite(vector.Y) && double.IsFinite(vector.Z);
}
