using System;
using System.Collections.Generic;
using System.Linq;

namespace PluginFailureCorrelationExplorer.Models
{
    /// <summary>How recurring failures are grouped.</summary>
    public enum GroupingMode
    {
        /// <summary>Default: exception type + error code + normalized message + plug-in type + top user stack frames.</summary>
        Fingerprint = 0,

        /// <summary>Exception type + normalized message only, across plug-in types. Can merge unrelated failures.</summary>
        ExceptionAndMessage = 1,

        /// <summary>Fingerprint split further by message name and table.</summary>
        FingerprintAndContext = 2,
    }

    /// <summary>A user-defined normalization rule applied before the built-in ones.</summary>
    public sealed class NormalizationRule
    {
        public string Name { get; set; }
        public string Pattern { get; set; }

        /// <summary>Replacement token, for example &lt;order-number&gt;.</summary>
        public string Token { get; set; } = "<custom>";
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Analysis settings. Public, parameterless and list-based so XrmToolBox's SettingsManager (XmlSerializer) can store
    /// them per connection.
    /// </summary>
    public sealed class AnalysisOptions
    {
        public int SlowSyncThresholdMs { get; set; } = 2000;
        public int SlowAsyncThresholdMs { get; set; } = 10000;

        /// <summary>Synchronous plug-ins are cancelled at 2 minutes; executions above this share of it are called out.</summary>
        public int NearTimeoutMs { get; set; } = 100000;

        /// <summary>Executions needed before average and median are reported for a group.</summary>
        public int MinSampleForStatistics { get; set; } = 5;

        /// <summary>Executions needed before the 95th percentile is reported (nearest-rank needs a reasonable sample).</summary>
        public int MinSampleForPercentile { get; set; } = 20;

        /// <summary>Occurrences of one fingerprint before it counts as recurring.</summary>
        public int RecurringMinOccurrences { get; set; } = 3;

        /// <summary>A failure group above this many occurrences per hour (over its active span) is "high frequency".</summary>
        public double HighFrequencyPerHour { get; set; } = 30;

        /// <summary>Window for heuristic (non-identifier) correlation, in seconds.</summary>
        public int HeuristicWindowSeconds { get; set; } = 5;

        /// <summary>Clock tolerance when checking that a child execution falls inside its suspected parent, in milliseconds.</summary>
        public int ParentChildToleranceMs { get; set; } = 1000;

        /// <summary>Current rate must be at least this multiple of the baseline rate to count as "increasing".</summary>
        public double IncreaseRatio { get; set; } = 2.0;

        /// <summary>Current median must be at least this multiple of the baseline median to count as a regression.</summary>
        public double RegressionRatio { get; set; } = 1.5;

        /// <summary>User frames from the stack trace included in the fingerprint.</summary>
        public int FingerprintStackFrames { get; set; } = 3;

        public GroupingMode Grouping { get; set; } = GroupingMode.Fingerprint;

        public List<NormalizationRule> CustomRules { get; set; } = new List<NormalizationRule>();

        /// <summary>Extra literal values always removed from exported reports (customer names, project codes…).</summary>
        public List<string> SensitiveValues { get; set; } = new List<string>();

        /// <summary>Last result limit used, restored on next open.</summary>
        public int LastResultLimit { get; set; } = TraceQuery.DefaultResultLimit;

        /// <summary>Last date range in hours (0 = custom).</summary>
        public int LastRangeHours { get; set; } = 24;

        /// <summary>When true, each analysis saves an aggregate-only snapshot locally for later trend comparison.</summary>
        public bool SaveBaselineSnapshots { get; set; }

        /// <summary>Clamps every value into a sensible range (protects against hand-edited settings files).</summary>
        public void Normalize()
        {
            SlowSyncThresholdMs = Clamp(SlowSyncThresholdMs, 1, 600000, 2000);
            SlowAsyncThresholdMs = Clamp(SlowAsyncThresholdMs, 1, 3600000, 10000);
            NearTimeoutMs = Clamp(NearTimeoutMs, 1000, 120000, 100000);
            MinSampleForStatistics = Clamp(MinSampleForStatistics, 1, 1000, 5);
            MinSampleForPercentile = Clamp(MinSampleForPercentile, 2, 10000, 20);
            RecurringMinOccurrences = Clamp(RecurringMinOccurrences, 2, 100000, 3);
            HeuristicWindowSeconds = Clamp(HeuristicWindowSeconds, 1, 600, 5);
            ParentChildToleranceMs = Clamp(ParentChildToleranceMs, 0, 60000, 1000);
            FingerprintStackFrames = Clamp(FingerprintStackFrames, 0, 10, 3);
            LastResultLimit = Clamp(LastResultLimit, 1, TraceQuery.MaxResultLimit, TraceQuery.DefaultResultLimit);
            if (double.IsNaN(HighFrequencyPerHour) || HighFrequencyPerHour <= 0) HighFrequencyPerHour = 30;
            if (double.IsNaN(IncreaseRatio) || IncreaseRatio <= 1) IncreaseRatio = 2.0;
            if (double.IsNaN(RegressionRatio) || RegressionRatio <= 1) RegressionRatio = 1.5;
            if (!Enum.IsDefined(typeof(GroupingMode), Grouping)) Grouping = GroupingMode.Fingerprint;
            CustomRules = (CustomRules ?? new List<NormalizationRule>()).Where(r => r != null).ToList();
            SensitiveValues = (SensitiveValues ?? new List<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().ToList();
        }

        /// <summary>Slow threshold for the execution mode of a trace.</summary>
        public int ThresholdFor(int? mode) => mode == 1 ? SlowAsyncThresholdMs : SlowSyncThresholdMs;

        public AnalysisOptions Clone()
        {
            var copy = (AnalysisOptions)MemberwiseClone();
            copy.CustomRules = (CustomRules ?? new List<NormalizationRule>())
                .Select(r => new NormalizationRule { Name = r.Name, Pattern = r.Pattern, Token = r.Token, Enabled = r.Enabled }).ToList();
            copy.SensitiveValues = new List<string>(SensitiveValues ?? new List<string>());
            return copy;
        }

        private static int Clamp(int value, int min, int max, int fallback) => value < min || value > max ? fallback : value;
    }
}
