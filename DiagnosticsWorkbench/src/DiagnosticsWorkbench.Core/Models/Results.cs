using System;
using System.Collections.Generic;
using System.Linq;

namespace DiagnosticsWorkbench.Models
{
    /// <summary>Recurring errors that share one normalized fingerprint.</summary>
    public sealed class ErrorGroup
    {
        public string Fingerprint { get; set; }
        public string TypeName { get; set; }
        public string MessageName { get; set; }
        public string PrimaryEntity { get; set; }

        /// <summary>The normalized text the fingerprint was computed from.</summary>
        public string NormalizedText { get; set; }

        /// <summary>Exception type and first message line of the most recent occurrence (not normalized).</summary>
        public string Headline { get; set; }

        /// <summary>Full exception text of the most recent occurrence.</summary>
        public string SampleException { get; set; }

        public List<TraceLog> Logs { get; set; } = new List<TraceLog>();
        public int Count => Logs.Count;
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }

        /// <summary>Distinct correlation IDs (separate executions) the error appeared in.</summary>
        public int DistinctExecutions { get; set; }

        /// <summary>Hits per UTC day, oldest first.</summary>
        public SortedDictionary<DateTime, int> HitsPerDay { get; set; } = new SortedDictionary<DateTime, int>();

        public List<ConfigFlag> Flags { get; set; } = new List<ConfigFlag>();
    }

    /// <summary>Duration statistics for one plugin type + message.</summary>
    public sealed class PerformanceStat
    {
        public string TypeName { get; set; }
        public string MessageName { get; set; }
        public string PrimaryEntity { get; set; }
        public string ModeText { get; set; }
        public string StageText { get; set; }
        public int Count { get; set; }
        public int P50Ms { get; set; }
        public int P95Ms { get; set; }
        public int MaxMs { get; set; }
        public double AverageMs { get; set; }
        public int SlowCount { get; set; }
        public int NearTimeoutCount { get; set; }
        public int ErrorCount { get; set; }

        /// <summary>Threshold that applies to most runs in this group (ms).</summary>
        public int ThresholdMs { get; set; }

        /// <summary>Runs sorted slowest first.</summary>
        public List<TraceLog> Logs { get; set; } = new List<TraceLog>();
        public List<ConfigFlag> Flags { get; set; } = new List<ConfigFlag>();

        public bool HasSlowRuns => SlowCount > 0;
        public bool HasNearTimeout => NearTimeoutCount > 0;
    }

    /// <summary>All logs that share one correlation ID.</summary>
    public sealed class CorrelationChain
    {
        public Guid CorrelationId { get; set; }

        /// <summary>Ordered by time, then depth.</summary>
        public List<TraceLog> Entries { get; set; } = new List<TraceLog>();

        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        /// <summary>From the first entry's start to the last entry's end (ms).</summary>
        public long SpanMs { get; set; }

        public int MaxDepth { get; set; }
        public bool Failed { get; set; }
        public int ErrorCount { get; set; }
        public int RequestCount { get; set; }
        public List<ConfigFlag> Flags { get; set; } = new List<ConfigFlag>();

        /// <summary>The entry that triggered the chain (lowest depth, earliest).</summary>
        public TraceLog Root => Entries.OrderBy(e => e.Depth).ThenBy(e => e.CreatedOn).FirstOrDefault();
    }

    public enum ConfigFlagKind
    {
        UpdateWithoutFilteringAttributes,
        ImageWithAllColumns,
        PossibleRecursion,
        StepDisabled
    }

    /// <summary>A configuration smell found in step metadata or in a chain.</summary>
    public sealed class ConfigFlag
    {
        public ConfigFlagKind Kind { get; set; }
        public Guid StepId { get; set; }
        public string StepName { get; set; }
        public string Message { get; set; }

        public string ShortText
        {
            get
            {
                switch (Kind)
                {
                    case ConfigFlagKind.UpdateWithoutFilteringAttributes: return "No filtering attributes";
                    case ConfigFlagKind.ImageWithAllColumns: return "Image retrieves all columns";
                    case ConfigFlagKind.PossibleRecursion: return "Possible recursion";
                    case ConfigFlagKind.StepDisabled: return "Step now disabled";
                    default: return Kind.ToString();
                }
            }
        }

        public override bool Equals(object obj) =>
            obj is ConfigFlag other && other.Kind == Kind && other.StepId == StepId && other.Message == Message;

        public override int GetHashCode() => ((int)Kind * 397) ^ StepId.GetHashCode() ^ (Message ?? "").GetHashCode();
    }

    /// <summary>The trace setting of the organization (organization.plugintracelogsetting).</summary>
    public enum TraceSetting
    {
        Unknown = -1,
        Off = 0,
        Exception = 1,
        All = 2
    }
}
