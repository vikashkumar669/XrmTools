using System;
using System.Drawing;
using System.Windows.Forms;

namespace DiagnosticsWorkbench.Controls
{
    internal static class Palette
    {
        public static readonly Color Brand = Color.FromArgb(0x30, 0x3F, 0x9F);
        public static readonly Color BrandLight = Color.FromArgb(0xE8, 0xEA, 0xF6);
        public static readonly Color Muted = Color.FromArgb(0x5E, 0x6B, 0x69);
        public static readonly Color Ink = Color.FromArgb(0x1D, 0x1F, 0x1E);

        // One colour per result tab (left stripe and border of every card)
        public static readonly Color Error = Color.FromArgb(0xC6, 0x28, 0x28);
        public static readonly Color ErrorTint = Color.FromArgb(0xFD, 0xEC, 0xEA);
        public static readonly Color Slow = Color.FromArgb(0xE0, 0x8A, 0x00);
        public static readonly Color SlowTint = Color.FromArgb(0xFF, 0xF6, 0xE5);
        public static readonly Color Chain = Color.FromArgb(0x15, 0x65, 0xC0);
        public static readonly Color ChainTint = Color.FromArgb(0xE8, 0xF1, 0xFB);
        public static readonly Color Neutral = Color.FromArgb(0xA8, 0xAF, 0xAD);
        public static readonly Color NeutralTint = Color.FromArgb(0xF7, 0xF8, 0xF8);
        public static readonly Color Ok = Color.FromArgb(0x2E, 0x7D, 0x32);
        public static readonly Color FlagFore = Color.FromArgb(0x8A, 0x4B, 0x00);

        public static readonly Color BannerWarnBack = Color.FromArgb(0xFF, 0xF4, 0xE0);
        public static readonly Color BannerWarnFore = Color.FromArgb(0x8A, 0x4B, 0x00);
        public static readonly Color BannerCritBack = Color.FromArgb(0xFD, 0xEC, 0xEA);
        public static readonly Color BannerCritFore = Color.FromArgb(0x9C, 0x00, 0x06);
    }

    /// <summary>Double-buffered grid.</summary>
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
            DefaultCellStyle.SelectionBackColor = Color.FromArgb(0xD1, 0xD7, 0xF0);
            DefaultCellStyle.SelectionForeColor = Color.Black;
            ShowCellToolTips = true;
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
}
