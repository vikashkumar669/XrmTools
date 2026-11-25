using System;
using System.Collections.Generic;
using System.Linq;
using ChartDesignerPro.Models;

namespace ChartDesignerPro.Explorer
{
    public enum ChartListView { All, Favourites, Recent }

    /// <summary>Favourite and recently opened charts, kept in the per-connection settings as "Scope:id" keys.</summary>
    public static class ChartBookmarks
    {
        public const int MaxRecent = 15;

        public static string Key(ChartRecord record) => record == null ? null : record.Scope + ":" + record.Id.ToString("D");

        public static bool IsFavourite(ToolSettings settings, ChartRecord record) =>
            settings?.Favourites != null && record != null && settings.Favourites.Contains(Key(record), StringComparer.OrdinalIgnoreCase);

        /// <summary>Adds or removes the chart from favourites and returns whether it is now a favourite.</summary>
        public static bool ToggleFavourite(ToolSettings settings, ChartRecord record)
        {
            if (settings == null || record == null) return false;
            settings.Favourites = settings.Favourites ?? new List<string>();
            var key = Key(record);
            var existing = settings.Favourites.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                settings.Favourites.Remove(existing);
                return false;
            }
            settings.Favourites.Add(key);
            return true;
        }

        /// <summary>Moves the chart to the front of the recent list, keeping at most <see cref="MaxRecent"/> entries.</summary>
        public static void AddRecent(ToolSettings settings, ChartRecord record)
        {
            if (settings == null || record == null) return;
            var key = Key(record);
            var list = (settings.Recent ?? new List<string>())
                .Where(k => !string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            list.Insert(0, key);
            settings.Recent = list.Take(MaxRecent).ToList();
        }

        /// <summary>
        /// Applies the view. Favourites keep the caller's order; Recent is ordered by recency.
        /// Keys of charts that aren't in <paramref name="charts"/> (deleted, or not loaded) are ignored.
        /// </summary>
        public static IEnumerable<ChartRecord> Apply(IEnumerable<ChartRecord> charts, ChartListView view, ToolSettings settings)
        {
            charts = charts ?? Enumerable.Empty<ChartRecord>();
            switch (view)
            {
                case ChartListView.Favourites:
                {
                    var keys = new HashSet<string>(settings?.Favourites ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                    return charts.Where(c => keys.Contains(Key(c)));
                }
                case ChartListView.Recent:
                {
                    var order = (settings?.Recent ?? new List<string>())
                        .Select((k, i) => new { k, i })
                        .GroupBy(x => x.k, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
                    return charts.Where(c => order.ContainsKey(Key(c))).OrderBy(c => order[Key(c)]);
                }
                default:
                    return charts;
            }
        }
    }
}
