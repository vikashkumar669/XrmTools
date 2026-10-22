using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DiagnosticsWorkbench.Export
{
    public static class Formatting
    {
        private static readonly Regex Guid = new Regex(@"\{?\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b\}?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>"850 ms", "2.4 s", "1 min 12 s".</summary>
        public static string Duration(long? ms)
        {
            if (ms == null) return "–";
            var v = ms.Value;
            if (v < 1000) return v.ToString(CultureInfo.InvariantCulture) + " ms";
            if (v < 60000) return (v / 1000.0).ToString(v < 10000 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";
            var minutes = v / 60000;
            var seconds = (v % 60000) / 1000;
            return minutes + " min " + seconds + " s";
        }

        /// <summary>Local time for display: "2026-10-21 14:05:09".</summary>
        public static string Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        /// <summary>ISO-8601 UTC for files: "2026-10-21T08:35:09.123Z".</summary>
        public static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        public static string Iso(DateTime? utc) => utc.HasValue ? Iso(utc.Value) : "";

        /// <summary>"3 min ago", "2 h ago", "4 d ago".</summary>
        public static string Ago(DateTime utc, DateTime nowUtc)
        {
            var span = nowUtc - utc;
            if (span.TotalSeconds < 60) return "just now";
            if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " min ago";
            if (span.TotalHours < 48) return (int)span.TotalHours + " h ago";
            return (int)span.TotalDays + " d ago";
        }

        private static readonly Regex FaultWrapper = new Regex(@"System\.ServiceModel\.FaultException`1\[(?:[\w]+\.)*(\w+)\]", RegexOptions.CultureInvariant);
        private static readonly Regex QualifiedException = new Regex(@"\b(?:[A-Za-z_]\w*\.)+(\w*(?:Exception|Fault))\b", RegexOptions.CultureInvariant);

        /// <summary>
        /// Readable headline for cards and reports: namespaces are dropped from exception types and the WCF fault wrapper
        /// becomes its fault name, e.g. "OrganizationServiceFault: Account name is required." The raw headline is kept elsewhere.
        /// </summary>
        public static string DisplayHeadline(string headline)
        {
            if (string.IsNullOrEmpty(headline)) return headline ?? "";
            var text = FaultWrapper.Replace(headline, "$1");
            return QualifiedException.Replace(text, "$1");
        }

        /// <summary>The message part of a display headline (after "Type: "), or the whole headline.</summary>
        public static string HeadlineMessage(string headline)
        {
            var display = DisplayHeadline(headline);
            var colon = display.IndexOf(": ", StringComparison.Ordinal);
            return colon > 0 && colon < 60 ? display.Substring(colon + 2) : display;
        }

        /// <summary>The exception type part of a display headline, or empty.</summary>
        public static string HeadlineType(string headline)
        {
            var display = DisplayHeadline(headline);
            var colon = display.IndexOf(": ", StringComparison.Ordinal);
            return colon > 0 && colon < 60 ? display.Substring(0, colon) : "";
        }

        public static string RedactGuids(string text) => string.IsNullOrEmpty(text) ? text : Guid.Replace(text, "<guid>");

        public static string FirstLine(string text, int max = 200)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var line = text.Replace("\r", "").Split('\n')[0].Trim();
            return line.Length > max ? line.Substring(0, max - 1) + "…" : line;
        }

        public static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            return text.Substring(0, max - 1) + "…";
        }
    }
}
