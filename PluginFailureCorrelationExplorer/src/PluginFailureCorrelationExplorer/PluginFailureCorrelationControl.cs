using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Controls;
using PluginFailureCorrelationExplorer.Data;
using PluginFailureCorrelationExplorer.Models;
using PluginFailureCorrelationExplorer.Persistence;
using PluginFailureCorrelationExplorer.Reporting;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Args;
using XrmToolBox.Extensibility.Interfaces;

namespace PluginFailureCorrelationExplorer
{
    /// <summary>
    /// Main control. Retrieves plug-in trace logs through the active XrmToolBox connection (read-only), analyzes them in
    /// the background, and shows the dashboard, failure groups, trace explorer and diagnostic report.
    /// </summary>
    public partial class PluginFailureCorrelationControl : PluginControlBase, IGitHubPlugin, IHelpPlugin, IStatusBarMessenger
    {
        public string RepositoryName => "XrmTools";
        public string UserName => "vikashkumar669";
        public string HelpUrl => "https://github.com/vikashkumar669/XrmTools/tree/main/PluginFailureCorrelationExplorer#readme";

        public event EventHandler<StatusBarMessageEventArgs> SendMessageToStatusBar;

        internal const string AppName = "Plugin Failure Correlation Explorer";

        private AnalysisOptions _options = new AnalysisOptions();
        private AnalysisResult _result;
        private FailureCorrelationService _correlation;
        private OrganizationInfo _org = new OrganizationInfo();
        private readonly Dictionary<Guid, StepRegistration> _stepCache = new Dictionary<Guid, StepRegistration>();
        private BaselineStore _baselineStore;
        private List<BaselineSnapshot> _snapshots = new List<BaselineSnapshot>();
        private string _groupingSignature;
        private bool _busy;

        public PluginFailureCorrelationControl()
        {
            BuildUi();
            ApplyRangePreset();
            RefreshAll();
        }

        // ================================================================ connection & settings

        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            base.UpdateConnection(newService, detail, actionName, parameter);
            _options = LoadSettings();
            _result = null;
            _correlation = null;
            _stepCache.Clear();
            _org = OrganizationFrom(detail);
            _baselineStore = new BaselineStore(SettingsKey);
            ReloadSnapshots();
            _numLimit.Value = Math.Max(_numLimit.Minimum, Math.Min(_numLimit.Maximum, _options.LastResultLimit));
            SelectRangePreset(_options.LastRangeHours);
            RefreshAll();
            if (newService != null) ReadOrganizationAsync();
        }

        private string SettingsKey => ConnectionDetail?.ConnectionId?.ToString() ?? "default";

        private static OrganizationInfo OrganizationFrom(ConnectionDetail detail)
        {
            var info = new OrganizationInfo();
            if (detail == null) return info;
            info.ConnectionName = detail.ConnectionName;
            try { info.OrganizationName = detail.OrganizationFriendlyName; } catch { /* not available on every connection type */ }
            try { info.Url = detail.WebApplicationUrl ?? detail.OriginalUrl; } catch { }
            try { info.EnvironmentId = detail.EnvironmentId?.ToString(); } catch { }
            try { info.OrganizationVersion = detail.OrganizationVersion; } catch { }
            if (string.IsNullOrWhiteSpace(info.EnvironmentId)) info.EnvironmentId = null;
            return info;
        }

        private AnalysisOptions LoadSettings()
        {
            AnalysisOptions settings = null;
            try
            {
                if (!SettingsManager.Instance.TryLoad(typeof(PluginFailureCorrelationPlugin), out settings, SettingsKey)) settings = null;
            }
            catch (Exception ex)
            {
                LogWarning("Couldn't read saved settings: {0}", ex.Message);
            }
            settings = settings ?? new AnalysisOptions();
            settings.Normalize();
            return settings;
        }

        private void SaveSettings()
        {
            try { SettingsManager.Instance.Save(typeof(PluginFailureCorrelationPlugin), _options, SettingsKey); }
            catch (Exception ex) { LogWarning("Couldn't save settings: {0}", ex.Message); }
        }

        private void EditSettings()
        {
            var edited = SettingsDialog.Edit(this, _options, ConnectionDetail?.ConnectionName);
            if (edited == null) return;
            var regroup = new ExceptionFingerprintService(edited).GroupingSignature != new ExceptionFingerprintService(_options).GroupingSignature;
            edited.LastRangeHours = _options.LastRangeHours;
            edited.LastResultLimit = _options.LastResultLimit;
            _options = edited;
            SaveSettings();
            Status("Settings saved" + (ConnectionDetail == null ? "" : " for " + ConnectionDetail.ConnectionName));
            if (_result != null)
            {
                Reanalyze(regroup
                    ? "Settings changed how failures are grouped; saved baselines from earlier settings can no longer be compared."
                    : null);
            }
        }

        private void ReloadSnapshots()
        {
            _snapshots = _baselineStore?.Load() ?? new List<BaselineSnapshot>();
            var selected = _cboBaseline.SelectedIndex;
            _cboBaseline.Items.Clear();
            _cboBaseline.Items.Add("No baseline");
            _cboBaseline.Items.Add("Previous period (same length)");
            foreach (var s in _snapshots)
                _cboBaseline.Items.Add(string.Format(CultureInfo.InvariantCulture, "Snapshot {0:yyyy-MM-dd HH:mm} UTC ({1:N0} traces, {2:g0} h)", s.CapturedUtc, s.TotalTraces, Math.Round(s.Hours)));
            _cboBaseline.SelectedIndex = selected >= 0 && selected < _cboBaseline.Items.Count ? selected : 0;
        }

        private void ReadOrganizationAsync()
        {
            var service = Service;
            var known = _org;
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Reading organization details...",
                Work = (worker, args) => args.Result = new DataverseTraceService(service).ReadOrganization(known),
                PostWorkCallBack = args =>
                {
                    if (args.Error != null) { LogWarning("Couldn't read organization details: {0}", args.Error.Message); return; }
                    _org = (OrganizationInfo)args.Result;
                    RefreshHeader();
                    RefreshDashboard();
                }
            });
        }

        // ================================================================ analysis

        private sealed class WorkOutput
        {
            public AnalysisResult Result;
            public FailureCorrelationService Correlation;
            public string GroupingSignature;
            public OrganizationInfo Organization;
        }

        private void Analyze()
        {
            if (Service == null || _busy) return;
            var query = BuildQuery(out var problems);
            if (problems.Count > 0)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, problems), AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _options.LastRangeHours = RangeHours();
            _options.LastResultLimit = query.ResultLimit;
            SaveSettings();

            var options = _options.Clone();
            var baselineIndex = _cboBaseline.SelectedIndex;
            var savedBaseline = baselineIndex >= 2 && baselineIndex - 2 < _snapshots.Count ? _snapshots[baselineIndex - 2] : null;
            var service = Service;
            var org = _org;
            var cache = _stepCache;
            SetBusy(true);

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Retrieving plug-in trace logs...",
                IsCancelable = true,
                Work = (worker, args) =>
                {
                    var data = new DataverseTraceService(service, () => worker.CancellationPending, p => worker.ReportProgress(0, p), cache);
                    var orgInfo = data.ReadOrganization(org);
                    var current = data.Retrieve(query, resolveSteps: true);
                    var notes = new List<string>(current.Notes);
                    var engine = new AnalysisEngine(options);

                    BaselineSnapshot baseline = savedBaseline;
                    if (baseline != null) baseline.Source = BaselineService.SourceSnapshot;
                    if (baselineIndex == 1 && !current.Cancelled)
                    {
                        var length = query.ToUtc - query.FromUtc;
                        var previous = query.Clone();
                        previous.FromUtc = query.FromUtc - length;
                        previous.ToUtc = query.FromUtc.AddSeconds(-1);
                        worker.ReportProgress(0, new RetrievalProgress { Stage = "Retrieving the previous period" });
                        var before = data.Retrieve(previous, resolveSteps: false);
                        if (before.Cancelled) notes.Add("Retrieving the previous period was cancelled, so no trend comparison was made.");
                        else
                        {
                            var baseResult = new AnalysisEngine(options).Analyze(before.Records, previous, orgInfo);
                            baseline = BaselineService.CreateSnapshot(baseResult, engine.GroupingSignature, BaselineService.SourcePreviousPeriod);
                        }
                    }

                    worker.ReportProgress(0, new RetrievalProgress { Stage = "Analyzing", Count = current.Records.Count });
                    var result = engine.Analyze(current.Records, query, orgInfo, baseline, DateTime.UtcNow, notes);
                    args.Result = new WorkOutput { Result = result, Correlation = engine.Correlation, GroupingSignature = engine.GroupingSignature, Organization = orgInfo };
                },
                ProgressChanged = e =>
                {
                    if (e.UserState is RetrievalProgress p)
                        SetWorkingMessage(p.Count > 0 ? $"{p.Stage}: {p.Count:N0}" : p.Stage + "...");
                },
                PostWorkCallBack = args =>
                {
                    SetBusy(false);
                    if (args.Error != null)
                    {
                        var message = DataverseErrors.Describe(args.Error);
                        LogError("Analysis failed: {0}", args.Error.ToString());
                        MessageBox.Show(this, message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Status(message);
                        return;
                    }
                    var output = (WorkOutput)args.Result;
                    _org = output.Organization;
                    Apply(output.Result, output.Correlation, output.GroupingSignature);
                    var s = output.Result.Summary;
                    LogInfo("Analyzed {0} traces ({1} with exception) from {2} to {3}; filters: {4}; findings: {5}",
                        s.TotalTraces, s.TracesWithException, Fmt.UtcIso(s.Query.FromUtc), Fmt.UtcIso(s.Query.ToUtc), s.Query.Describe(), output.Result.Findings.Count);
                    Status($"{s.TotalTraces:N0} traces analyzed, {output.Result.Findings.Count:N0} findings");

                    if (_options.SaveBaselineSnapshots && !s.Query.LimitReached && s.TotalTraces > 0 && _baselineStore != null)
                    {
                        try
                        {
                            _baselineStore.Add(BaselineService.CreateSnapshot(output.Result, output.GroupingSignature, BaselineService.SourceSnapshot));
                            ReloadSnapshots();
                        }
                        catch (Exception ex) { LogWarning("Couldn't save the baseline snapshot: {0}", ex.Message); }
                    }
                }
            });
        }

        /// <summary>Re-runs the analysis on the traces already retrieved (after a settings change). No Dataverse calls.</summary>
        private void Reanalyze(string note)
        {
            if (_result == null) return;
            var engine = new AnalysisEngine(_options);
            var notes = _result.Summary.RetrievalNotes.ToList();
            if (note != null) notes.Add(note);
            var result = engine.Analyze(_result.Records, _result.Summary.Query, _org, null, _result.Summary.AnalyzedUtc, notes);
            if (_result.Trend.Available)
                result.Summary.RetrievalNotes.Add("The trend comparison was cleared because the settings changed. Run the analysis again to compare.");
            Apply(result, engine.Correlation, engine.GroupingSignature);
        }

        private void Apply(AnalysisResult result, FailureCorrelationService correlation, string signature)
        {
            _result = result;
            _correlation = correlation;
            _groupingSignature = signature;
            _groupFilter = null;
            RefreshAll();
            _tabs.SelectedTab = _tabDashboard;
        }

        private TraceQuery BuildQuery(out List<string> problems)
        {
            problems = new List<string>();
            var (from, to) = CurrentRange();
            var q = new TraceQuery
            {
                FromUtc = from,
                ToUtc = to,
                Exceptions = (ExceptionFilter)Math.Max(0, _cboExceptions.SelectedIndex),
                MessageName = Clean(_cboMessage.Text),
                PrimaryEntity = Clean(_txtTable.Text)?.ToLowerInvariant(),
                PluginType = Clean(_txtType.Text),
                MinDurationMs = _numMinDuration.Value > 0 ? (int)_numMinDuration.Value : (int?)null,
                ResultLimit = (int)_numLimit.Value,
            };
            q.CorrelationId = ParseGuid(_txtCorrelation.Text, "Correlation ID", problems);
            q.RequestId = ParseGuid(_txtRequest.Text, "Request ID", problems);
            problems.AddRange(q.Validate());
            return q;
        }

        private static Guid? ParseGuid(string text, string label, List<string> problems)
        {
            var t = Clean(text);
            if (t == null) return null;
            if (Guid.TryParse(t, out var g)) return g;
            problems.Add(label + " isn't a valid GUID.");
            return null;
        }

        private static string Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _btnAnalyze.Enabled = _tsAnalyze.Enabled = !busy && Service != null;
            _btnCancel.Enabled = _tsCancel.Enabled = busy;
        }

        private void Cancel()
        {
            if (!_busy) return;
            CancelWorker();
            Status("Cancelling after the current page...");
        }

        // ================================================================ trace detail

        private Timer _detailTimer;

        /// <summary>Loads the trace text for the selected trace after a short pause (so arrowing through rows stays fast).</summary>
        private void QueueDetailLoad(PluginTraceRecord record)
        {
            if (record == null || record.MessageBlockLoaded || Service == null) return;
            if (_detailTimer == null)
            {
                _detailTimer = new Timer { Interval = 350 };
                _detailTimer.Tick += (s, e) =>
                {
                    _detailTimer.Stop();
                    var target = _traceGrid.SelectedItem;
                    if (target == null || target.MessageBlockLoaded || _busy) return;
                    LoadDetail(target);
                };
            }
            _detailTimer.Stop();
            _detailTimer.Start();
        }

        private void LoadDetail(PluginTraceRecord record)
        {
            var service = Service;
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading trace details...",
                Work = (worker, args) => new DataverseTraceService(service).LoadDetail(record),
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        record.MessageBlockLoaded = true;   // don't retry in a loop
                        record.MessageBlock = "(The trace text couldn't be loaded: " + DataverseErrors.Describe(args.Error) + ")";
                    }
                    if (_traceGrid.SelectedItem == record) ShowTraceDetail(record);
                }
            });
        }

        // ================================================================ export

        private ReportOptions ReportOptionsFor(bool redact)
        {
            var version = typeof(PluginFailureCorrelationControl).Assembly.GetName().Version;
            return new ReportOptions
            {
                ToolVersion = AppName + " " + version,
                Redaction = redact
                    ? new RedactionOptions { CustomValues = _options.SensitiveValues.ToList() }
                    : new RedactionOptions { Guids = false, Emails = false, UrlQueries = false, CustomValues = _options.SensitiveValues.ToList() },
            };
        }

        private void Export(string kind)
        {
            if (_result == null)
            {
                MessageBox.Show(this, "Run an analysis first.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var options = ReportOptionsFor(_tsRedact.Checked);
            string content, filter, name;
            Encoding encoding = new UTF8Encoding(false);
            var stamp = _result.Summary.AnalyzedUtc.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            var org = SafeFileName(_org.OrganizationName ?? _org.ConnectionName ?? "dataverse");
            switch (kind)
            {
                case "html": content = HtmlReportWriter.Write(_result, options); filter = "HTML report (*.html)|*.html"; name = $"plugin-failure-report-{org}-{stamp}.html"; break;
                case "json": content = JsonReportWriter.Write(_result, options); filter = "JSON report (*.json)|*.json"; name = $"plugin-failure-report-{org}-{stamp}.json"; break;
                case "findings": content = CsvReportWriter.Findings(_result, options); filter = "CSV (*.csv)|*.csv"; name = $"findings-{org}-{stamp}.csv"; encoding = CsvReportWriter.Utf8WithBom; break;
                case "groups": content = CsvReportWriter.FailureGroups(_result, options); filter = "CSV (*.csv)|*.csv"; name = $"failure-groups-{org}-{stamp}.csv"; encoding = CsvReportWriter.Utf8WithBom; break;
                case "performance": content = CsvReportWriter.Performance(_result, options); filter = "CSV (*.csv)|*.csv"; name = $"performance-{org}-{stamp}.csv"; encoding = CsvReportWriter.Utf8WithBom; break;
                case "traces": content = CsvReportWriter.Traces(_result, _traceGrid.Items, options); filter = "CSV (*.csv)|*.csv"; name = $"traces-{org}-{stamp}.csv"; encoding = CsvReportWriter.Utf8WithBom; break;
                default: return;
            }

            using (var dialog = new SaveFileDialog { Filter = filter, FileName = name, OverwritePrompt = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dialog.FileName, content, encoding);
                    Status("Saved " + Path.GetFileName(dialog.FileName) + (options.IsRedacted ? " (redacted)" : ""));
                    LogInfo("Exported {0} ({1}redacted)", kind, options.IsRedacted ? "" : "not ");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    MessageBox.Show(this, "The file couldn't be saved: " + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string SafeFileName(string text) =>
            new string(text.Select(c => char.IsLetterOrDigit(c) || c == '-' ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');

        // ================================================================ misc

        private void Status(string message) => SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs(message));

        private void CopyToClipboard(string text, string what)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetText(text);
                Status(what + " copied to the clipboard");
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException || ex is System.Threading.ThreadStateException)
            {
                Status("The clipboard is busy; try again.");
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5) { ExecuteMethod(Analyze); return true; }
            if (keyData == (Keys.Control | Keys.F) && _tabs.SelectedTab == _tabTraces) { _txtSearch.Focus(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            _detailTimer?.Dispose();
            _searchTimer?.Dispose();
            base.OnHandleDestroyed(e);
        }
    }
}
