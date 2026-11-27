using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ChartDesignerPro.Models;
using ChartDesignerPro.Xml;

namespace ChartDesignerPro.Validation
{
    /// <summary>
    /// The validation rules. IDs are stable (they appear in reports and settings):
    /// CDP-S structure, CDP-F FetchXML, CDP-M metadata, CDP-U Unified Interface compatibility.
    /// Each rule only reports; nothing is changed automatically.
    /// </summary>
    public static class RuleCatalog
    {
        public static IReadOnlyList<IValidationRule> All { get; } = new IValidationRule[]
        {
            // Structure
            new Rule("CDP-S001", RuleCategory.Structure, "Data XML is well-formed", DataWellFormed),
            new Rule("CDP-S002", RuleCategory.Structure, "Presentation XML is well-formed", PresentationWellFormed),
            new Rule("CDP-S003", RuleCategory.Structure, "Data XML has the required elements", DataStructure),
            new Rule("CDP-S004", RuleCategory.Structure, "Categories have measure collections and measures have aliases", CategoryStructure),
            new Rule("CDP-S005", RuleCategory.Structure, "Every measure alias matches an aggregate in the query", MeasureAliases),
            new Rule("CDP-S006", RuleCategory.Structure, "Measure collections match the presentation series", SeriesCount),
            new Rule("CDP-S007", RuleCategory.Structure, "One FetchXML query", SingleFetch),
            new Rule("CDP-S008", RuleCategory.Structure, "Chart areas match the categories", ChartAreaCount),
            new Rule("CDP-S020", RuleCategory.Structure, "Size is within the chart import limit", ImportSize),
            // FetchXML
            new Rule("CDP-F001", RuleCategory.FetchXml, "The query is an aggregate query", AggregateQuery),
            new Rule("CDP-F002", RuleCategory.FetchXml, "Grouped attributes have aliases", GroupByAliases),
            new Rule("CDP-F003", RuleCategory.FetchXml, "Aggregate functions are valid", AggregateValues),
            new Rule("CDP-F004", RuleCategory.FetchXml, "Date grouping values are valid", DateGroupingValues),
            new Rule("CDP-F005", RuleCategory.FetchXml, "Aliases are unique", UniqueAliases),
            // Metadata
            new Rule("CDP-M001", RuleCategory.Metadata, "The query's table matches the chart's table", TableMatches),
            new Rule("CDP-M002", RuleCategory.Metadata, "Columns exist on their tables", ColumnsExist),
            new Rule("CDP-M003", RuleCategory.Metadata, "Aggregations suit the column types", AggregateTypes),
            new Rule("CDP-M004", RuleCategory.Metadata, "Date grouping is only used on date columns", DateGroupingTypes),
            // Unified Interface
            new Rule("CDP-U001", RuleCategory.UnifiedInterface, "Chart types are supported", ChartTypesSupported),
            new Rule("CDP-U002", RuleCategory.UnifiedInterface, "Presentation properties are honoured by Unified Interface", UciProperties),
            new Rule("CDP-U003", RuleCategory.UnifiedInterface, "Series and category counts are within the documented limits", SeriesLimits),
            new Rule("CDP-U004", RuleCategory.UnifiedInterface, "Label formats are supported", LabelFormats),
            new Rule("CDP-U005", RuleCategory.UnifiedInterface, "Series colours are used by the chart type", ColorIgnored),
            new Rule("CDP-U006", RuleCategory.UnifiedInterface, "Custom colours are kept in the modern look", CustomColorOverride),
            new Rule("CDP-U007", RuleCategory.UnifiedInterface, "Colours use a supported format", ColorFormats),
        };

        private static readonly HashSet<string> Aggregates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "count", "countcolumn", "sum", "avg", "min", "max" };

        private static readonly HashSet<string> DateGroupings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "day", "week", "month", "quarter", "year", "fiscal-period", "fiscal-year" };

        // ------------------------------------------------------------------ Structure

        private static IEnumerable<ValidationResult> DataWellFormed(ValidationContext c)
        {
            if (string.IsNullOrWhiteSpace(c.DataText))
            {
                yield return Error(ValidationTarget.Data, "The data XML is empty",
                    "A chart needs a data description: the FetchXML query plus the category and measure definitions.",
                    "Restore the original data XML or paste a complete <datadefinition>.");
            }
            else if (c.DataError != null)
            {
                yield return new ValidationResult
                {
                    Severity = Severity.Error, Target = ValidationTarget.Data,
                    Title = "The data XML is not well-formed",
                    Explanation = c.DataError.Message,
                    Suggestion = "Fix the XML at the line shown. Dataverse rejects a chart whose XML can't be parsed.",
                    Line = c.DataError.Line, Column = c.DataError.Column
                };
            }
        }

        private static IEnumerable<ValidationResult> PresentationWellFormed(ValidationContext c)
        {
            if (string.IsNullOrWhiteSpace(c.PresentationText))
            {
                yield return Warning(ValidationTarget.Presentation, "The presentation XML is empty",
                    "Without a presentation description the chart has no chart type, colours or labels.",
                    "Restore the original presentation XML or paste a complete <Chart> element.");
            }
            else if (c.PresentationError != null)
            {
                yield return new ValidationResult
                {
                    Severity = Severity.Error, Target = ValidationTarget.Presentation,
                    Title = "The presentation XML is not well-formed",
                    Explanation = c.PresentationError.Message,
                    Suggestion = "Fix the XML at the line shown.",
                    Line = c.PresentationError.Line, Column = c.PresentationError.Column
                };
            }
        }

        private static IEnumerable<ValidationResult> DataStructure(ValidationContext c)
        {
            var root = c.Data?.Root;
            if (root == null) yield break;
            if (!Is(root, "datadefinition"))
            {
                yield return ErrorAt(root, ValidationTarget.Data, $"The root element is <{root.Name.LocalName}>, not <datadefinition>",
                    "The data description of a chart starts with <datadefinition>.", "Use <datadefinition> as the root element.");
                yield break;
            }
            if (!root.Elements().Any(e => Is(e, "fetchcollection")))
                yield return ErrorAt(root, ValidationTarget.Data, "<fetchcollection> is missing",
                    "The FetchXML query of the chart goes inside <fetchcollection>.", "Add <fetchcollection><fetch .../></fetchcollection>.");
            else if (!root.Descendants().Any(e => Is(e, "fetch")))
                yield return ErrorAt(root, ValidationTarget.Data, "<fetchcollection> has no <fetch>",
                    "The chart has no query, so it can't show data.", "Add the FetchXML query inside <fetchcollection>.");
            if (!root.Elements().Any(e => Is(e, "categorycollection")))
                yield return ErrorAt(root, ValidationTarget.Data, "<categorycollection> is missing",
                    "Categories and measures tell the chart which aggregate values to plot.", "Add <categorycollection><category>…</category></categorycollection>.");
            else if (!root.Descendants().Any(e => Is(e, "category")))
                yield return ErrorAt(root, ValidationTarget.Data, "<categorycollection> has no <category>",
                    "Without a category the chart has nothing to plot.", "Add a <category> with a <measurecollection>.");
        }

        private static IEnumerable<ValidationResult> CategoryStructure(ValidationContext c)
        {
            if (c.Data?.Root == null) yield break;
            foreach (var category in c.Data.Root.Descendants().Where(e => Is(e, "category")))
            {
                if (!category.Elements().Any(e => Is(e, "measurecollection")))
                    yield return ErrorAt(category, ValidationTarget.Data, "A <category> has no <measurecollection>",
                        "Each measure collection becomes one series of the chart.", "Add <measurecollection><measure alias=\"…\" /></measurecollection>.");
                foreach (var collection in category.Elements().Where(e => Is(e, "measurecollection")))
                {
                    if (!collection.Elements().Any(e => Is(e, "measure")))
                        yield return ErrorAt(collection, ValidationTarget.Data, "A <measurecollection> has no <measure>",
                            "The series has no value to plot.", "Add <measure alias=\"…\" /> pointing at an aggregate attribute alias.");
                    foreach (var measure in collection.Elements().Where(e => Is(e, "measure") && string.IsNullOrEmpty(Attr(e, "alias"))))
                        yield return ErrorAt(measure, ValidationTarget.Data, "A <measure> has no alias",
                            "A measure refers to an aggregate attribute of the query by its alias.", "Set alias to the alias of an aggregate attribute.");
                }
            }
        }

        private static IEnumerable<ValidationResult> MeasureAliases(ValidationContext c)
        {
            if (c.Data?.Root == null || c.DataModel?.Fetch == null) yield break;
            var aliases = new HashSet<string>(c.DataModel.Fetches.SelectMany(f => f.Attributes).Where(a => a.Alias != null).Select(a => a.Alias), StringComparer.OrdinalIgnoreCase);
            foreach (var measure in c.Data.Root.Descendants().Where(e => Is(e, "measure")))
            {
                var alias = Attr(measure, "alias");
                if (string.IsNullOrEmpty(alias) || aliases.Contains(alias)) continue;
                yield return ErrorAt(measure, ValidationTarget.Data, $"Measure alias '{alias}' doesn't match any attribute alias in the query",
                    "Each measure must point at an aggregate attribute of the FetchXML by its alias, otherwise the series has no data.",
                    $"Change the measure alias, or add alias=\"{alias}\" to the aggregate attribute it should show.");
            }
            foreach (var a in c.DataModel.Fetches.SelectMany(f => f.Aggregates))
            {
                var used = c.DataModel.Categories.SelectMany(cat => cat.MeasureCollections).SelectMany(m => m).Any(m => string.Equals(m, a.Alias, StringComparison.OrdinalIgnoreCase));
                if (!used && a.Alias != null)
                    yield return new ValidationResult
                    {
                        Severity = Severity.Information, Target = ValidationTarget.Data, Line = a.Line,
                        Title = $"Aggregate '{a.Alias}' isn't used by any measure",
                        Explanation = "The query calculates this value, but no series shows it.",
                        Suggestion = "Remove the attribute, or add a <measure> that uses it.",
                        Location = $"<attribute alias='{a.Alias}'>"
                    };
            }
        }

        private static IEnumerable<ValidationResult> SeriesCount(ValidationContext c)
        {
            if (c.DataModel == null || c.PresentationModel == null) yield break;
            var collections = c.DataModel.Categories.FirstOrDefault()?.MeasureCollections.Count ?? 0;
            var series = c.PresentationModel.Series.Count;
            if (collections > 0 && series > 0 && collections != series)
                yield return Warning(ValidationTarget.Presentation, $"{collections} measure collection(s) but {series} <Series> element(s)",
                    "Each <measurecollection> in the data XML maps to one <Series> in the presentation XML, in order. A mismatch can leave a series without formatting or without data.",
                    "Add or remove <Series> elements so the counts match.");
        }

        private static IEnumerable<ValidationResult> SingleFetch(ValidationContext c)
        {
            if (c.DataModel == null || c.DataModel.Fetches.Count <= 1) yield break;
            yield return Warning(ValidationTarget.Data, $"The data XML contains {c.DataModel.Fetches.Count} <fetch> queries",
                "Model-driven charts use one query. Comparison charts use one query with two grouped attributes, not two queries. Further queries may be ignored or rejected.",
                "Combine the queries into one <fetch>.");
        }

        private static IEnumerable<ValidationResult> ChartAreaCount(ValidationContext c)
        {
            if (c.Data?.Root == null || c.Presentation?.Root == null) yield break;
            var categories = c.Data.Root.Descendants().Count(e => Is(e, "category"));
            if (categories == 0) yield break;
            var areas = c.Presentation.Root.Elements().Where(e => Is(e, "ChartAreas")).SelectMany(e => e.Elements()).Where(e => Is(e, "ChartArea")).ToList();
            // Verified on Dataverse (2026-11-27): an empty <ChartArea /> (no attributes, no children) isn't counted.
            var counted = areas.Count(e => e.HasAttributes || e.HasElements);
            if (counted == categories) yield break;
            var empty = areas.Count - counted;
            var detail = empty > 0 ? $" ({empty} empty <ChartArea> element(s) don't count)" : string.Empty;
            yield return Error(ValidationTarget.Presentation, $"{counted} chart area(s) for {categories} categor{(categories == 1 ? "y" : "ies")}{detail}",
                "Dataverse rejects the save with \"The number of chart areas must be equal to the number of categories.\" A <ChartArea> with no attributes or child elements isn't counted.",
                empty > 0 ? "Give the <ChartArea> an attribute, for example BackColor=\"White\" (a property Unified Interface supports)." : "Add or remove <ChartArea> elements under <ChartAreas> so there is one per <category>.");
        }

        private static IEnumerable<ValidationResult> ImportSize(ValidationContext c)
        {
            var bytes = Encoding.UTF8.GetByteCount(c.DataText) + Encoding.UTF8.GetByteCount(c.PresentationText);
            var base64 = (bytes + 2) / 3 * 4;
            if (base64 > UciCapabilities.ImportBase64Limit)
                yield return Warning(ValidationTarget.Chart, $"The chart is about {base64:N0} characters when base64-encoded",
                    $"Microsoft documents a limit of {UciCapabilities.ImportBase64Limit:N0} base64 characters for importing a chart file through the UI. Saving through the API isn't affected, but exporting and re-importing this chart from the UI may fail.",
                    "Remove unused presentation properties (for example web-client-only fonts or styles) to make the chart smaller.");
        }

        // ------------------------------------------------------------------ FetchXML

        private static IEnumerable<ValidationResult> AggregateQuery(ValidationContext c)
        {
            if (c.Data?.Root == null) yield break;
            foreach (var fetch in c.Data.Root.Descendants().Where(e => Is(e, "fetch")))
            {
                if (!string.Equals(Attr(fetch, "aggregate"), "true", StringComparison.OrdinalIgnoreCase))
                    yield return ErrorAt(fetch, ValidationTarget.Data, "The <fetch> isn't an aggregate query",
                        "Chart queries group and aggregate records, which needs aggregate=\"true\" on <fetch>.", "Add aggregate=\"true\" to <fetch>.");
                if (!fetch.Elements().Any(e => Is(e, "entity")))
                    yield return ErrorAt(fetch, ValidationTarget.Data, "The <fetch> has no <entity>",
                        "The query must name the table it reads.", "Add <entity name=\"…\"> with the grouped and aggregate attributes.");
            }
        }

        private static IEnumerable<ValidationResult> GroupByAliases(ValidationContext c)
        {
            foreach (var a in Attributes(c).Where(a => IsTrue(Attr(a, "groupby")) && string.IsNullOrEmpty(Attr(a, "alias"))))
                yield return ErrorAt(a, ValidationTarget.Data, $"Grouped attribute '{Attr(a, "name")}' has no alias",
                    "In an aggregate query every grouped attribute needs an alias.", "Add an alias, e.g. alias=\"groupby_column\".");
            foreach (var a in Attributes(c).Where(a => !string.IsNullOrEmpty(Attr(a, "aggregate")) && string.IsNullOrEmpty(Attr(a, "alias"))))
                yield return ErrorAt(a, ValidationTarget.Data, $"Aggregate attribute '{Attr(a, "name")}' has no alias",
                    "Measures refer to aggregates by alias.", "Add an alias, e.g. alias=\"aggregate_column\".");
        }

        private static IEnumerable<ValidationResult> AggregateValues(ValidationContext c)
        {
            foreach (var a in Attributes(c))
            {
                var value = Attr(a, "aggregate");
                if (value != null && !Aggregates.Contains(value))
                    yield return ErrorAt(a, ValidationTarget.Data, $"'{value}' isn't a FetchXML aggregate",
                        "Valid aggregates are count, countcolumn, sum, avg, min and max.", "Use one of the valid aggregate values.");
            }
        }

        private static IEnumerable<ValidationResult> DateGroupingValues(ValidationContext c)
        {
            foreach (var a in Attributes(c))
            {
                var value = Attr(a, "dategrouping");
                if (value == null) continue;
                if (!DateGroupings.Contains(value))
                    yield return ErrorAt(a, ValidationTarget.Data, $"'{value}' isn't a valid date grouping",
                        "Valid values are day, week, month, quarter, year, fiscal-period and fiscal-year.", "Use one of the valid values.");
                if (!IsTrue(Attr(a, "groupby")))
                    yield return WarningAt(a, ValidationTarget.Data, $"dategrouping is set on '{Attr(a, "name")}' but it isn't grouped",
                        "Date grouping only applies to grouped attributes (groupby=\"true\").", "Add groupby=\"true\" or remove dategrouping.");
            }
        }

        private static IEnumerable<ValidationResult> UniqueAliases(ValidationContext c)
        {
            foreach (var group in Attributes(c).Where(a => !string.IsNullOrEmpty(Attr(a, "alias"))).GroupBy(a => Attr(a, "alias"), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                yield return WarningAt(group.Skip(1).First(), ValidationTarget.Data, $"Alias '{group.Key}' is used {group.Count()} times",
                    "Aliases identify values in the aggregate result; duplicates make measures ambiguous and Dataverse may reject the query.",
                    "Give each attribute a unique alias.");
        }

        // ------------------------------------------------------------------ Metadata

        private static IEnumerable<ValidationResult> TableMatches(ValidationContext c)
        {
            var fetch = c.DataModel?.Fetch;
            if (fetch == null || string.IsNullOrEmpty(fetch.EntityName)) yield break;
            if (!string.IsNullOrEmpty(c.ChartTable) && !string.Equals(fetch.EntityName, c.ChartTable, StringComparison.OrdinalIgnoreCase))
                yield return Error(ValidationTarget.Data, $"The query reads '{fetch.EntityName}' but the chart belongs to '{c.ChartTable}'",
                    "A chart's query must read the table the chart is attached to (primaryentitytypecode).",
                    $"Change <entity name=\"{fetch.EntityName}\"> to <entity name=\"{c.ChartTable}\">, or attach the chart to the other table.", fetch.Line);
        }

        private static IEnumerable<ValidationResult> ColumnsExist(ValidationContext c)
        {
            var fetch = c.DataModel?.Fetch;
            if (fetch == null) yield break;
            if (c.Metadata == null) { yield return NotChecked("column names"); yield break; }

            var uncheckedLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<(string table, string column, int line, string link)> refs =
                fetch.Attributes.Select(a => (a.Table, a.Name, a.Line, a.LinkEntity))
                .Concat(fetch.Conditions.Where(x => x.LinkEntity == null || fetch.Links.Any(l => (l.Alias ?? l.Name) == x.LinkEntity))
                    .Select(x => (x.LinkEntity == null ? fetch.EntityName : fetch.Links.First(l => (l.Alias ?? l.Name) == x.LinkEntity).Name, x.Attribute, x.Line, x.LinkEntity)));

            foreach (var (table, column, line, link) in refs)
            {
                if (string.IsNullOrEmpty(column)) continue;
                var meta = link == null ? c.Metadata : c.LinkedMetadata(table);
                if (meta == null) { uncheckedLinks.Add(table); continue; }
                if (!meta.Columns.ContainsKey(column))
                    yield return new ValidationResult
                    {
                        Severity = Severity.Error, Target = ValidationTarget.Data, Line = line,
                        Title = $"Column '{column}' doesn't exist on '{table}'",
                        Explanation = "The query refers to a column that isn't in this environment's metadata. The chart will fail to load.",
                        Suggestion = "Correct the column name, or create the column in this environment before using the chart.",
                        Location = $"<attribute name='{column}'>"
                    };
            }
            foreach (var t in uncheckedLinks)
                yield return new ValidationResult
                {
                    Severity = Severity.Information, Target = ValidationTarget.Data,
                    Title = $"Columns of linked table '{t}' weren't checked",
                    Explanation = "Metadata for the linked table couldn't be read.",
                    Suggestion = "Refresh metadata (⟳ Metadata) and validate again, or check your read access to that table."
                };
        }

        private static IEnumerable<ValidationResult> AggregateTypes(ValidationContext c)
        {
            var fetch = c.DataModel?.Fetch;
            if (fetch == null) yield break;
            if (c.Metadata == null) { yield return NotChecked("aggregation types"); yield break; }
            foreach (var a in fetch.Aggregates)
            {
                var meta = a.LinkEntity == null ? c.Metadata : c.LinkedMetadata(a.Table);
                if (meta == null || a.Name == null || !meta.Columns.TryGetValue(a.Name, out var col)) continue;
                var agg = a.Aggregate.ToLowerInvariant();
                if ((agg == "sum" || agg == "avg") && !col.IsNumeric)
                    yield return new ValidationResult
                    {
                        Severity = Severity.Error, Target = ValidationTarget.Data, Line = a.Line,
                        Title = $"{agg} on '{col.DisplayName}' ({a.Name}), a {col.Kind} column",
                        Explanation = "Sum and average only work on number and currency columns.",
                        Suggestion = "Use count or countcolumn, or choose a numeric column.",
                        Location = $"<attribute name='{a.Name}' aggregate='{a.Aggregate}'>"
                    };
                else if ((agg == "min" || agg == "max") && !col.IsNumeric && col.Kind != ColumnKind.DateTime)
                    yield return new ValidationResult
                    {
                        Severity = Severity.Error, Target = ValidationTarget.Data, Line = a.Line,
                        Title = $"{agg} on '{col.DisplayName}' ({a.Name}), a {col.Kind} column",
                        Explanation = "Minimum and maximum work on number, currency and date columns.",
                        Suggestion = "Use count, or choose a numeric or date column.",
                        Location = $"<attribute name='{a.Name}' aggregate='{a.Aggregate}'>"
                    };
                else if (agg == "count" && a.Name != null && meta.PrimaryIdAttribute != null && !string.Equals(a.Name, meta.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase))
                    yield return new ValidationResult
                    {
                        Severity = Severity.Information, Target = ValidationTarget.Data, Line = a.Line,
                        Title = $"count on '{col.DisplayName}' counts records, not values of that column",
                        Explanation = "aggregate=\"count\" counts all records in each group. To count only records where this column has a value, use countcolumn.",
                        Suggestion = "Keep count if you want record counts; use countcolumn to count non-empty values."
                    };
            }
        }

        private static IEnumerable<ValidationResult> DateGroupingTypes(ValidationContext c)
        {
            var fetch = c.DataModel?.Fetch;
            if (fetch == null) yield break;
            if (c.Metadata == null) { yield return NotChecked("date grouping columns"); yield break; }
            foreach (var g in fetch.GroupBys)
            {
                var meta = g.LinkEntity == null ? c.Metadata : c.LinkedMetadata(g.Table);
                if (meta == null || g.Name == null || !meta.Columns.TryGetValue(g.Name, out var col)) continue;
                if (!string.IsNullOrEmpty(g.DateGrouping) && col.Kind != ColumnKind.DateTime)
                    yield return new ValidationResult
                    {
                        Severity = Severity.Error, Target = ValidationTarget.Data, Line = g.Line,
                        Title = $"Date grouping on '{col.DisplayName}', which isn't a date column",
                        Explanation = "dategrouping only works on date and time columns.",
                        Suggestion = "Remove dategrouping or group by a date column."
                    };
                else if (string.IsNullOrEmpty(g.DateGrouping) && col.Kind == ColumnKind.DateTime)
                    yield return new ValidationResult
                    {
                        Severity = Severity.Warning, Target = ValidationTarget.Data, Line = g.Line,
                        Title = $"'{col.DisplayName}' is grouped by exact date and time",
                        Explanation = "Without dategrouping, each distinct date and time becomes its own category, which usually makes an unreadable chart.",
                        Suggestion = "Add dategrouping=\"month\" (or day, week, quarter, year) to the grouped attribute."
                    };
            }
        }

        // ------------------------------------------------------------------ Unified Interface

        private static IEnumerable<ValidationResult> ChartTypesSupported(ValidationContext c)
        {
            if (c.PresentationModel == null) yield break;
            foreach (var s in c.PresentationModel.Series)
            {
                if (string.IsNullOrEmpty(s.ChartType))
                    yield return new ValidationResult
                    {
                        Severity = Severity.Warning, Target = ValidationTarget.Presentation, Line = s.Line,
                        Title = "A <Series> has no ChartType", Explanation = "Without ChartType the chart engine falls back to its default type.",
                        Suggestion = "Set ChartType, e.g. ChartType=\"Column\"."
                    };
                else if (!UciCapabilities.ChartTypes.Contains(s.ChartType))
                    yield return new ValidationResult
                    {
                        Severity = Severity.Warning, Target = ValidationTarget.Presentation, Line = s.Line,
                        Title = $"Chart type '{s.ChartType}' isn't documented for model-driven charts",
                        Explanation = "Documented types: " + string.Join(", ", UciCapabilities.ChartTypes.OrderBy(x => x)) + ".",
                        Suggestion = "Use a documented chart type, and check how the chart looks in the app."
                    };
            }
        }

        private static IEnumerable<ValidationResult> UciProperties(ValidationContext c)
        {
            var root = c.Presentation?.Root;
            if (root == null) yield break;
            // One result per element name, listing the attributes Unified Interface ignores, to keep the list readable.
            var groups = root.DescendantsAndSelf()
                .SelectMany(e => e.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => new { Element = e, Attribute = a }))
                .Where(x => !UciCapabilities.IsAttributeSupported(x.Element.Name.LocalName, x.Attribute.Name.LocalName))
                .GroupBy(x => x.Element.Name.LocalName, StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                var attributes = g.Select(x => x.Attribute.Name.LocalName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
                var documented = UciCapabilities.SupportedAttributes.ContainsKey(g.Key);
                yield return new ValidationResult
                {
                    Severity = Severity.Information, Target = ValidationTarget.Presentation, Line = LineOf(g.First().Element), Column = ColumnOf(g.First().Element),
                    Title = documented
                        ? $"<{g.Key}>: {string.Join(", ", attributes)} {(attributes.Count == 1 ? "isn't" : "aren't")} used by Unified Interface"
                        : $"<{g.Key}> isn't documented for Unified Interface ({string.Join(", ", attributes)})",
                    Explanation = "Unified Interface draws charts with a different engine than the legacy web client and only honours a documented subset of properties. These settings are kept but have no visible effect in the app.",
                    Suggestion = "No action needed unless you rely on them. Details: " + UciCapabilities.DocumentationUrl,
                    Location = $"<{g.Key}>"
                };
            }
        }

        private static IEnumerable<ValidationResult> SeriesLimits(ValidationContext c)
        {
            if (c.DataModel?.Fetch == null) yield break;
            var groupBys = c.DataModel.Fetch.GroupBys.Count();
            var series = c.DataModel.Categories.FirstOrDefault()?.MeasureCollections.Count ?? 0;
            if (groupBys <= 1 && series > UciCapabilities.MaxSeries)
                yield return Warning(ValidationTarget.Data, $"{series} series; the documented maximum is {UciCapabilities.MaxSeries}",
                    "Charts with one category support at most five series.", "Reduce the number of measure collections to five or fewer.");
            if (groupBys == 2 && series != 1)
                yield return Warning(ValidationTarget.Data, $"Comparison chart with {series} series",
                    "A chart with two grouped attributes (a comparison chart) must have exactly one series.",
                    "Keep one measure collection, or remove the second grouped attribute.");
            if (groupBys > 2)
                yield return Warning(ValidationTarget.Data, $"{groupBys} grouped attributes",
                    "Charts support one category, or two for a comparison chart.", "Remove the extra grouped attributes.");
        }

        private static IEnumerable<ValidationResult> LabelFormats(ValidationContext c)
        {
            var root = c.Presentation?.Root;
            if (root == null) yield break;
            foreach (var attr in root.Descendants().SelectMany(e => e.Attributes()).Where(a => a.Name.LocalName == "LabelFormat" || a.Name.LocalName == "Format" || a.Name.LocalName == "Label" || a.Name.LocalName == "LegendText"))
            {
                var v = attr.Value ?? string.Empty;
                if (v.IndexOf("#VAL", StringComparison.OrdinalIgnoreCase) >= 0 || v.IndexOf("#TOTAL", StringComparison.OrdinalIgnoreCase) >= 0)
                    yield return new ValidationResult
                    {
                        Severity = Severity.Warning, Target = ValidationTarget.Presentation, Line = LineOf(attr.Parent), Column = ColumnOf(attr.Parent),
                        Title = $"{attr.Name.LocalName}=\"{v}\" uses #VAL or #TOTAL",
                        Explanation = "Microsoft states that #VAL and #TOTAL keywords aren't supported in Unified Interface, and there is no plan to support them. Charts using them may not render.",
                        Suggestion = "Use a number format such as #,0 or a percentage format instead.",
                        Location = $"<{attr.Parent.Name.LocalName} {attr.Name.LocalName}>"
                    };
                else if (v.IndexOf("#PERCENT", StringComparison.OrdinalIgnoreCase) >= 0)
                    yield return new ValidationResult
                    {
                        Severity = Severity.Information, Target = ValidationTarget.Presentation, Line = LineOf(attr.Parent), Column = ColumnOf(attr.Parent),
                        Title = $"{attr.Name.LocalName}=\"{v}\" uses #PERCENT",
                        Explanation = "#PERCENT is only partly supported in Unified Interface.",
                        Suggestion = "Check how the labels look in the app.",
                        Location = $"<{attr.Parent.Name.LocalName} {attr.Name.LocalName}>"
                    };
            }
        }

        private static IEnumerable<ValidationResult> ColorIgnored(ValidationContext c)
        {
            if (c.PresentationModel == null) yield break;
            foreach (var s in c.PresentationModel.Series.Where(s => !string.IsNullOrEmpty(s.Color) && UciCapabilities.SeriesColorIgnored.Contains(s.ChartType ?? "")))
                yield return new ValidationResult
                {
                    Severity = Severity.Information, Target = ValidationTarget.Presentation, Line = s.Line,
                    Title = $"Color is ignored on a {s.ChartType} series",
                    Explanation = "Pie, doughnut and funnel charts colour each slice from the palette, so the series Color has no effect.",
                    Suggestion = "Use the chart Palette or PaletteCustomColors to control slice colours."
                };
        }

        private static IEnumerable<ValidationResult> CustomColorOverride(ValidationContext c)
        {
            var model = c.PresentationModel;
            if (model == null) yield break;
            var hasCustomColors = model.Series.Any(s => !string.IsNullOrEmpty(s.Color)) ||
                                  c.Presentation.Root.Attributes().Any(a => a.Name.LocalName == "PaletteCustomColors");
            if (hasCustomColors && model.CustomColorOverride != true)
                yield return Information(ValidationTarget.Presentation, "Custom colours without CustomColorOverride=\"true\"",
                    "In the refreshed modern look, model-driven apps apply their own palette unless the chart sets CustomColorOverride=\"true\" on <Chart>.",
                    "Add CustomColorOverride=\"true\" to <Chart> if the custom colours must be kept.");
        }

        private static IEnumerable<ValidationResult> ColorFormats(ValidationContext c)
        {
            var root = c.Presentation?.Root;
            if (root == null) yield break;
            foreach (var attr in root.DescendantsAndSelf().SelectMany(e => e.Attributes())
                         .Where(a => a.Name.LocalName.EndsWith("Color", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.Value)))
            {
                if (UciCapabilities.IsValidColor(attr.Value)) continue;
                yield return new ValidationResult
                {
                    Severity = Severity.Warning, Target = ValidationTarget.Presentation, Line = LineOf(attr.Parent), Column = ColumnOf(attr.Parent),
                    Title = $"{attr.Name.LocalName}=\"{attr.Value}\" isn't a supported colour format",
                    Explanation = "Supported formats are r,g,b; a,r,g,b; #RRGGBB; and named colours such as SteelBlue.",
                    Suggestion = "Use one of the supported formats, e.g. #4169E1 or 65,105,225.",
                    Location = $"<{attr.Parent.Name.LocalName} {attr.Name.LocalName}>"
                };
            }
            if (root.Attribute("PaletteCustomColors") is XAttribute palette)
                foreach (var part in palette.Value.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0 && !UciCapabilities.IsValidColor(p)))
                    yield return new ValidationResult
                    {
                        Severity = Severity.Warning, Target = ValidationTarget.Presentation, Line = LineOf(root),
                        Title = $"PaletteCustomColors contains '{part}', which isn't a supported colour",
                        Explanation = "Colours in PaletteCustomColors are separated by semicolons and use the supported colour formats.",
                        Suggestion = "Fix or remove that colour."
                    };
        }

        // ------------------------------------------------------------------ helpers

        private sealed class Rule : IValidationRule
        {
            private readonly Func<ValidationContext, IEnumerable<ValidationResult>> _check;

            public Rule(string id, RuleCategory category, string title, Func<ValidationContext, IEnumerable<ValidationResult>> check)
            {
                Id = id;
                Category = category;
                Title = title;
                _check = check;
            }

            public string Id { get; }
            public RuleCategory Category { get; }
            public string Title { get; }
            public IEnumerable<ValidationResult> Check(ValidationContext context) => _check(context);
        }

        private static IEnumerable<XElement> Attributes(ValidationContext c) =>
            c.Data?.Root?.Descendants().Where(e => Is(e, "attribute")) ?? Enumerable.Empty<XElement>();

        private static ValidationResult NotChecked(string what) => new ValidationResult
        {
            Severity = Severity.Information, Target = ValidationTarget.Data,
            Title = $"Not checked: {what} (metadata unavailable)",
            Explanation = "Table metadata couldn't be read, so this check didn't run. It hasn't passed.",
            Suggestion = "Refresh metadata (⟳ Metadata), reopen the chart, and check your read access to the table."
        };

        private static ValidationResult Error(ValidationTarget t, string title, string why, string fix, int line = 0) =>
            new ValidationResult { Severity = Severity.Error, Target = t, Title = title, Explanation = why, Suggestion = fix, Line = line };

        private static ValidationResult Warning(ValidationTarget t, string title, string why, string fix) =>
            new ValidationResult { Severity = Severity.Warning, Target = t, Title = title, Explanation = why, Suggestion = fix };

        private static ValidationResult Information(ValidationTarget t, string title, string why, string fix) =>
            new ValidationResult { Severity = Severity.Information, Target = t, Title = title, Explanation = why, Suggestion = fix };

        private static ValidationResult ErrorAt(XElement e, ValidationTarget t, string title, string why, string fix) =>
            new ValidationResult { Severity = Severity.Error, Target = t, Title = title, Explanation = why, Suggestion = fix, Line = LineOf(e), Column = ColumnOf(e), Location = Describe(e) };

        private static ValidationResult WarningAt(XElement e, ValidationTarget t, string title, string why, string fix) =>
            new ValidationResult { Severity = Severity.Warning, Target = t, Title = title, Explanation = why, Suggestion = fix, Line = LineOf(e), Column = ColumnOf(e), Location = Describe(e) };

        private static string Describe(XElement e)
        {
            var name = Attr(e, "name") ?? Attr(e, "alias");
            return name == null ? $"<{e.Name.LocalName}>" : $"<{e.Name.LocalName} '{name}'>";
        }

        private static bool Is(XElement e, string name) => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

        private static string Attr(XElement e, string name) =>
            e.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

        private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

        private static int LineOf(XObject o) => o is IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : 0;

        /// <summary>1-based column of the element's start, so "go to line" lands on it even in one-line XML.</summary>
        private static int ColumnOf(XObject o) => o is IXmlLineInfo li && li.HasLineInfo() ? Math.Max(1, li.LinePosition - 1) : 0;
    }
}
