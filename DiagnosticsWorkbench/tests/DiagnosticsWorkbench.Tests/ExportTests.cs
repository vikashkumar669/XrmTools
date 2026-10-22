using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.Serialization;
using DiagnosticsWorkbench.Analysis;
using DiagnosticsWorkbench.Data;
using DiagnosticsWorkbench.Export;
using DiagnosticsWorkbench.Models;
using static DiagnosticsWorkbench.Tests.Samples;

namespace DiagnosticsWorkbench.Tests
{
    public static class ExportTests
    {
        private static readonly Regex AnyGuid = new Regex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);

        private static (DiagnosticsSession Session, AnalysisResult Result) Sample()
        {
            var corr = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            var session = new DiagnosticsSession { EnvironmentName = "Contoso DEV", EnvironmentUrl = "https://contoso-dev.crm.dynamics.com", TraceSetting = TraceSetting.Exception };
            session.SetLogs(new[]
            {
                Log(minutes: 0, correlation: corr, step: stepId, duration: 70000, block: "Starting sync for <script>alert(1)</script>"),
                Log(minutes: 1, correlation: corr, depth: 2, step: stepId, exception: FaultException($"Account {Guid.NewGuid()} is \"locked\", value 1,200")),
                Log(minutes: 2, type: "Contoso.Plugins.ContactUpdate", message: "Update", table: "contact", duration: 2500),
            }, new QueryDescription { FromUtc = T0.AddDays(-1), ToUtc = T0.AddDays(1), Table = "account", RowCap = 50000, CapReached = true });
            session.SetSteps(new[] { Step(stepId, "AccountSync: Update of account", "Update", null) });
            return (session, session.Analyze());
        }

        [Test]
        public static void Csv_escaping_and_formula_injection()
        {
            Assert.Equal("plain", CsvExporter.Escape("plain"));
            Assert.Equal("\"a,b\"", CsvExporter.Escape("a,b"));
            Assert.Equal("\"say \"\"hi\"\"\"", CsvExporter.Escape("say \"hi\""));
            Assert.Equal("\"line1\nline2\"", CsvExporter.Escape("line1\nline2"));
            Assert.Equal("'=HYPERLINK(1)", CsvExporter.Escape("=HYPERLINK(1)"));
            Assert.Equal("-2147220891", CsvExporter.Escape("-2147220891"), "negative numbers stay numbers");
            Assert.Equal("", CsvExporter.Escape(null));
        }

        [Test]
        public static void Csv_logs_have_one_value_per_header_column()
        {
            var (session, result) = Sample();
            var w = new StringWriter();
            CsvExporter.WriteLogs(w, result.Logs, session);
            var text = w.ToString();
            var header = text.Substring(0, text.IndexOf("\r\n", StringComparison.Ordinal));
            Assert.Equal(22, header.Split(',').Length);
            Assert.Contains("AccountSync: Update of account", text);
            Assert.Contains("Yes", text);
            Assert.Equal(4, CountRecords(text), "header + 3 rows");
        }

        [Test]
        public static void Csv_all_views_write_and_redaction_removes_ids()
        {
            var (session, result) = Sample();
            foreach (var redact in new[] { false, true })
            {
                var w = new StringWriter();
                CsvExporter.WriteErrorGroups(w, result.ErrorGroups, redact);
                CsvExporter.WritePerformance(w, result.Performance);
                CsvExporter.WriteChains(w, result.Chains, redact);
                CsvExporter.WriteLogs(w, result.Logs, session, redact);
                if (redact) Assert.False(AnyGuid.IsMatch(w.ToString()), "redacted CSV has no GUIDs");
                else Assert.True(AnyGuid.IsMatch(w.ToString()));
            }
            Assert.Equal(3, CsvExporter.Utf8Bom.GetPreamble().Length, "UTF-8 BOM for Excel");
        }

        [Test]
        public static void Html_report_sections_and_encoding()
        {
            var (session, result) = Sample();
            var html = HtmlReportBuilder.Build(session, result, new ViewFilter(), new HtmlReportOptions { ToolVersion = "1.2026.10.1" });
            foreach (var part in new[] { "<!doctype html>", "Contoso DEV", "Summary", "Top error groups", "Slowest plugin types", "Failed chains",
                                         "Exception</b>", "row cap of 50,000", "table = account", "is &quot;locked&quot;", "<svg class=\"spark\"",
                                         "No filtering attributes", "v1.2026.10.1" })
                Assert.Contains(part, html);
            Assert.DoesNotContain("<script>", html);
            Assert.DoesNotContain("src=", html);
        }

        [Test]
        public static void Html_redacted_report_has_no_guids_or_environment()
        {
            var (session, result) = Sample();
            var html = HtmlReportBuilder.Build(session, result, new ViewFilter { ErrorsOnly = true }, new HtmlReportOptions { Redact = true });
            Assert.False(AnyGuid.IsMatch(html), "no GUIDs");
            Assert.DoesNotContain("contoso-dev.crm", html);
            Assert.Contains("(redacted)", html);
            Assert.Contains("errors only", html);
        }

        [Test]
        public static void Markdown_snippets()
        {
            var (session, result) = Sample();
            var failing = result.Logs.First(l => l.IsError);
            var md = MarkdownFormatter.ForLog(failing, session.StepFor(failing), session.ChainFor(failing, result), session.Performance, session.EnvironmentName);
            Assert.Contains("### Plugin failure: AccountPreCreate on Create of account", md);
            Assert.Contains("| Filtering attributes | (all columns) |", md);
            Assert.Contains("**Chain** (2 steps", md);
            Assert.Contains("```", md);
            var slow = result.Logs.First(l => l.ExecutionDurationMs == 70000);
            Assert.Contains("(near timeout)", MarkdownFormatter.ForLog(slow, null, null, session.Performance, null));
            Assert.Contains("### Recurring error #", MarkdownFormatter.ForGroup(result.ErrorGroups[0]));
        }

        [Test]
        public static void Durations_format_for_people()
        {
            Assert.Equal("–", Formatting.Duration(null));
            Assert.Equal("850 ms", Formatting.Duration(850));
            Assert.Equal("2.4 s", Formatting.Duration(2400));
            Assert.Equal("45 s", Formatting.Duration(45000));
            Assert.Equal("1 min 12 s", Formatting.Duration(72000));
        }

        private static int CountRecords(string csv)
        {
            var count = 0;
            var quoted = false;
            for (var i = 0; i < csv.Length; i++)
            {
                if (csv[i] == '"') quoted = !quoted;
                else if (!quoted && csv[i] == '\n') count++;
            }
            return count;
        }
    }

    public static class FetchAndSettingsTests
    {
        [Test]
        public static void FetchXml_is_valid_and_escapes_input()
        {
            var q = new QueryDescription
            {
                FromUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc),
                ToUtc = new DateTime(2026, 10, 21, 12, 30, 0, DateTimeKind.Utc),
                Table = "account",
                Message = "Update",
                PluginType = "Contoso_<Sync>%",
            };
            var xml = FetchXmlBuilder.TraceLogs(q, 3, "<cookie page=\"2\" />", 5000);
            var doc = XDocument.Parse(xml);
            var fetch = doc.Root;
            Assert.Equal("5000", (string)fetch.Attribute("count"));
            Assert.Equal("3", (string)fetch.Attribute("page"));
            Assert.Equal("<cookie page=\"2\" />", (string)fetch.Attribute("paging-cookie"));
            var conditions = doc.Descendants("condition").ToList();
            Assert.Equal("2026-10-20T00:00:00Z", (string)conditions[0].Attribute("value"));
            Assert.Equal("2026-10-21T12:30:00Z", (string)conditions[1].Attribute("value"));
            Assert.Equal("%Contoso[_]<Sync>[%]%", (string)conditions.Single(c => (string)c.Attribute("attribute") == "typename").Attribute("value"));
            Assert.Equal(FetchXmlBuilder.Columns.Length, doc.Descendants("attribute").Count());
            Assert.Equal("true", (string)doc.Descendants("order").Single().Attribute("descending"));
        }

        [Test]
        public static void FetchXml_omits_empty_filters_and_clamps_count()
        {
            var xml = FetchXmlBuilder.TraceLogs(new QueryDescription { FromUtc = T0, ToUtc = T0.AddHours(1) }, 1, null, 12);
            var doc = XDocument.Parse(xml);
            Assert.Equal(2, doc.Descendants("condition").Count());
            Assert.Equal("12", (string)doc.Root.Attribute("count"));
            Assert.Equal(null, (string)doc.Root.Attribute("paging-cookie"));
        }

        [Test]
        public static void Settings_round_trip_through_xml_serializer()
        {
            var settings = new WorkbenchSettings
            {
                SyncThresholdMs = 1500,
                LastRangeHours = 168,
                ThresholdOverrides = { new ThresholdOverride { TypeName = "Contoso.Plugins.AccountSync", MessageName = "Update", ThresholdMs = 900 } },
                CustomRules = { new NormalizationRule { Name = "order", Pattern = @"ORD-\d+", Token = "<order>", Enabled = false } },
            };
            var serializer = new XmlSerializer(typeof(WorkbenchSettings));
            var w = new StringWriter();
            serializer.Serialize(w, settings);
            var back = (WorkbenchSettings)serializer.Deserialize(new StringReader(w.ToString()));
            Assert.Equal(1500, back.SyncThresholdMs);
            Assert.Equal(168, back.LastRangeHours);
            Assert.Equal(900, back.ThresholdOverrides.Single().ThresholdMs);
            Assert.Equal(@"ORD-\d+", back.CustomRules.Single().Pattern);
            Assert.False(back.CustomRules.Single().Enabled);
        }
    }
}
