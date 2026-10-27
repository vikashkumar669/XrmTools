using System;
using System.Drawing;
using System.Windows.Forms;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Controls
{
    /// <summary>Colours and fonts. Teal brand, neutral surfaces, one accent per severity.</summary>
    internal static class Theme
    {
        public static readonly Color Brand = Color.FromArgb(0x0F, 0x76, 0x6E);
        public static readonly Color BrandDark = Color.FromArgb(0x0B, 0x5A, 0x54);
        public static readonly Color BrandTint = Color.FromArgb(0xE6, 0xF4, 0xF2);
        public static readonly Color Surface = Color.FromArgb(0xF6, 0xF8, 0xF7);
        public static readonly Color Card = Color.White;
        public static readonly Color Line = Color.FromArgb(0xDF, 0xE5, 0xE3);
        public static readonly Color Ink = Color.FromArgb(0x1D, 0x23, 0x21);
        public static readonly Color Muted = Color.FromArgb(0x5B, 0x66, 0x62);

        public static readonly Color Error = Color.FromArgb(0xB4, 0x23, 0x18);
        public static readonly Color ErrorTint = Color.FromArgb(0xFD, 0xEC, 0xEA);
        public static readonly Color Warning = Color.FromArgb(0xC2, 0x6A, 0x00);
        public static readonly Color WarningTint = Color.FromArgb(0xFF, 0xF4, 0xE0);
        public static readonly Color Info = Color.FromArgb(0x1D, 0x5F, 0xA8);
        public static readonly Color InfoTint = Color.FromArgb(0xE8, 0xF1, 0xFB);
        public static readonly Color Ok = Color.FromArgb(0x1B, 0x6E, 0x3A);

        public static readonly Font Base = new Font("Segoe UI", 9F);
        public static readonly Font Bold = new Font("Segoe UI", 9F, FontStyle.Bold);
        public static readonly Font Small = new Font("Segoe UI", 8F);
        public static readonly Font Heading = new Font("Segoe UI Semibold", 11F);
        public static readonly Font Mono = new Font("Consolas", 9F);

        public static Color For(Severity s) => s == Severity.Error ? Error : s == Severity.Warning ? Warning : Info;
        public static Color TintFor(Severity s) => s == Severity.Error ? ErrorTint : s == Severity.Warning ? WarningTint : InfoTint;

        public static Color For(EvidenceLevel level)
        {
            switch (level)
            {
                case EvidenceLevel.ExactRequest: return Ok;
                case EvidenceLevel.SameCausalChain: return Brand;
                case EvidenceLevel.SuspectedParentChild: return Info;
                case EvidenceLevel.Heuristic: return Warning;
                default: return Muted;
            }
        }

        /// <summary>Flat toolbar renderer in the brand colours.</summary>
        public sealed class FlatRenderer : ToolStripProfessionalRenderer
        {
            public FlatRenderer() : base(new Colors()) { RoundedEdges = false; }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

            private sealed class Colors : ProfessionalColorTable
            {
                public override Color ToolStripGradientBegin => Card;
                public override Color ToolStripGradientMiddle => Card;
                public override Color ToolStripGradientEnd => Card;
                public override Color ButtonSelectedHighlight => BrandTint;
                public override Color ButtonSelectedGradientBegin => BrandTint;
                public override Color ButtonSelectedGradientMiddle => BrandTint;
                public override Color ButtonSelectedGradientEnd => BrandTint;
                public override Color ButtonSelectedBorder => Brand;
                public override Color ButtonPressedGradientBegin => Color.FromArgb(0xC9, 0xE8, 0xE3);
                public override Color ButtonPressedGradientMiddle => Color.FromArgb(0xC9, 0xE8, 0xE3);
                public override Color ButtonPressedGradientEnd => Color.FromArgb(0xC9, 0xE8, 0xE3);
                public override Color ButtonCheckedGradientBegin => BrandTint;
                public override Color ButtonCheckedGradientEnd => BrandTint;
                public override Color MenuItemSelected => BrandTint;
                public override Color MenuItemBorder => Brand;
                public override Color SeparatorDark => Line;
                public override Color SeparatorLight => Card;
            }
        }

        /// <summary>A flat button in the brand colour.</summary>
        public static Button PrimaryButton(string text, EventHandler click)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat, BackColor = Brand, ForeColor = Color.White, Font = Bold,
                Height = 30, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = BrandDark;
            if (click != null) b.Click += click;
            return b;
        }

        public static Button SecondaryButton(string text, EventHandler click)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat, BackColor = Card, ForeColor = Brand, Font = Base,
                Height = 28, Cursor = Cursors.Hand, UseVisualStyleBackColor = false, AutoSize = true,
            };
            b.FlatAppearance.BorderColor = Line;
            b.FlatAppearance.MouseOverBackColor = BrandTint;
            if (click != null) b.Click += click;
            return b;
        }

        public static Label Caption(string text) =>
            new Label { Text = text, AutoSize = true, ForeColor = Muted, Font = Small, Margin = new Padding(0, 8, 0, 2) };
    }
}
