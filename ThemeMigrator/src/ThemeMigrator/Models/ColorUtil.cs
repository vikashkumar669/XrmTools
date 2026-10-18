using System.Drawing;
using System.Text.RegularExpressions;

namespace ThemeMigrator.Models
{
    public static class ColorUtil
    {
        private static readonly Regex HexRegex = new Regex(
            "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);

        public static bool IsHex(string value) =>
            !string.IsNullOrWhiteSpace(value) && HexRegex.IsMatch(value.Trim());

        public static Color? Parse(string value)
        {
            if (!IsHex(value)) return null;
            var v = value.Trim().TrimStart('#');
            if (v.Length == 3)
                v = new string(new[] { v[0], v[0], v[1], v[1], v[2], v[2] });
            try
            {
                if (v.Length == 6)
                    return Color.FromArgb(255,
                        System.Convert.ToInt32(v.Substring(0, 2), 16),
                        System.Convert.ToInt32(v.Substring(2, 2), 16),
                        System.Convert.ToInt32(v.Substring(4, 2), 16));
                return Color.FromArgb(
                    System.Convert.ToInt32(v.Substring(0, 2), 16),
                    System.Convert.ToInt32(v.Substring(2, 2), 16),
                    System.Convert.ToInt32(v.Substring(4, 2), 16),
                    System.Convert.ToInt32(v.Substring(6, 2), 16));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Black or white, whichever reads better on the given background.</summary>
        public static Color ContrastText(Color background)
        {
            var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255;
            return luminance > 0.6 ? Color.Black : Color.White;
        }
    }
}
