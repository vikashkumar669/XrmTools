using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Analysis
{
    /// <summary>
    /// Groups executions by plug-in type, message and mode and measures them against the configured thresholds.
    /// An execution is only called slow when it has a recorded duration, a known mode, and meets the threshold for that
    /// mode. Statistics are only reported when the sample is large enough.
    /// </summary>
    public sealed class PerformanceAnalysisService
    {
        private readonly AnalysisOptions _options;

        public PerformanceAnalysisService(AnalysisOptions options)
        {
            _options = options ?? new AnalysisOptions();
        }

        public static string KeyFor(PluginTraceRecord r) =>
            ((r.FullTypeName ?? "").ToLowerInvariant()) + "|" + ((r.MessageName ?? "").ToLowerInvariant()) + "|" +
            (r.Mode.HasValue ? r.Mode.Value.ToString(CultureInfo.InvariantCulture) : "?");

        /// <summary>True when the trace is slow for its mode. Unknown mode or missing duration is never slow.</summary>
        public bool IsSlow(PluginTraceRecord r) =>
            r != null && r.ExecutionDurationMs.HasValue && r.Mode.HasValue && r.ExecutionDurationMs.Value >= _options.ThresholdFor(r.Mode);

        /// <summary>True for synchronous executions at or above the near-timeout limit.</summary>
        public bool IsNearTimeout(PluginTraceRecord r) =>
            r != null && r.Mode == 0 && r.ExecutionDurationMs.HasValue && r.ExecutionDurationMs.Value >= _options.NearTimeoutMs;

        public List<PerformanceGroup> Analyze(IEnumerable<PluginTraceRecord> records)
        {
            var groups = new List<PerformanceGroup>();
            foreach (var bucket in (records ?? Enumerable.Empty<PluginTraceRecord>()).Where(r => r != null).GroupBy(KeyFor))
            {
                var traces = bucket.OrderByDescending(r => r.CreatedOnUtc).ToList();
                var first = traces[0];
                var durations = traces.Where(r => r.ExecutionDurationMs.HasValue).Select(r => r.ExecutionDurationMs.Value).ToList();
                var constructors = traces.Where(r => r.ConstructorDurationMs.HasValue).Select(r => r.ConstructorDurationMs.Value).ToList();

                var group = new PerformanceGroup
                {
                    Key = bucket.Key,
                    PluginType = first.FullTypeName,
                    MessageName = first.MessageName,
                    PrimaryEntity = string.Join(", ", traces.Select(t => t.PrimaryEntity).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => e)),
                    Mode = first.Mode,
                    Executions = traces.Count,
                    Measured = durations.Count,
                    MaxMs = durations.Count == 0 ? (int?)null : durations.Max(),
                    ThresholdMs = _options.ThresholdFor(first.Mode),
                };
                group.Traces.AddRange(traces);

                if (durations.Count >= _options.MinSampleForStatistics)
                {
                    group.AverageMs = Statistics.Average(durations);
                    group.MedianMs = Statistics.Median(durations);
                }
                if (durations.Count >= _options.MinSampleForPercentile)
                    group.P95Ms = Statistics.PercentileNearestRank(durations, 95);
                if (constructors.Count >= _options.MinSampleForStatistics)
                    group.AverageConstructorMs = Statistics.Average(constructors);

                group.SampleNote = SampleNote(group, first.Mode);

                foreach (var t in traces)
                {
                    if (IsSlow(t)) { group.SlowCount++; group.SlowTraces.Add(t); }
                    if (IsNearTimeout(t)) group.NearTimeoutCount++;
                }
                group.SlowTraces.Sort((a, b) => (b.ExecutionDurationMs ?? 0).CompareTo(a.ExecutionDurationMs ?? 0));
                groups.Add(group);
            }

            return groups
                .OrderByDescending(g => g.NearTimeoutCount)
                .ThenByDescending(g => g.SlowCount)
                .ThenByDescending(g => g.MaxMs ?? 0)
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();
        }

        private string SampleNote(PerformanceGroup g, int? mode)
        {
            var notes = new List<string>();
            if (!mode.HasValue) notes.Add("execution mode unknown, so no execution is classified as slow");
            if (g.Measured == 0) notes.Add("no durations recorded");
            else if (g.Measured < _options.MinSampleForStatistics)
                notes.Add($"sample of {g.Measured} is below {_options.MinSampleForStatistics}: average and median not reported");
            else if (g.Measured < _options.MinSampleForPercentile)
                notes.Add($"sample of {g.Measured} is below {_options.MinSampleForPercentile}: 95th percentile not reported");
            return string.Join("; ", notes);
        }
    }
}
