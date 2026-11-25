using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace ChartDesignerPro.Xml
{
    /// <summary>One &lt;attribute&gt; inside the chart's FetchXML.</summary>
    public sealed class FetchAttribute
    {
        public string Name { get; set; }
        public string Alias { get; set; }

        /// <summary>count, countcolumn, sum, avg, min or max (as written), or null.</summary>
        public string Aggregate { get; set; }
        public bool GroupBy { get; set; }

        /// <summary>day, week, month, quarter, year, fiscal-period or fiscal-year (as written), or null.</summary>
        public string DateGrouping { get; set; }

        /// <summary>Alias (or name) of the link-entity the attribute sits in; null for the primary table.</summary>
        public string LinkEntity { get; set; }

        /// <summary>Logical name of the table the attribute belongs to.</summary>
        public string Table { get; set; }

        public int Line { get; set; }
    }

    public sealed class FetchLink
    {
        public string Name { get; set; }
        public string Alias { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public string LinkType { get; set; }
        public int Line { get; set; }
    }

    public sealed class FetchCondition
    {
        public string Attribute { get; set; }
        public string Operator { get; set; }
        public IReadOnlyList<string> Values { get; set; } = new string[0];

        /// <summary>"and"/"or" of the filter that directly contains the condition.</summary>
        public string FilterType { get; set; }
        public string LinkEntity { get; set; }
        public int Depth { get; set; }
        public int Line { get; set; }
    }

    public sealed class FetchOrder
    {
        public string Attribute { get; set; }
        public string Alias { get; set; }
        public bool Descending { get; set; }
    }

    /// <summary>Read-only projection of one &lt;fetch&gt;.</summary>
    public sealed class FetchModel
    {
        public string EntityName { get; set; }
        public bool IsAggregate { get; set; }
        public int? Count { get; set; }
        public List<FetchAttribute> Attributes { get; } = new List<FetchAttribute>();
        public List<FetchLink> Links { get; } = new List<FetchLink>();
        public List<FetchCondition> Conditions { get; } = new List<FetchCondition>();
        public List<FetchOrder> Orders { get; } = new List<FetchOrder>();
        public int Line { get; set; }

        public IEnumerable<FetchAttribute> GroupBys => Attributes.Where(a => a.GroupBy);
        public IEnumerable<FetchAttribute> Aggregates => Attributes.Where(a => !a.GroupBy && !string.IsNullOrEmpty(a.Aggregate));

        public FetchAttribute ByAlias(string alias) =>
            string.IsNullOrEmpty(alias) ? null : Attributes.FirstOrDefault(a => string.Equals(a.Alias, alias, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A &lt;category&gt; and the measure aliases of each &lt;measurecollection&gt; in it.</summary>
    public sealed class CategoryModel
    {
        public string Alias { get; set; }
        public List<List<string>> MeasureCollections { get; } = new List<List<string>>();
        public int Line { get; set; }
    }

    /// <summary>Read-only projection of the data description (datadescription).</summary>
    public sealed class DataDescriptionModel
    {
        public List<FetchModel> Fetches { get; } = new List<FetchModel>();
        public List<CategoryModel> Categories { get; } = new List<CategoryModel>();
        public FetchModel Fetch => Fetches.FirstOrDefault();
        public string RootName { get; set; }
    }

    /// <summary>One &lt;Series&gt; of the presentation description.</summary>
    public sealed class SeriesModel
    {
        public string ChartType { get; set; }
        public string Name { get; set; }
        public string Color { get; set; }
        public string LabelFormat { get; set; }
        public bool? ShowValueAsLabel { get; set; }
        public string YAxisType { get; set; }
        public string CustomProperties { get; set; }
        public int Line { get; set; }
    }

    /// <summary>Read-only projection of the presentation description (presentationdescription).</summary>
    public sealed class PresentationModel
    {
        public List<SeriesModel> Series { get; } = new List<SeriesModel>();
        public List<string> Titles { get; } = new List<string>();
        public bool? LegendEnabled { get; set; }
        public bool? CustomColorOverride { get; set; }
        public string Palette { get; set; }
        public string RootName { get; set; }
    }

    /// <summary>
    /// Builds read-only projections of chart XML for display and validation. Never used to write XML back:
    /// the stored text stays the source of truth (Step 2 architecture, section 7).
    /// </summary>
    public static class ChartDefinitionParser
    {
        public static DataDescriptionModel ParseData(XDocument doc)
        {
            var model = new DataDescriptionModel { RootName = doc?.Root?.Name.LocalName };
            if (doc?.Root == null) return model;

            foreach (var fetch in doc.Root.Descendants().Where(e => Is(e, "fetch")))
                model.Fetches.Add(ParseFetch(fetch));

            foreach (var category in doc.Root.Descendants().Where(e => Is(e, "category")))
            {
                var c = new CategoryModel { Alias = Attr(category, "alias"), Line = LineOf(category) };
                foreach (var collection in category.Elements().Where(e => Is(e, "measurecollection")))
                    c.MeasureCollections.Add(collection.Elements().Where(e => Is(e, "measure")).Select(m => Attr(m, "alias")).ToList());
                model.Categories.Add(c);
            }
            return model;
        }

        private static FetchModel ParseFetch(XElement fetch)
        {
            var model = new FetchModel
            {
                IsAggregate = string.Equals(Attr(fetch, "aggregate"), "true", StringComparison.OrdinalIgnoreCase),
                Count = int.TryParse(Attr(fetch, "count") ?? Attr(fetch, "top"), out var n) ? n : (int?)null,
                Line = LineOf(fetch)
            };
            var entity = fetch.Elements().FirstOrDefault(e => Is(e, "entity"));
            if (entity == null) return model;
            model.EntityName = Attr(entity, "name");
            Walk(entity, model, null, model.EntityName);
            return model;
        }

        private static void Walk(XElement container, FetchModel model, string link, string table)
        {
            foreach (var e in container.Elements())
            {
                if (Is(e, "attribute"))
                {
                    model.Attributes.Add(new FetchAttribute
                    {
                        Name = Attr(e, "name"),
                        Alias = Attr(e, "alias"),
                        Aggregate = Attr(e, "aggregate"),
                        GroupBy = string.Equals(Attr(e, "groupby"), "true", StringComparison.OrdinalIgnoreCase),
                        DateGrouping = Attr(e, "dategrouping"),
                        LinkEntity = link,
                        Table = table,
                        Line = LineOf(e)
                    });
                }
                else if (Is(e, "order"))
                {
                    model.Orders.Add(new FetchOrder
                    {
                        Attribute = Attr(e, "attribute"),
                        Alias = Attr(e, "alias"),
                        Descending = string.Equals(Attr(e, "descending"), "true", StringComparison.OrdinalIgnoreCase)
                    });
                }
                else if (Is(e, "filter"))
                {
                    WalkFilter(e, model, link, 0);
                }
                else if (Is(e, "link-entity"))
                {
                    var l = new FetchLink
                    {
                        Name = Attr(e, "name"),
                        Alias = Attr(e, "alias"),
                        From = Attr(e, "from"),
                        To = Attr(e, "to"),
                        LinkType = Attr(e, "link-type"),
                        Line = LineOf(e)
                    };
                    model.Links.Add(l);
                    Walk(e, model, l.Alias ?? l.Name, l.Name);
                }
            }
        }

        private static void WalkFilter(XElement filter, FetchModel model, string link, int depth)
        {
            var type = Attr(filter, "type") ?? "and";
            foreach (var e in filter.Elements())
            {
                if (Is(e, "condition"))
                {
                    var values = e.Elements().Where(v => Is(v, "value")).Select(v => v.Value).ToList();
                    var single = Attr(e, "value");
                    if (single != null) values.Insert(0, single);
                    model.Conditions.Add(new FetchCondition
                    {
                        Attribute = Attr(e, "attribute"),
                        Operator = Attr(e, "operator"),
                        Values = values,
                        FilterType = type,
                        LinkEntity = Attr(e, "entityname") ?? link,
                        Depth = depth,
                        Line = LineOf(e)
                    });
                }
                else if (Is(e, "filter"))
                {
                    WalkFilter(e, model, link, depth + 1);
                }
            }
        }

        public static PresentationModel ParsePresentation(XDocument doc)
        {
            var model = new PresentationModel { RootName = doc?.Root?.Name.LocalName };
            if (doc?.Root == null) return model;
            var root = doc.Root;
            model.Palette = Attr(root, "Palette");
            model.CustomColorOverride = Bool(Attr(root, "CustomColorOverride"));

            // <Chart><Series><Series .../></Series></Chart>: the inner elements are the series.
            var seriesContainer = root.Elements().FirstOrDefault(e => Is(e, "Series"));
            if (seriesContainer != null)
            {
                foreach (var s in seriesContainer.Elements().Where(e => Is(e, "Series")))
                {
                    model.Series.Add(new SeriesModel
                    {
                        ChartType = Attr(s, "ChartType"),
                        Name = Attr(s, "Name"),
                        Color = Attr(s, "Color"),
                        LabelFormat = Attr(s, "LabelFormat"),
                        ShowValueAsLabel = Bool(Attr(s, "IsValueShownAsLabel")),
                        YAxisType = Attr(s, "YAxisType"),
                        CustomProperties = Attr(s, "CustomProperties"),
                        Line = LineOf(s)
                    });
                }
            }

            foreach (var t in root.Descendants().Where(e => Is(e, "Title")))
            {
                var text = Attr(t, "Text");
                if (string.IsNullOrWhiteSpace(text)) text = t.Value;
                if (!string.IsNullOrWhiteSpace(text)) model.Titles.Add(text.Trim());
            }

            var legend = root.Descendants().FirstOrDefault(e => Is(e, "Legend"));
            if (legend != null) model.LegendEnabled = Bool(Attr(legend, "Enabled")) ?? true;
            return model;
        }

        private static bool Is(XElement e, string name) => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

        private static string Attr(XElement e, string name) =>
            e.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

        private static bool? Bool(string value) => bool.TryParse(value, out var b) ? b : (bool?)null;

        private static int LineOf(XObject o) => o is IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : 0;
    }
}
