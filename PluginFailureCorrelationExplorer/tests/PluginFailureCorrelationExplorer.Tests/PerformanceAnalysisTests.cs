using System;
using System.Linq;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;
using static PluginFailureCorrelationExplorer.Tests.Samples;

namespace PluginFailureCorrelationExplorer.Tests
{
    public static class StatisticsTests
    {
        [Test]
        public static void Median_of_odd_and_even_samples()
        {
            Check.Close(3, Statistics.Median(new[] { 5, 1, 3 }));
            Check.Close(2.5, Statistics.Median(new[] { 4, 1, 3, 2 }));
            Check.Close(7, Statistics.Median(new[] { 7 }));
            Check.Null(Statistics.Median(new int[0]));
        }

        [Test]
        public static void Nearest_rank_percentile_returns_observed_values()
        {
            var oneToTwenty = Enumerable.Range(1, 20).ToArray();
            Check.Close(19, Statistics.PercentileNearestRank(oneToTwenty, 95));   // ceil(0.95 × 20) = 19
            Check.Close(95, Statistics.PercentileNearestRank(Enumerable.Range(1, 100), 95));
            Check.Close(10, Statistics.PercentileNearestRank(Enumerable.Range(1, 10), 95)); // ceil(9.5) = 10
            Check.Close(42, Statistics.PercentileNearestRank(new[] { 42 }, 95));
            Check.Close(1, Statistics.PercentileNearestRank(oneToTwenty, 1));
            Check.Close(20, Statistics.PercentileNearestRank(oneToTwenty, 100));
            Check.Null(Statistics.PercentileNearestRank(new int[0], 95));
        }

        [Test]
        public static void Percentile_outside_range_throws()
        {
            Check.Throws<ArgumentOutOfRangeException>(() => Statistics.PercentileNearestRank(new[] { 1 }, 0));
            Check.Throws<ArgumentOutOfRangeException>(() => Statistics.PercentileNearestRank(new[] { 1 }, 101));
        }

        [Test]
        public static void Average_of_empty_is_null()
        {
            Check.Null(Statistics.Average(new int[0]));
            Check.Close(2, Statistics.Average(new[] { 1, 2, 3 }));
        }
    }

    public static class PerformanceAnalysisTests
    {
        private static PerformanceAnalysisService Service(AnalysisOptions o = null) => new PerformanceAnalysisService(o ?? new AnalysisOptions());

        [Test]
        public static void Threshold_is_inclusive_and_depends_on_mode()
        {
            var o = new AnalysisOptions { SlowSyncThresholdMs = 2000, SlowAsyncThresholdMs = 10000 };
            var s = Service(o);
            Assert.True(s.IsSlow(Trace(duration: 2000, mode: 0)), "sync at threshold");
            Assert.False(s.IsSlow(Trace(duration: 1999, mode: 0)), "sync below threshold");
            Assert.False(s.IsSlow(Trace(duration: 5000, mode: 1)), "async below async threshold");
            Assert.True(s.IsSlow(Trace(duration: 10000, mode: 1)), "async at threshold");
        }

        [Test]
        public static void Unknown_mode_or_missing_duration_is_never_slow()
        {
            var s = Service();
            Assert.False(s.IsSlow(Trace(duration: 999999, mode: null)));
            Assert.False(s.IsSlow(Trace(duration: null, mode: 0)));
            Assert.False(s.IsSlow(null));
        }

        [Test]
        public static void Near_timeout_only_applies_to_synchronous_executions()
        {
            var s = Service(new AnalysisOptions { NearTimeoutMs = 100000 });
            Assert.True(s.IsNearTimeout(Trace(duration: 110000, mode: 0)));
            Assert.False(s.IsNearTimeout(Trace(duration: 110000, mode: 1)));
        }

        [Test]
        public static void Groups_by_type_message_and_mode_with_statistics()
        {
            var traces = Repeat(20, i => Trace(duration: (i + 1) * 100, minutes: i));            // 100..2000 ms sync
            traces.AddRange(Repeat(3, i => Trace(duration: 50000, mode: 1, minutes: i)));      // async group, small sample
            var groups = Service().Analyze(traces);
            Assert.Equal(2, groups.Count);
            var sync = groups.Single(g => g.Mode == 0);
            Assert.Equal(20, sync.Measured);
            Check.Close(1050, sync.AverageMs);
            Check.Close(1050, sync.MedianMs);
            Check.Close(1900, sync.P95Ms);
            Assert.Equal(2000, sync.MaxMs);
            Assert.Equal(1, sync.SlowCount);                                                    // only 2000 ms reaches 2000
            Assert.Equal(2000, sync.SlowTraces[0].ExecutionDurationMs);

            var async = groups.Single(g => g.Mode == 1);
            Check.Null(async.MedianMs, "sample of 3 is too small");
            Check.Null(async.P95Ms);
            Assert.Equal(3, async.SlowCount);
            Assert.Contains("below 5", async.SampleNote);
        }

        [Test]
        public static void Percentile_needs_its_own_minimum_sample()
        {
            var groups = Service().Analyze(Repeat(10, i => Trace(duration: 100 + i)));
            Check.NotNull(groups[0].MedianMs);
            Check.Null(groups[0].P95Ms);
            Assert.Contains("95th percentile not reported", groups[0].SampleNote);
        }

        [Test]
        public static void Executions_without_duration_are_counted_but_not_measured()
        {
            var traces = Repeat(6, i => Trace(duration: i < 2 ? (int?)null : 500));
            var g = Service().Analyze(traces).Single();
            Assert.Equal(6, g.Executions);
            Assert.Equal(4, g.Measured);
            Check.Null(g.MedianMs);
        }

        [Test]
        public static void Empty_input_gives_no_groups()
        {
            Assert.Equal(0, Service().Analyze(new PluginTraceRecord[0]).Count);
            Assert.Equal(0, Service().Analyze(null).Count);
        }
    }
}
