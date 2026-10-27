using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Controls;
using PluginFailureCorrelationExplorer.Models;
using PluginFailureCorrelationExplorer.Reporting;

namespace PluginFailureCorrelationExplorer
{
    /// <summary>Binds the analysis result to the views.</summary>
    public partial class PluginFailureCorrelationControl
    {
        private FailureGroup _groupFilter;
        private HashSet<Guid> _traceFilterIds;
        private string _traceFilterLabel;

        private void RefreshAll()
        {
            RefreshHeader();
            RefreshDashboard();
            _groupGrid.SetItems(_result?.FailureGroups);
            ShowGroupDetail(_groupGrid.SelectedItem);
            RefreshTraceList();
            if (_tabs.SelectedTab == _tabReport) RefreshReportPreview();
            SetBusy(_busy);
        }

        private void RefreshHeader()
        {
            if (ConnectionDetail == null)
            {
                _lblOrgName.Text = "Not connected";
                _lblOrgDetails.Text = "Connect to an environment in XrmToolBox to start.";
                _lblTraceSetting.Text = "";
                _lblTraceSetting.Visible = false;
                return;
            }
            _lblOrgName.Text = _org.OrganizationName ?? _org.ConnectionName ?? "Dataverse";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(_org.ConnectionName) && _org.ConnectionName != _org.OrganizationName) parts.Add(_org.ConnectionName);
            if (!string.IsNullOrEmpty(_org.Url)) parts.Add(_org.Url);
            if (!string.IsNullOrEmpty(_org.EnvironmentId)) parts.Add("Environment " + _org.EnvironmentId);
            if (!string.IsNullOrEmpty(_org.OrganizationVersion)) parts.Add("Version " + _org.OrganizationVersion);
            _lblOrgDetails.Text = string.Join("   ·   ", parts);
            _lblTraceSetting.Visible = true;
            _lblTraceSetting.Text = "Plug-in trace log: " + (_org.TraceSetting ?? "unknown");
            _lblTraceSetting.BackColor = _org.TraceSetting == "All" ? Theme.BrandDark : _org.TraceSetting == null ? Theme.Muted : Theme.Warning;
            _lblTraceSetting.Location = new Point(_lblTraceSetting.Parent.Width - _lblTraceSetting.Width - 16, 15);
        }

        private void RefreshDashboard()
        {
            var r = _result;
            if (r == null)
            {
                foreach (var tile in new[] { _kpiTraces, _kpiExceptions, _kpiGroups, _kpiSlow, _kpiFindings, _kpiEvidence }) tile.Set("–");
                _findingGrid.SetItems(null);
                _recurringGrid.SetItems(null);
                ShowFindingDetail(null);
                _lblTrend.Text = "Choose a date range and filters, then press Analyze (F5).";
                SetBanner(TraceSettingBanner());
                return;
            }

            var s = r.Summary;
            _kpiTraces.Set(Fmt.Count(s.TotalTraces), s.Query.LimitReached ? "limit reached: newest only" : Fmt.Utc(s.Query.FromUtc).Replace(" UTC", "") + " →");
            _kpiExceptions.Set(Fmt.Count(s.TracesWithException), Fmt.Percent(s.TracesWithException, s.TotalTraces) + " of traces");
            _kpiGroups.Set(Fmt.Count(s.UniqueFailureGroups), Fmt.Count(s.RecurringFailureGroups) + " recurring");
            _kpiSlow.Set(Fmt.Count(s.SlowExecutions), $"≥ {r.Options.SlowSyncThresholdMs:N0} / {r.Options.SlowAsyncThresholdMs:N0} ms (sync/async)");
            var errors = r.Findings.Count(f => f.Severity == Severity.Error);
            _kpiFindings.Set(Fmt.Count(r.Findings.Count), Fmt.Count(errors) + " errors", errors > 0 ? Theme.Error : Theme.Info);
            _kpiEvidence.Set(Fmt.Percent(s.CompleteEvidence, s.TotalTraces), Fmt.Count(s.IncompleteEvidence) + " traces missing identifiers",
                s.TotalTraces > 0 && s.IncompleteEvidence * 4 > s.TotalTraces ? Theme.Warning : Theme.Ok);

            _findingGrid.SetItems(r.Findings);
            ShowFindingDetail(_findingGrid.SelectedItem);
            _recurringGrid.SetItems(r.FailureGroups.Where(g => g.Occurrences >= r.Options.RecurringMinOccurrences).Take(25));
            _recurringGrid.EmptyText = r.FailureGroups.Count == 0 ? "No failed traces." : $"No failure occurred {r.Options.RecurringMinOccurrences} or more times.";

            _lblTrend.Text = r.Trend.Available
                ? "Trend: compared with " + r.Trend.BaselineDescription + ". " +
                  $"{r.Trend.Failures.Count(t => t.Status == TrendStatus.New)} new, {r.Trend.Failures.Count(t => t.Status == TrendStatus.Increased)} increasing failure patterns; " +
                  $"{r.Trend.Performance.Count(t => t.IsRegression)} possible regressions."
                : "Trend analysis unavailable: " + r.Trend.UnavailableReason;
            _lblTrend.ForeColor = r.Trend.Available ? Theme.Ink : Theme.Muted;

            var notes = new List<string>();
            var banner = TraceSettingBanner();
            if (banner != null) notes.Add(banner);
            notes.AddRange(s.RetrievalNotes);
            notes.AddRange(r.InvalidRules.Select(p => $"Custom rule \"{p.Key}\" was skipped: {p.Value}"));
            if (s.TotalTraces == 0) notes.Add("No traces matched. Check the date range and filters, and that plug-in trace logging is enabled. Dataverse deletes trace logs after a retention period.");
            SetBanner(notes.Count == 0 ? null : string.Join("   •   ", notes));
        }

        private string TraceSettingBanner()
        {
            if (_org.TraceSetting == "Off") return "Plug-in trace logging is Off: no new traces are written.";
            if (_org.TraceSetting == "Exception") return "Plug-in trace logging is set to Exception: successful executions aren't logged, so performance data covers failures only.";
            return null;
        }

        private void SetBanner(string text)
        {
            _lblBanner.Text = text ?? "";
            _lblBanner.Height = string.IsNullOrEmpty(text) ? 0 : 34;
            _lblBanner.BackColor = Theme.WarningTint;
            _lblBanner.ForeColor = Color.FromArgb(0x7A, 0x45, 0x00);
        }

        // ================================================================ findings and groups

        private void ShowFindingDetail(DiagnosticFinding f)
        {
            var w = new DetailWriter(_findingDetail);
            if (f == null)
            {
                w.Muted(_result == null ? "Findings appear here after an analysis." : "Select a finding to see its evidence.");
                w.Done();
                return;
            }
            w.Title($"{f.Id} · {f.Title}", Theme.For(f.Severity));
            w.Muted($"{f.Severity} · confidence {f.Confidence} · rule {f.RuleId}");
            w.Paragraph(f.Description);
            w.Items("Observed facts", f.Facts, Theme.Ok);
            w.Items("Calculated metrics", f.Metrics, Theme.Info);
            w.Items("Correlation evidence", f.Correlations, Color.FromArgb(0x7A, 0x4B, 0xB3));
            w.Items("Possible explanations (hypotheses, not confirmed causes)", f.PossibleExplanations, Theme.Warning);
            w.Items("Recommended investigation steps", f.Recommendations, Theme.Brand, numbered: true);
            if (f.TraceIds.Count > 0)
                w.Muted($"{Fmt.Plural(f.TraceIds.Count + f.AdditionalTraceCount, "supporting trace")}. Double-click the finding to open them in the Trace explorer.");
            w.Done();
        }

        private void ShowGroupDetail(FailureGroup g)
        {
            var w = new DetailWriter(_groupDetail);
            if (g == null)
            {
                w.Muted(_result == null ? "Failure groups appear here after an analysis." : "Select a failure group.");
                w.Done();
                return;
            }
            var fp = g.Fingerprint;
            w.Title($"{fp.Label} · {g.Occurrences:N0} occurrences", Theme.For(g.Severity));
            w.Field("Fingerprint", g.Key);
            w.Field("Exception type", fp.ExceptionType + (string.IsNullOrEmpty(fp.ErrorCode) ? "" : "  (error code " + fp.ErrorCode + ")"));
            w.Field("First / latest", Fmt.Utc(g.FirstUtc) + "  →  " + Fmt.Utc(g.LastUtc));
            w.Field("Plug-in types", Fmt.List(g.PluginTypes, 10));
            w.Field("Messages / tables", Fmt.List(g.Messages) + "  /  " + Fmt.List(g.PrimaryEntities));
            w.Field("Distinct requests / chains", $"{g.DistinctRequests:N0} / {g.DistinctCorrelations:N0}");
            w.Field("Rate", Fmt.Rate(g.RatePerHour) + " over the active span");
            w.Heading("Original message (latest occurrence)");
            w.Mono(fp.Message);
            w.Heading("Normalized message");
            w.Mono(fp.NormalizedMessage);
            if (fp.TopFrames.Count > 0) { w.Heading("Top user stack frames"); w.Mono(string.Join(Environment.NewLine, fp.TopFrames)); }
            w.Heading("Why these traces are grouped");
            w.Paragraph("Normalization rules applied: " + (fp.RulesApplied.Count == 0 ? "none" : string.Join(", ", fp.RulesApplied)) +
                        ". Grouping mode: " + _options.Grouping + ". The fingerprint is a hash of:");
            w.Mono(fp.FingerprintInput);
            w.Muted("Double-click the group (or press Enter) to inspect its traces.");
            w.Done();
        }

        private void OpenGroupTraces(FailureGroup g)
        {
            if (g == null) return;
            _groupFilter = g;
            _traceFilterIds = null;
            _traceFilterLabel = null;
            _chkErrorsOnly.Checked = false;
            _chkSlowOnly.Checked = false;
            _txtSearch.Text = "";
            RefreshTraceList();
            _tabs.SelectedTab = _tabTraces;
            _traceGrid.Focus();
        }

        private void OpenFindingTraces(DiagnosticFinding f)
        {
            if (f == null || f.TraceIds.Count == 0 || _result == null) return;
            var group = f.RelatedKey == null ? null : _result.FailureGroups.FirstOrDefault(g => g.Key == f.RelatedKey);
            if (group != null && f.AdditionalTraceCount > 0) { OpenGroupTraces(group); return; }
            _groupFilter = null;
            _traceFilterIds = new HashSet<Guid>(f.TraceIds);
            _traceFilterLabel = $"Finding {f.Id} ({f.TraceIds.Count:N0} traces)";
            _chkErrorsOnly.Checked = false;
            _chkSlowOnly.Checked = false;
            _txtSearch.Text = "";
            RefreshTraceList();
            _tabs.SelectedTab = _tabTraces;
        }

        // ================================================================ trace explorer

        private bool IsSlow(PluginTraceRecord t) =>
            t.ExecutionDurationMs.HasValue && t.Mode.HasValue && t.ExecutionDurationMs.Value >= _options.ThresholdFor(t.Mode);

        private void RefreshTraceList()
        {
            IEnumerable<PluginTraceRecord> items = _result?.Records ?? new List<PluginTraceRecord>();
            if (_groupFilter != null) items = _groupFilter.Traces;
            else if (_traceFilterIds != null) items = items.Where(t => _traceFilterIds.Contains(t.Id));
            if (_chkErrorsOnly.Checked) items = items.Where(t => t.HasException);
            if (_chkSlowOnly.Checked) items = items.Where(IsSlow);
            var search = _txtSearch.Text.Trim();
            if (search.Length > 0) items = items.Where(t => Matches(t, search));

            var list = items.ToList();
            _traceGrid.SetItems(list);
            var filtered = _groupFilter != null || _traceFilterIds != null;
            _lblGroupFilter.Visible = _btnClearGroup.Visible = filtered;
            _lblGroupFilter.Text = _groupFilter != null ? $"Failure group {_groupFilter.Key}" : _traceFilterLabel ?? "";
            _lblTraceCount.Text = _result == null ? "" : $"{list.Count:N0} of {_result.Records.Count:N0} traces";
            _traceGrid.EmptyText = _result == null ? "Run an analysis to explore traces." : "No trace matches the current filters.";
            if (_traceGrid.SelectedItem == null) ShowTraceDetail(null);
        }

        private bool Matches(PluginTraceRecord t, string search)
        {
            bool Has(string s) => s != null && s.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
            return Has(t.TypeName) || Has(t.MessageName) || Has(t.PrimaryEntity) || Has(t.ExceptionDetails) || Has(t.MessageBlock)
                   || Has(t.Id.ToString()) || Has(t.CorrelationId?.ToString()) || Has(t.RequestId?.ToString()) || Has(t.PluginStepId?.ToString())
                   || Has(t.Step?.StepName) || Has(t.AssemblyName);
        }

        private void ShowTraceDetail(PluginTraceRecord t)
        {
            var w = new DetailWriter(_traceDetail);
            if (t == null)
            {
                _lblDetailTitle.Text = "Select a trace";
                w.Muted(_result == null ? "Traces appear here after an analysis." : "Select a trace to see its details and related traces.");
                w.Done();
                _relatedGrid.SetItems(null);
                _btnPrevInChain.Enabled = _btnNextInChain.Enabled = false;
                return;
            }

            _lblDetailTitle.Text = (t.HasException ? "✖ " : "") + t.ShortTypeName + " · " + (t.MessageName ?? "?") + (string.IsNullOrEmpty(t.PrimaryEntity) ? "" : " of " + t.PrimaryEntity);
            _lblDetailTitle.ForeColor = t.HasException ? Theme.Error : Theme.Brand;

            w.Heading("Execution");
            w.Field("Trace ID", t.Id.ToString());
            w.Field("Created", Fmt.Utc(t.CreatedOnUtc) + "  (local " + t.CreatedOnUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) + ")");
            w.Field("Plug-in type", t.FullTypeName ?? "(not recorded)");
            w.Field("Assembly", t.Step?.AssemblyName == null ? t.AssemblyName ?? "(not resolved)" : t.Step.AssemblyName + (string.IsNullOrEmpty(t.Step.AssemblyVersion) ? "" : " " + t.Step.AssemblyVersion));
            if (t.Step != null) w.Field("Step", (t.Step.StepName ?? "") + (t.Step.Stage.HasValue ? " · " + t.Step.StageText : "") + (t.Step.Rank.HasValue ? " · rank " + t.Step.Rank : "") + (t.Step.State == 1 ? " · DISABLED" : ""));
            w.Field("Message / table", (t.MessageName ?? "?") + " / " + (string.IsNullOrEmpty(t.PrimaryEntity) ? "(none)" : t.PrimaryEntity));
            w.Field("Mode / depth", t.ModeText + " / " + (t.Depth?.ToString() ?? "?") + "   ·   " + t.OperationTypeText);
            var slow = IsSlow(t);
            w.Field("Duration", t.ExecutionDurationMs.HasValue
                ? $"{t.ExecutionDurationMs.Value:N0} ms" + (slow ? $"  (at or above the {t.ModeText.ToLowerInvariant()} threshold of {_options.ThresholdFor(t.Mode):N0} ms)" : "")
                  + (t.ConstructorDurationMs.HasValue ? $"   ·   constructor {t.ConstructorDurationMs.Value:N0} ms" : "")
                : "(not recorded)", slow ? Theme.Warning : (Color?)null);
            w.Field("Correlation ID", t.CorrelationId?.ToString() ?? "(none)");
            w.Field("Request ID", t.RequestId?.ToString() ?? "(none)");
            w.Field("Step ID", t.PluginStepId?.ToString() ?? "(none)");
            w.Field("Organization", (_org.OrganizationName ?? _org.ConnectionName ?? "") + (string.IsNullOrEmpty(_org.EnvironmentId) ? "" : " · environment " + _org.EnvironmentId));
            if (!t.HasCompleteEvidence) w.Field("Missing identifiers", string.Join(", ", t.MissingIdentifiers), Theme.Warning);

            if (_result != null && _result.Fingerprints.TryGetValue(t.Id, out var fp))
            {
                var group = _result.GroupFor(t);
                w.Heading("Failure fingerprint");
                w.Field("Fingerprint", (group?.Key ?? fp.Value) + (group != null ? $"  ({group.Occurrences:N0} occurrences in this analysis)" : ""));
                w.Field("Exception", fp.ExceptionType + (string.IsNullOrEmpty(fp.ErrorCode) ? "" : "  (error code " + fp.ErrorCode + ")"));
            }

            w.Heading("Exception details");
            if (!t.ExceptionLoaded) w.Muted("Not loaded yet.");
            else if (!t.HasException) w.Muted("No exception was recorded.");
            else w.Mono(t.ExceptionDetails, Theme.Error);

            w.Heading("Trace output (message block)");
            if (!t.MessageBlockLoaded) w.Muted("Loading…");
            else if (string.IsNullOrWhiteSpace(t.MessageBlock)) w.Muted("No trace output was written.");
            else w.Mono(t.MessageBlock);
            w.Done();

            var related = _correlation?.FindRelated(t) ?? new List<RelatedTrace>();
            _relatedGrid.SetItems(related);
            _lblRelatedHint.Text = related.Count == 0
                ? (t.CorrelationId.HasValue ? "No other retrieved trace shares this trace's identifiers, context or fingerprint." : "This trace has no correlation ID, so only heuristic and same-pattern links are possible.")
                : "Select a related trace to see why it is linked. Double-click to open it.";

            var cluster = _correlation?.ClusterFor(t);
            _btnPrevInChain.Enabled = _btnNextInChain.Enabled = cluster != null;
            QueueDetailLoad(t);
        }

        private void OpenRelated(RelatedTrace related)
        {
            if (related == null) return;
            if (!_traceGrid.Select(related.Trace))
            {
                // Not in the current filter: show everything, then select it.
                _groupFilter = null;
                _traceFilterIds = null;
                _chkErrorsOnly.Checked = _chkSlowOnly.Checked = false;
                _txtSearch.Text = "";
                RefreshTraceList();
                _traceGrid.Select(related.Trace);
            }
        }

        private void MoveInChain(int direction)
        {
            var t = _traceGrid.SelectedItem;
            var cluster = _correlation?.ClusterFor(t);
            if (cluster == null) return;
            var index = cluster.Traces.IndexOf(t);
            var next = index + direction;
            if (next < 0 || next >= cluster.Traces.Count) { Status("Start or end of the causal chain"); return; }
            OpenRelated(new RelatedTrace { Trace = cluster.Traces[next] });
            Status($"Trace {next + 1} of {cluster.Traces.Count} in correlation {cluster.CorrelationId}");
        }

        private int _findFrom;

        private void FindInDetail()
        {
            var text = _txtFind.Text;
            if (string.IsNullOrEmpty(text)) return;
            var at = _traceDetail.Find(text, Math.Min(_findFrom, _traceDetail.TextLength), RichTextBoxFinds.None);
            if (at < 0 && _findFrom > 0) at = _traceDetail.Find(text, 0, RichTextBoxFinds.None);
            if (at < 0) { Status($"\"{text}\" not found"); _findFrom = 0; return; }
            _findFrom = at + text.Length;
            _traceDetail.ScrollToCaret();
            _traceDetail.Focus();
        }

        /// <summary>Plain-text summary for tickets. Credentials are scrubbed; trace output is not included.</summary>
        private string DiagnosticSummary(PluginTraceRecord t)
        {
            if (t == null) return null;
            var r = new SensitiveDataRedactor(RedactionOptions.None);
            var sb = new StringBuilder();
            sb.AppendLine("Plug-in trace " + t.Id);
            sb.AppendLine("Organization: " + (_org.OrganizationName ?? _org.ConnectionName) + (string.IsNullOrEmpty(_org.Url) ? "" : " (" + _org.Url + ")"));
            sb.AppendLine("Created (UTC): " + Fmt.UtcIso(t.CreatedOnUtc));
            sb.AppendLine("Plug-in type: " + (t.FullTypeName ?? "(not recorded)") + (string.IsNullOrEmpty(t.AssemblyName) ? "" : " [" + t.AssemblyName + (t.Step?.AssemblyVersion == null ? "" : " " + t.Step.AssemblyVersion) + "]"));
            if (t.Step != null) sb.AppendLine("Step: " + t.Step.StepName + " (" + t.Step.StageText + ")");
            sb.AppendLine($"Message / table: {t.MessageName} / {t.PrimaryEntity}");
            sb.AppendLine($"Mode / depth: {t.ModeText} / {t.Depth?.ToString() ?? "?"}");
            sb.AppendLine("Duration: " + (t.ExecutionDurationMs.HasValue ? t.ExecutionDurationMs.Value.ToString("N0", CultureInfo.InvariantCulture) + " ms" : "(not recorded)"));
            sb.AppendLine("Correlation ID: " + (t.CorrelationId?.ToString() ?? "(none)"));
            sb.AppendLine("Request ID: " + (t.RequestId?.ToString() ?? "(none)"));
            if (_result != null && _result.Fingerprints.TryGetValue(t.Id, out var fp))
            {
                var group = _result.GroupFor(t);
                sb.AppendLine("Fingerprint: " + (group?.Key ?? fp.Value) + (group != null ? $" ({group.Occurrences} occurrences, {Fmt.Utc(group.FirstUtc)} → {Fmt.Utc(group.LastUtc)})" : ""));
                sb.AppendLine("Exception: " + fp.ExceptionType + (string.IsNullOrEmpty(fp.ErrorCode) ? "" : " (error code " + fp.ErrorCode + ")"));
                sb.AppendLine("Message: " + r.Redact(fp.Message));
            }
            return sb.ToString();
        }

        // ================================================================ report

        private void RefreshReportPreview()
        {
            if (_result == null)
            {
                _lblReportHint.Text = "Run an analysis to build the report.";
                _reportBrowser.DocumentText = "<html><body style=\"font-family:Segoe UI;color:#5b6662;padding:24px\">Run an analysis to build the diagnostic report.</body></html>";
                return;
            }
            var options = ReportOptionsFor(_tsRedact.Checked);
            _lblReportHint.Text = options.IsRedacted
                ? "Preview is redacted (GUIDs, e-mail addresses, URL query strings). Credentials are always removed."
                : "Preview is not redacted. Credentials are always removed.";
            _reportBrowser.DocumentText = HtmlReportWriter.Write(_result, options);
        }

        /// <summary>Writes formatted text into a RichTextBox.</summary>
        private sealed class DetailWriter
        {
            private readonly RichTextBox _box;

            public DetailWriter(RichTextBox box)
            {
                _box = box;
                _box.SuspendLayout();
                _box.Clear();
            }

            public void Title(string text, Color color) => Append(text + Environment.NewLine, Theme.Heading, color);
            public void Heading(string text) => Append(Environment.NewLine + text + Environment.NewLine, Theme.Bold, Theme.BrandDark);
            public void Paragraph(string text) => Append((text ?? "") + Environment.NewLine, Theme.Base, Theme.Ink);
            public void Muted(string text) => Append((text ?? "") + Environment.NewLine, Theme.Base, Theme.Muted);
            public void Mono(string text, Color? color = null) => Append((text ?? "") + Environment.NewLine, Theme.Mono, color ?? Theme.Ink);

            public void Field(string label, string value, Color? color = null)
            {
                Append(label.PadRight(22), Theme.Base, Theme.Muted);
                Append((value ?? "") + Environment.NewLine, Theme.Base, color ?? Theme.Ink);
            }

            public void Items(string heading, IReadOnlyCollection<string> items, Color color, bool numbered = false)
            {
                if (items == null || items.Count == 0) return;
                Append(Environment.NewLine + heading + Environment.NewLine, Theme.Bold, color);
                var i = 0;
                foreach (var item in items)
                    Append((numbered ? $"{++i}. " : "• ") + item + Environment.NewLine, Theme.Base, Theme.Ink);
            }

            public void Done()
            {
                _box.SelectionStart = 0;
                _box.SelectionLength = 0;
                _box.ScrollToCaret();
                _box.ResumeLayout();
            }

            private void Append(string text, Font font, Color color)
            {
                _box.SelectionStart = _box.TextLength;
                _box.SelectionLength = 0;
                _box.SelectionFont = font;
                _box.SelectionColor = color;
                _box.AppendText(text);
            }
        }
    }
}
