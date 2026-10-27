using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Reporting
{
    /// <summary>Options shared by the CSV, HTML and JSON writers.</summary>
    public sealed class ReportOptions
    {
        /// <summary>Optional redaction. Credentials are scrubbed even when this is <see cref="RedactionOptions.None"/>.</summary>
        public RedactionOptions Redaction { get; set; } = RedactionOptions.None;

        /// <summary>Tool name and version written into the report header.</summary>
        public string ToolVersion { get; set; } = "Plugin Failure Correlation Explorer";

        /// <summary>Maximum failure groups, performance groups and correlation chains listed in tables.</summary>
        public int MaxRows { get; set; } = 50;

        /// <summary>When false, supporting trace IDs are left out of findings.</summary>
        public bool IncludeTraceIds { get; set; } = true;

        public SensitiveDataRedactor CreateRedactor() => new SensitiveDataRedactor(Redaction ?? RedactionOptions.None);

        public bool IsRedacted => Redaction != null && (Redaction.Guids || Redaction.Emails || Redaction.UrlQueries || (Redaction.CustomValues?.Count ?? 0) > 0);
    }

    /// <summary>Content shared by the report writers.</summary>
    internal static class ReportContent
    {
        public const string Title = "Plugin failure correlation report";

        /// <summary>Limitations stated in every report.</summary>
        public static readonly string[] StandardLimitations =
        {
            "Plug-in trace logs are only written when tracing is enabled, and Dataverse deletes them after a retention period. Missing traces are not evidence that no failures happened.",
            "Correlation ID links traces in one causal chain, which can span several operations and asynchronous continuations. It does not prove that two traces are the same operation.",
            "Parent/child relationships are inferred from depth and timing and are labelled as suspected. Dataverse does not record them.",
            "Heuristic correlations (no shared identifier) are leads to check, not proof of a shared transaction.",
            "Possible explanations are hypotheses that guide the investigation. They are not confirmed root causes.",
            "An execution is only called slow when its recorded duration meets the configured threshold for its execution mode.",
        };

        /// <summary>Distinct recommendations from findings, most severe findings first.</summary>
        public static List<string> AllRecommendations(AnalysisResult result) =>
            result.Findings.OrderByDescending(f => f.Severity).SelectMany(f => f.Recommendations).Distinct().ToList();
    }
}
