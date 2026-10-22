using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DiagnosticsWorkbench.Models;

namespace DiagnosticsWorkbench.Analysis
{
    /// <summary>
    /// Reduces exception text to a stable shape, so the same failure on different records, users and times
    /// produces the same fingerprint. Each rule is a named regex so it can be unit-tested and reported.
    /// </summary>
    public sealed class ExceptionNormalizer
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        /// <summary>Built-in rules, applied in this order after the custom rules.</summary>
        public static readonly IReadOnlyList<(string Name, Regex Regex, string Token)> BuiltInRules = new List<(string, Regex, string)>
        {
            ("guid", new Regex(@"\{?\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b\}?", Options), "<guid>"),
            ("iso-datetime", new Regex(@"\b\d{4}-\d{2}-\d{2}(?:[T ]\d{1,2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:\s?Z|\s?[+-]\d{2}:?\d{2})?)?\b", Options), "<datetime>"),
            ("locale-datetime", new Regex(@"\b\d{1,2}[/.]\d{1,2}[/.]\d{2,4}(?:,?\s+\d{1,2}:\d{2}(?::\d{2})?(?:\s?[AP]M)?)?", Options), "<datetime>"),
            ("time", new Regex(@"\b\d{1,2}:\d{2}:\d{2}(?:\.\d+)?(?:\s?[AP]M)?\b", Options), "<datetime>"),
            ("double-quoted", new Regex("\"[^\"\\r\\n]{0,400}\"", Options), "<str>"),
            ("single-quoted", new Regex(@"(?<![\w])'[^'\r\n]{0,400}'(?![\w])", Options), "<str>"),
            ("url", new Regex(@"\bhttps?://\S+", Options), "<url>"),
            ("email", new Regex(@"\b[\w.+-]+@[\w-]+(?:\.[\w-]+)+\b", Options), "<email>"),
            // Hex error codes such as 0x80040217 are kept: \b does not split "0x" from the digits, so they never match.
            ("number", new Regex(@"(?<![\w.])-?\d{2,}(?:[.,]\d+)?(?![\w])", Options), "<n>"),
        };

        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.CultureInvariant);
        private static readonly Regex ExceptionTypeLine = new Regex(@"^\s*Exception type:\s*(?<v>.+)$", Options | RegexOptions.Multiline);
        private static readonly Regex MessageLine = new Regex(@"^\s*Message:\s*(?<v>.*)$", Options | RegexOptions.Multiline);
        private static readonly Regex ErrorCodeTag = new Regex(@"<ErrorCode>\s*(?<v>-?\d+|0x[0-9a-f]+)\s*</ErrorCode>", Options);
        private static readonly Regex StackFrame = new Regex(@"^\s+at\s", RegexOptions.CultureInvariant);
        private static readonly Regex TypeAndMessage = new Regex(@"^(?<t>[\w.`\[\]]+(?:Exception|Fault)[\w`\[\].]*)\s*:\s*(?<m>.*)$", RegexOptions.CultureInvariant);

        private readonly List<(string Name, Regex Regex, string Token)> _customRules = new List<(string, Regex, string)>();

        /// <summary>Custom rules whose regex didn't compile (name → error).</summary>
        public Dictionary<string, string> InvalidRules { get; } = new Dictionary<string, string>();

        public ExceptionNormalizer(IEnumerable<NormalizationRule> customRules = null)
        {
            foreach (var rule in customRules ?? Enumerable.Empty<NormalizationRule>())
            {
                if (rule == null || !rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern)) continue;
                var name = string.IsNullOrWhiteSpace(rule.Name) ? rule.Pattern : rule.Name;
                try
                {
                    var regex = new Regex(rule.Pattern, Options, TimeSpan.FromMilliseconds(250));
                    _customRules.Add((name, regex, string.IsNullOrEmpty(rule.Token) ? "<custom>" : rule.Token));
                }
                catch (ArgumentException ex)
                {
                    InvalidRules[name] = ex.Message;
                }
            }
        }

        /// <summary>
        /// Exception type and first message line, e.g. "System.InvalidOperationException: Account name is required."
        /// Stack frames, fault XML and trace output are dropped. An Organization Service error code is appended.
        /// </summary>
        public static string Headline(string exceptionDetails)
        {
            if (string.IsNullOrWhiteSpace(exceptionDetails)) return "";
            var text = exceptionDetails.Replace("\r\n", "\n");

            string type = null, message = null;
            var typeMatch = ExceptionTypeLine.Match(text);
            if (typeMatch.Success) type = typeMatch.Groups["v"].Value.Trim();
            var messageMatch = MessageLine.Match(text);
            if (messageMatch.Success)
            {
                message = messageMatch.Groups["v"].Value.Trim();
                if (message.Length == 0)
                {
                    // "Message:" followed by the text on the next line(s)
                    var rest = text.Substring(messageMatch.Index + messageMatch.Length).TrimStart('\n');
                    message = rest.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                }
            }

            if (type == null && message == null)
            {
                // Plain .NET text: "Namespace.SomeException: message" then "   at ..." frames
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.TrimEnd();
                    if (line.Trim().Length == 0 || StackFrame.IsMatch(line)) continue;
                    line = line.Trim();
                    if (line.StartsWith("Unhandled exception", StringComparison.OrdinalIgnoreCase) && line.TrimEnd(':').Length <= "Unhandled exception".Length) continue;
                    var tm = TypeAndMessage.Match(line);
                    if (tm.Success) { type = tm.Groups["t"].Value; message = tm.Groups["m"].Value.Trim(); }
                    else message = line;
                    break;
                }
            }

            var headline = string.IsNullOrEmpty(type) ? (message ?? "") : string.IsNullOrEmpty(message) ? type : type + ": " + message;

            var code = ErrorCodeTag.Match(text);
            if (code.Success) headline += " [ErrorCode " + code.Groups["v"].Value + "]";
            return headline.Length > 1000 ? headline.Substring(0, 1000) : headline;
        }

        /// <summary>Applies custom rules, then built-in rules, collapses whitespace and lowercases.</summary>
        public string NormalizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            // Keep error codes stable: protect the bracketed code from the number rule.
            string code = null;
            var codeStart = text.LastIndexOf(" [ErrorCode ", StringComparison.Ordinal);
            if (codeStart >= 0 && text.EndsWith("]", StringComparison.Ordinal))
            {
                code = text.Substring(codeStart);
                text = text.Substring(0, codeStart);
            }

            foreach (var rule in _customRules)
            {
                try { text = rule.Regex.Replace(text, rule.Token); }
                catch (RegexMatchTimeoutException) { /* a slow custom rule is skipped, never fatal */ }
            }
            foreach (var rule in BuiltInRules) text = rule.Regex.Replace(text, rule.Token);

            text = Whitespace.Replace(text, " ").Trim();
            if (code != null) text += code;
            return text.ToLowerInvariant();
        }

        /// <summary>Headline → normalized text.</summary>
        public string Normalize(string exceptionDetails) => NormalizeText(Headline(exceptionDetails));

        /// <summary>12-character SHA-1 fingerprint of plugin type + message + normalized exception.</summary>
        public string Fingerprint(TraceLog log) => Fingerprint(log?.FullTypeName, log?.MessageName, Normalize(log?.ExceptionDetails));

        public static string Fingerprint(string typeName, string messageName, string normalizedText)
        {
            // The assembly version is left out so an error keeps its fingerprint across deployments.
            var input = (TraceLog.StripAssembly(typeName) ?? "").Trim().ToLowerInvariant() + "|" + (messageName ?? "").Trim().ToLowerInvariant() + "|" + (normalizedText ?? "");
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder(12);
                for (var i = 0; i < 6; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
