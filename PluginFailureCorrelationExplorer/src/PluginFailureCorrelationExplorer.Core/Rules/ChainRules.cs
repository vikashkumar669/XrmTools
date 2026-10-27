using System;
using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Rules
{
    /// <summary>
    /// PFC008: failures from several plug-in types or depths inside one causal chain. Chains whose failures have the same
    /// shape (same failure groups) are reported together, so one propagating defect produces one finding.
    /// </summary>
    public sealed class CorrelatedFailureChainRule : IDiagnosticRule
    {
        public const int MaxFindings = 5;
        public const int MaxExampleChains = 3;

        public string RuleId => "PFC008";
        public string Name => "Failures in one causal chain";
        public string Description => "One correlation ID holds failures from at least two plug-in types or depths, which can indicate a failure that propagated through the chain. Chains with the same failure groups are reported together.";

        public IEnumerable<DiagnosticFinding> Evaluate(AnalysisContext context)
        {
            if (context.Correlation == null) yield break;
            var keys = context.Result.GroupKeys;
            var shapes = context.Correlation.Clusters
                .Select(c => new { Cluster = c, Failed = c.Traces.Where(t => t.HasException).ToList() })
                .Where(x => x.Failed.Count >= 2 &&
                            (x.Failed.Select(t => t.FullTypeName ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2 ||
                             x.Failed.Select(t => t.Depth ?? 0).Distinct().Count() >= 2))
                .GroupBy(x => string.Join("+", x.Failed.Select(t => keys.TryGetValue(t.Id, out var k) ? k : "?").Distinct().OrderBy(k => k, StringComparer.Ordinal)))
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Sum(x => x.Failed.Count))
                .Take(MaxFindings);

            foreach (var shape in shapes)
            {
                var chains = shape.ToList();
                var failed = chains.SelectMany(x => x.Failed).ToList();
                var ordered = failed.OrderBy(t => t.Depth ?? 0).ThenBy(t => t.StartUtc).ToList();
                var path = string.Join(" → ", ordered.Select(t => TypeNames.ShortName(t.TypeName) + " (depth " + (t.Depth?.ToString() ?? "?") + ")").Distinct());
                var f = FindingHelper.New(this, Severity.Warning, Confidence.Medium,
                    chains.Count == 1
                        ? $"{Fmt.Count(failed.Count)} failures in one causal chain: {path}"
                        : $"Failures propagating through {Fmt.Count(chains.Count)} causal chains: {path}",
                    $"{Fmt.Plural(chains.Count, "correlation ID")} each link failures from {path}, between {Fmt.Utc(chains.Min(x => x.Cluster.FirstUtc))} and {Fmt.Utc(chains.Max(x => x.Cluster.LastUtc))}.");
                f.Facts.Add($"Chains with this shape: {Fmt.Count(chains.Count)}; failed traces in them: {Fmt.Count(failed.Count)}; all traces in them: {Fmt.Count(chains.Sum(x => x.Cluster.Traces.Count))}.");
                f.Facts.Add("Example correlation IDs: " + string.Join(", ", chains.Take(MaxExampleChains).Select(x => x.Cluster.CorrelationId)) + (chains.Count > MaxExampleChains ? $" and {chains.Count - MaxExampleChains} more" : "") + ".");
                f.Correlations.Add($"[{EvidenceText.Label(EvidenceLevel.SameCausalChain)}] The failures in each chain share a correlation ID. This proves they belong to one causal chain, not that they are the same immediate operation.");

                var failedIds = new HashSet<Guid>(failed.Select(t => t.Id));
                var edges = chains.SelectMany(x => x.Cluster.SuspectedEdges).Where(e => failedIds.Contains(e.Parent.Id) && failedIds.Contains(e.Child.Id)).ToList();
                if (edges.Count > 0)
                {
                    var e = edges[0];
                    f.Correlations.Add($"[{EvidenceText.Label(EvidenceLevel.SuspectedParentChild)}] In {Fmt.Plural(edges.Count, "chain")}, {TypeNames.ShortName(e.Parent.TypeName)} (depth {e.Parent.Depth}) → {TypeNames.ShortName(e.Child.TypeName)} (depth {e.Child.Depth}); signals: {EvidenceText.Signals(e.Signals)}.");
                }

                f.PossibleExplanations.Add("A failure in a nested (deeper) synchronous step propagates to the step that triggered it, so one underlying failure can appear as several exceptions in one chain. Start with the deepest failure.");
                f.Recommendations.Add("Open one of the chains in the Trace explorer (Chain ◀ ▶) and read the failures in order of depth and start time.");
                f.Recommendations.Add("Check whether the deepest failure's message appears inside the shallower failures' messages.");
                FindingHelper.AddTraces(f, failed);
                yield return f;
            }
        }
    }

    /// <summary>PFC009: the same plug-in type and message run at several depths in one causal chain.</summary>
    public sealed class SuspectedRecursionRule : IDiagnosticRule
    {
        public const int MinDistinctDepths = 3;
        public const int MaxFindings = 5;

        public string RuleId => "PFC009";
        public string Name => "Suspected recursion";
        public string Description => "The same plug-in type and message execute at three or more depths within one correlation ID.";

        public IEnumerable<DiagnosticFinding> Evaluate(AnalysisContext context)
        {
            if (context.Correlation == null) yield break;
            var hits = context.Correlation.Clusters
                .SelectMany(c => c.Traces
                    .Where(t => !string.IsNullOrEmpty(t.FullTypeName) && t.Depth.HasValue)
                    .GroupBy(t => (Type: t.FullTypeName.ToLowerInvariant(), Message: (t.MessageName ?? "").ToLowerInvariant()))
                    .Select(g => new { Cluster = c, Traces = g.ToList(), Depths = g.Select(t => t.Depth.Value).Distinct().OrderBy(d => d).ToList() }))
                .Where(x => x.Depths.Count >= MinDistinctDepths)
                .OrderByDescending(x => x.Depths.Count)
                .Take(MaxFindings);

            foreach (var x in hits)
            {
                var first = x.Traces[0];
                var f = FindingHelper.New(this, Severity.Warning, Confidence.Low,
                    $"Suspected recursion: {TypeNames.ShortName(first.TypeName)} on {first.MessageName} at depths {string.Join(", ", x.Depths)}",
                    $"Within correlation ID {x.Cluster.CorrelationId}, {first.FullTypeName} ran for {first.MessageName} at {Fmt.Plural(x.Depths.Count, "depth")}.");
                f.Facts.Add($"Executions in the chain: {Fmt.Count(x.Traces.Count)}; depths: {string.Join(", ", x.Depths)}; failures: {Fmt.Count(x.Traces.Count(t => t.HasException))}.");
                f.Correlations.Add($"[{EvidenceText.Label(EvidenceLevel.SameCausalChain)}] Same correlation ID {x.Cluster.CorrelationId}.");
                f.PossibleExplanations.Add("A step that updates the record it is registered on (or a related record that triggers it back) re-triggers itself. Dataverse stops such loops at depth 8 with an error.");
                f.PossibleExplanations.Add("This can also be legitimate: a hierarchy update that cascades to child records runs the same step at increasing depths.");
                f.Recommendations.Add("Check whether the step updates attributes that are in its own filtering attributes, and whether it guards with context.Depth.");
                FindingHelper.AddTraces(f, x.Traces);
                yield return f;
            }
        }
    }
}
