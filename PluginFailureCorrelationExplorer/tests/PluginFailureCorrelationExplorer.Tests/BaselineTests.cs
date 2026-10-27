using System;
using System.Linq;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;
using static PluginFailureCorrelationExplorer.Tests.Samples;

namespace PluginFailureCorrelationExplorer.Tests
{
    public static class BaselineTests
    {
        private static readonly AnalysisOptions Options = new AnalysisOptions();
        private static string Signature => new ExceptionFingerprintService(Options).GroupingSignature;

        /// <summary>Current period: T0-1h → T0+23h. Baseline: the 24 hours before.</summary>
        private static TraceQuery BaselineQuery() => Query(T0.AddHours(-25), T0.AddHours(-1));

        private static BaselineSnapshot Baseline(params PluginTraceRecord[] traces)
        {
            var result = Analyze(traces, Options, query: BaselineQuery());
            return BaselineService.CreateSnapshot(result, Signature, BaselineService.SourcePreviousPeriod);
        }

        [Test]
        public static void No_baseline_means_trend_unavailable_with_a_reason()
        {
            var r = Analyze(new[] { Trace(exception: Fault("x")) });
            Assert.False(r.Trend.Available);
            Assert.Contains("No baseline", r.Trend.UnavailableReason);
            Assert.False(r.Findings.Any(f => f.RuleId == "PFC006"), "no trend finding without a baseline");
        }

        [Test]
        public static void Empty_baseline_is_refused_rather_than_read_as_no_failures()
        {
            var r = Analyze(new[] { Trace(exception: Fault("x")) }, Options, Baseline());
            Assert.False(r.Trend.Available);
            Assert.Contains("contains no traces", r.Trend.UnavailableReason);
        }

        [Test]
        public static void Truncated_overlapping_or_differently_filtered_baselines_are_refused()
        {
            var b = Baseline(Trace(minutes: -120));
            b.LimitReached = true;
            Assert.Contains("result limit", BaselineService.Compare(Analyze(new[] { Trace() }), b, Signature, Options).UnavailableReason);

            b = Baseline(Trace(minutes: -120));
            b.ToUtc = T0;   // overlaps the current period
            Assert.Contains("overlaps", BaselineService.Compare(Analyze(new[] { Trace() }), b, Signature, Options).UnavailableReason);

            b = Baseline(Trace(minutes: -120));
            var q = Query();
            q.MessageName = "Update";
            Assert.Contains("different filters", BaselineService.Compare(Analyze(new[] { Trace() }, query: q), b, Signature, Options).UnavailableReason);

            b = Baseline(Trace(minutes: -120));
            Assert.Contains("fingerprint settings", BaselineService.Compare(Analyze(new[] { Trace() }), b, "other", Options).UnavailableReason);
        }

        [Test]
        public static void New_and_increasing_patterns_are_detected_per_hour()
        {
            // Baseline (24 h): 2 × failure A, 1 success. Current (24 h): 6 × A (3×), 3 × new failure B.
            var b = Baseline(
                Trace(minutes: -300, exception: Fault("A failed")), Trace(minutes: -200, exception: Fault("A failed")), Trace(minutes: -100));
            var current = Repeat(6, i => Trace(minutes: i, exception: Fault("A failed")))
                .Concat(Repeat(3, i => Trace(type: ContactPlugin, minutes: i, exception: Fault("B failed")))).ToList();
            var r = Analyze(current, Options, b);

            Assert.True(r.Trend.Available, r.Trend.UnavailableReason);
            var a = r.Trend.Failures.Single(f => f.CurrentCount == 6);
            Assert.Equal(TrendStatus.Increased, a.Status);
            Check.Close(6 / 24.0, a.CurrentPerHour);
            Check.Close(2 / 24.0, a.BaselinePerHour);
            Assert.Equal(TrendStatus.New, r.Trend.Failures.Single(f => f.CurrentCount == 3).Status);

            var findings = r.Findings.Where(f => f.RuleId == "PFC006").ToList();
            Assert.Equal(2, findings.Count);
            Assert.True(findings.All(f => f.Confidence == Confidence.Medium));
        }

        [Test]
        public static void Pattern_that_disappeared_is_not_seen_unless_the_current_retrieval_was_truncated()
        {
            var b = Baseline(Trace(minutes: -100, exception: Fault("Gone")));
            var r = Analyze(new[] { Trace() }, Options, b);
            Assert.Equal(TrendStatus.NotSeen, r.Trend.Failures.Single().Status);

            var q = Query();
            q.LimitReached = true;
            r = Analyze(new[] { Trace() }, Options, b, q);
            Assert.Equal(0, r.Trend.Failures.Count);
            Assert.True(r.Trend.Caveats.Any(c => c.Contains("lower bounds")));
        }

        [Test]
        public static void Performance_regression_needs_enough_samples_in_both_periods()
        {
            var b = Baseline(Repeat(10, i => Trace(minutes: -300 + i, duration: 100)).ToArray());
            var r = Analyze(Repeat(10, i => Trace(minutes: i, duration: 200)), Options, b);
            var t = r.Trend.Performance.Single();
            Assert.True(t.IsRegression);
            Check.Close(2.0, t.Ratio);
            Assert.Equal(1, r.Findings.Count(f => f.RuleId == "PFC007"));

            r = Analyze(Repeat(3, i => Trace(minutes: i, duration: 900)), Options, b);
            Assert.Equal(0, r.Trend.Performance.Count);
        }

        [Test]
        public static void Snapshot_holds_aggregates_only_and_round_trips()
        {
            var secretMessage = "Customer 'Northwind Secret' failed for someone@contoso.example";
            var b = Baseline(Trace(minutes: -100, exception: Fault(secretMessage), correlation: Guid.NewGuid(), block: "trace text"));
            var json = BaselineService.Serialize(new[] { b });
            Assert.DoesNotContain("Northwind", json);
            Assert.DoesNotContain("someone@", json);
            Assert.DoesNotContain("trace text", json);

            var back = BaselineService.Deserialize(json).Single();
            Assert.Equal(b.TotalTraces, back.TotalTraces);
            Assert.Equal(b.Failures.Single().Key, back.Failures.Single().Key);
            Assert.Equal(b.FromUtc, back.FromUtc);
            Assert.Equal(DateTimeKind.Utc, back.FromUtc.Kind);
        }

        [Test]
        public static void Corrupt_snapshot_files_read_as_empty()
        {
            Assert.Equal(0, BaselineService.Deserialize("{not json").Count);
            Assert.Equal(0, BaselineService.Deserialize("").Count);
            Assert.Equal(0, BaselineService.Deserialize(null).Count);
        }
    }
}
