using System;
using System.Drawing;
using System.Windows.Forms;

namespace ChartDesignerPro.Controls
{
    internal static class Palette
    {
        public static readonly Color Brand = Color.FromArgb(0x00, 0x69, 0x5C);
        public static readonly Color BrandLight = Color.FromArgb(0xE0, 0xF2, 0xF1);
        public static readonly Color Accent = Color.FromArgb(0xF5, 0x7C, 0x00);
        public static readonly Color Muted = Color.FromArgb(0x5E, 0x6B, 0x69);
        public static readonly Color Ink = Color.FromArgb(0x1D, 0x1F, 0x1E);

        public static readonly Color Ok = Color.FromArgb(0x2E, 0x7D, 0x32);
        public static readonly Color OkTint = Color.FromArgb(0xE8, 0xF5, 0xE9);
        public static readonly Color WarnBack = Color.FromArgb(0xFF, 0xF4, 0xE0);
        public static readonly Color WarnFore = Color.FromArgb(0x8A, 0x4B, 0x00);
        public static readonly Color ErrorBack = Color.FromArgb(0xFD, 0xEC, 0xEA);
        public static readonly Color ErrorFore = Color.FromArgb(0x9C, 0x00, 0x06);
        public static readonly Color InfoBack = Color.FromArgb(0xE8, 0xF1, 0xFB);
        public static readonly Color InfoFore = Color.FromArgb(0x0D, 0x47, 0xA1);

        // XML colouring (all meet WCAG AA contrast on white)
        public static readonly Color XmlTag = Color.FromArgb(0x80, 0x00, 0x00);
        public static readonly Color XmlAttribute = Color.FromArgb(0xB7, 0x1C, 0x1C);
        public static readonly Color XmlValue = Color.FromArgb(0x0D, 0x47, 0xA1);
        public static readonly Color XmlComment = Color.FromArgb(0x2E, 0x7D, 0x32);
        public static readonly Color XmlPunctuation = Color.FromArgb(0x1A, 0x23, 0x7E);
    }

    /// <summary>Double-buffered, read-only grid with the tool's header style.</summary>
    internal sealed class FastGrid : DataGridView
    {
        public FastGrid()
        {
            DoubleBuffered = true;
            AllowUserToAddRows = false;
            AllowUserToDeleteRows = false;
            AllowUserToResizeRows = false;
            RowHeadersVisible = false;
            ReadOnly = true;
            BackgroundColor = SystemColors.Window;
            BorderStyle = BorderStyle.None;
            SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            MultiSelect = false;
            EnableHeadersVisualStyles = false;
            ColumnHeadersDefaultCellStyle.BackColor = Palette.BrandLight;
            ColumnHeadersDefaultCellStyle.ForeColor = Palette.Brand;
            ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            ColumnHeadersDefaultCellStyle.SelectionBackColor = Palette.BrandLight;
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            ColumnHeadersHeight = 28;
            RowTemplate.Height = 22;
            GridColor = Color.FromArgb(0xE2, 0xE5, 0xEC);
            DefaultCellStyle.SelectionBackColor = Color.FromArgb(0xB2, 0xDF, 0xDB);
            DefaultCellStyle.SelectionForeColor = Color.Black;
            ShowCellToolTips = true;
            StandardTab = true; // Tab leaves the grid instead of moving between cells
        }
    }

    internal static class ToolStripFactory
    {
        public static ToolStripButton Button(string text, string tooltip, EventHandler click, bool bold = false)
        {
            var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tooltip };
            if (bold) b.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            if (click != null) b.Click += click;
            return b;
        }

        /// <summary>A toolbar toggle shown as ☑/☐ text (a CheckBox hosted in a ToolStrip doesn't reliably receive clicks).</summary>
        public static ToolStripButton Toggle(string text, string tooltip, bool initial, Action<bool> changed = null)
        {
            var b = new ToolStripButton { DisplayStyle = ToolStripItemDisplayStyle.Text, CheckOnClick = true, Checked = initial, ToolTipText = tooltip };
            void Refresh() => b.Text = (b.Checked ? "☑ " : "☐ ") + text;
            b.CheckedChanged += (s, e) =>
            {
                Refresh();
                changed?.Invoke(b.Checked);
            };
            Refresh();
            return b;
        }
    }

    /// <summary>A coloured one-line message strip (info, warning, error). Hidden when the text is empty.</summary>
    internal sealed class Banner : Label
    {
        public enum Kind { Info, Ok, Warning, Error }

        public Banner()
        {
            AutoSize = false;
            Dock = DockStyle.Top;
            Height = 28;
            Padding = new Padding(8, 0, 8, 0);
            TextAlign = ContentAlignment.MiddleLeft;
            Font = new Font("Segoe UI", 9F);
            Visible = false;
        }

        public void Show(Kind kind, string text)
        {
            if (string.IsNullOrEmpty(text)) { Visible = false; return; }
            string icon;
            switch (kind)
            {
                case Kind.Ok: BackColor = Palette.OkTint; ForeColor = Palette.Ok; icon = "✓ "; break;
                case Kind.Warning: BackColor = Palette.WarnBack; ForeColor = Palette.WarnFore; icon = "⚠ "; break;
                case Kind.Error: BackColor = Palette.ErrorBack; ForeColor = Palette.ErrorFore; icon = "✕ "; break;
                default: BackColor = Palette.InfoBack; ForeColor = Palette.InfoFore; icon = "ℹ "; break;
            }
            Text = icon + text; // the icon carries the meaning too, not only the colour
            Visible = true;
        }
    }
}
