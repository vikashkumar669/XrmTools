using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Rules
{
    /// <summary>PFC002: a failure occurs at a high rate.</summary>
    public sealed class HighFrequencyFailureRule : IDiagnosticRule
    {
        public string RuleId => "PFC002";
        public string Name => "High-frequency failure";
        public string Description => "A recurring failure occurs more often per hour than the configured limit (default 30/h) over its active span.";

        public IEnumerable<DiagnosticFinding> Evaluate(AnalysisContext context)
        {
            var o = context.Options;
            foreach (var g in context.FailureGroups.Where(g => g.Occurrences >= o.RecurringMinOccurrences && g.RatePerHour >= o.HighFrequencyPerHour))
            {
                var f = FindingHelper.New(this, Severity.Error, Confidence.High,
                    $"High-frequency failure: {Fmt.Rate(g.RatePerHour)} for {g.Fingerprint.Label}",
                    $"Failure group {g.Key} occurs at {Fmt.Rate(g.RatePerHour)}, above the configured limit of {Fmt.Rate(o.HighFrequencyPerHour)}.");
                f.RelatedKey = g.Key;
                f.Facts.Add($"{Fmt.Count(g.Occurrences)} occurrences between {Fmt.Utc(g.FirstUtc)} and {Fmt.Utc(g.LastUtc)}.");
                f.Facts.Add($"Plug-in types: {Fmt.List(g.PluginTypes)}.");
                f.Metrics.Add($"Rate: occurrences ÷ active span in hours (minimum 1 hour) = {Fmt.Rate(g.RatePerHour)}.");
                if (context.Result.Summary.Query.LimitReached)
                    f.Metrics.Add("The retrieval stopped at the result limit, so the true count may be higher.");
                f.PossibleExplanations.Add("A high rate can mean an integration, bulk operation or scheduled job is calling the failing operation repeatedly.");
                f.Recommendations.Add("Identify the caller: compare the request and correlation IDs and the trace output for a common source.");
                f.Recommendations.Add("Consider pausing or throttling the caller while the failure is investigated.");
                FindingHelper.AddTraces(f, g.Traces);
                yield return f;
            }
        }
    }
}
