using System.Collections.Generic;

namespace PluginFailureCorrelationExplorer.Models
{
    /// <summary>Execution statistics for one plug-in type + message + mode.</summary>
    public sealed class PerformanceGroup
    {
        public string Key { get; set; }
        public string PluginType { get; set; }
        public string MessageName { get; set; }
        public string PrimaryEntity { get; set; }
        public int? Mode { get; set; }
        public string ModeText => Mode == 1 ? "Async" : Mode == 0 ? "Sync" : "Unknown";

        /// <summary>All executions in the group.</summary>
        public int Executions { get; set; }

        /// <summary>Executions with a recorded duration (the sample the statistics use).</summary>
        public int Measured { get; set; }

        /// <summary>Null when the sample is smaller than <see cref="AnalysisOptions.MinSampleForStatistics"/>.</summary>
        public double? AverageMs { get; set; }
        public double? MedianMs { get; set; }

        /// <summary>Nearest-rank 95th percentile. Null when the sample is smaller than <see cref="AnalysisOptions.MinSampleForPercentile"/>.</summary>
        public double? P95Ms { get; set; }

        public int? MaxMs { get; set; }

        /// <summary>Executions at or above <see cref="ThresholdMs"/>.</summary>
        public int SlowCount { get; set; }

        /// <summary>Synchronous executions at or above the near-timeout limit.</summary>
        public int NearTimeoutCount { get; set; }

        /// <summary>The configured threshold used for this group's mode.</summary>
        public int ThresholdMs { get; set; }

        public double? AverageConstructorMs { get; set; }

        public List<PluginTraceRecord> SlowTraces { get; } = new List<PluginTraceRecord>();
        public List<PluginTraceRecord> Traces { get; } = new List<PluginTraceRecord>();

        /// <summary>Explains why a statistic is missing (sample too small, no durations recorded).</summary>
        public string SampleNote { get; set; }
    }
}
