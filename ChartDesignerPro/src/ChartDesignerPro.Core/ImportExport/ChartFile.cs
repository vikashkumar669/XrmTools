using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ChartDesignerPro.Models;
using ChartDesignerPro.Xml;

namespace ChartDesignerPro.ImportExport
{
    /// <summary>Where an exported chart came from.</summary>
    [DataContract]
    public sealed class ChartFileSource
    {
        [DataMember(Name = "environment", Order = 1, EmitDefaultValue = false)] public string Environment { get; set; }
        [DataMember(Name = "url", Order = 2, EmitDefaultValue = false)] public string Url { get; set; }
        [DataMember(Name = "scope", Order = 3, EmitDefaultValue = false)] public string Scope { get; set; }
        [DataMember(Name = "id", Order = 4, EmitDefaultValue = false)] public string Id { get; set; }
        [DataMember(Name = "modifiedOnUtc", Order = 5, EmitDefaultValue = false)] public string ModifiedOnUtc { get; set; }
    }

    /// <summary>
    /// The tool's portable chart file (JSON, versioned). Version 1 holds the chart's columns exactly as stored, so a file can be
    /// compared with, or applied to, a chart in any environment. Newer versions must stay readable by migrating older ones.
    /// </summary>
    [DataContract]
    public sealed class ChartFile
    {
        public const string FormatName = "ChartDesignerPro.Chart";
        public const int CurrentVersion = 1;

        [DataMember(Name = "format", Order = 1)] public string Format { get; set; } = FormatName;
        [DataMember(Name = "version", Order = 2)] public int Version { get; set; } = CurrentVersion;
        [DataMember(Name = "exportedUtc", Order = 3, EmitDefaultValue = false)] public string ExportedUtc { get; set; }
        [DataMember(Name = "tool", Order = 4, EmitDefaultValue = false)] public string Tool { get; set; }
        [DataMember(Name = "source", Order = 5, EmitDefaultValue = false)] public ChartFileSource Source { get; set; }
        [DataMember(Name = "name", Order = 6, EmitDefaultValue = false)] public string Name { get; set; }
        [DataMember(Name = "description", Order = 7, EmitDefaultValue = false)] public string Description { get; set; }
        [DataMember(Name = "table", Order = 8, EmitDefaultValue = false)] public string Table { get; set; }
        [DataMember(Name = "chartType", Order = 9)] public int ChartType { get; set; }
        [DataMember(Name = "isDefault", Order = 10)] public bool IsDefault { get; set; }
        [DataMember(Name = "dataXml", Order = 11, EmitDefaultValue = false)] public string DataXml { get; set; }
        [DataMember(Name = "presentationXml", Order = 12, EmitDefaultValue = false)] public string PresentationXml { get; set; }

        public static ChartFile From(ChartRecord record, string dataXml, string presentationXml, string environment, string url, string toolVersion, DateTime utcNow) =>
            new ChartFile
            {
                ExportedUtc = utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Tool = "Chart Designer Pro " + toolVersion,
                Source = new ChartFileSource
                {
                    Environment = environment,
                    Url = url,
                    Scope = record.Scope.ToString(),
                    Id = record.Id.ToString("D"),
                    ModifiedOnUtc = record.ModifiedOnUtc?.ToString("yyyy-MM-ddTHH:mm:ssZ")
                },
                Name = record.Name,
                Description = record.Description,
                Table = record.TableLogicalName,
                ChartType = record.Engine == ChartEngine.Unknown ? 0 : (int)record.Engine,
                IsDefault = record.IsDefault,
                DataXml = dataXml,
                PresentationXml = presentationXml
            };
    }

    public enum ChartFileKind { Json, VisualizationXml, DataXmlOnly, PresentationXmlOnly }

    public sealed class ChartFileImport
    {
        public ChartFile File { get; set; }
        public ChartFileKind Kind { get; set; }
        public List<string> Notes { get; } = new List<string>();
        public bool HasData => File?.DataXml != null;
        public bool HasPresentation => File?.PresentationXml != null;
    }

    /// <summary>Thrown when a file can't be imported; the message is shown to the user as is.</summary>
    public sealed class ChartFileException : Exception
    {
        public ChartFileException(string message) : base(message) { }
    }

    /// <summary>Reads and writes chart files. All parsing goes through <see cref="SafeXml"/> or the JSON reader with size limits.</summary>
    public static class ChartFileFormat
    {
        /// <summary>Largest file accepted for import.</summary>
        public const int MaxFileCharacters = 5_000_000;

        // ------------------------------------------------------------------ JSON

        public static string WriteJson(ChartFile file)
        {
            var serializer = new DataContractJsonSerializer(typeof(ChartFile));
            using (var ms = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    serializer.WriteObject(writer, file);
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static ChartFile ReadJson(string json)
        {
            ChartFile file;
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(ChartFile));
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(ms, new XmlDictionaryReaderQuotas { MaxStringContentLength = MaxFileCharacters, MaxDepth = 32 }))
                {
                    file = (ChartFile)serializer.ReadObject(reader);
                }
            }
            catch (Exception ex) when (ex is SerializationException || ex is XmlException || ex is InvalidCastException)
            {
                throw new ChartFileException("The file isn't valid Chart Designer Pro JSON: " + ex.Message);
            }
            if (file == null || !string.Equals(file.Format, ChartFile.FormatName, StringComparison.Ordinal))
                throw new ChartFileException("This JSON file wasn't created by Chart Designer Pro (format \"" + (file?.Format ?? "missing") + "\").");
            if (file.Version > ChartFile.CurrentVersion)
                throw new ChartFileException($"This file was made by a newer version of Chart Designer Pro (file version {file.Version}; this version reads up to {ChartFile.CurrentVersion}). Update the tool to import it.");
            if (file.Version < 1)
                throw new ChartFileException($"Unknown file version {file.Version}.");
            return file;
        }

        // ------------------------------------------------------------------ XML

        /// <summary>
        /// Writes a &lt;visualization&gt; file with the chart's columns as child elements, in the same layout as the app's Export Chart,
        /// so the file can be imported with the app's Import Chart.
        /// Well-formed data and presentation XML is nested as elements; anything else is written as text so nothing is lost.
        /// </summary>
        public static string WriteVisualizationXml(ChartFile file, bool indent)
        {
            var root = new XElement("visualization");
            // The app's Import Chart rejects a file without <visualizationid> ("Required nodes missing"). It never uses the id to
            // overwrite: a new id or an existing one both create a new chart (verified on Dataverse, 2026-11-27).
            var id = Guid.TryParse(file.Source?.Id, out var sourceId) ? sourceId : Guid.NewGuid();
            root.Add(new XElement("visualizationid", id.ToString("B")));
            root.Add(new XElement("name", file.Name ?? string.Empty));
            if (!string.IsNullOrEmpty(file.Description)) root.Add(new XElement("description", file.Description));
            if (!string.IsNullOrEmpty(file.Table)) root.Add(new XElement("primaryentitytypecode", file.Table));
            root.Add(new XElement("datadescription", Content(file.DataXml)));
            root.Add(new XElement("presentationdescription", Content(file.PresentationXml)));
            // The app's Export Chart writes "true"/"false" and no XML declaration (verified 2026-11-27).
            root.Add(new XElement("isdefault", file.IsDefault ? "true" : "false"));
            return root.ToString(indent ? SaveOptions.None : SaveOptions.DisableFormatting);
        }

        private static object Content(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return string.Empty;
            return SafeXml.TryParse(xml, out var parsed, out _) ? (object)new XElement(parsed.Root) : xml;
        }

        // ------------------------------------------------------------------ Import

        /// <summary>
        /// Detects and reads a chart file: Chart Designer Pro JSON, a &lt;visualization&gt; file (child XML nested or escaped),
        /// or a bare &lt;datadefinition&gt; or &lt;Chart&gt; document. Throws <see cref="ChartFileException"/> with a plain message.
        /// </summary>
        public static ChartFileImport Read(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ChartFileException("The file is empty.");
            if (text.Length > MaxFileCharacters) throw new ChartFileException($"The file is too large ({text.Length:N0} characters; the limit is {MaxFileCharacters:N0}).");
            var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');

            if (trimmed.StartsWith("{", StringComparison.Ordinal))
                return new ChartFileImport { File = ReadJson(trimmed), Kind = ChartFileKind.Json };

            if (!SafeXml.TryParse(trimmed, out var doc, out var error))
                throw new ChartFileException("The file is neither Chart Designer Pro JSON nor well-formed XML: " + error);

            var root = doc.Root;
            var name = root.Name.LocalName;
            if (Is(name, "datadefinition"))
            {
                var import = new ChartFileImport { Kind = ChartFileKind.DataXmlOnly, File = new ChartFile { DataXml = SafeXml.Serialize(doc) } };
                import.Notes.Add("The file contains only the data XML; the presentation XML is left as it is.");
                return import;
            }
            if (Is(name, "Chart"))
            {
                var import = new ChartFileImport { Kind = ChartFileKind.PresentationXmlOnly, File = new ChartFile { PresentationXml = SafeXml.Serialize(doc) } };
                import.Notes.Add("The file contains only the presentation XML; the data XML is left as it is.");
                return import;
            }

            // A <visualization> element, either as the root or inside a wrapper (e.g. <visualizations><visualization>).
            var visualizations = root.DescendantsAndSelf().Where(e => Is(e.Name.LocalName, "visualization")).ToList();
            if (visualizations.Count == 0)
                throw new ChartFileException($"The XML root element is <{name}>. Expected <visualization>, <datadefinition> or <Chart>.");

            var v = visualizations[0];
            var result = new ChartFileImport { Kind = ChartFileKind.VisualizationXml };
            if (visualizations.Count > 1) result.Notes.Add($"The file contains {visualizations.Count} charts; the first one ('{Child(v, "name")?.Value}') is imported.");
            var id = Child(v, "visualizationid")?.Value?.Trim().Trim('{', '}');
            var file = new ChartFile
            {
                Source = string.IsNullOrEmpty(id) ? null : new ChartFileSource { Id = id },
                Name = Child(v, "name")?.Value,
                Description = Child(v, "description")?.Value,
                Table = Child(v, "primaryentitytypecode")?.Value,
                IsDefault = IsTrue(Child(v, "isdefault")?.Value),
                DataXml = Inner(Child(v, "datadescription"), result, "data"),
                PresentationXml = Inner(Child(v, "presentationdescription"), result, "presentation")
            };
            if (file.DataXml == null && file.PresentationXml == null)
                throw new ChartFileException("The <visualization> element has neither <datadescription> nor <presentationdescription>.");
            result.File = file;
            return result;
        }

        /// <summary>The XML inside a description element: a nested element, or escaped XML text (as some exports write it).</summary>
        private static string Inner(XElement container, ChartFileImport import, string what)
        {
            if (container == null) return null;
            var element = container.Elements().FirstOrDefault();
            if (element != null) return SafeXml.Serialize(new XDocument(new XElement(element)));
            var text = container.Value;
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            if (!SafeXml.IsWellFormed(text)) import.Notes.Add($"The {what} XML in the file isn't well-formed; it is imported as is so you can fix it.");
            return text;
        }

        private static XElement Child(XElement e, string name) => e.Elements().FirstOrDefault(c => Is(c.Name.LocalName, name));

        private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static bool IsTrue(string value) => value != null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ File names

        /// <summary>A file-name-safe version of <paramref name="text"/> (invalid characters replaced, length capped).</summary>
        public static string SafeFileName(string text, int maxLength = 60)
        {
            if (string.IsNullOrWhiteSpace(text)) return "chart";
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }));
            var sb = new StringBuilder();
            foreach (var c in text.Trim())
                sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
            var name = sb.ToString().Trim(' ', '.');
            if (name.Length > maxLength) name = name.Substring(0, maxLength).Trim(' ', '.');
            return name.Length == 0 ? "chart" : name;
        }

        /// <summary>Default export name, e.g. "opportunity - Actual Revenue by Month - 20261127-1340".</summary>
        public static string DefaultFileName(ChartRecord record, DateTime localNow) =>
            SafeFileName($"{record?.TableLogicalName} - {record?.Name}", 100) + " - " + localNow.ToString("yyyyMMdd-HHmm");
    }
}
