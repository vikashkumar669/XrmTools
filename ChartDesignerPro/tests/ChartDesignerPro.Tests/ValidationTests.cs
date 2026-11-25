using System;
using System.Collections.Generic;
using System.Linq;
using ChartDesignerPro.Models;
using ChartDesignerPro.Validation;

namespace ChartDesignerPro.Tests
{
    public static class ValidationTests
    {
        private static TableMetadata Opportunity() => new TableMetadata
        {
            LogicalName = "opportunity", DisplayName = "Opportunity", PrimaryIdAttribute = "opportunityid",
            Columns = new Dictionary<string, ColumnMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["opportunityid"] = new ColumnMetadata { LogicalName = "opportunityid", DisplayName = "Opportunity", Kind = ColumnKind.UniqueIdentifier },
                ["stepname"] = new ColumnMetadata { LogicalName = "stepname", DisplayName = "Pipeline Phase", Kind = ColumnKind.Text },
                ["estimatedvalue"] = new ColumnMetadata { LogicalName = "estimatedvalue", DisplayName = "Est. Revenue", Kind = ColumnKind.Money },
                ["actualclosedate"] = new ColumnMetadata { LogicalName = "actualclosedate", DisplayName = "Actual Close Date", Kind = ColumnKind.DateTime },
                ["name"] = new ColumnMetadata { LogicalName = "name", DisplayName = "Topic", Kind = ColumnKind.Text },
            }
        };

        private static ValidationReport Run(string data, string presentation, TableMetadata meta = null, string table = "opportunity") =>
            new ValidationEngine().Run(ValidationContext.Create(data, presentation, table, meta));

        private static IEnumerable<ValidationResult> Findings(ValidationReport r, string ruleId) =>
            r.Results.Where(x => x.RuleId == ruleId && x.Severity != Severity.Passed);

        private static string Data(string attributes, string measures = "<measure alias=\"a\" />", string fetchAttrs = "aggregate=\"true\"", string entity = "opportunity") =>
            $"<datadefinition><fetchcollection><fetch mapping=\"logical\" {fetchAttrs}><entity name=\"{entity}\">{attributes}</entity></fetch></fetchcollection>" +
            $"<categorycollection><category><measurecollection>{measures}</measurecollection></category></categorycollection></datadefinition>";

        private const string GroupAndSum = "<attribute groupby=\"true\" alias=\"g\" name=\"stepname\" /><attribute alias=\"a\" name=\"estimatedvalue\" aggregate=\"sum\" />";
        private const string ColumnChart = "<Chart CustomColorOverride=\"true\"><Series><Series ChartType=\"Column\" Color=\"#4169E1\" /></Series></Chart>";

        [Test]
        public static void ValidChartHasNoErrorsOrWarnings()
        {
            var r = Run(Samples.ColumnData, Samples.ColumnPresentation, Opportunity());
            Assert.Equal(0, r.Errors, "errors: " + string.Join("; ", r.Results.Where(x => x.Severity == Severity.Error)));
            Assert.Equal(0, r.Warnings, "warnings: " + string.Join("; ", r.Results.Where(x => x.Severity == Severity.Warning)));
            Assert.True(r.Passed >= 15, "most rules pass");
            Assert.Equal(RuleCatalog.All.Count, r.Results.Select(x => x.RuleId).Distinct().Count(), "every rule reports");
        }

        [Test]
        public static void RuleIdsAreUnique()
        {
            Assert.Equal(RuleCatalog.All.Count, RuleCatalog.All.Select(x => x.Id).Distinct().Count());
        }

        [Test]
        public static void MalformedXmlIsAnErrorWithLine()
        {
            var r = Run(Samples.Malformed, ColumnChart);
            var e = Findings(r, "CDP-S001").Single();
            Assert.Equal(Severity.Error, e.Severity);
            Assert.Equal(4, e.Line);
            Assert.Equal(Severity.Error, r.Results[0].Severity, "errors sort first");
        }

        [Test]
        public static void MissingStructureIsReported()
        {
            var r = Run("<datadefinition><fetchcollection /></datadefinition>", ColumnChart);
            var titles = Findings(r, "CDP-S003").Select(x => x.Title).ToList();
            Assert.True(titles.Any(t => t.Contains("no <fetch>")), "no fetch");
            Assert.True(titles.Any(t => t.Contains("<categorycollection> is missing")), "no categories");
            Assert.Equal(Severity.Error, Findings(Run("<chart />", ColumnChart), "CDP-S003").Single().Severity);
        }

        [Test]
        public static void MeasureAliasMustMatchAggregate()
        {
            var r = Run(Data(GroupAndSum, "<measure alias=\"missing\" />"), ColumnChart, Opportunity());
            Assert.True(Findings(r, "CDP-S005").Any(x => x.Severity == Severity.Error && x.Title.Contains("'missing'")), "unmatched alias");
            Assert.True(Findings(r, "CDP-S005").Any(x => x.Severity == Severity.Information && x.Title.Contains("'a' isn't used")), "unused aggregate");
        }

        [Test]
        public static void SeriesCountMismatchIsAWarning()
        {
            var r = Run(Data(GroupAndSum), "<Chart><Series><Series ChartType=\"Column\" /><Series ChartType=\"Line\" /></Series></Chart>", Opportunity());
            Assert.Equal(Severity.Warning, Findings(r, "CDP-S006").Single().Severity);
        }

        [Test]
        public static void FetchRules()
        {
            var r = Run(Data("<attribute groupby=\"true\" name=\"stepname\" dategrouping=\"decade\" /><attribute alias=\"a\" name=\"estimatedvalue\" aggregate=\"total\" /><attribute alias=\"a\" name=\"name\" aggregate=\"count\" />", fetchAttrs: ""), ColumnChart);
            Assert.True(Findings(r, "CDP-F001").Any(), "aggregate missing");
            Assert.True(Findings(r, "CDP-F002").Any(x => x.Title.Contains("stepname")), "groupby alias");
            Assert.True(Findings(r, "CDP-F003").Any(x => x.Title.Contains("'total'")), "bad aggregate");
            Assert.True(Findings(r, "CDP-F003").Single().Column > 100, "column points into the one-line XML");
            Assert.True(Findings(r, "CDP-F004").Any(x => x.Title.Contains("'decade'")), "bad date grouping");
            Assert.True(Findings(r, "CDP-F005").Any(x => x.Title.Contains("'a'")), "duplicate alias");
        }

        [Test]
        public static void MetadataRules()
        {
            var r = Run(Data("<attribute groupby=\"true\" alias=\"g\" name=\"actualclosedate\" /><attribute alias=\"a\" name=\"name\" aggregate=\"sum\" /><attribute alias=\"b\" name=\"nosuchcolumn\" aggregate=\"max\" /><attribute alias=\"c\" name=\"stepname\" groupby=\"true\" dategrouping=\"month\" />"), ColumnChart, Opportunity());
            Assert.True(Findings(r, "CDP-M002").Any(x => x.Severity == Severity.Error && x.Title.Contains("nosuchcolumn")), "missing column");
            Assert.True(Findings(r, "CDP-M003").Any(x => x.Severity == Severity.Error && x.Title.StartsWith("sum")), "sum on text");
            Assert.True(Findings(r, "CDP-M004").Any(x => x.Severity == Severity.Warning && x.Title.Contains("exact date")), "date without grouping");
            Assert.True(Findings(r, "CDP-M004").Any(x => x.Severity == Severity.Error && x.Title.Contains("Pipeline Phase")), "grouping on text");
        }

        [Test]
        public static void CountOnNonPrimaryColumnIsExplained()
        {
            var r = Run(Data("<attribute groupby=\"true\" alias=\"g\" name=\"stepname\" /><attribute alias=\"a\" name=\"name\" aggregate=\"count\" />"), ColumnChart, Opportunity());
            Assert.Equal(Severity.Information, Findings(r, "CDP-M003").Single().Severity);
        }

        [Test]
        public static void WrongTableIsAnError()
        {
            var r = Run(Data(GroupAndSum, entity: "account"), ColumnChart, Opportunity());
            Assert.Equal(Severity.Error, Findings(r, "CDP-M001").Single().Severity);
        }

        [Test]
        public static void WithoutMetadataChecksAreNotCheckedNotPassed()
        {
            var r = Run(Samples.ColumnData, Samples.ColumnPresentation);
            foreach (var id in new[] { "CDP-M002", "CDP-M003", "CDP-M004" })
            {
                var x = r.Results.Single(v => v.RuleId == id);
                Assert.Equal(Severity.Information, x.Severity, id);
                Assert.Contains("Not checked", x.Title);
            }
        }

        [Test]
        public static void UnifiedInterfaceRules()
        {
            const string presentation =
                "<Chart Palette=\"None\"><Series><Series ChartType=\"Pie\" Color=\"blue-ish\" LabelFormat=\"#VAL of #TOTAL\" Font=\"{0}, 9px\" />" +
                "</Series><ChartAreas><ChartArea BorderColor=\"White\"><AxisY Title=\"x\" LabelAutoFitMinFontSize=\"8\" /></ChartArea></ChartAreas></Chart>";
            var r = Run(Data(GroupAndSum), presentation, Opportunity());
            Assert.True(Findings(r, "CDP-U002").Any(x => x.Title.Contains("<Series>") && x.Title.Contains("Font")), "Series Font unsupported");
            Assert.True(Findings(r, "CDP-U002").Any(x => x.Title.Contains("<ChartArea>") && x.Title.Contains("BorderColor")), "ChartArea BorderColor unsupported");
            Assert.False(Findings(r, "CDP-U002").Any(x => x.Title.Contains("ChartType")), "ChartType is supported");
            Assert.Equal(Severity.Warning, Findings(r, "CDP-U004").Single().Severity);
            Assert.True(Findings(r, "CDP-U005").Any(), "pie colour ignored");
            Assert.True(Findings(r, "CDP-U006").Any(), "no CustomColorOverride");
            Assert.True(Findings(r, "CDP-U007").Any(x => x.Title.Contains("blue-ish")), "bad colour");
        }

        [Test]
        public static void UnknownChartTypeAndSeriesLimits()
        {
            var six = string.Concat(Enumerable.Range(1, 6).Select(i => $"<attribute alias=\"a{i}\" name=\"estimatedvalue\" aggregate=\"sum\" />"));
            var measures = string.Concat(Enumerable.Range(1, 6).Select(i => $"<measure alias=\"a{i}\" />"));
            var data = $"<datadefinition><fetchcollection><fetch aggregate=\"true\"><entity name=\"opportunity\"><attribute groupby=\"true\" alias=\"g\" name=\"stepname\" />{six}</entity></fetch></fetchcollection>" +
                       "<categorycollection><category>" + string.Concat(Enumerable.Range(1, 6).Select(i => $"<measurecollection><measure alias=\"a{i}\" /></measurecollection>")) + "</category></categorycollection></datadefinition>";
            var r = Run(data, "<Chart><Series><Series ChartType=\"Radar\" /></Series></Chart>", Opportunity());
            Assert.True(Findings(r, "CDP-U001").Any(x => x.Title.Contains("Radar")), "unknown type");
            Assert.True(Findings(r, "CDP-U003").Any(x => x.Title.Contains("6 series")), "over five series");
        }

        [Test]
        public static void ColourFormats()
        {
            Assert.True(UciCapabilities.IsValidColor("91, 151, 213"), "r, g, b with spaces");
            Assert.True(UciCapabilities.IsValidColor("255,91,151,213"), "a,r,g,b");
            Assert.True(UciCapabilities.IsValidColor("#4169E1"), "hex");
            Assert.True(UciCapabilities.IsValidColor("SteelBlue"), "named");
            Assert.False(UciCapabilities.IsValidColor("#4169E"), "short hex");
            Assert.False(UciCapabilities.IsValidColor("300,0,0"), "out of range");
        }

        [Test]
        public static void ImportSizeWarning()
        {
            var big = "<Chart Comment=\"" + new string('x', 13000) + "\" />";
            Assert.Equal(Severity.Warning, Findings(Run(Samples.ColumnData, big), "CDP-S020").Single().Severity);
        }

        [Test]
        public static void DisabledRulesDontRunAndCrashingRulesAreContained()
        {
            var ctx = ValidationContext.Create(Samples.ColumnData, Samples.ColumnPresentation, "opportunity");
            var r = new ValidationEngine().Run(ctx, new HashSet<string> { "CDP-U002" });
            Assert.False(r.Results.Any(x => x.RuleId == "CDP-U002"), "disabled");

            var crash = new ValidationEngine(new[] { new Crasher() }).Run(ctx);
            Assert.Equal(Severity.Information, crash.Results.Single().Severity);
            Assert.Contains("could not run", crash.Results.Single().Title);
        }

        private sealed class Crasher : IValidationRule
        {
            public string Id => "TEST-1";
            public RuleCategory Category => RuleCategory.Structure;
            public string Title => "Crashes";
            public IEnumerable<ValidationResult> Check(ValidationContext context) => throw new InvalidOperationException("boom");
        }
    }
}
