using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Rules
{
    /// <summary>PFC006: new or increasing failure patterns compared with a valid baseline.</summary>
    public sealed class IncreasingFailureRule : IDiagnosticRule
    {
        public string RuleId => "PFC006";
        public string Name => "New or increasing failure";
        public string Description => "Compared with a valid baseline, a failure group is new or its hourly rate rose by at least the configured ratio (default 2×). Produces nothing when no valid baseline exists.";

        public IEnumerable<DiagnosticFinding> Evaluate(AnalysisContext context)
        {
            var trend = context.Trend;
            if (trend == null || !trend.Available) yield break;

            foreach (var t in trend.Failures.Where(t => t.Status == TrendStatus.New || t.Status == TrendStatus.Increased))
            {
                var group = context.FailureGroups.FirstOrDefault(g => g.Key == t.Key);
                var isNew = t.Status == TrendStatus.New;
                var f = FindingHelper.New(this, isNew && t.CurrentCount < context.Options.RecurringMinOccurrences ? Severity.Information : Severity.Warning, Confidence.Medium,
                    (isNew ? "New failure pattern: " : "Increasing failure pattern: ") + t.Label,
                    isNew
                        ? $"Failure group {t.Key} occurred {Fmt.Plural(t.CurrentCount, "time")} in the current period and not at all in the baseline."
                        : $"Failure group {t.Key} rose from {Fmt.Rate(t.BaselinePerHour)} to {Fmt.Rate(t.CurrentPerHour)}.");
                f.RelatedKey = t.Key;
                f.Facts.Add($"Current period: {Fmt.Count(t.CurrentCount)} occurrences. Baseline: {Fmt.Count(t.BaselineCount)} occurrences.");
                f.Facts.Add("Baseline: " + trend.BaselineDescription + ".");
                f.Metrics.Add($"Hourly rate: current {Fmt.Rate(t.CurrentPerHour)}, baseline {Fmt.Rate(t.BaselinePerHour)}" +
                              (t.BaselinePerHour > 0 ? $" ({Fmt.Ratio(t.CurrentPerHour / t.BaselinePerHour)})." : "."));
                foreach (var c in trend.Caveats) f.Metrics.Add("Caveat: " + c);
                f.PossibleExplanations.Add("A new or increasing pattern often follows a deployment, a configuration change, a new integration or a change in data. The comparison shows when, not why.");
                f.Recommendations.Add("Compare the time of the first occurrence with recent deployments and configuration changes.");
                f.Recommendations.Add("Open the failure group to inspect the original exception text.");
                if (group != null) FindingHelper.AddTraces(f, group.Traces);
                yield return f;
            }
        }
    }

    /// <summary>PFC007: performance regression compared with a valid baseline.</summary>
    public sealed class PerformanceRegressionRule : IDiagnosticRule
    {
        public string RuleId => "PFC007";
        public string Name => "Performance regression";
        public string Description => "Compared with a valid baseline, a plug-in type + message + mode has a median duration at least the configured ratio (default 1.5×) of the baseline median, with enough executions in both periods.";

        public IEnumerable<DiagnosticFinding> Evaluate(AnalysisContext context)
        {
            var trend = context.Trend;
            if (trend == null || !trend.Available) yield break;

            foreach (var t in trend.Performance.Where(t => t.IsRegression))
            {
                var group = context.PerformanceGroups.FirstOrDefault(p => p.Key == t.Key);
                var f = FindingHelper.New(this, Severity.Warning, Confidence.Medium,
                    "Possible performance regression: " + t.Label,
                    $"The median duration rose from {Fmt.Ms(t.BaselineMedianMs)} to {Fmt.Ms(t.CurrentMedianMs)} ({Fmt.Ratio(t.Ratio)}).");
                f.RelatedKey = t.Key;
                f.Facts.Add($"Executions measured: current {Fmt.Count(t.CurrentSample)}, baseline {Fmt.Count(t.BaselineSample)}.");
                f.Facts.Add("Baseline: " + trend.BaselineDescription + ".");
                f.Metrics.Add($"Median ratio: {Fmt.Ratio(t.Ratio)} (configured regression ratio {Fmt.Ratio(context.Options.RegressionRatio)}).");
                foreach (var c in trend.Caveats) f.Metrics.Add("Caveat: " + c);
                f.PossibleExplanations.Add("A slower median can come from a code change, more data being processed per execution, a slower external dependency or higher concurrent load.");
                f.Recommendations.Add("Compare the step's assembly version and the trace output between a fast baseline execution and a slow current one.");
                if (group != null) FindingHelper.AddTraces(f, group.SlowTraces.Count > 0 ? group.SlowTraces : group.Traces);
                yield return f;
            }
        }
    }
}
