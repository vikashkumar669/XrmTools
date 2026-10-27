using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Reporting
{
    /// <summary>
    /// CSV exports (RFC 4180 quoting). Text cells starting with = + - @ or a control character are prefixed with an
    /// apostrophe so spreadsheet applications don't evaluate them as formulas. Write the result with a UTF-8 BOM
    /// (see <see cref="Utf8WithBom"/>) so Excel detects the encoding.
    /// </summary>
    public static class CsvReportWriter
    {
        public static readonly Encoding Utf8WithBom = new UTF8Encoding(true);

        public static string Findings(AnalysisResult result, ReportOptions options = null)
        {
            options = options ?? new ReportOptions();
            var r = options.CreateRedactor();
            var sb = new StringBuilder();
            Row(sb, "Finding ID", "Severity", "Confidence", "Rule", "Title", "Description", "Observed facts", "Calculated metrics",
                "Correlation evidence", "Possible explanations", "Recommended steps", "Trace IDs");
            foreach (var f in result.Findings)
            {
                Row(sb, f.Id, f.Severity.ToString(), f.Confidence.ToString(), f.RuleId, r.Redact(f.Title), r.Redact(f.Description),
                    Join(f.Facts, r), Join(f.Metrics, r), Join(f.Correlations, r), Join(f.PossibleExplanations, r), Join(f.Recommendations, r),
                    options.IncludeTraceIds ? string.Join(" ", f.TraceIds.Select(id => r.Redact(id))) + (f.AdditionalTraceCount > 0 ? $" (+{f.AdditionalTraceCount} more)" : "") : "");
            }
            return sb.ToString();
        }

        public static string FailureGroups(AnalysisResult result, ReportOptions options = null)
        {
            options = options ?? new ReportOptions();
            var r = options.CreateRedactor();
            var sb = new StringBuilder();
            Row(sb, "Fingerprint", "Severity", "Exception type", "Error code", "Message", "Normalized message", "Occurrences",
                "First occurrence (UTC)", "Latest occurrence (UTC)", "Plug-in types", "Messages", "Tables", "Distinct requests", "Distinct correlations", "Rate per hour");
            foreach (var g in result.FailureGroups)
            {
                Row(sb, g.Key, g.Severity.ToString(), g.Fingerprint.ExceptionType, g.Fingerprint.ErrorCode ?? "", r.Redact(g.Fingerprint.Message),
                    r.Redact(g.Fingerprint.NormalizedMessage), Num(g.Occurrences), Fmt.UtcIso(g.FirstUtc), Fmt.UtcIso(g.LastUtc),
                    string.Join("; ", g.PluginTypes), string.Join("; ", g.Messages), string.Join("; ", g.PrimaryEntities),
                    Num(g.DistinctRequests), Num(g.DistinctCorrelations), g.RatePerHour.ToString("0.##", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static string Performance(AnalysisResult result, ReportOptions options = null)
        {
            var sb = new StringBuilder();
            Row(sb, "Plug-in type", "Message", "Tables", "Mode", "Executions", "Measured", "Threshold (ms)", "Slow", "Near timeout",
                "Average (ms)", "Median (ms)", "P95 (ms)", "Max (ms)", "Sample note");
            foreach (var p in result.PerformanceGroups)
            {
                Row(sb, p.PluginType, p.MessageName, p.PrimaryEntity, p.ModeText, Num(p.Executions), Num(p.Measured), Num(p.ThresholdMs),
                    Num(p.SlowCount), Num(p.NearTimeoutCount), Dbl(p.AverageMs), Dbl(p.MedianMs), Dbl(p.P95Ms),
                    p.MaxMs?.ToString(CultureInfo.InvariantCulture) ?? "", p.SampleNote ?? "");
            }
            return sb.ToString();
        }

        /// <summary>One row per trace. Trace text (message block) is never exported; only the exception's first line.</summary>
        public static string Traces(AnalysisResult result, IEnumerable<PluginTraceRecord> traces = null, ReportOptions options = null)
        {
            options = options ?? new ReportOptions();
            var r = options.CreateRedactor();
            var sb = new StringBuilder();
            Row(sb, "Trace ID", "Created (UTC)", "Plug-in type", "Message", "Table", "Mode", "Depth", "Duration (ms)", "Has exception",
                "Exception type", "Exception message", "Fingerprint", "Correlation ID", "Request ID", "Step ID");
            foreach (var t in traces ?? result.Records)
            {
                result.Fingerprints.TryGetValue(t.Id, out var fp);
                result.GroupKeys.TryGetValue(t.Id, out var key);
                Row(sb, r.Redact(t.Id), Fmt.UtcIso(t.CreatedOnUtc), t.FullTypeName ?? "", t.MessageName ?? "", t.PrimaryEntity ?? "", t.ModeText,
                    t.Depth?.ToString(CultureInfo.InvariantCulture) ?? "", t.ExecutionDurationMs?.ToString(CultureInfo.InvariantCulture) ?? "",
                    t.HasException ? "Yes" : (t.ExceptionLoaded ? "No" : "Unknown"), fp?.ExceptionType ?? "", r.Redact(fp?.Message ?? ""), key ?? "",
                    r.Redact(t.CorrelationId), r.Redact(t.RequestId), r.Redact(t.PluginStepId));
            }
            return sb.ToString();
        }

        /// <summary>Escapes one CSV cell.</summary>
        public static string Escape(string value)
        {
            if (value == null) return "";
            if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
            var needsQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || value.StartsWith(" ", StringComparison.Ordinal) || value.EndsWith(" ", StringComparison.Ordinal);
            return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        private static void Row(StringBuilder sb, params string[] cells)
        {
            sb.Append(string.Join(",", cells.Select(Escape))).Append("\r\n");
        }

        /// <summary>Culture-invariant numbers (never negative, so the formula guard doesn't touch them).</summary>
        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Dbl(double? value) => value.HasValue ? Math.Round(value.Value, 1).ToString("0.#", CultureInfo.InvariantCulture) : "";
        private static string Join(IEnumerable<string> items, SensitiveDataRedactor r) => string.Join(" | ", items.Select(r.Redact));
    }
}
