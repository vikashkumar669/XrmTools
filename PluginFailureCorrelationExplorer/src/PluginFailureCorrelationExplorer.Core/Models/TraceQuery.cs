using System;
using System.Collections.Generic;
using System.Globalization;

namespace PluginFailureCorrelationExplorer.Models
{
    /// <summary>Which traces to retrieve, by exception presence.</summary>
    public enum ExceptionFilter
    {
        All = 0,
        WithException = 1,
        WithoutException = 2,
    }

    /// <summary>Server-side filters for one retrieval. Every filter is optional except the date range and the limit.</summary>
    public sealed class TraceQuery
    {
        public const int DefaultResultLimit = 5000;
        public const int MaxResultLimit = 100000;

        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public ExceptionFilter Exceptions { get; set; }

        /// <summary>Exact message name (messagename eq).</summary>
        public string MessageName { get; set; }

        /// <summary>Exact table logical name (primaryentity eq).</summary>
        public string PrimaryEntity { get; set; }

        /// <summary>Partial plug-in type name (typename like %value%).</summary>
        public string PluginType { get; set; }

        public Guid? CorrelationId { get; set; }
        public Guid? RequestId { get; set; }

        /// <summary>Only traces with performanceexecutionduration ≥ this value (ms).</summary>
        public int? MinDurationMs { get; set; }

        /// <summary>Maximum rows to retrieve. Never unlimited.</summary>
        public int ResultLimit { get; set; } = DefaultResultLimit;

        /// <summary>Set by the retrieval when the limit stopped it while more rows matched.</summary>
        public bool LimitReached { get; set; }

        /// <summary>Validates the query; returns the problems found (empty when valid).</summary>
        public List<string> Validate()
        {
            var problems = new List<string>();
            if (ToUtc <= FromUtc) problems.Add("The end of the date range must be after the start.");
            if (ResultLimit < 1 || ResultLimit > MaxResultLimit)
                problems.Add($"The result limit must be between 1 and {MaxResultLimit.ToString("N0", CultureInfo.InvariantCulture)}.");
            if (MinDurationMs.HasValue && MinDurationMs.Value < 0) problems.Add("The minimum duration can't be negative.");
            return problems;
        }

        /// <summary>Human-readable summary of the active filters (no secrets: only what the user typed).</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (Exceptions == ExceptionFilter.WithException) parts.Add("with exception");
            if (Exceptions == ExceptionFilter.WithoutException) parts.Add("without exception");
            if (!string.IsNullOrWhiteSpace(MessageName)) parts.Add("message = " + MessageName.Trim());
            if (!string.IsNullOrWhiteSpace(PrimaryEntity)) parts.Add("table = " + PrimaryEntity.Trim());
            if (!string.IsNullOrWhiteSpace(PluginType)) parts.Add("plug-in type contains \"" + PluginType.Trim() + "\"");
            if (CorrelationId.HasValue) parts.Add("correlation ID = " + CorrelationId.Value);
            if (RequestId.HasValue) parts.Add("request ID = " + RequestId.Value);
            if (MinDurationMs.HasValue) parts.Add("duration ≥ " + MinDurationMs.Value.ToString(CultureInfo.InvariantCulture) + " ms");
            return parts.Count == 0 ? "none" : string.Join("; ", parts);
        }

        public TraceQuery Clone() => (TraceQuery)MemberwiseClone();
    }
}
