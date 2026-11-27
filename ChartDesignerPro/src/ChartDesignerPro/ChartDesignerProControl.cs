using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ChartDesignerPro.Abstractions;
using ChartDesignerPro.Comparison;
using ChartDesignerPro.Controls;
using ChartDesignerPro.Data;
using ChartDesignerPro.Editing;
using ChartDesignerPro.Explorer;
using ChartDesignerPro.ImportExport;
using ChartDesignerPro.Models;
using ChartDesignerPro.Summary;
using ChartDesignerPro.Validation;
using ChartDesignerPro.Xml;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Args;
using XrmToolBox.Extensibility.Interfaces;
using Label = System.Windows.Forms.Label;
using MetadataCache = ChartDesignerPro.Explorer.MetadataCache;

namespace ChartDesignerPro
{
    /// <summary>
    /// Main UI. Chart Explorer on the left; the open chart (Summary, Data XML, Presentation XML, Validation, Diff) in the
    /// centre; its properties on the right. Milestones M1–M5: charts can be inspected, validated, imported, exported, backed
    /// up and their XML edited locally, but nothing is written to Dataverse yet (safe save arrives in M6).
    /// </summary>
    public class ChartDesignerProControl : PluginControlBase, IGitHubPlugin, IHelpPlugin, IStatusBarMessenger
    {
        private const string AppName = "Chart Designer Pro";
        private static readonly string ToolVersion = typeof(ChartDesignerProControl).Assembly.GetName().Version?.ToString() ?? "";

        // IGitHubPlugin / IHelpPlugin
        public string RepositoryName => "XrmTools";
        public string UserName => "vikashkumar669";
        public string HelpUrl => "https://github.com/vikashkumar669/XrmTools/tree/main/ChartDesignerPro#readme";

        public event EventHandler<StatusBarMessageEventArgs> SendMessageToStatusBar;

        // State
        private ToolSettings _settings = new ToolSettings();
        private readonly ILog _log;
        private IChartRepository _repository;
        private MetadataCache _metadata;
        private List<ChartRecord> _charts = new List<ChartRecord>();
        private ChartWorkspace _workspace;
        private Dictionary<string, TableMetadata> _workspaceMetadata = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
        private ChartSortColumn _sortColumn = ChartSortColumn.Name;
        private bool _sortDescending;
        private CancellationTokenSource _loadCts;
        private int _openRequest;
        private int _generation; // bumped on every connection change, so late results from the old connection are ignored
        private bool _binding;   // true while the UI is being filled from code, so change events don't act

        // UI: main toolbar
        private ToolStrip _toolbar;
        private ToolStripButton _tsbLoad, _tsbCancel, _tsbSystem, _tsbPersonal;
        private ToolStripComboBox _cboTable, _cboView;
        private ToolStripTextBox _txtSearch;
        private Label _lblEnvironment;
        private Banner _banner;

        // UI: explorer
        private FastGrid _grid;
        private Label _lblCount;

        // UI: open chart
        private Label _lblTitle;
        private ToolStrip _chartBar;
        private ToolStripButton _tsbFavourite, _tsbEdit, _tsbDiscard, _tsbFormat, _tsbFind, _tsbIndent, _tsbImport, _tsbExportList;
        private ToolStripDropDownButton _tsbExport;
        private Banner _chartBanner;
        private TabControl _tabs;
        private TabPage _tabSummary, _tabData, _tabPresentation;
        private Label _lblSentence, _lblNotes;
        private FastGrid _facts;
        private XmlEditorBox _dataEditor, _presentationEditor;
        private Label _lblDataStatus, _lblPresentationStatus;
        private FastGrid _properties;
        private System.Windows.Forms.Timer _searchTimer, _summaryTimer;

        // UI: validation and diff
        private TabPage _tabValidation, _tabDiff;
        private FastGrid _validationGrid;
        private Label _lblValidationSummary, _lblValidationDetails;
        private CheckBox _chkErrors, _chkWarnings, _chkInfo, _chkPassed;
        private ValidationReport _report;
        private DiffView _diffView;
        private RadioButton _rdoDiffData, _rdoDiffPresentation;
        private CheckBox _chkIgnoreFormatting, _chkChangesOnly;
        private Label _lblDiffSummary;

        public ChartDesignerProControl()
        {
            _log = new PluginLog((m, a) => LogInfo(m, a), (m, a) => LogWarning(m, a), (m, a) => LogError(m, a));
            BuildUi();
            ShowNoChart();
            UpdateEnvironment();
            UpdateButtons();
        }

        // ============================== Connection, closing & settings ==============================

        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            var hadChanges = _workspace?.IsDirty == true;
            base.UpdateConnection(newService, detail, actionName, parameter);
            _generation++;
            _openRequest++;
            _loadCts?.Cancel();
            _settings = LoadSettings();
            _repository = newService == null ? null : new DataverseChartRepository(newService, _log);
            _metadata = newService == null ? null
                : new MetadataCache(new DataverseMetadataService(newService), TimeSpan.FromMinutes(Math.Max(1, _settings.MetadataCacheMinutes)));
            _charts = new List<ChartRecord>();
            ApplySettingsToToolbar();
            BindTables();
            BindGrid();
            ShowNoChart();
            UpdateEnvironment();
            UpdateButtons();
            _banner.Show(Banner.Kind.Info, newService == null ? null :
                (hadChanges ? "The connection changed, so your unsaved XML edits were discarded. " : "") +
                "Connected. Click ▶ Load charts (F5) to list the charts in this environment.");
        }

        /// <summary>Asks before closing the tool (or XrmToolBox) when there are unsaved XML edits.</summary>
        public override void ClosingPlugin(PluginCloseInfo info)
        {
            if (_workspace?.IsDirty == true && !ConfirmDiscard("close Chart Designer Pro"))
            {
                info.Cancel = true;
                return;
            }
            base.ClosingPlugin(info);
        }

        private string SettingsKey => ConnectionDetail?.ConnectionId?.ToString() ?? "default";

        private ToolSettings LoadSettings()
        {
            try
            {
                if (SettingsManager.Instance.TryLoad(typeof(ChartDesignerProPlugin), out ToolSettings saved, SettingsKey) && saved != null)
                    return saved;
            }
            catch (Exception ex)
            {
                LogWarning("Couldn't read saved settings: {0}", ex.Message);
            }
            return new ToolSettings();
        }

        private void SaveSettings()
        {
            try { SettingsManager.Instance.Save(typeof(ChartDesignerProPlugin), _settings, SettingsKey); }
            catch (Exception ex) { LogWarning("Couldn't save settings: {0}", ex.Message); }
        }

        private void ApplySettingsToToolbar()
        {
            _binding = true;
            try
            {
                _tsbSystem.Checked = _settings.IncludeSystem;
                _tsbPersonal.Checked = _settings.IncludePersonal;
                _tsbIndent.Checked = _settings.FormattedView;
            }
            finally { _binding = false; }
        }

        /// <summary>Handles a user change to a toolbar setting; ignored while the toolbar is being filled from code.</summary>
        private void OnSettingChanged(Action apply, Action refresh)
        {
            if (_binding) return;
            apply();
            SaveSettings();
            refresh();
        }

        private static string EnvironmentUrl(ConnectionDetail detail)
        {
            if (detail == null) return null;
            try { return detail.WebApplicationUrl ?? detail.OriginalUrl; }
            catch { return null; }
        }

        private void UpdateEnvironment()
        {
            var detail = ConnectionDetail;
            if (detail == null)
            {
                _lblEnvironment.Text = "Not connected: use Connect in XrmToolBox";
                return;
            }
            var mode = _workspace?.IsEditing == true
                ? "EDITING LOCALLY · changes are not saved to Dataverse in this version"
                : "READ-ONLY";
            _lblEnvironment.Text = $"{detail.ConnectionName}   ·   {EnvironmentUrl(detail)}   ·   {mode}";
            _lblEnvironment.BackColor = _workspace?.IsEditing == true ? Palette.WarnBack : Palette.BrandLight;
            _lblEnvironment.ForeColor = _workspace?.IsEditing == true ? Palette.WarnFore : Palette.Brand;
        }

        private void UpdateButtons()
        {
            var connected = Service != null;
            var busy = _loadCts != null;
            _tsbLoad.Enabled = connected && !busy;
            _tsbCancel.Enabled = busy;
            _cboTable.Enabled = connected && !busy;

            var open = _workspace != null;
            var editing = open && _workspace.IsEditing;
            _tsbFavourite.Enabled = open;
            _tsbFavourite.Text = open && ChartBookmarks.IsFavourite(_settings, _workspace.Record) ? "★ Favourite" : "☆ Favourite";
            _tsbEdit.Enabled = open && !editing && _workspace.Capability.CanEdit;
            _tsbEdit.ToolTipText = open && !_workspace.Capability.CanEdit ? _workspace.Capability.Reason : "Edit the XML of this chart (Ctrl+E). Changes stay local in this version.";
            _tsbDiscard.Enabled = editing;
            _tsbFormat.Enabled = editing && ActiveEditor() != null;
            _tsbFind.Enabled = open && ActiveEditor() != null;
            _tsbIndent.Enabled = open && !editing;
            _tsbImport.Enabled = open && _workspace.Capability.CanEdit;
            _tsbImport.ToolTipText = open && !_workspace.Capability.CanEdit ? _workspace.Capability.Reason
                : "Load a chart file (.json or .xml) into this chart as edits you can review in Validation and Diff";
            _tsbExport.Enabled = open;
            _tsbExportList.Enabled = _grid.Rows.Count > 0;
        }

        // ============================== Load charts ==============================

        private void LoadCharts()
        {
            if (_repository == null) return;
            if (!_settings.IncludeSystem && !_settings.IncludePersonal)
            {
                _banner.Show(Banner.Kind.Warning, "Turn on System, Personal or both before loading.");
                return;
            }

            var query = new ChartQuery
            {
                TableLogicalName = SelectedTable(),
                IncludeSystem = _settings.IncludeSystem,
                IncludePersonal = _settings.IncludePersonal
            };
            var repository = _repository;
            var metadata = _metadata;
            var cts = new CancellationTokenSource();
            var generation = _generation;
            _loadCts = cts;
            UpdateButtons();
            _banner.Show(Banner.Kind.Info, null);

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading charts...",
                IsCancelable = true,
                Work = (worker, args) =>
                {
                    var result = repository.ListCharts(query, cts.Token, new SyncProgress(m => worker.ReportProgress(0, m)));

                    // Table display names: helpful, not essential. A failure here only costs the nicer names.
                    try
                    {
                        worker.ReportProgress(0, "Reading table names...");
                        var names = metadata.ListTables(cts.Token).ToDictionary(t => t.LogicalName, t => t.DisplayName, StringComparer.OrdinalIgnoreCase);
                        foreach (var c in result.Charts)
                            if (c.TableLogicalName != null && names.TryGetValue(c.TableLogicalName, out var display)) c.TableDisplayName = display;
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add("Table display names couldn't be read, so logical names are shown: " + ex.Message);
                    }
                    args.Result = result;
                },
                ProgressChanged = e => { if (e.UserState is string m) SetWorkingMessage(m); },
                PostWorkCallBack = e =>
                {
                    var cancelled = cts.IsCancellationRequested;
                    if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
                    cts.Dispose();
                    UpdateButtons();
                    if (generation != _generation) return; // the connection changed while loading

                    if (e.Error != null)
                    {
                        ShowError("Charts couldn't be loaded", e.Error,
                            "Check that your security role can read System Charts (savedqueryvisualization), then try again.");
                        return;
                    }

                    var result = (ChartListResult)e.Result;
                    _charts = result.Charts;
                    BindTables();
                    BindGrid();

                    var system = _charts.Count(c => c.Scope == ChartScope.System);
                    var personal = _charts.Count(c => c.Scope == ChartScope.Personal);
                    var summary = $"Loaded {system:N0} system and {personal:N0} personal charts ({query.Describe()}).";
                    _log.Info("{0}", summary);
                    Status(summary);

                    if (cancelled)
                        _banner.Show(Banner.Kind.Warning, "Loading was cancelled: the list may be incomplete. " + summary);
                    else if (result.Warnings.Count > 0)
                        _banner.Show(Banner.Kind.Warning, string.Join("  ", result.Warnings));
                    else
                        _banner.Show(Banner.Kind.Ok, summary);
                }
            });
        }

        private void CancelLoad()
        {
            _loadCts?.Cancel();
            CancelWorker();
            Status("Cancelling after the current page...");
        }

        /// <summary>Progress that reports synchronously on the worker thread (System.Progress posts to a captured context).</summary>
        private sealed class SyncProgress : IProgress<string>
        {
            private readonly Action<string> _report;
            public SyncProgress(Action<string> report) { _report = report; }
            public void Report(string value) => _report(value);
        }

        // ============================== Explorer grid ==============================

        private string SelectedTable() => (_cboTable.SelectedItem as TableSummary)?.LogicalName;

        private ChartListView SelectedView() =>
            _cboView.SelectedIndex == 1 ? ChartListView.Favourites : _cboView.SelectedIndex == 2 ? ChartListView.Recent : ChartListView.All;

        private void BindTables()
        {
            var keep = SelectedTable() ?? _settings.LastTable;
            _binding = true;
            try
            {
                _cboTable.Items.Clear();
                _cboTable.Items.Add(new TableSummary { LogicalName = null, DisplayName = "(All tables)" });
                foreach (var t in ChartListFilter.TablesOf(_charts)) _cboTable.Items.Add(t);
                if (!string.IsNullOrEmpty(keep) && !_cboTable.Items.Cast<TableSummary>().Any(t => string.Equals(t.LogicalName, keep, StringComparison.OrdinalIgnoreCase)))
                    _cboTable.Items.Add(new TableSummary { LogicalName = keep, DisplayName = keep });
                _cboTable.SelectedItem = _cboTable.Items.Cast<TableSummary>()
                    .FirstOrDefault(t => string.Equals(t.LogicalName, keep, StringComparison.OrdinalIgnoreCase)) ?? _cboTable.Items[0];
            }
            finally { _binding = false; }
        }

        private void BindGrid()
        {
            var table = SelectedTable();
            var view = SelectedView();
            var visible = _charts
                .Where(c => table == null || string.Equals(c.TableLogicalName, table, StringComparison.OrdinalIgnoreCase))
                .Where(c => (c.Scope == ChartScope.System && _settings.IncludeSystem) || (c.Scope == ChartScope.Personal && _settings.IncludePersonal));
            visible = ChartBookmarks.Apply(ChartListFilter.Search(visible, _txtSearch.Text), view, _settings);
            // The Recent view keeps recency order; the others use the column sort.
            var rows = view == ChartListView.Recent ? visible.ToList() : ChartListFilter.Sort(visible, _sortColumn, _sortDescending);

            var openId = _workspace?.Record.Id;
            _binding = true;
            try
            {
                _grid.SuspendLayout();
                _grid.Rows.Clear();
                foreach (var c in rows)
                {
                    var i = _grid.Rows.Add(ChartBookmarks.IsFavourite(_settings, c) ? "★" : "",
                        c.Name, c.TableText, c.ScopeText, c.EngineText,
                        c.IsDefault ? "Yes" : "",
                        c.IsManaged == null ? "" : c.IsManaged == true ? "Managed" : "Unmanaged",
                        c.ModifiedOnUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "");
                    var row = _grid.Rows[i];
                    row.Tag = c;
                    if (!ChartCapability.For(c).CanEdit) row.DefaultCellStyle.ForeColor = Palette.Muted;
                    row.Cells[1].ToolTipText = c.Description;
                }
                // Rows.Add selects the first row by itself: select only the open chart (or nothing), so clicking any row opens it.
                _grid.ClearSelection();
                foreach (DataGridViewRow row in _grid.Rows)
                    if (openId != null && row.Tag is ChartRecord c && c.Id == openId) { row.Selected = true; break; }
                _grid.ResumeLayout();
            }
            finally { _binding = false; }

            foreach (DataGridViewColumn col in _grid.Columns.Cast<DataGridViewColumn>().Where(c => c.SortMode == DataGridViewColumnSortMode.Programmatic))
                col.HeaderCell.SortGlyphDirection = view != ChartListView.Recent && (ChartSortColumn)col.Tag == _sortColumn
                    ? (_sortDescending ? SortOrder.Descending : SortOrder.Ascending)
                    : SortOrder.None;

            if (_tsbExportList != null) _tsbExportList.Enabled = _grid.Rows.Count > 0;
            _lblCount.Text = _charts.Count == 0
                ? "No charts loaded"
                : view == ChartListView.Favourites && _grid.Rows.Count == 0 ? "No favourites yet: open a chart and click ☆ Favourite"
                : view == ChartListView.Recent && _grid.Rows.Count == 0 ? "No recently opened charts for this connection"
                : $"{_grid.Rows.Count:N0} of {_charts.Count:N0} charts";
        }

        private void OnGridHeaderClick(int columnIndex)
        {
            if (columnIndex < 0 || _grid.Columns[columnIndex].SortMode != DataGridViewColumnSortMode.Programmatic) return;
            var column = (ChartSortColumn)_grid.Columns[columnIndex].Tag;
            _sortDescending = column == _sortColumn && !_sortDescending;
            _sortColumn = column;
            BindGrid();
        }

        /// <summary>Selects the open chart's row again (after the user declined to leave it).</summary>
        private void ReselectOpenChart()
        {
            _binding = true;
            try
            {
                _grid.ClearSelection();
                foreach (DataGridViewRow row in _grid.Rows)
                    if (row.Tag is ChartRecord c && _workspace != null && c.Id == _workspace.Record.Id) { row.Selected = true; break; }
            }
            finally { _binding = false; }
        }

        // ============================== Open a chart ==============================

        private void OpenSelected()
        {
            if (_binding) return;
            if (_grid.SelectedRows.Count == 0 || !(_grid.SelectedRows[0].Tag is ChartRecord row)) return;
            if (_workspace != null && _workspace.Record.Id == row.Id) return;
            if (_repository == null) return;
            if (_workspace?.IsDirty == true && !ConfirmDiscard($"open '{row.Name}'"))
            {
                ReselectOpenChart();
                return;
            }

            var request = ++_openRequest;
            var repository = _repository;
            var metadata = _metadata;
            WorkAsync(new WorkAsyncInfo
            {
                Message = $"Opening '{row.Name}'...",
                Work = (worker, args) =>
                {
                    var chart = repository.Retrieve(row.Scope, row.Id);
                    var tables = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
                    if (chart != null && metadata != null)
                    {
                        // Metadata for the summary: the chart's table and any linked tables. Failures only cost display names.
                        var names = new List<string> { chart.TableLogicalName };
                        if (SafeXml.TryParse(chart.DataXml, out var doc, out _))
                            names.AddRange(ChartDefinitionParser.ParseData(doc).Fetches.SelectMany(f => f.Links.Select(l => l.Name).Append(f.EntityName)));
                        foreach (var name in names.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            try
                            {
                                worker.ReportProgress(0, $"Reading metadata for {name}...");
                                var t = metadata.GetTable(name);
                                if (t != null) tables[name] = t;
                            }
                            catch (Exception ex) { _log.Warn("Metadata for {0} couldn't be read: {1}", name, ex.Message); }
                        }
                    }
                    args.Result = Tuple.Create(chart, tables);
                },
                ProgressChanged = e => { if (e.UserState is string m) SetWorkingMessage(m); },
                PostWorkCallBack = e =>
                {
                    if (request != _openRequest) return; // a newer selection superseded this one
                    if (e.Error != null)
                    {
                        ShowError($"'{row.Name}' couldn't be opened", e.Error, "You may not have read access to this chart.");
                        return;
                    }
                    var result = (Tuple<ChartRecord, Dictionary<string, TableMetadata>>)e.Result;
                    if (result.Item1 == null)
                    {
                        _banner.Show(Banner.Kind.Warning, $"'{row.Name}' no longer exists in this environment. Reload the list (F5).");
                        return;
                    }
                    result.Item1.TableDisplayName = row.TableDisplayName;
                    ShowChart(result.Item1, result.Item2);
                }
            });
        }

        private void ShowNoChart()
        {
            _workspace = null;
            _workspaceMetadata = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
            _lblTitle.Text = "No chart open";
            _chartBanner.Show(Banner.Kind.Info, "Select a chart in the list to inspect it.");
            _dataEditor.ReadOnly = _presentationEditor.ReadOnly = true;
            _dataEditor.LoadText(string.Empty);
            _presentationEditor.LoadText(string.Empty);
            _lblDataStatus.Text = _lblPresentationStatus.Text = "";
            _lblSentence.Text = "";
            _lblNotes.Text = "";
            _facts.Rows.Clear();
            _properties.Rows.Clear();
            _report = null;
            _validationGrid.Rows.Clear();
            _lblValidationSummary.Text = "Open a chart to validate it.";
            _lblValidationDetails.Text = "";
            _diffView.ShowMessage("Open a chart to see its changes.");
            _lblDiffSummary.Text = "";
            UpdateTabTitles();
            UpdateButtons();
        }

        private void ShowChart(ChartRecord chart, Dictionary<string, TableMetadata> metadata)
        {
            _workspace = new ChartWorkspace(chart);
            _workspaceMetadata = metadata ?? new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(chart.TableDisplayName) && chart.TableLogicalName != null && _workspaceMetadata.TryGetValue(chart.TableLogicalName, out var t))
                chart.TableDisplayName = t.DisplayName;

            ChartBookmarks.AddRecent(_settings, chart);
            SaveSettings();

            LoadEditors();
            BindProperties(chart);
            RefreshSummary();
            RunValidation();
            RefreshDiff();
            UpdateChartBanner();
            UpdateTitle();
            UpdateEnvironment();
            UpdateButtons();
            if (SelectedView() == ChartListView.Recent) BindGrid();
            Status($"Opened '{chart.Name}'");
        }

        private void UpdateTitle()
        {
            if (_workspace == null) return;
            _lblTitle.Text = $"{_workspace.Record.TableText}  ›  {_workspace.Record.Name}" + (_workspace.IsDirty ? "   ● unsaved changes" : "");
            _lblTitle.ForeColor = _workspace.IsDirty ? Palette.WarnFore : Palette.Ink;
        }

        private void UpdateChartBanner()
        {
            if (_workspace == null) return;
            var cap = _workspace.Capability;
            if (!cap.CanEdit) _chartBanner.Show(Banner.Kind.Warning, "Read-only: " + cap.Reason);
            else if (_workspace.IsEditing)
                _chartBanner.Show(Banner.Kind.Warning, "Editing locally. Saving to Dataverse arrives in a later milestone: your changes are kept only until you discard them, open another chart or close the tool (you'll be asked first).");
            else
                _chartBanner.Show(Banner.Kind.Info, "Read-only. Click ✎ Edit to change the XML locally." + (cap.Reason == null ? "" : "  " + cap.Reason));
        }

        /// <summary>Fills both editors from the workspace. Read-only mode can show indented XML (display only); edit mode shows the exact text.</summary>
        private void LoadEditors()
        {
            foreach (var kind in new[] { DocumentKind.Data, DocumentKind.Presentation })
            {
                var doc = _workspace.Document(kind);
                var editor = EditorFor(kind);
                var text = doc.Current;
                if (!_workspace.IsEditing && _settings.FormattedView) text = SafeXml.Format(doc.Current) ?? doc.Current;
                _binding = true;
                try
                {
                    editor.ReadOnly = !_workspace.IsEditing;
                    editor.LoadText(text);
                }
                finally { _binding = false; }
                UpdateDocumentStatus(kind);
            }
            UpdateTabTitles();
        }

        private XmlEditorBox EditorFor(DocumentKind kind) => kind == DocumentKind.Data ? _dataEditor : _presentationEditor;
        private Label StatusFor(DocumentKind kind) => kind == DocumentKind.Data ? _lblDataStatus : _lblPresentationStatus;

        private XmlEditorBox ActiveEditor() =>
            _tabs.SelectedTab == _tabData ? _dataEditor : _tabs.SelectedTab == _tabPresentation ? _presentationEditor : null;

        private void OnEditorTextChanged(DocumentKind kind)
        {
            if (_binding || _workspace == null || !_workspace.IsEditing) return;
            _workspace.Document(kind).Current = EditorFor(kind).EditorText;
            UpdateDocumentStatus(kind);
            UpdateTabTitles();
            UpdateTitle();
            _summaryTimer.Stop();
            _summaryTimer.Start();
        }

        private void UpdateDocumentStatus(DocumentKind kind)
        {
            var label = StatusFor(kind);
            if (_workspace == null) { label.Text = ""; return; }
            var doc = _workspace.Document(kind);
            var editor = EditorFor(kind);
            var caret = editor.Caret;
            var position = $"Ln {caret.Y}, Col {caret.X}";
            label.Tag = null;
            label.Cursor = Cursors.Default;

            if (string.IsNullOrEmpty(doc.Current))
            {
                label.Text = $"{doc.Title} is empty.";
                label.ForeColor = Palette.Muted;
                return;
            }

            if (doc.Parse(out var error) != null)
            {
                var how = _workspace.IsEditing ? "editing the stored text" : _settings.FormattedView ? "shown indented (display only)" : "shown exactly as stored";
                label.Text = $"✓ Well-formed  ·  {doc.Current.Length:N0} characters  ·  {position}  ·  {how}" + (doc.IsDirty ? "  ·  ● changed" : "");
                label.ForeColor = Palette.Ok;
            }
            else
            {
                label.Text = $"✕ Not well-formed: {error}   (click to go there)  ·  {position}" + (doc.IsDirty ? "  ·  ● changed" : "");
                label.ForeColor = Palette.ErrorFore;
                label.Tag = error;
                label.Cursor = Cursors.Hand;
            }
        }

        private void UpdateTabTitles()
        {
            _tabData.Text = "Data XML" + (_workspace?.Data.IsDirty == true ? " ●" : "");
            _tabPresentation.Text = "Presentation XML" + (_workspace?.Presentation.IsDirty == true ? " ●" : "");
            if (_tabValidation != null)
            {
                var r = _workspace == null ? null : _report;
                _tabValidation.Text = r == null ? "Validation"
                    : r.Errors > 0 ? $"Validation ({r.Errors} ✕{(r.Warnings > 0 ? $", {r.Warnings} ⚠" : "")})"
                    : r.Warnings > 0 ? $"Validation ({r.Warnings} ⚠)"
                    : "Validation ✓";
                _tabDiff.Text = _workspace?.IsDirty == true ? "Diff ●" : "Diff";
            }
        }

        private void RefreshSummary()
        {
            _facts.Rows.Clear();
            if (_workspace == null) return;
            var data = _workspace.Data.Parse(out var dataError);
            var presentation = _workspace.Presentation.Parse(out _);
            if (data == null)
            {
                _lblSentence.Text = "The data XML isn't well-formed, so the chart can't be described.";
                _lblNotes.Text = dataError?.ToString() ?? "";
                return;
            }

            var table = _workspace.Record.TableLogicalName != null && _workspaceMetadata.TryGetValue(_workspace.Record.TableLogicalName, out var t) ? t : null;
            var summary = ChartDescriber.Describe(
                ChartDefinitionParser.ParseData(data),
                presentation == null ? null : ChartDefinitionParser.ParsePresentation(presentation),
                table,
                name => name != null && _workspaceMetadata.TryGetValue(name, out var linked) ? linked : null);

            _lblSentence.Text = summary.Sentence;
            FitSentence();
            foreach (var f in summary.Facts) _facts.Rows.Add(f.Label, f.Value);
            var notes = new List<string>(summary.Notes);
            if (presentation == null && !string.IsNullOrEmpty(_workspace.Presentation.Current)) notes.Add("The presentation XML isn't well-formed, so the chart type isn't described.");
            if (_workspace.IsDirty) notes.Add("This summary reflects your unsaved edits.");
            _lblNotes.Text = string.Join(Environment.NewLine, notes.Select(n => "ℹ " + n));
        }

        private void BindProperties(ChartRecord c)
        {
            _properties.Rows.Clear();
            void Add(string name, string value) { if (!string.IsNullOrEmpty(value)) _properties.Rows.Add(name, value); }
            Add("Name", c.Name);
            Add("Description", c.Description);
            Add("Table", c.TableText);
            Add("Scope", c.Scope == ChartScope.System ? "System chart (savedqueryvisualization)" : "Personal chart (userqueryvisualization)");
            Add("Type", c.EngineText);
            Add("Default chart", c.IsDefault ? "Yes" : "No");
            if (c.Scope == ChartScope.System)
            {
                Add("Managed", c.IsManaged == null ? "Unknown" : c.IsManaged == true ? "Yes" : "No");
                Add("Customizable", c.IsCustomizable == null ? "Unknown" : c.IsCustomizable == true ? "Yes" : "No");
            }
            Add("Owner", c.OwnerName);
            Add("Modified on", c.ModifiedOnUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            Add("Modified by", c.ModifiedByName);
            Add("Chart id", c.Id.ToString());
            if (c.WebResourceId.HasValue) Add("Web resource id", c.WebResourceId.Value.ToString());
            var cap = ChartCapability.For(c);
            Add("Editable", cap.CanEdit ? "Yes (locally; saving arrives in a later milestone)" : "No: " + cap.Reason);
        }

        // ============================== Validation ==============================

        /// <summary>Runs every rule on the current (possibly edited) XML, with the metadata read when the chart was opened.</summary>
        private void RunValidation()
        {
            if (_workspace == null) return;
            var table = _workspace.Record.TableLogicalName != null && _workspaceMetadata.TryGetValue(_workspace.Record.TableLogicalName, out var t) ? t : null;
            var context = ValidationContext.For(_workspace, table, name => name != null && _workspaceMetadata.TryGetValue(name, out var linked) ? linked : null);
            _report = new ValidationEngine().Run(context);
            BindValidation();
            UpdateTabTitles();
        }

        private void BindValidation()
        {
            _validationGrid.Rows.Clear();
            _lblValidationDetails.Text = "";
            if (_report == null) return;
            _lblValidationSummary.Text = _report.Summary + (_workspace?.IsDirty == true ? "  ·  validating your unsaved edits" : "");
            _lblValidationSummary.ForeColor = _report.Errors > 0 ? Palette.ErrorFore : _report.Warnings > 0 ? Palette.WarnFore : Palette.Ok;
            foreach (var r in _report.Results)
            {
                if (r.Severity == Severity.Error && !_chkErrors.Checked) continue;
                if (r.Severity == Severity.Warning && !_chkWarnings.Checked) continue;
                if (r.Severity == Severity.Information && !_chkInfo.Checked) continue;
                if (r.Severity == Severity.Passed && !_chkPassed.Checked) continue;
                var where = r.Severity == Severity.Passed ? "" :
                    (r.Target == ValidationTarget.Data ? "Data XML" : r.Target == ValidationTarget.Presentation ? "Presentation XML" : "Chart") +
                    (r.Line > 0 ? $" · line {r.Line}" : "");
                var i = _validationGrid.Rows.Add(r.SeverityText, r.RuleId, r.Title, where);
                var row = _validationGrid.Rows[i];
                row.Tag = r;
                row.Cells[0].Style.ForeColor = r.Severity == Severity.Error ? Palette.ErrorFore
                    : r.Severity == Severity.Warning ? Palette.WarnFore
                    : r.Severity == Severity.Information ? Palette.InfoFore : Palette.Ok;
            }
            _validationGrid.ClearSelection();
        }

        private void ShowValidationDetails()
        {
            if (_validationGrid.SelectedRows.Count == 0 || !(_validationGrid.SelectedRows[0].Tag is ValidationResult r))
            {
                _lblValidationDetails.Text = "";
                return;
            }
            var parts = new List<string> { $"{r.RuleId} · {r.Category} · {r.SeverityText}", r.Title };
            if (!string.IsNullOrEmpty(r.Location)) parts.Add("Where: " + r.Location + (r.Line > 0 ? $", line {r.Line}" : ""));
            if (!string.IsNullOrEmpty(r.Explanation)) parts.Add("Why: " + r.Explanation);
            if (!string.IsNullOrEmpty(r.Suggestion)) parts.Add("Suggestion: " + r.Suggestion);
            if (r.Line > 0) parts.Add("Double-click the row to go to the line.");
            _lblValidationDetails.Text = string.Join(Environment.NewLine, parts);
        }

        /// <summary>Opens the XML tab of a result and moves the caret to its line (the line refers to the stored text).</summary>
        private void GoToValidationResult()
        {
            if (_validationGrid.SelectedRows.Count == 0 || !(_validationGrid.SelectedRows[0].Tag is ValidationResult r) || r.Line <= 0) return;
            if (r.Target == ValidationTarget.Chart) return;
            var kind = r.Target == ValidationTarget.Data ? DocumentKind.Data : DocumentKind.Presentation;
            if (_workspace != null && !_workspace.IsEditing && _settings.FormattedView)
            {
                // Line numbers refer to the stored text; show it as stored so the line matches.
                _binding = true;
                try { _tsbIndent.Checked = false; } finally { _binding = false; }
                _settings.FormattedView = false;
                SaveSettings();
                LoadEditors();
                Status("Switched off 'Indent (view only)' so line numbers match the stored XML.");
            }
            _tabs.SelectedTab = kind == DocumentKind.Data ? _tabData : _tabPresentation;
            EditorFor(kind).GoTo(r.Line, Math.Max(1, r.Column));
        }

        // ============================== Diff ==============================

        private void RefreshDiff()
        {
            if (_workspace == null) return;
            var doc = _rdoDiffPresentation.Checked ? _workspace.Presentation : _workspace.Data;
            if (!doc.IsDirty)
            {
                _diffView.ShowMessage($"No changes: the {doc.Title} is exactly as stored in Dataverse.\r\n\r\nEdit the chart (✎ Edit) to see your changes here before saving.");
                _lblDiffSummary.Text = "No changes";
                _lblDiffSummary.ForeColor = Palette.Muted;
                return;
            }
            var diff = LineDiff.Compare(doc.Original, doc.Current, _chkIgnoreFormatting.Checked);
            if (!diff.HasChanges)
                _diffView.ShowMessage($"The {doc.Title} differs from the stored text only in formatting (whitespace, indentation or attribute order).\r\n\r\nClear 'Ignore formatting' to see those differences.");
            else
                _diffView.ShowDiff(diff, _chkChangesOnly.Checked);
            _lblDiffSummary.Text = $"{doc.Title}: {diff.Summary}" +
                (diff.FellBackToExact ? "  ·  one side isn't well-formed, so formatting is compared too" : "");
            _lblDiffSummary.ForeColor = diff.HasChanges ? Palette.WarnFore : Palette.Muted;
        }

        // ============================== Import / export / backup ==============================

        private enum ExportKind { Json, Xml, XmlFormatted, DataOnly, PresentationOnly }

        private string EnvironmentName => ConnectionDetail?.ConnectionName ?? "Unknown environment";

        private string BackupFolder()
        {
            string root;
            try { root = XrmToolBox.Extensibility.Paths.SettingsPath; }
            catch { root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MscrmTools", "XrmToolBox", "Settings"); }
            return Path.Combine(root, "ChartDesignerPro", "Backups");
        }

        private void ImportIntoChart()
        {
            if (_workspace == null) return;
            if (!_workspace.Capability.CanEdit)
            {
                MessageBox.Show(this, _workspace.Capability.Reason, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string path;
            using (var dlg = new OpenFileDialog { Title = "Import a chart file", Filter = "Chart files (*.json;*.xml)|*.json;*.xml|All files (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }

            ChartFileImport import;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > ChartFileFormat.MaxFileCharacters * 2L)
                    throw new ChartFileException($"The file is too large ({info.Length:N0} bytes).");
                import = ChartFileFormat.Read(File.ReadAllText(path));
            }
            catch (ChartFileException ex)
            {
                MessageBox.Show(this, ex.Message, AppName + ": import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(this, "The file couldn't be read: " + ex.Message, AppName + ": import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var f = import.File;
            var lines = new List<string> { $"File: {Path.GetFileName(path)}" };
            if (!string.IsNullOrEmpty(f.Name)) lines.Add($"Chart in the file: {f.Name}" + (string.IsNullOrEmpty(f.Table) ? "" : $" ({f.Table})"));
            if (!string.IsNullOrEmpty(f.Source?.Environment)) lines.Add($"Exported from: {f.Source.Environment}" + (string.IsNullOrEmpty(f.ExportedUtc) ? "" : $" on {f.ExportedUtc}"));
            lines.Add("");
            lines.Add($"Into the open chart: {_workspace.Record.Name} ({_workspace.Record.TableLogicalName})");
            lines.Add("Replaces: " + (import.HasData && import.HasPresentation ? "Data XML and Presentation XML" : import.HasData ? "Data XML only" : "Presentation XML only"));
            var warning = false;
            if (!string.IsNullOrEmpty(f.Table) && !string.Equals(f.Table, _workspace.Record.TableLogicalName, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add("");
                lines.Add($"⚠ The file is for table '{f.Table}', but this chart belongs to '{_workspace.Record.TableLogicalName}'. Validation will report column references that don't exist here.");
                warning = true;
            }
            foreach (var note in import.Notes) lines.Add("ℹ " + note);
            lines.Add("");
            lines.Add("The chart's name and description aren't changed. Nothing is saved to Dataverse: review the result in the Validation and Diff tabs, and use ↶ Discard changes to undo.");
            lines.Add("");
            lines.Add("Import?");
            if (MessageBox.Show(this, string.Join(Environment.NewLine, lines), AppName + ": import",
                    MessageBoxButtons.YesNo, warning ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            if (!_workspace.IsEditing) BeginEdit();
            if (!_workspace.IsEditing) return;
            if (import.HasData) _dataEditor.ReplaceAllText(f.DataXml);
            if (import.HasPresentation) _presentationEditor.ReplaceAllText(f.PresentationXml);
            RefreshSummary();
            RunValidation();
            _tabs.SelectedTab = _tabDiff;
            if (import.HasPresentation && !import.HasData) _rdoDiffPresentation.Checked = true; else _rdoDiffData.Checked = true;
            RefreshDiff();
            _log.Info("Imported {0} into chart {1} ({2})", Path.GetFileName(path), _workspace.Record.Name, _workspace.Record.Id);
            Status($"Imported {Path.GetFileName(path)}. Review the changes in Diff and Validation; nothing is saved yet.");
        }

        private void ExportChart(ExportKind kind)
        {
            if (_workspace == null) return;
            var data = _workspace.Data.Original;
            var presentation = _workspace.Presentation.Original;
            var version = "stored";
            if (_workspace.IsDirty)
            {
                var answer = MessageBox.Show(this,
                    "This chart has unsaved changes.\n\nYes: export your edited version\nNo: export the version stored in Dataverse",
                    AppName + ": export", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.Yes)
                {
                    data = _workspace.Data.Current;
                    presentation = _workspace.Presentation.Current;
                    version = "edited";
                }
            }

            var file = ChartFile.From(_workspace.Record, data, presentation, EnvironmentName, EnvironmentUrl(ConnectionDetail), ToolVersion, DateTime.UtcNow);
            string content, extension, filter, suffix = "";
            switch (kind)
            {
                case ExportKind.Json:
                    content = ChartFileFormat.WriteJson(file); extension = ".json"; filter = "Chart Designer Pro chart (*.json)|*.json"; break;
                case ExportKind.Xml:
                    content = ChartFileFormat.WriteVisualizationXml(file, indent: false); extension = ".xml"; filter = "Chart XML (*.xml)|*.xml"; break;
                case ExportKind.XmlFormatted:
                    content = ChartFileFormat.WriteVisualizationXml(file, indent: true); extension = ".xml"; filter = "Chart XML (*.xml)|*.xml"; break;
                case ExportKind.DataOnly:
                    content = data; extension = ".xml"; filter = "Data XML (*.xml)|*.xml"; suffix = " - data"; break;
                default:
                    content = presentation; extension = ".xml"; filter = "Presentation XML (*.xml)|*.xml"; suffix = " - presentation"; break;
            }

            using (var dlg = new SaveFileDialog
            {
                Title = "Export chart",
                FileName = ChartFileFormat.DefaultFileName(_workspace.Record, DateTime.Now) + suffix + extension,
                Filter = filter + "|All files (*.*)|*.*",
                OverwritePrompt = true,
                AddExtension = true
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, content ?? string.Empty, new System.Text.UTF8Encoding(false));
                    _log.Info("Exported the {0} version of chart {1} to {2}", version, _workspace.Record.Id, dlg.FileName);
                    _banner.Show(Banner.Kind.Ok, $"Exported the {version} version of '{_workspace.Record.Name}' to {dlg.FileName}");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    MessageBox.Show(this, "The file couldn't be written: " + ex.Message, AppName + ": export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ExportList()
        {
            var rows = _grid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<ChartRecord>().ToList();
            if (rows.Count == 0) return;
            using (var dlg = new SaveFileDialog
            {
                Title = "Export chart list",
                FileName = ChartFileFormat.SafeFileName(EnvironmentName, 60) + " - charts - " + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv",
                Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
                OverwritePrompt = true,
                AddExtension = true
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, InventoryCsv.Write(rows), new System.Text.UTF8Encoding(false));
                    _banner.Show(Banner.Kind.Ok, $"Exported {rows.Count:N0} charts (as filtered in the list) to {dlg.FileName}");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    MessageBox.Show(this, "The file couldn't be written: " + ex.Message, AppName + ": export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void BackupNow()
        {
            if (_workspace == null) return;
            try
            {
                var path = new BackupStore(BackupFolder()).Save(_workspace.Record, EnvironmentName, EnvironmentUrl(ConnectionDetail), ToolVersion);
                _log.Info("Backed up chart {0} to {1}", _workspace.Record.Id, path);
                _banner.Show(Banner.Kind.Ok, $"Backed up the stored version of '{_workspace.Record.Name}' to {path}");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                MessageBox.Show(this, "The backup couldn't be written: " + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"The folder couldn't be opened: {ex.Message}\n\n{folder}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ============================== Edit commands ==============================

        private void BeginEdit()
        {
            if (_workspace == null || _workspace.IsEditing) return;
            if (!_workspace.BeginEdit())
            {
                MessageBox.Show(this, _workspace.Capability.Reason, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            LoadEditors(); // switches to the exact stored text
            UpdateChartBanner();
            UpdateEnvironment();
            UpdateButtons();
            if (ActiveEditor() == null) _tabs.SelectedTab = _tabPresentation;
            ActiveEditor()?.Focus();
            Status("Editing locally. Ctrl+Z / Ctrl+Y undo and redo, Ctrl+F find, Ctrl+H replace, Ctrl+Shift+F format.");
        }

        private void DiscardEdits()
        {
            if (_workspace == null || !_workspace.IsEditing) return;
            if (_workspace.IsDirty && !ConfirmDiscard("stop editing")) return;
            _workspace.DiscardChanges();
            LoadEditors();
            RefreshSummary();
            RunValidation();
            RefreshDiff();
            UpdateChartBanner();
            UpdateTitle();
            UpdateEnvironment();
            UpdateButtons();
            Status("Changes discarded");
        }

        private void FormatActive()
        {
            var editor = ActiveEditor();
            if (editor == null || _workspace?.IsEditing != true) return;
            if (!editor.FormatXml())
                MessageBox.Show(this, "The XML isn't well-formed, so it can't be formatted. Fix the error shown below the editor first.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                Status("Formatted. This is a change to the stored text; Ctrl+Z undoes it.");
        }

        private void ToggleFavourite()
        {
            if (_workspace == null) return;
            var now = ChartBookmarks.ToggleFavourite(_settings, _workspace.Record);
            SaveSettings();
            BindGrid();
            UpdateButtons();
            Status(now ? $"'{_workspace.Record.Name}' added to favourites" : $"'{_workspace.Record.Name}' removed from favourites");
        }

        /// <summary>Asks whether to throw away unsaved edits. Returns true when the user agrees.</summary>
        private bool ConfirmDiscard(string action)
        {
            var answer = MessageBox.Show(this,
                $"'{_workspace.Record.Name}' has unsaved changes ({_workspace.ChangeSummary().ToLowerInvariant()}).\n\n" +
                $"This version can't save to Dataverse yet. Discard the changes and {action}?",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            return answer == DialogResult.Yes;
        }

        // ============================== Helpers ==============================

        private void Status(string message) => SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs(message));

        /// <summary>Plain-language message for the user; the full exception goes to the XrmToolBox log only.</summary>
        private void ShowError(string title, Exception ex, string hint)
        {
            var inner = ex;
            while (inner.InnerException != null && inner is System.Reflection.TargetInvocationException) inner = inner.InnerException;
            _log.Error(inner, "{0}", title);
            var message = inner is System.ServiceModel.FaultException<OrganizationServiceFault> fault ? fault.Detail?.Message ?? fault.Message : inner.Message;
            _banner.Show(Banner.Kind.Error, $"{title}: {message}");
            MessageBox.Show(this, $"{title}.\n\n{message}\n\n{hint}\n\nTechnical details are in the XrmToolBox log (Help › Open log file).",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var editor = ActiveEditor();
            switch (keyData)
            {
                case Keys.F5:
                    if (_tsbLoad.Enabled) ExecuteMethod(LoadCharts);
                    return true;
                case Keys.Control | Keys.F:
                    if (editor != null && (editor.Focused || _tabs.ContainsFocus)) editor.ShowFind(false);
                    else { _txtSearch.Focus(); _txtSearch.SelectAll(); }
                    return true;
                case Keys.Control | Keys.H:
                    if (editor != null) { editor.ShowFind(true); return true; }
                    break;
                case Keys.Control | Keys.E:
                    BeginEdit();
                    return true;
                case Keys.Control | Keys.Shift | Keys.F:
                    FormatActive();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _loadCts?.Cancel();
                _searchTimer?.Dispose();
                _summaryTimer?.Dispose();
            }
            base.Dispose(disposing);
        }

        // ============================== UI construction ==============================

        private void BuildUi()
        {
            SuspendLayout();
            Font = new Font("Segoe UI", 9F);
            Size = new Size(1300, 800);
            BackColor = Color.White;

            // ---- Main toolbar ----
            _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2), RenderMode = ToolStripRenderMode.System };
            var tsbClose = ToolStripFactory.Button("Close", "Close this tool", (s, e) => CloseTool());
            _tsbLoad = ToolStripFactory.Button("▶ Load charts", "List the charts for the selected table (F5)", (s, e) => ExecuteMethod(LoadCharts), true);
            _tsbLoad.ForeColor = Palette.Brand;
            _tsbCancel = ToolStripFactory.Button("■ Cancel", "Stop loading after the current page", (s, e) => CancelLoad());
            _cboTable = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 220, ToolTipText = "Table filter. Loading with a table selected only asks Dataverse for that table's charts." };
            _cboTable.SelectedIndexChanged += (s, e) => OnSettingChanged(() => _settings.LastTable = SelectedTable(), BindGrid);
            _cboView = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 120, ToolTipText = "Show all charts, your favourites, or the charts you opened recently (per connection)" };
            _cboView.Items.AddRange(new object[] { "All charts", "★ Favourites", "Recent" });
            _cboView.SelectedIndex = 0;
            _cboView.SelectedIndexChanged += (s, e) => { if (!_binding) BindGrid(); };
            _txtSearch = new ToolStripTextBox { AutoSize = false, Width = 200, ToolTipText = "Search by name, description, table, type or id (Ctrl+F)" };
            _searchTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); BindGrid(); };
            _txtSearch.TextChanged += (s, e) => { _searchTimer.Stop(); _searchTimer.Start(); };
            _tsbSystem = ToolStripFactory.Toggle("System", "Include system charts (savedqueryvisualization)", true, v => OnSettingChanged(() => _settings.IncludeSystem = v, BindGrid));
            _tsbPersonal = ToolStripFactory.Toggle("Personal", "Include personal charts you own or that are shared with you (userqueryvisualization)", true, v => OnSettingChanged(() => _settings.IncludePersonal = v, BindGrid));
            _tsbExportList = ToolStripFactory.Button("⇩ Chart list (CSV)", "Export the charts shown in the list as a CSV file", (s, e) => ExportList());
            var tsbRefreshMetadata = ToolStripFactory.Button("⟳ Metadata", "Forget cached table metadata so it is read again", (s, e) =>
            {
                _metadata?.Clear();
                Status("Metadata cache cleared");
            });

            _toolbar.Items.AddRange(new ToolStripItem[]
            {
                tsbClose, new ToolStripSeparator(), _tsbLoad, _tsbCancel, new ToolStripSeparator(),
                new ToolStripLabel("Table:"), _cboTable, new ToolStripLabel("Show:"), _cboView,
                new ToolStripLabel("Search:"), _txtSearch, new ToolStripSeparator(),
                _tsbSystem, _tsbPersonal, new ToolStripSeparator(), tsbRefreshMetadata, new ToolStripSeparator(), _tsbExportList
            });

            _lblEnvironment = new Label
            {
                Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 5, 8, 0), ForeColor = Palette.Brand, BackColor = Palette.BrandLight,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold), AutoEllipsis = true
            };
            _banner = new Banner();

            // ---- Left: Chart Explorer ----
            var explorer = new Panel { Dock = DockStyle.Fill };
            var explorerHeader = new Label { Dock = DockStyle.Top, Height = 26, Text = "CHART EXPLORER", Padding = new Padding(8, 6, 0, 0), Font = new Font("Segoe UI", 8.5F, FontStyle.Bold), ForeColor = Palette.Muted };
            _lblCount = new Label { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(8, 3, 0, 0), ForeColor = Palette.Muted };
            _grid = new FastGrid { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None };
            AddColumn(_grid, "★", 26, ChartSortColumn.Name, sortable: false);
            AddColumn(_grid, "Name", 220, ChartSortColumn.Name);
            AddColumn(_grid, "Table", 150, ChartSortColumn.Table);
            AddColumn(_grid, "Scope", 70, ChartSortColumn.Scope);
            AddColumn(_grid, "Type", 80, ChartSortColumn.Type);
            AddColumn(_grid, "Default", 55, ChartSortColumn.Name, sortable: false);
            AddColumn(_grid, "Managed", 80, ChartSortColumn.Name, sortable: false);
            AddColumn(_grid, "Modified", 115, ChartSortColumn.Modified);
            _grid.Columns[0].DefaultCellStyle.ForeColor = Palette.Accent;
            _grid.Columns[0].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.ColumnHeaderMouseClick += (s, e) => OnGridHeaderClick(e.ColumnIndex);
            _grid.SelectionChanged += (s, e) => { if (_grid.Focused) OpenSelected(); };
            _grid.CellDoubleClick += (s, e) => OpenSelected();
            _grid.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; } };
            explorer.Controls.Add(_grid);
            explorer.Controls.Add(_lblCount);
            explorer.Controls.Add(explorerHeader);

            // ---- Centre: open chart ----
            var centre = new Panel { Dock = DockStyle.Fill };
            _lblTitle = new Label { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 8, 8, 0), Font = new Font("Segoe UI", 11F, FontStyle.Bold), ForeColor = Palette.Ink, AutoEllipsis = true };
            _chartBar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 0, 4, 2), RenderMode = ToolStripRenderMode.System, Dock = DockStyle.Top };
            _tsbFavourite = ToolStripFactory.Button("☆ Favourite", "Add or remove this chart from your favourites", (s, e) => ToggleFavourite());
            _tsbEdit = ToolStripFactory.Button("✎ Edit", "Edit the XML of this chart (Ctrl+E)", (s, e) => BeginEdit(), true);
            _tsbDiscard = ToolStripFactory.Button("↶ Discard changes", "Throw away your edits and return to read-only", (s, e) => DiscardEdits());
            _tsbFormat = ToolStripFactory.Button("Format XML", "Indent the XML in the current tab (Ctrl+Shift+F). This changes the stored text.", (s, e) => FormatActive());
            _tsbFind = ToolStripFactory.Button("Find / Replace", "Find (Ctrl+F) and replace (Ctrl+H) in the current XML tab", (s, e) => ActiveEditor()?.ShowFind(_workspace?.IsEditing == true));
            _tsbIndent = ToolStripFactory.Toggle("Indent (view only)", "Show the XML indented while read-only. Display only: the stored XML is never changed.", true,
                v => OnSettingChanged(() => _settings.FormattedView = v, () => { if (_workspace != null && !_workspace.IsEditing) LoadEditors(); }));
            _tsbImport = ToolStripFactory.Button("⇧ Import…", "Load a chart file into this chart as edits", (s, e) => ImportIntoChart());
            _tsbExport = new ToolStripDropDownButton("⇩ Export") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Save this chart to a file, or back it up" };
            _tsbExport.DropDownItems.Add("Chart file (.json) – Chart Designer Pro format", null, (s, e) => ExportChart(ExportKind.Json));
            _tsbExport.DropDownItems.Add("Chart XML (.xml)", null, (s, e) => ExportChart(ExportKind.Xml));
            _tsbExport.DropDownItems.Add("Chart XML, formatted (.xml)", null, (s, e) => ExportChart(ExportKind.XmlFormatted));
            _tsbExport.DropDownItems.Add(new ToolStripSeparator());
            _tsbExport.DropDownItems.Add("Data XML only (.xml)", null, (s, e) => ExportChart(ExportKind.DataOnly));
            _tsbExport.DropDownItems.Add("Presentation XML only (.xml)", null, (s, e) => ExportChart(ExportKind.PresentationOnly));
            _tsbExport.DropDownItems.Add(new ToolStripSeparator());
            _tsbExport.DropDownItems.Add("Back up the stored version now", null, (s, e) => BackupNow());
            _tsbExport.DropDownItems.Add("Open backup folder", null, (s, e) => OpenFolder(BackupFolder()));
            _chartBar.Items.AddRange(new ToolStripItem[] { _tsbFavourite, new ToolStripSeparator(), _tsbEdit, _tsbDiscard, new ToolStripSeparator(), _tsbFormat, _tsbFind, new ToolStripSeparator(), _tsbImport, _tsbExport, new ToolStripSeparator(), _tsbIndent });
            _chartBanner = new Banner();

            _tabs = new TabControl { Dock = DockStyle.Fill };
            _tabSummary = new TabPage("Summary") { BackColor = Color.White, ToolTipText = "What the chart shows, in plain words", Padding = new Padding(10) };
            _lblSentence = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 56, Font = new Font("Segoe UI", 11F), ForeColor = Palette.Ink, Padding = new Padding(2, 4, 2, 4) };
            _facts = new FastGrid { Dock = DockStyle.Fill, ColumnHeadersVisible = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
            _facts.Columns.Add(new DataGridViewTextBoxColumn { Name = "Fact", Width = 190, DefaultCellStyle = { ForeColor = Palette.Muted } });
            _facts.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DefaultCellStyle = { WrapMode = DataGridViewTriState.True } });
            _facts.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _lblNotes = new Label { Dock = DockStyle.Bottom, Height = 90, ForeColor = Palette.InfoFore, Padding = new Padding(2, 6, 2, 0) };
            _tabSummary.Controls.Add(_facts);
            _tabSummary.Controls.Add(_lblNotes);
            _tabSummary.Controls.Add(_lblSentence);
            _tabSummary.Resize += (s, e) => FitSentence();

            _dataEditor = new XmlEditorBox { Dock = DockStyle.Fill };
            _presentationEditor = new XmlEditorBox { Dock = DockStyle.Fill };
            _lblDataStatus = StatusLabel(DocumentKind.Data);
            _lblPresentationStatus = StatusLabel(DocumentKind.Presentation);
            _dataEditor.EditorTextChanged += (s, e) => OnEditorTextChanged(DocumentKind.Data);
            _presentationEditor.EditorTextChanged += (s, e) => OnEditorTextChanged(DocumentKind.Presentation);
            _dataEditor.CaretMoved += (s, e) => { if (!_binding) UpdateDocumentStatus(DocumentKind.Data); };
            _presentationEditor.CaretMoved += (s, e) => { if (!_binding) UpdateDocumentStatus(DocumentKind.Presentation); };
            _tabData = XmlTab("Data XML", "Underlying data: FetchXML, categories and measures (datadescription)", _dataEditor, _lblDataStatus);
            _tabPresentation = XmlTab("Presentation XML", "Appearance: chart type, colours, labels, axes (presentationdescription)", _presentationEditor, _lblPresentationStatus);
            BuildValidationTab();
            BuildDiffTab();
            _tabs.TabPages.AddRange(new[] { _tabSummary, _tabData, _tabPresentation, _tabValidation, _tabDiff });
            _tabs.SelectedIndexChanged += (s, e) =>
            {
                if (_tabs.SelectedTab == _tabSummary) RefreshSummary();
                if (_tabs.SelectedTab == _tabDiff) RefreshDiff();
                UpdateButtons();
            };

            _summaryTimer = new System.Windows.Forms.Timer { Interval = 800 };
            _summaryTimer.Tick += (s, e) =>
            {
                _summaryTimer.Stop();
                RefreshSummary();
                RunValidation();
                if (_tabs.SelectedTab == _tabDiff) RefreshDiff();
            };

            centre.Controls.Add(_tabs);
            centre.Controls.Add(_chartBanner);
            centre.Controls.Add(_chartBar);
            centre.Controls.Add(_lblTitle);

            // ---- Right: properties ----
            var right = new Panel { Dock = DockStyle.Fill };
            var rightHeader = new Label { Dock = DockStyle.Top, Height = 26, Text = "PROPERTIES", Padding = new Padding(8, 6, 0, 0), Font = new Font("Segoe UI", 8.5F, FontStyle.Bold), ForeColor = Palette.Muted };
            _properties = new FastGrid { Dock = DockStyle.Fill, ColumnHeadersVisible = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
            _properties.Columns.Add(new DataGridViewTextBoxColumn { Name = "Property", Width = 105, DefaultCellStyle = { ForeColor = Palette.Muted } });
            _properties.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DefaultCellStyle = { WrapMode = DataGridViewTriState.True } });
            _properties.SelectionMode = DataGridViewSelectionMode.CellSelect; // so a value can be copied with Ctrl+C
            right.Controls.Add(_properties);
            right.Controls.Add(rightHeader);

            // ---- Layout: explorer | (centre | properties) ----
            var inner = new SplitContainer { Dock = DockStyle.Fill };
            inner.Panel1.Controls.Add(centre);
            inner.Panel2.Controls.Add(right);
            var outer = new SplitContainer { Dock = DockStyle.Fill };
            outer.Panel1.Controls.Add(explorer);
            outer.Panel2.Controls.Add(inner);

            Controls.Add(outer);
            Controls.Add(_banner);
            Controls.Add(_lblEnvironment);
            Controls.Add(_toolbar);

            // Splitter distances need the real size, which XrmToolBox only gives the control after it is docked into its
            // tab (the Load event is too early). Apply them the first time the layout is wide enough, then leave them to the user.
            // Panel sizes: explorer about a third of the width (320–560 px), properties 340 px. They are re-applied on every
            // resize until the user drags a splitter, because XrmToolBox resizes (and DPI-scales) the control after creating it,
            // which made one-time sizing unreliable. Once the user moves a splitter, their sizes are kept.
            var outerByUser = false;
            var innerByUser = false;
            var sizing = false;
            void ApplySplitters(object s, EventArgs e)
            {
                if (sizing) return;
                sizing = true;
                try
                {
                    if (!outerByUser && outer.Width > 700)
                        outer.SplitterDistance = Math.Min(560, Math.Max(320, outer.Width / 3));
                    if (!innerByUser && inner.Width > 700)
                        inner.SplitterDistance = inner.Width - inner.SplitterWidth - 340;
                }
                catch (InvalidOperationException) { /* not laid out yet: the next resize tries again */ }
                catch (ArgumentOutOfRangeException) { }
                finally { sizing = false; }
            }
            outer.SplitterMoving += (s, e) => outerByUser = true;
            inner.SplitterMoving += (s, e) => innerByUser = true;
            outer.SizeChanged += ApplySplitters;
            inner.SizeChanged += ApplySplitters;
            VisibleChanged += ApplySplitters;
            ResumeLayout();
        }

        private void BuildValidationTab()
        {
            _tabValidation = new TabPage("Validation") { BackColor = Color.White, ToolTipText = "Structure, FetchXML, metadata and Unified Interface checks" };
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(4, 4, 4, 0), WrapContents = false };
            var btnValidate = new Button { Text = "Validate again", AutoSize = true, FlatStyle = FlatStyle.System };
            btnValidate.Click += (s, e) => RunValidation();
            CheckBox Filter(string text, bool on)
            {
                var c = new CheckBox { Text = text, Checked = on, AutoSize = true, Padding = new Padding(6, 3, 0, 0) };
                c.CheckedChanged += (s, e) => BindValidation();
                return c;
            }
            _chkErrors = Filter("✕ Errors", true);
            _chkWarnings = Filter("⚠ Warnings", true);
            _chkInfo = Filter("ℹ Info", true);
            _chkPassed = Filter("✓ Passed", false);
            _lblValidationSummary = new Label { AutoSize = true, Padding = new Padding(12, 6, 0, 0), Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
            top.Controls.AddRange(new Control[] { btnValidate, _chkErrors, _chkWarnings, _chkInfo, _chkPassed, _lblValidationSummary });

            _validationGrid = new FastGrid { Dock = DockStyle.Fill, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
            _validationGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Severity", Width = 95, SortMode = DataGridViewColumnSortMode.NotSortable });
            _validationGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Rule", Width = 80, SortMode = DataGridViewColumnSortMode.NotSortable });
            _validationGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Finding", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = { WrapMode = DataGridViewTriState.True } });
            _validationGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Where", Width = 150, SortMode = DataGridViewColumnSortMode.NotSortable });
            _validationGrid.SelectionChanged += (s, e) => ShowValidationDetails();
            _validationGrid.CellDoubleClick += (s, e) => GoToValidationResult();
            _validationGrid.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { GoToValidationResult(); e.Handled = true; } };

            _lblValidationDetails = new Label { Dock = DockStyle.Bottom, Height = 110, Padding = new Padding(8, 6, 8, 4), BackColor = Color.FromArgb(0xF7, 0xF9, 0xF8), ForeColor = Palette.Ink };
            var note = new Label
            {
                Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(8, 3, 8, 0), ForeColor = Palette.Muted,
                Text = $"Checks report only; nothing is changed automatically. Unified Interface rules follow Microsoft's documentation (reviewed {UciCapabilities.LastReviewed})."
            };
            _tabValidation.Controls.Add(_validationGrid);
            _tabValidation.Controls.Add(_lblValidationDetails);
            _tabValidation.Controls.Add(note);
            _tabValidation.Controls.Add(top);
        }

        private void BuildDiffTab()
        {
            _tabDiff = new TabPage("Diff") { BackColor = Color.White, ToolTipText = "Your changes compared with the XML stored in Dataverse" };
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(4, 4, 4, 0), WrapContents = false };
            _rdoDiffData = new RadioButton { Text = "Data XML", Checked = true, AutoSize = true, Padding = new Padding(2, 3, 0, 0) };
            _rdoDiffPresentation = new RadioButton { Text = "Presentation XML", AutoSize = true, Padding = new Padding(2, 3, 0, 0) };
            // On by default: Dataverse usually stores chart XML on one line, so an exact diff would show the whole document as one changed line.
            _chkIgnoreFormatting = new CheckBox { Text = "Ignore formatting", Checked = true, AutoSize = true, Padding = new Padding(16, 3, 0, 0) };
            _chkChangesOnly = new CheckBox { Text = "Changed lines only", Checked = true, AutoSize = true, Padding = new Padding(6, 3, 0, 0) };
            _lblDiffSummary = new Label { AutoSize = true, Padding = new Padding(12, 6, 0, 0), Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
            _rdoDiffData.CheckedChanged += (s, e) => { if (_rdoDiffData.Checked) RefreshDiff(); };
            _rdoDiffPresentation.CheckedChanged += (s, e) => { if (_rdoDiffPresentation.Checked) RefreshDiff(); };
            _chkIgnoreFormatting.CheckedChanged += (s, e) => RefreshDiff();
            _chkChangesOnly.CheckedChanged += (s, e) => RefreshDiff();
            top.Controls.AddRange(new Control[] { _rdoDiffData, _rdoDiffPresentation, _chkIgnoreFormatting, _chkChangesOnly, _lblDiffSummary });
            _diffView = new DiffView { Dock = DockStyle.Fill };
            var legend = new Label
            {
                Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(8, 3, 8, 0), ForeColor = Palette.Muted,
                Text = "Columns: line in the stored XML, line in your version.  − removed  + added.  This is the diff you'll confirm before saving (milestone M6)."
            };
            _tabDiff.Controls.Add(_diffView);
            _tabDiff.Controls.Add(legend);
            _tabDiff.Controls.Add(top);
        }

        /// <summary>Grows the summary sentence label to fit its wrapped text.</summary>
        private void FitSentence()
        {
            var width = Math.Max(100, _tabSummary.ClientSize.Width - 30);
            var size = TextRenderer.MeasureText(_lblSentence.Text + " ", _lblSentence.Font, new Size(width, 0), TextFormatFlags.WordBreak);
            _lblSentence.Height = Math.Max(40, size.Height + 14);
        }

        private static void AddColumn(DataGridView grid, string header, int width, ChartSortColumn sort, bool sortable = true)
        {
            var col = new DataGridViewTextBoxColumn
            {
                HeaderText = header, Width = width, Tag = sort,
                SortMode = sortable ? DataGridViewColumnSortMode.Programmatic : DataGridViewColumnSortMode.NotSortable
            };
            grid.Columns.Add(col);
        }

        private Label StatusLabel(DocumentKind kind)
        {
            var label = new Label
            {
                Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(6, 4, 6, 0), AutoEllipsis = true, BackColor = Color.FromArgb(0xFA, 0xFA, 0xFA)
            };
            label.Click += (s, e) =>
            {
                if (label.Tag is XmlParseError error && error.Line > 0) EditorFor(kind).GoTo(error.Line, error.Column);
            };
            return label;
        }

        private static TabPage XmlTab(string title, string tooltip, Control view, Control status)
        {
            var page = new TabPage(title) { ToolTipText = tooltip, BackColor = Color.White };
            page.Controls.Add(view);
            page.Controls.Add(status);
            return page;
        }
    }
}
