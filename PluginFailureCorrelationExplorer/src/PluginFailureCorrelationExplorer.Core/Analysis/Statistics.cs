using System;
using System.Collections.Generic;
using System.Linq;

namespace PluginFailureCorrelationExplorer.Analysis
{
    /// <summary>Deterministic descriptive statistics used by the performance analysis.</summary>
    public static class Statistics
    {
        /// <summary>Arithmetic mean; null for an empty sample.</summary>
        public static double? Average(IReadOnlyCollection<int> values) =>
            values == null || values.Count == 0 ? (double?)null : values.Average(v => (double)v);

        /// <summary>Median (mean of the two middle values for an even count); null for an empty sample.</summary>
        public static double? Median(IEnumerable<int> values)
        {
            var sorted = values?.OrderBy(v => v).ToList();
            if (sorted == null || sorted.Count == 0) return null;
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + (double)sorted[mid]) / 2.0;
        }

        /// <summary>
        /// Nearest-rank percentile: the smallest value such that at least <paramref name="percentile"/>% of the sample is
        /// less than or equal to it (rank = ceil(p/100 × n)). Always returns an observed value. Null for an empty sample.
        /// </summary>
        public static double? PercentileNearestRank(IEnumerable<int> values, double percentile)
        {
            if (percentile <= 0 || percentile > 100) throw new ArgumentOutOfRangeException(nameof(percentile));
            var sorted = values?.OrderBy(v => v).ToList();
            if (sorted == null || sorted.Count == 0) return null;
            var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count);
            rank = Math.Max(1, Math.Min(sorted.Count, rank));
            return sorted[rank - 1];
        }
    }
}
