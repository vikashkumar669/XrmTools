using System;
using System.Collections.Generic;
using System.Linq;
using DiagnosticsWorkbench.Models;

namespace DiagnosticsWorkbench.Analysis
{
    /// <summary>Slow-run thresholds and per plugin type duration statistics.</summary>
    public sealed class PerformanceAnalyzer
    {
        private readonly WorkbenchSettings _settings;

        public PerformanceAnalyzer(WorkbenchSettings settings)
        {
            _settings = settings ?? new WorkbenchSettings();
        }

        /// <summary>Most specific first: type + message, then type, then mode default.</summary>
        public int ThresholdFor(TraceLog log)
        {
            if (log == null) return _settings.SyncThresholdMs;
            var overrides = _settings.ThresholdOverrides ?? new List<ThresholdOverride>();

            ThresholdOverride typeOnly = null;
            foreach (var o in overrides)
            {
                if (o == null || o.ThresholdMs <= 0 || !TypeMatches(o.TypeName, log)) continue;
                if (!string.IsNullOrWhiteSpace(o.MessageName))
                {
                    if (string.Equals(o.MessageName.Trim(), log.MessageName, StringComparison.OrdinalIgnoreCase)) return o.ThresholdMs;
                }
                else if (typeOnly == null) typeOnly = o;
            }
            if (typeOnly != null) return typeOnly.ThresholdMs;
            return log.IsAsync ? _settings.AsyncThresholdMs : _settings.SyncThresholdMs;
        }

        private static bool TypeMatches(string pattern, TraceLog log)
        {
            if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrEmpty(log.TypeName)) return false;
            pattern = pattern.Trim();
            return string.Equals(TraceLog.StripAssembly(pattern), log.FullTypeName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pattern, log.ShortTypeName, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsSlow(TraceLog log) => log?.ExecutionDurationMs != null && log.ExecutionDurationMs.Value > ThresholdFor(log);

        /// <summary>Synchronous runs near the 2-minute platform timeout.</summary>
        public bool IsNearTimeout(TraceLog log) =>
            log?.ExecutionDurationMs != null && !log.IsAsync && log.ExecutionDurationMs.Value > _settings.NearTimeoutMs;

        public List<PerformanceStat> Analyze(IEnumerable<TraceLog> logs, IDictionary<Guid, StepInfo> steps = null)
        {
            var result = new List<PerformanceStat>();
            var timed = (logs ?? Enumerable.Empty<TraceLog>()).Where(l => l?.ExecutionDurationMs != null);

            foreach (var group in timed.GroupBy(l => ((l.FullTypeName ?? "").ToLowerInvariant(), (l.MessageName ?? "").ToLowerInvariant(), l.Mode)))
            {
                var runs = group.OrderByDescending(l => l.ExecutionDurationMs.Value).ThenByDescending(l => l.CreatedOn).ToList();
                var durations = runs.Select(l => l.ExecutionDurationMs.Value).OrderBy(d => d).ToList();
                var first = runs[0];

                var stat = new PerformanceStat
                {
                    TypeName = first.FullTypeName,
                    MessageName = first.MessageName,
                    PrimaryEntity = Distinct(runs.Select(r => r.PrimaryEntity)),
                    ModeText = Distinct(runs.Select(r => r.ModeText)),
                    Count = runs.Count,
                    P50Ms = Percentile(durations, 50),
                    P95Ms = Percentile(durations, 95),
                    MaxMs = durations[durations.Count - 1],
                    AverageMs = durations.Average(),
                    SlowCount = runs.Count(IsSlow),
                    NearTimeoutCount = runs.Count(IsNearTimeout),
                    ErrorCount = runs.Count(r => r.IsError),
                    ThresholdMs = runs.GroupBy(ThresholdFor).OrderByDescending(g => g.Count()).First().Key,
                    Logs = runs,
                };

                if (steps != null)
                {
                    var stages = runs.Where(r => r.StepId.HasValue && steps.ContainsKey(r.StepId.Value))
                                     .Select(r => steps[r.StepId.Value].StageText).Distinct().ToList();
                    stat.StageText = stages.Count == 0 ? null : stages.Count == 1 ? stages[0] : "Several stages";
                }

                result.Add(stat);
            }

            return result
                .OrderByDescending(s => s.NearTimeoutCount > 0)
                .ThenByDescending(s => s.SlowCount)
                .ThenByDescending(s => s.P95Ms)
                .ToList();
        }

        /// <summary>Nearest-rank percentile of an ascending list.</summary>
        public static int Percentile(IList<int> ascending, int percentile)
        {
            if (ascending == null || ascending.Count == 0) return 0;
            if (percentile <= 0) return ascending[0];
            if (percentile >= 100) return ascending[ascending.Count - 1];
            var rank = (int)Math.Ceiling(percentile / 100.0 * ascending.Count);
            return ascending[Math.Max(0, Math.Min(ascending.Count - 1, rank - 1))];
        }

        private static string Distinct(IEnumerable<string> values)
        {
            var list = values.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            return list.Count == 0 ? "" : list.Count == 1 ? list[0] : "Several";
        }
    }
}
