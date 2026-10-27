using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Analysis
{
    /// <summary>A named, ordered normalization rule.</summary>
    public sealed class NormalizerRule
    {
        public NormalizerRule(string name, string description, Regex regex, string token)
        {
            Name = name;
            Description = description;
            Regex = regex;
            Token = token;
        }

        public string Name { get; }
        public string Description { get; }
        public Regex Regex { get; }
        public string Token { get; }
    }

    /// <summary>Normalized text plus the rules that changed it.</summary>
    public sealed class NormalizedText
    {
        public string Text { get; set; }
        public List<string> RulesApplied { get; } = new List<string>();
    }

    /// <summary>
    /// Replaces volatile values (GUIDs, timestamps, quoted values, numbers…) with placeholders so the same failure on
    /// different records, users and times produces the same text. The input is never modified; callers keep the
    /// original for investigation. Custom rules run first, then the built-in rules in the order of <see cref="BuiltInRules"/>.
    /// </summary>
    public sealed class TraceTextNormalizer
    {
        /// <summary>Bump when built-in rules change: fingerprints from different versions are not comparable.</summary>
        public const string Version = "1";

        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private static readonly TimeSpan CustomRuleTimeout = TimeSpan.FromMilliseconds(250);
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.CultureInvariant);

        /// <summary>Built-in rules, applied in this order after the custom rules.</summary>
        public static readonly IReadOnlyList<NormalizerRule> BuiltInRules = new List<NormalizerRule>
        {
            new NormalizerRule("guid", "GUIDs, with or without braces",
                new Regex(@"\{?\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b\}?", Options), "<guid>"),
            new NormalizerRule("iso-datetime", "ISO 8601 dates and date-times, with optional fraction and offset",
                new Regex(@"\b\d{4}-\d{2}-\d{2}(?:[T ]\d{1,2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:\s?Z|\s?[+-]\d{2}:?\d{2})?)?\b", Options), "<datetime>"),
            new NormalizerRule("locale-datetime", "Locale dates such as 10/27/2026 or 27.10.2026, with optional time",
                new Regex(@"\b\d{1,2}[/.]\d{1,2}[/.]\d{2,4}(?:,?\s+\d{1,2}:\d{2}(?::\d{2})?(?:\s?[AP]M)?)?", Options), "<datetime>"),
            new NormalizerRule("time", "Times of day such as 14:03:22 or 2:03:22 PM",
                new Regex(@"\b\d{1,2}:\d{2}:\d{2}(?:\.\d+)?(?:\s?[AP]M)?\b", Options), "<datetime>"),
            new NormalizerRule("reference-number", "Dataverse support reference numbers such as #8A1B2C3D",
                new Regex(@"#[0-9a-f]{8}\b", Options), "#<ref>"),
            new NormalizerRule("url", "URLs",
                new Regex(@"\bhttps?://[^\s""'<>]+", Options), "<url>"),
            new NormalizerRule("email", "E-mail addresses",
                new Regex(@"\b[\w.+-]+@[\w-]+(?:\.[\w-]+)+\b", Options), "<email>"),
            new NormalizerRule("double-quoted", "Text in double quotes (record names, values)",
                new Regex("\"[^\"\\r\\n]{0,400}\"", Options), "<str>"),
            new NormalizerRule("single-quoted", "Text in single quotes (record names, values)",
                new Regex(@"(?<![\w])'[^'\r\n]{0,400}'(?![\w])", Options), "<str>"),
            new NormalizerRule("il-offset", "IL offsets such as +0x1a in stack frames",
                new Regex(@"\+\s?0x[0-9a-f]+", Options), "+<offset>"),
            new NormalizerRule("line-number", "Source line numbers (:line 42)",
                new Regex(@":line \d+", Options), ":line <n>"),
            // Hex error codes such as 0x80040217 are kept: \b doesn't separate "0x" from the digits, so they never match.
            // Numbers in parentheses such as "(404)" are kept: they are usually status codes that distinguish failures.
            new NormalizerRule("number", "Numbers with 2 or more digits, except hex codes and parenthesized codes",
                new Regex(@"(?<![\w.(])-?\d{2,}(?:[.,]\d+)?(?![\w)])", Options), "<n>"),
        };

        private readonly List<NormalizerRule> _customRules = new List<NormalizerRule>();

        /// <summary>Custom rules that could not be compiled (name → error). They are skipped.</summary>
        public Dictionary<string, string> InvalidRules { get; } = new Dictionary<string, string>();

        public TraceTextNormalizer(IEnumerable<NormalizationRule> customRules = null)
        {
            foreach (var rule in customRules ?? Enumerable.Empty<NormalizationRule>())
            {
                if (rule == null || !rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern)) continue;
                var name = string.IsNullOrWhiteSpace(rule.Name) ? rule.Pattern : rule.Name.Trim();
                try
                {
                    var regex = new Regex(rule.Pattern, Options, CustomRuleTimeout);
                    _customRules.Add(new NormalizerRule("custom:" + name, "Custom rule", regex, string.IsNullOrEmpty(rule.Token) ? "<custom>" : rule.Token));
                }
                catch (ArgumentException ex)
                {
                    InvalidRules[name] = ex.Message;
                }
            }
        }

        /// <summary>Custom rules that compiled and will be applied.</summary>
        public IReadOnlyList<NormalizerRule> CustomRules => _customRules;

        /// <summary>Normalizes text: custom rules, built-in rules, whitespace collapse, lower case.</summary>
        public NormalizedText Normalize(string text)
        {
            var result = new NormalizedText();
            if (string.IsNullOrEmpty(text))
            {
                result.Text = "";
                return result;
            }

            foreach (var rule in _customRules.Concat(BuiltInRules))
            {
                string replaced;
                try { replaced = rule.Regex.Replace(text, rule.Token); }
                catch (RegexMatchTimeoutException)
                {
                    // A slow custom rule is skipped for this text; it is never fatal.
                    continue;
                }
                if (!string.Equals(replaced, text, StringComparison.Ordinal))
                {
                    result.RulesApplied.Add(rule.Name);
                    text = replaced;
                }
            }

            result.Text = Whitespace.Replace(text, " ").Trim().ToLowerInvariant();
            return result;
        }

        /// <summary>Shortcut returning only the normalized text.</summary>
        public string NormalizeText(string text) => Normalize(text).Text;
    }
}
