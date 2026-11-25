using System;
using System.Collections.Generic;
using System.Linq;
using ChartDesignerPro.Models;
using ChartDesignerPro.Summary;
using ChartDesignerPro.Xml;

namespace ChartDesignerPro.Tests
{
    public static class SummaryTests
    {
        private static DataDescriptionModel Data(string xml)
        {
            Assert.True(SafeXml.TryParse(xml, out var doc, out var error), error?.ToString());
            return ChartDefinitionParser.ParseData(doc);
        }

        private static PresentationModel Presentation(string xml)
        {
            Assert.True(SafeXml.TryParse(xml, out var doc, out var error), error?.ToString());
            return ChartDefinitionParser.ParsePresentation(doc);
        }

        private static TableMetadata Opportunity() => new TableMetadata
        {
            LogicalName = "opportunity",
            DisplayName = "Opportunity",
            DisplayCollectionName = "Opportunities",
            Columns = new Dictionary<string, ColumnMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["stepname"] = new ColumnMetadata { LogicalName = "stepname", DisplayName = "Pipeline Phase", Kind = ColumnKind.Text },
                ["estimatedvalue"] = new ColumnMetadata { LogicalName = "estimatedvalue", DisplayName = "Est. Revenue", Kind = ColumnKind.Money },
                ["actualclosedate"] = new ColumnMetadata { LogicalName = "actualclosedate", DisplayName = "Actual Close Date", Kind = ColumnKind.DateTime },
                ["statecode"] = new ColumnMetadata { LogicalName = "statecode", DisplayName = "Status", Kind = ColumnKind.State },
            }
        };

        [Test]
        public static void ParsesFetchCategoriesAndSeries()
        {
            var data = Data(Samples.ColumnData);
            Assert.Equal("opportunity", data.Fetch.EntityName);
            Assert.True(data.Fetch.IsAggregate, "aggregate");
            Assert.Equal("stepname", data.Fetch.GroupBys.Single().Name);
            Assert.Equal("sum", data.Fetch.ByAlias("aggregate_column").Aggregate);
            Assert.Equal("aggregate_column", data.Categories.Single().MeasureCollections.Single().Single());
            Assert.True(data.Fetch.Attributes[0].Line > 0, "line info kept");

            var p = Presentation(Samples.ColumnPresentation);
            Assert.Equal("Column", p.Series.Single().ChartType);
            Assert.Equal("#4169E1", p.Series.Single().Color);
            Assert.Equal(true, p.CustomColorOverride);
            Assert.Equal(false, p.LegendEnabled);
        }

        [Test]
        public static void DescribesWithDisplayNames()
        {
            var s = ChartDescriber.Describe(Data(Samples.ColumnData), Presentation(Samples.ColumnPresentation), Opportunity());
            Assert.Equal("Sums Est. Revenue by Pipeline Phase, shown as a column chart.", s.Sentence);
            Assert.Equal("Opportunity (opportunity)", s.Facts.First(f => f.Label == "Table").Value);
            Assert.False(s.UsedLogicalNames, "metadata used");
            Assert.True(s.Notes.Any(n => n.Contains("no filter")), "explains view filtering");
        }

        [Test]
        public static void DescribesWithoutMetadataUsingLogicalNames()
        {
            var s = ChartDescriber.Describe(Data(Samples.ColumnData), Presentation(Samples.ColumnPresentation));
            Assert.Equal("Sums estimatedvalue by stepname, shown as a column chart.", s.Sentence);
            Assert.True(s.UsedLogicalNames, "logical names");
        }

        [Test]
        public static void DescribesCountDateGroupingFiltersAndTopN()
        {
            const string xml =
                "<datadefinition><fetchcollection><fetch mapping=\"logical\" aggregate=\"true\" count=\"5\"><entity name=\"opportunity\">" +
                "<attribute alias=\"c\" name=\"opportunityid\" aggregate=\"count\" />" +
                "<attribute groupby=\"true\" alias=\"g\" name=\"actualclosedate\" dategrouping=\"month\" />" +
                "<order alias=\"c\" descending=\"true\" />" +
                "<filter><condition attribute=\"statecode\" operator=\"eq\" value=\"1\" /></filter>" +
                "</entity></fetch></fetchcollection><categorycollection><category><measurecollection><measure alias=\"c\" />" +
                "</measurecollection></category></categorycollection></datadefinition>";
            var s = ChartDescriber.Describe(Data(xml), Presentation("<Chart><Series><Series ChartType=\"Line\" /></Series></Chart>"), Opportunity());
            Assert.Equal("Counts Opportunities by Actual Close Date (month), shown as a line chart, limited to the top 5.", s.Sentence);
            Assert.Equal("Status eq 1", s.Facts.First(f => f.Label == "Chart filters").Value);
            Assert.Equal("c (descending)", s.Facts.First(f => f.Label == "Sort").Value);
        }

        [Test]
        public static void ReportsUnmatchedMeasureAlias()
        {
            var xml = Samples.ColumnData.Replace("<measure alias=\"aggregate_column\" />", "<measure alias=\"missing\" />");
            var s = ChartDescriber.Describe(Data(xml), null, Opportunity());
            Assert.True(s.Notes.Any(n => n.Contains("'missing'")), "note about alias");
            Assert.Contains("Sums Est. Revenue", s.Sentence);
        }

        [Test]
        public static void MultiSeriesAndLinkedTables()
        {
            const string xml =
                "<datadefinition><fetchcollection><fetch aggregate=\"true\"><entity name=\"opportunity\">" +
                "<attribute alias=\"a\" name=\"estimatedvalue\" aggregate=\"sum\" /><attribute alias=\"b\" name=\"actualvalue\" aggregate=\"sum\" />" +
                "<link-entity name=\"account\" from=\"accountid\" to=\"parentaccountid\" alias=\"acc\"><attribute groupby=\"true\" alias=\"g\" name=\"industrycode\" /></link-entity>" +
                "</entity></fetch></fetchcollection><categorycollection><category><measurecollection><measure alias=\"a\" /></measurecollection>" +
                "<measurecollection><measure alias=\"b\" /></measurecollection></category></categorycollection></datadefinition>";
            var s = ChartDescriber.Describe(Data(xml),
                Presentation("<Chart><Series><Series ChartType=\"Column\" /><Series ChartType=\"Line\" /></Series></Chart>"),
                Opportunity(),
                t => t == "account" ? new TableMetadata { LogicalName = "account", DisplayName = "Account" } : null);
            Assert.Equal("Sums Est. Revenue and sums actualvalue by industrycode (Account), shown as a column chart combined with line chart.", s.Sentence);
            Assert.Equal("Account", s.Facts.First(f => f.Label == "Related tables").Value);
            Assert.Equal("acc", Data(xml).Fetch.GroupBys.Single().LinkEntity);
        }

        [Test]
        public static void EmptyOrMissingFetchIsExplained()
        {
            var s = ChartDescriber.Describe(Data("<datadefinition />"), null);
            Assert.Contains("no FetchXML", s.Sentence);
        }
    }
}
