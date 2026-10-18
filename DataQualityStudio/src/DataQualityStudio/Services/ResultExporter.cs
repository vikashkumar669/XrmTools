using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DataQualityStudio.Models;

namespace DataQualityStudio.Services
{
    /// <summary>Writes validation results (Excel with summary, issues and corrected data; or CSV) and blank import templates.</summary>
    public static class ResultExporter
    {
        public static void ExportExcel(string path, SourceTable table, ValidationResult result, IList<ColumnMapping> mappings, string connectionName)
        {
            using (var x = new XlsxWriter(path))
            {
                x.AddSheet("Summary", SummaryRows(table, result, mappings, connectionName), new double[] { 30, 28, 22, 16, 16, 22, 26, 26 }, -1);
                x.AddSheet("Issues", IssueRows(result), new double[] { 8, 10, 24, 26, 26, 60, 50, 30 });
                x.AddSheet("Data with fixes", CorrectedRows(table, result), CorrectedWidths(table));
                x.AddSheet("Transformed preview", PreviewRows(table, result), new[] { 8d, 10d }.Concat(result.Columns.Select(c => 22d)).ToArray());
                x.Save();
            }
        }

        private static IEnumerable<IList<XCell>> SummaryRows(SourceTable table, ValidationResult result, IList<ColumnMapping> mappings, string connectionName)
        {
            yield return new List<XCell> { XCell.Of("Data Quality Studio — validation summary", CellStyle.Title) };
            yield return new List<XCell>();
            IList<XCell> Kv(string k, string v, CellStyle s = CellStyle.Normal) => new List<XCell> { XCell.Of(k, CellStyle.Bold), XCell.Of(v, s) };
            IList<XCell> Kn(string k, double v, CellStyle s = CellStyle.Normal) => new List<XCell> { XCell.Of(k, CellStyle.Bold), XCell.Num(v, s) };

            yield return Kv("Source file", table.FilePath);
            yield return Kv("Sheet", table.SheetName);
            yield return Kn("Header row", table.HeaderRow);
            yield return Kv("Dataverse table", $"{result.EntityDisplayName} ({result.EntityLogicalName})");
            yield return Kv("Environment", connectionName ?? "");
            yield return Kv("Import mode", result.Options.Mode == ImportMode.Create ? "Create new records" : "Update existing records");
            yield return Kv("Date format", result.Options.DateOrder.ToString());
            yield return Kv("Number format", result.Options.NumberFormat.ToString());
            yield return Kv("Lookups checked", result.Options.CheckLookups ? "Yes" : "No");
            yield return Kv("Validated on", result.ValidatedOn.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            yield return new List<XCell>();
            yield return Kn("Rows", result.TotalRows);
            yield return Kn("Rows ready (no errors or warnings)", result.CleanRows, CellStyle.Corrected);
            yield return Kn("Rows with warnings only", result.RowsWithWarnings, result.RowsWithWarnings > 0 ? CellStyle.Warning : CellStyle.Normal);
            yield return Kn("Rows with errors", result.RowsWithErrors, result.RowsWithErrors > 0 ? CellStyle.Error : CellStyle.Normal);
            yield return Kn("Errors", result.ErrorCount, result.ErrorCount > 0 ? CellStyle.Error : CellStyle.Normal);
            yield return Kn("Warnings", result.WarningCount, result.WarningCount > 0 ? CellStyle.Warning : CellStyle.Normal);
            yield return Kv("Unmapped columns", result.UnmappedColumns.Count == 0 ? "None" : string.Join(", ", result.UnmappedColumns),
                result.UnmappedColumns.Count > 0 ? CellStyle.Info : CellStyle.Normal);
            if (result.IssuesTruncated)
                yield return Kv("Note", "The issue list was truncated; counts include every issue.", CellStyle.Warning);
            yield return new List<XCell>();
            yield return new List<XCell>
            {
                XCell.Of("Source column", CellStyle.Header), XCell.Of("Dataverse column", CellStyle.Header), XCell.Of("Type", CellStyle.Header),
                XCell.Of("Required", CellStyle.Header), XCell.Of("Transform", CellStyle.Header), XCell.Of("Default value", CellStyle.Header),
                XCell.Of("Value map", CellStyle.Header), XCell.Of("Lookup match column", CellStyle.Header)
            };
            foreach (var header in table.Headers)
            {
                var col = result.Columns.FirstOrDefault(c => string.Equals(c.Header, header, StringComparison.OrdinalIgnoreCase));
                if (col == null)
                {
                    yield return new List<XCell> { header, XCell.Of("(not mapped)", CellStyle.Info) };
                    continue;
                }
                yield return new List<XCell>
                {
                    header, col.Field.ComboText, col.Field.TypeLabel, col.Field.RequiredLabel,
                    col.Mapping.Transform == Transforms.None ? "" : col.Mapping.Transform, col.Mapping.DefaultValue, col.Mapping.ValueMap,
                    col.Mapping.LookupMatchField
                };
            }
        }

        private static IEnumerable<IList<XCell>> IssueRows(ValidationResult result)
        {
            yield return new List<XCell>
            {
                XCell.Of("Row", CellStyle.Header), XCell.Of("Severity", CellStyle.Header), XCell.Of("Source column", CellStyle.Header),
                XCell.Of("Dataverse column", CellStyle.Header), XCell.Of("Value", CellStyle.Header), XCell.Of("Message", CellStyle.Header),
                XCell.Of("Suggested fix", CellStyle.Header), XCell.Of("Corrected value", CellStyle.Header)
            };
            foreach (var i in OrderedIssues(result))
            {
                var style = StyleFor(i.Severity);
                yield return new List<XCell>
                {
                    i.RowNumber.HasValue ? XCell.Num(i.RowNumber.Value) : XCell.Of("—"),
                    XCell.Of(i.Severity.ToString(), style),
                    i.Column, i.Field, i.Value, i.Message, i.Suggestion,
                    i.CorrectedValue == null ? XCell.Of(null) : XCell.Of(i.CorrectedValue, CellStyle.Corrected)
                };
            }
        }

        public static IEnumerable<ValidationIssue> OrderedIssues(ValidationResult result) =>
            result.Issues.OrderBy(i => i.RowIndex < 0 ? 0 : 1).ThenBy(i => i.RowIndex).ThenByDescending(i => i.Severity).ThenBy(i => i.SourceIndex);

        private static CellStyle StyleFor(Severity s) =>
            s == Severity.Error ? CellStyle.Error : s == Severity.Warning ? CellStyle.Warning : CellStyle.Info;

        private static double[] CorrectedWidths(SourceTable table) =>
            new[] { 8d, 14d }.Concat(table.Headers.Select(h => Math.Max(12d, Math.Min(40d, h.Length + 4)))).Concat(new[] { 90d }).ToArray();

        /// <summary>The original rows with every concrete suggestion applied, colored by outcome.</summary>
        private static IEnumerable<IList<XCell>> CorrectedRows(SourceTable table, ValidationResult result)
        {
            // "DQ issues" is the last column: its long text can then only run into empty space on the right,
            // never over an empty data cell (Excel lets text overflow into empty neighbouring cells)
            var header = new List<XCell> { XCell.Of("Row", CellStyle.Header), XCell.Of("DQ status", CellStyle.Header) };
            header.AddRange(table.Headers.Select(h => XCell.Of(h, CellStyle.Header)));
            header.Add(XCell.Of("DQ issues", CellStyle.Header));
            yield return header;

            var byRow = result.Issues.Where(i => i.RowIndex >= 0).GroupBy(i => i.RowIndex).ToDictionary(g => g.Key, g => g.ToList());
            for (int r = 0; r < table.Rows.Count; r++)
            {
                var row = table.Rows[r];
                byRow.TryGetValue(r, out var issues);
                issues = issues ?? new List<ValidationIssue>();

                var cells = new List<XCell>(table.Headers.Count + 3);
                var rowSev = result.RowSeverity != null && r < result.RowSeverity.Length ? result.RowSeverity[r] : (byte)0;
                var fixable = issues.Count > 0 && issues.All(i => i.CorrectedValue != null || i.Severity < Severity.Error);
                cells.Add(XCell.Num(row.RowNumber));
                cells.Add(rowSev >= (byte)Severity.Error
                    ? XCell.Of(fixable ? "Fixed (review)" : "Error", fixable ? CellStyle.Corrected : CellStyle.Error)
                    : rowSev == (byte)Severity.Warning ? XCell.Of("Warning", CellStyle.Warning)
                    : XCell.Of("OK", CellStyle.Corrected));

                for (int c = 0; c < table.Headers.Count; c++)
                {
                    var cellIssues = issues.Where(i => i.SourceIndex == c).ToList();
                    var value = row.Get(c);
                    if (cellIssues.Count == 0) { cells.Add(value); continue; }
                    var fix = cellIssues.FirstOrDefault(i => i.CorrectedValue != null && i.Severity == Severity.Error)
                              ?? cellIssues.FirstOrDefault(i => i.CorrectedValue != null);
                    var worst = cellIssues.Max(i => i.Severity);
                    if (fix != null) cells.Add(XCell.Of(fix.CorrectedValue, CellStyle.Corrected));
                    else cells.Add(XCell.Of(value, StyleFor(worst)));
                }
                cells.Add(XCell.Of(string.Join(" | ", issues.Select(i => $"{i.Column}: {i.Message}")), CellStyle.Normal));
                yield return cells;
            }
        }

        private static IEnumerable<IList<XCell>> PreviewRows(SourceTable table, ValidationResult result)
        {
            var header = new List<XCell> { XCell.Of("Row", CellStyle.Header), XCell.Of("Status", CellStyle.Header) };
            header.AddRange(result.Columns.Select(c => XCell.Of($"{c.Field.DisplayName} ({c.Field.LogicalName})", CellStyle.Header)));
            yield return header;
            for (int r = 0; r < table.Rows.Count; r++)
            {
                var sev = result.RowSeverity[r];
                var cells = new List<XCell>
                {
                    XCell.Num(table.Rows[r].RowNumber),
                    sev >= (byte)Severity.Error ? XCell.Of("Error", CellStyle.Error) : sev == (byte)Severity.Warning ? XCell.Of("Warning", CellStyle.Warning) : XCell.Of("OK", CellStyle.Corrected)
                };
                for (int c = 0; c < result.Columns.Count; c++)
                {
                    var s = result.CellSeverity[r][c];
                    cells.Add(XCell.Of(result.Preview[r][c], s >= (byte)Severity.Error ? CellStyle.Error : s == (byte)Severity.Warning ? CellStyle.Warning : CellStyle.Normal));
                }
                yield return cells;
            }
        }

        // ============================== CSV ==============================

        public static void ExportCsv(string path, ValidationResult result)
        {
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.WriteLine(string.Join(",", new[] { "Row", "Severity", "Source column", "Dataverse column", "Value", "Message", "Suggested fix", "Corrected value" }.Select(Csv)));
                foreach (var i in OrderedIssues(result))
                {
                    w.WriteLine(string.Join(",", new[]
                    {
                        i.RowNumber?.ToString(CultureInfo.InvariantCulture) ?? "", i.Severity.ToString(), i.Column, i.Field, i.Value,
                        i.Message, i.Suggestion, i.CorrectedValue
                    }.Select(Csv)));
                }
            }
        }

        /// <summary>The corrected data as CSV (same content as the "Data with fixes" sheet, without colors).</summary>
        public static void ExportCorrectedCsv(string path, SourceTable table, ValidationResult result)
        {
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                foreach (var row in CorrectedRows(table, result))
                    w.WriteLine(string.Join(",", row.Select(c => Csv(c.Number.HasValue ? c.Number.Value.ToString("R", CultureInfo.InvariantCulture) : c.Text, false))));
            }
        }

        private static string Csv(string s) => Csv(s, true);

        /// <param name="guardFormulas">Prefix values that Excel would run as a formula with an apostrophe. Off for data meant for re-import.</param>
        private static string Csv(string s, bool guardFormulas)
        {
            s = s ?? "";
            if (guardFormulas && s.Length > 0 && "=+-@".IndexOf(s[0]) >= 0 && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) s = "'" + s;
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        // ============================== Template ==============================

        public static void ExportTemplate(string path, DvEntity entity, IList<DvField> fields)
        {
            using (var x = new XlsxWriter(path))
            {
                var header = fields.Select(f => XCell.Of(f.DisplayName, f.IsRequired ? CellStyle.Error : CellStyle.Header)).ToList();
                x.AddSheet(entity.DisplayName, new List<IList<XCell>> { header }, fields.Select(f => Math.Max(14d, Math.Min(40d, f.DisplayName.Length + 4))).ToArray());

                var reference = new List<IList<XCell>>
                {
                    new List<XCell>
                    {
                        XCell.Of("Header", CellStyle.Header), XCell.Of("Logical name", CellStyle.Header), XCell.Of("Type", CellStyle.Header),
                        XCell.Of("Required", CellStyle.Header), XCell.Of("Max length", CellStyle.Header), XCell.Of("Allowed values / format", CellStyle.Header)
                    }
                };
                foreach (var f in fields)
                {
                    string allowed;
                    if (f.IsChoice) allowed = string.Join("; ", f.Options.Select(o => o.Label));
                    else if (f.Kind == FieldKind.Boolean) allowed = $"{f.TrueLabel}; {f.FalseLabel}";
                    else if (f.IsLookup) allowed = $"Name of a {string.Join(" / ", f.Targets)} record, or its GUID" + (f.Targets.Length > 1 ? " (prefix with table, e.g. contact:Jane Doe)" : "");
                    else if (f.Kind == FieldKind.DateTime) allowed = f.DateOnly ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm";
                    else if (f.MinValue.HasValue || f.MaxValue.HasValue) allowed = $"{f.MinValue:0.##} to {f.MaxValue:0.##}" + (f.Precision.HasValue ? $", {f.Precision} decimals" : "");
                    else allowed = f.Format;
                    reference.Add(new List<XCell>
                    {
                        f.DisplayName, f.LogicalName, f.TypeLabel, XCell.Of(f.IsRequired ? "Required" : f.RequiredLabel, f.IsRequired ? CellStyle.Error : CellStyle.Normal),
                        f.MaxLength.HasValue ? XCell.Num(f.MaxLength.Value) : XCell.Of(null), XCell.Of(allowed, CellStyle.Wrap)
                    });
                }
                x.AddSheet("Column reference", reference, new double[] { 30, 28, 22, 14, 12, 80 });
                x.Save();
            }
        }
    }
}
