using System;
using System.Collections.Generic;
using System.Linq;
using ChartDesignerPro.Editing;
using ChartDesignerPro.Explorer;
using ChartDesignerPro.Models;

namespace ChartDesignerPro.Tests
{
    public static class EditingTests
    {
        private static ChartRecord Chart(bool editable = true) => new ChartRecord
        {
            Id = Guid.NewGuid(), Name = "Pipeline", Scope = ChartScope.System,
            Engine = editable ? ChartEngine.AspNetCharts : ChartEngine.PowerBI,
            DataXml = Samples.ColumnData, PresentationXml = Samples.ColumnPresentation, HasXml = true
        };

        [Test]
        public static void WorkspaceTracksDirtyAndDiscards()
        {
            var ws = new ChartWorkspace(Chart());
            Assert.False(ws.IsDirty, "fresh");
            Assert.True(ws.BeginEdit(), "editable");
            ws.Presentation.Current = ws.Presentation.Current.Replace("Column", "Bar");
            Assert.True(ws.IsDirty, "dirty");
            Assert.Equal("Presentation XML changed", ws.ChangeSummary());
            ws.Data.Current += " ";
            Assert.Equal("Data XML and Presentation XML changed", ws.ChangeSummary(), "whitespace counts");
            ws.DiscardChanges();
            Assert.False(ws.IsDirty, "discarded");
            Assert.False(ws.IsEditing, "back to read-only");
            Assert.Equal(Samples.ColumnPresentation, ws.Presentation.Current);
        }

        [Test]
        public static void ReadOnlyChartsCannotEnterEditMode()
        {
            var ws = new ChartWorkspace(Chart(editable: false));
            Assert.False(ws.BeginEdit(), "Power BI is read-only");
            Assert.False(ws.IsEditing, "still read-only");
        }

        [Test]
        public static void DocumentParseIsCachedAndReportsErrors()
        {
            var doc = new EditableDocument(DocumentKind.Data, Samples.ColumnData);
            var a = doc.Parse(out var e1);
            Assert.True(a != null && e1 == null, "parses");
            Assert.True(ReferenceEquals(a, doc.Parse(out _)), "cached");
            doc.Current = "<broken>";
            Assert.True(doc.Parse(out var e2) == null && e2 != null, "error reported");
            doc.AcceptCurrent();
            Assert.False(doc.IsDirty, "accepted becomes baseline");
        }

        [Test]
        public static void UndoRedoWithCoalescing()
        {
            var t0 = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc);
            var h = new UndoHistory("a");
            h.Record("ab", 2, t0);
            h.Record("abc", 3, t0.AddMilliseconds(300)); // same burst
            h.Record("abcd", 4, t0.AddSeconds(3));       // new step
            Assert.True(h.TryUndo(out var s1), "undo 1");
            Assert.Equal("abc", s1.Text);
            Assert.True(h.TryUndo(out var s2), "undo 2");
            Assert.Equal("a", s2.Text, "burst undone as one step");
            Assert.False(h.TryUndo(out _), "nothing left");
            Assert.True(h.TryRedo(out var r1), "redo");
            Assert.Equal("abc", r1.Text);
            h.Record("abcX", 4, t0.AddSeconds(10));
            Assert.False(h.CanRedo, "new edit clears redo");
        }

        [Test]
        public static void UndoHistoryIsCapped()
        {
            var t = DateTime.UtcNow;
            var h = new UndoHistory("", maxSteps: 3);
            for (var i = 1; i <= 10; i++) h.Record(new string('x', i), i, t.AddSeconds(i * 5));
            var steps = 0;
            while (h.TryUndo(out _)) steps++;
            Assert.Equal(3, steps);
        }

        [Test]
        public static void FindWrapsAndHonoursCase()
        {
            const string text = "Color Bar color bar";
            Assert.Equal(0, FindReplace.FindNext(text, "color", 0, false));
            Assert.Equal(10, FindReplace.FindNext(text, "color", 1, false));
            Assert.Equal(0, FindReplace.FindNext(text, "color", 11, false), "wraps");
            Assert.Equal(10, FindReplace.FindNext(text, "color", 0, true), "match case");
            Assert.Equal(10, FindReplace.FindNext(text, "color", 0, false, backwards: true), "backwards wraps to last");
            Assert.Equal(-1, FindReplace.FindNext(text, "pie", 0, false));
            Assert.Equal(2, FindReplace.Count(text, "BAR", false));
        }

        [Test]
        public static void ReplaceAllCountsReplacements()
        {
            var result = FindReplace.ReplaceAll("aXbxc", "x", "--", false, out var n);
            Assert.Equal("a--b--c", result);
            Assert.Equal(2, n);
            Assert.Equal("aXbxc", FindReplace.ReplaceAll("aXbxc", "", "-", false, out var none));
            Assert.Equal(0, none);
        }

        [Test]
        public static void IndexOfLineFindsLineStarts()
        {
            const string text = "one\ntwo\nthree";
            Assert.Equal(0, FindReplace.IndexOfLine(text, 1));
            Assert.Equal(4, FindReplace.IndexOfLine(text, 2));
            Assert.Equal(8, FindReplace.IndexOfLine(text, 3));
            Assert.Equal(text.Length, FindReplace.IndexOfLine(text, 9));
        }

        [Test]
        public static void FavouritesAndRecent()
        {
            var settings = new ToolSettings();
            var a = Chart(); a.Name = "A";
            var b = Chart(); b.Name = "B";
            var c = Chart(); c.Name = "C";
            var all = new List<ChartRecord> { a, b, c };

            Assert.True(ChartBookmarks.ToggleFavourite(settings, b), "added");
            Assert.True(ChartBookmarks.IsFavourite(settings, b), "is favourite");
            Assert.Equal("B", ChartBookmarks.Apply(all, ChartListView.Favourites, settings).Single().Name);
            Assert.False(ChartBookmarks.ToggleFavourite(settings, b), "removed");
            Assert.Equal(0, ChartBookmarks.Apply(all, ChartListView.Favourites, settings).Count());

            ChartBookmarks.AddRecent(settings, a);
            ChartBookmarks.AddRecent(settings, c);
            ChartBookmarks.AddRecent(settings, a);
            Assert.Equal("A|C", string.Join("|", ChartBookmarks.Apply(all, ChartListView.Recent, settings).Select(x => x.Name)), "most recent first, no duplicates");
            Assert.Equal(3, ChartBookmarks.Apply(all, ChartListView.All, settings).Count());

            for (var i = 0; i < 30; i++) ChartBookmarks.AddRecent(settings, Chart());
            Assert.Equal(ChartBookmarks.MaxRecent, settings.Recent.Count, "capped");
        }

        [Test]
        public static void SettingsRoundTripThroughXmlSerializer()
        {
            // XrmToolBox's SettingsManager stores settings with XmlSerializer.
            var settings = new ToolSettings { LastTable = "account", Favourites = { "System:" + Guid.Empty }, Recent = { "Personal:" + Guid.Empty } };
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(ToolSettings));
            var sw = new System.IO.StringWriter();
            serializer.Serialize(sw, settings);
            var back = (ToolSettings)serializer.Deserialize(new System.IO.StringReader(sw.ToString()));
            Assert.Equal("account", back.LastTable);
            Assert.Equal(1, back.Favourites.Count, "favourites (no duplication from the default list)");
            Assert.Equal(1, back.Recent.Count);
        }
    }
}
