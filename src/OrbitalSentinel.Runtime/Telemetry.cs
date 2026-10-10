using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OrbitalSentinel.Runtime;

public static class Telemetry
{
    public const string Name = "OrbitalSentinel";
    public static readonly ActivitySource Activities = new(Name);
    public static readonly Meter Meter = new(Name);
    public static readonly Counter<long> IngestionChanges = Meter.CreateCounter<long>("orbital.ingestion.changes");
    public static readonly Counter<long> IngestionFailures = Meter.CreateCounter<long>("orbital.ingestion.failures");
    public static readonly Counter<long> Propagations = Meter.CreateCounter<long>("orbital.propagations.completed");
    public static readonly Histogram<double> PropagationDuration = Meter.CreateHistogram<double>("orbital.propagation.duration", "ms");
    public static readonly Counter<long> PublicationFailures = Meter.CreateCounter<long>("orbital.outbox.failures");
    public static readonly Counter<long> QuarantinedMessages = Meter.CreateCounter<long>("orbital.consumer.quarantined");
    public static readonly Histogram<long> ConjunctionObjects = Meter.CreateHistogram<long>("orbital.conjunction.objects");
    public static readonly Histogram<long> ConjunctionPairs = Meter.CreateHistogram<long>("orbital.conjunction.pairs");
    public static readonly Histogram<long> ConjunctionIntervals = Meter.CreateHistogram<long>("orbital.conjunction.intervals");
    public static readonly Histogram<long> ConjunctionCandidates = Meter.CreateHistogram<long>("orbital.conjunction.candidates");
    public static readonly Histogram<long> ConjunctionResults = Meter.CreateHistogram<long>("orbital.conjunction.results");
    public static readonly Histogram<double> ConjunctionDuration = Meter.CreateHistogram<double>("orbital.conjunction.duration", "ms");
    public static readonly Histogram<double> RefinementDuration = Meter.CreateHistogram<double>("orbital.conjunction.refinement.duration", "ms");
    public static readonly Counter<long> ConjunctionFailures = Meter.CreateCounter<long>("orbital.conjunction.failures");
}
