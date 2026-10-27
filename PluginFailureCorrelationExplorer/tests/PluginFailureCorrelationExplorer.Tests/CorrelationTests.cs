using System;
using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;
using static PluginFailureCorrelationExplorer.Tests.Samples;

namespace PluginFailureCorrelationExplorer.Tests
{
    public static class CorrelationTests
    {
        private static FailureCorrelationService Service(IEnumerable<PluginTraceRecord> traces, AnalysisOptions o = null, IDictionary<Guid, string> keys = null) =>
            new FailureCorrelationService(traces, o ?? new AnalysisOptions(), keys);

        private static RelatedTrace Find(List<RelatedTrace> related, PluginTraceRecord t) => related.SingleOrDefault(r => r.Trace.Id == t.Id);

        [Test]
        public static void Same_request_id_is_an_exact_match()
        {
            var request = Guid.NewGuid();
            var a = Trace(request: request, correlation: Guid.NewGuid());
            var b = Trace(type: ContactPlugin, minutes: 30, request: request);
            var related = Service(new[] { a, b }).FindRelated(a);
            var r = Find(related, b);
            Assert.Equal(EvidenceLevel.ExactRequest, r.Level);
            Assert.True(r.Signals.HasFlag(CorrelationSignal.RequestId));
            Assert.False(r.Signals.HasFlag(CorrelationSignal.CorrelationId), "different correlation IDs are not a correlation signal");
        }

        [Test]
        public static void Same_correlation_id_is_same_causal_chain_not_same_operation()
        {
            var chain = Guid.NewGuid();
            var a = Trace(correlation: chain, request: Guid.NewGuid());
            var b = Trace(type: ContactPlugin, minutes: 5, correlation: chain, request: Guid.NewGuid(), mode: 1);
            var r = Find(Service(new[] { a, b }).FindRelated(a), b);
            Assert.Equal(EvidenceLevel.SameCausalChain, r.Level);
            Assert.Contains("does not prove they are the same immediate operation", r.Explanation);
        }

        [Test]
        public static void Nested_execution_inside_parent_window_is_a_suspected_child()
        {
            var chain = Guid.NewGuid();
            var parent = Timed(0, 1000, 1, chain, AccountPlugin, "Create");
            var child = Timed(200, 300, 2, chain, ContactPlugin, "Update", "contact");
            var service = Service(new[] { parent, child });

            var fromParent = Find(service.FindRelated(parent), child);
            Assert.Equal(EvidenceLevel.SuspectedParentChild, fromParent.Level);
            Assert.Equal("child", fromParent.Role);
            Assert.True(fromParent.Signals.HasFlag(CorrelationSignal.DepthOrder | CorrelationSignal.TimeContainment));
            Assert.Contains("Suspected child", fromParent.Explanation);

            var fromChild = Find(service.FindRelated(child), parent);
            Assert.Equal("parent", fromChild.Role);
            Assert.Equal(1, service.ClusterFor(parent).SuspectedEdges.Count);
        }

        [Test]
        public static void Ambiguous_parents_produce_no_parent_child_edge()
        {
            var chain = Guid.NewGuid();
            var p1 = Timed(0, 1000, 1, chain);
            var p2 = Timed(50, 1000, 1, chain, ContactPlugin);
            var child = Timed(200, 100, 2, chain, "Contoso.Plugins.Audit");
            var service = Service(new[] { p1, p2, child });
            Assert.Equal(0, service.ClusterFor(child).SuspectedEdges.Count);
            Assert.Equal(EvidenceLevel.SameCausalChain, Find(service.FindRelated(child), p1).Level);
        }

        [Test]
        public static void Async_continuation_after_the_parent_is_not_a_child()
        {
            var chain = Guid.NewGuid();
            var parent = Timed(0, 500, 1, chain);
            var asyncStep = Timed(5000, 200, 2, chain, ContactPlugin, mode: 1);
            var r = Find(Service(new[] { parent, asyncStep }).FindRelated(parent), asyncStep);
            Assert.Equal(EvidenceLevel.SameCausalChain, r.Level);
        }

        [Test]
        public static void Missing_duration_means_no_parent_child_inference()
        {
            var chain = Guid.NewGuid();
            var parent = Timed(0, 1000, 1, chain);
            var child = Trace(type: ContactPlugin, correlation: chain, depth: 2, duration: null);
            child.CreatedOnUtc = T0.AddMilliseconds(500);
            var service = Service(new[] { parent, child });
            Assert.Equal(0, service.ClusterFor(parent).SuspectedEdges.Count);
        }

        [Test]
        public static void Heuristic_link_needs_context_and_no_shared_identifier()
        {
            var a = Trace(minutes: 0, correlation: null, request: null, exception: Fault("x"));
            var b = Trace(minutes: 0.05, correlation: null, request: null);            // 3 s later, same type/message/table
            var r = Find(Service(new[] { a, b }).FindRelated(a), b);
            Assert.Equal(EvidenceLevel.Heuristic, r.Level);
            Assert.True(r.Signals.HasFlag(CorrelationSignal.TimeProximity));
            Assert.Contains("not as proof of a shared transaction", r.Explanation);
        }

        [Test]
        public static void Time_proximity_alone_never_links_traces()
        {
            var a = Trace(type: AccountPlugin, message: "Create", table: "account", correlation: null);
            var b = Trace(type: ContactPlugin, message: "Update", table: "contact", minutes: 0.01, correlation: null);
            Assert.Equal(0, Service(new[] { a, b }).FindRelated(a).Count);
        }

        [Test]
        public static void One_matching_context_signal_is_not_enough()
        {
            var a = Trace(type: AccountPlugin, message: "Create", table: "account", correlation: null);
            var b = Trace(type: ContactPlugin, message: "Create", table: "contact", minutes: 0.01, correlation: null);
            Assert.Equal(0, Service(new[] { a, b }).FindRelated(a).Count);
        }

        [Test]
        public static void Different_known_correlation_ids_are_never_heuristically_linked()
        {
            var a = Trace(correlation: Guid.NewGuid());
            var b = Trace(minutes: 0.01, correlation: Guid.NewGuid());
            Assert.Equal(0, Service(new[] { a, b }).FindRelated(a).Count);
        }

        [Test]
        public static void Heuristic_window_boundary_is_inclusive()
        {
            var options = new AnalysisOptions { HeuristicWindowSeconds = 5 };
            var a = Timed(0, 100, 1, null);
            var atEdge = Timed(5000, 100, 1, null);
            var beyond = Timed(5001, 100, 1, null);
            var related = Service(new[] { a, atEdge, beyond }, options).FindRelated(a);
            Check.NotNull(Find(related, atEdge), "exactly at the window edge");
            Check.Null(Find(related, beyond), "1 ms beyond the window");
        }

        [Test]
        public static void Same_fingerprint_is_a_pattern_match_only()
        {
            var a = Trace(correlation: Guid.NewGuid(), exception: Fault("x"));
            var b = Trace(minutes: 600, correlation: Guid.NewGuid(), exception: Fault("x"));
            var keys = new Dictionary<Guid, string> { [a.Id] = "k1", [b.Id] = "k1" };
            var r = Find(Service(new[] { a, b }, keys: keys).FindRelated(a), b);
            Assert.Equal(EvidenceLevel.SamePattern, r.Level);
            Assert.Contains("recurrence of the same failure", r.Explanation);
        }

        [Test]
        public static void Results_are_ordered_strongest_evidence_first_and_exclude_the_selection()
        {
            var chain = Guid.NewGuid();
            var request = Guid.NewGuid();
            var a = Trace(correlation: chain, request: request, exception: Fault("x"));
            var sameRequest = Trace(minutes: 1, correlation: chain, request: request, type: ContactPlugin);
            var sameChain = Trace(minutes: 2, correlation: chain, request: Guid.NewGuid(), type: "Contoso.Plugins.Audit");
            var samePattern = Trace(minutes: 300, correlation: Guid.NewGuid(), exception: Fault("x"));
            var keys = new Dictionary<Guid, string> { [a.Id] = "k", [samePattern.Id] = "k" };
            var related = Service(new[] { samePattern, sameChain, sameRequest, a }, keys: keys).FindRelated(a);
            Assert.Equal(3, related.Count);
            Assert.Equal(EvidenceLevel.ExactRequest, related[0].Level);
            Assert.Equal(EvidenceLevel.SameCausalChain, related[1].Level);
            Assert.Equal(EvidenceLevel.SamePattern, related[2].Level);
            Assert.False(related.Any(r => r.Trace.Id == a.Id), "selection excluded");
        }

        [Test]
        public static void Traces_without_any_identifier_do_not_break_correlation()
        {
            var bare = new PluginTraceRecord { Id = Guid.NewGuid(), CreatedOnUtc = T0 };
            var other = new PluginTraceRecord { Id = Guid.NewGuid(), CreatedOnUtc = T0.AddSeconds(1) };
            var service = Service(new[] { bare, other });
            Assert.Equal(0, service.FindRelated(bare).Count);
            Check.Null(service.ClusterFor(bare));
            Assert.Equal(0, service.FindRelated(null).Count);
        }

        [Test]
        public static void Clusters_list_chains_with_failures_first()
        {
            var c1 = Guid.NewGuid();
            var c2 = Guid.NewGuid();
            var traces = new[]
            {
                Trace(correlation: c1), Trace(correlation: c1, minutes: 1),
                Trace(correlation: c2, minutes: 2, exception: Fault("x")), Trace(correlation: c2, minutes: 3),
                Trace(correlation: Guid.NewGuid()), // single trace: no cluster
            };
            var clusters = Service(traces).Clusters;
            Assert.Equal(2, clusters.Count);
            Assert.Equal(c2, clusters[0].CorrelationId);
            Assert.Equal(1, clusters[0].FailureCount);
        }
    }
}
