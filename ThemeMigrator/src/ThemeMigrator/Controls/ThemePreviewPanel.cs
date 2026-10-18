using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using ThemeMigrator.Models;

namespace ThemeMigrator.Controls
{
    /// <summary>
    /// Draws a mock app header (nav bar + logo + title) and a swatch grid of every color in the theme.
    /// </summary>
    public class ThemePreviewPanel : Control
    {
        private List<KeyValuePair<string, string>> _swatches = new List<KeyValuePair<string, string>>();
        private string _title = "";
        private Color _headerBack = SystemColors.ControlDark;
        private Color? _accent;
        private Image _logo;
        private string _note = "Select a theme to preview it.";
        private readonly Font _titleFont = new Font("Segoe UI", 11F, FontStyle.Bold);

        public ThemePreviewPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = SystemColors.Window;
            Font = new Font("Segoe UI", 9F);
        }

        public void ShowTheme(string title, string headerColor, string accentColor, Image logo,
            IEnumerable<KeyValuePair<string, string>> swatches, string note = null)
        {
            _title = title ?? "";
            _headerBack = ColorUtil.Parse(headerColor) ?? Color.FromArgb(0x74, 0x27, 0x74);
            _accent = ColorUtil.Parse(accentColor);
            _logo?.Dispose();
            _logo = logo;
            _swatches = (swatches ?? Enumerable.Empty<KeyValuePair<string, string>>()).ToList();
            _note = note;
            AutoScrollMinHeightChanged();
            Invalidate();
        }

        public void Clear(string note = "Select a theme to preview it.")
        {
            _logo?.Dispose();
            _logo = null;
            _swatches.Clear();
            _title = "";
            _note = note;
            Invalidate();
        }

        /// <summary>Keeps the host scroll panel tall enough to show every swatch.</summary>
        private void AutoScrollMinHeightChanged()
        {
            var columns = Math.Max(1, (Width - 24) / 230);
            var rows = (int)Math.Ceiling(_swatches.Count / (double)columns);
            var needed = 140 + rows * 34;
            if (Parent is ScrollableControl host)
                host.AutoScrollMinSize = new Size(0, needed);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            AutoScrollMinHeightChanged();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            if (_swatches.Count == 0 && string.IsNullOrEmpty(_title))
            {
                TextRenderer.DrawText(g, _note ?? "", Font, ClientRectangle, SystemColors.GrayText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            // Mock app header
            var header = new Rectangle(12, 12, Width - 24, 48);
            using (var b = new SolidBrush(_headerBack)) g.FillRectangle(b, header);
            var textColor = ColorUtil.ContrastText(_headerBack);
            var x = header.Left + 10;
            if (_logo != null)
            {
                var h = header.Height - 12;
                var w = Math.Min(160, (int)(_logo.Width * (h / (double)_logo.Height)));
                g.DrawImage(_logo, new Rectangle(x, header.Top + 6, w, h));
                x += w + 12;
            }
            TextRenderer.DrawText(g, _title, _titleFont,
                new Rectangle(x, header.Top, header.Right - x - 10, header.Height), textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (_accent.HasValue)
            {
                using (var b = new SolidBrush(_accent.Value)) g.FillRectangle(b, header.Left, header.Bottom, header.Width, 4);
            }

            var y = header.Bottom + 14;
            if (!string.IsNullOrEmpty(_note))
            {
                var noteRect = new Rectangle(12, y, Width - 24, 36);
                TextRenderer.DrawText(g, _note, Font, noteRect, SystemColors.GrayText, TextFormatFlags.WordBreak);
                y += 40;
            }

            // Swatches
            var columns = Math.Max(1, (Width - 24) / 230);
            var cellWidth = (Width - 24) / columns;
            for (var i = 0; i < _swatches.Count; i++)
            {
                var col = i % columns;
                var row = i / columns;
                var cell = new Rectangle(12 + col * cellWidth, y + row * 34, cellWidth - 8, 28);
                var color = ColorUtil.Parse(_swatches[i].Value) ?? Color.Transparent;
                var chip = new Rectangle(cell.Left, cell.Top + 2, 24, 24);
                using (var b = new SolidBrush(color)) g.FillRectangle(b, chip);
                g.DrawRectangle(SystemPens.ControlDark, chip);
                TextRenderer.DrawText(g, $"{_swatches[i].Key}  {_swatches[i].Value}", Font,
                    new Rectangle(chip.Right + 6, cell.Top, cell.Width - 30, cell.Height), ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _logo?.Dispose();
                _titleFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
