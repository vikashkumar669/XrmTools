using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Rules
{
    /// <summary>PFC003: executions at or above the configured threshold for their mode.</summary>
    public sealed class SlowExecutionRule : IDiagnosticRule
    {
        public string RuleId => "PFC003";
        public string Name => "Slow execution";
        public string Description => "Executions with a recorded duration at or above the configured threshold for their execution mode (sync or async).";

        public IEnumerable<DiagnosticFinding> Evaluate(AnalysisContext context)
        {
            foreach (var p in context.PerformanceGroups.Where(p => p.SlowCount > 0))
            {
                var nearTimeout = p.NearTimeoutCount > 0;
                var f = FindingHelper.New(this, nearTimeout ? Severity.Error : Severity.Warning, Confidence.High,
                    $"Slow {p.ModeText.ToLowerInvariant()} execution: {TypeNames.ShortName(p.PluginType)} on {p.MessageName}",
                    $"{Fmt.Count(p.SlowCount)} of {Fmt.Count(p.Measured)} measured executions took at least the {p.ModeText.ToLowerInvariant()} threshold of {Fmt.Ms(p.ThresholdMs)}." +
                    (nearTimeout ? $" {Fmt.Count(p.NearTimeoutCount)} came within reach of the 2-minute synchronous timeout." : ""));
                f.RelatedKey = p.Key;
                f.Facts.Add($"Plug-in type: {p.PluginType}; message: {p.MessageName}; mode: {p.ModeText}; tables: {(string.IsNullOrEmpty(p.PrimaryEntity) ? "(none)" : p.PrimaryEntity)}.");
                f.Facts.Add($"Slowest execution: {Fmt.Ms(p.MaxMs)}. Executions in group: {Fmt.Count(p.Executions)}, with a duration: {Fmt.Count(p.Measured)}.");
                f.Metrics.Add($"Share at or above the threshold: {Fmt.Percent(p.SlowCount, p.Measured)}.");
                if (p.AverageMs.HasValue) f.Metrics.Add($"Average {Fmt.Ms(p.AverageMs)}, median {Fmt.Ms(p.MedianMs)}" + (p.P95Ms.HasValue ? $", 95th percentile {Fmt.Ms(p.P95Ms)} (nearest rank)." : "."));
                if (!string.IsNullOrEmpty(p.SampleNote)) f.Metrics.Add("Note: " + p.SampleNote + ".");
                if (p.AverageConstructorMs.HasValue && p.AverageConstructorMs.Value > 100)
                    f.Metrics.Add($"Average constructor time {Fmt.Ms(p.AverageConstructorMs)} (reported separately from execution time).");

                f.PossibleExplanations.Add("Common causes of slow steps include external HTTP calls, retrieving many records or columns, per-record queries in a loop, and lock contention with concurrent operations. The traces alone don't show which applies.");
                if (p.Mode == 0)
                    f.PossibleExplanations.Add("Synchronous steps add their duration to the user's save time; work that doesn't need to block the user could move to an asynchronous step.");

                f.Recommendations.Add("Read the trace output of the slowest executions for timing markers, and add timing traces around external calls and queries if there are none.");
                f.Recommendations.Add("Check whether the slow executions cluster at certain times (bulk jobs, integrations) or are spread evenly.");
                if (nearTimeout) f.Recommendations.Add("Prioritise this step: synchronous executions over 2 minutes are cancelled by the platform.");

                FindingHelper.AddTraces(f, p.SlowTraces);
                yield return f;
            }
        }
    }
}
