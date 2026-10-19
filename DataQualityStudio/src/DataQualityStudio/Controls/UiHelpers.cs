using System;
using System.Drawing;
using System.Windows.Forms;

namespace DataQualityStudio.Controls
{
    internal static class Palette
    {
        public static readonly Color Brand = Color.FromArgb(0x0B, 0x5D, 0x57);
        public static readonly Color BrandLight = Color.FromArgb(0xE3, 0xF2, 0xEE);
        public static readonly Color ErrorBack = Color.FromArgb(0xFF, 0xD9, 0xDC);
        public static readonly Color ErrorFore = Color.FromArgb(0x9C, 0x00, 0x06);
        public static readonly Color WarningBack = Color.FromArgb(0xFF, 0xF0, 0xB8);
        public static readonly Color WarningFore = Color.FromArgb(0x7A, 0x4B, 0x00);
        public static readonly Color InfoBack = Color.FromArgb(0xE2, 0xEE, 0xFA);
        public static readonly Color OkFore = Color.FromArgb(0x00, 0x61, 0x00);
        public static readonly Color Muted = Color.FromArgb(0x70, 0x70, 0x70);
    }

    /// <summary>Double-buffered grid (smooth scrolling for large files).</summary>
    internal sealed class FastGrid : DataGridView
    {
        public FastGrid()
        {
            DoubleBuffered = true;
            AllowUserToAddRows = false;
            AllowUserToDeleteRows = false;
            AllowUserToResizeRows = false;
            RowHeadersVisible = false;
            BackgroundColor = SystemColors.Window;
            BorderStyle = BorderStyle.None;
            SelectionMode = DataGridViewSelectionMode.CellSelect;
            EnableHeadersVisualStyles = false;
            ColumnHeadersDefaultCellStyle.BackColor = Palette.BrandLight;
            ColumnHeadersDefaultCellStyle.ForeColor = Palette.Brand;
            ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            ColumnHeadersDefaultCellStyle.SelectionBackColor = Palette.BrandLight;
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            ColumnHeadersHeight = 30;
            RowTemplate.Height = 24;
            GridColor = Color.FromArgb(0xDD, 0xE5, 0xE3);
            DefaultCellStyle.SelectionBackColor = Color.FromArgb(0xC5, 0xE3, 0xDC);
            DefaultCellStyle.SelectionForeColor = Color.Black;
            ShowCellToolTips = true;
        }
    }

    /// <summary>Small modal text prompt.</summary>
    internal sealed class InputDialog : Form
    {
        private readonly TextBox _text;

        private InputDialog(string title, string prompt, string value)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(420, 130);

            var label = new Label { Text = prompt, Left = 12, Top = 12, Width = 396, AutoSize = false, Height = 20 };
            _text = new TextBox { Left = 12, Top = 36, Width = 396, Text = value ?? "" };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 252, Top = 84, Width = 75 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 333, Top = 84, Width = 75 };
            Controls.AddRange(new Control[] { label, _text, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public static string Ask(IWin32Window owner, string title, string prompt, string value)
        {
            using (var dlg = new InputDialog(title, prompt, value))
            {
                dlg._text.SelectAll();
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg._text.Text.Trim() : null;
            }
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

        public static ToolStripComboBox Combo(int width, params object[] items)
        {
            var c = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = width, FlatStyle = FlatStyle.System };
            c.Items.AddRange(items);
            if (c.Items.Count > 0) c.SelectedIndex = 0;
            return c;
        }
    }
}
