using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using ChartDesignerPro.Comparison;

namespace ChartDesignerPro.Controls
{
    /// <summary>
    /// Read-only unified diff: removed lines on a red tint with "−", added lines on a green tint with "+", and the
    /// original/new line numbers. Built as RTF in one pass. The marker characters carry the meaning, not only the colour.
    /// </summary>
    internal sealed class DiffView : RichTextBox
    {
        public DiffView()
        {
            ReadOnly = true;
            BackColor = Color.White;
            BorderStyle = BorderStyle.None;
            Font = new Font("Consolas", 10F);
            WordWrap = false;
            DetectUrls = false;
            ScrollBars = RichTextBoxScrollBars.Both;
        }

        public void ShowMessage(string text)
        {
            Rtf = null;
            Text = text ?? string.Empty;
        }

        public void ShowDiff(DiffResult diff, bool changesOnly)
        {
            var sb = new StringBuilder();
            sb.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fmodern ").Append(Font.Name).Append(";}}");
            // 1 ink, 2 muted, 3 removed text, 4 added text, 5 removed back, 6 added back
            sb.Append(@"{\colortbl ;");
            foreach (var c in new[] { Palette.Ink, Palette.Muted, Palette.ErrorFore, Palette.Ok, Palette.ErrorBack, Palette.OkTint })
                sb.Append(@"\red").Append(c.R).Append(@"\green").Append(c.G).Append(@"\blue").Append(c.B).Append(';');
            sb.Append('}');
            sb.Append(@"\f0\fs").Append((int)Math.Round(Font.SizeInPoints * 2)).Append(' ');

            var skipped = 0;
            for (var i = 0; i < diff.Lines.Count; i++)
            {
                var line = diff.Lines[i];
                if (changesOnly && line.Kind == DiffKind.Same && !NearChange(diff, i, 2))
                {
                    skipped++;
                    continue;
                }
                if (skipped > 0)
                {
                    Append(sb, 2, 0, $"      ⋯ {skipped} unchanged line(s)");
                    skipped = 0;
                }
                var numbers = $"{(line.OldNumber > 0 ? line.OldNumber.ToString() : ""),5} {(line.NewNumber > 0 ? line.NewNumber.ToString() : ""),5} ";
                switch (line.Kind)
                {
                    case DiffKind.Removed: Append(sb, 3, 5, numbers + "− " + line.Text); break;
                    case DiffKind.Added: Append(sb, 4, 6, numbers + "+ " + line.Text); break;
                    default: Append(sb, 1, 0, numbers + "  " + line.Text); break;
                }
            }
            if (skipped > 0) Append(sb, 2, 0, $"      ⋯ {skipped} unchanged line(s)");
            sb.Append('}');
            Rtf = sb.ToString();
        }

        private static bool NearChange(DiffResult diff, int index, int context)
        {
            for (var j = Math.Max(0, index - context); j <= Math.Min(diff.Lines.Count - 1, index + context); j++)
                if (diff.Lines[j].Kind != DiffKind.Same) return true;
            return false;
        }

        private static void Append(StringBuilder sb, int colour, int highlight, string text)
        {
            sb.Append(@"\cf").Append(colour).Append(@"\highlight").Append(highlight).Append(' ');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\\': sb.Append(@"\\"); break;
                    case '{': sb.Append(@"\{"); break;
                    case '}': sb.Append(@"\}"); break;
                    case '\t': sb.Append(@"\tab "); break;
                    case '\r': break;
                    default:
                        if (c < 0x80) sb.Append(c);
                        else sb.Append(@"\u").Append((short)c).Append('?');
                        break;
                }
            }
            sb.Append(@"\highlight0\par ").Append(Environment.NewLine);
        }
    }
}
