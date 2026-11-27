using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ChartDesignerPro.Abstractions;
using ChartDesignerPro.Editing;
using ChartDesignerPro.ImportExport;
using ChartDesignerPro.Models;
using ChartDesignerPro.Saving;
using ChartDesignerPro.Validation;

namespace ChartDesignerPro.Tests
{
    /// <summary>The save sequence with a fake repository and an in-memory file store. No Dataverse calls.</summary>
    public static class SaveTests
    {
        private static readonly DateTime Opened = new DateTime(2026, 11, 27, 8, 0, 0, DateTimeKind.Utc);

        private static ChartRecord Chart(ChartScope scope = ChartScope.System) => new ChartRecord
        {
            Id = new Guid("8385e212-3aea-f111-84ea-000d3af0ef1d"), Name = "Pipeline", TableLogicalName = "opportunity",
            Scope = scope, Engine = ChartEngine.AspNetCharts, IsManaged = false, IsCustomizable = true,
            ModifiedOnUtc = Opened, ModifiedByName = "Vikash Kumar",
            DataXml = Samples.ColumnData, PresentationXml = Samples.ColumnPresentation, HasXml = true
        };

        private static ChartRecord Copy(ChartRecord c) => new ChartRecord
        {
            Id = c.Id, Name = c.Name, TableLogicalName = c.TableLogicalName, Scope = c.Scope, Engine = c.Engine,
            IsManaged = c.IsManaged, IsCustomizable = c.IsCustomizable, ModifiedOnUtc = c.ModifiedOnUtc, ModifiedByName = c.ModifiedByName,
            DataXml = c.DataXml, PresentationXml = c.PresentationXml, HasXml = c.HasXml
        };

        /// <summary>Workspace in edit mode with the presentation XML changed.</summary>
        private static ChartWorkspace Edited(ChartScope scope = ChartScope.System, bool data = false, bool presentation = true)
        {
            var ws = new ChartWorkspace(Chart(scope));
            Assert.True(ws.BeginEdit(), "editable");
            if (presentation) ws.Presentation.Current = Samples.ColumnPresentation.Replace("#4169E1", "#00695C");
            if (data) ws.Data.Current = Samples.ColumnData.Replace("\"sum\"", "\"avg\"");
            return ws;
        }

        private static ValidationReport Validate(ChartWorkspace ws) => new ValidationEngine().Run(ValidationContext.For(ws));

        private sealed class FakeRepository : IChartRepository
        {
            public readonly List<string> Calls;
            public ChartRecord Stored;
            public Exception UpdateError, PublishError, RetrieveError;
            public Func<string, string> StoreTransform = s => s;

            public FakeRepository(List<string> calls, ChartRecord stored) { Calls = calls; Stored = stored; }

            public ChartListResult ListCharts(ChartQuery query, CancellationToken ct, IProgress<string> progress) => throw new NotSupportedException();

            public ChartRecord Retrieve(ChartScope scope, Guid id)
            {
                Calls.Add("retrieve");
                if (RetrieveError != null) throw RetrieveError;
                return Stored == null ? null : Copy(Stored);
            }

            public void Update(ChartScope scope, Guid id, string dataXml, string presentationXml)
            {
                Calls.Add($"update data={(dataXml == null ? "-" : "sent")} presentation={(presentationXml == null ? "-" : "sent")}");
                if (UpdateError != null) throw UpdateError;
                if (dataXml != null) Stored.DataXml = StoreTransform(dataXml);
                if (presentationXml != null) Stored.PresentationXml = StoreTransform(presentationXml);
                Stored.ModifiedOnUtc = Opened.AddMinutes(10);
            }

            public void PublishTable(string tableLogicalName)
            {
                Calls.Add("publish " + tableLogicalName);
                if (PublishError != null) throw PublishError;
            }
        }

        private sealed class Files : IFileStore
        {
            public readonly List<string> Calls;
            public bool Fail;
            public readonly Dictionary<string, string> Written = new Dictionary<string, string>();
            public Files(List<string> calls) { Calls = calls; }
            public void WriteAllText(string path, string text)
            {
                Calls.Add("backup");
                if (Fail) throw new IOException("disk full");
                Written[path] = text;
            }
            public string ReadAllText(string path) => Written[path];
            public bool DirectoryExists(string path) => true;
            public void CreateDirectory(string path) { }
            public IEnumerable<string> GetFiles(string directory, string pattern) => Written.Keys.ToList();
        }

        private static SaveResult Run(ChartWorkspace ws, out List<string> calls, out FakeRepository repo, out Files files,
            bool publish = true, Action<FakeRepository, Files> setup = null)
        {
            calls = new List<string>();
            repo = new FakeRepository(calls, Copy(ws.Record));
            files = new Files(calls);
            setup?.Invoke(repo, files);
            var workflow = new SaveWorkflow(repo, new BackupStore("backups", files));
            return workflow.Execute(new SaveRequest
            {
                Workspace = ws, CurrentInDataverse = Copy(ws.Record), Publish = publish,
                EnvironmentName = "D365 Env", EnvironmentUrl = "https://org.example.com/", ToolVersion = "1"
            });
        }

        // ---------------------------------------------------------------- readiness

        [Test]
        public static void ValidEditCanBeSaved()
        {
            var ws = Edited();
            var r = SaveWorkflow.CheckReadiness(ws, Validate(ws));
            Assert.True(r.CanSave, r.BlockReason);
            Assert.False(r.RequiresAcknowledgement, "no warnings");
        }

        [Test]
        public static void NothingToSaveIsBlocked()
        {
            var ws = new ChartWorkspace(Chart());
            ws.BeginEdit();
            var r = SaveWorkflow.CheckReadiness(ws, Validate(ws));
            Assert.False(r.CanSave, "no changes");
            Assert.Contains("no changes", r.BlockReason);
        }

        [Test]
        public static void ValidationErrorsBlockSaving()
        {
            var ws = Edited();
            ws.Presentation.Current = "<Chart><Series><Series ChartType=\"Column\" /></Series><ChartAreas><ChartArea /></ChartAreas></Chart>";
            var r = SaveWorkflow.CheckReadiness(ws, Validate(ws));
            Assert.False(r.CanSave, "error blocks");
            Assert.True(r.Errors.Any(e => e.RuleId == "CDP-S008"), "the empty chart area is the error");
            Assert.Contains("error", r.BlockReason);
        }

        [Test]
        public static void MalformedXmlBlocksSaving()
        {
            var ws = Edited();
            ws.Data.Current = "<datadefinition>";
            var r = SaveWorkflow.CheckReadiness(ws, Validate(ws));
            Assert.False(r.CanSave, "malformed");
            Assert.Contains("isn't well-formed", r.BlockReason);
        }

        [Test]
        public static void WarningsNeedAcknowledgement()
        {
            var ws = Edited();
            // Two measure collections but one <Series>: CDP-S006 is a warning.
            ws.Data.Current = Samples.ColumnData.Replace("</measurecollection>", "</measurecollection><measurecollection><measure alias=\"aggregate_column\" /></measurecollection>");
            var r = SaveWorkflow.CheckReadiness(ws, Validate(ws));
            Assert.True(r.CanSave, r.BlockReason);
            Assert.True(r.RequiresAcknowledgement, "warning needs a tick");
        }

        [Test]
        public static void ManagedNotCustomizableChartIsBlocked()
        {
            var record = Chart();
            record.IsManaged = true;
            record.IsCustomizable = false;
            var ws = new ChartWorkspace(record);
            Assert.False(ws.BeginEdit(), "can't even edit");
            var r = SaveWorkflow.CheckReadiness(ws, Validate(ws));
            Assert.False(r.CanSave, "blocked");
            Assert.Contains("not customizable", r.BlockReason);
        }

        // ---------------------------------------------------------------- conflicts

        [Test]
        public static void UnchangedChartHasNoConflict()
        {
            Assert.Equal(ConflictKind.None, SaveWorkflow.CheckConflict(Chart(), Chart()).Kind);
        }

        [Test]
        public static void ChangeSinceOpeningIsAConflict()
        {
            var now = Chart();
            now.ModifiedOnUtc = Opened.AddMinutes(3);
            now.ModifiedByName = "Asha Rao";
            var c = SaveWorkflow.CheckConflict(Chart(), now);
            Assert.Equal(ConflictKind.ChangedSinceOpened, c.Kind);
            Assert.Contains("Asha Rao", c.Message);
            Assert.Contains("other columns", c.Message);

            var xml = Chart();
            xml.PresentationXml = "<Chart />";
            var c2 = SaveWorkflow.CheckConflict(Chart(), xml);
            Assert.Equal(ConflictKind.ChangedSinceOpened, c2.Kind);
            Assert.Contains("XML is different", c2.Message);
        }

        [Test]
        public static void DeletedChartIsAConflict()
        {
            var c = SaveWorkflow.CheckConflict(Chart(), null);
            Assert.Equal(ConflictKind.Deleted, c.Kind);
            Assert.Contains("no longer exists", c.Message);
        }

        // ---------------------------------------------------------------- execute

        [Test]
        public static void SystemChartIsBackedUpUpdatedPublishedAndVerified()
        {
            var result = Run(Edited(), out var calls, out var repo, out var files);
            Assert.Equal("backup|update data=- presentation=sent|publish opportunity|retrieve", string.Join("|", calls), "order and only the changed column");
            Assert.True(result.Saved && result.Published, "saved and published");
            Assert.False(result.NeedsPublish, "nothing left to publish");
            Assert.Equal(VerifyOutcome.Exact, result.Verify);
            Assert.Equal("presentationdescription", string.Join(",", result.ColumnsSent));
            Assert.True(result.BackupPath != null && files.Written.ContainsKey(result.BackupPath), "backup written");
            Assert.Equal(Samples.ColumnPresentation, ChartFileFormat.ReadJson(files.Written[result.BackupPath]).PresentationXml, "the backup holds the stored version");
        }

        [Test]
        public static void BothColumnsAreSentWhenBothChanged()
        {
            Run(Edited(data: true, presentation: true), out var calls, out _, out _);
            Assert.True(calls.Contains("update data=sent presentation=sent"), string.Join("|", calls));
        }

        [Test]
        public static void PersonalChartIsNotPublished()
        {
            var result = Run(Edited(ChartScope.Personal), out var calls, out _, out _);
            Assert.False(calls.Any(c => c.StartsWith("publish")), "no publish call");
            Assert.True(result.Saved, "saved");
            Assert.False(result.Published || result.NeedsPublish, "publishing doesn't apply");
        }

        [Test]
        public static void SaveWithoutPublishingLeavesPublishPending()
        {
            var result = Run(Edited(), out var calls, out _, out _, publish: false);
            Assert.False(calls.Any(c => c.StartsWith("publish")), "no publish call");
            Assert.True(result.Saved && result.NeedsPublish, "saved, publish pending");
        }

        [Test]
        public static void FailedBackupStopsTheSave()
        {
            var result = Run(Edited(), out var calls, out _, out _, setup: (r, f) => f.Fail = true);
            Assert.Equal("backup", string.Join("|", calls), "nothing after the failed backup");
            Assert.False(result.Saved, "not saved");
            Assert.True(result.BackupError is IOException, "backup error reported");
        }

        [Test]
        public static void FailedUpdateIsNotPublished()
        {
            var result = Run(Edited(), out var calls, out _, out _, setup: (r, f) => r.UpdateError = new InvalidOperationException("no privilege"));
            Assert.False(calls.Any(c => c.StartsWith("publish") || c == "retrieve"), string.Join("|", calls));
            Assert.False(result.Saved, "not saved");
            Assert.Equal("no privilege", result.UpdateError.Message);
            Assert.True(result.BackupPath != null, "the backup is still there");
        }

        [Test]
        public static void FailedPublishIsReportedSeparately()
        {
            var result = Run(Edited(), out var calls, out _, out _, setup: (r, f) => r.PublishError = new TimeoutException("publish timed out"));
            Assert.True(result.Saved, "the update stands");
            Assert.False(result.Published, "not published");
            Assert.True(result.NeedsPublish, "publish pending");
            Assert.True(result.PublishError is TimeoutException, "error kept");
            Assert.True(calls.Last() == "retrieve", "still verified");
        }

        [Test]
        public static void VerificationDetectsReformattingAndDifferences()
        {
            var reformatted = Run(Edited(), out _, out _, out _, setup: (r, f) => r.StoreTransform = s => s.Replace("><", ">\n<"));
            Assert.Equal(VerifyOutcome.Reformatted, reformatted.Verify);

            var different = Run(Edited(), out _, out _, out _, setup: (r, f) => r.StoreTransform = s => s.Replace("#00695C", "#000000"));
            Assert.Equal(VerifyOutcome.Different, different.Verify);

            var unreadable = Run(Edited(), out _, out _, out _, setup: (r, f) => r.RetrieveError = new InvalidOperationException("network"));
            Assert.Equal(VerifyOutcome.Unavailable, unreadable.Verify);
            Assert.True(unreadable.Saved, "the save itself succeeded");
        }

        [Test]
        public static void BackupIsTheVersionBeingReplaced()
        {
            // After a conflict the user may overwrite someone else's version: that version is what gets backed up.
            var ws = Edited();
            var calls = new List<string>();
            var theirs = Copy(ws.Record);
            theirs.PresentationXml = "<Chart Palette=\"Berry\" />";
            var files = new Files(calls);
            var workflow = new SaveWorkflow(new FakeRepository(calls, Copy(theirs)), new BackupStore("backups", files));
            var result = workflow.Execute(new SaveRequest { Workspace = ws, CurrentInDataverse = theirs, EnvironmentName = "D365 Env" });
            Assert.Equal("<Chart Palette=\"Berry\" />", ChartFileFormat.ReadJson(files.Written[result.BackupPath]).PresentationXml);
        }
    }
}
