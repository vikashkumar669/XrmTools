using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Reporting
{
    /// <summary>
    /// Standalone HTML report: one file, inline CSS, no scripts, no external resources. A Content-Security-Policy meta tag
    /// blocks scripts even if one were injected, and every value is HTML-encoded.
    /// </summary>
    public static class HtmlReportWriter
    {
        // Literal colours (no CSS variables) and no flex gap, so the report also renders in the WebBrowser (IE11 engine) preview.
        private const string Css = @"
*{box-sizing:border-box}body{margin:0;font-family:'Segoe UI',system-ui,-apple-system,sans-serif;color:#1d2321;background:#f6f8f7;font-size:14px;line-height:1.5}
.wrap{max-width:1180px;margin:0 auto;padding:24px 20px 48px}
header{background:#0f766e;color:#fff;padding:22px 0}header .wrap{padding-top:0;padding-bottom:0}
h1{margin:0;font-size:24px;font-weight:600}header p{margin:4px 0 0;opacity:.9}
h2{font-size:18px;margin:32px 0 12px;padding-bottom:6px;border-bottom:2px solid #e6f4f2;color:#0f766e}
h3{font-size:15px;margin:0}
.card{background:#fff;border:1px solid #dfe5e3;border-radius:8px;padding:14px 16px;margin:0 0 12px}
.kpis{display:flex;flex-wrap:wrap;margin:0 -6px}.kpi{flex:1 1 150px;margin:0 6px 12px;background:#fff;border:1px solid #dfe5e3;border-radius:8px;padding:12px 14px}
.kpi b{display:block;font-size:24px;color:#0f766e}.kpi span{color:#5b6662;font-size:12px}
table{width:100%;border-collapse:collapse;background:#fff;border:1px solid #dfe5e3;font-size:13px}
th,td{text-align:left;vertical-align:top;padding:6px 8px;border-bottom:1px solid #dfe5e3}th{background:#e6f4f2;color:#0f766e;font-weight:600}
td.num,th.num{text-align:right;white-space:nowrap}code,.mono{font-family:Consolas,'Cascadia Mono',monospace;font-size:12px;word-break:break-all}
.sev{display:inline-block;padding:1px 8px;border-radius:10px;font-size:12px;font-weight:600}
.sev-Error{background:#fdecea;color:#b42318}.sev-Warning{background:#fff4e0;color:#a15c07}.sev-Information{background:#e8f1fb;color:#1d5fa8}
.finding{border-left:5px solid #1d5fa8}.finding.Error{border-left-color:#b42318}.finding.Warning{border-left-color:#e08a00}
.meta{color:#5b6662;font-size:12px}
.tag{display:inline-block;min-width:150px;font-size:11px;font-weight:600;text-transform:uppercase;letter-spacing:.03em;margin-right:6px}
.t-fact{color:#1b6e3a}.t-metric{color:#1d5fa8}.t-corr{color:#7a4bb3}.t-hyp{color:#a15c07}
ul.ev{list-style:none;padding:0;margin:8px 0}ul.ev li{padding:2px 0}
.legend li{margin:2px 0}.note{background:#fff4e0;border:1px solid #f3d19c;border-radius:6px;padding:10px 12px}
.ok{background:#e6f4f2;border:1px solid #b7dcd6;border-radius:6px;padding:10px 12px}
footer{color:#5b6662;font-size:12px;margin-top:32px}";

        public static string Write(AnalysisResult result, ReportOptions options = null)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            options = options ?? new ReportOptions();
            var r = options.CreateRedactor();
            var s = result.Summary;
            var o = s.Organization;
            var sb = new StringBuilder();

            sb.Append("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
              .Append("<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">")
              .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:\">")
              .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
              .Append("<title>").Append(E(ReportContent.Title + " – " + (o.OrganizationName ?? o.ConnectionName ?? "Dataverse"))).Append("</title>")
              .Append("<style>").Append(Css).Append("</style></head><body>");

            sb.Append("<header><div class=\"wrap\"><h1>").Append(E(ReportContent.Title)).Append("</h1><p>")
              .Append(E(r.Redact(o.OrganizationName ?? o.ConnectionName ?? "(organization)"))).Append(" · generated ")
              .Append(E(Fmt.Utc(s.AnalyzedUtc))).Append("</p></div></header><div class=\"wrap\">");

            // How to read
            sb.Append("<h2>How to read this report</h2><div class=\"card\"><ul class=\"ev legend\">")
              .Append("<li><span class=\"tag t-fact\">Observed fact</span>Read directly from the retrieved traces.</li>")
              .Append("<li><span class=\"tag t-metric\">Calculated metric</span>Computed from the traces (rates, medians, percentiles, ratios).</li>")
              .Append("<li><span class=\"tag t-corr\">Correlation evidence</span>Each item names its evidence level: exact match, same causal chain, suspected parent/child, heuristic or same pattern.</li>")
              .Append("<li><span class=\"tag t-hyp\">Possible explanation</span>A hypothesis to check, never a confirmed root cause.</li>")
              .Append("</ul></div>");

            // 1. Scope
            sb.Append("<h2>1. Scope</h2><table><tbody>");
            Kv(sb, "Organization", r.Redact(o.OrganizationName ?? o.ConnectionName));
            Kv(sb, "URL", r.Redact(o.Url));
            Kv(sb, "Environment ID", o.EnvironmentId == null ? null : r.Redact(o.EnvironmentId));
            Kv(sb, "Version", o.OrganizationVersion);
            Kv(sb, "Plug-in trace setting", o.TraceSetting ?? "Unknown");
            Kv(sb, "Analysis date", Fmt.Utc(s.AnalyzedUtc));
            Kv(sb, "Time range", Fmt.Utc(s.Query.FromUtc) + " → " + Fmt.Utc(s.Query.ToUtc));
            Kv(sb, "Query filters", r.Redact(s.Query.Describe()));
            Kv(sb, "Result limit", Fmt.Count(s.Query.ResultLimit) + (s.Query.LimitReached ? " (reached: more traces matched)" : " (not reached)"));
            Kv(sb, "Redaction", options.IsRedacted ? "GUIDs, e-mail addresses, URL query strings and custom values redacted as configured; credentials always removed" : "Credentials removed; no other redaction");
            Kv(sb, "Tool", options.ToolVersion);
            sb.Append("</tbody></table>");
            if (s.RetrievalNotes.Count > 0) List(sb, "note", s.RetrievalNotes.Select(r.Redact));

            // 2. Summary
            sb.Append("<h2>2. Summary</h2><div class=\"kpis\">");
            Kpi(sb, s.TotalTraces, "traces analyzed");
            Kpi(sb, s.TracesWithException, "with an exception");
            Kpi(sb, s.UniqueFailureGroups, "unique failure groups");
            Kpi(sb, s.RecurringFailureGroups, "recurring groups");
            Kpi(sb, s.SlowExecutions, "slow executions");
            Kpi(sb, result.Findings.Count, "findings");
            sb.Append("</div><div class=\"card\" style=\"margin-top:12px\"><h3>Diagnostic evidence completeness</h3><p>")
              .Append(E($"{Fmt.Count(s.CompleteEvidence)} traces have complete evidence and {Fmt.Count(s.IncompleteEvidence)} are missing at least one identifier used by the analysis."))
              .Append("</p>");
            if (s.MissingIdentifierCounts.Count > 0)
            {
                sb.Append("<table><thead><tr><th>Missing column</th><th class=\"num\">Traces</th><th class=\"num\">Share</th></tr></thead><tbody>");
                foreach (var p in s.MissingIdentifierCounts.OrderByDescending(p => p.Value))
                    sb.Append("<tr><td><code>").Append(E(p.Key)).Append("</code></td><td class=\"num\">").Append(Fmt.Count(p.Value))
                      .Append("</td><td class=\"num\">").Append(E(Fmt.Percent(p.Value, s.TotalTraces))).Append("</td></tr>");
                sb.Append("</tbody></table>");
            }
            sb.Append("</div>");

            // 3. Findings
            sb.Append("<h2>3. Diagnostic findings</h2>");
            if (result.Findings.Count == 0) sb.Append("<div class=\"ok\">No rule produced a finding for this data.</div>");
            foreach (var f in result.Findings)
            {
                sb.Append("<div class=\"card finding ").Append(f.Severity).Append("\"><h3>").Append(E(f.Id)).Append(" · ").Append(E(r.Redact(f.Title))).Append("</h3>")
                  .Append("<div class=\"meta\"><span class=\"sev sev-").Append(f.Severity).Append("\">").Append(f.Severity).Append("</span> ")
                  .Append("Confidence: ").Append(f.Confidence).Append(" · Rule ").Append(E(f.RuleId)).Append("</div>")
                  .Append("<p>").Append(E(r.Redact(f.Description))).Append("</p><ul class=\"ev\">");
                foreach (var x in f.Facts) Ev(sb, "t-fact", "Observed fact", r.Redact(x));
                foreach (var x in f.Metrics) Ev(sb, "t-metric", "Calculated metric", r.Redact(x));
                foreach (var x in f.Correlations) Ev(sb, "t-corr", "Correlation evidence", r.Redact(x));
                foreach (var x in f.PossibleExplanations) Ev(sb, "t-hyp", "Possible explanation", r.Redact(x));
                sb.Append("</ul>");
                if (f.Recommendations.Count > 0)
                {
                    sb.Append("<b>Recommended investigation steps</b><ol>");
                    foreach (var x in f.Recommendations) sb.Append("<li>").Append(E(r.Redact(x))).Append("</li>");
                    sb.Append("</ol>");
                }
                if (options.IncludeTraceIds && f.TraceIds.Count > 0)
                {
                    sb.Append("<div class=\"meta\">Supporting traces: <span class=\"mono\">")
                      .Append(E(string.Join(", ", f.TraceIds.Select(id => r.Redact(id)))))
                      .Append("</span>").Append(f.AdditionalTraceCount > 0 ? E($" and {Fmt.Count(f.AdditionalTraceCount)} more") : "").Append("</div>");
                }
                sb.Append("</div>");
            }

            // 4. Recurring failure groups
            sb.Append("<h2>4. Recurring failure groups</h2>");
            if (result.FailureGroups.Count == 0) sb.Append("<div class=\"ok\">No failed traces in this analysis.</div>");
            else
            {
                sb.Append("<table><thead><tr><th>Fingerprint</th><th>Severity</th><th>Exception</th><th>Message</th><th class=\"num\">Count</th><th>First (UTC)</th><th>Latest (UTC)</th><th>Plug-in types</th><th>Tables</th></tr></thead><tbody>");
                foreach (var g in result.FailureGroups.Take(options.MaxRows))
                {
                    sb.Append("<tr><td class=\"mono\">").Append(E(g.Key)).Append("</td><td><span class=\"sev sev-").Append(g.Severity).Append("\">").Append(g.Severity).Append("</span></td><td>")
                      .Append(E(g.Fingerprint.Label))
                      .Append("</td><td>").Append(E(Fmt.OneLine(r.Redact(g.Fingerprint.Message), 220))).Append("</td><td class=\"num\">").Append(Fmt.Count(g.Occurrences))
                      .Append("</td><td>").Append(E(Fmt.Utc(g.FirstUtc))).Append("</td><td>").Append(E(Fmt.Utc(g.LastUtc)))
                      .Append("</td><td>").Append(E(Fmt.List(g.PluginTypes.Select(TypeNames.ShortName)))).Append("</td><td>").Append(E(Fmt.List(g.PrimaryEntities))).Append("</td></tr>");
                }
                sb.Append("</tbody></table>");
                More(sb, result.FailureGroups.Count, options.MaxRows, "failure groups");
            }

            // 5. Performance
            sb.Append("<h2>5. Performance observations</h2><p class=\"meta\">")
              .Append(E($"Thresholds: synchronous {Fmt.Ms(result.Options.SlowSyncThresholdMs)}, asynchronous {Fmt.Ms(result.Options.SlowAsyncThresholdMs)}. Average and median need {result.Options.MinSampleForStatistics} measured executions; the 95th percentile (nearest rank) needs {result.Options.MinSampleForPercentile}."))
              .Append("</p>");
            var perf = result.PerformanceGroups.Where(p => p.SlowCount > 0).ToList();
            if (perf.Count == 0) sb.Append("<div class=\"ok\">").Append(E(s.MeasuredExecutions == 0 ? "No executions with a recorded duration." : "No execution reached its configured threshold.")).Append("</div>");
            else
            {
                sb.Append("<table><thead><tr><th>Plug-in type</th><th>Message</th><th>Mode</th><th class=\"num\">Measured</th><th class=\"num\">Slow</th><th class=\"num\">Average</th><th class=\"num\">Median</th><th class=\"num\">P95</th><th class=\"num\">Max</th><th>Note</th></tr></thead><tbody>");
                foreach (var p in perf.Take(options.MaxRows))
                {
                    sb.Append("<tr><td>").Append(E(TypeNames.ShortName(p.PluginType))).Append("</td><td>").Append(E(p.MessageName)).Append("</td><td>").Append(E(p.ModeText))
                      .Append("</td><td class=\"num\">").Append(Fmt.Count(p.Measured)).Append("</td><td class=\"num\">").Append(Fmt.Count(p.SlowCount))
                      .Append(p.NearTimeoutCount > 0 ? " <span class=\"sev sev-Error\">" + Fmt.Count(p.NearTimeoutCount) + " near timeout</span>" : "")
                      .Append("</td><td class=\"num\">").Append(E(Fmt.Ms(p.AverageMs))).Append("</td><td class=\"num\">").Append(E(Fmt.Ms(p.MedianMs)))
                      .Append("</td><td class=\"num\">").Append(E(Fmt.Ms(p.P95Ms))).Append("</td><td class=\"num\">").Append(E(Fmt.Ms(p.MaxMs)))
                      .Append("</td><td class=\"meta\">").Append(E(p.SampleNote)).Append("</td></tr>");
                }
                sb.Append("</tbody></table>");
                More(sb, perf.Count, options.MaxRows, "performance groups");
            }

            // 6. Correlation evidence
            sb.Append("<h2>6. Correlation evidence</h2><div class=\"card\"><ul class=\"ev legend\">");
            foreach (EvidenceLevel level in Enum.GetValues(typeof(EvidenceLevel)))
                sb.Append("<li><b>").Append(E(EvidenceText.Label(level))).Append("</b>: ").Append(E(EvidenceText.Meaning(level))).Append("</li>");
            sb.Append("</ul></div>");
            var chains = result.Clusters.Where(c => c.FailureCount > 0).ToList();
            if (chains.Count == 0) sb.Append("<div class=\"ok\">No correlation ID links a failure to another trace in this analysis.</div>");
            else
            {
                sb.Append("<table><thead><tr><th>Correlation ID</th><th class=\"num\">Traces</th><th class=\"num\">Failures</th><th class=\"num\">Requests</th><th class=\"num\">Max depth</th><th>Plug-in types</th><th class=\"num\">Suspected parent/child links</th><th>Span (UTC)</th></tr></thead><tbody>");
                foreach (var c in chains.Take(options.MaxRows))
                {
                    sb.Append("<tr><td class=\"mono\">").Append(E(r.Redact(c.CorrelationId))).Append("</td><td class=\"num\">").Append(Fmt.Count(c.Traces.Count))
                      .Append("</td><td class=\"num\">").Append(Fmt.Count(c.FailureCount)).Append("</td><td class=\"num\">").Append(Fmt.Count(c.DistinctRequests))
                      .Append("</td><td class=\"num\">").Append(c.MaxDepth).Append("</td><td>").Append(E(Fmt.List(c.PluginTypes.Select(TypeNames.ShortName))))
                      .Append("</td><td class=\"num\">").Append(Fmt.Count(c.SuspectedEdges.Count)).Append("</td><td>").Append(E(Fmt.Utc(c.FirstUtc))).Append(" → ").Append(E(Fmt.Utc(c.LastUtc))).Append("</td></tr>");
                }
                sb.Append("</tbody></table>");
                More(sb, chains.Count, options.MaxRows, "causal chains");
            }

            // 7. Trend
            sb.Append("<h2>7. Comparison with a previous period</h2>");
            var t = result.Trend;
            if (t == null || !t.Available)
                sb.Append("<div class=\"note\"><b>Trend analysis unavailable.</b> ").Append(E(t?.UnavailableReason ?? "No baseline.")).Append("</div>");
            else
            {
                sb.Append("<p>Baseline: ").Append(E(t.BaselineDescription)).Append("</p>");
                if (t.Caveats.Count > 0) List(sb, "note", t.Caveats);
                var changes = t.Failures.Where(x => x.Status != TrendStatus.Stable).ToList();
                if (changes.Count > 0)
                {
                    sb.Append("<table><thead><tr><th>Failure group</th><th>Status</th><th class=\"num\">Current</th><th class=\"num\">Baseline</th><th class=\"num\">Current /h</th><th class=\"num\">Baseline /h</th></tr></thead><tbody>");
                    foreach (var x in changes.Take(options.MaxRows))
                        sb.Append("<tr><td>").Append(E(x.Label)).Append(" <span class=\"meta mono\">").Append(E(x.Key)).Append("</span></td><td>").Append(x.Status)
                          .Append("</td><td class=\"num\">").Append(Fmt.Count(x.CurrentCount)).Append("</td><td class=\"num\">").Append(Fmt.Count(x.BaselineCount))
                          .Append("</td><td class=\"num\">").Append(E(Fmt.Rate(x.CurrentPerHour))).Append("</td><td class=\"num\">").Append(E(Fmt.Rate(x.BaselinePerHour))).Append("</td></tr>");
                    sb.Append("</tbody></table>");
                }
                else sb.Append("<div class=\"ok\">No failure group changed beyond the configured ratio.</div>");
                if (t.Performance.Count > 0)
                {
                    sb.Append("<table style=\"margin-top:12px\"><thead><tr><th>Step</th><th class=\"num\">Baseline median</th><th class=\"num\">Current median</th><th class=\"num\">Ratio</th><th>Regression</th></tr></thead><tbody>");
                    foreach (var x in t.Performance.Take(options.MaxRows))
                        sb.Append("<tr><td>").Append(E(x.Label)).Append("</td><td class=\"num\">").Append(E(Fmt.Ms(x.BaselineMedianMs))).Append("</td><td class=\"num\">")
                          .Append(E(Fmt.Ms(x.CurrentMedianMs))).Append("</td><td class=\"num\">").Append(E(Fmt.Ratio(x.Ratio))).Append("</td><td>").Append(x.IsRegression ? "Yes" : "No").Append("</td></tr>");
                    sb.Append("</tbody></table>");
                }
            }

            // 8. Recommended steps
            sb.Append("<h2>8. Recommended investigation steps</h2>");
            var recs = ReportContent.AllRecommendations(result);
            if (recs.Count == 0) sb.Append("<p>No specific steps: no findings were produced.</p>");
            else
            {
                sb.Append("<div class=\"card\"><ol>");
                foreach (var x in recs) sb.Append("<li>").Append(E(r.Redact(x))).Append("</li>");
                sb.Append("</ol></div>");
            }

            // 9. Limitations
            sb.Append("<h2>9. Notes and limitations</h2><div class=\"card\"><ul>");
            foreach (var x in ReportContent.StandardLimitations) sb.Append("<li>").Append(E(x)).Append("</li>");
            foreach (var p in result.InvalidRules) sb.Append("<li>").Append(E($"Custom normalization rule \"{p.Key}\" was skipped: {p.Value}")).Append("</li>");
            sb.Append("</ul></div>");

            sb.Append("<footer>").Append(E(options.ToolVersion)).Append(" · read-only analysis of plugintracelog · no data was changed in Dataverse.</footer>");
            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        /// <summary>HTML-encodes text (null → empty).</summary>
        public static string E(string text) => string.IsNullOrEmpty(text) ? "" : WebUtility.HtmlEncode(text);

        private static void Kv(StringBuilder sb, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            sb.Append("<tr><th style=\"width:220px\">").Append(E(key)).Append("</th><td>").Append(E(value)).Append("</td></tr>");
        }

        private static void Kpi(StringBuilder sb, int value, string caption) =>
            sb.Append("<div class=\"kpi\"><b>").Append(Fmt.Count(value)).Append("</b><span>").Append(E(caption)).Append("</span></div>");

        private static void Ev(StringBuilder sb, string css, string label, string text) =>
            sb.Append("<li><span class=\"tag ").Append(css).Append("\">").Append(E(label)).Append("</span>").Append(E(text)).Append("</li>");

        private static void List(StringBuilder sb, string css, IEnumerable<string> items)
        {
            sb.Append("<div class=\"").Append(css).Append("\" style=\"margin-top:10px\"><ul style=\"margin:0\">");
            foreach (var x in items) sb.Append("<li>").Append(E(x)).Append("</li>");
            sb.Append("</ul></div>");
        }

        private static void More(StringBuilder sb, int total, int shown, string what)
        {
            if (total > shown) sb.Append("<p class=\"meta\">").Append(E($"Showing {Fmt.Count(shown)} of {Fmt.Count(total)} {what}. Export CSV for the full list.")).Append("</p>");
        }
    }
}
