using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Analysis
{
    /// <summary>
    /// Builds stable fingerprints for failures and groups recurring failures.
    /// <para>
    /// The fingerprint hashes: exception type (inner type when the outer one is a fault wrapper), the Organization Service
    /// error code, the normalized first message line, the plug-in type (without assembly version) and the top user stack
    /// frames. Including the plug-in type and frames keeps generic messages such as "Object reference not set to an
    /// instance of an object." from different plug-ins in separate groups.
    /// </para>
    /// </summary>
    public sealed class ExceptionFingerprintService
    {
        private readonly AnalysisOptions _options;
        private readonly TraceTextNormalizer _normalizer;

        public ExceptionFingerprintService(AnalysisOptions options)
        {
            _options = options ?? new AnalysisOptions();
            _normalizer = new TraceTextNormalizer(_options.CustomRules);
        }

        public TraceTextNormalizer Normalizer => _normalizer;

        /// <summary>
        /// Signature of everything that affects fingerprints. Two analyses can only be compared when their signatures match.
        /// </summary>
        public string GroupingSignature
        {
            get
            {
                var custom = string.Join("\u001f", _normalizer.CustomRules.Select(r => r.Name + "=" + r.Regex + "=>" + r.Token));
                return Hash("v" + TraceTextNormalizer.Version + "|" + _options.Grouping + "|" + _options.FingerprintStackFrames.ToString(CultureInfo.InvariantCulture) + "|" + custom, 16);
            }
        }

        /// <summary>Fingerprint for one trace. Returns null when the trace has no exception.</summary>
        public FailureFingerprint Fingerprint(PluginTraceRecord record)
        {
            if (record == null || !record.HasException) return null;
            return Fingerprint(record.ExceptionDetails, record.FullTypeName);
        }

        /// <summary>Fingerprint for exception text raised by a plug-in type.</summary>
        public FailureFingerprint Fingerprint(string exceptionDetails, string pluginType)
        {
            var parsed = ExceptionParser.Parse(exceptionDetails);
            var message = parsed.Message;
            if (string.IsNullOrWhiteSpace(message))
                message = (exceptionDetails ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

            var normalized = _normalizer.Normalize(message);
            var frames = parsed.UserFrames.Take(_options.FingerprintStackFrames).Select(f => _normalizer.NormalizeText(f)).ToList();
            var type = (parsed.DisplayType ?? "").Trim();

            var input = new StringBuilder()
                .Append("v").Append(TraceTextNormalizer.Version)
                .Append("|type=").Append(type.ToLowerInvariant())
                .Append("|code=").Append(parsed.ErrorCode ?? "")
                .Append("|msg=").Append(normalized.Text)
                .Append("|plugin=").Append((TypeNames.StripAssembly(pluginType) ?? "").ToLowerInvariant())
                .Append("|frames=").Append(string.Join(";", frames))
                .ToString();

            var fingerprint = new FailureFingerprint
            {
                Value = Hash(input, 16),
                FingerprintInput = input,
                ExceptionType = string.IsNullOrEmpty(type) ? "(unknown exception type)" : type,
                ErrorCode = parsed.ErrorCode,
                Message = message,
                NormalizedMessage = normalized.Text,
            };
            fingerprint.TopFrames.AddRange(parsed.UserFrames.Take(_options.FingerprintStackFrames));   // as written, for display
            fingerprint.RulesApplied.AddRange(normalized.RulesApplied);
            return fingerprint;
        }

        /// <summary>The grouping key for a fingerprint under the configured <see cref="GroupingMode"/>.</summary>
        public string GroupKey(FailureFingerprint fingerprint, PluginTraceRecord record)
        {
            if (fingerprint == null) return null;
            switch (_options.Grouping)
            {
                case GroupingMode.ExceptionAndMessage:
                    return Hash("em|" + fingerprint.ExceptionType.ToLowerInvariant() + "|" + (fingerprint.ErrorCode ?? "") + "|" + fingerprint.NormalizedMessage, 16);
                case GroupingMode.FingerprintAndContext:
                    return Hash("ctx|" + fingerprint.Value + "|" + (record?.MessageName ?? "").ToLowerInvariant() + "|" + (record?.PrimaryEntity ?? "").ToLowerInvariant(), 16);
                default:
                    return fingerprint.Value;
            }
        }

        /// <summary>
        /// Groups failed traces. Traces without an exception (or whose exception wasn't loaded) are ignored.
        /// Results are ordered by occurrences, then latest occurrence.
        /// </summary>
        public List<FailureGroup> Group(IEnumerable<PluginTraceRecord> records,
            IDictionary<Guid, FailureFingerprint> fingerprintsOut = null, IDictionary<Guid, string> groupKeysOut = null)
        {
            var groups = new Dictionary<string, FailureGroup>(StringComparer.Ordinal);
            foreach (var record in records ?? Enumerable.Empty<PluginTraceRecord>())
            {
                if (record == null || !record.HasException) continue;
                var fp = Fingerprint(record);
                var key = GroupKey(fp, record);
                if (fingerprintsOut != null) fingerprintsOut[record.Id] = fp;
                if (groupKeysOut != null) groupKeysOut[record.Id] = key;

                if (!groups.TryGetValue(key, out var group))
                {
                    group = new FailureGroup { Key = key, Fingerprint = fp };
                    groups.Add(key, group);
                }
                group.Traces.Add(record);
            }

            foreach (var g in groups.Values)
            {
                g.Traces.Sort((a, b) => b.CreatedOnUtc.CompareTo(a.CreatedOnUtc));
                // Recurring and user-facing (synchronous) → Error; recurring asynchronous or repeated → Warning; single → Information.
                var recurring = g.Occurrences >= _options.RecurringMinOccurrences;
                g.Severity = recurring && g.Traces.Any(t => t.Mode != 1) ? Severity.Error
                    : g.Occurrences > 1 ? Severity.Warning : Severity.Information;
            }

            return groups.Values
                .OrderByDescending(g => g.Occurrences)
                .ThenByDescending(g => g.LastUtc)
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Lower-case hex of SHA-256, truncated to <paramref name="hexChars"/> characters.</summary>
        public static string Hash(string input, int hexChars)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? ""));
                var sb = new StringBuilder(hexChars);
                for (var i = 0; i < bytes.Length && sb.Length < hexChars; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString(0, Math.Min(hexChars, sb.Length));
            }
        }
    }
}
