using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DiagnosticsWorkbench.Controls
{
    /// <summary>What one card shows. Built from an error group, a performance stat, a chain or a log.</summary>
    internal sealed class Card
    {
        public string Title { get; set; }
        public List<string> Lines { get; } = new List<string>();

        /// <summary>Optional line drawn in the flag colour (configuration flags).</summary>
        public string FlagLine { get; set; }

        /// <summary>Large figure at the top right (hit count, p95…).</summary>
        public string Badge { get; set; }
        public string BadgeCaption { get; set; }

        public Color Accent { get; set; } = Palette.Neutral;
        public Color Tint { get; set; } = Palette.NeutralTint;

        /// <summary>Draws a red border regardless of the accent (near timeout, failed chain).</summary>
        public bool Alert { get; set; }

        public Color? TitleColor { get; set; }
        public object Tag { get; set; }
    }

    /// <summary>
    /// Owner-drawn list of cards. Fixed item height keeps it fast with tens of thousands of items.
    /// </summary>
    internal sealed class CardList : ListBox
    {
        private const int Gap = 6, Stripe = 6, Radius = 8;
        private readonly Font _titleFont = new Font("Segoe UI", 9.75F, FontStyle.Bold);
        private readonly Font _lineFont = new Font("Segoe UI", 9F);
        private readonly Font _badgeFont = new Font("Segoe UI", 14F, FontStyle.Bold);
        private readonly Font _captionFont = new Font("Segoe UI", 7.5F);
        private string _emptyText = "Nothing to show.";

        public CardList(int cardHeight)
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = Math.Min(255, cardHeight);
            IntegralHeight = false;
            BorderStyle = BorderStyle.None;
            BackColor = Color.White;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            HorizontalScrollbar = false;
        }

        public string EmptyText
        {
            get => _emptyText;
            set { _emptyText = value; Invalidate(); }
        }

        public Card SelectedCard => SelectedItem as Card;

        public void SetCards(IList<Card> cards)
        {
            BeginUpdate();
            Items.Clear();
            if (cards != null && cards.Count > 0)
            {
                var array = new object[cards.Count];
                for (var i = 0; i < cards.Count; i++) array[i] = cards[i];
                Items.AddRange(array);
            }
            EndUpdate();
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            const int WM_PAINT = 0x000F;
            if (m.Msg == WM_PAINT && Items.Count == 0 && !string.IsNullOrEmpty(_emptyText))
            {
                using (var g = CreateGraphics())
                    TextRenderer.DrawText(g, _emptyText, _lineFont, new Rectangle(0, 24, ClientSize.Width, 40), Palette.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var g = e.Graphics;
            g.FillRectangle(Brushes.White, e.Bounds);
            if (e.Index < 0 || e.Index >= Items.Count || !(Items[e.Index] is Card card)) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            var rect = new Rectangle(e.Bounds.X + Gap, e.Bounds.Y + Gap / 2, e.Bounds.Width - Gap * 2 - 1, e.Bounds.Height - Gap);

            var border = card.Alert ? Palette.Error : card.Accent;
            using (var path = Rounded(rect, Radius))
            {
                using (var fill = new SolidBrush(selected ? Blend(card.Tint, card.Accent, 0.18) : card.Tint)) g.FillPath(fill, path);
                using (var pen = new Pen(border, selected || card.Alert ? 2.2f : 1.2f)) g.DrawPath(pen, path);
            }
            using (var stripe = Rounded(new Rectangle(rect.X, rect.Y, Stripe * 2, rect.Height), Radius))
            using (var brush = new SolidBrush(card.Accent))
            {
                var clip = g.Clip;
                g.SetClip(new Rectangle(rect.X, rect.Y, Stripe, rect.Height + 1));
                g.FillPath(brush, stripe);
                g.Clip = clip;
            }
            g.SmoothingMode = SmoothingMode.Default;

            var left = rect.X + Stripe + 10;
            var badgeWidth = string.IsNullOrEmpty(card.Badge) ? 0 : 92;
            var textWidth = Math.Max(40, rect.Right - left - 10 - badgeWidth);
            var y = rect.Y + 7;
            const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

            TextRenderer.DrawText(g, card.Title ?? "", _titleFont, new Rectangle(left, y, textWidth, 20), card.TitleColor ?? Palette.Ink, flags);
            y += 21;
            var lineHeight = 17;
            var maxY = rect.Bottom - 4;
            for (var i = 0; i < card.Lines.Count && y + lineHeight <= maxY; i++)
            {
                TextRenderer.DrawText(g, card.Lines[i] ?? "", _lineFont, new Rectangle(left, y, textWidth, lineHeight), i == 0 ? Palette.Ink : Palette.Muted, flags);
                y += lineHeight;
            }
            if (!string.IsNullOrEmpty(card.FlagLine) && y + lineHeight <= maxY)
                TextRenderer.DrawText(g, card.FlagLine, _lineFont, new Rectangle(left, y, textWidth, lineHeight), Palette.FlagFore, flags);

            if (badgeWidth > 0)
            {
                var badgeRect = new Rectangle(rect.Right - badgeWidth - 8, rect.Y + 6, badgeWidth, 26);
                TextRenderer.DrawText(g, card.Badge, _badgeFont, badgeRect, card.Alert ? Palette.Error : card.Accent,
                    TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                if (!string.IsNullOrEmpty(card.BadgeCaption))
                    TextRenderer.DrawText(g, card.BadgeCaption, _captionFont, new Rectangle(badgeRect.X, badgeRect.Bottom, badgeWidth, 14), Palette.Muted,
                        TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }

            if ((e.State & DrawItemState.Focus) == DrawItemState.Focus && Focused)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(rect, -3, -3));
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color Blend(Color a, Color b, double amount) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _titleFont.Dispose();
                _lineFont.Dispose();
                _badgeFont.Dispose();
                _captionFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
