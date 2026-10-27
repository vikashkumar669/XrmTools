using System;
using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Analysis
{
    /// <summary>
    /// Evidence-based correlation between traces. Each relationship carries an <see cref="EvidenceLevel"/>, the signals that
    /// produced it and an explanation. Rules:
    /// <list type="bullet">
    /// <item>Same request ID → exact match.</item>
    /// <item>Same correlation ID → same causal chain, never "same operation".</item>
    /// <item>Parent/child is only ever suspected: depth differs by one, both executions have known timing, the child runs
    /// inside the parent's window, and exactly one candidate parent exists.</item>
    /// <item>Heuristic links need time proximity plus at least two matching context signals, and are never made between
    /// traces whose correlation IDs are both known and different. Time proximity alone never links traces.</item>
    /// <item>Same fingerprint → same failure pattern (recurrence, not a shared transaction).</item>
    /// </list>
    /// </summary>
    public sealed class FailureCorrelationService
    {
        /// <summary>Maximum same-pattern traces returned for one selection.</summary>
        public const int MaxPatternMatches = 50;

        /// <summary>Maximum heuristic matches returned for one selection.</summary>
        public const int MaxHeuristicMatches = 25;

        private readonly AnalysisOptions _options;
        private readonly List<PluginTraceRecord> _byStart;
        private readonly Dictionary<Guid, List<PluginTraceRecord>> _byRequest;
        private readonly Dictionary<Guid, List<PluginTraceRecord>> _byCorrelation;
        private readonly Dictionary<string, List<PluginTraceRecord>> _byGroup;
        private readonly IDictionary<Guid, string> _groupKeys;
        private readonly Dictionary<Guid, CorrelationCluster> _clusters = new Dictionary<Guid, CorrelationCluster>();

        public FailureCorrelationService(IEnumerable<PluginTraceRecord> records, AnalysisOptions options, IDictionary<Guid, string> groupKeys = null)
        {
            _options = options ?? new AnalysisOptions();
            var list = (records ?? Enumerable.Empty<PluginTraceRecord>()).Where(r => r != null).ToList();
            _groupKeys = groupKeys ?? new Dictionary<Guid, string>();
            _byStart = list.OrderBy(r => r.StartUtc).ThenBy(r => r.Id).ToList();
            _byRequest = Index(list, r => r.RequestId);
            _byCorrelation = Index(list, r => r.CorrelationId);
            _byGroup = list.Where(r => _groupKeys.ContainsKey(r.Id))
                .GroupBy(r => _groupKeys[r.Id], StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            foreach (var pair in _byCorrelation)
            {
                if (pair.Value.Count < 2) continue;
                _clusters[pair.Key] = BuildCluster(pair.Key, pair.Value);
            }
        }

        /// <summary>Correlation IDs shared by two or more traces, failures first.</summary>
        public IReadOnlyList<CorrelationCluster> Clusters =>
            _clusters.Values.OrderByDescending(c => c.FailureCount).ThenByDescending(c => c.Traces.Count).ThenBy(c => c.FirstUtc).ToList();

        public CorrelationCluster ClusterFor(PluginTraceRecord record) =>
            record?.CorrelationId != null && _clusters.TryGetValue(record.CorrelationId.Value, out var c) ? c : null;

        /// <summary>Traces related to <paramref name="selected"/>, strongest evidence first. The selected trace is excluded.</summary>
        public List<RelatedTrace> FindRelated(PluginTraceRecord selected)
        {
            var result = new List<RelatedTrace>();
            if (selected == null) return result;
            var seen = new HashSet<Guid> { selected.Id };

            // 1. Exact: same request ID
            if (selected.RequestId.HasValue && _byRequest.TryGetValue(selected.RequestId.Value, out var sameRequest))
            {
                foreach (var t in sameRequest.Where(t => seen.Add(t.Id)))
                {
                    var signals = CorrelationSignal.RequestId | ContextSignals(selected, t);
                    if (SameValue(selected.CorrelationId, t.CorrelationId)) signals |= CorrelationSignal.CorrelationId;
                    result.Add(Related(t, EvidenceLevel.ExactRequest, signals, null));
                }
            }

            // 2. Same causal chain, with suspected parent/child where the evidence allows
            var cluster = ClusterFor(selected);
            if (cluster != null)
            {
                foreach (var t in cluster.Traces.Where(t => seen.Add(t.Id)))
                {
                    var edge = cluster.SuspectedEdges.FirstOrDefault(e =>
                        (e.Parent.Id == selected.Id && e.Child.Id == t.Id) || (e.Child.Id == selected.Id && e.Parent.Id == t.Id));
                    if (edge.Parent != null)
                    {
                        var role = edge.Parent.Id == t.Id ? "parent" : "child";
                        result.Add(Related(t, EvidenceLevel.SuspectedParentChild, edge.Signals | ContextSignals(selected, t), role));
                    }
                    else
                    {
                        result.Add(Related(t, EvidenceLevel.SameCausalChain, CorrelationSignal.CorrelationId | ContextSignals(selected, t), null));
                    }
                }
            }

            // 3. Heuristic: only when at least one side has no correlation ID
            var window = TimeSpan.FromSeconds(_options.HeuristicWindowSeconds);
            var heuristics = new List<RelatedTrace>();
            foreach (var t in Window(selected.StartUtc - window, selected.StartUtc + window))
            {
                if (seen.Contains(t.Id)) continue;
                // Both correlation IDs known and different: provably separate chains, so no heuristic link.
                // (Equal IDs were already handled as the same causal chain above.)
                if (selected.CorrelationId.HasValue && t.CorrelationId.HasValue) continue;
                var context = ContextSignals(selected, t);
                if (CountContext(context) < 2) continue;
                heuristics.Add(Related(t, EvidenceLevel.Heuristic, context | CorrelationSignal.TimeProximity, null));
            }
            foreach (var h in heuristics.OrderBy(h => Math.Abs((h.Trace.StartUtc - selected.StartUtc).Ticks)).Take(MaxHeuristicMatches))
            {
                seen.Add(h.Trace.Id);
                result.Add(h);
            }

            // 4. Same failure pattern
            if (_groupKeys.TryGetValue(selected.Id, out var key) && _byGroup.TryGetValue(key, out var sameGroup))
            {
                foreach (var t in sameGroup.Where(t => !seen.Contains(t.Id))
                    .OrderBy(t => Math.Abs((t.CreatedOnUtc - selected.CreatedOnUtc).Ticks)).Take(MaxPatternMatches))
                {
                    seen.Add(t.Id);
                    result.Add(Related(t, EvidenceLevel.SamePattern, CorrelationSignal.SameFingerprint | ContextSignals(selected, t), null));
                }
            }

            return result
                .OrderBy(r => r.Level)
                .ThenBy(r => r.Trace.StartUtc)
                .ToList();
        }

        /// <summary>Signals from matching context (plug-in type, step, message, table).</summary>
        public static CorrelationSignal ContextSignals(PluginTraceRecord a, PluginTraceRecord b)
        {
            var s = CorrelationSignal.None;
            if (!string.IsNullOrEmpty(a.FullTypeName) && string.Equals(a.FullTypeName, b.FullTypeName, StringComparison.OrdinalIgnoreCase)) s |= CorrelationSignal.SamePluginType;
            if (SameValue(a.PluginStepId, b.PluginStepId)) s |= CorrelationSignal.SameStep;
            if (!string.IsNullOrEmpty(a.MessageName) && string.Equals(a.MessageName, b.MessageName, StringComparison.OrdinalIgnoreCase)) s |= CorrelationSignal.SameMessage;
            if (!string.IsNullOrEmpty(a.PrimaryEntity) && !string.Equals(a.PrimaryEntity, "none", StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.PrimaryEntity, b.PrimaryEntity, StringComparison.OrdinalIgnoreCase)) s |= CorrelationSignal.SamePrimaryEntity;
            return s;
        }

        private static int CountContext(CorrelationSignal s)
        {
            var n = 0;
            if (s.HasFlag(CorrelationSignal.SamePluginType)) n++;
            if (s.HasFlag(CorrelationSignal.SameMessage)) n++;
            if (s.HasFlag(CorrelationSignal.SamePrimaryEntity)) n++;
            return n;
        }

        private static RelatedTrace Related(PluginTraceRecord t, EvidenceLevel level, CorrelationSignal signals, string role)
        {
            var explanation = EvidenceText.Meaning(level) + " Signals: " + EvidenceText.Signals(signals) + ".";
            if (role != null) explanation = "Suspected " + role + ". " + explanation;
            return new RelatedTrace { Trace = t, Level = level, Signals = signals, Explanation = explanation, Role = role };
        }

        private CorrelationCluster BuildCluster(Guid id, List<PluginTraceRecord> traces)
        {
            var cluster = new CorrelationCluster { CorrelationId = id };
            cluster.Traces.AddRange(traces.OrderBy(t => t.StartUtc).ThenBy(t => t.Depth ?? 0).ThenBy(t => t.Id));
            var tolerance = TimeSpan.FromMilliseconds(_options.ParentChildToleranceMs);

            foreach (var child in cluster.Traces)
            {
                if (!child.Depth.HasValue || child.Depth.Value <= 1 || !HasTiming(child)) continue;
                var candidates = cluster.Traces.Where(p =>
                        p.Id != child.Id && p.Depth == child.Depth - 1 && HasTiming(p) &&
                        child.StartUtc >= p.StartUtc - tolerance && child.EndUtc <= p.EndUtc + tolerance)
                    .ToList();
                // Ambiguous (several possible parents) or none: no edge. A guess would be presented as structure.
                if (candidates.Count == 1)
                    cluster.SuspectedEdges.Add((candidates[0], child, CorrelationSignal.CorrelationId | CorrelationSignal.DepthOrder | CorrelationSignal.TimeContainment));
            }
            return cluster;
        }

        /// <summary>Timing is known when the duration is recorded (start can then be derived).</summary>
        private static bool HasTiming(PluginTraceRecord r) => r.ExecutionDurationMs.HasValue;

        private IEnumerable<PluginTraceRecord> Window(DateTime from, DateTime to)
        {
            // Binary search for the first trace starting at or after "from"
            int lo = 0, hi = _byStart.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (_byStart[mid].StartUtc < from) lo = mid + 1; else hi = mid;
            }
            for (var i = lo; i < _byStart.Count && _byStart[i].StartUtc <= to; i++) yield return _byStart[i];
        }

        private static Dictionary<Guid, List<PluginTraceRecord>> Index(IEnumerable<PluginTraceRecord> records, Func<PluginTraceRecord, Guid?> key)
        {
            var map = new Dictionary<Guid, List<PluginTraceRecord>>();
            foreach (var r in records)
            {
                var k = key(r);
                if (!k.HasValue || k.Value == Guid.Empty) continue;
                if (!map.TryGetValue(k.Value, out var list)) map[k.Value] = list = new List<PluginTraceRecord>();
                list.Add(r);
            }
            return map;
        }

        private static bool SameValue(Guid? a, Guid? b) => a.HasValue && b.HasValue && a.Value != Guid.Empty && a.Value == b.Value;
    }
}
