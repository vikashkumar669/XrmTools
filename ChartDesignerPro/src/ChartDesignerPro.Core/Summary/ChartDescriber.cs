using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartDesignerPro.Models;
using ChartDesignerPro.Xml;

namespace ChartDesignerPro.Summary
{
    /// <summary>One labelled fact about a chart, for the Summary tab.</summary>
    public sealed class ChartFact
    {
        public ChartFact(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public string Value { get; }
    }

    /// <summary>A plain-language explanation of what a chart shows.</summary>
    public sealed class ChartSummary
    {
        /// <summary>One sentence, e.g. "Sums Est. Revenue of Opportunities by Sales Stage, shown as a column chart."</summary>
        public string Sentence { get; set; }
        public List<ChartFact> Facts { get; } = new List<ChartFact>();

        /// <summary>Things the reader should know, e.g. a measure that couldn't be matched to the query.</summary>
        public List<string> Notes { get; } = new List<string>();

        /// <summary>True when the summary was built without table metadata, so logical names are shown.</summary>
        public bool UsedLogicalNames { get; set; }
    }

    /// <summary>
    /// Rule-based explanation of a chart (the start of the Chart Intelligence Advisor in the brief, section 10).
    /// Deterministic: it only states what the XML says. Display names come from metadata when available.
    /// </summary>
    public static class ChartDescriber
    {
        public static ChartSummary Describe(DataDescriptionModel data, PresentationModel presentation,
            TableMetadata table = null, Func<string, TableMetadata> linkedTable = null)
        {
            var summary = new ChartSummary { UsedLogicalNames = table == null };
            var fetch = data?.Fetch;
            if (fetch == null || string.IsNullOrEmpty(fetch.EntityName))
            {
                summary.Sentence = "The data description has no FetchXML query, so what the chart shows can't be described.";
                return summary;
            }

            string Column(FetchAttribute a)
            {
                if (a == null) return "?";
                var meta = a.LinkEntity == null ? table : linkedTable?.Invoke(a.Table);
                var display = meta != null && a.Name != null && meta.Columns.TryGetValue(a.Name, out var c) ? c.DisplayName : a.Name;
                if (a.LinkEntity == null) return display;
                var tableName = meta?.DisplayName ?? a.Table;
                return display + " (" + tableName + ")";
            }

            var plural = !string.IsNullOrEmpty(table?.DisplayCollectionName) ? table.DisplayCollectionName
                : !string.IsNullOrEmpty(table?.DisplayName) ? table.DisplayName + " records"
                : fetch.EntityName + " records";

            // Measures: the first category's first measure collection (the series), mapped to aggregate attributes.
            var measureAliases = data.Categories.SelectMany(c => c.MeasureCollections).SelectMany(m => m).Where(a => a != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var measures = new List<string>();
            foreach (var alias in measureAliases)
            {
                var attribute = fetch.ByAlias(alias);
                if (attribute == null)
                {
                    summary.Notes.Add($"The measure alias '{alias}' doesn't match any attribute alias in the query.");
                    continue;
                }
                measures.Add(DescribeMeasure(attribute, Column(attribute), plural));
            }
            if (measures.Count == 0)
            {
                foreach (var a in fetch.Aggregates) measures.Add(DescribeMeasure(a, Column(a), plural));
            }

            var groups = fetch.GroupBys.Select(g => DescribeGroup(g, Column(g))).ToList();

            var chartTypes = (presentation?.Series ?? new List<SeriesModel>())
                .Select(s => s.ChartType).Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var sentence = measures.Count == 0
                ? $"Shows {plural}"
                : Capitalise(JoinAnd(measures));
            if (groups.Count > 0) sentence += " by " + JoinAnd(groups);
            if (chartTypes.Count > 0) sentence += ", shown as " + Article(ChartTypeText(chartTypes[0])) + (chartTypes.Count > 1 ? " combined with " + JoinAnd(chartTypes.Skip(1).Select(ChartTypeText)) : "");
            if (fetch.Count.HasValue && fetch.Count.Value > 0)
            {
                var order = fetch.Orders.FirstOrDefault();
                sentence += $", limited to the {(order != null && !order.Descending ? "bottom" : "top")} {fetch.Count.Value}";
            }
            summary.Sentence = sentence + ".";

            // Facts
            summary.Facts.Add(new ChartFact("Table", table == null ? fetch.EntityName : $"{table.DisplayName} ({fetch.EntityName})"));
            if (groups.Count > 0) summary.Facts.Add(new ChartFact(groups.Count > 1 ? "Categories" : "Category", string.Join("; ", groups)));
            for (var i = 0; i < measures.Count; i++)
                summary.Facts.Add(new ChartFact(measures.Count > 1 ? $"Measure {i + 1}" : "Measure", measures[i]));
            if (chartTypes.Count > 0) summary.Facts.Add(new ChartFact("Chart type", string.Join(", ", chartTypes)));
            if (presentation != null && presentation.Series.Count > 0) summary.Facts.Add(new ChartFact("Series", presentation.Series.Count.ToString(CultureInfo.InvariantCulture)));
            if (fetch.Count.HasValue) summary.Facts.Add(new ChartFact("Record limit (Top/Bottom N)", fetch.Count.Value.ToString(CultureInfo.InvariantCulture)));
            if (fetch.Links.Count > 0)
                summary.Facts.Add(new ChartFact("Related tables", string.Join(", ", fetch.Links.Select(l => linkedTable?.Invoke(l.Name)?.DisplayName ?? l.Name).Distinct())));
            if (fetch.Conditions.Count > 0)
                summary.Facts.Add(new ChartFact("Chart filters", string.Join("; ", fetch.Conditions.Select(c => DescribeCondition(c, table)))));
            if (fetch.Orders.Count > 0)
                summary.Facts.Add(new ChartFact("Sort", string.Join(", ", fetch.Orders.Select(o => (o.Alias ?? o.Attribute) + (o.Descending ? " (descending)" : " (ascending)")))));
            if (presentation != null && presentation.Titles.Count > 0) summary.Facts.Add(new ChartFact("Title", string.Join(" / ", presentation.Titles)));
            if (data.Fetches.Count > 1) summary.Notes.Add($"The data description contains {data.Fetches.Count} fetch queries; the summary describes the first one.");
            if (fetch.Conditions.Count == 0) summary.Notes.Add("The chart itself has no filter: in the app it shows the records of the view it is displayed with.");
            if (table == null) summary.Notes.Add("Table metadata isn't loaded, so column logical names are shown.");
            return summary;
        }

        private static string DescribeMeasure(FetchAttribute a, string column, string plural)
        {
            switch ((a.Aggregate ?? string.Empty).ToLowerInvariant())
            {
                case "count": return "counts " + plural;
                case "countcolumn": return $"counts {plural} that have a {column}";
                case "sum": return "sums " + column;
                case "avg": return "averages " + column;
                case "min": return "shows the lowest " + column;
                case "max": return "shows the highest " + column;
                default: return string.IsNullOrEmpty(a.Aggregate) ? "shows " + column : $"applies '{a.Aggregate}' to {column}";
            }
        }

        private static string DescribeGroup(FetchAttribute g, string column)
        {
            if (string.IsNullOrEmpty(g.DateGrouping)) return column;
            switch (g.DateGrouping.ToLowerInvariant())
            {
                case "fiscal-period": return column + " (fiscal period)";
                case "fiscal-year": return column + " (fiscal year)";
                default: return column + " (" + g.DateGrouping.ToLowerInvariant() + ")";
            }
        }

        private static string DescribeCondition(FetchCondition c, TableMetadata table)
        {
            var column = c.Attribute;
            if (c.LinkEntity == null && table != null && c.Attribute != null && table.Columns.TryGetValue(c.Attribute, out var meta)) column = meta.DisplayName;
            if (c.LinkEntity != null) column += " (" + c.LinkEntity + ")";
            var values = c.Values.Count == 0 ? "" : " " + string.Join(", ", c.Values);
            return $"{column} {c.Operator}{values}".Trim();
        }

        public static string ChartTypeText(string chartType)
        {
            if (string.IsNullOrEmpty(chartType)) return "chart";
            switch (chartType.ToLowerInvariant())
            {
                case "column": return "column chart";
                case "stackedcolumn": return "stacked column chart";
                case "stackedcolumn100": return "100% stacked column chart";
                case "bar": return "bar chart";
                case "stackedbar": return "stacked bar chart";
                case "stackedbar100": return "100% stacked bar chart";
                case "line": return "line chart";
                case "area": return "area chart";
                case "stackedarea": return "stacked area chart";
                case "stackedarea100": return "100% stacked area chart";
                case "pie": return "pie chart";
                case "doughnut": return "doughnut chart";
                case "funnel": return "funnel chart";
                case "point": return "point chart";
                default: return chartType + " chart";
            }
        }

        private static string Article(string phrase) =>
            phrase.Length > 0 && "aeiou".IndexOf(char.ToLowerInvariant(phrase[0])) >= 0 ? "an " + phrase : "a " + phrase;

        private static string Capitalise(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string JoinAnd(IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count <= 1) return list.FirstOrDefault() ?? string.Empty;
            return string.Join(", ", list.Take(list.Count - 1)) + " and " + list[list.Count - 1];
        }
    }
}
