using System;
using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Models;
using PluginFailureCorrelationExplorer.Reporting;
using static PluginFailureCorrelationExplorer.Tests.Samples;

namespace PluginFailureCorrelationExplorer.Tests
{
    public static class RedactionTests
    {
        [Test]
        public static void Guids_get_stable_placeholders_within_one_report()
        {
            var r = new SensitiveDataRedactor(new RedactionOptions());
            var g1 = "3F2504E0-4F89-11D3-9A0C-0305E82C3301";
            var g2 = "9A2504E0-4F89-11D3-9A0C-0305E82C3399";
            Assert.Equal("[GUID-1] then [GUID-2] then [GUID-1]", r.Redact($"{g1} then {g2} then {{{g1.ToLowerInvariant()}}}"));
            Assert.Equal("[GUID-2]", r.Redact((Guid?)Guid.Parse(g2)));
        }

        [Test]
        public static void Emails_url_queries_and_custom_values_are_removed()
        {
            var r = new SensitiveDataRedactor(new RedactionOptions { CustomValues = new List<string> { "Northwind Traders", "NW" } });
            Assert.Equal("Mail [EMAIL] about [REDACTED] at https://api.contoso.example/orders?[REDACTED]",
                r.Redact("Mail someone@contoso.example about Northwind Traders at https://api.contoso.example/orders?customer=42&region=eu"));
        }

        [Test]
        public static void Credentials_are_always_removed_even_without_redaction()
        {
            var r = new SensitiveDataRedactor(RedactionOptions.None);
            var text = r.Redact("Authorization: Bearer abc.def-123 ; Password=Hunter2;ClientSecret=xyz; client_secret: 'qq' " +
                                "https://x.blob.core.windows.net/c/f?sv=2020&sig=SECRETSIG&se=2026 token=tok123 " +
                                "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.c2lnbmF0dXJlMTIz");
            foreach (var secret in new[] { "abc.def-123", "Hunter2", "xyz", "'qq'", "SECRETSIG", "tok123", "eyJhbGciOiJIUzI1NiJ9" })
                Assert.DoesNotContain(secret, text);
            Assert.Contains("Password=[REDACTED]", text);
            Assert.Contains("sig=[REDACTED]", text);
            Assert.Contains("3F2504E0-4F89-11D3-9A0C-0305E82C3301", r.Redact("3F2504E0-4F89-11D3-9A0C-0305E82C3301"));
        }

        [Test]
        public static void Null_and_empty_are_passed_through()
        {
            var r = new SensitiveDataRedactor(new RedactionOptions());
            Check.Null(r.Redact((string)null));
            Assert.Equal("", r.Redact(""));
            Assert.Equal("", r.Redact((Guid?)null));
        }
    }

    public static class ReportExportTests
    {
        private const string Script = "<script>alert('x')</script>";
        private const string TraceTextMarker = "TRACE-TEXT-MUST-NOT-BE-EXPORTED";

        private static AnalysisResult Result()
        {
            var chain = Guid.NewGuid();
            var traces = Repeat(3, i => Trace(minutes: i, correlation: chain, request: Guid.NewGuid(), block: TraceTextMarker,
                    exception: Fault("Bad value " + Script + ", \"quoted\", for someone@contoso.example")))
                .Concat(new[] { Trace(duration: 4000, block: TraceTextMarker) }).ToList();
            return Analyze(traces);
        }

        [Test]
        public static void Csv_escaping_quotes_delimiters_newlines_and_formulas()
        {
            Assert.Equal("plain", CsvReportWriter.Escape("plain"));
            Assert.Equal("\"a,b\"", CsvReportWriter.Escape("a,b"));
            Assert.Equal("\"say \"\"hi\"\"\"", CsvReportWriter.Escape("say \"hi\""));
            Assert.Equal("\"line1\nline2\"", CsvReportWriter.Escape("line1\nline2"));
            Assert.Equal("'=SUM(A1)", CsvReportWriter.Escape("=SUM(A1)"));
            Assert.Equal("'+1", CsvReportWriter.Escape("+1"));
            Assert.Equal("'@cmd", CsvReportWriter.Escape("@cmd"));
            Assert.Equal("\"'-2,3\"", CsvReportWriter.Escape("-2,3"));
            Assert.Equal("\" padded \"", CsvReportWriter.Escape(" padded "));
            Assert.Equal("", CsvReportWriter.Escape(null));
        }

        [Test]
        public static void Csv_exports_have_one_line_per_row_plus_header()
        {
            var r = Result();
            int Rows(string csv) => csv.Split(new[] { "\r\n" }, StringSplitOptions.None).Length - 1;   // trailing CRLF
            Assert.Equal(r.Findings.Count + 1, Rows(CsvReportWriter.Findings(r)));
            Assert.Equal(r.FailureGroups.Count + 1, Rows(CsvReportWriter.FailureGroups(r)));
            Assert.Equal(r.PerformanceGroups.Count + 1, Rows(CsvReportWriter.Performance(r)));
            Assert.Equal(r.Records.Count + 1, Rows(CsvReportWriter.Traces(r)));
        }

        [Test]
        public static void Html_is_encoded_and_contains_no_script()
        {
            var html = HtmlReportWriter.Write(Result());
            Assert.DoesNotContain("<script", html);
            Assert.Contains("&lt;script&gt;", html);
            Assert.Contains("Content-Security-Policy", html);
            Assert.Contains("default-src 'none'", html);
            Assert.Equal("&lt;b&gt;&amp;&quot;&#39;", HtmlReportWriter.E("<b>&\"'"));
        }

        [Test]
        public static void Html_report_has_every_required_section_and_classification()
        {
            var html = HtmlReportWriter.Write(Result());
            foreach (var section in new[] { "1. Scope", "2. Summary", "3. Diagnostic findings", "4. Recurring failure groups",
                         "5. Performance observations", "6. Correlation evidence", "7. Comparison with a previous period",
                         "8. Recommended investigation steps", "9. Notes and limitations" })
                Assert.Contains(section, html);
            foreach (var label in new[] { "Observed fact", "Calculated metric", "Correlation evidence", "Possible explanation" })
                Assert.Contains(label, html);
            Assert.Contains("Trend analysis unavailable", html);
            Assert.Contains("contosodev", html);
            Assert.Contains("evidence completeness", html);
        }

        [Test]
        public static void Trace_text_is_never_exported()
        {
            var r = Result();
            var options = new ReportOptions();
            foreach (var output in new[] { HtmlReportWriter.Write(r, options), JsonReportWriter.Write(r, options),
                         CsvReportWriter.Findings(r, options), CsvReportWriter.Traces(r, null, options), CsvReportWriter.FailureGroups(r, options) })
                Assert.DoesNotContain(TraceTextMarker, output);
        }

        [Test]
        public static void Redacted_reports_remove_guids_and_emails_everywhere()
        {
            var r = Result();
            var options = new ReportOptions { Redaction = new RedactionOptions() };
            var someGuid = r.Records[0].Id.ToString();
            foreach (var output in new[] { HtmlReportWriter.Write(r, options), JsonReportWriter.Write(r, options), CsvReportWriter.Traces(r, null, options), CsvReportWriter.Findings(r, options) })
            {
                Assert.DoesNotContain("someone@contoso.example", output);
                Assert.DoesNotContain(someGuid, output);
                Assert.DoesNotContain(someGuid.ToUpperInvariant(), output);
            }
            var unredacted = HtmlReportWriter.Write(r, new ReportOptions());
            Assert.Contains("someone@contoso.example", unredacted);
        }

        [Test]
        public static void Json_report_is_well_formed()
        {
            var json = JsonReportWriter.Write(Result(), new ReportOptions { Redaction = new RedactionOptions() });
            AssertJson(json);
            Assert.Contains("\"schemaVersion\": \"1.0\"", json);
            Assert.Contains("\\u003cscript\\u003e", json);
            AssertJson(JsonReportWriter.Write(Analyze(new PluginTraceRecord[0])));
        }

        [Test]
        public static void Json_string_escaping()
        {
            Assert.Equal("\"a\\\"b\\\\c\\nd\\u0001\\u003c\"", Reporting.JsonWriter.Quote("a\"b\\c\nd\u0001<"));
        }

        [Test]
        public static void Empty_result_exports_without_errors()
        {
            var r = Analyze(new PluginTraceRecord[0]);
            Assert.Contains("No rule produced a finding", HtmlReportWriter.Write(r));
            Assert.Contains("No failed traces", HtmlReportWriter.Write(r));
            Assert.Equal(1, CsvReportWriter.Findings(r).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Length);
        }

        private static void AssertJson(string json)
        {
#if NET5_0_OR_GREATER
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
                Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
#else
            // .NET Framework has no JSON parser in the base library: check structural balance outside strings.
            int depth = 0; var inString = false; var escaped = false;
            foreach (var c in json)
            {
                if (inString) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') inString = false; continue; }
                if (c == '"') inString = true; else if (c == '{' || c == '[') depth++; else if (c == '}' || c == ']') depth--;
                Assert.True(depth >= 0, "unbalanced JSON");
            }
            Assert.Equal(0, depth);
            Assert.False(inString, "unterminated string");
#endif
        }
    }
}
