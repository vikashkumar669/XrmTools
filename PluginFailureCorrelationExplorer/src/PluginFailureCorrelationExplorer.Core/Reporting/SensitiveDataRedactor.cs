using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace PluginFailureCorrelationExplorer.Reporting
{
    /// <summary>What to redact from exported reports.</summary>
    public sealed class RedactionOptions
    {
        /// <summary>Replace GUIDs with stable placeholders ([GUID-1], [GUID-2]…), so the same ID stays recognizable within one report.</summary>
        public bool Guids { get; set; } = true;

        public bool Emails { get; set; } = true;

        /// <summary>Replace URL query strings and fragments (path and host are kept).</summary>
        public bool UrlQueries { get; set; } = true;

        /// <summary>Extra literal values to remove (case-insensitive).</summary>
        public List<string> CustomValues { get; set; } = new List<string>();

        public static RedactionOptions None => new RedactionOptions { Guids = false, Emails = false, UrlQueries = false };
    }

    /// <summary>
    /// Removes sensitive values from text before it leaves the tool.
    /// <para>
    /// Credentials are <b>always</b> scrubbed, whatever the options: bearer tokens, JWTs, connection-string secrets
    /// (Password=, ClientSecret=…), "secret/token/key: value" pairs and sensitive URL parameters (sig, token, code,
    /// access_token…). GUIDs, e-mail addresses, URL query strings and custom values are removed when enabled.
    /// </para>
    /// One instance keeps its GUID mapping, so use one instance per report.
    /// </summary>
    public sealed class SensitiveDataRedactor
    {
        public const string Redacted = "[REDACTED]";

        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        private static readonly Regex Bearer = new Regex(@"\bBearer\s+[A-Za-z0-9\-._~+/]+=*", Options);
        private static readonly Regex Jwt = new Regex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\b", Options);
        private static readonly Regex SecretPair = new Regex(
            @"\b(?<k>password|pwd|passwd|client[_ ]?secret|clientsecret|secret|api[_-]?key|apikey|access[_-]?token|refresh[_-]?token|auth[_-]?token|token|sharedaccesskey|accountkey|sas)\b(?<sep>\s*[=:]\s*)(?<v>""[^""]*""|'[^']*'|[^\s;,&""'<>]+)",
            Options);
        private static readonly Regex SensitiveQueryParam = new Regex(
            @"(?<=[?&#])(?<k>sig|signature|token|access_token|id_token|refresh_token|code|key|apikey|api_key|client_secret|secret|password|pwd|sv|se|sp|skoid|sktid)=(?<v>[^&#\s""'<>]*)",
            Options);
        private static readonly Regex Guid = new Regex(@"\{?\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b\}?", Options);
        private static readonly Regex Email = new Regex(@"\b[\w.+-]+@[\w-]+(?:\.[\w-]+)+\b", Options);
        private static readonly Regex UrlQuery = new Regex(@"(?<base>\bhttps?://[^\s?#""'<>]+)(?<q>[?#][^\s""'<>]*)", Options);

        private readonly RedactionOptions _options;
        private readonly Dictionary<string, string> _guidMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Regex> _custom;

        public SensitiveDataRedactor(RedactionOptions options)
        {
            _options = options ?? RedactionOptions.None;
            _custom = (_options.CustomValues ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => v.Length)                      // longest first so overlapping values are fully removed
                .Select(v => new Regex(Regex.Escape(v), Options))
                .ToList();
        }

        /// <summary>True when any optional redaction is active (credentials are always scrubbed).</summary>
        public bool IsRedacting => _options.Guids || _options.Emails || _options.UrlQueries || _custom.Count > 0;

        /// <summary>Redacts one text. Null stays null.</summary>
        public string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Always: credentials
            text = Bearer.Replace(text, "Bearer " + Redacted);
            text = Jwt.Replace(text, Redacted);
            text = SecretPair.Replace(text, m => m.Groups["k"].Value + m.Groups["sep"].Value + Redacted);
            text = SensitiveQueryParam.Replace(text, m => m.Groups["k"].Value + "=" + Redacted);

            foreach (var custom in _custom) text = custom.Replace(text, Redacted);
            if (_options.UrlQueries) text = UrlQuery.Replace(text, m => m.Groups["base"].Value + "?" + Redacted);
            if (_options.Emails) text = Email.Replace(text, "[EMAIL]");
            if (_options.Guids) text = Guid.Replace(text, m => GuidToken(m.Value));
            return text;
        }

        /// <summary>Redacts a GUID value (for ID columns).</summary>
        public string Redact(Guid? id)
        {
            if (!id.HasValue) return "";
            var text = id.Value.ToString("D");
            return _options.Guids ? GuidToken(text) : text;
        }

        private string GuidToken(string value)
        {
            var key = value.Trim('{', '}').ToLowerInvariant();
            if (!_guidMap.TryGetValue(key, out var token))
            {
                token = "[GUID-" + (_guidMap.Count + 1).ToString(CultureInfo.InvariantCulture) + "]";
                _guidMap[key] = token;
            }
            return token;
        }
    }
}
