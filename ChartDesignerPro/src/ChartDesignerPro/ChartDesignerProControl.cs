using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ChartDesignerPro.Abstractions;
using ChartDesignerPro.Controls;
using ChartDesignerPro.Data;
using ChartDesignerPro.Explorer;
using ChartDesignerPro.Models;
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
    /// Main UI (milestone M1): Chart Explorer on the left, the open chart's XML in the centre, its properties on the right.
    /// Everything is read-only in this milestone: the tool never writes to Dataverse.
    /// </summary>
    public class ChartDesignerProControl : PluginControlBase, IGitHubPlugin, IHelpPlugin, IStatusBarMessenger
    {
        private const string AppName = "Chart Designer Pro";

        // IGitHubPlugin / IHelpPlugin
        public string RepositoryName => "XrmTools";
        public string UserName => "vikashkumar669";
        public string HelpUrl => "https://github.com/vikashkumar669/XrmTools/tree/main/ChartDesignerPro#readme";

        public event EventHandler<StatusBarMessageEventArgs> SendMessageToStatusBar;

        // State
        private ToolSettings _settings = new ToolSettings();
        private ILog _log;
        private IChartRepository _repository;
        private MetadataCache _metadata;
        private List<ChartRecord> _charts = new List<ChartRecord>();
        private ChartRecord _open;
        private ChartSortColumn _sortColumn = ChartSortColumn.Name;
        private bool _sortDescending;
        private CancellationTokenSource _loadCts;
        private int _openRequest;
        private int _generation; // bumped on every connection change, so late results from the old connection are ignored
        private bool _binding; // true while the UI is being filled from code, so change events don't save settings

        // UI
        private ToolStrip _toolbar;
        private ToolStripButton _tsbLoad, _tsbCancel, _tsbSystem, _tsbPersonal, _tsbFormatted;
        private ToolStripComboBox _cboTable;
        private ToolStripTextBox _txtSearch;
        private Label _lblEnvironment;
        private Banner _banner;
        private FastGrid _grid;
        private Label _lblCount;
        private Label _lblTitle;
        private Banner _chartBanner;
        private TabControl _tabs;
        private XmlViewBox _dataView, _presentationView;
        private Label _lblDataStatus, _lblPresentationStatus;
        private FastGrid _properties;
        private System.Windows.Forms.Timer _searchTimer;

        public ChartDesignerProControl()
        {
            _log = new PluginLog((m, a) => LogInfo(m, a), (m, a) => LogWarning(m, a), (m, a) => LogError(m, a));
            BuildUi();
            ShowNoChart();
            UpdateEnvironment();
            UpdateButtons();
        }

        // ============================== Connection & settings ==============================

        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
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
            _banner.Show(Banner.Kind.Info, newService == null ? null : "Connected. Click ▶ Load charts (F5) to list the charts in this environment.");
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
                _tsbFormatted.Checked = _settings.FormattedView;
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
            _lblEnvironment.Text = detail == null
                ? "Not connected: use Connect in XrmToolBox"
                : $"{detail.ConnectionName}   ·   {EnvironmentUrl(detail)}   ·   READ-ONLY";
        }

        private void UpdateButtons()
        {
            var connected = Service != null;
            var busy = _loadCts != null;
            _tsbLoad.Enabled = connected && !busy;
            _tsbCancel.Enabled = busy;
            _cboTable.Enabled = connected && !busy;
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
            var visible = _charts
                .Where(c => table == null || string.Equals(c.TableLogicalName, table, StringComparison.OrdinalIgnoreCase))
                .Where(c => (c.Scope == ChartScope.System && _settings.IncludeSystem) || (c.Scope == ChartScope.Personal && _settings.IncludePersonal));
            var rows = ChartListFilter.Sort(ChartListFilter.Search(visible, _txtSearch.Text), _sortColumn, _sortDescending);

            var selectedId = _open?.Id;
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var c in rows)
            {
                var i = _grid.Rows.Add(c.Name, c.TableText, c.ScopeText, c.EngineText,
                    c.IsDefault ? "Yes" : "",
                    c.IsManaged == null ? "" : c.IsManaged == true ? "Managed" : "Unmanaged",
                    c.ModifiedOnUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "");
                var row = _grid.Rows[i];
                row.Tag = c;
                if (!ChartCapability.For(c).CanEdit) row.DefaultCellStyle.ForeColor = Palette.Muted;
                row.Cells[0].ToolTipText = c.Description;
                if (selectedId == c.Id) row.Selected = true;
            }
            _grid.ResumeLayout();

            foreach (DataGridViewColumn col in _grid.Columns.Cast<DataGridViewColumn>().Where(c => c.SortMode == DataGridViewColumnSortMode.Programmatic))
                col.HeaderCell.SortGlyphDirection = (ChartSortColumn)col.Tag == _sortColumn
                    ? (_sortDescending ? SortOrder.Descending : SortOrder.Ascending)
                    : SortOrder.None;

            _lblCount.Text = _charts.Count == 0
                ? "No charts loaded"
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

        // ============================== Open a chart ==============================

        private void OpenSelected()
        {
            if (_grid.SelectedRows.Count == 0 || !(_grid.SelectedRows[0].Tag is ChartRecord row)) return;
            if (_open != null && _open.Id == row.Id) return;
            if (_repository == null) return;

            var request = ++_openRequest;
            var repository = _repository;
            WorkAsync(new WorkAsyncInfo
            {
                Message = $"Opening '{row.Name}'...",
                Work = (worker, args) => args.Result = repository.Retrieve(row.Scope, row.Id),
                PostWorkCallBack = e =>
                {
                    if (request != _openRequest) return; // a newer selection superseded this one
                    if (e.Error != null)
                    {
                        ShowError($"'{row.Name}' couldn't be opened", e.Error, "You may not have read access to this chart.");
                        return;
                    }
                    var chart = e.Result as ChartRecord;
                    if (chart == null)
                    {
                        _banner.Show(Banner.Kind.Warning, $"'{row.Name}' no longer exists in this environment. Reload the list (F5).");
                        return;
                    }
                    chart.TableDisplayName = row.TableDisplayName;
                    ShowChart(chart);
                }
            });
        }

        private void ShowNoChart()
        {
            _open = null;
            _lblTitle.Text = "No chart open";
            _chartBanner.Show(Banner.Kind.Info, "Select a chart in the list to inspect it.");
            _dataView.ShowXml(string.Empty);
            _presentationView.ShowXml(string.Empty);
            _lblDataStatus.Text = _lblPresentationStatus.Text = "";
            _properties.Rows.Clear();
        }

        private void ShowChart(ChartRecord chart)
        {
            _open = chart;
            _lblTitle.Text = $"{chart.TableText}  ›  {chart.Name}";

            var capability = ChartCapability.For(chart);
            if (!capability.CanEdit) _chartBanner.Show(Banner.Kind.Warning, "Read-only: " + capability.Reason);
            else _chartBanner.Show(Banner.Kind.Info, "Read-only in this version. Editing arrives in a later milestone." +
                                                     (capability.Reason == null ? "" : "  " + capability.Reason));

            ShowDocument(chart.DataXml, _dataView, _lblDataStatus, "Data XML");
            ShowDocument(chart.PresentationXml, _presentationView, _lblPresentationStatus, "Presentation XML");
            BindProperties(chart);
            Status($"Opened '{chart.Name}'");
        }

        private void ShowDocument(string text, XmlViewBox view, Label status, string what)
        {
            if (string.IsNullOrEmpty(text))
            {
                view.ShowXml(string.Empty);
                status.Text = $"{what} is empty.";
                status.ForeColor = Palette.Muted;
                return;
            }

            if (SafeXml.TryParse(text, out _, out var error))
            {
                var shown = _settings.FormattedView ? SafeXml.Format(text) ?? text : text;
                view.ShowXml(shown);
                status.Text = $"✓ Well-formed  ·  {text.Length:N0} characters" + (_settings.FormattedView ? "  ·  shown indented (display only)" : "  ·  shown exactly as stored");
                status.ForeColor = Palette.Ok;
            }
            else
            {
                view.ShowXml(text);
                status.Text = $"✕ Not well-formed: {error}";
                status.ForeColor = Palette.ErrorFore;
            }
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
            Add("Editable", cap.CanEdit ? "Yes (from a later milestone)" : "No: " + cap.Reason);
        }

        private void RefreshOpenDocuments()
        {
            if (_open == null) return;
            ShowDocument(_open.DataXml, _dataView, _lblDataStatus, "Data XML");
            ShowDocument(_open.PresentationXml, _presentationView, _lblPresentationStatus, "Presentation XML");
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
            if (keyData == Keys.F5) { if (_tsbLoad.Enabled) ExecuteMethod(LoadCharts); return true; }
            if (keyData == (Keys.Control | Keys.F)) { _txtSearch.Focus(); _txtSearch.SelectAll(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _loadCts?.Cancel();
                _searchTimer?.Dispose();
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

            // ---- Toolbar ----
            _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2), RenderMode = ToolStripRenderMode.System };
            var tsbClose = ToolStripFactory.Button("Close", "Close this tool", (s, e) => CloseTool());
            _tsbLoad = ToolStripFactory.Button("▶ Load charts", "List the charts for the selected table (F5)", (s, e) => ExecuteMethod(LoadCharts), true);
            _tsbLoad.ForeColor = Palette.Brand;
            _tsbCancel = ToolStripFactory.Button("■ Cancel", "Stop loading after the current page", (s, e) => CancelLoad());
            _cboTable = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 240, ToolTipText = "Table filter. Loading with a table selected only asks Dataverse for that table's charts." };
            _cboTable.SelectedIndexChanged += (s, e) => OnSettingChanged(() => _settings.LastTable = SelectedTable(), BindGrid);
            _txtSearch = new ToolStripTextBox { AutoSize = false, Width = 220, ToolTipText = "Search by name, description, table, type or id (Ctrl+F)" };
            _searchTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); BindGrid(); };
            _txtSearch.TextChanged += (s, e) => { _searchTimer.Stop(); _searchTimer.Start(); };
            _tsbSystem = ToolStripFactory.Toggle("System", "Include system charts (savedqueryvisualization)", true, v => OnSettingChanged(() => _settings.IncludeSystem = v, BindGrid));
            _tsbPersonal = ToolStripFactory.Toggle("Personal", "Include personal charts you own or that are shared with you (userqueryvisualization)", true, v => OnSettingChanged(() => _settings.IncludePersonal = v, BindGrid));
            _tsbFormatted = ToolStripFactory.Toggle("Indent XML", "Show the XML indented. Display only: the stored XML is never changed.", true, v => OnSettingChanged(() => _settings.FormattedView = v, RefreshOpenDocuments));
            var tsbRefreshMetadata = ToolStripFactory.Button("⟳ Metadata", "Forget cached table metadata so the next load reads it again", (s, e) =>
            {
                _metadata?.Clear();
                Status("Metadata cache cleared");
            });

            _toolbar.Items.AddRange(new ToolStripItem[]
            {
                tsbClose, new ToolStripSeparator(), _tsbLoad, _tsbCancel, new ToolStripSeparator(),
                new ToolStripLabel("Table:"), _cboTable, new ToolStripLabel("Search:"), _txtSearch, new ToolStripSeparator(),
                _tsbSystem, _tsbPersonal, new ToolStripSeparator(), _tsbFormatted, tsbRefreshMetadata
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
            AddColumn(_grid, "Name", 220, ChartSortColumn.Name);
            AddColumn(_grid, "Table", 150, ChartSortColumn.Table);
            AddColumn(_grid, "Scope", 70, ChartSortColumn.Scope);
            AddColumn(_grid, "Type", 80, ChartSortColumn.Type);
            AddColumn(_grid, "Default", 55, ChartSortColumn.Name, sortable: false);
            AddColumn(_grid, "Managed", 80, ChartSortColumn.Name, sortable: false);
            AddColumn(_grid, "Modified", 115, ChartSortColumn.Modified);
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
            _chartBanner = new Banner();
            _tabs = new TabControl { Dock = DockStyle.Fill };
            _dataView = new XmlViewBox { Dock = DockStyle.Fill };
            _presentationView = new XmlViewBox { Dock = DockStyle.Fill };
            _lblDataStatus = StatusLabel();
            _lblPresentationStatus = StatusLabel();
            _tabs.TabPages.Add(XmlTab("Data XML", "Underlying data: FetchXML, categories and measures (datadescription)", _dataView, _lblDataStatus));
            _tabs.TabPages.Add(XmlTab("Presentation XML", "Appearance: chart type, colours, labels, axes (presentationdescription)", _presentationView, _lblPresentationStatus));
            centre.Controls.Add(_tabs);
            centre.Controls.Add(_chartBanner);
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
            var inner = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
            inner.Panel1.Controls.Add(centre);
            inner.Panel2.Controls.Add(right);
            var outer = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            outer.Panel1.Controls.Add(explorer);
            outer.Panel2.Controls.Add(inner);

            Controls.Add(outer);
            Controls.Add(_banner);
            Controls.Add(_lblEnvironment);
            Controls.Add(_toolbar);

            // Splitter distances need the real size, which XrmToolBox only gives the control after it is docked into its
            // tab (the Load event is too early). Apply them the first time the layout is wide enough, then leave them to the user.
            var splittersApplied = false;
            void ApplySplitters(object s, EventArgs e)
            {
                if (splittersApplied || outer.Width < 900) return;
                try
                {
                    outer.SplitterDistance = Math.Min(560, Math.Max(320, outer.Width / 3));
                    inner.SplitterDistance = Math.Max(300, inner.Width - 320);
                    splittersApplied = true;
                }
                catch (InvalidOperationException) { /* not laid out yet: try again on the next resize */ }
            }
            outer.SizeChanged += ApplySplitters;
            VisibleChanged += ApplySplitters;
            ResumeLayout();
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

        private static Label StatusLabel() => new Label
        {
            Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(6, 4, 6, 0), AutoEllipsis = true, BackColor = Color.FromArgb(0xFA, 0xFA, 0xFA)
        };

        private static TabPage XmlTab(string title, string tooltip, Control view, Control status)
        {
            var page = new TabPage(title) { ToolTipText = tooltip, BackColor = Color.White };
            page.Controls.Add(view);
            page.Controls.Add(status);
            return page;
        }
    }
}
