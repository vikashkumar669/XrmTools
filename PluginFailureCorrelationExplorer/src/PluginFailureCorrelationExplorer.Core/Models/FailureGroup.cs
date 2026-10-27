using System;
using System.Collections.Generic;
using System.Linq;

namespace PluginFailureCorrelationExplorer.Models
{
    /// <summary>Severity used by failure groups and findings.</summary>
    public enum Severity
    {
        Information = 0,
        Warning = 1,
        Error = 2,
    }

    /// <summary>The parsed, normalized shape of one exception. The original text is never modified.</summary>
    public sealed class FailureFingerprint
    {
        /// <summary>16 hex characters of SHA-256 over <see cref="FingerprintInput"/>.</summary>
        public string Value { get; set; }

        /// <summary>Exactly what was hashed, kept so the grouping can be explained and debugged.</summary>
        public string FingerprintInput { get; set; }

        /// <summary>Exception type, e.g. System.NullReferenceException or the FaultException type.</summary>
        public string ExceptionType { get; set; }

        /// <summary>Organization Service error code, when present (e.g. -2147220891).</summary>
        public string ErrorCode { get; set; }

        /// <summary>First message line as written.</summary>
        public string Message { get; set; }

        /// <summary>First message line after normalization.</summary>
        public string NormalizedMessage { get; set; }

        /// <summary>Top user stack frames (method only, no file or line), as written. The fingerprint hashes their normalized form.</summary>
        public List<string> TopFrames { get; set; } = new List<string>();

        /// <summary>
        /// Short label for titles: the short exception type, or for Organization Service faults
        /// "OrganizationServiceFault -2147220891" (the error code says more than the wrapper type).
        /// </summary>
        public string Label
        {
            get
            {
                var shortType = TypeNames.ShortName(ExceptionType);
                if (shortType.StartsWith("FaultException<", StringComparison.Ordinal))
                    return (shortType.Substring("FaultException<".Length).TrimEnd('>')) + (string.IsNullOrEmpty(ErrorCode) ? "" : " " + ErrorCode);
                return shortType;
            }
        }

        /// <summary>Normalization rules that changed the message (for the "why grouped" explanation).</summary>
        public List<string> RulesApplied { get; set; } = new List<string>();
    }

    /// <summary>Recurring failures that share a grouping key.</summary>
    public sealed class FailureGroup
    {
        /// <summary>Grouping key (the fingerprint, or a derived key depending on <see cref="GroupingMode"/>).</summary>
        public string Key { get; set; }

        /// <summary>A representative fingerprint for display.</summary>
        public FailureFingerprint Fingerprint { get; set; }

        public List<PluginTraceRecord> Traces { get; } = new List<PluginTraceRecord>();

        public int Occurrences => Traces.Count;
        public DateTime FirstUtc => Traces.Count == 0 ? DateTime.MinValue : Traces.Min(t => t.CreatedOnUtc);
        public DateTime LastUtc => Traces.Count == 0 ? DateTime.MinValue : Traces.Max(t => t.CreatedOnUtc);

        public IReadOnlyList<string> PluginTypes => Distinct(t => t.FullTypeName);
        public IReadOnlyList<string> PrimaryEntities => Distinct(t => t.PrimaryEntity);
        public IReadOnlyList<string> Messages => Distinct(t => t.MessageName);

        /// <summary>Distinct request IDs (executions that can be told apart by identifier).</summary>
        public int DistinctRequests => Traces.Where(t => t.RequestId.HasValue).Select(t => t.RequestId.Value).Distinct().Count();

        /// <summary>Distinct correlation IDs (causal chains).</summary>
        public int DistinctCorrelations => Traces.Where(t => t.CorrelationId.HasValue).Select(t => t.CorrelationId.Value).Distinct().Count();

        /// <summary>
        /// Error when recurring with at least one synchronous (or unknown-mode) occurrence, Warning when recurring only
        /// asynchronously or seen more than once, Information for a single occurrence.
        /// </summary>
        public Severity Severity { get; set; }

        /// <summary>Occurrences per hour over the span between the first and last occurrence (minimum span 1 hour).</summary>
        public double RatePerHour
        {
            get
            {
                if (Traces.Count == 0) return 0;
                var hours = Math.Max(1.0, (LastUtc - FirstUtc).TotalHours);
                return Occurrences / hours;
            }
        }

        private IReadOnlyList<string> Distinct(Func<PluginTraceRecord, string> selector) =>
            Traces.Select(selector).Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
