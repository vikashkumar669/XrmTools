using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Analysis
{
    /// <summary>
    /// Builds aggregate baselines and compares an analysis with one. A comparison is refused, with a reason, when it
    /// would not be like-for-like or when the baseline can't support the claim (no traces, truncated, overlapping).
    /// </summary>
    public static class BaselineService
    {
        public const string SourcePreviousPeriod = "previous period";
        public const string SourceSnapshot = "saved snapshot";

        /// <summary>Aggregate snapshot of an analysis: counts and statistics only, no text or identifiers from traces.</summary>
        public static BaselineSnapshot CreateSnapshot(AnalysisResult result, string groupingSignature, string source)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            var q = result.Summary.Query;
            var snapshot = new BaselineSnapshot
            {
                CapturedUtc = result.Summary.AnalyzedUtc,
                FromUtc = q.FromUtc,
                ToUtc = q.ToUtc,
                Filters = FilterSignature(q),
                GroupingSignature = groupingSignature,
                TotalTraces = result.Summary.TotalTraces,
                TracesWithException = result.Summary.TracesWithException,
                LimitReached = q.LimitReached,
                Source = source,
            };
            foreach (var g in result.FailureGroups)
            {
                snapshot.Failures.Add(new BaselineFailure
                {
                    Key = g.Key,
                    ExceptionType = g.Fingerprint?.Label,
                    PluginType = g.PluginTypes.Count == 1 ? TypeNames.ShortName(g.PluginTypes[0]) : g.PluginTypes.Count + " plug-in types",
                    Count = g.Occurrences,
                });
            }
            foreach (var p in result.PerformanceGroups.Where(p => p.MedianMs.HasValue))
                snapshot.Performance.Add(new BaselinePerformance { Key = p.Key, Measured = p.Measured, MedianMs = p.MedianMs, P95Ms = p.P95Ms });
            return snapshot;
        }

        /// <summary>
        /// Filters that change which traces are retrieved (everything except the date range and limit). Comparisons are
        /// only made when the current and baseline filters match.
        /// </summary>
        public static string FilterSignature(TraceQuery q)
        {
            if (q == null) return "";
            var copy = q.Clone();
            copy.FromUtc = copy.ToUtc = default;
            return copy.Describe().ToLowerInvariant();
        }

        /// <summary>Compares the current analysis with a baseline.</summary>
        public static TrendComparison Compare(AnalysisResult current, BaselineSnapshot baseline, string groupingSignature, AnalysisOptions options)
        {
            options = options ?? new AnalysisOptions();
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (baseline == null) return TrendComparison.Unavailable("No baseline was selected, so trend analysis is unavailable.");
            if (baseline.SchemaVersion != BaselineSnapshot.CurrentSchemaVersion)
                return TrendComparison.Unavailable("The baseline was saved by a different version of the tool and can't be compared.");
            if (!string.Equals(baseline.GroupingSignature, groupingSignature, StringComparison.Ordinal))
                return TrendComparison.Unavailable("The fingerprint settings (grouping mode, stack frames or custom normalization rules) changed since the baseline was taken, so failure groups are not comparable.");

            var q = current.Summary.Query;
            if (!string.Equals(baseline.Filters ?? "", FilterSignature(q), StringComparison.Ordinal))
                return TrendComparison.Unavailable($"The baseline used different filters ({baseline.Filters}) from the current analysis ({FilterSignature(q)}), so the counts are not comparable.");
            if (baseline.Hours <= 0 || (q.ToUtc - q.FromUtc).TotalHours <= 0)
                return TrendComparison.Unavailable("The baseline or the current period has no duration.");
            if (baseline.FromUtc < q.ToUtc && q.FromUtc < baseline.ToUtc)
                return TrendComparison.Unavailable("The baseline period overlaps the current period. Choose a baseline that ends before the current period starts.");
            if (baseline.TotalTraces == 0)
                return TrendComparison.Unavailable("The baseline period contains no traces. Trace logs may have been deleted by retention, or tracing was off, so the absence of failures can't be told apart from missing data.");
            if (baseline.LimitReached)
                return TrendComparison.Unavailable("The baseline retrieval stopped at the result limit, so its counts are incomplete. Raise the limit or narrow the filters and capture the baseline again.");

            var trend = new TrendComparison
            {
                Available = true,
                BaselineDescription = string.Format(CultureInfo.InvariantCulture, "{0}: {1:yyyy-MM-dd HH:mm} → {2:yyyy-MM-dd HH:mm} UTC, {3:N0} traces, {4:N0} with exception",
                    baseline.Source ?? "baseline", baseline.FromUtc, baseline.ToUtc, baseline.TotalTraces, baseline.TracesWithException),
            };
            if (q.LimitReached)
                trend.Caveats.Add("The current retrieval stopped at the result limit: current counts are lower bounds, so decreases and patterns that are no longer seen are not reported.");
            trend.Caveats.Add("Rates are compared per hour of each period. A change in usage volume between the periods changes failure counts too.");

            var currentHours = (q.ToUtc - q.FromUtc).TotalHours;
            var baseFailures = baseline.Failures.Where(f => f.Key != null).GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.First());

            foreach (var g in current.FailureGroups)
            {
                baseFailures.TryGetValue(g.Key, out var b);
                var item = new FailureTrend
                {
                    Key = g.Key,
                    Label = Label(g.Fingerprint?.Label, g.PluginTypes.Count == 1 ? TypeNames.ShortName(g.PluginTypes[0]) : null),
                    CurrentCount = g.Occurrences,
                    BaselineCount = b?.Count ?? 0,
                    CurrentPerHour = g.Occurrences / currentHours,
                    BaselinePerHour = (b?.Count ?? 0) / baseline.Hours,
                };
                if (item.BaselineCount == 0) item.Status = TrendStatus.New;
                else if (item.CurrentPerHour >= item.BaselinePerHour * options.IncreaseRatio && item.CurrentCount >= options.RecurringMinOccurrences) item.Status = TrendStatus.Increased;
                else if (!q.LimitReached && item.CurrentPerHour * options.IncreaseRatio <= item.BaselinePerHour) item.Status = TrendStatus.Decreased;
                else item.Status = TrendStatus.Stable;
                trend.Failures.Add(item);
            }
            if (!q.LimitReached)
            {
                var currentKeys = new HashSet<string>(current.FailureGroups.Select(g => g.Key));
                foreach (var b in baseFailures.Values.Where(b => !currentKeys.Contains(b.Key)))
                {
                    trend.Failures.Add(new FailureTrend
                    {
                        Key = b.Key, Label = Label(b.ExceptionType, b.PluginType), BaselineCount = b.Count,
                        BaselinePerHour = b.Count / baseline.Hours, Status = TrendStatus.NotSeen,
                    });
                }
            }

            var basePerf = baseline.Performance.Where(p => p.Key != null).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First());
            foreach (var p in current.PerformanceGroups)
            {
                if (!p.MedianMs.HasValue || !basePerf.TryGetValue(p.Key, out var b) || !b.MedianMs.HasValue) continue;
                if (b.Measured < options.MinSampleForStatistics || p.Measured < options.MinSampleForStatistics) continue;
                var t = new PerformanceTrend
                {
                    Key = p.Key,
                    Label = TypeNames.ShortName(p.PluginType) + " · " + p.MessageName + " (" + p.ModeText + ")",
                    CurrentMedianMs = p.MedianMs.Value,
                    BaselineMedianMs = b.MedianMs.Value,
                    CurrentSample = p.Measured,
                    BaselineSample = b.Measured,
                };
                t.IsRegression = t.BaselineMedianMs > 0 && t.CurrentMedianMs >= t.BaselineMedianMs * options.RegressionRatio;
                trend.Performance.Add(t);
            }

            trend.Failures.Sort((a, b) => a.Status != b.Status ? a.Status.CompareTo(b.Status) : b.CurrentCount.CompareTo(a.CurrentCount));
            trend.Performance.Sort((a, b) => b.Ratio.CompareTo(a.Ratio));
            return trend;
        }

        private static string Label(string exceptionType, string pluginType) =>
            (exceptionType ?? "(unknown)") + (string.IsNullOrEmpty(pluginType) ? "" : " in " + pluginType);

        // ------------------------------------------------------------------ serialization

        public static string Serialize(IList<BaselineSnapshot> snapshots)
        {
            var serializer = new DataContractJsonSerializer(typeof(List<BaselineSnapshot>), new DataContractJsonSerializerSettings
            {
                DateTimeFormat = new System.Runtime.Serialization.DateTimeFormat("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            });
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, new List<BaselineSnapshot>(snapshots ?? new List<BaselineSnapshot>()));
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        /// <summary>Reads snapshots; returns an empty list for empty or unreadable content (a corrupt file is never fatal).</summary>
        public static List<BaselineSnapshot> Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<BaselineSnapshot>();
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(List<BaselineSnapshot>), new DataContractJsonSerializerSettings
                {
                    DateTimeFormat = new System.Runtime.Serialization.DateTimeFormat("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                });
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var list = serializer.ReadObject(ms) as List<BaselineSnapshot> ?? new List<BaselineSnapshot>();
                    foreach (var s in list)
                    {
                        s.FromUtc = DateTime.SpecifyKind(s.FromUtc.Kind == DateTimeKind.Local ? s.FromUtc.ToUniversalTime() : s.FromUtc, DateTimeKind.Utc);
                        s.ToUtc = DateTime.SpecifyKind(s.ToUtc.Kind == DateTimeKind.Local ? s.ToUtc.ToUniversalTime() : s.ToUtc, DateTimeKind.Utc);
                        s.CapturedUtc = DateTime.SpecifyKind(s.CapturedUtc.Kind == DateTimeKind.Local ? s.CapturedUtc.ToUniversalTime() : s.CapturedUtc, DateTimeKind.Utc);
                        s.Failures = s.Failures ?? new List<BaselineFailure>();
                        s.Performance = s.Performance ?? new List<BaselinePerformance>();
                    }
                    return list.Where(s => s != null).ToList();
                }
            }
            catch (Exception ex) when (ex is System.Runtime.Serialization.SerializationException || ex is FormatException || ex is ArgumentException)
            {
                return new List<BaselineSnapshot>();
            }
        }
    }
}
