using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DiagnosticsWorkbench.Analysis;
using DiagnosticsWorkbench.Models;

namespace DiagnosticsWorkbench.Export
{
    /// <summary>UTF-8 (with BOM, so Excel detects it) CSV writers for each result view.</summary>
    public static class CsvExporter
    {
        public static readonly Encoding Utf8Bom = new UTF8Encoding(true);

        public static void WriteLogs(TextWriter w, IEnumerable<TraceLog> logs, DiagnosticsSession session, bool redact = false)
        {
            Row(w, "Created on (UTC)", "Plugin type", "Message", "Table", "Mode", "Depth", "Operation", "Duration (ms)", "Constructor (ms)",
                "Slow", "Threshold (ms)", "Error", "Exception", "Correlation ID", "Request ID", "Step", "Stage", "Filtering attributes",
                "Created by", "Trace log ID", "Exception details", "Message block");
            foreach (var l in logs)
            {
                var step = session?.StepFor(l);
                Row(w, Formatting.Iso(l.CreatedOn), l.FullTypeName, l.MessageName, l.PrimaryEntity, l.ModeText, l.Depth.ToString(), l.OperationTypeText,
                    l.ExecutionDurationMs?.ToString(), l.ConstructorDurationMs?.ToString(),
                    session != null && session.Performance.IsSlow(l) ? "Yes" : "No",
                    session?.Performance.ThresholdFor(l).ToString(),
                    l.IsError ? "Yes" : "No",
                    R(ExceptionNormalizer.Headline(l.ExceptionDetails), redact),
                    redact ? "" : l.CorrelationId?.ToString(), redact ? "" : l.RequestId?.ToString(),
                    step?.Name, step?.StageText, step?.FilteringAttributes,
                    redact ? "" : l.CreatedByName, redact ? "" : l.Id.ToString(),
                    R(l.ExceptionDetails, redact), redact ? "" : l.MessageBlock);
            }
        }

        public static void WriteErrorGroups(TextWriter w, IEnumerable<ErrorGroup> groups, bool redact = false)
        {
            Row(w, "Fingerprint", "Count", "Executions", "Plugin type", "Message", "Table", "First seen (UTC)", "Last seen (UTC)",
                "Exception", "Normalized", "Configuration flags", "Sample exception");
            foreach (var g in groups)
                Row(w, g.Fingerprint, g.Count.ToString(), g.DistinctExecutions.ToString(), g.TypeName, g.MessageName, g.PrimaryEntity,
                    Formatting.Iso(g.FirstSeen), Formatting.Iso(g.LastSeen), R(g.Headline, redact), g.NormalizedText,
                    string.Join(" | ", g.Flags.Select(f => f.Message)), R(g.SampleException, redact));
        }

        public static void WritePerformance(TextWriter w, IEnumerable<PerformanceStat> stats)
        {
            Row(w, "Plugin type", "Message", "Table", "Mode", "Stage", "Runs", "p50 (ms)", "p95 (ms)", "Max (ms)", "Average (ms)",
                "Threshold (ms)", "Slow runs", "Near timeout", "Errors", "Configuration flags");
            foreach (var s in stats)
                Row(w, s.TypeName, s.MessageName, s.PrimaryEntity, s.ModeText, s.StageText, s.Count.ToString(), s.P50Ms.ToString(), s.P95Ms.ToString(),
                    s.MaxMs.ToString(), Math.Round(s.AverageMs).ToString(), s.ThresholdMs.ToString(), s.SlowCount.ToString(),
                    s.NearTimeoutCount.ToString(), s.ErrorCount.ToString(), string.Join(" | ", s.Flags.Select(f => f.Message)));
        }

        public static void WriteChains(TextWriter w, IEnumerable<CorrelationChain> chains, bool redact = false)
        {
            Row(w, "Correlation ID", "Start (UTC)", "Span (ms)", "Steps", "Max depth", "Requests", "Failed", "Errors",
                "Order", "Depth", "Plugin type", "Message", "Table", "Mode", "Duration (ms)", "Exception");
            var chainNo = 0;
            foreach (var c in chains)
            {
                chainNo++;
                var order = 0;
                var id = redact ? "chain " + chainNo : c.CorrelationId.ToString();
                foreach (var e in c.Entries)
                    Row(w, id, Formatting.Iso(c.Start), c.SpanMs.ToString(), c.Entries.Count.ToString(), c.MaxDepth.ToString(), c.RequestCount.ToString(),
                        c.Failed ? "Yes" : "No", c.ErrorCount.ToString(), (++order).ToString(), e.Depth.ToString(), e.FullTypeName, e.MessageName,
                        e.PrimaryEntity, e.ModeText, e.ExecutionDurationMs?.ToString(), R(ExceptionNormalizer.Headline(e.ExceptionDetails), redact));
            }
        }

        private static string R(string text, bool redact) => redact ? Formatting.RedactGuids(text) : text;

        public static void Row(TextWriter w, params string[] values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0) w.Write(',');
                w.Write(Escape(values[i]));
            }
            w.Write("\r\n");
        }

        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            // Neutralise spreadsheet formulas (CSV injection) in text cells.
            if ("=+-@".IndexOf(value[0]) >= 0 && !double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                value = "'" + value;
            var needsQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || value != value.Trim();
            return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
