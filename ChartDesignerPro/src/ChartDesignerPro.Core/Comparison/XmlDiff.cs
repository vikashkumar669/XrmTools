using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ChartDesignerPro.Xml;

namespace ChartDesignerPro.Comparison
{
    public enum DiffKind { Same, Removed, Added }

    public sealed class DiffLine
    {
        public DiffLine(DiffKind kind, int oldNumber, int newNumber, string text)
        {
            Kind = kind;
            OldNumber = oldNumber;
            NewNumber = newNumber;
            Text = text;
        }

        public DiffKind Kind { get; }

        /// <summary>1-based line in the original text, or 0 for added lines.</summary>
        public int OldNumber { get; }

        /// <summary>1-based line in the new text, or 0 for removed lines.</summary>
        public int NewNumber { get; }
        public string Text { get; }
    }

    public sealed class DiffResult
    {
        public List<DiffLine> Lines { get; } = new List<DiffLine>();
        public int Added => Lines.Count(l => l.Kind == DiffKind.Added);
        public int Removed => Lines.Count(l => l.Kind == DiffKind.Removed);
        public bool HasChanges => Added > 0 || Removed > 0;

        /// <summary>True when the formatting-insensitive comparison couldn't parse one side and fell back to an exact comparison.</summary>
        public bool FellBackToExact { get; set; }

        public string Summary => !HasChanges ? "No differences" : $"{Removed} line(s) removed, {Added} line(s) added";
    }

    /// <summary>
    /// Canonical form of XML for comparing: insignificant whitespace removed, attributes sorted by name, two-space
    /// indentation, comments kept. Only for display and comparison: never used to save a chart.
    /// </summary>
    public static class XmlNormalizer
    {
        public static string Normalize(string text)
        {
            if (!SafeXml.TryParse(text, out _, out _)) return null;
            XDocument doc;
            var readerSettings = SafeXml.CreateReaderSettings();
            readerSettings.IgnoreWhitespace = true;
            using (var reader = XmlReader.Create(new StringReader(text), readerSettings))
                doc = XDocument.Load(reader, LoadOptions.None);

            foreach (var e in doc.Descendants())
            {
                var sorted = e.Attributes().OrderBy(a => a.IsNamespaceDeclaration ? 0 : 1).ThenBy(a => a.Name.NamespaceName, StringComparer.Ordinal).ThenBy(a => a.Name.LocalName, StringComparer.Ordinal).ToList();
                e.RemoveAttributes();
                e.Add(sorted);
            }

            var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true, NewLineChars = "\n", NewLineHandling = NewLineHandling.Replace };
            var sb = new StringBuilder();
            using (var writer = XmlWriter.Create(new StringWriter(sb), settings))
                doc.Root?.Save(writer);
            return sb.ToString();
        }
    }

    /// <summary>Line diff (longest common subsequence). Chart XML is small, so the quadratic table is fine; very large inputs fall back to a simple comparison.</summary>
    public static class LineDiff
    {
        /// <summary>Above this many lines on both sides the LCS table would be too large; the diff then shows all old lines removed and all new lines added after a common prefix and suffix.</summary>
        public const int MaxCells = 4_000_000;

        public static DiffResult Compare(string oldText, string newText, bool ignoreFormatting)
        {
            var result = new DiffResult();
            if (ignoreFormatting)
            {
                var a = XmlNormalizer.Normalize(oldText);
                var b = XmlNormalizer.Normalize(newText);
                if (a != null && b != null)
                {
                    oldText = a;
                    newText = b;
                }
                else
                {
                    result.FellBackToExact = true;
                }
            }

            var oldLines = Split(oldText);
            var newLines = Split(newText);

            // Common prefix and suffix first: keeps the table small for typical edits.
            var prefix = 0;
            while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
            var suffix = 0;
            while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix &&
                   oldLines[oldLines.Length - 1 - suffix] == newLines[newLines.Length - 1 - suffix]) suffix++;

            for (var i = 0; i < prefix; i++) result.Lines.Add(new DiffLine(DiffKind.Same, i + 1, i + 1, oldLines[i]));

            var oldMid = oldLines.Skip(prefix).Take(oldLines.Length - prefix - suffix).ToArray();
            var newMid = newLines.Skip(prefix).Take(newLines.Length - prefix - suffix).ToArray();
            if ((long)oldMid.Length * newMid.Length <= MaxCells) Lcs(oldMid, newMid, prefix, result);
            else
            {
                for (var i = 0; i < oldMid.Length; i++) result.Lines.Add(new DiffLine(DiffKind.Removed, prefix + i + 1, 0, oldMid[i]));
                for (var j = 0; j < newMid.Length; j++) result.Lines.Add(new DiffLine(DiffKind.Added, 0, prefix + j + 1, newMid[j]));
            }

            for (var k = 0; k < suffix; k++)
            {
                var oi = oldLines.Length - suffix + k;
                var ni = newLines.Length - suffix + k;
                result.Lines.Add(new DiffLine(DiffKind.Same, oi + 1, ni + 1, oldLines[oi]));
            }
            return result;
        }

        private static void Lcs(string[] a, string[] b, int offset, DiffResult result)
        {
            var n = a.Length;
            var m = b.Length;
            var table = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);

            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (a[x] == b[y])
                {
                    result.Lines.Add(new DiffLine(DiffKind.Same, offset + x + 1, offset + y + 1, a[x]));
                    x++; y++;
                }
                else if (table[x + 1, y] >= table[x, y + 1])
                {
                    result.Lines.Add(new DiffLine(DiffKind.Removed, offset + x + 1, 0, a[x]));
                    x++;
                }
                else
                {
                    result.Lines.Add(new DiffLine(DiffKind.Added, 0, offset + y + 1, b[y]));
                    y++;
                }
            }
            for (; x < n; x++) result.Lines.Add(new DiffLine(DiffKind.Removed, offset + x + 1, 0, a[x]));
            for (; y < m; y++) result.Lines.Add(new DiffLine(DiffKind.Added, 0, offset + y + 1, b[y]));
        }

        private static string[] Split(string text) =>
            string.IsNullOrEmpty(text) ? new string[0] : text.Replace("\r\n", "\n").Split('\n');
    }
}
