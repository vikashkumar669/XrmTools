using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DataQualityStudio.Models;

namespace DataQualityStudio.Services
{
    /// <summary>
    /// Reads .xlsx/.xlsm workbooks (Office Open XML, no Excel or extra libraries needed) and CSV/TXT files.
    /// All values are returned as text; date-formatted cells become yyyy-MM-dd[ HH:mm:ss].
    /// </summary>
    public static class SpreadsheetReader
    {
        public static bool IsExcel(string path)
        {
            var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return ext == ".xlsx" || ext == ".xlsm";
        }

        public static List<string> GetSheetNames(string path)
        {
            if (!IsExcel(path)) return new List<string> { Path.GetFileNameWithoutExtension(path) };
            using (var package = new XlsxPackage(path))
                return package.Sheets.Select(s => s.Name).ToList();
        }

        public static SourceTable Read(string path, string sheetName, int headerRow)
        {
            if (headerRow < 1) headerRow = 1;
            var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            if (ext == ".xls")
                throw new NotSupportedException("Legacy .xls workbooks aren't supported. Open the file in Excel and save it as .xlsx (or CSV).");

            List<KeyValuePair<int, string[]>> rows;
            string sheet;
            if (IsExcel(path))
            {
                using (var package = new XlsxPackage(path))
                {
                    var info = package.Sheets.FirstOrDefault(s => string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase))
                               ?? package.Sheets.FirstOrDefault();
                    if (info == null) throw new InvalidDataException("The workbook doesn't contain any worksheet.");
                    sheet = info.Name;
                    rows = package.ReadRows(info);
                }
            }
            else
            {
                sheet = Path.GetFileNameWithoutExtension(path);
                rows = CsvReader.ReadRecords(path);
            }

            return BuildTable(path, sheet, headerRow, rows);
        }

        internal static SourceTable BuildTable(string path, string sheet, int headerRow, List<KeyValuePair<int, string[]>> rows)
        {
            var table = new SourceTable { FilePath = path, SheetName = sheet, HeaderRow = headerRow };
            var header = rows.FirstOrDefault(r => r.Key == headerRow);
            if (header.Value == null)
                throw new InvalidDataException($"Row {headerRow} of '{sheet}' is empty, so it can't be used as the header row.");

            var dataRows = rows.Where(r => r.Key > headerRow).ToList();
            var width = Math.Max(header.Value.Length, dataRows.Count == 0 ? 0 : dataRows.Max(r => r.Value.Length));

            // Drop trailing columns that have neither a header nor data
            while (width > 0 && string.IsNullOrWhiteSpace(Cell(header.Value, width - 1)) &&
                   dataRows.All(r => string.IsNullOrWhiteSpace(Cell(r.Value, width - 1))))
                width--;

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < width; c++)
            {
                var name = (Cell(header.Value, c) ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
                if (name.Length == 0) name = "Column " + ColumnLetters(c);
                var unique = name;
                for (int n = 2; used.Contains(unique); n++) unique = $"{name} ({n})";
                used.Add(unique);
                table.Headers.Add(unique);
            }

            foreach (var r in dataRows)
            {
                var values = new string[width];
                var any = false;
                for (int c = 0; c < width; c++)
                {
                    values[c] = Cell(r.Value, c) ?? "";
                    if (!any && values[c].Trim().Length > 0) any = true;
                }
                if (any) table.Rows.Add(new SourceRow { RowNumber = r.Key, Values = values });
            }
            return table;
        }

        private static string Cell(string[] values, int index) => index < values.Length ? values[index] : null;

        public static string ColumnLetters(int index)
        {
            var sb = new StringBuilder();
            index++;
            while (index > 0)
            {
                var rem = (index - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                index = (index - 1) / 26;
            }
            return sb.ToString();
        }

        internal static int ColumnIndex(string cellRef)
        {
            var n = 0;
            foreach (var ch in cellRef)
            {
                var c = char.ToUpperInvariant(ch);
                if (c < 'A' || c > 'Z') break;
                n = n * 26 + (c - 'A' + 1);
            }
            return n - 1;
        }

        internal static int RowIndex(string cellRef)
        {
            var digits = new string(cellRef.SkipWhile(char.IsLetter).TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var r) ? r : 0;
        }

        // ======================================================================

        private sealed class SheetInfo
        {
            public string Name;
            public string Path;
        }

        private sealed class XlsxPackage : IDisposable
        {
            private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            private readonly FileStream _stream;
            private readonly ZipArchive _zip;
            private readonly Dictionary<string, ZipArchiveEntry> _entries;
            private List<string> _sharedStrings;
            private HashSet<int> _dateStyles;
            private HashSet<int> _timeOnlyStyles;
            private bool _date1904;
            private string _workbookPath = "xl/workbook.xml";

            public List<SheetInfo> Sheets { get; } = new List<SheetInfo>();

            public XlsxPackage(string path)
            {
                // FileShare.ReadWrite: the workbook may be open in Excel
                _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                try
                {
                    _zip = new ZipArchive(_stream, ZipArchiveMode.Read);
                }
                catch (InvalidDataException)
                {
                    _stream.Dispose();
                    throw new InvalidDataException("The file isn't a valid .xlsx workbook. If it's password protected, remove the password in Excel first.");
                }
                _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in _zip.Entries) _entries[e.FullName.Replace('\\', '/').TrimStart('/')] = e;
                LoadWorkbook();
            }

            public void Dispose()
            {
                _zip?.Dispose();
                _stream?.Dispose();
            }

            private XmlReader Open(string entryPath)
            {
                if (!_entries.TryGetValue(entryPath.TrimStart('/'), out var entry)) return null;
                return XmlReader.Create(entry.Open(), new XmlReaderSettings { IgnoreWhitespace = false, DtdProcessing = DtdProcessing.Prohibit, CloseInput = true });
            }

            private void LoadWorkbook()
            {
                // Locate the workbook part through the package relationships (normally xl/workbook.xml)
                using (var rels = Open("_rels/.rels"))
                {
                    if (rels != null)
                    {
                        while (rels.Read())
                        {
                            if (rels.NodeType == XmlNodeType.Element && rels.LocalName == "Relationship" &&
                                (rels.GetAttribute("Type") ?? "").EndsWith("/officeDocument", StringComparison.Ordinal))
                            {
                                _workbookPath = (rels.GetAttribute("Target") ?? _workbookPath).TrimStart('/');
                                break;
                            }
                        }
                    }
                }

                var baseDir = _workbookPath.Contains("/") ? _workbookPath.Substring(0, _workbookPath.LastIndexOf('/') + 1) : "";
                var relsPath = baseDir + "_rels/" + _workbookPath.Substring(baseDir.Length) + ".rels";
                var targets = new Dictionary<string, string>();
                using (var rels = Open(relsPath))
                {
                    if (rels != null)
                    {
                        while (rels.Read())
                        {
                            if (rels.NodeType != XmlNodeType.Element || rels.LocalName != "Relationship") continue;
                            var id = rels.GetAttribute("Id");
                            var target = rels.GetAttribute("Target") ?? "";
                            if (id == null) continue;
                            targets[id] = target.StartsWith("/") ? target.TrimStart('/') : ResolvePath(baseDir, target);
                        }
                    }
                }

                using (var wb = Open(_workbookPath))
                {
                    if (wb == null) throw new InvalidDataException("The workbook part is missing; the file may be damaged.");
                    while (wb.Read())
                    {
                        if (wb.NodeType != XmlNodeType.Element) continue;
                        if (wb.LocalName == "workbookPr")
                        {
                            var d = wb.GetAttribute("date1904");
                            _date1904 = d == "1" || string.Equals(d, "true", StringComparison.OrdinalIgnoreCase);
                        }
                        else if (wb.LocalName == "sheet")
                        {
                            var rid = wb.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                            var name = wb.GetAttribute("name");
                            if (rid != null && targets.TryGetValue(rid, out var target) && _entries.ContainsKey(target))
                                Sheets.Add(new SheetInfo { Name = name, Path = target });
                        }
                    }
                }
            }

            private static string ResolvePath(string baseDir, string relative)
            {
                var parts = new List<string>((baseDir + relative).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
                for (int i = 0; i < parts.Count; i++)
                {
                    if (parts[i] == ".") { parts.RemoveAt(i); i--; }
                    else if (parts[i] == ".." && i > 0) { parts.RemoveRange(i - 1, 2); i -= 2; }
                }
                return string.Join("/", parts);
            }

            private string FindPart(string suffix) =>
                _entries.Keys.FirstOrDefault(k => k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

            private void LoadSharedStrings()
            {
                _sharedStrings = new List<string>();
                var path = FindPart("sharedStrings.xml");
                if (path == null) return;
                using (var r = Open(path))
                {
                    while (r.Read())
                    {
                        if (r.NodeType == XmlNodeType.Element && r.LocalName == "si")
                        {
                            using (var sub = r.ReadSubtree())
                                _sharedStrings.Add(ReadText(sub));
                        }
                    }
                }
            }

            /// <summary>Concatenates every &lt;t&gt; in the subtree except phonetic runs (&lt;rPh&gt;).</summary>
            private static string ReadText(XmlReader sub)
            {
                var sb = new StringBuilder();
                sub.Read();
                while (!sub.EOF)
                {
                    if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "rPh") { sub.Skip(); continue; }
                    if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "t") { sb.Append(sub.ReadElementContentAsString()); continue; }
                    sub.Read();
                }
                return sb.ToString();
            }

            private void LoadStyles()
            {
                _dateStyles = new HashSet<int>();
                _timeOnlyStyles = new HashSet<int>();
                var path = FindPart("styles.xml");
                if (path == null) return;

                var customFormats = new Dictionary<int, string>();
                var xfFormats = new List<int>();
                using (var r = Open(path))
                {
                    var inCellXfs = false;
                    while (r.Read())
                    {
                        if (r.NodeType == XmlNodeType.Element)
                        {
                            if (r.LocalName == "numFmt" && int.TryParse(r.GetAttribute("numFmtId"), out var id))
                                customFormats[id] = r.GetAttribute("formatCode") ?? "";
                            else if (r.LocalName == "cellXfs")
                                inCellXfs = !r.IsEmptyElement;
                            else if (inCellXfs && r.LocalName == "xf")
                                xfFormats.Add(int.TryParse(r.GetAttribute("numFmtId"), out var f) ? f : 0);
                        }
                        else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "cellXfs") inCellXfs = false;
                    }
                }

                for (int i = 0; i < xfFormats.Count; i++)
                {
                    var fmt = xfFormats[i];
                    bool isDate, timeOnly = false;
                    if (customFormats.TryGetValue(fmt, out var code))
                    {
                        var clean = Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.|_.|\\*.", "");
                        isDate = Regex.IsMatch(clean, "[dmyhs]", RegexOptions.IgnoreCase) && !Regex.IsMatch(clean, "^[#0.,%E+\\-;@ ]*$");
                        timeOnly = isDate && !Regex.IsMatch(clean, "[dy]", RegexOptions.IgnoreCase) && !Regex.IsMatch(clean, "m{3,}", RegexOptions.IgnoreCase);
                    }
                    else
                    {
                        isDate = (fmt >= 14 && fmt <= 22) || (fmt >= 27 && fmt <= 36) || (fmt >= 45 && fmt <= 47) || (fmt >= 50 && fmt <= 58);
                        timeOnly = (fmt >= 18 && fmt <= 21) || (fmt >= 45 && fmt <= 47);
                    }
                    if (isDate) _dateStyles.Add(i);
                    if (timeOnly) _timeOnlyStyles.Add(i);
                }
            }

            public List<KeyValuePair<int, string[]>> ReadRows(SheetInfo sheet)
            {
                if (_sharedStrings == null) LoadSharedStrings();
                if (_dateStyles == null) LoadStyles();

                var rows = new List<KeyValuePair<int, string[]>>();
                var cells = new SortedDictionary<int, string>();
                var rowNumber = 0;
                var lastCol = -1;
                var inRow = false;

                void Flush()
                {
                    if (!inRow) return;
                    if (cells.Count > 0)
                    {
                        var arr = new string[cells.Keys.Max() + 1];
                        foreach (var kv in cells) arr[kv.Key] = kv.Value;
                        rows.Add(new KeyValuePair<int, string[]>(rowNumber, arr));
                    }
                    cells.Clear();
                    inRow = false;
                }

                using (var r = Open(sheet.Path))
                {
                    while (r.Read())
                    {
                        if (r.NodeType != XmlNodeType.Element) continue;
                        if (r.LocalName == "row")
                        {
                            Flush();
                            inRow = true;
                            rowNumber = int.TryParse(r.GetAttribute("r"), out var rn) ? rn : rowNumber + 1;
                            lastCol = -1;
                            if (r.IsEmptyElement) Flush();
                        }
                        else if (r.LocalName == "c" && inRow)
                        {
                            var cellRef = r.GetAttribute("r");
                            var col = string.IsNullOrEmpty(cellRef) ? lastCol + 1 : ColumnIndex(cellRef);
                            lastCol = col;
                            var type = r.GetAttribute("t");
                            var styleIndex = int.TryParse(r.GetAttribute("s"), out var si) ? si : 0;

                            string raw = null, inline = null;
                            if (!r.IsEmptyElement)
                            {
                                using (var sub = r.ReadSubtree())
                                {
                                    sub.Read();
                                    while (!sub.EOF)
                                    {
                                        if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "v") { raw = sub.ReadElementContentAsString(); continue; }
                                        if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "is")
                                        {
                                            using (var isr = sub.ReadSubtree()) inline = ReadText(isr);
                                            sub.Read();
                                            continue;
                                        }
                                        if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "f") { sub.Skip(); continue; }
                                        sub.Read();
                                    }
                                }
                            }

                            var value = ConvertCell(type, raw, inline, styleIndex);
                            if (!string.IsNullOrEmpty(value) && col >= 0 && col < 16384) cells[col] = value;
                        }
                    }
                    Flush();
                }
                return rows;
            }

            private string ConvertCell(string type, string raw, string inline, int style)
            {
                switch (type)
                {
                    case "s":
                        return int.TryParse(raw, out var idx) && idx >= 0 && idx < _sharedStrings.Count ? _sharedStrings[idx] : "";
                    case "inlineStr":
                        return inline ?? raw ?? "";
                    case "str":
                        return raw ?? "";
                    case "b":
                        return raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : raw ?? "";
                    case "e":
                        return raw ?? "#ERROR";
                    case "d":
                        // ISO 8601 date stored as text
                        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var iso))
                            return ValueParser.FormatDate(iso, false);
                        return raw ?? "";
                }

                if (raw == null) return inline ?? "";
                if (_dateStyles.Contains(style) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
                {
                    try
                    {
                        if (_timeOnlyStyles.Contains(style) && serial < 1)
                            return TimeSpan.FromDays(serial).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
                        if (_date1904) serial += 1462;
                        var dt = DateTime.FromOADate(serial);
                        // Round to the nearest second to undo floating point noise
                        dt = new DateTime((dt.Ticks + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond);
                        return dt.TimeOfDay == TimeSpan.Zero
                            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                            : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    }
                    catch (ArgumentException) { return raw; }
                }

                // Plain numbers: Excel stores the shortest round-trip form; tidy binary noise like 0.30000000000000004
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                    raw.Length > 15 && raw.Contains(".") && !raw.Contains("E") && !raw.Contains("e"))
                {
                    var tidy = number.ToString("G15", CultureInfo.InvariantCulture);
                    if (!tidy.Contains("E")) return tidy;
                }
                return raw;
            }
        }
    }

    /// <summary>RFC 4180 CSV reader with delimiter and encoding detection.</summary>
    public static class CsvReader
    {
        public static List<KeyValuePair<int, string[]>> ReadRecords(string path)
        {
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                bytes = ms.ToArray();
            }
            return Parse(Decode(bytes));
        }

        internal static string Decode(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // Not UTF-8: fall back to the Windows ANSI code page (typical for "CSV (Comma delimited)" saved by Excel)
                return Encoding.Default.GetString(bytes);
            }
        }

        internal static char DetectDelimiter(string text)
        {
            var candidates = new[] { ',', ';', '\t', '|' };
            var counts = new int[candidates.Length];
            var inQuotes = false;
            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"') inQuotes = !inQuotes;
                else if (!inQuotes && (c == '\n' || c == '\r')) break;
                else if (!inQuotes)
                {
                    var k = Array.IndexOf(candidates, c);
                    if (k >= 0) counts[k]++;
                }
            }
            var best = 0;
            for (int k = 1; k < counts.Length; k++) if (counts[k] > counts[best]) best = k;
            return counts[best] == 0 ? ',' : candidates[best];
        }

        internal static List<KeyValuePair<int, string[]>> Parse(string text)
        {
            var records = new List<KeyValuePair<int, string[]>>();
            if (string.IsNullOrEmpty(text)) return records;
            if (text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase))
            {
                // Excel's "sep=;" hint line
                var nl = text.IndexOf('\n');
                var hint = text.Length > 4 ? text[4] : ',';
                text = nl >= 0 ? text.Substring(nl + 1) : "";
                return Parse(text, hint);
            }
            return Parse(text, DetectDelimiter(text));
        }

        internal static List<KeyValuePair<int, string[]>> Parse(string text, char delimiter)
        {
            var records = new List<KeyValuePair<int, string[]>>();
            var fields = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;
            var record = 1;
            var i = 0;

            void EndRecord()
            {
                fields.Add(field.ToString());
                field.Clear();
                if (!(fields.Count == 1 && fields[0].Length == 0))
                    records.Add(new KeyValuePair<int, string[]>(record, fields.ToArray()));
                fields.Clear();
                record++;
            }

            while (i < text.Length)
            {
                var c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                        inQuotes = false;
                        i++;
                        continue;
                    }
                    field.Append(c);
                    i++;
                    continue;
                }

                if (c == '"' && field.Length == 0) { inQuotes = true; i++; continue; }
                if (c == delimiter) { fields.Add(field.ToString()); field.Clear(); i++; continue; }
                if (c == '\r' || c == '\n')
                {
                    EndRecord();
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    i++;
                    continue;
                }
                field.Append(c);
                i++;
            }
            if (field.Length > 0 || fields.Count > 0) EndRecord();
            return records;
        }
    }
}
