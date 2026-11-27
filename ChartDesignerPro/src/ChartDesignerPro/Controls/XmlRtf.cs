using System;
using System.Drawing;
using System.Text;

namespace ChartDesignerPro.Controls
{
    /// <summary>
    /// Builds coloured RTF for XML in one pass (tags, attributes, values, comments). Used by <see cref="XmlEditorBox"/>.
    /// Colouring is lexical only: it never changes the text.
    /// </summary>
    internal static class XmlRtf
    {
        /// <summary>Above this size the text is shown without colouring, to keep the UI responsive.</summary>
        public const int ColourLimit = 300_000;

        // Colour table indexes (1-based in RTF): 1 ink, 2 tag, 3 attribute, 4 value, 5 comment, 6 punctuation
        public static string BuildRtf(string text, Font font)
        {
            var sb = new StringBuilder(text.Length * 2);
            sb.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fmodern ").Append(font.Name).Append(";}}");
            sb.Append(@"{\colortbl ;");
            foreach (var c in new[] { Palette.Ink, Palette.XmlTag, Palette.XmlAttribute, Palette.XmlValue, Palette.XmlComment, Palette.XmlPunctuation })
                sb.Append(@"\red").Append(c.R).Append(@"\green").Append(c.G).Append(@"\blue").Append(c.B).Append(';');
            sb.Append('}');
            sb.Append(@"\f0\fs").Append((int)Math.Round(font.SizeInPoints * 2)).Append(' ');

            var i = 0;
            var n = text.Length;
            while (i < n)
            {
                if (StartsWith(text, i, "<!--"))
                {
                    var end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    end = end < 0 ? n : end + 3;
                    Emit(sb, 5, text, i, end);
                    i = end;
                }
                else if (StartsWith(text, i, "<![CDATA["))
                {
                    var end = text.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                    end = end < 0 ? n : end + 3;
                    Emit(sb, 1, text, i, end);
                    i = end;
                }
                else if (text[i] == '<')
                {
                    i = EmitTag(sb, text, i);
                }
                else
                {
                    var next = text.IndexOf('<', i);
                    if (next < 0) next = n;
                    Emit(sb, 1, text, i, next);
                    i = next;
                }
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Emits one tag starting at '&lt;' and returns the index after it.</summary>
        private static int EmitTag(StringBuilder sb, string text, int start)
        {
            var n = text.Length;
            var i = start;
            // "<", "</", "<?"
            var p = i + 1;
            if (p < n && (text[p] == '/' || text[p] == '?' || text[p] == '!')) p++;
            Emit(sb, 6, text, i, p);
            i = p;

            // element name
            var nameEnd = i;
            while (nameEnd < n && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] != '>' && text[nameEnd] != '/' && text[nameEnd] != '?') nameEnd++;
            Emit(sb, 2, text, i, nameEnd);
            i = nameEnd;

            while (i < n)
            {
                var ch = text[i];
                if (ch == '>')
                {
                    Emit(sb, 6, text, i, i + 1);
                    return i + 1;
                }
                if (ch == '/' || ch == '?' || ch == '=')
                {
                    Emit(sb, 6, text, i, i + 1);
                    i++;
                }
                else if (ch == '"' || ch == '\'')
                {
                    var close = text.IndexOf(ch, i + 1);
                    close = close < 0 ? n : close + 1;
                    Emit(sb, 4, text, i, close);
                    i = close;
                }
                else if (char.IsWhiteSpace(ch))
                {
                    var end = i;
                    while (end < n && char.IsWhiteSpace(text[end])) end++;
                    Emit(sb, 1, text, i, end);
                    i = end;
                }
                else
                {
                    var end = i;
                    while (end < n && !char.IsWhiteSpace(text[end]) && text[end] != '=' && text[end] != '>' && text[end] != '/' && text[end] != '"' && text[end] != '\'') end++;
                    if (end == i) end = i + 1;
                    Emit(sb, 3, text, i, end);
                    i = end;
                }
            }
            return n;
        }

        private static bool StartsWith(string text, int index, string value) =>
            string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

        private static void Emit(StringBuilder sb, int colour, string text, int start, int end)
        {
            if (end <= start) return;
            sb.Append(@"\cf").Append(colour).Append(' ');
            for (var i = start; i < end; i++)
            {
                var c = text[i];
                switch (c)
                {
                    case '\\': sb.Append(@"\\"); break;
                    case '{': sb.Append(@"\{"); break;
                    case '}': sb.Append(@"\}"); break;
                    case '\r': break;
                    case '\n': sb.Append(@"\par ").Append(Environment.NewLine); break;
                    case '\t': sb.Append(@"\tab "); break;
                    default:
                        if (c < 0x80) sb.Append(c);
                        else sb.Append(@"\u").Append((short)c).Append('?');
                        break;
                }
            }
        }
    }
}
