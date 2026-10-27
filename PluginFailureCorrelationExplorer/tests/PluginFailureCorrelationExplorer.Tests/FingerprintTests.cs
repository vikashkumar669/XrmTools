using System;
using System.Linq;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;
using static PluginFailureCorrelationExplorer.Tests.Samples;

namespace PluginFailureCorrelationExplorer.Tests
{
    public static class NormalizerTests
    {
        private static string N(string text) => new TraceTextNormalizer().NormalizeText(text);

        [Test]
        public static void Guids_with_and_without_braces_become_tokens()
        {
            Assert.Equal("record <guid> and <guid> missing", N("Record 3F2504E0-4F89-11D3-9A0C-0305E82C3301 and {3f2504e0-4f89-11d3-9a0c-0305e82c3302} missing"));
        }

        [Test]
        public static void Iso_locale_and_time_values_become_datetime_tokens()
        {
            Assert.Equal("at <datetime>", N("at 2026-10-20T09:15:22.123Z"));
            Assert.Equal("at <datetime>", N("at 2026-10-20 09:15:22+05:30"));
            Assert.Equal("on <datetime>", N("on 10/20/2026 9:15:22 AM"));
            Assert.Equal("on <datetime>", N("on 20.10.2026"));
            Assert.Equal("since <datetime>", N("since 14:03:22"));
        }

        [Test]
        public static void Quoted_values_urls_emails_and_reference_numbers_are_replaced()
        {
            Assert.Equal("account <str> not found", N("Account 'Contoso Ltd' not found"));
            Assert.Equal("value <str> invalid", N("Value \"ABC-123\" invalid"));
            Assert.Equal("call to <url> failed", N("Call to https://api.contoso.example/v1/orders?id=7 failed"));
            Assert.Equal("owner <email>", N("Owner someone@contoso.example"));
            Assert.Equal("an error occurred. reference number: #<ref>", N("An error occurred. Reference number: #8A1B2C3D"));
        }

        [Test]
        public static void Hex_error_codes_and_parenthesized_status_codes_are_kept()
        {
            Assert.Equal("failed with 0x80040217", N("Failed with 0x80040217"));
            Assert.Equal("the remote server returned an error: (404) not found.", N("The remote server returned an error: (404) Not Found."));
            Assert.NotEqual(N("returned (404)"), N("returned (500)"));
        }

        [Test]
        public static void Numbers_with_two_or_more_digits_are_replaced_but_single_digits_and_identifiers_kept()
        {
            Assert.Equal("quantity <n> exceeds limit 5 in int32", N("Quantity 1500 exceeds limit 5 in Int32"));
            Assert.Equal("amount <n>", N("Amount 12.50"));
        }

        [Test]
        public static void Whitespace_is_collapsed_and_text_lowercased()
        {
            Assert.Equal("a b c", N("  A \r\n\t B   C "));
        }

        [Test]
        public static void Applied_rules_are_reported_in_order()
        {
            var r = new TraceTextNormalizer().Normalize("Record 3F2504E0-4F89-11D3-9A0C-0305E82C3301 at 2026-10-20 owned by 'x'");
            Assert.Equal("guid,iso-datetime,single-quoted", string.Join(",", r.RulesApplied));
        }

        [Test]
        public static void Custom_rules_run_first_and_invalid_rules_are_reported_not_fatal()
        {
            var normalizer = new TraceTextNormalizer(new[]
            {
                new NormalizationRule { Name = "order", Pattern = @"ORD-[A-Z]{3}", Token = "<order>" },
                new NormalizationRule { Name = "broken", Pattern = "([unclosed" },
                new NormalizationRule { Name = "disabled", Pattern = "Order", Enabled = false },
            });
            Assert.Equal("order <order> failed", normalizer.NormalizeText("Order ORD-XYZ failed"));
            Assert.True(normalizer.InvalidRules.ContainsKey("broken"), "invalid rule reported");
            Assert.Equal(1, normalizer.CustomRules.Count);
        }

        [Test]
        public static void Empty_and_null_text_normalize_to_empty()
        {
            Assert.Equal("", N(null));
            Assert.Equal("", N(""));
        }
    }

    public static class ExceptionParserTests
    {
        [Test]
        public static void Dataverse_fault_layout_is_parsed()
        {
            var p = ExceptionParser.Parse(Fault("Account name is required.", "-2147220891"));
            Assert.Equal("System.ServiceModel.FaultException`1[Microsoft.Xrm.Sdk.OrganizationServiceFault]", p.ExceptionType);
            Assert.Equal("Account name is required.", p.Message);
            Assert.Equal("-2147220891", p.ErrorCode);
        }

        [Test]
        public static void Inner_exception_type_is_taken_from_the_fault_message()
        {
            var p = ExceptionParser.Parse(Fault("Object reference not set to an instance of an object.", "-2147220956",
                "System.NullReferenceException: Microsoft Dynamics CRM has experienced an error. Reference number for administrators or support: #1A2B3C4D"));
            Assert.Equal("System.NullReferenceException", p.InnerExceptionType);
            Assert.Equal("System.NullReferenceException", p.DisplayType);
        }

        [Test]
        public static void Plain_dotnet_text_and_frames_are_parsed()
        {
            var p = ExceptionParser.Parse(DotNet("System.InvalidOperationException", "Sequence contains no elements"));
            Assert.Equal("System.InvalidOperationException", p.ExceptionType);
            Assert.Equal("Sequence contains no elements", p.Message);
            Assert.Equal(2, p.Frames.Count);
            Assert.Equal(1, p.UserFrames.Count);
            Assert.Equal("Contoso.Plugins.AccountPreCreate.Execute(IServiceProvider serviceProvider)", p.UserFrames[0]);
        }

        [Test]
        public static void Compiler_generated_names_are_normalized_in_frames()
        {
            Assert.Equal("Contoso.Plugins.X.<>c__DisplayClass.<Run>b__(Entity e)",
                ExceptionParser.NormalizeFrame("Contoso.Plugins.X.<>c__DisplayClass12_0.<Run>b__3(Entity e)"));
        }

        [Test]
        public static void Message_on_the_following_line_is_found()
        {
            var p = ExceptionParser.Parse("Exception type: System.Exception\r\nMessage:\r\n\r\n  Something broke\r\n");
            Assert.Equal("Something broke", p.Message);
        }

        [Test]
        public static void Empty_input_gives_an_empty_result()
        {
            var p = ExceptionParser.Parse("  ");
            Check.Null(p.ExceptionType);
            Check.Null(p.Message);
            Assert.Equal(0, p.Frames.Count);
        }
    }

    public static class FingerprintTests
    {
        private static ExceptionFingerprintService Service(AnalysisOptions o = null) => new ExceptionFingerprintService(o ?? new AnalysisOptions());

        [Test]
        public static void Same_failure_with_different_volatile_values_has_the_same_fingerprint()
        {
            var s = Service();
            var a = s.Fingerprint(Fault("Account 'Contoso' with id 3F2504E0-4F89-11D3-9A0C-0305E82C3301 failed at 2026-10-20T09:00:00Z"), AccountPlugin);
            var b = s.Fingerprint(Fault("Account 'Fabrikam' with id 9A2504E0-4F89-11D3-9A0C-0305E82C3399 failed at 2026-10-21T17:45:10Z"), AccountPlugin);
            Assert.Equal(a.Value, b.Value);
            Assert.Equal(16, a.Value.Length);
        }

        [Test]
        public static void Fingerprint_is_stable_across_runs()
        {
            var fp = Service().Fingerprint(DotNet("System.InvalidOperationException", "Sequence contains no elements"), AccountPlugin);
            // Pinned value: a change here means every saved baseline becomes incomparable, so bump TraceTextNormalizer.Version.
            Assert.Equal(fp.Value, Service().Fingerprint(DotNet("System.InvalidOperationException", "Sequence contains no elements"), AccountPlugin).Value);
            Assert.Equal("v1|type=system.invalidoperationexception|code=|msg=sequence contains no elements|plugin=contoso.plugins.accountprecreate|frames=contoso.plugins.accountprecreate.execute(iserviceprovider serviceprovider)", fp.FingerprintInput);
        }

        [Test]
        public static void Generic_message_in_different_plugins_is_not_merged()
        {
            var s = Service();
            const string nre = "Object reference not set to an instance of an object.";
            var a = s.Fingerprint(DotNet("System.NullReferenceException", nre, "Contoso.Plugins.AccountPreCreate.Execute(IServiceProvider sp)"), AccountPlugin);
            var b = s.Fingerprint(DotNet("System.NullReferenceException", nre, "Contoso.Plugins.ContactSync.Execute(IServiceProvider sp)"), ContactPlugin);
            Assert.NotEqual(a.Value, b.Value);
        }

        [Test]
        public static void Same_plugin_and_message_from_different_code_paths_is_not_merged()
        {
            var s = Service();
            const string nre = "Object reference not set to an instance of an object.";
            var a = s.Fingerprint(DotNet("System.NullReferenceException", nre, "Contoso.Plugins.AccountPreCreate.ValidateName(Entity target)"), AccountPlugin);
            var b = s.Fingerprint(DotNet("System.NullReferenceException", nre, "Contoso.Plugins.AccountPreCreate.SetOwner(Entity target)"), AccountPlugin);
            Assert.NotEqual(a.Value, b.Value);
        }

        [Test]
        public static void Line_numbers_and_assembly_versions_do_not_change_the_fingerprint()
        {
            var s = Service();
            var a = s.Fingerprint(DotNet("System.Exception", "Boom", line: 42), AccountPlugin + ", Contoso.Plugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=abc");
            var b = s.Fingerprint(DotNet("System.Exception", "Boom", line: 57), AccountPlugin + ", Contoso.Plugins, Version=1.0.1.0, Culture=neutral, PublicKeyToken=abc");
            Assert.Equal(a.Value, b.Value);
        }

        [Test]
        public static void Different_error_codes_are_different_fingerprints()
        {
            var s = Service();
            Assert.NotEqual(s.Fingerprint(Fault("Failed", "-2147220891"), AccountPlugin).Value, s.Fingerprint(Fault("Failed", "-2147220969"), AccountPlugin).Value);
        }

        [Test]
        public static void Fingerprinting_never_modifies_the_original_text()
        {
            var text = Fault("Account 'Contoso' failed 3F2504E0-4F89-11D3-9A0C-0305E82C3301");
            var trace = Trace(exception: text);
            var fp = Service().Fingerprint(trace);
            Assert.Equal(text, trace.ExceptionDetails);
            Assert.Equal("Account 'Contoso' failed 3F2504E0-4F89-11D3-9A0C-0305E82C3301", fp.Message);
            Assert.Contains("<guid>", fp.NormalizedMessage);
        }

        [Test]
        public static void Traces_without_exception_have_no_fingerprint()
        {
            Check.Null(Service().Fingerprint(Trace()));
        }

        [Test]
        public static void Grouping_counts_occurrences_and_first_and_latest()
        {
            var traces = Repeat(4, i => Trace(minutes: i * 10, exception: Fault($"Account 'A{i}' failed")));
            traces.Add(Trace(type: ContactPlugin, exception: DotNet("System.TimeoutException", "Timed out")));
            traces.Add(Trace());
            var groups = Service().Group(traces);
            Assert.Equal(2, groups.Count);
            Assert.Equal(4, groups[0].Occurrences);
            Assert.Equal(T0, groups[0].FirstUtc);
            Assert.Equal(T0.AddMinutes(30), groups[0].LastUtc);
            Assert.Equal(Severity.Error, groups[0].Severity);
            Assert.Equal(Severity.Information, groups[1].Severity);
            Assert.Equal(AccountPlugin, groups[0].PluginTypes.Single());
        }

        [Test]
        public static void Recurring_asynchronous_failures_are_warnings()
        {
            var groups = Service().Group(Repeat(3, i => Trace(minutes: i, mode: 1, exception: Fault("x"))));
            Assert.Equal(Severity.Warning, groups.Single().Severity);
        }

        [Test]
        public static void Exception_and_message_mode_merges_across_plugins_and_context_mode_splits_by_table()
        {
            var traces = new[]
            {
                Trace(type: AccountPlugin, table: "account", exception: Fault("Name required")),
                Trace(type: ContactPlugin, table: "contact", exception: Fault("Name required")),
                Trace(type: AccountPlugin, table: "lead", exception: Fault("Name required")),
            };
            Assert.Equal(2, Service().Group(traces).Count);
            Assert.Equal(1, Service(new AnalysisOptions { Grouping = GroupingMode.ExceptionAndMessage }).Group(traces).Count);
            Assert.Equal(3, Service(new AnalysisOptions { Grouping = GroupingMode.FingerprintAndContext }).Group(traces).Count);
        }

        [Test]
        public static void Grouping_signature_changes_with_settings()
        {
            var a = Service().GroupingSignature;
            Assert.Equal(a, Service().GroupingSignature);
            Assert.NotEqual(a, Service(new AnalysisOptions { FingerprintStackFrames = 1 }).GroupingSignature);
            Assert.NotEqual(a, Service(new AnalysisOptions { CustomRules = { new NormalizationRule { Name = "x", Pattern = "X-\\d+" } } }).GroupingSignature);
        }
    }
}
