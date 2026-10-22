using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using DiagnosticsWorkbench.Analysis;
using DiagnosticsWorkbench.Models;

namespace DiagnosticsWorkbench.Export
{
    public sealed class HtmlReportOptions
    {
        /// <summary>Strip GUIDs, user names and trace output, for reports that leave the team.</summary>
        public bool Redact { get; set; }

        public int TopErrorGroups { get; set; } = 20;
        public int TopPerformance { get; set; } = 20;
        public int TopChains { get; set; } = 10;
        public string ToolVersion { get; set; }
    }

    /// <summary>One self-contained HTML file (inline CSS, no scripts) that can be e-mailed or attached to a ticket.</summary>
    public static class HtmlReportBuilder
    {
        public static string Build(DiagnosticsSession session, AnalysisResult result, ViewFilter filter, HtmlReportOptions options = null)
        {
            options = options ?? new HtmlReportOptions();
            var now = DateTime.UtcNow;
            var sb = new StringBuilder(64 * 1024);
            string E(string s) => WebUtility.HtmlEncode(s ?? "");
            string R(string s) => options.Redact ? Formatting.RedactGuids(s) : s;

            var q = session.Query;
            sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<title>Dataverse diagnostics report</title><style>").Append(Css).Append("</style></head><body><main>");

            // ---- Header ----
            sb.Append("<header><p class=\"kicker\">Dataverse Developer Diagnostics Workbench</p><h1>Diagnostics report</h1><dl class=\"meta\">");
            Meta(sb, "Environment", options.Redact ? "(redacted)" : E(session.EnvironmentName) + (string.IsNullOrEmpty(session.EnvironmentUrl) ? "" : " · " + E(session.EnvironmentUrl)));
            if (q != null)
            {
                Meta(sb, "Date range (UTC)", E(Formatting.Iso(q.FromUtc)) + " → " + E(Formatting.Iso(q.ToUtc)));
                Meta(sb, "Query filters", E(q.FiltersText));
            }
            Meta(sb, "View filters", E(Describe(filter)));
            Meta(sb, "Trace setting", E(TraceSettingText(session.TraceSetting)));
            Meta(sb, "Thresholds", E($"sync {session.Settings.SyncThresholdMs:N0} ms · async {session.Settings.AsyncThresholdMs:N0} ms · near timeout {session.Settings.NearTimeoutMs:N0} ms" +
                                     (session.Settings.ThresholdOverrides.Count > 0 ? $" · {session.Settings.ThresholdOverrides.Count} override(s)" : "")));
            Meta(sb, "Generated (UTC)", E(Formatting.Iso(now)) + (string.IsNullOrEmpty(options.ToolVersion) ? "" : " · v" + E(options.ToolVersion)));
            sb.Append("</dl></header>");

            if (session.TraceSetting == TraceSetting.Exception)
                sb.Append("<p class=\"note warn\">Trace logging is set to <b>Exception</b>: only failed executions were logged, so performance figures cover failures only.</p>");
            else if (session.TraceSetting == TraceSetting.Off)
                sb.Append("<p class=\"note crit\">Trace logging is <b>Off</b>: no new logs are being written.</p>");
            if (q != null && q.CapReached)
                sb.Append($"<p class=\"note warn\">The row cap of {q.RowCap:N0} was reached: older logs in the range were not loaded.</p>");

            // ---- Summary ----
            sb.Append("<section><h2>Summary</h2><div class=\"tiles\">");
            Tile(sb, "Logs", result.Logs.Count, null);
            Tile(sb, "Errors", result.ErrorCount, result.ErrorCount > 0 ? "crit" : null);
            Tile(sb, "Distinct errors", result.ErrorGroups.Count, null);
            Tile(sb, "Slow runs", result.SlowCount, result.SlowCount > 0 ? "warn" : null);
            Tile(sb, "Near timeout", result.NearTimeoutCount, result.NearTimeoutCount > 0 ? "crit" : null);
            Tile(sb, "Failed chains", result.FailedChainCount, result.FailedChainCount > 0 ? "crit" : null);
            sb.Append("</div></section>");

            // ---- Top error groups ----
            sb.Append("<section><h2>Top error groups</h2>");
            if (result.ErrorGroups.Count == 0) sb.Append("<p class=\"empty\">No errors in this view.</p>");
            else
            {
                sb.Append("<table><thead><tr><th class=\"num\">Hits</th><th>Error</th><th>Plugin type · message</th><th>First seen</th><th>Last seen</th><th>Trend</th></tr></thead><tbody>");
                foreach (var g in result.ErrorGroups.Take(options.TopErrorGroups))
                {
                    sb.Append("<tr><td class=\"num\"><b>").Append(g.Count.ToString("N0", CultureInfo.InvariantCulture)).Append("</b><br><span class=\"sub\">")
                      .Append(g.DistinctExecutions.ToString("N0", CultureInfo.InvariantCulture)).Append(" exec.</span></td>");
                    sb.Append("<td><div class=\"err\" title=\"").Append(E(R(g.Headline))).Append("\">").Append(E(R(Formatting.DisplayHeadline(g.Headline)))).Append("</div><div class=\"sub mono\">#").Append(E(g.Fingerprint)).Append("</div>");
                    Flags(sb, g.Flags, E);
                    sb.Append("</td><td>").Append(E(g.TypeName)).Append("<br><span class=\"sub\">").Append(E(g.MessageName)).Append(" · ").Append(E(g.PrimaryEntity)).Append("</span></td>");
                    sb.Append("<td class=\"nowrap\">").Append(E(Formatting.Iso(g.FirstSeen).Replace('T', ' ').Substring(0, 16))).Append("</td>");
                    sb.Append("<td class=\"nowrap\">").Append(E(Formatting.Iso(g.LastSeen).Replace('T', ' ').Substring(0, 16))).Append("</td>");
                    sb.Append("<td>").Append(Sparkline(g.HitsPerDay)).Append("</td></tr>");
                }
                sb.Append("</tbody></table>");
                if (result.ErrorGroups.Count > options.TopErrorGroups)
                    sb.Append($"<p class=\"sub\">{result.ErrorGroups.Count - options.TopErrorGroups:N0} more group(s) not shown; export CSV for the full list.</p>");
            }
            sb.Append("</section>");

            // ---- Slowest plugin types ----
            sb.Append("<section><h2>Slowest plugin types</h2>");
            var perf = result.Performance.Where(p => p.SlowCount > 0 || p.NearTimeoutCount > 0).Take(options.TopPerformance).ToList();
            if (perf.Count == 0) perf = result.Performance.OrderByDescending(p => p.P95Ms).Take(Math.Min(5, options.TopPerformance)).ToList();
            if (perf.Count == 0) sb.Append("<p class=\"empty\">No executions with a duration in this view.</p>");
            else
            {
                sb.Append("<table><thead><tr><th>Plugin type · message</th><th class=\"num\">Runs</th><th class=\"num\">p50</th><th class=\"num\">p95</th><th class=\"num\">Max</th><th class=\"num\">Threshold</th><th class=\"num\">Slow</th><th class=\"num\">Near timeout</th></tr></thead><tbody>");
                foreach (var p in perf)
                {
                    sb.Append("<tr><td>").Append(E(p.TypeName)).Append("<br><span class=\"sub\">").Append(E(p.MessageName)).Append(" · ").Append(E(p.PrimaryEntity))
                      .Append(" · ").Append(E(p.ModeText)).Append(string.IsNullOrEmpty(p.StageText) ? "" : " · " + E(p.StageText)).Append("</span>");
                    Flags(sb, p.Flags, E);
                    sb.Append("</td><td class=\"num\">").Append(p.Count.ToString("N0", CultureInfo.InvariantCulture)).Append("</td>");
                    sb.Append("<td class=\"num\">").Append(E(Formatting.Duration(p.P50Ms))).Append("</td>");
                    sb.Append("<td class=\"num\"><b>").Append(E(Formatting.Duration(p.P95Ms))).Append("</b></td>");
                    sb.Append("<td class=\"num\">").Append(E(Formatting.Duration(p.MaxMs))).Append("</td>");
                    sb.Append("<td class=\"num\">").Append(E(Formatting.Duration(p.ThresholdMs))).Append("</td>");
                    sb.Append("<td class=\"num").Append(p.SlowCount > 0 ? " warnc" : "").Append("\">").Append(p.SlowCount.ToString("N0", CultureInfo.InvariantCulture)).Append("</td>");
                    sb.Append("<td class=\"num").Append(p.NearTimeoutCount > 0 ? " critc" : "").Append("\">").Append(p.NearTimeoutCount.ToString("N0", CultureInfo.InvariantCulture)).Append("</td></tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");

            // ---- Failed chains ----
            sb.Append("<section><h2>Failed chains</h2>");
            var failed = result.Chains.Where(c => c.Failed).OrderByDescending(c => c.Entries.Count).ThenByDescending(c => c.Start).Take(options.TopChains).ToList();
            if (failed.Count == 0) sb.Append("<p class=\"empty\">No failed multi-step chains in this view. Single failed executions are listed under the error groups.</p>");
            var chainNo = 0;
            foreach (var c in failed)
            {
                chainNo++;
                sb.Append("<div class=\"chain\"><div class=\"chainhead\"><b>").Append(options.Redact ? "Chain " + chainNo : E(c.CorrelationId.ToString())).Append("</b> <span class=\"sub\">")
                  .Append(E(Formatting.Iso(c.Start).Replace('T', ' ').Substring(0, 19))).Append(" UTC · ").Append(c.Entries.Count).Append(" step(s) · depth ")
                  .Append(c.MaxDepth).Append(" · span ").Append(E(Formatting.Duration(c.SpanMs))).Append("</span></div>");
                Flags(sb, c.Flags, E);
                sb.Append("<ol class=\"tree\">");
                foreach (var e in c.Entries)
                {
                    sb.Append("<li style=\"margin-left:").Append(Math.Max(0, e.Depth - 1) * 18).Append("px\"").Append(e.IsError ? " class=\"failed\"" : "").Append(">")
                      .Append("<span class=\"depth\">d").Append(e.Depth).Append("</span> ").Append(E(e.ShortTypeName)).Append(" <span class=\"sub\">")
                      .Append(E(e.MessageName)).Append(" · ").Append(E(e.PrimaryEntity)).Append(" · ").Append(E(e.ModeText)).Append(" · ")
                      .Append(E(Formatting.Duration(e.ExecutionDurationMs))).Append("</span>");
                    if (e.IsError) sb.Append("<div class=\"err\">").Append(E(R(Formatting.DisplayHeadline(ExceptionNormalizer.Headline(e.ExceptionDetails))))).Append("</div>");
                    sb.Append("</li>");
                }
                sb.Append("</ol></div>");
            }
            sb.Append("</section>");

            sb.Append("<footer>").Append(options.Redact ? "Redacted report: GUIDs, user names and trace output removed. " : "")
              .Append("Times are UTC. Generated by Dataverse Developer Diagnostics Workbench for XrmToolBox.</footer>");
            sb.Append("</main></body></html>");
            return sb.ToString();
        }

        public static string TraceSettingText(TraceSetting setting)
        {
            switch (setting)
            {
                case TraceSetting.Off: return "Off";
                case TraceSetting.Exception: return "Exception (failures only)";
                case TraceSetting.All: return "All";
                default: return "Unknown";
            }
        }

        public static string Describe(ViewFilter filter)
        {
            if (filter == null || filter.IsEmpty) return "none";
            var parts = new List<string>();
            if (filter.ErrorsOnly) parts.Add("errors only");
            if (filter.SlowOnly) parts.Add("slow only");
            if (!string.IsNullOrWhiteSpace(filter.Search)) parts.Add("search \"" + filter.Search.Trim() + "\"");
            return string.Join(", ", parts);
        }

        /// <summary>Inline SVG bar sparkline of hits per day.</summary>
        public static string Sparkline(SortedDictionary<DateTime, int> hits)
        {
            if (hits == null || hits.Count == 0) return "";
            var first = hits.Keys.First();
            var last = hits.Keys.Last();
            var days = Math.Min(60, (int)(last - first).TotalDays + 1);
            var start = last.AddDays(-(days - 1));
            var max = hits.Values.Max();
            const int w = 4, gap = 1, h = 22;
            var sb = new StringBuilder();
            sb.Append("<svg class=\"spark\" width=\"").Append(days * (w + gap)).Append("\" height=\"").Append(h).Append("\" role=\"img\" aria-label=\"hits per day\">");
            for (var i = 0; i < days; i++)
            {
                hits.TryGetValue(start.AddDays(i), out var n);
                var bh = n == 0 ? 1 : Math.Max(2, (int)Math.Round(n * (double)h / max));
                sb.Append("<rect x=\"").Append(i * (w + gap)).Append("\" y=\"").Append(h - bh).Append("\" width=\"").Append(w).Append("\" height=\"").Append(bh)
                  .Append("\"").Append(n == 0 ? " class=\"zero\"" : "").Append("><title>").Append(start.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                  .Append(": ").Append(n).Append("</title></rect>");
            }
            sb.Append("</svg>");
            return sb.ToString();
        }

        private static void Meta(StringBuilder sb, string term, string html) => sb.Append("<dt>").Append(term).Append("</dt><dd>").Append(html).Append("</dd>");

        private static void Tile(StringBuilder sb, string label, int value, string cls) =>
            sb.Append("<div class=\"tile").Append(cls == null ? "" : " " + cls).Append("\"><div class=\"v\">").Append(value.ToString("N0", CultureInfo.InvariantCulture))
              .Append("</div><div class=\"l\">").Append(label).Append("</div></div>");

        private static void Flags(StringBuilder sb, IEnumerable<ConfigFlag> flags, Func<string, string> e)
        {
            var list = flags?.ToList();
            if (list == null || list.Count == 0) return;
            sb.Append("<ul class=\"flags\">");
            foreach (var f in list) sb.Append("<li title=\"").Append(e(f.Message)).Append("\">⚑ ").Append(e(f.ShortText)).Append(": ").Append(e(f.StepName)).Append("</li>");
            sb.Append("</ul>");
        }

        private const string Css = @"
:root{--bg:#fafaf8;--fg:#1d1f1e;--muted:#5e6b69;--line:#dde3e1;--card:#fff;--brand:#3949ab;--crit:#c62828;--critbg:#fdecea;--warn:#a15c00;--warnbg:#fff4e0;--blue:#1565c0}
@media (prefers-color-scheme:dark){:root{--bg:#161817;--fg:#e8ebea;--muted:#9aa5a3;--line:#2d3331;--card:#1e2120;--brand:#8c9eff;--crit:#ef9a9a;--critbg:#3a1f1f;--warn:#ffcc80;--warnbg:#3a2e1a;--blue:#90caf9}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.5 'Segoe UI',system-ui,-apple-system,sans-serif}
main{max-width:1100px;margin:0 auto;padding:24px 16px 48px}h1{margin:0 0 12px;font-size:26px}h2{font-size:18px;margin:32px 0 12px;padding-bottom:6px;border-bottom:1px solid var(--line)}
.kicker{margin:0;color:var(--brand);font-weight:600;font-size:12px;letter-spacing:.06em;text-transform:uppercase}
.meta{display:grid;grid-template-columns:max-content 1fr;gap:4px 16px;margin:0}.meta dt{color:var(--muted)}.meta dd{margin:0;overflow-wrap:anywhere}
.note{padding:10px 14px;border-radius:8px;margin:16px 0}.note.warn{background:var(--warnbg);color:var(--warn)}.note.crit{background:var(--critbg);color:var(--crit)}
.tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:12px}.tile{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px}
.tile .v{font-size:26px;font-weight:600}.tile .l{color:var(--muted)}.tile.crit{border-left:4px solid var(--crit)}.tile.warn{border-left:4px solid var(--warn)}
table{width:100%;border-collapse:collapse;background:var(--card);border:1px solid var(--line);border-radius:10px;overflow:hidden;display:block;overflow-x:auto}
th,td{padding:8px 10px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}th{font-size:12px;color:var(--muted);font-weight:600;background:var(--bg)}
.num{text-align:right;white-space:nowrap}.nowrap{white-space:nowrap}.sub{color:var(--muted);font-size:12px}.mono{font-family:Consolas,monospace}
.err{color:var(--crit);overflow-wrap:anywhere}.warnc{color:var(--warn);font-weight:600}.critc{color:var(--crit);font-weight:600}
.flags{list-style:none;margin:6px 0 0;padding:0;display:flex;flex-wrap:wrap;gap:4px}.flags li{background:var(--warnbg);color:var(--warn);border-radius:10px;padding:1px 8px;font-size:12px}
.spark rect{fill:var(--crit)}.spark rect.zero{fill:var(--line)}
.chain{background:var(--card);border:1px solid var(--line);border-left:4px solid var(--blue);border-radius:10px;padding:10px 14px;margin:0 0 12px}
.tree{list-style:none;margin:8px 0 0;padding:0}.tree li{padding:3px 0 3px 8px;border-left:2px solid var(--line)}.tree li.failed{border-left-color:var(--crit)}
.depth{display:inline-block;min-width:24px;font-size:11px;color:var(--muted);font-family:Consolas,monospace}.empty{color:var(--muted)}
footer{margin-top:40px;color:var(--muted);font-size:12px}";
    }
}
