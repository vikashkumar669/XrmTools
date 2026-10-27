using System;
using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;
using PluginFailureCorrelationExplorer.Rules;
using static PluginFailureCorrelationExplorer.Tests.Samples;

namespace PluginFailureCorrelationExplorer.Tests
{
    public static class RuleTests
    {
        private static List<DiagnosticFinding> Run(IDiagnosticRule rule, IEnumerable<PluginTraceRecord> traces, AnalysisOptions options = null, Action<AnalysisResult> tweak = null)
        {
            var engine = new AnalysisEngine(options ?? new AnalysisOptions(), new IDiagnosticRule[0]);
            var result = engine.Analyze(traces, Query(), Org(), null, T0.AddDays(1));
            tweak?.Invoke(result);
            return rule.Evaluate(new AnalysisContext(result, engine.Correlation)).ToList();
        }

        [Test]
        public static void Recurring_failure_needs_the_configured_occurrences()
        {
            var rule = new RecurringFailureRule();
            Assert.Equal(0, Run(rule, Repeat(2, i => Trace(minutes: i, exception: Fault("x")))).Count);
            var f = Run(rule, Repeat(3, i => Trace(minutes: i, exception: Fault("x")))).Single();
            Assert.Equal(Severity.Error, f.Severity);       // synchronous
            Assert.Equal(Confidence.High, f.Confidence);
            Assert.Equal(3, f.TraceIds.Count);
            Assert.True(f.PossibleExplanations.Any(p => p.Contains("InvalidPluginExecutionException")), "hint for -2147220891");
            Assert.True(f.Recommendations.Count > 0);
        }

        [Test]
        public static void Recurring_async_failure_is_a_warning()
        {
            var f = Run(new RecurringFailureRule(), Repeat(3, i => Trace(minutes: i, mode: 1, exception: Fault("x")))).Single();
            Assert.Equal(Severity.Warning, f.Severity);
        }

        [Test]
        public static void High_frequency_uses_the_active_span()
        {
            var rule = new HighFrequencyFailureRule();
            // 40 failures in 30 minutes → span counted as 1 hour → 40/h ≥ 30/h
            Assert.Equal(1, Run(rule, Repeat(40, i => Trace(minutes: i * 0.75, exception: Fault("x")))).Count);
            // 40 failures over 4 hours → 10/h
            Assert.Equal(0, Run(rule, Repeat(40, i => Trace(minutes: i * 6, exception: Fault("x")))).Count);
        }

        [Test]
        public static void Slow_execution_is_an_error_only_near_the_timeout()
        {
            var rule = new SlowExecutionRule();
            var f = Run(rule, new[] { Trace(duration: 3000), Trace(duration: 100) }).Single();
            Assert.Equal(Severity.Warning, f.Severity);
            Assert.Equal(1, f.TraceIds.Count);
            Assert.True(f.PossibleExplanations.Count > 0 && f.Facts.Count > 0 && f.Metrics.Count > 0);

            f = Run(rule, new[] { Trace(duration: 115000) }).Single();
            Assert.Equal(Severity.Error, f.Severity);
            Assert.Equal(0, Run(rule, new[] { Trace(duration: 1500) }).Count);
        }

        [Test]
        public static void Hotspot_reports_failure_share_and_respects_exception_only_logging()
        {
            var traces = Repeat(3, i => Trace(minutes: i, exception: Fault("x" + i)))
                .Concat(Repeat(3, i => Trace(minutes: 10 + i))).ToList();
            var f = Run(new MessageEntityHotspotRule(), traces).Single();
            Assert.Equal(Severity.Warning, f.Severity);   // 50% failed
            Assert.Contains("50%", string.Join(" ", f.Metrics));

            f = Run(new MessageEntityHotspotRule(), traces, tweak: r => r.Summary.Organization.TraceSetting = "Exception").Single();
            Assert.True(f.Metrics.Any(m => m.Contains("Exception only")));
        }

        [Test]
        public static void Incomplete_evidence_lists_missing_identifiers()
        {
            var bare = new PluginTraceRecord { Id = Guid.NewGuid(), CreatedOnUtc = T0, ExceptionDetails = Fault("x"), ExceptionLoaded = true };
            var findings = Run(new IncompleteEvidenceRule(), new[] { bare, Trace(correlation: Guid.NewGuid(), request: Guid.NewGuid()) });
            var f = findings.Single();
            Assert.Equal(Severity.Warning, f.Severity);   // the only failure has no correlation ID
            Assert.True(f.Facts.Any(x => x.Contains("correlationid")));
            Assert.Equal(1, f.TraceIds.Count);
        }

        [Test]
        public static void Incomplete_evidence_reports_trace_setting_and_result_limit()
        {
            var traces = new[] { Trace(correlation: Guid.NewGuid(), request: Guid.NewGuid()) };
            var findings = Run(new IncompleteEvidenceRule(), traces, tweak: r =>
            {
                r.Summary.Organization.TraceSetting = "Exception";
                r.Summary.Query.LimitReached = true;
            });
            Assert.Equal(2, findings.Count);
            Assert.True(findings.Any(f => f.Title.Contains("set to Exception")));
            Assert.True(findings.Any(f => f.Title.Contains("result limit")));
        }

        [Test]
        public static void Chain_rule_reports_failures_from_several_plugins_in_one_chain()
        {
            var chain = Guid.NewGuid();
            var parent = Timed(0, 1000, 1, chain, AccountPlugin, "Create", exception: Fault("Parent failed"));
            var child = Timed(100, 300, 2, chain, ContactPlugin, "Update", "contact", exception: Fault("Child failed"));
            var f = Run(new CorrelatedFailureChainRule(), new[] { parent, child }).Single();
            Assert.Equal(Confidence.Medium, f.Confidence);
            Assert.True(f.Correlations.Any(c => c.StartsWith("[Same causal chain")));
            Assert.Contains("AccountPreCreate (depth 1) → ContactSync (depth 2)", f.Title);
            Assert.True(f.Correlations.Any(c => c.StartsWith("[Suspected parent/child]")));
            Assert.True(f.Correlations.Any(c => c.Contains("not that they are the same immediate operation")));
        }

        [Test]
        public static void Chains_with_the_same_failure_shape_are_reported_once()
        {
            var traces = Enumerable.Range(0, 4).Select(i =>
            {
                var chain = Guid.NewGuid();
                return new[]
                {
                    Timed(i * 10000, 1000, 1, chain, AccountPlugin, "Create", exception: Fault("Parent failed")),
                    Timed(i * 10000 + 100, 300, 2, chain, ContactPlugin, "Update", "contact", exception: Fault("Child failed")),
                };
            }).SelectMany(x => x).ToList();
            var f = Run(new CorrelatedFailureChainRule(), traces).Single();
            Assert.Contains("4 causal chains", f.Title);
            Assert.Equal(8, f.TraceIds.Count);
        }

        [Test]
        public static void Hotspot_is_skipped_for_a_single_recurring_failure()
        {
            var traces = Repeat(5, i => Trace(minutes: i, exception: Fault("same")));
            Assert.Equal(0, Run(new MessageEntityHotspotRule(), traces).Count);
        }

        [Test]
        public static void Recursion_rule_needs_three_depths()
        {
            var chain = Guid.NewGuid();
            var two = new[] { Timed(0, 100, 1, chain), Timed(10, 50, 2, chain) };
            Assert.Equal(0, Run(new SuspectedRecursionRule(), two).Count);
            var three = two.Concat(new[] { Timed(20, 20, 3, chain) }).ToArray();
            var f = Run(new SuspectedRecursionRule(), three).Single();
            Assert.Equal(Confidence.Low, f.Confidence);
            Assert.True(f.PossibleExplanations.Count >= 2, "offers the legitimate alternative too");
        }

        [Test]
        public static void Trend_rules_produce_nothing_without_a_valid_baseline()
        {
            var traces = Repeat(5, i => Trace(minutes: i, exception: Fault("x")));
            Assert.Equal(0, Run(new IncreasingFailureRule(), traces).Count);
            Assert.Equal(0, Run(new PerformanceRegressionRule(), traces).Count);
        }

        [Test]
        public static void Supporting_trace_ids_are_capped()
        {
            var f = Run(new RecurringFailureRule(), Repeat(40, i => Trace(minutes: i, exception: Fault("x")))).Single();
            Assert.Equal(FindingHelper.MaxTraceIds, f.TraceIds.Count);
            Assert.Equal(15, f.AdditionalTraceCount);
        }

        [Test]
        public static void Findings_never_state_a_root_cause()
        {
            var chain = Guid.NewGuid();
            var traces = Repeat(40, i => Trace(minutes: i * 0.5, duration: 120000, exception: Fault("Object reference not set", "-2147220956"), correlation: chain, depth: 1 + i % 4));
            var result = Analyze(traces);
            Assert.True(result.Findings.Count >= 4);
            foreach (var f in result.Findings)
                foreach (var text in new[] { f.Title, f.Description }.Concat(f.Facts).Concat(f.Metrics).Concat(f.PossibleExplanations))
                    Assert.False(text.IndexOf("root cause is", StringComparison.OrdinalIgnoreCase) >= 0, "asserted root cause: " + text);
        }
    }

    public static class DiagnosticServiceTests
    {
        private sealed class ThrowingRule : IDiagnosticRule
        {
            public string RuleId => "TEST01";
            public string Name => "Throws";
            public string Description => "";
            public IEnumerable<DiagnosticFinding> Evaluate(AnalysisContext context) => throw new InvalidOperationException("boom");
        }

        [Test]
        public static void A_failing_rule_is_isolated_and_reported()
        {
            var engine = new AnalysisEngine(new AnalysisOptions(), new IDiagnosticRule[] { new ThrowingRule(), new SlowExecutionRule() });
            var result = engine.Analyze(new[] { Trace(duration: 5000) }, Query(), Org(), null, T0);
            Assert.Equal(2, result.Findings.Count);
            Assert.True(result.Findings.Any(f => f.RuleId == "TEST01" && f.Facts.Single().Contains("boom")));
            Assert.True(result.Findings.Any(f => f.RuleId == "PFC003"));
        }

        [Test]
        public static void Findings_are_ordered_by_severity_and_numbered()
        {
            var traces = Repeat(3, i => Trace(minutes: i, exception: Fault("x"))).Concat(new[] { Trace(duration: 3000) }).ToList();
            var findings = Analyze(traces).Findings;
            Assert.Equal("F-001", findings[0].Id);
            for (var i = 1; i < findings.Count; i++)
            {
                Assert.True(findings[i - 1].Severity >= findings[i].Severity, "ordered by severity");
                Assert.Equal("F-" + (i + 1).ToString("000"), findings[i].Id);
            }
        }

        [Test]
        public static void Duplicate_rule_ids_are_rejected()
        {
            Check.Throws<ArgumentException>(() => new DiagnosticAnalysisService(new IDiagnosticRule[] { new SlowExecutionRule(), new SlowExecutionRule() }));
        }

        [Test]
        public static void Default_rules_have_unique_ids_and_descriptions()
        {
            var rules = DiagnosticAnalysisService.DefaultRules().ToList();
            Assert.Equal(9, rules.Count);
            Assert.Equal(rules.Count, rules.Select(r => r.RuleId).Distinct().Count());
            Assert.True(rules.All(r => !string.IsNullOrWhiteSpace(r.Description)));
        }
    }

    public static class EngineTests
    {
        [Test]
        public static void Empty_input_produces_an_empty_but_valid_result()
        {
            var r = Analyze(new PluginTraceRecord[0]);
            Assert.True(r.IsEmpty);
            Assert.Equal(0, r.Findings.Count);
            Assert.Equal(0, r.Summary.TotalTraces);
            Assert.False(r.Trend.Available);
        }

        [Test]
        public static void Records_with_every_field_missing_are_analyzed_safely()
        {
            var bare = new PluginTraceRecord { Id = Guid.NewGuid() };
            var failedBare = new PluginTraceRecord { Id = Guid.NewGuid(), ExceptionDetails = "boom", ExceptionLoaded = true };
            var r = Analyze(new[] { bare, failedBare, null });
            Assert.Equal(2, r.Summary.TotalTraces);
            Assert.Equal(1, r.Summary.TracesWithException);
            Assert.Equal(2, r.Summary.IncompleteEvidence);
            Assert.Equal("boom", r.FailureGroups.Single().Fingerprint.Message);
            Assert.Equal("(unknown exception type)", r.FailureGroups.Single().Fingerprint.ExceptionType);
            Assert.True(r.Findings.Any(f => f.RuleId == "PFC005"));
        }

        [Test]
        public static void Duplicate_records_from_paging_edges_are_counted_once()
        {
            var t = Trace(exception: Fault("x"));
            var r = Analyze(new[] { t, t });
            Assert.Equal(1, r.Summary.TotalTraces);
        }

        [Test]
        public static void Summary_counts_are_consistent()
        {
            var traces = Repeat(3, i => Trace(minutes: i, exception: Fault("x"), correlation: Guid.NewGuid(), request: Guid.NewGuid()))
                .Concat(new[] { Trace(duration: 5000, correlation: Guid.NewGuid(), request: Guid.NewGuid()), Trace(duration: null) }).ToList();
            var s = Analyze(traces).Summary;
            Assert.Equal(5, s.TotalTraces);
            Assert.Equal(3, s.TracesWithException);
            Assert.Equal(1, s.UniqueFailureGroups);
            Assert.Equal(1, s.RecurringFailureGroups);
            Assert.Equal(1, s.SlowExecutions);
            Assert.Equal(4, s.MeasuredExecutions);
            Assert.Equal(4, s.CompleteEvidence);
            Assert.Equal(1, s.MissingIdentifierCounts["performanceexecutionduration"]);
        }
    }
}
