using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ChartDesignerPro.Abstractions;
using ChartDesignerPro.Explorer;
using ChartDesignerPro.Models;

namespace ChartDesignerPro.Tests
{
    public static class ExplorerTests
    {
        private static readonly Guid PipelineId = new Guid("11111111-2222-3333-4444-555555555555");

        private static List<ChartRecord> Charts() => new List<ChartRecord>
        {
            new ChartRecord { Id = PipelineId, Name = "Pipeline by stage", TableLogicalName = "opportunity", TableDisplayName = "Opportunity",
                              Engine = ChartEngine.AspNetCharts, Scope = ChartScope.System, ModifiedOnUtc = new DateTime(2026, 1, 5) },
            new ChartRecord { Id = Guid.NewGuid(), Name = "Leads by source", TableLogicalName = "lead", TableDisplayName = "Lead",
                              Engine = ChartEngine.AspNetCharts, Scope = ChartScope.Personal, ModifiedOnUtc = new DateTime(2026, 3, 1) },
            new ChartRecord { Id = Guid.NewGuid(), Name = "Sales dashboard", TableLogicalName = "opportunity", TableDisplayName = "Opportunity",
                              Engine = ChartEngine.PowerBI, Scope = ChartScope.System, Description = "Embedded report" },
        };

        [Test]
        public static void SearchMatchesEveryTermAcrossFields()
        {
            var charts = Charts();
            Assert.Equal(2, ChartListFilter.Search(charts, "opportunity").Count(), "by table");
            Assert.Equal(1, ChartListFilter.Search(charts, "opportunity stage").Count(), "two terms");
            Assert.Equal(1, ChartListFilter.Search(charts, "power bi").Count(), "by type");
            Assert.Equal(1, ChartListFilter.Search(charts, "embedded").Count(), "by description");
            Assert.Equal(1, ChartListFilter.Search(charts, "personal").Count(), "by scope");
            Assert.Equal(3, ChartListFilter.Search(charts, "  ").Count(), "blank keeps all");
        }

        [Test]
        public static void SearchMatchesIdWithOrWithoutBraces()
        {
            var charts = Charts();
            Assert.Equal(PipelineId, ChartListFilter.Search(charts, "{" + PipelineId.ToString().ToUpperInvariant() + "}").Single().Id);
            Assert.Equal(PipelineId, ChartListFilter.Search(charts, "11111111-2222").Single().Id);
        }

        [Test]
        public static void SortIsStableAndSupportsDirection()
        {
            var byName = ChartListFilter.Sort(Charts(), ChartSortColumn.Name, false).Select(c => c.Name).ToArray();
            Assert.Equal("Leads by source|Pipeline by stage|Sales dashboard", string.Join("|", byName));

            var byModifiedDesc = ChartListFilter.Sort(Charts(), ChartSortColumn.Modified, true).Select(c => c.Name).ToArray();
            Assert.Equal("Leads by source", byModifiedDesc[0]);
            Assert.Equal("Sales dashboard", byModifiedDesc[2], "missing date sorts last when descending");

            var byTable = ChartListFilter.Sort(Charts(), ChartSortColumn.Table, false).Select(c => c.Name).ToArray();
            Assert.Equal("Leads by source|Pipeline by stage|Sales dashboard", string.Join("|", byTable));
        }

        [Test]
        public static void TablesOfReturnsDistinctTables()
        {
            var tables = ChartListFilter.TablesOf(Charts());
            Assert.Equal(2, tables.Count);
            Assert.Equal("Lead (lead)", tables[0].Text);
            Assert.Equal("Opportunity (opportunity)", tables[1].Text);
        }

        [Test]
        public static void CapabilityBlocksNonChartsAndLockedManagedCharts()
        {
            Assert.False(ChartCapability.For(new ChartRecord { Engine = ChartEngine.PowerBI }).CanEdit, "Power BI");
            Assert.False(ChartCapability.For(new ChartRecord { Engine = ChartEngine.AspNetCharts, WebResourceId = Guid.NewGuid() }).CanEdit, "web resource");
            Assert.False(ChartCapability.For(new ChartRecord { Engine = ChartEngine.Unknown }).CanEdit, "unknown engine");
            Assert.False(ChartCapability.For(new ChartRecord
            {
                Engine = ChartEngine.AspNetCharts, Scope = ChartScope.System, IsManaged = true, IsCustomizable = false
            }).CanEdit, "managed + not customizable");
            Assert.False(ChartCapability.For(null).CanEdit, "null");
        }

        [Test]
        public static void CapabilityAllowsCustomizableAndPersonalCharts()
        {
            var managed = ChartCapability.For(new ChartRecord { Engine = ChartEngine.AspNetCharts, Scope = ChartScope.System, IsManaged = true, IsCustomizable = true });
            Assert.True(managed.CanEdit, "managed + customizable");
            Assert.Contains("unmanaged layer", managed.Reason);

            Assert.True(ChartCapability.For(new ChartRecord { Engine = ChartEngine.AspNetCharts, Scope = ChartScope.System, IsManaged = false }).CanEdit, "unmanaged");
            Assert.True(ChartCapability.For(new ChartRecord { Engine = ChartEngine.AspNetCharts, Scope = ChartScope.Personal }).CanEdit, "personal");
        }

        private sealed class FakeClock : IClock
        {
            public DateTime UtcNow { get; set; } = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
        }

        private sealed class CountingProvider : IMetadataProvider
        {
            public int TableCalls, ListCalls;
            public IReadOnlyList<TableSummary> ListTables(CancellationToken ct)
            {
                ListCalls++;
                return new[] { new TableSummary { LogicalName = "account", DisplayName = "Account" } };
            }
            public TableMetadata GetTable(string logicalName)
            {
                TableCalls++;
                return logicalName == "missing" ? null : new TableMetadata { LogicalName = logicalName };
            }
        }

        [Test]
        public static void MetadataCacheReusesUntilExpiry()
        {
            var clock = new FakeClock();
            var provider = new CountingProvider();
            var cache = new MetadataCache(provider, TimeSpan.FromMinutes(30), clock);

            cache.GetTable("account");
            cache.GetTable("ACCOUNT");
            Assert.Equal(1, provider.TableCalls, "cached, case-insensitive");

            clock.UtcNow = clock.UtcNow.AddMinutes(31);
            cache.GetTable("account");
            Assert.Equal(2, provider.TableCalls, "expired");

            cache.ListTables(CancellationToken.None);
            cache.ListTables(CancellationToken.None);
            Assert.Equal(1, provider.ListCalls, "table list cached");

            cache.Clear();
            cache.ListTables(CancellationToken.None);
            cache.GetTable("account");
            Assert.Equal(2, provider.ListCalls, "cleared list");
            Assert.Equal(3, provider.TableCalls, "cleared tables");
        }

        [Test]
        public static void MetadataCacheDoesNotCacheMissingTables()
        {
            var provider = new CountingProvider();
            var cache = new MetadataCache(provider, TimeSpan.FromMinutes(30), new FakeClock());
            Assert.True(cache.GetTable("missing") == null, "null");
            cache.GetTable("missing");
            Assert.Equal(2, provider.TableCalls);
        }
    }
}
