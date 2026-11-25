using System;
using System.Collections.Generic;
using System.Linq;

namespace ChartDesignerPro.Validation
{
    /// <summary>
    /// What Unified Interface (which renders charts with Highcharts) supports in the presentation XML, per Microsoft's
    /// documentation. Kept as data so it can be reviewed against the docs in one place.
    /// Source: https://learn.microsoft.com/power-apps/developer/model-driven-apps/understand-charts-underlying-data-chart-representation
    /// (section "Supported methods and properties in Unified Interface"). Last reviewed: 2026-11-25.
    /// </summary>
    public static class UciCapabilities
    {
        public const string DocumentationUrl = "https://learn.microsoft.com/power-apps/developer/model-driven-apps/understand-charts-underlying-data-chart-representation";
        public const string LastReviewed = "2026-11-25";

        /// <summary>Documented maximum number of series in a chart with one category.</summary>
        public const int MaxSeries = 5;

        /// <summary>Documented chart import limit: the chart file, base64-encoded, must be at most 16,000 characters.</summary>
        public const int ImportBase64Limit = 16000;

        private static readonly HashSet<string> LabelStyle = Set("Enabled", "ForeColor", "Format");
        private static readonly HashSet<string> GridOrTick = Set("Enabled", "LineColor");

        /// <summary>Element name → attributes Unified Interface honours. Elements not listed are not documented for Unified Interface.</summary>
        public static readonly IReadOnlyDictionary<string, HashSet<string>> SupportedAttributes =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Chart"] = Set("Palette", "PaletteCustomColors", "CustomColorOverride"),
                ["ChartArea"] = Set("BackColor", "BackSecondaryColor", "BackGradientStyle"),
                ["Area3DStyle"] = Set("Enable3D"),
                ["AxisX"] = Set("Enabled", "LineColor", "IsReversed", "Title", "TitleForeColor"),
                ["AxisY"] = Set("Enabled", "LineColor", "Interval", "Minimum", "Maximum", "Title", "TitleForeColor"),
                ["AxisY2"] = Set("Enabled", "LineColor", "Interval", "Minimum", "Maximum", "Title", "TitleForeColor"),
                ["LabelStyle"] = LabelStyle,
                ["MajorGrid"] = GridOrTick,
                ["MajorTickMark"] = GridOrTick,
                ["Legend"] = Set("Enabled"),
                ["Series"] = Set("ChartType", "Color", "BorderColor", "BorderWidth", "IsValueShownAsLabel", "IsVisibleInLegend",
                    "LabelForeColor", "LabelFormat", "LegendText", "YAxisType", "CustomProperties"),
            };

        /// <summary>Elements that only group other elements (no attributes of their own are expected).</summary>
        public static readonly HashSet<string> Containers = Set("Series", "ChartAreas", "Legends", "Titles");

        /// <summary>Chart types documented for model-driven charts. 3D variants are made with Area3DStyle Enable3D.</summary>
        public static readonly HashSet<string> ChartTypes = Set(
            "Column", "StackedColumn", "StackedColumn100", "Bar", "StackedBar", "StackedBar100",
            "Area", "StackedArea", "StackedArea100", "Line", "Pie", "Funnel", "Tag", "Doughnut", "Point");

        /// <summary>Chart types whose series colour is ignored in Unified Interface.</summary>
        public static readonly HashSet<string> SeriesColorIgnored = Set("Pie", "Funnel", "Doughnut");

        public static bool IsAttributeSupported(string element, string attribute) =>
            SupportedAttributes.TryGetValue(element, out var attrs) && attrs.Contains(attribute);

        private static HashSet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Documented colour formats: "r,g,b", "a,r,g,b", "#RRGGBB" and named browser colours (letters only, e.g. "SteelBlue").
        /// Spaces after commas are allowed, as Microsoft's own charts use them.
        /// </summary>
        public static bool IsValidColor(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            if (v.StartsWith("#", StringComparison.Ordinal))
                return v.Length == 7 && v.Skip(1).All(Uri.IsHexDigit);
            if (v.All(char.IsLetter)) return true;
            var parts = v.Split(',');
            if (parts.Length != 3 && parts.Length != 4) return false;
            return parts.All(p => int.TryParse(p.Trim(), out var n) && n >= 0 && n <= 255);
        }
    }
}
