using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PluginFailureCorrelationExplorer.Controls
{
    /// <summary>A flat metric tile: large value, caption and a coloured top stripe.</summary>
    internal sealed class KpiTile : Control
    {
        private static readonly Font ValueFont = new Font("Segoe UI Semibold", 18F);
        private string _value = "–";
        private string _caption = "";
        private string _note = "";
        private Color _accent = Theme.Brand;

        public KpiTile(string caption, Color accent)
        {
            _caption = caption;
            _accent = accent;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Size = new Size(180, 84);
            Margin = new Padding(0, 0, 10, 10);
        }

        public void Set(string value, string note = null, Color? accent = null)
        {
            _value = value ?? "–";
            _note = note ?? "";
            if (accent.HasValue) _accent = accent.Value;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Rounded(r, 8))
            using (var fill = new SolidBrush(Theme.Card))
            using (var border = new Pen(Theme.Line))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            using (var stripe = new SolidBrush(_accent))
                g.FillRectangle(stripe, new Rectangle(8, 0, Width - 16, 3));

            TextRenderer.DrawText(g, _value, ValueFont, new Rectangle(12, 10, Width - 20, 34), _accent, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, _caption, Theme.Base, new Rectangle(12, 46, Width - 20, 18), Theme.Ink, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (_note.Length > 0)
                TextRenderer.DrawText(g, _note, Theme.Small, new Rectangle(12, 63, Width - 20, 16), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
