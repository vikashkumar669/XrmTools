using System;
using System.Collections.Generic;
using System.Linq;

namespace PluginFailureCorrelationExplorer.Models
{
    /// <summary>Organization details shown in the dashboard and reports. Never contains credentials.</summary>
    public sealed class OrganizationInfo
    {
        public string ConnectionName { get; set; }
        public string OrganizationName { get; set; }
        public string Url { get; set; }
        public string EnvironmentId { get; set; }
        public string OrganizationVersion { get; set; }

        /// <summary>organization.plugintracelogsetting as text (Off, Exception, All), when it could be read.</summary>
        public string TraceSetting { get; set; }
    }

    /// <summary>Headline numbers for one analysis.</summary>
    public sealed class AnalysisSummary
    {
        public OrganizationInfo Organization { get; set; } = new OrganizationInfo();
        public DateTime AnalyzedUtc { get; set; }
        public TraceQuery Query { get; set; } = new TraceQuery();

        public int TotalTraces { get; set; }
        public int TracesWithException { get; set; }
        public int UniqueFailureGroups { get; set; }
        public int RecurringFailureGroups { get; set; }
        public int SlowExecutions { get; set; }
        public int MeasuredExecutions { get; set; }
        public int CompleteEvidence { get; set; }
        public int IncompleteEvidence { get; set; }

        /// <summary>Column → number of traces missing it.</summary>
        public Dictionary<string, int> MissingIdentifierCounts { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Notes about the retrieval itself (limit reached, fallbacks used).</summary>
        public List<string> RetrievalNotes { get; } = new List<string>();
    }

    /// <summary>Everything one analysis produced.</summary>
    public sealed class AnalysisResult
    {
        public AnalysisSummary Summary { get; set; } = new AnalysisSummary();
        public AnalysisOptions Options { get; set; } = new AnalysisOptions();
        public List<PluginTraceRecord> Records { get; set; } = new List<PluginTraceRecord>();
        public List<FailureGroup> FailureGroups { get; set; } = new List<FailureGroup>();
        public List<PerformanceGroup> PerformanceGroups { get; set; } = new List<PerformanceGroup>();
        public List<CorrelationCluster> Clusters { get; set; } = new List<CorrelationCluster>();
        public List<DiagnosticFinding> Findings { get; set; } = new List<DiagnosticFinding>();
        public TrendComparison Trend { get; set; } = TrendComparison.Unavailable("No baseline was selected.");

        /// <summary>Trace ID → its failure fingerprint (failed traces only).</summary>
        public Dictionary<Guid, FailureFingerprint> Fingerprints { get; } = new Dictionary<Guid, FailureFingerprint>();

        /// <summary>Trace ID → grouping key of its failure group (failed traces only).</summary>
        public Dictionary<Guid, string> GroupKeys { get; } = new Dictionary<Guid, string>();

        /// <summary>Custom normalization rules that were invalid and skipped (name → error).</summary>
        public Dictionary<string, string> InvalidRules { get; } = new Dictionary<string, string>();

        public FailureGroup GroupFor(PluginTraceRecord record) =>
            record != null && GroupKeys.TryGetValue(record.Id, out var key) ? FailureGroups.FirstOrDefault(g => g.Key == key) : null;

        public bool IsEmpty => Records.Count == 0;
    }
}
