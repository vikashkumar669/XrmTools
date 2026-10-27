using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Rules
{
    /// <summary>PFC005: diagnostic evidence is incomplete (missing identifiers, trace setting, result limit).</summary>
    public sealed class IncompleteEvidenceRule : IDiagnosticRule
    {
        public string RuleId => "PFC005";
        public string Name => "Incomplete diagnostic evidence";
        public string Description => "Traces are missing identifiers used for correlation or performance analysis, trace logging is limited, or the result limit cut the retrieval short.";

        public IEnumerable<DiagnosticFinding> Evaluate(AnalysisContext context)
        {
            var s = context.Result.Summary;
            if (s.TotalTraces == 0) yield break;

            if (s.IncompleteEvidence > 0)
            {
                var failed = context.Records.Where(r => r.HasException).ToList();
                var failedWithoutCorrelation = failed.Count(r => !r.CorrelationId.HasValue);
                var severe = failed.Count > 0 && failedWithoutCorrelation * 4 >= failed.Count;
                var f = FindingHelper.New(this, severe ? Severity.Warning : Severity.Information, Confidence.High,
                    "Some traces are missing diagnostic identifiers",
                    $"{Fmt.Count(s.IncompleteEvidence)} of {Fmt.Count(s.TotalTraces)} traces lack at least one identifier used by the analysis. Correlation and performance results for those traces are limited.");
                foreach (var pair in s.MissingIdentifierCounts.OrderByDescending(p => p.Value))
                    f.Facts.Add($"Missing {pair.Key}: {Fmt.Count(pair.Value)} traces ({Fmt.Percent(pair.Value, s.TotalTraces)}).");
                if (failed.Count > 0)
                    f.Metrics.Add($"Failed traces without a correlation ID: {Fmt.Count(failedWithoutCorrelation)} of {Fmt.Count(failed.Count)}. Only heuristic correlation is possible for these.");
                f.PossibleExplanations.Add("Some executions (for example certain system or workflow activity traces) don't record every identifier. Durations are missing when the execution didn't complete normally.");
                f.Recommendations.Add("Treat correlations for these traces as heuristic leads only.");
                f.Recommendations.Add("Write the identifiers you need into the trace output (ITracingService) so they appear in the message block.");
                FindingHelper.AddTraces(f, context.Records.Where(r => !r.HasCompleteEvidence));
                yield return f;
            }

            var setting = s.Organization.TraceSetting;
            if (setting == "Exception" || setting == "Off")
            {
                var f = FindingHelper.New(this, setting == "Off" ? Severity.Warning : Severity.Information, Confidence.High,
                    $"Plug-in trace logging is set to {setting}",
                    setting == "Off"
                        ? "Plug-in trace logging is off, so no new traces are being written. The analysis only covers traces written before it was turned off."
                        : "Only executions that throw an exception are logged. Successful executions are missing, so performance statistics and failure shares cover failing executions only.");
                f.Facts.Add($"organization.plugintracelogsetting = {setting}.");
                f.Recommendations.Add("To analyze performance as well, set plug-in trace logging to All in a non-production environment (System Settings → Customization), reproduce, then set it back.");
                yield return f;
            }

            if (s.Query.LimitReached)
            {
                var f = FindingHelper.New(this, Severity.Warning, Confidence.High,
                    "The retrieval stopped at the result limit",
                    $"More traces matched the filters than the limit of {Fmt.Count(s.Query.ResultLimit)}. Only the newest traces were analyzed, so counts are lower bounds and the oldest part of the date range is missing.");
                f.Facts.Add($"Result limit: {Fmt.Count(s.Query.ResultLimit)}; traces retrieved: {Fmt.Count(s.TotalTraces)}.");
                f.Recommendations.Add("Narrow the date range or filters, or raise the result limit, and run the analysis again.");
                yield return f;
            }
        }
    }
}
