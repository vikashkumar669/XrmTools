using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;
using PluginFailureCorrelationExplorer.Reporting;
using static PluginFailureCorrelationExplorer.Tests.Samples;

namespace PluginFailureCorrelationExplorer.Tests
{
    /// <summary>
    /// A deterministic, fully synthetic 24-hour scenario (fictional Contoso organization) used for the sample report in
    /// the documentation and for an end-to-end test. Run: dotnet run --project tests/PluginFailureCorrelationExplorer.Tests -- --samples &lt;folder&gt;
    /// </summary>
    public static class SampleScenario
    {
        public static readonly DateTime From = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
        public static readonly DateTime To = From.AddHours(24);

        private sealed class Gen
        {
            private readonly Random _rnd;
            private int _n;
            public readonly List<PluginTraceRecord> Traces = new List<PluginTraceRecord>();

            public Gen(int seed) { _rnd = new Random(seed); }

            public Guid Id()
            {
                var bytes = new byte[16];
                _rnd.NextBytes(bytes);
                return new Guid(bytes);
            }

            public double Minute(DateTime from) => _rnd.NextDouble() * 24 * 60;
            public int Between(int a, int b) => _rnd.Next(a, b);

            public PluginTraceRecord Add(DateTime start, int durationMs, string type, string message, string table, int depth, int mode,
                Guid? correlation, Guid? request, string exception = null, Guid? step = null, string block = null)
            {
                var t = new PluginTraceRecord
                {
                    Id = Id(),
                    ExecutionStartUtc = start,
                    ExecutionDurationMs = durationMs,
                    CreatedOnUtc = start.AddMilliseconds(durationMs),
                    TypeName = type + ", Contoso.Plugins, Version=2.4.0.0, Culture=neutral, PublicKeyToken=0123456789abcdef",
                    MessageName = message,
                    PrimaryEntity = table,
                    Depth = depth,
                    Mode = mode,
                    OperationType = 1,
                    CorrelationId = correlation,
                    RequestId = request,
                    PluginStepId = step ?? StepFor(type, message),
                    ExceptionDetails = exception,
                    ExceptionLoaded = true,
                    MessageBlock = block ?? $"Entered {type}.Execute()\nDepth: {depth}\nExiting {type}.Execute()",
                    MessageBlockLoaded = true,
                };
                Traces.Add(t);
                _n++;
                return t;
            }

            private readonly Dictionary<string, Guid> _steps = new Dictionary<string, Guid>();
            private Guid StepFor(string type, string message)
            {
                var key = type + "|" + message;
                if (!_steps.TryGetValue(key, out var id)) _steps[key] = id = Id();
                return id;
            }
        }

        /// <summary>Traces for one 24-hour period. The baseline period has fewer failures and faster exports.</summary>
        public static List<PluginTraceRecord> Build(DateTime from, bool baseline, int seed)
        {
            var g = new Gen(seed);
            DateTime At(double minutes) => from.AddMinutes(minutes);

            // Healthy background traffic
            for (var i = 0; i < 260; i++)
            {
                var start = At(g.Minute(from));
                var c = g.Id();
                g.Add(start, g.Between(40, 400), "Contoso.Plugins.AccountPreCreate", "Create", "account", 1, 0, c, g.Id());
                if (i % 3 == 0) g.Add(start.AddMilliseconds(500), g.Between(80, 900), "Contoso.Plugins.ContactSync", "Update", "contact", 1, 0, g.Id(), g.Id());
            }

            // 1. Validation failure thrown on purpose (InvalidPluginExecutionException): increases in the current period
            var validation = baseline ? 6 : 18;
            for (var i = 0; i < validation; i++)
            {
                var start = At(g.Minute(from));
                g.Add(start, g.Between(30, 120), "Contoso.Plugins.AccountPreCreate", "Create", "account", 1, 0, g.Id(), g.Id(),
                    Fault($"Account name is required for '{(i % 2 == 0 ? "Fabrikam" : "Northwind")} branch {100 + i}'.", "-2147220891", activityId: g.Id(), at: start));
            }

            // 2. Cascading failure: OrderSubmit (depth 1) → ContactSync (depth 2) NullReferenceException (current period only)
            if (!baseline)
            {
                for (var i = 0; i < 6; i++)
                {
                    var start = At(300 + i * 97);
                    var chain = g.Id();
                    var request = g.Id();
                    var nre = "Unhandled exception: \nException type: System.ServiceModel.FaultException`1[Microsoft.Xrm.Sdk.OrganizationServiceFault]\n" +
                              "Message: Object reference not set to an instance of an object.\nDetail: \n<OrganizationServiceFault>\n" +
                              $"  <ActivityId>{g.Id()}</ActivityId>\n  <ErrorCode>-2147220956</ErrorCode>\n" +
                              "  <Message>System.NullReferenceException: Microsoft Dynamics CRM has experienced an error. Reference number for administrators or support: #" + g.Between(0x10000000, 0x7FFFFFFF).ToString("X8") + "</Message>\n" +
                              "</OrganizationServiceFault>\n" +
                              "   at Contoso.Plugins.ContactSync.ResolvePrimaryContact(Entity order)\n   at Contoso.Plugins.ContactSync.Execute(IServiceProvider serviceProvider)";
                    g.Add(start.AddMilliseconds(180), 240, "Contoso.Plugins.ContactSync", "Update", "contact", 2, 0, chain, g.Id(), nre);
                    g.Add(start, 900, "Contoso.Plugins.OrderSubmit", "Create", "salesorder", 1, 0, chain, request,
                        Fault("ContactSync failed: Object reference not set to an instance of an object.", "-2147220891", activityId: g.Id(), at: start));
                }
            }

            // 2b. A second plug-in on Create of account hits SQL deadlocks under load (current period only)
            if (!baseline)
            {
                for (var i = 0; i < 4; i++)
                {
                    var start = At(820 + i * 3);
                    g.Add(start, g.Between(1500, 4000), "Contoso.Plugins.AccountNumbering", "Create", "account", 1, 0, g.Id(), g.Id(),
                        Fault("Generic SQL error.", "-2147204784", activityId: g.Id(), at: start));
                }
            }

            // 3. KeyNotFoundException in an asynchronous rollup (both periods, stable)
            for (var i = 0; i < 4; i++)
            {
                var start = At(g.Minute(from));
                g.Add(start, g.Between(200, 600), "Contoso.Plugins.OpportunityRollup", "Update", "opportunity", 1, 1, g.Id(), g.Id(),
                    "System.Collections.Generic.KeyNotFoundException: The given key was not present in the dictionary.\n" +
                    "   at Contoso.Plugins.OpportunityRollup.SumLines(Entity target)\n   at Contoso.Plugins.OpportunityRollup.Execute(IServiceProvider serviceProvider)");
            }

            // 4. Slow asynchronous export: slower in the current period (regression)
            for (var i = 0; i < 30; i++)
            {
                var start = At(g.Minute(from));
                var duration = baseline ? g.Between(3000, 7000) : g.Between(9000, 26000);
                g.Add(start, duration, "Contoso.Plugins.InvoiceExport", "Create", "invoice", 1, 1, g.Id(), g.Id(),
                    block: "Calling https://erp.contoso.example/api/invoices?tenant=contoso&sig=abc123\nERP responded after " + (duration - 150) + " ms");
            }

            // 5. Synchronous price calculation close to the 2-minute timeout (current period)
            if (!baseline)
            {
                foreach (var d in new[] { 104000, 112500, 2600, 2900, 3100 })
                    g.Add(At(g.Minute(from)), d, "Contoso.Plugins.PriceCalculator", "Update", "quote", 1, 0, g.Id(), g.Id());
            }

            // 6. Suspected recursion: AccountHierarchy re-triggering itself at increasing depths
            if (!baseline)
            {
                var chain = g.Id();
                var start = At(700);
                for (var depth = 1; depth <= 4; depth++)
                    g.Add(start.AddMilliseconds(depth * 20), 1000 - depth * 200, "Contoso.Plugins.AccountHierarchy", "Update", "account", depth, 0, chain, g.Id());
            }

            // 7. Workflow activity traces without correlation or request IDs (incomplete evidence)
            for (var i = 0; i < 12; i++)
            {
                var t = g.Add(At(g.Minute(from)), g.Between(50, 300), "Contoso.Workflows.SendWelcome", "Create", "contact", 1, 1, null, null);
                t.OperationType = 2;
                if (i % 4 == 0) t.ExecutionDurationMs = null;
            }

            return g.Traces;
        }

        public static OrganizationInfo Organization() => new OrganizationInfo
        {
            ConnectionName = "Contoso UAT (synthetic)",
            OrganizationName = "contoso-uat",
            Url = "https://contoso-uat.crm.dynamics.com",
            EnvironmentId = "00000000-0000-4000-8000-0000000000a1",
            OrganizationVersion = "9.2.26093.00150",
            TraceSetting = "All",
        };

        public static AnalysisResult Analyze(AnalysisOptions options = null)
        {
            options = options ?? new AnalysisOptions();
            var current = new TraceQuery { FromUtc = From, ToUtc = To, ResultLimit = 5000 };
            var previous = new TraceQuery { FromUtc = From.AddHours(-24), ToUtc = From.AddSeconds(-1), ResultLimit = 5000 };
            var engine = new AnalysisEngine(options);
            var baseResult = new AnalysisEngine(options).Analyze(Build(previous.FromUtc, true, 7), previous, Organization(), null, From);
            var baseline = BaselineService.CreateSnapshot(baseResult, engine.GroupingSignature, BaselineService.SourcePreviousPeriod);
            return engine.Analyze(Build(From, false, 42), current, Organization(), baseline, To.AddMinutes(5));
        }

        /// <summary>Writes the sample HTML, JSON and CSV reports (redacted) to a folder.</summary>
        public static void Write(string folder)
        {
            Directory.CreateDirectory(folder);
            var result = Analyze();
            var options = new ReportOptions
            {
                ToolVersion = "Plugin Failure Correlation Explorer 1.2026.10.1 (sample, synthetic data)",
                Redaction = new RedactionOptions { CustomValues = new List<string> { "Northwind" } },
            };
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(folder, "sample-report.html"), HtmlReportWriter.Write(result, options), utf8);
            File.WriteAllText(Path.Combine(folder, "sample-report.json"), JsonReportWriter.Write(result, options), utf8);
            File.WriteAllText(Path.Combine(folder, "sample-findings.csv"), CsvReportWriter.Findings(result, options), CsvReportWriter.Utf8WithBom);
            Console.WriteLine($"Wrote sample reports to {folder}: {result.Summary.TotalTraces} traces, {result.Findings.Count} findings.");
        }
    }

    public static class SampleScenarioTests
    {
        [Test]
        public static void End_to_end_scenario_produces_the_expected_findings()
        {
            var r = SampleScenario.Analyze();
            var rules = r.Findings.Select(f => f.RuleId).Distinct().OrderBy(x => x).ToList();
            foreach (var expected in new[] { "PFC001", "PFC003", "PFC004", "PFC005", "PFC006", "PFC007", "PFC008", "PFC009" })
                Assert.True(rules.Contains(expected), "missing " + expected + " in " + string.Join(",", rules));
            Assert.True(r.Trend.Available, r.Trend.UnavailableReason);
            Assert.True(r.Findings.Any(f => f.Severity == Severity.Error && f.RuleId == "PFC003"), "near-timeout step is an error");

            // Validation failures with different names and numbers group together
            var validation = r.FailureGroups.Single(g => g.Fingerprint.Message.StartsWith("Account name is required"));
            Assert.Equal(18, validation.Occurrences);
            Assert.Equal(TrendStatus.Increased, r.Trend.Failures.Single(t => t.Key == validation.Key).Status);
        }

        [Test]
        public static void Scenario_is_deterministic()
        {
            var a = SampleScenario.Analyze();
            var b = SampleScenario.Analyze();
            Assert.Equal(string.Join(",", a.Findings.Select(f => f.Id + f.Title)), string.Join(",", b.Findings.Select(f => f.Id + f.Title)));
            Assert.Equal(HtmlReportWriter.Write(a), HtmlReportWriter.Write(b));
        }

        [Test]
        public static void Sample_report_leaks_no_sensitive_values()
        {
            var options = new ReportOptions { Redaction = new RedactionOptions { CustomValues = new List<string> { "Northwind" } } };
            var html = HtmlReportWriter.Write(SampleScenario.Analyze(), options);
            Assert.DoesNotContain("Northwind", html);
            Assert.DoesNotContain("sig=abc123", html);
            Assert.DoesNotContain("ERP responded", html);   // trace text is never exported
        }
    }
}
