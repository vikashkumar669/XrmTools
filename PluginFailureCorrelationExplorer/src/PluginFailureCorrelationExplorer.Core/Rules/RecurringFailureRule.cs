using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Rules
{
    /// <summary>PFC001: the same failure fingerprint recurs across several executions.</summary>
    public sealed class RecurringFailureRule : IDiagnosticRule
    {
        public string RuleId => "PFC001";
        public string Name => "Recurring failure";
        public string Description => "A failure fingerprint occurs at least the configured number of times (default 3).";

        public IEnumerable<DiagnosticFinding> Evaluate(AnalysisContext context)
        {
            foreach (var g in context.FailureGroups.Where(g => g.Occurrences >= context.Options.RecurringMinOccurrences))
            {
                var fp = g.Fingerprint;
                var sync = g.Traces.Any(t => t.Mode == 0);
                var f = FindingHelper.New(this, sync ? Severity.Error : Severity.Warning, Confidence.High,
                    $"Recurring failure: {fp.Label} in {Fmt.List(g.PluginTypes.Select(TypeNames.ShortName), 2)}",
                    $"The same failure fingerprint ({g.Key}) occurred {Fmt.Plural(g.Occurrences, "time")}. " +
                    (sync ? "At least one occurrence was synchronous, so the user's operation was rolled back and an error was shown."
                          : "All occurrences were asynchronous, so users may not see the error directly."));
                f.RelatedKey = g.Key;

                f.Facts.Add($"Occurrences: {Fmt.Count(g.Occurrences)}, from {Fmt.Utc(g.FirstUtc)} to {Fmt.Utc(g.LastUtc)}.");
                f.Facts.Add($"Exception: {fp.ExceptionType}{(string.IsNullOrEmpty(fp.ErrorCode) ? "" : " (error code " + fp.ErrorCode + ")")}.");
                f.Facts.Add($"Message: {Fmt.OneLine(fp.Message, 300)}");
                f.Facts.Add($"Plug-in types: {Fmt.List(g.PluginTypes)}. Messages: {Fmt.List(g.Messages)}. Tables: {Fmt.List(g.PrimaryEntities)}.");
                f.Facts.Add($"Distinct request IDs: {Fmt.Count(g.DistinctRequests)}; distinct correlation IDs: {Fmt.Count(g.DistinctCorrelations)}.");
                if (fp.TopFrames.Count > 0) f.Facts.Add("Top user frames: " + string.Join(" → ", fp.TopFrames));
                f.Metrics.Add($"Rate over its active span: {Fmt.Rate(g.RatePerHour)}.");

                var chains = g.Traces.Where(t => t.CorrelationId.HasValue).GroupBy(t => t.CorrelationId.Value).Count(c => c.Count() > 1);
                if (chains > 0)
                    f.Correlations.Add($"[{EvidenceText.Label(EvidenceLevel.SameCausalChain)}] {Fmt.Plural(chains, "causal chain")} contain this failure more than once (possible retries inside one chain).");

                f.PossibleExplanations.AddRange(KnownFailureHints.For(fp));

                f.Recommendations.Add("Open the failure group and compare the original exception text and trace output of the first and latest occurrences.");
                f.Recommendations.Add("Check whether the first occurrence lines up with a deployment, a configuration change or a data import.");
                if (g.DistinctRequests > 0 && g.DistinctRequests < g.Occurrences)
                    f.Recommendations.Add("Some occurrences share a request ID: check whether the same operation is being retried.");
                f.Recommendations.Add("Reproduce the failure in a non-production environment with the same message, table and input.");

                FindingHelper.AddTraces(f, g.Traces);
                yield return f;
            }
        }
    }
}
