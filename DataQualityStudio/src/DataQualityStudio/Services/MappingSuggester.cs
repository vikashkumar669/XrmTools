using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DataQualityStudio.Models;

namespace DataQualityStudio.Services
{
    /// <summary>Matches spreadsheet headers to Dataverse columns by display name, logical name or schema name.</summary>
    public static class MappingSuggester
    {
        public const double SuggestThreshold = 0.82;

        public sealed class Match
        {
            public DvField Field;
            public string Kind;
            public double Score;
        }

        /// <summary>Best match for one header, ignoring fields in <paramref name="taken"/>.</summary>
        public static Match Suggest(string header, IEnumerable<DvField> fields, ISet<string> taken)
        {
            if (string.IsNullOrWhiteSpace(header)) return null;
            var candidates = fields.Where(f => taken == null || !taken.Contains(f.LogicalName)).ToList();
            var h = header.Trim();
            var hn = TextUtil.Normalize(h);
            if (hn.Length == 0) return null;

            // "Account Name (name)" or "name [Account Name]" style headers produced by exports and templates
            var bracket = Regex.Match(h, @"[\(\[]\s*([A-Za-z0-9_]+)\s*[\)\]]\s*$");
            if (bracket.Success)
            {
                var inner = bracket.Groups[1].Value;
                var byInner = candidates.FirstOrDefault(f => string.Equals(f.LogicalName, inner, StringComparison.OrdinalIgnoreCase));
                if (byInner != null) return new Match { Field = byInner, Kind = "Auto: logical name", Score = 1 };
            }

            var exact = candidates.FirstOrDefault(f => string.Equals(f.LogicalName, h, StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault(f => string.Equals(f.SchemaName, h, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return new Match { Field = exact, Kind = "Auto: logical name", Score = 1 };

            var hx = TextUtil.Normalize(TextUtil.ExpandAbbreviations(h));
            var display = candidates.Where(f => string.Equals(f.DisplayName, h, StringComparison.OrdinalIgnoreCase)).ToList();
            if (display.Count == 0) display = candidates.Where(f => TextUtil.Normalize(f.DisplayName) == hn).ToList();
            if (display.Count == 0 && hx != hn) display = candidates.Where(f => TextUtil.Normalize(TextUtil.ExpandAbbreviations(f.DisplayName)) == hx).ToList();
            if (display.Count > 0)
            {
                // Same display name on several columns: prefer the importable, non-system one
                var pick = display.OrderByDescending(f => f.IsValidForCreate).ThenBy(f => f.LogicalName.Contains("_") ? 0 : 1).First();
                return new Match { Field = pick, Kind = display.Count > 1 ? "Auto: display name (ambiguous)" : "Auto: display name", Score = 1 };
            }

            // Logical name without publisher prefix: "industry" for "new_industry"
            var unprefixed = candidates.FirstOrDefault(f =>
            {
                var i = f.LogicalName.IndexOf('_');
                return i > 0 && i < f.LogicalName.Length - 1 && TextUtil.Normalize(f.LogicalName.Substring(i + 1)) == hn;
            });
            if (unprefixed != null) return new Match { Field = unprefixed, Kind = "Auto: logical name", Score = 0.95 };

            var normalizedLogical = candidates.FirstOrDefault(f => TextUtil.Normalize(f.LogicalName) == hn);
            if (normalizedLogical != null) return new Match { Field = normalizedLogical, Kind = "Auto: logical name", Score = 0.95 };

            // Fuzzy
            Match best = null;
            foreach (var f in candidates)
            {
                var score = Math.Max(Math.Max(TextUtil.Similarity(h, f.DisplayName), TextUtil.Similarity(h, f.LogicalName)),
                                     TextUtil.Similarity(TextUtil.ExpandAbbreviations(h), TextUtil.ExpandAbbreviations(f.DisplayName)));
                if (best == null || score > best.Score) best = new Match { Field = f, Score = score };
            }
            if (best != null && best.Score >= SuggestThreshold)
            {
                best.Kind = $"Suggested ({best.Score:P0})";
                return best;
            }
            return null;
        }

        /// <summary>Closest field for an unmapped header, for "did you mean" hints (lower threshold).</summary>
        public static DvField Closest(string header, IEnumerable<DvField> fields, out double score)
        {
            score = 0;
            DvField best = null;
            foreach (var f in fields)
            {
                var s = Math.Max(Math.Max(TextUtil.Similarity(header, f.DisplayName), TextUtil.Similarity(header, f.LogicalName)),
                                 TextUtil.Similarity(TextUtil.ExpandAbbreviations(header), TextUtil.ExpandAbbreviations(f.DisplayName)));
                if (s > score) { score = s; best = f; }
            }
            return best;
        }

        /// <summary>Auto-maps every header in <paramref name="headers"/> that isn't mapped in <paramref name="existing"/>.</summary>
        public static List<ColumnMapping> AutoMap(IList<string> headers, DvEntity entity, IDictionary<string, ColumnMapping> existing, bool overwrite)
        {
            var result = new List<ColumnMapping>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!overwrite && existing != null)
                foreach (var m in existing.Values.Where(m => m.IsMapped && headers.Contains(m.SourceColumn, StringComparer.OrdinalIgnoreCase)))
                    taken.Add(m.TargetField);

            // Pass 1: keep existing mappings; pass 2: exact matches first so fuzzy guesses don't steal them
            var pending = new List<string>();
            foreach (var h in headers)
            {
                ColumnMapping current = null;
                existing?.TryGetValue(h, out current);
                if (!overwrite && current != null &&
                    ((current.IsMapped && entity.GetField(current.TargetField) != null) || current.MatchKind == ColumnMapping.Ignored))
                    result.Add(current.Clone());
                else
                {
                    var m = current?.Clone() ?? new ColumnMapping { SourceColumn = h };
                    m.TargetField = null;
                    m.MatchKind = "Unmapped";
                    result.Add(m);
                    pending.Add(h);
                }
            }

            foreach (var exactOnly in new[] { true, false })
            {
                foreach (var h in pending.ToList())
                {
                    var match = Suggest(h, entity.Fields, taken);
                    if (match == null) continue;
                    if (exactOnly && match.Score < 0.95) continue;
                    var m = result.First(r => r.SourceColumn == h);
                    m.TargetField = match.Field.LogicalName;
                    m.MatchKind = match.Kind;
                    taken.Add(match.Field.LogicalName);
                    pending.Remove(h);
                }
            }
            return result;
        }
    }
}
