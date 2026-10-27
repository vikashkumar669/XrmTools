using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PluginFailureCorrelationExplorer.Analysis
{
    /// <summary>Culture-invariant formatting shared by findings, the UI and reports.</summary>
    public static class Fmt
    {
        private static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public static string Utc(DateTime value) => value == DateTime.MinValue ? "" : value.ToString("yyyy-MM-dd HH:mm:ss", C) + " UTC";

        public static string UtcIso(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ssZ", C);

        public static string Ms(double? value) => value.HasValue ? Math.Round(value.Value).ToString("N0", C) + " ms" : "n/a";

        public static string Ms(int? value) => value.HasValue ? value.Value.ToString("N0", C) + " ms" : "n/a";

        public static string Count(int value) => value.ToString("N0", C);

        public static string Rate(double perHour) => perHour.ToString(perHour >= 10 ? "N0" : "N1", C) + "/h";

        public static string Percent(int part, int whole) => whole <= 0 ? "n/a" : (100.0 * part / whole).ToString("N0", C) + "%";

        public static string Ratio(double value) => value.ToString("N1", C) + "×";

        public static string List(IEnumerable<string> values, int max = 5)
        {
            var list = (values ?? Enumerable.Empty<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (list.Count == 0) return "(none)";
            return list.Count <= max ? string.Join(", ", list) : string.Join(", ", list.Take(max)) + $" and {list.Count - max} more";
        }

        public static string Plural(int n, string singular, string plural = null) =>
            Count(n) + " " + (n == 1 ? singular : plural ?? singular + "s");

        /// <summary>Shortens text for grids, keeping it on one line.</summary>
        public static string OneLine(string text, int max = 160)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var t = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length <= max ? t : t.Substring(0, max - 1) + "…";
        }
    }
}
