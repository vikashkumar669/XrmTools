using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace DataQualityStudio.Services
{
    public enum CellStyle
    {
        Normal = 0,
        Header = 1,
        Error = 2,
        Warning = 3,
        Corrected = 4,
        Info = 5,
        Title = 6,
        Bold = 7,
        Wrap = 8
    }

    public struct XCell
    {
        public string Text;
        public double? Number;
        public CellStyle Style;

        public static XCell Of(string text, CellStyle style = CellStyle.Normal) => new XCell { Text = text, Style = style };
        public static XCell Num(double value, CellStyle style = CellStyle.Normal) => new XCell { Number = value, Style = style };
        public static implicit operator XCell(string text) => Of(text);
    }

    /// <summary>Minimal streaming .xlsx writer (inline strings, a fixed style sheet, frozen header and auto filter).</summary>
    public sealed class XlsxWriter : IDisposable
    {
        private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        private readonly FileStream _stream;
        private readonly ZipArchive _zip;
        private readonly List<SheetEntry> _sheets = new List<SheetEntry>();
        private bool _saved;

        private sealed class SheetEntry
        {
            public string Name;
            public string FilterRef;
        }

        public XlsxWriter(string path)
        {
            _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            _zip = new ZipArchive(_stream, ZipArchiveMode.Create, false, Encoding.UTF8);
        }

        /// <param name="headerRowIndex">0-based index of the header row inside <paramref name="rows"/> (frozen + filtered), or -1.</param>
        public void AddSheet(string name, IEnumerable<IList<XCell>> rows, double[] columnWidths, int headerRowIndex = 0)
        {
            name = SafeSheetName(name);
            var entry = new SheetEntry { Name = name };
            var index = _sheets.Count + 1;
            var zipEntry = _zip.CreateEntry($"xl/worksheets/sheet{index}.xml", CompressionLevel.Optimal);
            using (var w = XmlWriter.Create(zipEntry.Open(), new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = true }))
            {
                w.WriteStartDocument(true);
                w.WriteStartElement("worksheet", Main);
                w.WriteAttributeString("xmlns", "r", null, RelNs);

                w.WriteStartElement("sheetViews", Main);
                w.WriteStartElement("sheetView", Main);
                w.WriteAttributeString("workbookViewId", "0");
                if (index == 1) w.WriteAttributeString("tabSelected", "1");
                if (headerRowIndex >= 0)
                {
                    w.WriteStartElement("pane", Main);
                    w.WriteAttributeString("ySplit", (headerRowIndex + 1).ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("topLeftCell", "A" + (headerRowIndex + 2).ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("activePane", "bottomLeft");
                    w.WriteAttributeString("state", "frozen");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("sheetFormatPr", Main);
                w.WriteAttributeString("defaultRowHeight", "15");
                w.WriteEndElement();

                if (columnWidths != null && columnWidths.Length > 0)
                {
                    w.WriteStartElement("cols", Main);
                    for (int i = 0; i < columnWidths.Length; i++)
                    {
                        w.WriteStartElement("col", Main);
                        w.WriteAttributeString("min", (i + 1).ToString(CultureInfo.InvariantCulture));
                        w.WriteAttributeString("max", (i + 1).ToString(CultureInfo.InvariantCulture));
                        w.WriteAttributeString("width", Math.Max(4, Math.Min(120, columnWidths[i])).ToString("0.##", CultureInfo.InvariantCulture));
                        w.WriteAttributeString("customWidth", "1");
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }

                w.WriteStartElement("sheetData", Main);
                var rowNumber = 0;
                var headerWidth = 0;
                foreach (var row in rows)
                {
                    rowNumber++;
                    if (rowNumber - 1 == headerRowIndex) headerWidth = row.Count;
                    w.WriteStartElement("row", Main);
                    w.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
                    for (int c = 0; c < row.Count; c++)
                    {
                        var cell = row[c];
                        if (cell.Text == null && !cell.Number.HasValue && cell.Style == CellStyle.Normal) continue;
                        w.WriteStartElement("c", Main);
                        w.WriteAttributeString("r", SpreadsheetReader.ColumnLetters(c) + rowNumber.ToString(CultureInfo.InvariantCulture));
                        if (cell.Style != CellStyle.Normal) w.WriteAttributeString("s", ((int)cell.Style).ToString(CultureInfo.InvariantCulture));
                        if (cell.Number.HasValue && !double.IsNaN(cell.Number.Value) && !double.IsInfinity(cell.Number.Value))
                        {
                            w.WriteElementString("v", Main, cell.Number.Value.ToString("R", CultureInfo.InvariantCulture));
                        }
                        else if (!string.IsNullOrEmpty(cell.Text))
                        {
                            w.WriteAttributeString("t", "inlineStr");
                            w.WriteStartElement("is", Main);
                            w.WriteStartElement("t", Main);
                            w.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                            w.WriteString(Clean(cell.Text));
                            w.WriteEndElement();
                            w.WriteEndElement();
                        }
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }
                w.WriteEndElement(); // sheetData

                if (headerRowIndex >= 0 && headerWidth > 0 && rowNumber > headerRowIndex)
                {
                    entry.FilterRef = $"$A${headerRowIndex + 1}:${SpreadsheetReader.ColumnLetters(headerWidth - 1)}${Math.Max(rowNumber, headerRowIndex + 1)}";
                    w.WriteStartElement("autoFilter", Main);
                    w.WriteAttributeString("ref", entry.FilterRef.Replace("$", ""));
                    w.WriteEndElement();
                }

                w.WriteEndElement(); // worksheet
                w.WriteEndDocument();
            }
            _sheets.Add(entry);
        }

        public void Save()
        {
            if (_saved) return;
            _saved = true;
            if (_sheets.Count == 0) AddSheet("Sheet1", new List<IList<XCell>>(), null, -1);

            WriteText("[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                string.Concat(_sheets.Select((s, i) =>
                    $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
                "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
                "</Types>");

            WriteText("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
                "</Relationships>");

            WriteText("docProps/core.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
                "xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                "<dc:creator>Data Quality Studio</dc:creator>" +
                $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created>" +
                "</cp:coreProperties>");

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append($"<workbook xmlns=\"{Main}\" xmlns:r=\"{RelNs}\"><bookViews><workbookView/></bookViews><sheets>");
            for (int i = 0; i < _sheets.Count; i++)
                sb.Append($"<sheet name=\"{Escape(_sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            sb.Append("</sheets>");
            if (_sheets.Any(s => s.FilterRef != null))
            {
                sb.Append("<definedNames>");
                for (int i = 0; i < _sheets.Count; i++)
                    if (_sheets[i].FilterRef != null)
                        sb.Append($"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">'{Escape(_sheets[i].Name.Replace("'", "''"))}'!{_sheets[i].FilterRef}</definedName>");
                sb.Append("</definedNames>");
            }
            sb.Append("</workbook>");
            WriteText("xl/workbook.xml", sb.ToString());

            sb.Clear();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 0; i < _sheets.Count; i++)
                sb.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
            sb.Append($"<Relationship Id=\"rId{_sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            WriteText("xl/_rels/workbook.xml.rels", sb.ToString());

            WriteText("xl/styles.xml", Styles);
        }

        // Index order must match the CellStyle enum
        private const string Styles =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"6\">" +
            "<font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
            "<font><b/><sz val=\"11\"/><color rgb=\"FF0B4F4A\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
            "<font><sz val=\"11\"/><color rgb=\"FF9C0006\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
            "<font><sz val=\"11\"/><color rgb=\"FF7A4B00\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
            "<font><sz val=\"11\"/><color rgb=\"FF006100\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
            "<font><b/><sz val=\"15\"/><color rgb=\"FF0B4F4A\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
            "</fonts>" +
            "<fills count=\"7\">" +
            "<fill><patternFill patternType=\"none\"/></fill>" +
            "<fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDCEFEA\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFC7CE\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFEB9C\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFC6EFCE\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDDEBF7\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
            "</fills>" +
            "<borders count=\"2\">" +
            "<border><left/><right/><top/><bottom/><diagonal/></border>" +
            "<border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FF8DB8AF\"/></bottom><diagonal/></border>" +
            "</borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"9\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"3\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"4\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"4\" fillId=\"5\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"6\" borderId=\"0\" xfId=\"0\" applyFill=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"5\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment wrapText=\"1\" vertical=\"top\"/></xf>" +
            "</cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>";

        private void WriteText(string path, string content)
        {
            var entry = _zip.CreateEntry(path, CompressionLevel.Optimal);
            using (var s = entry.Open())
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_saved) Save();
            }
            finally
            {
                _zip.Dispose();
                _stream.Dispose();
            }
        }

        public static string SafeSheetName(string name)
        {
            var invalid = new[] { '[', ']', ':', '*', '?', '/', '\\' };
            var s = new string((name ?? "Sheet").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('\'').Trim();
            if (s.Length == 0) s = "Sheet";
            return s.Length > 31 ? s.Substring(0, 31) : s;
        }

        private static string Escape(string s) =>
            (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        /// <summary>Removes characters that are illegal in XML and enforces Excel's 32,767-character cell limit.</summary>
        private static string Clean(string s)
        {
            if (s.Length > 32767) s = s.Substring(0, 32767);
            var ok = true;
            foreach (var c in s) if (!XmlConvert.IsXmlChar(c) && !char.IsSurrogate(c)) { ok = false; break; }
            if (ok && !HasLoneSurrogate(s)) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
                else if (XmlConvert.IsXmlChar(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool HasLoneSurrogate(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]))
                {
                    if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                    i++;
                }
                else if (char.IsLowSurrogate(s[i])) return true;
            }
            return false;
        }
    }
}
