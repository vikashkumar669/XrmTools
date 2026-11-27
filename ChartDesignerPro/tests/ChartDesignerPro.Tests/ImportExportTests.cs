using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChartDesignerPro.Abstractions;
using ChartDesignerPro.ImportExport;
using ChartDesignerPro.Models;

namespace ChartDesignerPro.Tests
{
    public static class ImportExportTests
    {
        private static ChartRecord Chart() => new ChartRecord
        {
            Id = new Guid("0f9f8bf1-2382-de11-9ff3-00155da3b012"), Name = "Actual Revenue by Month", Description = "Revenue \"closed\", per month",
            TableLogicalName = "opportunity", TableDisplayName = "Opportunity", Scope = ChartScope.System, Engine = ChartEngine.AspNetCharts,
            IsDefault = true, IsManaged = true, IsCustomizable = true, ModifiedOnUtc = new DateTime(2026, 11, 1, 19, 51, 18, DateTimeKind.Utc),
            ModifiedByName = "SYSTEM", DataXml = Samples.ColumnData, PresentationXml = Samples.ColumnPresentation, HasXml = true
        };

        private static readonly DateTime Now = new DateTime(2026, 11, 27, 8, 0, 0, DateTimeKind.Utc);

        [Test]
        public static void JsonRoundTripKeepsXmlExactly()
        {
            var file = ChartFile.From(Chart(), Samples.ColumnData, Samples.ColumnPresentation, "D365 Env", "https://org.example.com/", "1.2026.11.1", Now);
            var json = ChartFileFormat.WriteJson(file);
            Assert.Contains("\"format\": \"ChartDesignerPro.Chart\"", json);
            Assert.Contains("\"version\": 1", json);
            var import = ChartFileFormat.Read(json);
            Assert.Equal(ChartFileKind.Json, import.Kind);
            Assert.Equal(Samples.ColumnData, import.File.DataXml, "data exact");
            Assert.Equal(Samples.ColumnPresentation, import.File.PresentationXml, "presentation exact");
            Assert.Equal("opportunity", import.File.Table);
            Assert.Equal("System", import.File.Source.Scope);
            Assert.True(import.File.IsDefault, "default");
        }

        [Test]
        public static void JsonFromNewerVersionOrOtherToolIsRejected()
        {
            var newer = ChartFileFormat.WriteJson(new ChartFile { Version = 2, Name = "x" });
            var ex1 = Assert.Throws<ChartFileException>(() => ChartFileFormat.Read(newer));
            Assert.Contains("newer version", ex1.Message);
            var ex2 = Assert.Throws<ChartFileException>(() => ChartFileFormat.Read("{\"format\":\"Something.Else\",\"version\":1}"));
            Assert.Contains("wasn't created by Chart Designer Pro", ex2.Message);
            Assert.Throws<ChartFileException>(() => ChartFileFormat.Read("{ not json"));
        }

        [Test]
        public static void VisualizationXmlRoundTrip()
        {
            var file = ChartFile.From(Chart(), Samples.ColumnData, Samples.ColumnPresentation, "D365 Env", null, "1", Now);
            foreach (var indent in new[] { false, true })
            {
                var xml = ChartFileFormat.WriteVisualizationXml(file, indent);
                Assert.Contains("<visualization>", xml);
                Assert.Contains("<primaryentitytypecode>opportunity</primaryentitytypecode>", xml);
                var import = ChartFileFormat.Read(xml);
                Assert.Equal(ChartFileKind.VisualizationXml, import.Kind);
                Assert.Equal("Actual Revenue by Month", import.File.Name);
                Assert.True(import.File.IsDefault, "isdefault");
                if (!indent)
                {
                    Assert.Equal(Samples.ColumnData, import.File.DataXml, "exact data when not indented");
                    Assert.Equal(Samples.ColumnPresentation, import.File.PresentationXml, "exact presentation when not indented");
                }
                else
                {
                    Assert.False(Comparison.LineDiff.Compare(Samples.ColumnData, import.File.DataXml, true).HasChanges, "same structure when indented");
                }
            }
        }

        [Test]
        public static void VisualizationXmlWithEscapedContentIsRead()
        {
            var escaped = System.Security.SecurityElement.Escape(Samples.ColumnData);
            var xml = "<?xml version=\"1.0\"?><visualizations><visualization><name>Esc</name><primaryentitytypecode>opportunity</primaryentitytypecode>" +
                      $"<datadescription>{escaped}</datadescription><presentationdescription>{System.Security.SecurityElement.Escape(Samples.ColumnPresentation)}</presentationdescription>" +
                      "<isdefault>false</isdefault></visualization></visualizations>";
            var import = ChartFileFormat.Read(xml);
            Assert.Equal(Samples.ColumnData, import.File.DataXml);
            Assert.Equal(Samples.ColumnPresentation, import.File.PresentationXml);
            Assert.False(import.File.IsDefault, "isdefault false");
        }

        [Test]
        public static void BareDataOrPresentationFilesAreRecognised()
        {
            var data = ChartFileFormat.Read(Samples.ColumnData);
            Assert.Equal(ChartFileKind.DataXmlOnly, data.Kind);
            Assert.True(data.HasData && !data.HasPresentation, "data only");
            var presentation = ChartFileFormat.Read("﻿" + Samples.ColumnPresentation);
            Assert.Equal(ChartFileKind.PresentationXmlOnly, presentation.Kind);
            Assert.Equal(Samples.ColumnPresentation, presentation.File.PresentationXml);
        }

        [Test]
        public static void UnsafeOrUnknownFilesAreRejectedWithPlainMessages()
        {
            Assert.Contains("neither", Assert.Throws<ChartFileException>(() => ChartFileFormat.Read(Samples.ExternalEntity)).Message);
            Assert.Contains("<root>", Assert.Throws<ChartFileException>(() => ChartFileFormat.Read("<root/>")).Message);
            Assert.Contains("empty", Assert.Throws<ChartFileException>(() => ChartFileFormat.Read("  ")).Message);
            Assert.Contains("neither", Assert.Throws<ChartFileException>(() => ChartFileFormat.Read("<visualization><name>x</name></visualization>")).Message);
        }

        [Test]
        public static void CsvEscapesAndNeutralisesFormulas()
        {
            Assert.Equal("plain", InventoryCsv.Escape("plain"));
            Assert.Equal("\"a,b\"", InventoryCsv.Escape("a,b"));
            Assert.Equal("\"say \"\"hi\"\"\"", InventoryCsv.Escape("say \"hi\""));
            Assert.True(InventoryCsv.Escape("=1+1").StartsWith("'="), "formula neutralised");
            Assert.True(InventoryCsv.Escape("@SUM(A1)").StartsWith("'@"), "at neutralised");

            var csv = InventoryCsv.Write(new[] { Chart() });
            var lines = csv.TrimStart('﻿').Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.True(lines[0].StartsWith("Name,Table (logical)"), "header");
            Assert.Contains("\"Revenue \"\"closed\"\", per month\"", lines[1]);
            Assert.Contains("2026-11-01 19:51:18", lines[1]);
        }

        [Test]
        public static void SafeFileNames()
        {
            Assert.Equal("a_b_c", ChartFileFormat.SafeFileName("a/b:c"));
            Assert.Equal("chart", ChartFileFormat.SafeFileName("  "));
            Assert.Equal(60, ChartFileFormat.SafeFileName(new string('x', 200)).Length);
            Assert.Equal("opportunity - Actual Revenue by Month - 20261127-1340", ChartFileFormat.DefaultFileName(Chart(), new DateTime(2026, 11, 27, 13, 40, 0)));
        }

        private sealed class MemoryFiles : IFileStore
        {
            public readonly Dictionary<string, string> Files = new Dictionary<string, string>();
            public readonly HashSet<string> Dirs = new HashSet<string>();
            public void WriteAllText(string path, string text) => Files[path] = text;
            public string ReadAllText(string path) => Files[path];
            public bool DirectoryExists(string path) => Dirs.Contains(path);
            public void CreateDirectory(string path) => Dirs.Add(path);
            public IEnumerable<string> GetFiles(string directory, string pattern) => Files.Keys.Where(k => Path.GetDirectoryName(k) == directory).ToList();
        }

        private sealed class Clock : IClock { public DateTime UtcNow { get; set; } = Now; }

        [Test]
        public static void BackupsAreWrittenAndListedNewestFirst()
        {
            var files = new MemoryFiles();
            var clock = new Clock();
            var store = new BackupStore(Path.Combine("root", "backups"), files, clock);
            var first = store.Save(Chart(), "D365 Env: Sales", "https://org.example.com/", "1");
            clock.UtcNow = Now.AddMinutes(5);
            var second = store.Save(Chart(), "D365 Env: Sales", "https://org.example.com/", "1");

            Assert.Contains("D365 Env_ Sales", first);
            Assert.Contains("opportunity - Actual Revenue by Month - 0f9f8bf1 - 20261127-080000.json", first);
            var list = store.List("D365 Env: Sales", Chart().Id);
            Assert.Equal(2, list.Count);
            Assert.Equal(second, list[0].Path, "newest first");
            Assert.Equal(Samples.ColumnData, ChartFileFormat.ReadJson(files.Files[first]).DataXml, "original XML kept");
            Assert.Equal(0, store.List("Other env", Chart().Id).Count, "per environment");
        }

        [Test]
        public static void BackupRequiresRetrievedXml()
        {
            var chart = Chart();
            chart.HasXml = false;
            Assert.Throws<InvalidOperationException>(() => new BackupStore("root", new MemoryFiles()).Save(chart, "env", null, "1"));
        }
            [Test]
        public static void AppExportChartFileIsRead()
        {
            var import = ChartFileFormat.Read(Samples.AppExport);
            Assert.Equal(ChartFileKind.VisualizationXml, import.Kind);
            Assert.Equal("CDP Test - Accounts by industry (pie)", import.File.Name);
            Assert.Equal("account", import.File.Table);
            Assert.Equal("0a18e918-3aea-f111-84ea-000d3af0ef1d", import.File.Source?.Id);
            Assert.False(import.File.IsDefault, "isdefault false");
            Assert.True(import.File.DataXml.StartsWith("<datadefinition>"), "data XML extracted");
            Assert.True(import.File.PresentationXml.StartsWith("<Chart "), "presentation XML extracted");
            Assert.Equal(0, import.Notes.Count);
        }

        [Test]
        public static void VisualizationXmlMatchesTheAppExportLayout()
        {
            var file = ChartFile.From(Chart(), Samples.ColumnData, Samples.ColumnPresentation, "D365 Env", null, "1", Now);
            var xml = ChartFileFormat.WriteVisualizationXml(file, false);
            Assert.True(xml.StartsWith("<visualization><visualizationid>{"), "no XML declaration and the id first, like the app");
            Assert.Equal(file.Source.Id, ChartFileFormat.Read(xml).File.Source.Id, "id round-trips");
            Assert.Contains("<visualizationid>{", ChartFileFormat.WriteVisualizationXml(new ChartFile { Name = "no id" }, false));
            Assert.Contains("<isdefault>true</isdefault>", xml);
            file.IsDefault = false;
            Assert.Contains("<isdefault>false</isdefault>", ChartFileFormat.WriteVisualizationXml(file, true));
        }
    }
}

