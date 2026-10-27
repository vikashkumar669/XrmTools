using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PluginFailureCorrelationExplorer.Models
{
    /// <summary>
    /// Aggregate-only summary of one analysis, kept for trend comparison. It holds counts, fingerprint keys, exception
    /// types, plug-in class names and duration statistics. It never holds exception text, trace text, record IDs,
    /// user names or identifiers from the traces.
    /// </summary>
    [DataContract(Name = "baseline", Namespace = "")]
    public sealed class BaselineSnapshot
    {
        public const int CurrentSchemaVersion = 1;

        [DataMember(Name = "schemaVersion", Order = 0)] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        [DataMember(Name = "capturedUtc", Order = 1)] public DateTime CapturedUtc { get; set; }
        [DataMember(Name = "fromUtc", Order = 2)] public DateTime FromUtc { get; set; }
        [DataMember(Name = "toUtc", Order = 3)] public DateTime ToUtc { get; set; }

        /// <summary>Description of the filters, so a comparison is only made like-for-like.</summary>
        [DataMember(Name = "filters", Order = 4)] public string Filters { get; set; }

        /// <summary>Hash of the grouping settings: fingerprints are only comparable when these match.</summary>
        [DataMember(Name = "groupingSignature", Order = 5)] public string GroupingSignature { get; set; }

        [DataMember(Name = "totalTraces", Order = 6)] public int TotalTraces { get; set; }
        [DataMember(Name = "tracesWithException", Order = 7)] public int TracesWithException { get; set; }

        /// <summary>True when the result limit cut the retrieval short; counts are then lower bounds.</summary>
        [DataMember(Name = "limitReached", Order = 8)] public bool LimitReached { get; set; }

        /// <summary>Where the baseline came from: "previous period" (queried live) or "saved snapshot".</summary>
        [DataMember(Name = "source", Order = 9)] public string Source { get; set; }

        [DataMember(Name = "failures", Order = 10)] public List<BaselineFailure> Failures { get; set; } = new List<BaselineFailure>();
        [DataMember(Name = "performance", Order = 11)] public List<BaselinePerformance> Performance { get; set; } = new List<BaselinePerformance>();

        public double Hours => Math.Max(0, (ToUtc - FromUtc).TotalHours);
    }

    [DataContract(Name = "failure", Namespace = "")]
    public sealed class BaselineFailure
    {
        [DataMember(Name = "key", Order = 0)] public string Key { get; set; }
        [DataMember(Name = "exceptionType", Order = 1)] public string ExceptionType { get; set; }
        [DataMember(Name = "pluginType", Order = 2)] public string PluginType { get; set; }
        [DataMember(Name = "count", Order = 3)] public int Count { get; set; }
    }

    [DataContract(Name = "performance", Namespace = "")]
    public sealed class BaselinePerformance
    {
        [DataMember(Name = "key", Order = 0)] public string Key { get; set; }
        [DataMember(Name = "measured", Order = 1)] public int Measured { get; set; }
        [DataMember(Name = "medianMs", Order = 2)] public double? MedianMs { get; set; }
        [DataMember(Name = "p95Ms", Order = 3)] public double? P95Ms { get; set; }
    }

    /// <summary>Change of one failure group against the baseline.</summary>
    public enum TrendStatus
    {
        New = 0,
        Increased = 1,
        Stable = 2,
        Decreased = 3,
        NotSeen = 4,
    }

    public sealed class FailureTrend
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public int CurrentCount { get; set; }
        public int BaselineCount { get; set; }
        public double CurrentPerHour { get; set; }
        public double BaselinePerHour { get; set; }
        public TrendStatus Status { get; set; }
    }

    public sealed class PerformanceTrend
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public double CurrentMedianMs { get; set; }
        public double BaselineMedianMs { get; set; }
        public int CurrentSample { get; set; }
        public int BaselineSample { get; set; }
        public double Ratio => BaselineMedianMs <= 0 ? 0 : CurrentMedianMs / BaselineMedianMs;
        public bool IsRegression { get; set; }
    }

    /// <summary>Result of comparing the current analysis with a baseline. When unavailable, the reason says why.</summary>
    public sealed class TrendComparison
    {
        public bool Available { get; set; }
        public string UnavailableReason { get; set; }
        public string BaselineDescription { get; set; }

        /// <summary>Caveats that don't block the comparison but limit what it can claim.</summary>
        public List<string> Caveats { get; } = new List<string>();

        public List<FailureTrend> Failures { get; } = new List<FailureTrend>();
        public List<PerformanceTrend> Performance { get; } = new List<PerformanceTrend>();

        public static TrendComparison Unavailable(string reason) => new TrendComparison { Available = false, UnavailableReason = reason };
    }
}
