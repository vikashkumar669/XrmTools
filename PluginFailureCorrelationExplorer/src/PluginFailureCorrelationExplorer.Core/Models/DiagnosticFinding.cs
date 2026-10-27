using System;
using System.Collections.Generic;

namespace PluginFailureCorrelationExplorer.Models
{
    /// <summary>How much the evidence supports a finding.</summary>
    public enum Confidence
    {
        /// <summary>Directly observed in the traces or calculated from them.</summary>
        High = 0,

        /// <summary>Supported by identifiers or several consistent signals, with some inference.</summary>
        Medium = 1,

        /// <summary>A heuristic lead or hypothesis that needs confirmation.</summary>
        Low = 2,
    }

    /// <summary>
    /// One diagnostic finding. Evidence is split by kind so reports can keep observed facts, calculated metrics,
    /// heuristic correlations and possible explanations clearly apart. A finding never asserts a root cause:
    /// explanations are listed as possibilities.
    /// </summary>
    public sealed class DiagnosticFinding
    {
        /// <summary>Sequential ID within one analysis, e.g. F-003.</summary>
        public string Id { get; set; }

        /// <summary>Rule that produced the finding, e.g. PFC001.</summary>
        public string RuleId { get; set; }

        public Severity Severity { get; set; }
        public Confidence Confidence { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }

        /// <summary>Observed facts: counts, timestamps and values read directly from the traces.</summary>
        public List<string> Facts { get; } = new List<string>();

        /// <summary>Calculated metrics: rates, medians, percentiles, ratios.</summary>
        public List<string> Metrics { get; } = new List<string>();

        /// <summary>Correlation evidence, each item labelled with its evidence level.</summary>
        public List<string> Correlations { get; } = new List<string>();

        /// <summary>Possible explanations (hypotheses). Never presented as the confirmed root cause.</summary>
        public List<string> PossibleExplanations { get; } = new List<string>();

        public List<string> Recommendations { get; } = new List<string>();

        /// <summary>plugintracelogid values of the supporting traces (capped by the rule).</summary>
        public List<Guid> TraceIds { get; } = new List<Guid>();

        /// <summary>Supporting traces beyond the cap that were not listed.</summary>
        public int AdditionalTraceCount { get; set; }

        /// <summary>Key of the related failure group or performance group, used for navigation.</summary>
        public string RelatedKey { get; set; }
    }
}
