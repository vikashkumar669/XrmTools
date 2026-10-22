using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DiagnosticsWorkbench.Analysis;
using DiagnosticsWorkbench.Controls;
using DiagnosticsWorkbench.Data;
using DiagnosticsWorkbench.Export;
using DiagnosticsWorkbench.Models;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Args;
using XrmToolBox.Extensibility.Interfaces;
using Label = System.Windows.Forms.Label;

namespace DiagnosticsWorkbench
{
    /// <summary>
    /// Main UI: load plug-in trace logs for a date range, then review recurring errors, slow executions,
    /// correlation chains and every log as colour-coded cards. Uses the active XrmToolBox connection.
    /// </summary>
    public class DiagnosticsWorkbenchControl : PluginControlBase, IGitHubPlugin, IHelpPlugin, IStatusBarMessenger
    {
        // IGitHubPlugin / IHelpPlugin
        public string RepositoryName => "XrmTools";
        public string UserName => "vikashkumar669";
        public string HelpUrl => "https://github.com/vikashkumar669/XrmTools/tree/main/DiagnosticsWorkbench#readme";

        public event EventHandler<StatusBarMessageEventArgs> SendMessageToStatusBar;

        private const string AppName = "Diagnostics Workbench";
        private const int MaxSubCards = 2000;

        private static readonly string[] CommonMessages =
        {
            "", "Create", "Update", "Delete", "Retrieve", "RetrieveMultiple", "Assign", "SetState", "SetStateDynamicEntity",
            "Associate", "Disassociate", "Merge", "QualifyLead", "Win", "Lose", "Send", "ExecuteWorkflow"
        };

        // State
        private DiagnosticsSession _session = new DiagnosticsSession();
        private AnalysisResult _result = new AnalysisResult();
        private readonly ViewFilter _filter = new ViewFilter();
        private object _detailObject;
        private bool _suppress;
        private bool _traceChecked;

        // UI
        private ToolStrip _mainBar;
        private ToolStripButton _tsbLoad, _tsbCancel;
        private ToolStripDropDownButton _ddExport;
        private Label _lblInfo, _lblBanner, _lblSummary;
        private ComboBox _cboRange, _cboTable, _cboMessage, _cboType;
        private DateTimePicker _dtFrom, _dtTo;
        private CheckBox _chkErrors, _chkSlow;
        private TextBox _txtSearch;
        private Button _btnLoad;
        private Timer _searchTimer;
        private TabControl _tabs;
        private TabPage _tabErrors, _tabPerf, _tabChains, _tabLogs;
        private CardList _errorCards, _errorLogCards, _perfCards, _perfLogCards, _chainCards, _logCards;
        private Label _lblErrorLogs, _lblPerfLogs, _lblChainTree;
        private TreeView _chainTree;
        private FastGrid _logGrid;
        private ToolStripButton _tsbGridView;
        private Label _lblDetailTitle;
        private RichTextBox _detail;
        private ToolStripButton _tsbCopyMarkdown, _tsbCopyException, _tsbShowChain;

        public DiagnosticsWorkbenchControl()
        {
            BuildUi();
            ApplyRangePreset();
            UpdateInfo();
            UpdateBanner();
            UpdateButtons();
            ShowEmptyDetail();
        }

        // ============================== Connection & settings ==============================

        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            base.UpdateConnection(newService, detail, actionName, parameter);
            var settings = LoadSettings();
            _session = new DiagnosticsSession { Settings = settings, EnvironmentName = detail?.ConnectionName, EnvironmentUrl = EnvironmentUrl(detail) };
            _result = new AnalysisResult();
            _traceChecked = false;
            SelectRangePreset(settings.LastRangeHours);
            RefreshViews();
            UpdateInfo();
            UpdateBanner();
            UpdateButtons();
            if (newService != null && IsHandleCreated) CheckTraceSetting();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (Service != null && !_traceChecked) CheckTraceSetting();
        }

        private static string EnvironmentUrl(ConnectionDetail detail)
        {
            if (detail == null) return null;
            try { return detail.WebApplicationUrl ?? detail.OriginalUrl; }
            catch { return null; }
        }

        private string SettingsKey => ConnectionDetail?.ConnectionId?.ToString() ?? "default";

        private WorkbenchSettings LoadSettings()
        {
            WorkbenchSettings settings = null;
            try
            {
                if (!SettingsManager.Instance.TryLoad(typeof(DiagnosticsWorkbenchPlugin), out settings, SettingsKey))
                    settings = null;
            }
            catch (Exception ex)
            {
                LogWarning("Couldn't read saved settings: {0}", ex.Message);
            }
            settings = settings ?? new WorkbenchSettings();
            settings.Normalize();
            return settings;
        }

        private void SaveSettings()
        {
            try { SettingsManager.Instance.Save(typeof(DiagnosticsWorkbenchPlugin), _session.Settings, SettingsKey); }
            catch (Exception ex) { LogWarning("Couldn't save settings: {0}", ex.Message); }
        }

        private void CheckTraceSetting()
        {
            if (Service == null) return;
            _traceChecked = true;
            var service = Service;
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Reading the plug-in trace setting...",
                Work = (worker, args) => args.Result = new TraceLogRepository(service).GetTraceSetting(),
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        LogWarning("Couldn't read organization.plugintracelogsetting: {0}", args.Error.Message);
                        _session.TraceSetting = TraceSetting.Unknown;
                        UpdateBanner(args.Error.Message);
                        return;
                    }
                    _session.TraceSetting = (TraceSetting)args.Result;
                    UpdateBanner();
                    UpdateInfo();
                }
            });
        }

        // ============================== Loading ==============================

        private void LoadLogs()
        {
            if (Service == null) return;
            var range = CurrentRange();
            if (range.ToUtc <= range.FromUtc)
            {
                MessageBox.Show(this, "The end of the date range must be after the start.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var settings = _session.Settings.Clone();
            settings.LastRangeHours = RangeHours();
            var query = new QueryDescription
            {
                FromUtc = range.FromUtc,
                ToUtc = range.ToUtc,
                Table = ComboValue(_cboTable),
                Message = ComboValue(_cboMessage),
                PluginType = ComboValue(_cboType),
                RowCap = settings.RowCap,
            };
            var service = Service;
            var environment = ConnectionDetail?.ConnectionName;
            var url = EnvironmentUrl(ConnectionDetail);

            _tsbCancel.Enabled = true;
            _tsbLoad.Enabled = _btnLoad.Enabled = false;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading plug-in trace logs...",
                IsCancelable = true,
                Work = (worker, args) =>
                {
                    var repo = new TraceLogRepository(service);
                    var session = new DiagnosticsSession { Settings = settings, EnvironmentName = environment, EnvironmentUrl = url };
                    try { session.TraceSetting = repo.GetTraceSetting(); }
                    catch { session.TraceSetting = TraceSetting.Unknown; }

                    var logs = repo.Load(query, count => worker.ReportProgress(0, $"Loaded {count:N0} trace logs..."), () => !worker.CancellationPending);
                    var cancelled = worker.CancellationPending;
                    session.SetLogs(logs, query);

                    string metadataError = null;
                    if (!cancelled && logs.Count > 0)
                    {
                        worker.ReportProgress(0, "Reading step registrations...");
                        try { session.SetSteps(new StepMetadataRepository(service).Load(session.StepIds, () => !worker.CancellationPending)); }
                        catch (Exception ex) { metadataError = ex.Message; }
                    }

                    worker.ReportProgress(0, $"Analyzing {logs.Count:N0} logs...");
                    var analysis = session.Analyze(new ViewFilter());
                    args.Result = new LoadResult { Session = session, Analysis = analysis, Cancelled = cancelled, MetadataError = metadataError };
                },
                ProgressChanged = e => SetWorkingMessage(e.UserState as string ?? "Loading..."),
                PostWorkCallBack = args =>
                {
                    _tsbCancel.Enabled = false;
                    UpdateButtons();
                    if (args.Error != null)
                    {
                        ShowError("Couldn't load the plug-in trace logs", args.Error);
                        return;
                    }

                    var load = (LoadResult)args.Result;
                    _session = load.Session;
                    _traceChecked = true;
                    SaveSettings();
                    PopulateFilterLists();
                    RefreshViews();
                    UpdateBanner();
                    UpdateInfo();
                    UpdateButtons();

                    var q = _session.Query;
                    var note = load.Cancelled ? " (cancelled: partial result)" : q.CapReached ? $" (row cap of {q.RowCap:N0} reached: older logs not loaded)" : "";
                    Status($"{_session.AllLogs.Count:N0} trace logs loaded from {environment}{note}");
                    LogInfo("Loaded {0} trace logs ({1} → {2} UTC, filters: {3}){4}", _session.AllLogs.Count, Formatting.Iso(q.FromUtc), Formatting.Iso(q.ToUtc), q.FiltersText, note);
                    if (load.MetadataError != null)
                    {
                        LogWarning("Step metadata unavailable: {0}", load.MetadataError);
                        MessageBox.Show(this, "The logs loaded, but step registrations couldn't be read, so configuration flags are not shown.\n\n" + load.MetadataError,
                            AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    if (_session.AllLogs.Count == 0)
                        _errorCards.EmptyText = _logCards.EmptyText = "No trace logs in this range." + (_session.TraceSetting == TraceSetting.Off ? " Trace logging is Off." : "");
                }
            });
        }

        private sealed class LoadResult
        {
            public DiagnosticsSession Session;
            public AnalysisResult Analysis;
            public bool Cancelled;
            public string MetadataError;
        }

        private void PopulateFilterLists()
        {
            void Fill(ComboBox combo, IEnumerable<string> values, IEnumerable<string> extra = null)
            {
                var current = combo.Text;
                var items = new[] { "" }.Concat(extra ?? Enumerable.Empty<string>()).Concat(values.Where(v => !string.IsNullOrWhiteSpace(v)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray();
                combo.BeginUpdate();
                combo.Items.Clear();
                combo.Items.AddRange(items);
                combo.EndUpdate();
                combo.Text = current;
            }
            Fill(_cboTable, _session.AllLogs.Select(l => l.PrimaryEntity));
            Fill(_cboMessage, _session.AllLogs.Select(l => l.MessageName), CommonMessages);
            Fill(_cboType, _session.AllLogs.Select(l => l.FullTypeName));
        }

        // ============================== Views ==============================

        private void ApplyViewFilter()
        {
            _filter.ErrorsOnly = _chkErrors.Checked;
            _filter.SlowOnly = _chkSlow.Checked;
            _filter.Search = _txtSearch.Text;
            if (!_session.HasData) return;
            Cursor = Cursors.WaitCursor;
            try { RefreshViews(); }
            finally { Cursor = Cursors.Default; }
        }

        private void RefreshViews()
        {
            _result = _session.HasData ? _session.Analyze(_filter) : new AnalysisResult();
            var now = DateTime.UtcNow;

            _errorCards.SetCards(_result.ErrorGroups.Select(g => GroupCard(g, now)).ToList());
            _perfCards.SetCards(_result.Performance.Select(PerfCard).ToList());
            _chainCards.SetCards(_result.Chains.Select(ChainCard).ToList());
            _logCards.SetCards(_result.Logs.Select(LogCard).ToList());
            _logGrid.RowCount = 0;
            _logGrid.RowCount = _result.Logs.Count;

            _errorLogCards.SetCards(null);
            _perfLogCards.SetCards(null);
            _chainTree.Nodes.Clear();
            _lblErrorLogs.Text = "Select an error group to see its logs";
            _lblPerfLogs.Text = "Select a plugin type to see its slowest runs";
            _lblChainTree.Text = "Select a chain to see its steps, indented by depth";

            var empty = _session.HasData ? "Nothing matches the current filters." : "Pick a date range and press Load logs (F5).";
            _errorCards.EmptyText = _session.HasData && _result.ErrorCount == 0 ? "No errors in this view." : empty;
            _perfCards.EmptyText = _session.HasData && _result.Performance.Count == 0 ? "No executions with a recorded duration." : empty;
            _chainCards.EmptyText = _session.HasData && _result.Chains.Count == 0 ? "No multi-step chains in this view. Single executions are in Errors and All logs." : empty;
            _logCards.EmptyText = empty;

            _tabErrors.Text = $"Errors ({_result.ErrorGroups.Count:N0})";
            _tabPerf.Text = $"Performance ({_result.Performance.Count:N0})";
            _tabChains.Text = $"Chains ({_result.Chains.Count:N0})";
            _tabLogs.Text = $"All logs ({_result.Logs.Count:N0})";

            UpdateSummary();
            ShowEmptyDetail();
            UpdateButtons();
        }

        private void UpdateSummary()
        {
            if (!_session.HasData)
            {
                _lblSummary.Text = "No logs loaded yet.";
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"{_result.Logs.Count:N0} of {_session.AllLogs.Count:N0} logs in view");
            sb.AppendLine($"{_result.ErrorCount:N0} errors in {_result.ErrorGroups.Count:N0} groups");
            sb.AppendLine($"{_result.SlowCount:N0} slow · {_result.NearTimeoutCount:N0} near timeout");
            sb.AppendLine($"{_result.Chains.Count:N0} chains · {_result.FailedChainCount:N0} failed");
            sb.Append($"{_session.Steps.Count:N0} step registrations read");
            if (_session.Query?.CapReached == true) sb.Append($"\nRow cap of {_session.Query.RowCap:N0} reached");
            _lblSummary.Text = sb.ToString();
        }

        // ---------- Cards ----------

        private static string Short(string typeName) => new TraceLog { TypeName = typeName }.ShortTypeName;

        private static string FlagText(IList<ConfigFlag> flags) =>
            flags == null || flags.Count == 0 ? null : "⚑ " + string.Join("  ·  ", flags.Select(f => f.ShortText + ": " + f.StepName).Distinct());

        private Card GroupCard(ErrorGroup g, DateTime now)
        {
            var card = new Card
            {
                Title = Formatting.HeadlineMessage(g.Headline),
                Accent = Palette.Error,
                Tint = Palette.ErrorTint,
                Badge = g.Count.ToString("N0"),
                BadgeCaption = g.Count == 1 ? "hit" : "hits",
                TitleColor = Palette.BannerCritFore,
                Tag = g,
                FlagLine = FlagText(g.Flags),
            };
            var type = Formatting.HeadlineType(g.Headline);
            card.Lines.Add((type.Length > 0 ? type + "  ·  " : "") + $"{Short(g.TypeName)}  ·  {g.MessageName}  ·  {Blank(g.PrimaryEntity)}");
            card.Lines.Add($"{g.DistinctExecutions:N0} execution(s)  ·  first {Formatting.Local(g.FirstSeen)}  ·  last {Formatting.Ago(g.LastSeen, now)}");
            card.Lines.Add($"#{g.Fingerprint}  ·  {Formatting.Truncate(g.NormalizedText, 140)}");
            return card;
        }

        private Card PerfCard(PerformanceStat s)
        {
            var card = new Card
            {
                Title = $"{Short(s.TypeName)}  ·  {s.MessageName}",
                Accent = Palette.Slow,
                Tint = s.HasSlowRuns ? Palette.SlowTint : Palette.NeutralTint,
                Alert = s.HasNearTimeout,
                Badge = Formatting.Duration(s.P95Ms),
                BadgeCaption = "p95",
                Tag = s,
                FlagLine = FlagText(s.Flags),
            };
            card.Lines.Add($"{Blank(s.PrimaryEntity)}  ·  {s.ModeText}" + (string.IsNullOrEmpty(s.StageText) ? "" : "  ·  " + s.StageText) + $"  ·  {s.TypeName}");
            card.Lines.Add($"p50 {Formatting.Duration(s.P50Ms)}  ·  p95 {Formatting.Duration(s.P95Ms)}  ·  max {Formatting.Duration(s.MaxMs)}  ·  threshold {Formatting.Duration(s.ThresholdMs)}");
            card.Lines.Add($"{s.SlowCount:N0} of {s.Count:N0} runs slow" + (s.NearTimeoutCount > 0 ? $"  ·  {s.NearTimeoutCount:N0} near timeout" : "") +
                           (s.ErrorCount > 0 ? $"  ·  {s.ErrorCount:N0} failed" : ""));
            return card;
        }

        private Card ChainCard(CorrelationChain c)
        {
            var root = c.Root;
            var firstError = c.Entries.FirstOrDefault(e => e.IsError);
            var card = new Card
            {
                Title = $"{root?.ShortTypeName}  ·  {root?.MessageName}  ·  {Blank(root?.PrimaryEntity)}",
                Accent = Palette.Chain,
                Tint = Palette.ChainTint,
                Alert = c.Failed,
                Badge = c.Failed ? "FAILED" : c.Entries.Count.ToString("N0"),
                BadgeCaption = c.Failed ? $"{c.ErrorCount} of {c.Entries.Count} steps" : (c.Entries.Count == 1 ? "step" : "steps"),
                Tag = c,
                FlagLine = FlagText(c.Flags),
            };
            card.Lines.Add($"{Formatting.Local(c.Start)}  ·  {c.Entries.Count} step(s)  ·  depth {c.MaxDepth}  ·  span {Formatting.Duration(c.SpanMs)}" +
                           (c.RequestCount > 1 ? $"  ·  {c.RequestCount} requests" : ""));
            card.Lines.Add(firstError != null ? "✖ " + Formatting.DisplayHeadline(ExceptionNormalizer.Headline(firstError.ExceptionDetails)) : "✓ No errors");
            card.Lines.Add("Correlation " + c.CorrelationId);
            return card;
        }

        private Card LogCard(TraceLog l)
        {
            var slow = _session.Performance.IsSlow(l);
            var near = _session.Performance.IsNearTimeout(l);
            var card = new Card
            {
                Title = $"{Formatting.Local(l.CreatedOn)}   {l.ShortTypeName}  ·  {l.MessageName}  ·  {Blank(l.PrimaryEntity)}",
                Accent = l.IsError ? Palette.Error : slow ? Palette.Slow : Palette.Neutral,
                Tint = l.IsError ? Palette.ErrorTint : slow ? Palette.SlowTint : Palette.NeutralTint,
                Alert = near,
                Badge = Formatting.Duration(l.ExecutionDurationMs),
                BadgeCaption = near ? "near timeout" : slow ? "slow" : "",
                Tag = l,
            };
            card.Lines.Add(l.IsError ? "✖ " + Formatting.DisplayHeadline(ExceptionNormalizer.Headline(l.ExceptionDetails))
                : string.IsNullOrWhiteSpace(l.MessageBlock) ? "✓ Succeeded (no trace output)" : "✓ " + Formatting.FirstLine(l.MessageBlock));
            card.Lines.Add($"depth {l.Depth}  ·  {l.ModeText}  ·  {l.OperationTypeText}" + (l.CorrelationId.HasValue ? "  ·  correlation " + l.CorrelationId.Value.ToString().Substring(0, 8) : ""));
            return card;
        }

        private static string Blank(string s) => string.IsNullOrEmpty(s) ? "(no table)" : s;

        // ---------- Selection ----------

        private void OnErrorGroupSelected()
        {
            if (!(_errorCards.SelectedCard?.Tag is ErrorGroup g)) return;
            var logs = g.Logs.Take(MaxSubCards).ToList();
            _errorLogCards.SetCards(logs.Select(LogCard).ToList());
            _lblErrorLogs.Text = $"Logs in this group ({g.Count:N0}{(g.Count > MaxSubCards ? $", newest {MaxSubCards:N0} shown" : "")}), newest first";
            ShowGroupDetail(g);
        }

        private void OnPerfSelected()
        {
            if (!(_perfCards.SelectedCard?.Tag is PerformanceStat s)) return;
            var logs = s.Logs.Take(MaxSubCards).ToList();
            _perfLogCards.SetCards(logs.Select(LogCard).ToList());
            _lblPerfLogs.Text = $"Runs, slowest first ({s.Count:N0}{(s.Count > MaxSubCards ? $", {MaxSubCards:N0} shown" : "")})";
            ShowStatDetail(s);
        }

        private void OnChainSelected()
        {
            if (!(_chainCards.SelectedCard?.Tag is CorrelationChain c)) return;
            BuildChainTree(c);
            ShowChainDetail(c);
        }

        private void BuildChainTree(CorrelationChain chain, Guid? select = null)
        {
            _chainTree.BeginUpdate();
            _chainTree.Nodes.Clear();
            var stack = new List<TreeNode>();
            TreeNode selected = null;
            foreach (var e in chain.Entries)
            {
                var node = new TreeNode($"d{e.Depth}  {e.ShortTypeName}  ·  {e.MessageName}  ·  {Blank(e.PrimaryEntity)}  ·  {e.ModeText}  ·  {Formatting.Duration(e.ExecutionDurationMs)}" +
                                        (e.IsError ? "   ✖ " + Formatting.Truncate(Formatting.HeadlineMessage(ExceptionNormalizer.Headline(e.ExceptionDetails)), 70) : ""))
                {
                    Tag = e,
                    ForeColor = e.IsError ? Palette.Error : _session.Performance.IsSlow(e) ? Palette.FlagFore : Palette.Ink,
                    ToolTipText = e.FullTypeName,
                };
                while (stack.Count > 0 && ((TraceLog)stack[stack.Count - 1].Tag).Depth >= e.Depth) stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0) _chainTree.Nodes.Add(node);
                else stack[stack.Count - 1].Nodes.Add(node);
                stack.Add(node);
                if (select.HasValue && e.Id == select.Value) selected = node;
            }
            _chainTree.ExpandAll();
            _chainTree.EndUpdate();
            if (selected != null) { _chainTree.SelectedNode = selected; selected.EnsureVisible(); }
            _lblChainTree.Text = $"Steps in this chain ({chain.Entries.Count}), indented by depth  ·  red = failed, amber = slow";
        }

        private void JumpToChain(TraceLog log)
        {
            var chain = _session.ChainFor(log, _result);
            if (chain == null) return;
            _tabs.SelectedTab = _tabChains;
            for (var i = 0; i < _chainCards.Items.Count; i++)
            {
                if (((Card)_chainCards.Items[i]).Tag == chain)
                {
                    _suppress = true;
                    _chainCards.SelectedIndex = i;
                    _suppress = false;
                    break;
                }
            }
            BuildChainTree(chain, log.Id);
            ShowLogDetail(log);
        }

        // ============================== Detail pane ==============================

        private void ShowEmptyDetail()
        {
            _detailObject = null;
            _lblDetailTitle.Text = "Details";
            _detail.Clear();
            Para(_session.HasData ? "Select a card to see its details." : "Load trace logs to start.", Palette.Muted);
            UpdateDetailButtons();
        }

        private void ShowLogDetail(TraceLog log)
        {
            if (log == null) return;
            _detailObject = log;
            var step = _session.StepFor(log);
            var chain = _session.ChainFor(log, _result);
            var perf = _session.Performance;

            _lblDetailTitle.Text = $"{log.ShortTypeName} · {log.MessageName} · {Blank(log.PrimaryEntity)}";
            _detail.SuspendLayout();
            _detail.Clear();

            if (log.IsError) Para("✖ Failed", Palette.Error, true);
            else Para("✓ Succeeded", Palette.Ok, true);
            if (perf.IsNearTimeout(log)) Para($"Near the 2-minute timeout ({Formatting.Duration(log.ExecutionDurationMs)})", Palette.Error, true);
            else if (perf.IsSlow(log)) Para($"Slow: {Formatting.Duration(log.ExecutionDurationMs)} against a threshold of {Formatting.Duration(perf.ThresholdFor(log))}", Palette.FlagFore, true);

            Section("Execution context");
            Kv("When", $"{Formatting.Local(log.CreatedOn)} local  ·  {Formatting.Iso(log.CreatedOn)}");
            Kv("Plugin type", log.FullTypeName);
            Kv("Message · table", $"{log.MessageName} · {Blank(log.PrimaryEntity)}");
            Kv("Mode · depth", $"{log.ModeText} · {log.Depth}");
            Kv("Operation", log.OperationTypeText);
            Kv("Duration", $"{Formatting.Duration(log.ExecutionDurationMs)} (constructor {Formatting.Duration(log.ConstructorDurationMs)}, threshold {Formatting.Duration(perf.ThresholdFor(log))})");
            if (!string.IsNullOrEmpty(log.CreatedByName)) Kv("Created by", log.CreatedByName);
            if (log.CorrelationId.HasValue) Kv("Correlation ID", log.CorrelationId.ToString());
            if (log.RequestId.HasValue) Kv("Request ID", log.RequestId.ToString());
            Kv("Trace log ID", log.Id.ToString());

            Section("Step registration");
            if (step == null)
                Para(log.StepId.HasValue ? "Registration not found (system step, deleted step, or no read access)." : "No step ID on this log.", Palette.Muted);
            else
            {
                Kv("Step", step.Name);
                Kv("Stage · mode · rank", $"{step.StageText} · {step.ModeText} · {step.Rank}");
                Kv("Filtering attributes", step.HasFilteringAttributes ? step.FilteringAttributes : "(none: runs for every column)");
                if (step.Images.Count == 0) Kv("Images", "(none)");
                foreach (var image in step.Images)
                    Kv(image.ImageTypeText, $"{image.Name}: " + (image.AllColumns ? "all columns" : image.Attributes));
                if (!string.IsNullOrEmpty(step.AssemblyName))
                    Kv("Assembly", $"{step.AssemblyName} {step.AssemblyVersion} ({step.IsolationText})");
                Kv("State", step.StateCode == 1 ? "Disabled" : "Enabled");
            }

            var flags = ConfigFlagger.ForStep(step);
            if (chain != null) flags.AddRange(chain.Flags.Where(f => f.Kind == ConfigFlagKind.PossibleRecursion && f.StepId == log.StepId));
            if (flags.Count > 0)
            {
                Section("Configuration flags");
                foreach (var f in flags.Distinct()) Para("⚑ " + f.Message, Palette.FlagFore);
            }

            if (log.IsError)
            {
                Section("Exception");
                Mono(log.ExceptionDetails);
            }
            Section("Trace output (message block)");
            if (string.IsNullOrWhiteSpace(log.MessageBlock)) Para("(empty)", Palette.Muted);
            else Mono(log.MessageBlock);

            if (chain != null && chain.Entries.Count > 1)
            {
                Section($"Chain  ·  {chain.Entries.Count} steps  ·  span {Formatting.Duration(chain.SpanMs)}");
                foreach (var e in chain.Entries)
                    Para(new string(' ', Math.Max(0, e.Depth - 1) * 4) + (e.Id == log.Id ? "▶ " : "• ") +
                         $"d{e.Depth} {e.ShortTypeName} · {e.MessageName} · {Formatting.Duration(e.ExecutionDurationMs)}" + (e.IsError ? "  ✖" : ""),
                         e.IsError ? Palette.Error : e.Id == log.Id ? Palette.Brand : Palette.Ink, e.Id == log.Id);
            }

            _detail.SelectionStart = 0;
            _detail.ScrollToCaret();
            _detail.ResumeLayout();
            UpdateDetailButtons();
        }

        private void ShowGroupDetail(ErrorGroup g)
        {
            _detailObject = g;
            _lblDetailTitle.Text = $"Error group #{g.Fingerprint}  ·  {g.Count:N0} hits";
            _detail.Clear();
            Para(g.Headline, Palette.Error, true);

            Section("Where");
            Kv("Plugin type", g.TypeName);
            Kv("Message · table", $"{g.MessageName} · {Blank(g.PrimaryEntity)}");
            Kv("Hits · executions", $"{g.Count:N0} · {g.DistinctExecutions:N0}");
            Kv("First seen", $"{Formatting.Local(g.FirstSeen)} local");
            Kv("Last seen", $"{Formatting.Local(g.LastSeen)} local ({Formatting.Ago(g.LastSeen, DateTime.UtcNow)})");

            Section("Hits per day (UTC)");
            var max = g.HitsPerDay.Values.DefaultIfEmpty(1).Max();
            foreach (var day in g.HitsPerDay.Reverse().Take(31))
                MonoLine($"{day.Key:yyyy-MM-dd}  {new string('■', Math.Max(1, (int)Math.Round(day.Value * 12.0 / max)))} {day.Value:N0}");

            if (g.Flags.Count > 0)
            {
                Section("Configuration flags");
                foreach (var f in g.Flags) Para("⚑ " + f.Message, Palette.FlagFore);
            }

            Section("Fingerprint");
            Kv("Fingerprint", g.Fingerprint);
            Kv("Normalized", g.NormalizedText);

            Section("Most recent exception");
            Mono(g.SampleException);
            _detail.SelectionStart = 0;
            _detail.ScrollToCaret();
            UpdateDetailButtons();
        }

        private void ShowStatDetail(PerformanceStat s)
        {
            _detailObject = s;
            _lblDetailTitle.Text = $"{Short(s.TypeName)} · {s.MessageName}  ·  p95 {Formatting.Duration(s.P95Ms)}";
            _detail.Clear();
            if (s.NearTimeoutCount > 0) Para($"{s.NearTimeoutCount:N0} run(s) near the 2-minute timeout", Palette.Error, true);
            if (s.SlowCount > 0) Para($"{s.SlowCount:N0} of {s.Count:N0} runs above the {Formatting.Duration(s.ThresholdMs)} threshold", Palette.FlagFore, true);
            else Para($"No runs above the {Formatting.Duration(s.ThresholdMs)} threshold", Palette.Ok, true);

            Section("Durations");
            Kv("Runs", s.Count.ToString("N0"));
            Kv("p50 · p95 · max", $"{Formatting.Duration(s.P50Ms)} · {Formatting.Duration(s.P95Ms)} · {Formatting.Duration(s.MaxMs)}");
            Kv("Average", Formatting.Duration((long)Math.Round(s.AverageMs)));
            Kv("Failed runs", s.ErrorCount.ToString("N0"));

            Section("Where");
            Kv("Plugin type", s.TypeName);
            Kv("Message · table", $"{s.MessageName} · {Blank(s.PrimaryEntity)}");
            Kv("Mode · stage", s.ModeText + (string.IsNullOrEmpty(s.StageText) ? "" : " · " + s.StageText));

            if (s.Flags.Count > 0)
            {
                Section("Configuration flags");
                foreach (var f in s.Flags) Para("⚑ " + f.Message, Palette.FlagFore);
            }

            Section("Slowest runs");
            foreach (var l in s.Logs.Take(10))
                MonoLine($"{Formatting.Duration(l.ExecutionDurationMs),10}  {Formatting.Local(l.CreatedOn)}  d{l.Depth} {l.ModeText}{(l.IsError ? "  ✖ failed" : "")}");
            _detail.SelectionStart = 0;
            _detail.ScrollToCaret();
            UpdateDetailButtons();
        }

        private void ShowChainDetail(CorrelationChain c)
        {
            _detailObject = c;
            _lblDetailTitle.Text = $"Chain  ·  {c.Entries.Count} {(c.Entries.Count == 1 ? "step" : "steps")}  ·  {(c.Failed ? "failed" : "succeeded")}";
            _detail.Clear();
            if (c.Failed) Para($"✖ {c.ErrorCount} of {c.Entries.Count} steps failed", Palette.Error, true);
            else Para("✓ No errors", Palette.Ok, true);

            Section("Chain");
            Kv("Correlation ID", c.CorrelationId.ToString());
            Kv("Start", $"{Formatting.Local(c.Start)} local");
            Kv("Span", Formatting.Duration(c.SpanMs));
            Kv("Depth · requests", $"{c.MaxDepth} · {c.RequestCount}");

            if (c.Flags.Count > 0)
            {
                Section("Configuration flags");
                foreach (var f in c.Flags) Para("⚑ " + f.Message, Palette.FlagFore);
            }

            Section("Steps (select one in the tree below for full details)");
            foreach (var e in c.Entries)
                Para(new string(' ', Math.Max(0, e.Depth - 1) * 4) + $"• d{e.Depth} {e.ShortTypeName} · {e.MessageName} · {Formatting.Duration(e.ExecutionDurationMs)}" +
                     (e.IsError ? "  ✖ " + Formatting.DisplayHeadline(ExceptionNormalizer.Headline(e.ExceptionDetails)) : ""), e.IsError ? Palette.Error : Palette.Ink);
            _detail.SelectionStart = 0;
            _detail.ScrollToCaret();
            UpdateDetailButtons();
        }

        private void UpdateDetailButtons()
        {
            _tsbCopyMarkdown.Enabled = _detailObject is TraceLog || _detailObject is ErrorGroup;
            _tsbCopyException.Enabled = (_detailObject as TraceLog)?.IsError == true || _detailObject is ErrorGroup;
            _tsbShowChain.Enabled = _detailObject is TraceLog l && _session.ChainFor(l, _result)?.Entries.Count > 1 && _tabs.SelectedTab != _tabChains;
        }

        private void CopyMarkdown()
        {
            string text = null;
            if (_detailObject is TraceLog log)
                text = MarkdownFormatter.ForLog(log, _session.StepFor(log), _session.ChainFor(log, _result), _session.Performance, _session.EnvironmentName);
            else if (_detailObject is ErrorGroup g)
                text = MarkdownFormatter.ForGroup(g);
            if (string.IsNullOrEmpty(text)) return;
            Clipboard.SetText(text);
            Status("Copied as Markdown");
        }

        private void CopyException()
        {
            var text = (_detailObject as TraceLog)?.ExceptionDetails ?? (_detailObject as ErrorGroup)?.SampleException;
            if (string.IsNullOrEmpty(text)) return;
            Clipboard.SetText(text);
            Status("Exception copied");
        }

        // Rich text helpers
        private void Section(string title)
        {
            _detail.SelectionStart = _detail.TextLength;
            _detail.SelectionFont = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            _detail.SelectionColor = Palette.Brand;
            _detail.AppendText((_detail.TextLength > 0 ? "\n" : "") + title + "\n");
        }

        private void Kv(string key, string value)
        {
            _detail.SelectionStart = _detail.TextLength;
            _detail.SelectionFont = new Font("Segoe UI", 9F);
            _detail.SelectionColor = Palette.Muted;
            _detail.AppendText(key + ":  ");
            _detail.SelectionFont = new Font("Segoe UI", 9F);
            _detail.SelectionColor = Palette.Ink;
            _detail.AppendText((value ?? "") + "\n");
        }

        private void Para(string text, Color color, bool bold = false)
        {
            _detail.SelectionStart = _detail.TextLength;
            _detail.SelectionFont = new Font("Segoe UI", 9F, bold ? FontStyle.Bold : FontStyle.Regular);
            _detail.SelectionColor = color;
            _detail.AppendText((text ?? "") + "\n");
        }

        private void Mono(string text)
        {
            _detail.SelectionStart = _detail.TextLength;
            _detail.SelectionFont = new Font("Consolas", 8.5F);
            _detail.SelectionColor = Palette.Ink;
            _detail.AppendText(Formatting.Truncate((text ?? "").Replace("\r\n", "\n").TrimEnd(), 60000) + "\n");
        }

        private void MonoLine(string text) => Mono(text);

        // ============================== Export ==============================

        private void ExportCsv(bool currentTab, bool redact)
        {
            if (!_session.HasData) return;
            var tab = currentTab ? _tabs.SelectedTab : _tabLogs;
            var what = !currentTab ? "trace-logs-all" : tab == _tabErrors ? "error-groups" : tab == _tabPerf ? "performance" : tab == _tabChains ? "chains" : "trace-logs";
            var path = AskSavePath($"{what}-{DateTime.Now:yyyyMMdd-HHmm}{(redact ? "-redacted" : "")}.csv", "CSV file (*.csv)|*.csv");
            if (path == null) return;
            try
            {
                using (var writer = new StreamWriter(path, false, CsvExporter.Utf8Bom))
                {
                    if (!currentTab) CsvExporter.WriteLogs(writer, _session.AllLogs, _session, redact);
                    else if (tab == _tabErrors) CsvExporter.WriteErrorGroups(writer, _result.ErrorGroups, redact);
                    else if (tab == _tabPerf) CsvExporter.WritePerformance(writer, _result.Performance);
                    else if (tab == _tabChains) CsvExporter.WriteChains(writer, _result.Chains, redact);
                    else CsvExporter.WriteLogs(writer, _result.Logs, _session, redact);
                }
                Status("Exported " + Path.GetFileName(path));
                OfferOpen(path);
            }
            catch (Exception ex) { ShowError("Couldn't write the CSV file", ex); }
        }

        private void ExportHtml(bool redact)
        {
            if (!_session.HasData) return;
            var path = AskSavePath($"diagnostics-report-{DateTime.Now:yyyyMMdd-HHmm}{(redact ? "-redacted" : "")}.html", "HTML report (*.html)|*.html");
            if (path == null) return;
            try
            {
                var html = HtmlReportBuilder.Build(_session, _result, _filter, new HtmlReportOptions
                {
                    Redact = redact,
                    ToolVersion = typeof(DiagnosticsWorkbenchControl).Assembly.GetName().Version?.ToString(),
                });
                File.WriteAllText(path, html, new UTF8Encoding(false));
                Status("Exported " + Path.GetFileName(path));
                OfferOpen(path);
            }
            catch (Exception ex) { ShowError("Couldn't write the HTML report", ex); }
        }

        private string AskSavePath(string fileName, string filter)
        {
            using (var dlg = new SaveFileDialog { FileName = fileName, Filter = filter, OverwritePrompt = true, AddExtension = true })
                return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
        }

        private void OfferOpen(string path)
        {
            if (MessageBox.Show(this, $"Saved {Path.GetFileName(path)}.\n\nOpen it now?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { ShowError("Couldn't open the file", ex); }
        }

        // ============================== Settings ==============================

        private void EditSettings()
        {
            var types = _session.AllLogs.Select(l => l.FullTypeName);
            var edited = SettingsDialog.Edit(this, _session.Settings, ConnectionDetail?.ConnectionName, types);
            if (edited == null) return;
            edited.LastRangeHours = RangeHours();
            _session.Settings = edited;
            foreach (var bad in _session.Normalizer.InvalidRules)
                LogWarning("Normalization rule '{0}' ignored: {1}", bad.Key, bad.Value);
            SaveSettings();
            if (_session.HasData) ApplyViewFilter();
            Status("Settings saved for " + (ConnectionDetail?.ConnectionName ?? "this tool"));
        }

        // ============================== Date range ==============================

        private static readonly int[] RangePresetHours = { 1, 24, 24 * 7, 24 * 30, 0 };

        private int RangeHours() => _cboRange.SelectedIndex >= 0 ? RangePresetHours[_cboRange.SelectedIndex] : 0;

        private void SelectRangePreset(int hours)
        {
            var index = Array.IndexOf(RangePresetHours, hours);
            _cboRange.SelectedIndex = index >= 0 ? index : 1;
            ApplyRangePreset();
        }

        private void ApplyRangePreset()
        {
            var hours = RangeHours();
            var custom = hours == 0;
            _dtFrom.Enabled = _dtTo.Enabled = custom;
            if (!custom)
            {
                var now = DateTime.Now;
                _dtTo.Value = now;
                _dtFrom.Value = now.AddHours(-hours);
            }
        }

        private (DateTime FromUtc, DateTime ToUtc) CurrentRange()
        {
            var hours = RangeHours();
            if (hours > 0)
            {
                var now = DateTime.UtcNow;
                return (now.AddHours(-hours), now.AddMinutes(1));
            }
            return (DateTime.SpecifyKind(_dtFrom.Value, DateTimeKind.Local).ToUniversalTime(),
                    DateTime.SpecifyKind(_dtTo.Value, DateTimeKind.Local).ToUniversalTime());
        }

        private static string ComboValue(ComboBox combo) => string.IsNullOrWhiteSpace(combo.Text) ? null : combo.Text.Trim();

        // ============================== Helpers ==============================

        private void UpdateInfo()
        {
            var parts = new List<string>();
            parts.Add(ConnectionDetail == null ? "Not connected" : $"Environment: {ConnectionDetail.ConnectionName}");
            parts.Add("Trace setting: " + (Service == null ? "–" : HtmlReportBuilder.TraceSettingText(_session.TraceSetting)));
            if (_session.HasData)
            {
                var q = _session.Query;
                parts.Add($"{_session.AllLogs.Count:N0} logs · {Formatting.Local(q.FromUtc).Substring(0, 16)} → {Formatting.Local(q.ToUtc).Substring(0, 16)}" +
                          (q.FiltersText == "none" ? "" : " · " + q.FiltersText) + (q.CapReached ? " · ROW CAP REACHED" : ""));
            }
            _lblInfo.Text = string.Join("     |     ", parts);
        }

        private void UpdateBanner(string error = null)
        {
            string text = null;
            var critical = false;
            if (Service != null)
            {
                switch (_session.TraceSetting)
                {
                    case TraceSetting.Off:
                        critical = true;
                        text = "Plug-in trace logging is OFF in this environment, so no new logs are being written. Existing logs can still be loaded. " +
                               "Turn it on in Power Platform admin center or System Settings > Customization > \"Enable logging to plug-in trace log\".";
                        break;
                    case TraceSetting.Exception:
                        text = "Plug-in trace logging is set to EXCEPTION: only failed executions are logged, so performance figures cover failures only. " +
                               "Set it to All while you investigate slow steps.";
                        break;
                    case TraceSetting.Unknown when error != null:
                        text = "Couldn't read the plug-in trace setting (read access to the Organization table is needed). Logs can still be loaded.";
                        break;
                }
            }
            _lblBanner.Visible = text != null;
            _lblBanner.Text = text ?? "";
            _lblBanner.BackColor = critical ? Palette.BannerCritBack : Palette.BannerWarnBack;
            _lblBanner.ForeColor = critical ? Palette.BannerCritFore : Palette.BannerWarnFore;
        }

        private void UpdateButtons()
        {
            if (_tsbLoad == null) return;
            var busy = _tsbCancel.Enabled;
            _tsbLoad.Enabled = _btnLoad.Enabled = !busy;
            _ddExport.Enabled = _session.HasData && !busy;
        }

        private void Status(string message) => SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs(message));

        private void ShowError(string title, Exception ex)
        {
            var inner = ex;
            while (inner.InnerException != null && inner is System.Reflection.TargetInvocationException) inner = inner.InnerException;
            LogError("{0}: {1}", title, inner.ToString());
            MessageBox.Show(this, $"{title}.\n\n{inner.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5) { ExecuteMethod(LoadLogs); return true; }
            if (keyData == (Keys.Control | Keys.F)) { _txtSearch.Focus(); _txtSearch.SelectAll(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ============================== UI construction ==============================

        private void BuildUi()
        {
            SuspendLayout();
            Font = new Font("Segoe UI", 9F);
            Size = new Size(1300, 800);
            BackColor = Color.White;

            // ---- Main toolbar ----
            _mainBar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2), RenderMode = ToolStripRenderMode.System };
            var tsbClose = ToolStripFactory.Button("Close", "Close this tool", (s, e) => CloseTool());
            _tsbLoad = ToolStripFactory.Button("▶ Load logs", "Load plug-in trace logs for the date range and filters on the left (F5)", (s, e) => ExecuteMethod(LoadLogs), true);
            _tsbLoad.ForeColor = Palette.Brand;
            _tsbCancel = ToolStripFactory.Button("■ Cancel", "Stop loading after the current page", (s, e) => CancelWorker());
            _tsbCancel.Enabled = false;
            _ddExport = new ToolStripDropDownButton("Export") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Export the current view as CSV or a diagnostic report as HTML" };
            _ddExport.DropDownItems.Add("HTML diagnostic report…", null, (s, e) => ExportHtml(false));
            _ddExport.DropDownItems.Add("HTML diagnostic report, redacted (no GUIDs, users or trace output)…", null, (s, e) => ExportHtml(true));
            _ddExport.DropDownItems.Add(new ToolStripSeparator());
            _ddExport.DropDownItems.Add("Current tab as CSV…", null, (s, e) => ExportCsv(true, false));
            _ddExport.DropDownItems.Add("Current tab as CSV, redacted…", null, (s, e) => ExportCsv(true, true));
            _ddExport.DropDownItems.Add("All loaded logs as CSV…", null, (s, e) => ExportCsv(false, false));
            var tsbSettings = ToolStripFactory.Button("Settings…", "Slow-run thresholds, row cap and custom normalization rules (saved per connection)", (s, e) => EditSettings());
            var tsbTrace = ToolStripFactory.Button("⟳ Trace setting", "Re-read the environment's plug-in trace setting", (s, e) => ExecuteMethod(CheckTraceSetting));
            _mainBar.Items.AddRange(new ToolStripItem[]
            {
                tsbClose, new ToolStripSeparator(), _tsbLoad, _tsbCancel, new ToolStripSeparator(), _ddExport, new ToolStripSeparator(), tsbSettings, tsbTrace
            });

            _lblInfo = new Label { Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 5, 8, 0), ForeColor = Palette.Brand, BackColor = Palette.BrandLight, Font = new Font("Segoe UI", 9F, FontStyle.Bold), AutoEllipsis = true };
            _lblBanner = new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 4, 8, 4), AutoEllipsis = true, Visible = false };

            // ---- Filter panel (left) ----
            var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10, 8, 8, 8), AutoScroll = true, BackColor = Color.FromArgb(0xFA, 0xFA, 0xFC) };
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void Add(Control c) { c.Dock = DockStyle.Top; filters.Controls.Add(c); }
            Label Caption(string text, bool heading = false) => new Label
            {
                Text = text, AutoSize = false, Height = heading ? 26 : 20, Margin = new Padding(0, heading ? 10 : 4, 0, 0),
                Font = heading ? new Font("Segoe UI", 9F, FontStyle.Bold) : Font, ForeColor = heading ? Palette.Brand : Palette.Muted, TextAlign = ContentAlignment.BottomLeft
            };

            Add(Caption("Query  ·  sent to Dataverse", true));
            Add(Caption("Date range"));
            _cboRange = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            _cboRange.Items.AddRange(new object[] { "Last hour", "Last 24 hours", "Last 7 days", "Last 30 days", "Custom range" });
            _cboRange.SelectedIndex = 1;
            _cboRange.SelectedIndexChanged += (s, e) => ApplyRangePreset();
            Add(_cboRange);
            Add(Caption("From (local time)"));
            _dtFrom = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm" };
            Add(_dtFrom);
            Add(Caption("To (local time)"));
            _dtTo = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm" };
            Add(_dtTo);
            Add(Caption("Table (logical name)"));
            _cboTable = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
            Add(_cboTable);
            Add(Caption("Message"));
            _cboMessage = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
            _cboMessage.Items.AddRange(CommonMessages.Cast<object>().ToArray());
            Add(_cboMessage);
            Add(Caption("Plugin type contains"));
            _cboType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
            Add(_cboType);
            _btnLoad = new Button { Text = "Load logs  (F5)", Height = 32, Margin = new Padding(0, 10, 0, 0), BackColor = Palette.Brand, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
            _btnLoad.FlatAppearance.BorderSize = 0;
            _btnLoad.Click += (s, e) => ExecuteMethod(LoadLogs);
            Add(_btnLoad);

            Add(Caption("View  ·  instant, no reload", true));
            _chkErrors = new CheckBox { Text = "Errors only", Height = 22 };
            _chkSlow = new CheckBox { Text = "Slow runs only", Height = 22 };
            _chkErrors.CheckedChanged += (s, e) => ApplyViewFilter();
            _chkSlow.CheckedChanged += (s, e) => ApplyViewFilter();
            Add(_chkErrors);
            Add(_chkSlow);
            Add(Caption("Search  ·  Ctrl+F"));
            _txtSearch = new TextBox();
            new ToolTip().SetToolTip(_txtSearch, "Matches plugin type, message, table, exception, trace output and IDs");
            _searchTimer = new Timer { Interval = 350 };
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); ApplyViewFilter(); };
            _txtSearch.TextChanged += (s, e) => { _searchTimer.Stop(); _searchTimer.Start(); };
            Add(_txtSearch);

            Add(Caption("Summary", true));
            _lblSummary = new Label { AutoSize = false, Height = 110, ForeColor = Palette.Ink };
            Add(_lblSummary);

            // ---- Tabs (centre) ----
            _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            _tabErrors = new TabPage("Errors");
            _tabPerf = new TabPage("Performance");
            _tabChains = new TabPage("Chains");
            _tabLogs = new TabPage("All logs");

            _errorCards = new CardList(112) { Dock = DockStyle.Fill };
            _errorLogCards = new CardList(78) { Dock = DockStyle.Fill };
            _errorCards.SelectedIndexChanged += (s, e) => { if (!_suppress) OnErrorGroupSelected(); };
            _errorLogCards.SelectedIndexChanged += (s, e) => { if (_errorLogCards.SelectedCard?.Tag is TraceLog l) ShowLogDetail(l); };
            _tabErrors.Controls.Add(Split(_errorCards, _errorLogCards, out _lblErrorLogs));

            _perfCards = new CardList(112) { Dock = DockStyle.Fill };
            _perfLogCards = new CardList(78) { Dock = DockStyle.Fill };
            _perfCards.SelectedIndexChanged += (s, e) => { if (!_suppress) OnPerfSelected(); };
            _perfLogCards.SelectedIndexChanged += (s, e) => { if (_perfLogCards.SelectedCard?.Tag is TraceLog l) ShowLogDetail(l); };
            _tabPerf.Controls.Add(Split(_perfCards, _perfLogCards, out _lblPerfLogs));

            _chainCards = new CardList(112) { Dock = DockStyle.Fill };
            _chainTree = new TreeView { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, ShowNodeToolTips = true, HideSelection = false, FullRowSelect = true, ItemHeight = 22, Font = new Font("Segoe UI", 9F) };
            _chainCards.SelectedIndexChanged += (s, e) => { if (!_suppress) OnChainSelected(); };
            _chainTree.AfterSelect += (s, e) => { if (e.Node?.Tag is TraceLog l) ShowLogDetail(l); };
            _tabChains.Controls.Add(Split(_chainCards, _chainTree, out _lblChainTree));

            _logCards = new CardList(78) { Dock = DockStyle.Fill };
            _logCards.SelectedIndexChanged += (s, e) => { if (_logCards.SelectedCard?.Tag is TraceLog l) ShowLogDetail(l); };
            _logGrid = new FastGrid { Dock = DockStyle.Fill, VirtualMode = true, Visible = false };
            _logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Created on (local)", Width = 140 });
            _logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Plugin type", Width = 240 });
            _logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Message", Width = 110 });
            _logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Table", Width = 120 });
            _logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Duration", Width = 80, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight } });
            _logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Depth", Width = 50, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight } });
            _logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Mode", Width = 60 });
            _logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Result", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 200 });
            _logGrid.CellValueNeeded += LogGridCellValueNeeded;
            _logGrid.CellFormatting += LogGridCellFormatting;
            _logGrid.SelectionChanged += (s, e) =>
            {
                var row = _logGrid.CurrentCell?.RowIndex ?? -1;
                if (row >= 0 && row < _result.Logs.Count) ShowLogDetail(_result.Logs[row]);
            };
            var logBar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System, Dock = DockStyle.Top };
            _tsbGridView = ToolStripFactory.Toggle("Grid view", "Switch between cards and a compact grid", false, on =>
            {
                _logGrid.Visible = on;
                _logCards.Visible = !on;
            });
            logBar.Items.Add(_tsbGridView);
            logBar.Items.Add(new ToolStripLabel("  Red = failed  ·  amber = slow  ·  red border = near timeout") { ForeColor = Palette.Muted });
            _tabLogs.Controls.Add(_logCards);
            _tabLogs.Controls.Add(_logGrid);
            _tabLogs.Controls.Add(logBar);

            _tabs.TabPages.AddRange(new[] { _tabErrors, _tabPerf, _tabChains, _tabLogs });
            _tabs.SelectedIndexChanged += (s, e) => UpdateDetailButtons();

            // ---- Detail pane (right) ----
            var detailPanel = new Panel { Dock = DockStyle.Fill };
            _lblDetailTitle = new Label { Dock = DockStyle.Top, Height = 28, Padding = new Padding(8, 6, 8, 0), Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), ForeColor = Palette.Ink, AutoEllipsis = true, BackColor = Color.FromArgb(0xFA, 0xFA, 0xFC) };
            var detailBar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System, Dock = DockStyle.Top };
            _tsbCopyMarkdown = ToolStripFactory.Button("Copy as Markdown", "Copy a ticket-ready summary of this log or error group", (s, e) => CopyMarkdown());
            _tsbCopyException = ToolStripFactory.Button("Copy exception", "Copy the full exception text", (s, e) => CopyException());
            _tsbShowChain = ToolStripFactory.Button("Show chain", "Open this log's correlation chain", (s, e) => { if (_detailObject is TraceLog l) JumpToChain(l); });
            detailBar.Items.AddRange(new ToolStripItem[] { _tsbCopyMarkdown, _tsbCopyException, _tsbShowChain });
            _detail = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, DetectUrls = false, WordWrap = true, Font = new Font("Segoe UI", 9F) };
            var detailPad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 4, 4), BackColor = Color.White };
            detailPad.Controls.Add(_detail);
            detailPanel.Controls.Add(detailPad);
            detailPanel.Controls.Add(detailBar);
            detailPanel.Controls.Add(_lblDetailTitle);

            // ---- Layout ----
            var right = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = 5 };
            right.Panel1.Controls.Add(_tabs);
            right.Panel2.Controls.Add(detailPanel);
            var main = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 5 };
            main.Panel1.Controls.Add(filters);
            main.Panel2.Controls.Add(right);

            Controls.Add(main);
            Controls.Add(_lblBanner);
            Controls.Add(_lblInfo);
            Controls.Add(_mainBar);

            // Splitter positions need a real size; set them once the control is laid out.
            void Position()
            {
                try
                {
                    main.Panel1MinSize = 200;
                    main.SplitterDistance = 250;
                    right.Panel2MinSize = 300;
                    right.SplitterDistance = Math.Max(400, right.Width - 460);
                }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
            }
            Load += (s, e) => Position();

            ResumeLayout();
        }

        /// <summary>Cards on top, a captioned sub-view below.</summary>
        private static SplitContainer Split(Control top, Control bottom, out Label caption)
        {
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5, BackColor = Color.FromArgb(0xE8, 0xEA, 0xF0) };
            split.Panel1.BackColor = split.Panel2.BackColor = Color.White;
            split.Panel1.Controls.Add(top);
            caption = new Label { Dock = DockStyle.Top, Height = 24, Padding = new Padding(8, 4, 8, 0), ForeColor = Palette.Muted, BackColor = Color.FromArgb(0xFA, 0xFA, 0xFC), AutoEllipsis = true };
            split.Panel2.Controls.Add(bottom);
            split.Panel2.Controls.Add(caption);
            split.HandleCreated += (s, e) =>
            {
                try { split.SplitterDistance = Math.Max(100, (int)(split.Height * 0.55)); }
                catch (InvalidOperationException) { }
            };
            return split;
        }

        private void LogGridCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _result.Logs.Count) return;
            var l = _result.Logs[e.RowIndex];
            switch (e.ColumnIndex)
            {
                case 0: e.Value = Formatting.Local(l.CreatedOn); break;
                case 1: e.Value = l.FullTypeName; break;
                case 2: e.Value = l.MessageName; break;
                case 3: e.Value = l.PrimaryEntity; break;
                case 4: e.Value = Formatting.Duration(l.ExecutionDurationMs); break;
                case 5: e.Value = l.Depth; break;
                case 6: e.Value = l.ModeText; break;
                case 7: e.Value = l.IsError ? "✖ " + Formatting.DisplayHeadline(ExceptionNormalizer.Headline(l.ExceptionDetails)) : "✓ Succeeded"; break;
            }
        }

        private void LogGridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _result.Logs.Count) return;
            var l = _result.Logs[e.RowIndex];
            if (l.IsError) e.CellStyle.ForeColor = Palette.Error;
            else if (e.ColumnIndex == 4 && _session.Performance.IsSlow(l)) e.CellStyle.ForeColor = Palette.FlagFore;
        }
    }
}
