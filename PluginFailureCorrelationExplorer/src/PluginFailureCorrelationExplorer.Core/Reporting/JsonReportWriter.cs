using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Reporting
{
    /// <summary>Machine-readable JSON report with the same content and redaction as the HTML report.</summary>
    public static class JsonReportWriter
    {
        public const string SchemaVersion = "1.0";

        public static string Write(AnalysisResult result, ReportOptions options = null)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            options = options ?? new ReportOptions();
            var r = options.CreateRedactor();
            var s = result.Summary;
            var w = new JsonWriter();

            w.BeginObject();
            w.Prop("schemaVersion", SchemaVersion);
            w.Prop("tool", options.ToolVersion);
            w.Prop("generatedUtc", Fmt.UtcIso(s.AnalyzedUtc));
            w.Prop("redacted", options.IsRedacted);

            w.Name("organization"); w.BeginObject();
            w.Prop("name", r.Redact(s.Organization.OrganizationName ?? s.Organization.ConnectionName));
            w.Prop("url", r.Redact(s.Organization.Url));
            w.Prop("environmentId", s.Organization.EnvironmentId == null ? null : r.Redact(s.Organization.EnvironmentId));
            w.Prop("version", s.Organization.OrganizationVersion);
            w.Prop("traceSetting", s.Organization.TraceSetting);
            w.EndObject();

            w.Name("query"); w.BeginObject();
            w.Prop("fromUtc", Fmt.UtcIso(s.Query.FromUtc));
            w.Prop("toUtc", Fmt.UtcIso(s.Query.ToUtc));
            w.Prop("filters", r.Redact(s.Query.Describe()));
            w.Prop("resultLimit", s.Query.ResultLimit);
            w.Prop("limitReached", s.Query.LimitReached);
            w.Name("retrievalNotes"); w.Strings(s.RetrievalNotes.Select(r.Redact));
            w.EndObject();

            w.Name("summary"); w.BeginObject();
            w.Prop("totalTraces", s.TotalTraces);
            w.Prop("tracesWithException", s.TracesWithException);
            w.Prop("uniqueFailureGroups", s.UniqueFailureGroups);
            w.Prop("recurringFailureGroups", s.RecurringFailureGroups);
            w.Prop("slowExecutions", s.SlowExecutions);
            w.Prop("measuredExecutions", s.MeasuredExecutions);
            w.Prop("completeEvidence", s.CompleteEvidence);
            w.Prop("incompleteEvidence", s.IncompleteEvidence);
            w.Name("missingIdentifiers"); w.BeginObject();
            foreach (var p in s.MissingIdentifierCounts.OrderBy(p => p.Key, StringComparer.Ordinal)) w.Prop(p.Key, p.Value);
            w.EndObject();
            w.EndObject();

            w.Name("findings"); w.BeginArray();
            foreach (var f in result.Findings)
            {
                w.BeginObject();
                w.Prop("id", f.Id);
                w.Prop("ruleId", f.RuleId);
                w.Prop("severity", f.Severity.ToString());
                w.Prop("confidence", f.Confidence.ToString());
                w.Prop("title", r.Redact(f.Title));
                w.Prop("description", r.Redact(f.Description));
                w.Name("observedFacts"); w.Strings(f.Facts.Select(r.Redact));
                w.Name("calculatedMetrics"); w.Strings(f.Metrics.Select(r.Redact));
                w.Name("correlationEvidence"); w.Strings(f.Correlations.Select(r.Redact));
                w.Name("possibleExplanations"); w.Strings(f.PossibleExplanations.Select(r.Redact));
                w.Name("recommendedSteps"); w.Strings(f.Recommendations.Select(r.Redact));
                w.Name("traceIds"); w.Strings(options.IncludeTraceIds ? f.TraceIds.Select(id => r.Redact(id)) : Enumerable.Empty<string>());
                w.Prop("additionalTraceCount", f.AdditionalTraceCount);
                w.EndObject();
            }
            w.EndArray();

            w.Name("failureGroups"); w.BeginArray();
            foreach (var g in result.FailureGroups)
            {
                w.BeginObject();
                w.Prop("fingerprint", g.Key);
                w.Prop("severity", g.Severity.ToString());
                w.Prop("exceptionType", g.Fingerprint.ExceptionType);
                w.Prop("errorCode", g.Fingerprint.ErrorCode);
                w.Prop("message", r.Redact(g.Fingerprint.Message));
                w.Prop("normalizedMessage", r.Redact(g.Fingerprint.NormalizedMessage));
                w.Prop("occurrences", g.Occurrences);
                w.Prop("firstUtc", Fmt.UtcIso(g.FirstUtc));
                w.Prop("latestUtc", Fmt.UtcIso(g.LastUtc));
                w.Name("pluginTypes"); w.Strings(g.PluginTypes);
                w.Name("messages"); w.Strings(g.Messages);
                w.Name("tables"); w.Strings(g.PrimaryEntities);
                w.Prop("distinctRequests", g.DistinctRequests);
                w.Prop("distinctCorrelations", g.DistinctCorrelations);
                w.EndObject();
            }
            w.EndArray();

            w.Name("performance"); w.BeginArray();
            foreach (var p in result.PerformanceGroups)
            {
                w.BeginObject();
                w.Prop("pluginType", p.PluginType);
                w.Prop("message", p.MessageName);
                w.Prop("mode", p.ModeText);
                w.Prop("executions", p.Executions);
                w.Prop("measured", p.Measured);
                w.Prop("thresholdMs", p.ThresholdMs);
                w.Prop("slow", p.SlowCount);
                w.Prop("nearTimeout", p.NearTimeoutCount);
                w.Prop("averageMs", p.AverageMs);
                w.Prop("medianMs", p.MedianMs);
                w.Prop("p95Ms", p.P95Ms);
                w.Prop("maxMs", p.MaxMs.HasValue ? p.MaxMs.Value : (double?)null);
                w.Prop("sampleNote", p.SampleNote);
                w.EndObject();
            }
            w.EndArray();

            w.Name("correlationChains"); w.BeginArray();
            foreach (var c in result.Clusters.Where(c => c.FailureCount > 0))
            {
                w.BeginObject();
                w.Prop("correlationId", r.Redact(c.CorrelationId));
                w.Prop("evidenceLevel", EvidenceText.Label(EvidenceLevel.SameCausalChain));
                w.Prop("traces", c.Traces.Count);
                w.Prop("failures", c.FailureCount);
                w.Prop("distinctRequests", c.DistinctRequests);
                w.Prop("suspectedParentChildLinks", c.SuspectedEdges.Count);
                w.EndObject();
            }
            w.EndArray();

            var t = result.Trend;
            w.Name("trend"); w.BeginObject();
            w.Prop("available", t != null && t.Available);
            w.Prop("unavailableReason", t?.UnavailableReason);
            w.Prop("baseline", t?.BaselineDescription);
            w.Name("caveats"); w.Strings(t?.Caveats ?? new List<string>());
            w.Name("failures"); w.BeginArray();
            foreach (var x in t?.Failures ?? new List<FailureTrend>())
            {
                w.BeginObject();
                w.Prop("fingerprint", x.Key); w.Prop("label", x.Label); w.Prop("status", x.Status.ToString());
                w.Prop("current", x.CurrentCount); w.Prop("baseline", x.BaselineCount);
                w.Prop("currentPerHour", x.CurrentPerHour); w.Prop("baselinePerHour", x.BaselinePerHour);
                w.EndObject();
            }
            w.EndArray();
            w.Name("performance"); w.BeginArray();
            foreach (var x in t?.Performance ?? new List<PerformanceTrend>())
            {
                w.BeginObject();
                w.Prop("key", x.Key); w.Prop("label", x.Label); w.Prop("baselineMedianMs", x.BaselineMedianMs);
                w.Prop("currentMedianMs", x.CurrentMedianMs); w.Prop("ratio", x.Ratio); w.Prop("regression", x.IsRegression);
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();

            w.Name("limitations"); w.Strings(ReportContent.StandardLimitations);
            w.EndObject();
            return w.ToString();
        }
    }

    /// <summary>Minimal, dependency-free JSON writer (indented, RFC 8259 string escaping).</summary>
    public sealed class JsonWriter
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private readonly Stack<bool> _first = new Stack<bool>();
        private bool _afterName;

        public void BeginObject() { Value(); _sb.Append('{'); _first.Push(true); }
        public void EndObject() { Close('}'); }
        public void BeginArray() { Value(); _sb.Append('['); _first.Push(true); }
        public void EndArray() { Close(']'); }

        public void Name(string name)
        {
            Separator();
            _sb.Append(Quote(name)).Append(": ");
            _afterName = true;
        }

        public void Prop(string name, string value) { Name(name); Raw(value == null ? "null" : Quote(value)); }
        public void Prop(string name, int value) { Name(name); Raw(value.ToString(CultureInfo.InvariantCulture)); }
        public void Prop(string name, bool value) { Name(name); Raw(value ? "true" : "false"); }
        public void Prop(string name, double? value)
        {
            Name(name);
            Raw(value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
                ? Math.Round(value.Value, 3).ToString("0.###", CultureInfo.InvariantCulture) : "null");
        }

        public void Strings(IEnumerable<string> values)
        {
            BeginArray();
            foreach (var v in values ?? Enumerable.Empty<string>()) Raw(v == null ? "null" : Quote(v));
            EndArray();
        }

        public override string ToString() => _sb.ToString();

        public static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '<': sb.Append("\\u003c"); break;   // safe to embed in HTML
                    case '>': sb.Append("\\u003e"); break;
                    default:
                        // Control characters, and U+2028/U+2029 (valid JSON but line breaks in JavaScript), are escaped.
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private void Raw(string token) { Value(); _sb.Append(token); }

        /// <summary>Writes the separator before a value, unless it follows a property name.</summary>
        private void Value()
        {
            if (_afterName) { _afterName = false; return; }
            Separator();
        }

        private void Separator()
        {
            if (_first.Count == 0) return;
            if (_first.Peek()) { _first.Pop(); _first.Push(false); }
            else _sb.Append(',');
            _sb.Append('\n').Append(' ', _first.Count * 2);
        }

        private void Close(char c)
        {
            var wasEmpty = _first.Pop();
            if (!wasEmpty) _sb.Append('\n').Append(' ', _first.Count * 2);
            _sb.Append(c);
        }
    }
}
