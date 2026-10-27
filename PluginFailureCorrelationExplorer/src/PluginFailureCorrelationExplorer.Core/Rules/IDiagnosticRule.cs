using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Rules
{
    /// <summary>
    /// A diagnostic rule. Rules read the analysis context and return findings; they never change the context.
    /// Each rule is independently testable: build an <see cref="AnalysisContext"/> from synthetic traces and call
    /// <see cref="Evaluate"/>.
    /// </summary>
    public interface IDiagnosticRule
    {
        /// <summary>Stable identifier, e.g. PFC001. Never reuse an identifier for a different rule.</summary>
        string RuleId { get; }

        string Name { get; }

        /// <summary>What the rule checks, for documentation and the settings screen.</summary>
        string Description { get; }

        IEnumerable<DiagnosticFinding> Evaluate(AnalysisContext context);
    }

    /// <summary>Everything a rule can read.</summary>
    public sealed class AnalysisContext
    {
        public AnalysisContext(AnalysisResult result, FailureCorrelationService correlation)
        {
            Result = result;
            Correlation = correlation;
        }

        public AnalysisResult Result { get; }
        public FailureCorrelationService Correlation { get; }
        public AnalysisOptions Options => Result.Options;
        public IReadOnlyList<PluginTraceRecord> Records => Result.Records;
        public IReadOnlyList<FailureGroup> FailureGroups => Result.FailureGroups;
        public IReadOnlyList<PerformanceGroup> PerformanceGroups => Result.PerformanceGroups;
        public TrendComparison Trend => Result.Trend;
    }

    /// <summary>Helpers shared by the built-in rules.</summary>
    public static class FindingHelper
    {
        /// <summary>Supporting trace IDs listed per finding.</summary>
        public const int MaxTraceIds = 25;

        public static DiagnosticFinding New(IDiagnosticRule rule, Severity severity, Confidence confidence, string title, string description)
        {
            return new DiagnosticFinding
            {
                RuleId = rule.RuleId,
                Severity = severity,
                Confidence = confidence,
                Title = title,
                Description = description,
            };
        }

        public static void AddTraces(DiagnosticFinding finding, IEnumerable<PluginTraceRecord> traces)
        {
            var all = traces.Select(t => t.Id).Distinct().ToList();
            finding.TraceIds.AddRange(all.Take(MaxTraceIds));
            finding.AdditionalTraceCount = System.Math.Max(0, all.Count - MaxTraceIds);
        }
    }
}
