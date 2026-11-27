using System;
using System.Collections.Generic;
using System.Linq;
using ChartDesignerPro.Models;

namespace ChartDesignerPro.Explorer
{
    public enum ChartSortColumn { Name, Table, Scope, Type, Modified }

    /// <summary>Search and sort for the Chart Explorer grid. Pure logic, so it is unit-tested.</summary>
    public static class ChartListFilter
    {
        /// <summary>
        /// Keeps charts whose name, description, table (logical or display name), type or id contain every
        /// space-separated term (case-insensitive). An id pasted with or without braces matches.
        /// </summary>
        public static IEnumerable<ChartRecord> Search(IEnumerable<ChartRecord> charts, string text)
        {
            if (charts == null) return Enumerable.Empty<ChartRecord>();
            var terms = (text ?? string.Empty)
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim('{', '}'))
                .Where(t => t.Length > 0)
                .ToArray();
            if (terms.Length == 0) return charts;
            return charts.Where(c => terms.All(t => Matches(c, t)));
        }

        private static bool Matches(ChartRecord c, string term)
        {
            return Contains(c.Name, term)
                || Contains(c.Description, term)
                || Contains(c.TableLogicalName, term)
                || Contains(c.TableDisplayName, term)
                || Contains(c.EngineText, term)
                || Contains(c.ScopeText, term)
                || Contains(c.Id.ToString(), term);
        }

        private static bool Contains(string value, string term) =>
            value != null && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Sorts by the given column; ties are broken by name, then id, so the order is stable.</summary>
        public static List<ChartRecord> Sort(IEnumerable<ChartRecord> charts, ChartSortColumn column, bool descending)
        {
            var list = (charts ?? Enumerable.Empty<ChartRecord>()).ToList();
            IOrderedEnumerable<ChartRecord> ordered;
            var text = StringComparer.CurrentCultureIgnoreCase;
            switch (column)
            {
                case ChartSortColumn.Table:
                    ordered = descending ? list.OrderByDescending(c => c.TableText, text) : list.OrderBy(c => c.TableText, text);
                    break;
                case ChartSortColumn.Scope:
                    ordered = descending ? list.OrderByDescending(c => c.ScopeText, text) : list.OrderBy(c => c.ScopeText, text);
                    break;
                case ChartSortColumn.Type:
                    ordered = descending ? list.OrderByDescending(c => c.EngineText, text) : list.OrderBy(c => c.EngineText, text);
                    break;
                case ChartSortColumn.Modified:
                    ordered = descending
                        ? list.OrderByDescending(c => c.ModifiedOnUtc ?? DateTime.MinValue)
                        : list.OrderBy(c => c.ModifiedOnUtc ?? DateTime.MinValue);
                    break;
                default:
                    ordered = descending ? list.OrderByDescending(c => c.Name, text) : list.OrderBy(c => c.Name, text);
                    break;
            }
            return ordered.ThenBy(c => c.Name, text).ThenBy(c => c.Id).ToList();
        }

        /// <summary>Distinct tables of the given charts, for the table filter, sorted by display text.</summary>
        public static List<TableSummary> TablesOf(IEnumerable<ChartRecord> charts)
        {
            return (charts ?? Enumerable.Empty<ChartRecord>())
                .Where(c => !string.IsNullOrEmpty(c.TableLogicalName))
                .GroupBy(c => c.TableLogicalName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new TableSummary
                {
                    LogicalName = g.Key,
                    DisplayName = g.Select(c => c.TableDisplayName).FirstOrDefault(n => !string.IsNullOrEmpty(n))
                })
                .OrderBy(t => t.Text, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }
}
