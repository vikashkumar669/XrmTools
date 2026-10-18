using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DataQualityStudio.Models;

namespace DataQualityStudio.Services
{
    /// <summary>Text helpers: normalization, similarity and transforms.</summary>
    public static class TextUtil
    {
        /// <summary>Lower-case letters and digits only ("Account Name" and "account_name" both become "accountname").</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static readonly Dictionary<string, string> Abbreviations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "no", "number" }, { "nr", "number" }, { "num", "number" }, { "#", "number" }, { "qty", "quantity" },
            { "amt", "amount" }, { "desc", "description" }, { "addr", "address" }, { "tel", "phone" }, { "ph", "phone" },
            { "mob", "mobile" }, { "e-mail", "email" }, { "dob", "birthday" }, { "dept", "department" }, { "org", "organization" },
            { "acct", "account" }, { "acc", "account" }, { "cust", "customer" }, { "emp", "employee" }, { "emps", "employees" },
            { "st", "street" }, { "zip", "postal code" }, { "postcode", "postal code" }, { "ref", "reference" }, { "id", "id" }
        };

        /// <summary>Expands common header abbreviations word by word ("No. of Employees" -> "number of Employees").</summary>
        public static string ExpandAbbreviations(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var words = Regex.Split(s.Replace("#", " # "), @"[\s_.\-/]+").Where(w => w.Length > 0)
                .Select(w => Abbreviations.TryGetValue(w, out var full) ? full : w);
            return string.Join(" ", words);
        }

        /// <summary>Similarity that also rewards a value matching the start of a longer label ("Agricultur" ~ "Agriculture and ...").</summary>
        public static double PrefixSimilarity(string value, string label)
        {
            var v = Normalize(value);
            var l = Normalize(label);
            var score = Similarity(value, label);
            if (v.Length >= 4 && l.Length > v.Length)
                score = Math.Max(score, 0.92 * (1.0 - (double)Levenshtein(v, l.Substring(0, Math.Min(l.Length, v.Length + 1))) / (v.Length + 1)));
            return score;
        }

        public static int Levenshtein(string a, string b)
        {
            a = a ?? "";
            b = b ?? "";
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }

        /// <summary>0..1 similarity of two normalized strings.</summary>
        public static double Similarity(string a, string b)
        {
            a = Normalize(a);
            b = Normalize(b);
            if (a.Length == 0 && b.Length == 0) return 1;
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a == b) return 1;
            var score = 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);
            // A whole-word containment ("Email" in "Email Address") is a good hint too
            if (a.Length >= 4 && b.Length >= 4 && (a.Contains(b) || b.Contains(a)))
                score = Math.Max(score, 0.75 * Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length) + 0.25);
            return score;
        }

        /// <summary>Returns the candidate closest to <paramref name="value"/>, or null if none reaches <paramref name="threshold"/>.</summary>
        public static string Closest(string value, IEnumerable<string> candidates, double threshold, out double score, bool prefixAware = false)
        {
            string best = null;
            score = 0;
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                var s = prefixAware ? PrefixSimilarity(value, c) : Similarity(value, c);
                if (s > score)
                {
                    score = s;
                    best = c;
                }
            }
            return score >= threshold ? best : null;
        }

        public static string ApplyTransform(string transform, string value)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(transform) || transform == Transforms.None) return value;
            switch (transform)
            {
                case Transforms.Upper: return value.ToUpperInvariant();
                case Transforms.Lower: return value.ToLowerInvariant();
                case Transforms.Title: return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
                case Transforms.RemoveSpaces: return new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
                case Transforms.DigitsOnly: return new string(value.Where(char.IsDigit).ToArray());
                case Transforms.CollapseSpaces: return Regex.Replace(value.Trim(), @"\s+", " ");
                default: return value;
            }
        }

        public static string Truncate(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max);

        public static string Quote(string s) => "'" + Truncate(s ?? "", 80) + "'";
    }

    /// <summary>Culture-tolerant parsing of numbers, dates and yes/no values.</summary>
    public static class ValueParser
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------------- Numbers ----------------

        /// <param name="symbolsRemoved">True if currency symbols, percent signs, spaces or accounting brackets had to be removed.</param>
        public static bool TryParseNumber(string text, NumberFormatOption format, out decimal value, out bool symbolsRemoved)
        {
            value = 0;
            symbolsRemoved = false;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var s = text.Trim();

            var negative = false;
            if (s.StartsWith("(") && s.EndsWith(")") && s.Length > 2)
            {
                negative = true;
                s = s.Substring(1, s.Length - 2);
                symbolsRemoved = true;
            }

            // ISO currency code before or after the amount: "USD 1,200" / "1.200 EUR"
            var code = CurrencyCode.Match(s);
            if (code.Success)
            {
                s = code.Groups["p"].Success && code.Groups["p"].Length > 0 ? code.Groups["a1"].Value : code.Groups["a2"].Value;
                symbolsRemoved = true;
            }

            var sb = new StringBuilder();
            foreach (var c in s)
            {
                if (char.IsDigit(c) || c == '.' || c == ',' || c == '-' || c == '+' || c == 'e' || c == 'E') sb.Append(c);
                else if (c == '\'' || c == ' ' || c == ' ' || c == ' ') { /* thousands separators */ }
                else if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.CurrencySymbol || c == '%') symbolsRemoved = true;
                else return false;
            }
            s = sb.ToString();
            if (s.Length == 0 || !s.Any(char.IsDigit)) return false;

            string normalized;
            switch (format)
            {
                case NumberFormatOption.DotDecimal:
                    normalized = s.Replace(",", "");
                    break;
                case NumberFormatOption.CommaDecimal:
                    normalized = s.Replace(".", "").Replace(',', '.');
                    break;
                default:
                    normalized = GuessNormalize(s);
                    break;
            }
            if (normalized == null) return false;

            if (!decimal.TryParse(normalized, NumberStyles.Float, Inv, out value))
            {
                // Very large or tiny exponents
                if (!double.TryParse(normalized, NumberStyles.Float, Inv, out var d) || double.IsInfinity(d) || Math.Abs(d) > 7.9e28) return false;
                value = (decimal)d;
            }
            if (negative) value = -value;
            return true;
        }

        private static readonly Regex CurrencyCode = new Regex(
            @"^(?:(?<p>[A-Za-z]{3})\s?(?<a1>[\d\s.,'\-+]*\d[\d\s.,'\-+]*)|(?<a2>[\d\s.,'\-+]*\d[\d\s.,'\-+]*?)\s?(?<s>[A-Za-z]{3}))$",
            RegexOptions.Compiled);

        /// <summary>Works out which of '.' and ',' is the decimal separator.</summary>
        private static string GuessNormalize(string s)
        {
            var lastDot = s.LastIndexOf('.');
            var lastComma = s.LastIndexOf(',');
            if (lastDot >= 0 && lastComma >= 0)
            {
                // The separator that comes last is the decimal separator
                return lastDot > lastComma ? s.Replace(",", "") : s.Replace(".", "").Replace(',', '.');
            }
            if (lastComma >= 0)
            {
                var commas = s.Count(c => c == ',');
                var after = s.Length - lastComma - 1;
                if (commas > 1) return s.Replace(",", "");                 // 1,234,567
                if (after == 3 && lastComma > 0 && s.Substring(0, lastComma).Trim('-', '+').Length <= 3)
                    return s.Replace(",", "");                              // 1,234 -> thousands
                return s.Replace(',', '.');                                 // 12,5 -> decimal comma
            }
            if (lastDot >= 0 && s.Count(c => c == '.') > 1)
                return s.Replace(".", "");                                  // 1.234.567
            return s;
        }

        public static int DecimalPlaces(decimal value)
        {
            var s = FormatNumber(value);
            var dot = s.IndexOf('.');
            return dot < 0 ? 0 : s.Length - dot - 1;
        }

        public static string FormatNumber(decimal value) => value.ToString("0.############################", Inv);

        // ---------------- Booleans ----------------

        private static readonly HashSet<string> TrueWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "true", "yes", "y", "1", "t", "on", "x", "✓", "✔" };
        private static readonly HashSet<string> FalseWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "false", "no", "n", "0", "f", "off" };

        public static bool TryParseBool(string s, string trueLabel, string falseLabel, out bool value)
        {
            value = false;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (TrueWords.Contains(s) || (!string.IsNullOrEmpty(trueLabel) && string.Equals(s, trueLabel, StringComparison.OrdinalIgnoreCase))) { value = true; return true; }
            if (FalseWords.Contains(s) || (!string.IsNullOrEmpty(falseLabel) && string.Equals(s, falseLabel, StringComparison.OrdinalIgnoreCase))) { value = false; return true; }
            return false;
        }

        // ---------------- Dates ----------------

        private static readonly string[] TimeParts = { "", " H:mm", " H:mm:ss", " h:mm tt", " h:mm:ss tt", " HH:mm:ss.fff", "THH:mm", "THH:mm:ss", "THH:mm:ss.fff" };

        private static readonly string[] IsoDates = { "yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyyMMdd" };
        private static readonly string[] DayFirstDates = { "d/M/yyyy", "d-M-yyyy", "d.M.yyyy", "d/M/yy", "d-M-yy", "d.M.yy" };
        private static readonly string[] MonthFirstDates = { "M/d/yyyy", "M-d-yyyy", "M.d.yyyy", "M/d/yy", "M-d-yy" };
        private static readonly string[] NamedMonthDates =
        {
            "d MMM yyyy", "d-MMM-yyyy", "d MMMM yyyy", "d-MMMM-yyyy", "d MMM yy", "d-MMM-yy", "d/MMM/yyyy",
            "MMM d yyyy", "MMM d, yyyy", "MMMM d yyyy", "MMMM d, yyyy", "dddd, MMMM d, yyyy", "ddd, d MMM yyyy", "yyyy-MMM-dd"
        };

        private static readonly string[] IsoFormats = Combine(IsoDates);
        private static readonly string[] DayFirstFormats = Combine(DayFirstDates);
        private static readonly string[] MonthFirstFormats = Combine(MonthFirstDates);
        private static readonly string[] NamedFormats = Combine(NamedMonthDates);

        private static string[] Combine(string[] dates) =>
            dates.SelectMany(d => TimeParts.Where(t => !(d == "yyyyMMdd" && t.Length > 0)).Select(t => d + t)).ToArray();

        public static bool MonthFirstCulture =>
            CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern.TrimStart().StartsWith("M", StringComparison.OrdinalIgnoreCase);

        /// <param name="ambiguous">True when the text could be read as day-first or month-first and the two readings differ.</param>
        /// <param name="note">How the value was interpreted, when it wasn't obvious (Excel serial number, ambiguous order...).</param>
        public static bool TryParseDate(string text, DateOrder order, out DateTime value, out bool ambiguous, out string note)
        {
            value = default(DateTime);
            ambiguous = false;
            note = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var s = text.Trim();
            const DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces;

            // ISO 8601 with offset or Z
            if (s.Length >= 10 && char.IsDigit(s[0]) && s[4] == '-' &&
                DateTimeOffset.TryParse(s, Inv, DateTimeStyles.AssumeLocal, out var dto) && (s.EndsWith("Z") || Regex.IsMatch(s, @"[+-]\d\d:?\d\d$")))
            {
                value = dto.LocalDateTime;
                return true;
            }

            if (DateTime.TryParseExact(s, IsoFormats, Inv, styles, out value)) return true;

            // Excel serial date stored as text/number (e.g. 45231 or 45231.5)
            if (Regex.IsMatch(s, @"^\d{4,5}(\.\d+)?$") && double.TryParse(s, NumberStyles.Float, Inv, out var serial) && serial > 0 && serial < 2958466)
            {
                try
                {
                    value = DateTime.FromOADate(serial);
                    note = "read as an Excel date serial number";
                    return true;
                }
                catch (ArgumentException) { return false; }
            }

            if (DateTime.TryParseExact(s, NamedFormats, Inv, styles, out value)) return true;
            if (DateTime.TryParseExact(s, NamedFormats, CultureInfo.CurrentCulture, styles, out value)) return true;

            var dayOk = DateTime.TryParseExact(s, DayFirstFormats, Inv, styles, out var dayFirst);
            var monthOk = DateTime.TryParseExact(s, MonthFirstFormats, Inv, styles, out var monthFirst);

            switch (order)
            {
                case DateOrder.DayFirst:
                    if (dayOk) { value = dayFirst; return true; }
                    break;
                case DateOrder.MonthFirst:
                    if (monthOk) { value = monthFirst; return true; }
                    break;
                case DateOrder.YearFirst:
                    break;
                default:
                    if (dayOk && monthOk)
                    {
                        if (dayFirst == monthFirst) { value = dayFirst; return true; }
                        ambiguous = true;
                        value = MonthFirstCulture ? monthFirst : dayFirst;
                        note = MonthFirstCulture ? "read as month/day/year" : "read as day/month/year";
                        return true;
                    }
                    if (dayOk) { value = dayFirst; return true; }
                    if (monthOk) { value = monthFirst; return true; }
                    break;
            }

            if (order == DateOrder.Auto && DateTime.TryParse(s, CultureInfo.CurrentCulture, styles, out value)) return true;
            return false;
        }

        public static string FormatDate(DateTime value, bool dateOnly) =>
            dateOnly || value.TimeOfDay == TimeSpan.Zero
                ? value.ToString("yyyy-MM-dd", Inv)
                : value.ToString(value.Second == 0 ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd HH:mm:ss", Inv);

        // ---------------- Formats ----------------

        private static readonly Regex EmailRegex = new Regex(@"^[^@\s""<>()\[\],;:]+@[^@\s""<>()\[\],;:]+\.[A-Za-z]{2,}$", RegexOptions.Compiled);

        public static bool IsEmail(string s) => !string.IsNullOrEmpty(s) && EmailRegex.IsMatch(s);

        public static bool IsUrl(string s) =>
            Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp) &&
            uri.Host.Contains(".");
    }
}
