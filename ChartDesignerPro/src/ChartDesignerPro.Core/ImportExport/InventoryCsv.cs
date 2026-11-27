using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ChartDesignerPro.Models;

namespace ChartDesignerPro.ImportExport
{
    /// <summary>Chart list as CSV (UTF-8 with BOM so Excel shows non-ASCII names correctly).</summary>
    public static class InventoryCsv
    {
        public static readonly string[] Headers =
        {
            "Name", "Table (logical)", "Table (display)", "Scope", "Type", "Default", "Managed", "Customizable",
            "Modified on (UTC)", "Modified by", "Owner", "Chart id", "Description"
        };

        public static string Write(IEnumerable<ChartRecord> charts)
        {
            var sb = new StringBuilder();
            sb.Append('﻿');
            sb.AppendLine(string.Join(",", Array.ConvertAll(Headers, Escape)));
            foreach (var c in charts ?? new ChartRecord[0])
            {
                var cells = new[]
                {
                    c.Name, c.TableLogicalName, c.TableDisplayName, c.ScopeText, c.EngineText,
                    c.IsDefault ? "Yes" : "No",
                    c.IsManaged == null ? "" : c.IsManaged == true ? "Yes" : "No",
                    c.IsCustomizable == null ? "" : c.IsCustomizable == true ? "Yes" : "No",
                    c.ModifiedOnUtc?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    c.ModifiedByName, c.OwnerName, c.Id.ToString("D"), c.Description
                };
                sb.AppendLine(string.Join(",", Array.ConvertAll(cells, Escape)));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Quotes a cell when needed and neutralises spreadsheet formulas: a cell starting with = + - @ (or a tab or carriage
        /// return) gets a leading apostrophe, so opening the file in Excel never runs a formula from chart data.
        /// </summary>
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if ("=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
            var needsQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || value.StartsWith(" ", StringComparison.Ordinal) || value.EndsWith(" ", StringComparison.Ordinal);
            return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
