using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DiagnosticsWorkbench.Analysis;
using DiagnosticsWorkbench.Models;
using static DiagnosticsWorkbench.Tests.Samples;

namespace DiagnosticsWorkbench.Tests
{
    public static class NormalizerTests
    {
        [Test]
        public static void Headline_from_organization_service_fault()
        {
            var h = ExceptionNormalizer.Headline(FaultException("Account name is required."));
            Assert.Equal("System.ServiceModel.FaultException`1[Microsoft.Xrm.Sdk.OrganizationServiceFault]: Account name is required. [ErrorCode -2147220891]", h);
        }

        [Test]
        public static void Headline_from_plain_dotnet_exception_drops_stack_frames()
        {
            var h = ExceptionNormalizer.Headline(DotNetException("Microsoft.Xrm.Sdk.InvalidPluginExecutionException", "Credit limit exceeded"));
            Assert.Equal("Microsoft.Xrm.Sdk.InvalidPluginExecutionException: Credit limit exceeded", h);
            Assert.DoesNotContain("at Contoso", h);
        }

        [Test]
        public static void Headline_handles_message_on_next_line_and_empty_input()
        {
            Assert.Equal("", ExceptionNormalizer.Headline(null));
            Assert.Equal("", ExceptionNormalizer.Headline("   "));
            var h = ExceptionNormalizer.Headline("Exception type: System.TimeoutException\nMessage:\n   The operation timed out.\n");
            Assert.Equal("System.TimeoutException: The operation timed out.", h);
        }

        [Test]
        public static void Headline_of_unstructured_text_is_first_meaningful_line()
        {
            Assert.Equal("Something odd happened", ExceptionNormalizer.Headline("\n\nSomething odd happened\nmore detail"));
        }

        [Test]
        public static void Normalizes_variable_parts()
        {
            var n = new ExceptionNormalizer();
            var text = n.NormalizeText("Record 3fa85f64-5717-4562-b3fc-2c963f66afa6 for 'Contoso Ltd' failed at 2026-10-21T08:35:09Z (attempt 12345, \"retry\") https://x.crm.dynamics.com/api user a@b.com");
            Assert.Equal("record <guid> for <str> failed at <datetime> (attempt <n>, <str>) <url> user <email>", text);
        }

        [Test]
        public static void Keeps_hex_codes_short_numbers_and_error_codes()
        {
            var n = new ExceptionNormalizer();
            Assert.Equal("error 0x80040217 at step 2", n.NormalizeText("Error 0x80040217 at step 2"));
            Assert.Contains("[errorcode -2147220891]", n.Normalize(FaultException("Order 5512 failed")));
            Assert.Contains("order <n> failed", n.Normalize(FaultException("Order 5512 failed")));
        }

        [Test]
        public static void Normalizes_locale_dates_and_times()
        {
            var n = new ExceptionNormalizer();
            Assert.Equal("due <datetime> passed", n.NormalizeText("Due 21/10/2026 14:05:09 passed"));
            Assert.Equal("at <datetime>", n.NormalizeText("at 10/21/2026 2:05 PM"));
        }

        [Test]
        public static void Same_failure_on_different_records_has_one_fingerprint()
        {
            var n = new ExceptionNormalizer();
            var a = Log(exception: FaultException($"Account {Guid.NewGuid()} has 3 open cases"));
            var b = Log(exception: FaultException($"Account {Guid.NewGuid()} has 3 open cases"));
            var c = Log(exception: FaultException("A different failure"));
            var d = Log(type: "Contoso.Plugins.Other", exception: a.ExceptionDetails);
            Assert.Equal(n.Fingerprint(a), n.Fingerprint(b));
            Assert.NotEqual(n.Fingerprint(a), n.Fingerprint(c));
            Assert.NotEqual(n.Fingerprint(a), n.Fingerprint(d), "plugin type is part of the fingerprint");
            Assert.Equal(12, n.Fingerprint(a).Length);
        }

        [Test]
        public static void Custom_rules_run_first_and_invalid_rules_are_reported()
        {
            var n = new ExceptionNormalizer(new[]
            {
                new NormalizationRule { Name = "order", Pattern = @"ORD-\d+", Token = "<order>" },
                new NormalizationRule { Name = "broken", Pattern = "([", Token = "x" },
                new NormalizationRule { Name = "off", Pattern = "Contoso", Token = "<x>", Enabled = false },
            });
            Assert.Equal("order <order> rejected by contoso", n.NormalizeText("Order ORD-7 rejected by Contoso"));
            Assert.True(n.InvalidRules.ContainsKey("broken"));
        }
    }

    public static class GroupingTests
    {
        [Test]
        public static void Groups_by_fingerprint_with_counts_dates_and_trend()
        {
            var corr = Guid.NewGuid();
            var logs = new List<TraceLog>
            {
                Log(minutes: 0, exception: FaultException($"Account {Guid.NewGuid()} is locked"), correlation: corr),
                Log(minutes: 5, exception: FaultException($"Account {Guid.NewGuid()} is locked"), correlation: corr),
                Log(minutes: 60 * 26, exception: FaultException($"Account {Guid.NewGuid()} is locked"), correlation: Guid.NewGuid()),
                Log(minutes: 10, exception: DotNetException("System.NullReferenceException", "Object reference not set to an instance of an object.")),
                Log(minutes: 11),
            };
            var groups = ErrorGrouper.Group(logs, new ExceptionNormalizer());
            Assert.Equal(2, groups.Count);
            var top = groups[0];
            Assert.Equal(3, top.Count);
            Assert.Equal(2, top.DistinctExecutions);
            Assert.Equal(T0, top.FirstSeen);
            Assert.Equal(T0.AddMinutes(60 * 26), top.LastSeen);
            Assert.Equal(2, top.HitsPerDay.Count);
            Assert.Equal(2, top.HitsPerDay[T0.Date]);
            Assert.Equal(T0.AddMinutes(60 * 26), top.Logs[0].CreatedOn, "newest first");
            Assert.Contains("is locked", top.Headline);
        }

        [Test]
        public static void Signature_cache_is_used_on_regroup()
        {
            var cache = new Dictionary<Guid, ErrorGrouper.Signature>();
            var logs = new List<TraceLog> { Log(exception: FaultException("x")), Log(exception: FaultException("y")) };
            ErrorGrouper.Group(logs, new ExceptionNormalizer(), cache);
            Assert.Equal(2, cache.Count);
            cache[logs[0].Id].Fingerprint = "cached";
            var groups = ErrorGrouper.Group(logs, new ExceptionNormalizer(), cache);
            Assert.True(groups.Any(g => g.Fingerprint == "cached"));
        }
    }

    public static class PerformanceTests
    {
        [Test]
        public static void Threshold_resolution_is_most_specific_first()
        {
            var settings = new WorkbenchSettings
            {
                SyncThresholdMs = 2000,
                AsyncThresholdMs = 10000,
                ThresholdOverrides =
                {
                    new ThresholdOverride { TypeName = "AccountPreCreate", ThresholdMs = 500 },
                    new ThresholdOverride { TypeName = "Contoso.Plugins.AccountPreCreate", MessageName = "update", ThresholdMs = 300 },
                }
            };
            var p = new PerformanceAnalyzer(settings);
            Assert.Equal(300, p.ThresholdFor(Log(message: "Update")), "type + message");
            Assert.Equal(500, p.ThresholdFor(Log(message: "Create")), "type (short name)");
            Assert.Equal(2000, p.ThresholdFor(Log(type: "Other.Plugin")), "sync default");
            Assert.Equal(10000, p.ThresholdFor(Log(type: "Other.Plugin", mode: 1)), "async default");
        }

        [Test]
        public static void Slow_and_near_timeout()
        {
            var p = new PerformanceAnalyzer(new WorkbenchSettings());
            Assert.False(p.IsSlow(Log(duration: 2000)), "equal to threshold is not slow");
            Assert.True(p.IsSlow(Log(duration: 2001)));
            Assert.False(p.IsSlow(Log(duration: null)));
            Assert.True(p.IsNearTimeout(Log(duration: 65000)));
            Assert.False(p.IsNearTimeout(Log(duration: 65000, mode: 1)), "async runs have no 2-minute limit");
        }

        [Test]
        public static void Percentiles_nearest_rank()
        {
            var values = Enumerable.Range(1, 100).ToList();
            Assert.Equal(50, PerformanceAnalyzer.Percentile(values, 50));
            Assert.Equal(95, PerformanceAnalyzer.Percentile(values, 95));
            Assert.Equal(100, PerformanceAnalyzer.Percentile(values, 100));
            Assert.Equal(7, PerformanceAnalyzer.Percentile(new[] { 7 }, 95));
            Assert.Equal(0, PerformanceAnalyzer.Percentile(new int[0], 50));
        }

        [Test]
        public static void Stats_per_type_and_message_sorted_by_severity()
        {
            var logs = new List<TraceLog>();
            for (var i = 0; i < 20; i++) logs.Add(Log(type: "Fast.Plugin", duration: 50 + i));
            for (var i = 0; i < 10; i++) logs.Add(Log(type: "Slow.Plugin", message: "Update", duration: 1000 * (i + 1)));
            logs.Add(Log(type: "Slow.Plugin", message: "Update", duration: null));
            logs.Add(Log(type: "Hang.Plugin", duration: 90000, exception: "System.TimeoutException: x"));

            var stats = new PerformanceAnalyzer(new WorkbenchSettings()).Analyze(logs);
            Assert.Equal(3, stats.Count);
            Assert.Equal("Hang.Plugin", stats[0].TypeName, "near-timeout first");
            Assert.Equal(1, stats[0].NearTimeoutCount);
            Assert.Equal(1, stats[0].ErrorCount);
            var slow = stats[1];
            Assert.Equal("Slow.Plugin", slow.TypeName);
            Assert.Equal(10, slow.Count, "runs without a duration are skipped");
            Assert.Equal(8, slow.SlowCount);
            Assert.Equal(5000, slow.P50Ms);
            Assert.Equal(10000, slow.MaxMs);
            Assert.Equal(10000, slow.Logs[0].ExecutionDurationMs.Value, "slowest first");
            Assert.Equal(0, stats[2].SlowCount);
        }
    }

    public static class CorrelationTests
    {
        [Test]
        public static void Builds_ordered_chains_and_ignores_logs_without_correlation()
        {
            var c1 = Guid.NewGuid();
            var c2 = Guid.NewGuid();
            var logs = new List<TraceLog>
            {
                Log(type: "B", minutes: 1, depth: 2, correlation: c1, duration: 300),
                Log(type: "A", minutes: 0, depth: 1, correlation: c1, duration: 100),
                Log(type: "C", minutes: 2, depth: 3, correlation: c1, exception: "System.Exception: boom", request: Guid.NewGuid()),
                Log(type: "D", minutes: 5, correlation: c2),
                Log(type: "E", minutes: 6, correlation: null),
            };
            var chains = CorrelationBuilder.Build(logs);
            Assert.Equal(2, chains.Count);
            var first = chains[0];
            Assert.Equal(c1, first.CorrelationId, "failed chain first");
            Assert.True(first.Failed);
            Assert.Equal(1, first.ErrorCount);
            Assert.Equal(3, first.MaxDepth);
            Assert.Equal("A,B,C", string.Join(",", first.Entries.Select(e => e.TypeName)));
            Assert.Equal("A", first.Root.TypeName);
            Assert.Equal(1, first.RequestCount);
            Assert.True(first.SpanMs >= 120000, "span covers first start to last end");
            Assert.Equal(1, CorrelationBuilder.Build(logs, minimumEntries: 2).Count);
        }
    }

    public static class FlagTests
    {
        [Test]
        public static void Step_flags()
        {
            var id = Guid.NewGuid();
            var step = Step(id, "AccountSync: Update of account", "Update", null,
                new StepImage { Name = "PreImage", ImageType = 0, Attributes = "" },
                new StepImage { Name = "PostImage", ImageType = 1, Attributes = "name,revenue" });
            var flags = ConfigFlagger.ForStep(step);
            Assert.Equal(2, flags.Count);
            Assert.True(flags.Any(f => f.Kind == ConfigFlagKind.UpdateWithoutFilteringAttributes));
            Assert.True(flags.Any(f => f.Kind == ConfigFlagKind.ImageWithAllColumns && f.Message.Contains("PreImage")));

            Assert.Equal(0, ConfigFlagger.ForStep(Step(id, "ok", "Update", "name")).Count);
            Assert.Equal(0, ConfigFlagger.ForStep(Step(id, "create", "Create", null)).Count, "Create steps have no filtering attributes");
            var disabled = Step(id, "off", "Create");
            disabled.StateCode = 1;
            Assert.Equal(ConfigFlagKind.StepDisabled, ConfigFlagger.ForStep(disabled).Single().Kind);
            Assert.Equal(0, ConfigFlagger.ForStep(null).Count);
        }

        [Test]
        public static void Recursion_flag_and_propagation_to_groups()
        {
            var stepId = Guid.NewGuid();
            var corr = Guid.NewGuid();
            var logs = new List<TraceLog>
            {
                Log(minutes: 0, depth: 1, correlation: corr, step: stepId),
                Log(minutes: 1, depth: 2, correlation: corr, step: stepId),
                Log(minutes: 2, depth: 3, correlation: corr, step: stepId, exception: "System.InvalidOperationException: This workflow job was canceled because the workflow that started it included an infinite loop."),
            };
            var steps = new Dictionary<Guid, StepInfo> { [stepId] = Step(stepId, "AccountSync", "Update", null) };
            var chains = CorrelationBuilder.Build(logs);
            var groups = ErrorGrouper.Group(logs, new ExceptionNormalizer());
            var stats = new PerformanceAnalyzer(new WorkbenchSettings()).Analyze(logs);
            ConfigFlagger.Apply(steps, groups, stats, chains);

            Assert.True(chains[0].Flags.Any(f => f.Kind == ConfigFlagKind.PossibleRecursion && f.Message.Contains("1, 2, 3")));
            Assert.True(chains[0].Flags.Any(f => f.Kind == ConfigFlagKind.UpdateWithoutFilteringAttributes));
            Assert.True(groups[0].Flags.Any(f => f.Kind == ConfigFlagKind.PossibleRecursion));
            Assert.True(stats[0].Flags.Any(f => f.Kind == ConfigFlagKind.UpdateWithoutFilteringAttributes));
            Assert.Equal(groups[0].Flags.Count, groups[0].Flags.Distinct().Count(), "no duplicate flags");
        }

        [Test]
        public static void No_recursion_when_step_runs_once_per_depth_one()
        {
            var stepId = Guid.NewGuid();
            var corr = Guid.NewGuid();
            var chain = CorrelationBuilder.Build(new[] { Log(depth: 1, correlation: corr, step: stepId), Log(minutes: 1, depth: 1, correlation: corr, step: stepId) })[0];
            Assert.Equal(0, ConfigFlagger.ForChain(chain, null).Count);
        }
    }

    public static class SessionTests
    {
        private static DiagnosticsSession Loaded(out Guid corr)
        {
            corr = Guid.NewGuid();
            var session = new DiagnosticsSession();
            session.SetLogs(new[]
            {
                Log(type: "A", minutes: 0, correlation: corr, duration: 100),
                Log(type: "B", minutes: 1, correlation: corr, depth: 2, duration: 5000, exception: FaultException("Credit check failed for 'Fabrikam'")),
                Log(type: "C", minutes: 2, duration: 3000, block: "Calling ERP endpoint"),
                Log(type: "D", minutes: 3, duration: 10),
            }, new QueryDescription { FromUtc = T0.AddDays(-1), ToUtc = T0.AddDays(1), RowCap = 50000 });
            return session;
        }

        [Test]
        public static void Filters_apply_without_reload()
        {
            var session = Loaded(out _);
            Assert.Equal(4, session.Analyze().Logs.Count);
            Assert.Equal(1, session.Analyze(new ViewFilter { ErrorsOnly = true }).Logs.Count);
            Assert.Equal(2, session.Analyze(new ViewFilter { SlowOnly = true }).Logs.Count);
            Assert.Equal("C", session.Analyze(new ViewFilter { Search = "erp ENDPOINT" }).Logs.Single().TypeName);
            Assert.Equal("B", session.Analyze(new ViewFilter { Search = "fabrikam" }).Logs.Single().TypeName);
        }

        [Test]
        public static void Chains_keep_all_entries_when_filtered()
        {
            var session = Loaded(out var corr);
            var result = session.Analyze(new ViewFilter { ErrorsOnly = true });
            Assert.Equal(1, result.Chains.Count);
            Assert.Equal(2, result.Chains[0].Entries.Count, "the successful parent stays in the chain");
            Assert.Equal(corr, result.Chains[0].CorrelationId);
        }

        [Test]
        public static void Summary_counts_and_settings_change_regroups()
        {
            var session = Loaded(out _);
            var result = session.Analyze();
            Assert.Equal(1, result.ErrorCount);
            Assert.Equal(2, result.SlowCount);
            Assert.Equal(0, result.NearTimeoutCount);
            Assert.Equal(1, result.FailedChainCount);

            session.Settings = new WorkbenchSettings { SyncThresholdMs = 50 };
            Assert.Equal(3, session.Analyze().SlowCount);
        }

        [Test]
        public static void Settings_are_normalized_and_cloned()
        {
            var raw = new WorkbenchSettings { SyncThresholdMs = -1, RowCap = 0, ThresholdOverrides = null, CustomRules = null };
            var session = new DiagnosticsSession { Settings = raw };
            Assert.Equal(WorkbenchSettings.DefaultSyncThresholdMs, session.Settings.SyncThresholdMs);
            Assert.Equal(WorkbenchSettings.DefaultRowCap, session.Settings.RowCap);
            Assert.True(session.Settings.ThresholdOverrides != null && session.Settings.CustomRules != null);
            Assert.False(ReferenceEquals(raw, session.Settings));
        }

        [Test]
        public static void Analyzes_fifty_thousand_logs_quickly()
        {
            var random = new Random(7);
            var logs = new List<TraceLog>(50000);
            var messages = new[] { "Account {0} is locked", "Credit limit of {1} exceeded for '{2}'", "Timeout calling https://erp.contoso.com/orders/{1}" };
            for (var i = 0; i < 50000; i++)
            {
                var error = i % 3 == 0;
                var text = string.Format(messages[(i / 3) % messages.Length], Guid.NewGuid(), random.Next(1000, 99999), "Customer " + random.Next(100));
                logs.Add(Log(type: "Plugin" + (i % 40), message: i % 2 == 0 ? "Create" : "Update", minutes: i % 10000,
                    duration: random.Next(10, 8000), exception: error ? FaultException(text) : null,
                    correlation: i % 5 == 0 ? (Guid?)null : new Guid(i / 4 + 1, 0, 0, new byte[8]), depth: i % 4 + 1));
            }
            var session = new DiagnosticsSession();
            session.SetLogs(logs, new QueryDescription());
            var sw = Stopwatch.StartNew();
            var result = session.Analyze();
            var first = sw.ElapsedMilliseconds;
            sw.Restart();
            session.Analyze(new ViewFilter { ErrorsOnly = true });
            var second = sw.ElapsedMilliseconds;
            Console.WriteLine($"        50,000 logs: first analysis {first} ms, cached re-group {second} ms, {result.ErrorGroups.Count} groups, {result.Chains.Count} chains");
            Assert.Equal(120, result.ErrorGroups.Count, "40 types x 3 templates: GUIDs, numbers and names collapse");
            Assert.True(first < 15000, "first analysis under 15 s");
            Assert.True(second < first, "re-grouping uses the cache");
        }
    }
}

namespace DiagnosticsWorkbench.Tests
{
    public static class AssemblyQualifiedNameTests
    {
        private const string V1 = "Contoso.Plugins.AccountSync, Contoso.Plugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
        private const string V2 = "Contoso.Plugins.AccountSync, Contoso.Plugins, Version=2.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";

        [Test]
        public static void Type_names_drop_the_assembly_qualifier()
        {
            var log = Samples.Log(type: V1);
            Assert.Equal("Contoso.Plugins.AccountSync", log.FullTypeName);
            Assert.Equal("AccountSync", log.ShortTypeName);
            Assert.Equal("Plain.Type", Models.TraceLog.StripAssembly("Plain.Type"));
        }

        [Test]
        public static void Fingerprint_survives_a_new_assembly_version()
        {
            var n = new ExceptionNormalizer();
            var a = Samples.Log(type: V1, exception: Samples.FaultException("x failed"));
            var b = Samples.Log(type: V2, exception: Samples.FaultException("x failed"));
            Assert.Equal(n.Fingerprint(a), n.Fingerprint(b));
            Assert.Equal(1, ErrorGrouper.Group(new[] { a, b }, n).Count);
            Assert.Equal("Contoso.Plugins.AccountSync", ErrorGrouper.Group(new[] { a }, n)[0].TypeName);
        }

        [Test]
        public static void Overrides_and_stats_use_the_plain_type_name()
        {
            var settings = new Models.WorkbenchSettings();
            settings.ThresholdOverrides.Add(new Models.ThresholdOverride { TypeName = "Contoso.Plugins.AccountSync", ThresholdMs = 100 });
            var p = new PerformanceAnalyzer(settings);
            Assert.Equal(100, p.ThresholdFor(Samples.Log(type: V1)));
            var stats = p.Analyze(new[] { Samples.Log(type: V1, duration: 50), Samples.Log(type: V2, duration: 500) });
            Assert.Equal(1, stats.Count);
            Assert.Equal("Contoso.Plugins.AccountSync", stats[0].TypeName);
        }

        [Test]
        public static void Single_executions_are_not_listed_as_chains()
        {
            var session = new DiagnosticsSession();
            var c = System.Guid.NewGuid();
            session.SetLogs(new[]
            {
                Samples.Log(correlation: System.Guid.NewGuid()),
                Samples.Log(correlation: System.Guid.NewGuid(), exception: "System.Exception: boom"),
                Samples.Log(correlation: c), Samples.Log(correlation: c, depth: 2, minutes: 1),
            }, new QueryDescription());
            var chains = session.Analyze().Chains;
            Assert.Equal(1, chains.Count, "only the two-step chain; single executions (even failed) are in Errors");
            Assert.Equal(c, chains[0].CorrelationId);
        }
    }
}

namespace DiagnosticsWorkbench.Tests
{
    public static class LiveTestFindingsTests
    {
        [Test]
        public static void Display_headline_drops_namespaces_and_fault_wrapper()
        {
            var raw = "System.ServiceModel.FaultException`1[Microsoft.Xrm.Sdk.OrganizationServiceFault]: Sync to ERP failed at depth 3.";
            Assert.Equal("OrganizationServiceFault: Sync to ERP failed at depth 3.", Export.Formatting.DisplayHeadline(raw));
            Assert.Equal("Sync to ERP failed at depth 3.", Export.Formatting.HeadlineMessage(raw));
            Assert.Equal("OrganizationServiceFault", Export.Formatting.HeadlineType(raw));
            Assert.Equal("NullReferenceException: Object reference not set.", Export.Formatting.DisplayHeadline("System.NullReferenceException: Object reference not set."));
            Assert.Equal("Plain message without type", Export.Formatting.HeadlineMessage("Plain message without type"));
            Assert.Equal("Value 1.5 is not valid", Export.Formatting.DisplayHeadline("Value 1.5 is not valid"));
        }

        [Test]
        public static void Recursion_flag_only_on_groups_that_recursed()
        {
            var stepId = System.Guid.NewGuid();
            var chain = System.Guid.NewGuid();
            var logs = new System.Collections.Generic.List<Models.TraceLog>
            {
                Samples.Log(correlation: chain, depth: 1, step: stepId),
                Samples.Log(correlation: chain, depth: 2, step: stepId, minutes: 1),
                Samples.Log(correlation: chain, depth: 3, step: stepId, minutes: 2, exception: "System.Exception: deep failure"),
                Samples.Log(correlation: System.Guid.NewGuid(), depth: 1, step: stepId, exception: "System.NullReferenceException: Object reference not set."),
            };
            var steps = new System.Collections.Generic.Dictionary<System.Guid, Models.StepInfo> { [stepId] = Samples.Step(stepId, "Sync", "Update", null) };
            var session = new DiagnosticsSession();
            session.SetLogs(logs, new QueryDescription());
            session.SetSteps(steps.Values);
            var result = session.Analyze();
            var deep = result.ErrorGroups.Single(g => g.Headline.Contains("deep failure"));
            var nre = result.ErrorGroups.Single(g => g.Headline.Contains("Object reference"));
            Assert.True(deep.Flags.Any(f => f.Kind == Models.ConfigFlagKind.PossibleRecursion));
            Assert.False(nre.Flags.Any(f => f.Kind == Models.ConfigFlagKind.PossibleRecursion), "the single execution never recursed");
            Assert.True(nre.Flags.Any(f => f.Kind == Models.ConfigFlagKind.UpdateWithoutFilteringAttributes), "registration flags still apply");
        }

        [Test]
        public static void Performance_splits_sync_and_async_runs()
        {
            var stats = new PerformanceAnalyzer(new Models.WorkbenchSettings()).Analyze(new[]
            {
                Samples.Log(duration: 3000, mode: 0), Samples.Log(duration: 12000, mode: 1), Samples.Log(duration: 9000, mode: 1),
            });
            Assert.Equal(2, stats.Count);
            var sync = stats.Single(s => s.ModeText == "Sync");
            var async = stats.Single(s => s.ModeText == "Async");
            Assert.Equal(1, sync.SlowCount);
            Assert.Equal(2000, sync.ThresholdMs);
            Assert.Equal(1, async.SlowCount);
            Assert.Equal(10000, async.ThresholdMs);
        }
    }
}
