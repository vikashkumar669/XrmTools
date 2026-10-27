using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Controls;
using PluginFailureCorrelationExplorer.Models;
using Label = System.Windows.Forms.Label;

namespace PluginFailureCorrelationExplorer
{
    /// <summary>Layout: toolbar, organization header, query panel on the left, four tabs on the right.</summary>
    public partial class PluginFailureCorrelationControl
    {
        private static readonly string[] CommonMessages =
        {
            "", "Create", "Update", "Delete", "Retrieve", "RetrieveMultiple", "Assign", "SetState", "SetStateDynamicEntity",
            "Associate", "Disassociate", "Merge", "QualifyLead", "Win", "Lose", "Send", "ExecuteWorkflow",
        };

        private static readonly (string Text, int Hours)[] RangePresets =
        {
            ("Last hour", 1), ("Last 24 hours", 24), ("Last 7 days", 168), ("Last 30 days", 720), ("Custom range", 0),
        };

        // Toolbar and header
        private ToolStrip _toolbar;
        private ToolStripButton _tsAnalyze, _tsCancel, _tsRedact;
        private ToolStripComboBox _tsBaselineHost;
        private ComboBox _cboBaseline;
        private Label _lblOrgName, _lblOrgDetails, _lblTraceSetting;

        // Query panel
        private ComboBox _cboRange, _cboExceptions, _cboMessage;
        private DateTimePicker _dtFrom, _dtTo;
        private TextBox _txtTable, _txtType, _txtCorrelation, _txtRequest;
        private NumericUpDown _numMinDuration, _numLimit;
        private Button _btnAnalyze, _btnCancel;

        // Tabs
        private TabControl _tabs;
        private TabPage _tabDashboard, _tabGroups, _tabTraces, _tabReport;

        // Dashboard
        private KpiTile _kpiTraces, _kpiExceptions, _kpiGroups, _kpiSlow, _kpiFindings, _kpiEvidence;
        private Label _lblBanner, _lblTrend;
        private VirtualGrid<DiagnosticFinding> _findingGrid;
        private RichTextBox _findingDetail;
        private VirtualGrid<FailureGroup> _recurringGrid;

        // Failure groups
        private VirtualGrid<FailureGroup> _groupGrid;
        private RichTextBox _groupDetail;

        // Trace explorer
        private TextBox _txtSearch, _txtFind;
        private CheckBox _chkErrorsOnly, _chkSlowOnly;
        private Label _lblGroupFilter, _lblTraceCount, _lblDetailTitle, _lblRelatedHint;
        private Button _btnClearGroup;
        private VirtualGrid<PluginTraceRecord> _traceGrid;
        private RichTextBox _traceDetail;
        private VirtualGrid<RelatedTrace> _relatedGrid;
        private Button _btnPrevInChain, _btnNextInChain;
        private Timer _searchTimer;

        // Report
        private WebBrowser _reportBrowser;
        private Label _lblReportHint;

        private void BuildUi()
        {
            SuspendLayout();
            Font = Theme.Base;
            BackColor = Theme.Surface;
            Size = new Size(1280, 800);

            _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6), Font = Theme.Base };
            _tabDashboard = new TabPage("Dashboard") { BackColor = Theme.Surface, Padding = new Padding(12) };
            _tabGroups = new TabPage("Failure groups") { BackColor = Theme.Surface, Padding = new Padding(12) };
            _tabTraces = new TabPage("Trace explorer") { BackColor = Theme.Surface, Padding = new Padding(12) };
            _tabReport = new TabPage("Diagnostic report") { BackColor = Theme.Surface, Padding = new Padding(12) };
            _tabs.TabPages.AddRange(new[] { _tabDashboard, _tabGroups, _tabTraces, _tabReport });
            _tabs.SelectedIndexChanged += (s, e) => { if (_tabs.SelectedTab == _tabReport) RefreshReportPreview(); };

            BuildDashboard();
            BuildGroups();
            BuildTraces();
            BuildReport();

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 8, 8) };
            body.Controls.Add(_tabs);

            Controls.Add(body);
            Controls.Add(BuildQueryPanel());
            Controls.Add(BuildHeader());
            Controls.Add(BuildToolbar());
            ResumeLayout();
            SetBusy(false);
        }

        private ToolStrip BuildToolbar()
        {
            _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Renderer = new Theme.FlatRenderer(), Padding = new Padding(6, 4, 6, 4), BackColor = Theme.Card, Font = Theme.Base };
            _tsAnalyze = new ToolStripButton("▶  Analyze") { Font = Theme.Bold, ForeColor = Theme.Brand, ToolTipText = "Retrieve and analyze trace logs for the filters on the left (F5)" };
            _tsAnalyze.Click += (s, e) => ExecuteMethod(Analyze);
            _tsCancel = new ToolStripButton("■  Cancel") { ToolTipText = "Stop after the current page" };
            _tsCancel.Click += (s, e) => Cancel();

            _tsBaselineHost = new ToolStripComboBox { AutoSize = false, Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, ToolTipText = "Compare with a previous period or a saved snapshot to detect new or increasing failures and regressions" };
            _cboBaseline = _tsBaselineHost.ComboBox;
            _cboBaseline.Items.AddRange(new object[] { "No baseline", "Previous period (same length)" });
            _cboBaseline.SelectedIndex = 0;

            _tsRedact = new ToolStripButton { CheckOnClick = true, Checked = true, ToolTipText = "Remove GUIDs, e-mail addresses and URL query strings from exports and the report preview. Credentials are always removed." };
            void RedactText() => _tsRedact.Text = (_tsRedact.Checked ? "☑" : "☐") + "  Redact exports";
            _tsRedact.CheckedChanged += (s, e) => { RedactText(); if (_tabs.SelectedTab == _tabReport) RefreshReportPreview(); };
            RedactText();

            var export = new ToolStripDropDownButton("⭳  Export") { ToolTipText = "Export the analysis" };
            export.DropDownItems.Add("Diagnostic report (HTML)…", null, (s, e) => Export("html"));
            export.DropDownItems.Add("Diagnostic report (JSON)…", null, (s, e) => Export("json"));
            export.DropDownItems.Add(new ToolStripSeparator());
            export.DropDownItems.Add("Findings (CSV)…", null, (s, e) => Export("findings"));
            export.DropDownItems.Add("Failure groups (CSV)…", null, (s, e) => Export("groups"));
            export.DropDownItems.Add("Performance (CSV)…", null, (s, e) => Export("performance"));
            export.DropDownItems.Add("Traces in the explorer (CSV)…", null, (s, e) => Export("traces"));

            var settings = new ToolStripButton("⚙  Settings") { ToolTipText = "Thresholds, grouping, normalization rules and report privacy (saved per connection)" };
            settings.Click += (s, e) => EditSettings();
            var help = new ToolStripButton("?  Help") { Alignment = ToolStripItemAlignment.Right };
            help.Click += (s, e) => { try { System.Diagnostics.Process.Start(HelpUrl); } catch { /* no browser */ } };

            _toolbar.Items.AddRange(new ToolStripItem[]
            {
                _tsAnalyze, _tsCancel, new ToolStripSeparator(),
                new ToolStripLabel("Compare with:") { ForeColor = Theme.Muted }, _tsBaselineHost, new ToolStripSeparator(),
                export, _tsRedact, new ToolStripSeparator(), settings, help,
            });
            return _toolbar;
        }

        private Control BuildHeader()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Theme.Brand, Padding = new Padding(16, 6, 16, 6) };
            _lblOrgName = new Label { AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 12F), Location = new Point(16, 6), Text = "Not connected" };
            _lblOrgDetails = new Label { AutoSize = true, ForeColor = Color.FromArgb(0xD5, 0xEE, 0xEA), Font = Theme.Base, Location = new Point(17, 30) };
            _lblTraceSetting = new Label { AutoSize = true, ForeColor = Color.White, Font = Theme.Bold, Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Theme.BrandDark, Padding = new Padding(8, 3, 8, 3) };
            header.Controls.Add(_lblOrgName);
            header.Controls.Add(_lblOrgDetails);
            header.Controls.Add(_lblTraceSetting);
            header.Resize += (s, e) => _lblTraceSetting.Location = new Point(header.Width - _lblTraceSetting.Width - 16, 15);
            return header;
        }

        private Control BuildQueryPanel()
        {
            var outer = new Panel { Dock = DockStyle.Left, Width = 270, Padding = new Padding(8, 8, 8, 8), BackColor = Theme.Surface };
            var card = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card, Padding = new Padding(12, 8, 12, 8), AutoScroll = true };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            const int w = 220;

            flow.Controls.Add(new Label { Text = "Query", Font = Theme.Heading, ForeColor = Theme.Brand, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });

            flow.Controls.Add(Theme.Caption("Date range"));
            _cboRange = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = w };
            _cboRange.Items.AddRange(RangePresets.Select(p => (object)p.Text).ToArray());
            _cboRange.SelectedIndex = 1;
            _cboRange.SelectedIndexChanged += (s, e) => ApplyRangePreset();
            flow.Controls.Add(_cboRange);

            flow.Controls.Add(Theme.Caption("From (local time)"));
            _dtFrom = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Width = w };
            flow.Controls.Add(_dtFrom);
            flow.Controls.Add(Theme.Caption("To (local time)"));
            _dtTo = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Width = w };
            flow.Controls.Add(_dtTo);
            _dtFrom.ValueChanged += (s, e) => MarkCustomRange();
            _dtTo.ValueChanged += (s, e) => MarkCustomRange();

            flow.Controls.Add(Theme.Caption("Exceptions"));
            _cboExceptions = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = w };
            _cboExceptions.Items.AddRange(new object[] { "All traces", "Only traces with an exception", "Only traces without an exception" });
            _cboExceptions.SelectedIndex = 0;
            flow.Controls.Add(_cboExceptions);

            flow.Controls.Add(Theme.Caption("Message"));
            _cboMessage = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = w };
            _cboMessage.Items.AddRange(CommonMessages.Cast<object>().ToArray());
            flow.Controls.Add(_cboMessage);

            flow.Controls.Add(Theme.Caption("Table (logical name)"));
            _txtTable = new TextBox { Width = w };
            flow.Controls.Add(_txtTable);

            flow.Controls.Add(Theme.Caption("Plug-in type contains"));
            _txtType = new TextBox { Width = w };
            flow.Controls.Add(_txtType);

            flow.Controls.Add(Theme.Caption("Correlation ID"));
            _txtCorrelation = new TextBox { Width = w, Font = Theme.Mono };
            flow.Controls.Add(_txtCorrelation);

            flow.Controls.Add(Theme.Caption("Request ID"));
            _txtRequest = new TextBox { Width = w, Font = Theme.Mono };
            flow.Controls.Add(_txtRequest);

            flow.Controls.Add(Theme.Caption("Minimum duration (ms, 0 = any)"));
            _numMinDuration = new NumericUpDown { Width = w, Maximum = 3600000, Increment = 500, ThousandsSeparator = true };
            flow.Controls.Add(_numMinDuration);

            flow.Controls.Add(Theme.Caption("Result limit (newest first)"));
            _numLimit = new NumericUpDown { Width = w, Minimum = 1, Maximum = TraceQuery.MaxResultLimit, Increment = 1000, ThousandsSeparator = true, Value = TraceQuery.DefaultResultLimit };
            flow.Controls.Add(_numLimit);

            _btnAnalyze = Theme.PrimaryButton("▶  Analyze (F5)", (s, e) => ExecuteMethod(Analyze));
            _btnAnalyze.Width = w;
            _btnAnalyze.Margin = new Padding(0, 16, 0, 4);
            flow.Controls.Add(_btnAnalyze);
            _btnCancel = Theme.SecondaryButton("Cancel", (s, e) => Cancel());
            _btnCancel.AutoSize = false;
            _btnCancel.Width = w;
            flow.Controls.Add(_btnCancel);

            var reset = new LinkLabel { Text = "Clear filters", AutoSize = true, LinkColor = Theme.Brand, Margin = new Padding(0, 8, 0, 0) };
            reset.LinkClicked += (s, e) => ClearFilters();
            flow.Controls.Add(reset);

            flow.Controls.Add(new Label
            {
                Text = "Read-only: the tool never changes Dataverse data, step registrations or trace logs.",
                ForeColor = Theme.Muted, Font = Theme.Small, MaximumSize = new Size(w, 0), AutoSize = true, Margin = new Padding(0, 14, 0, 0),
            });

            card.Controls.Add(flow);
            outer.Controls.Add(card);
            return outer;
        }

        private void BuildDashboard()
        {
            var tiles = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 96, WrapContents = false, AutoScroll = true, BackColor = Theme.Surface };
            _kpiTraces = new KpiTile("Traces analyzed", Theme.Brand);
            _kpiExceptions = new KpiTile("With an exception", Theme.Error);
            _kpiGroups = new KpiTile("Unique failures", Theme.Warning);
            _kpiSlow = new KpiTile("Slow executions", Theme.Warning);
            _kpiFindings = new KpiTile("Findings", Theme.Info);
            _kpiEvidence = new KpiTile("Complete evidence", Theme.Ok);
            tiles.Controls.AddRange(new Control[] { _kpiTraces, _kpiExceptions, _kpiGroups, _kpiSlow, _kpiFindings, _kpiEvidence });

            _lblBanner = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 0, Padding = new Padding(10, 6, 10, 6), Font = Theme.Base };
            _lblTrend = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 30, Padding = new Padding(10, 6, 10, 6), ForeColor = Theme.Muted, BackColor = Theme.Card };

            _findingGrid = new VirtualGrid<DiagnosticFinding>(new[]
            {
                new GridColumn<DiagnosticFinding>("ID", f => f.Id, 60),
                new GridColumn<DiagnosticFinding>("Severity", f => (int)f.Severity, 80) { Display = f => f.Severity.ToString(), ForeColor = f => Theme.For(f.Severity) },
                new GridColumn<DiagnosticFinding>("Confidence", f => -(int)f.Confidence, 85) { Display = f => f.Confidence.ToString() },
                new GridColumn<DiagnosticFinding>("Rule", f => f.RuleId, 65),
                new GridColumn<DiagnosticFinding>("Title", f => f.Title, 300, fill: true),
                new GridColumn<DiagnosticFinding>("Traces", f => f.TraceIds.Count + f.AdditionalTraceCount, 60, rightAlign: true),
            }) { Dock = DockStyle.Fill, EmptyText = "Run an analysis to see diagnostic findings." };
            _findingGrid.SelectionChanged += (s, e) => ShowFindingDetail(_findingGrid.SelectedItem);
            _findingGrid.CellDoubleClick += (s, e) => OpenFindingTraces(_findingGrid.SelectedItem);

            _findingDetail = DetailBox();

            var findingsSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 220, BackColor = Theme.Line };
            findingsSplit.Panel1.Controls.Add(_findingGrid);
            findingsSplit.Panel1.Controls.Add(Section("Diagnostic findings · double-click to open the supporting traces"));
            findingsSplit.Panel2.Controls.Add(_findingDetail);

            _recurringGrid = new VirtualGrid<FailureGroup>(new[]
            {
                new GridColumn<FailureGroup>("Count", g => g.Occurrences, 55, rightAlign: true),
                new GridColumn<FailureGroup>("Exception", g => g.Fingerprint.Label, 150),
                new GridColumn<FailureGroup>("Plug-in", g => Fmt.List(g.PluginTypes.Select(TypeNames.ShortName), 2), 150, fill: true),
                new GridColumn<FailureGroup>("Latest (UTC)", g => g.LastUtc, 125) { Display = g => g.LastUtc.ToString("yyyy-MM-dd HH:mm") },
            }) { Dock = DockStyle.Fill, EmptyText = "No recurring failures." };
            _recurringGrid.CellDoubleClick += (s, e) => OpenGroupTraces(_recurringGrid.SelectedItem);

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(_recurringGrid);
            right.Controls.Add(Section("Recurring failure summary · double-click to inspect"));

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Theme.Line };
            split.Panel1.Controls.Add(findingsSplit);
            split.Panel2.Controls.Add(right);
            split.HandleCreated += (s, e) => { try { split.SplitterDistance = (int)(split.Width * 0.62); } catch (InvalidOperationException) { } };

            var spacer = new Panel { Dock = DockStyle.Top, Height = 8 };
            _tabDashboard.Controls.Add(split);
            _tabDashboard.Controls.Add(spacer);
            _tabDashboard.Controls.Add(_lblTrend);
            _tabDashboard.Controls.Add(_lblBanner);
            _tabDashboard.Controls.Add(tiles);
        }

        private void BuildGroups()
        {
            _groupGrid = new VirtualGrid<FailureGroup>(new[]
            {
                new GridColumn<FailureGroup>("Fingerprint", g => g.Key, 130),
                new GridColumn<FailureGroup>("Severity", g => (int)g.Severity, 80) { Display = g => g.Severity.ToString(), ForeColor = g => Theme.For(g.Severity) },
                new GridColumn<FailureGroup>("Exception", g => g.Fingerprint.Label, 200),
                new GridColumn<FailureGroup>("Message", g => Fmt.OneLine(g.Fingerprint.Message, 200), 280, fill: true),
                new GridColumn<FailureGroup>("Count", g => g.Occurrences, 60, rightAlign: true),
                new GridColumn<FailureGroup>("First (UTC)", g => g.FirstUtc, 125) { Display = g => g.FirstUtc.ToString("yyyy-MM-dd HH:mm") },
                new GridColumn<FailureGroup>("Latest (UTC)", g => g.LastUtc, 125) { Display = g => g.LastUtc.ToString("yyyy-MM-dd HH:mm") },
                new GridColumn<FailureGroup>("Plug-in types", g => Fmt.List(g.PluginTypes.Select(TypeNames.ShortName), 3), 180),
                new GridColumn<FailureGroup>("Tables", g => Fmt.List(g.PrimaryEntities, 3), 110),
                new GridColumn<FailureGroup>("Rate /h", g => g.RatePerHour, 65, rightAlign: true) { Display = g => Fmt.Rate(g.RatePerHour) },
            }) { Dock = DockStyle.Fill, EmptyText = "No failed traces in this analysis." };
            _groupGrid.SelectionChanged += (s, e) => ShowGroupDetail(_groupGrid.SelectedItem);
            _groupGrid.CellDoubleClick += (s, e) => OpenGroupTraces(_groupGrid.SelectedItem);
            _groupGrid.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { OpenGroupTraces(_groupGrid.SelectedItem); e.Handled = true; } };

            _groupDetail = DetailBox();
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 330, BackColor = Theme.Line };
            split.Panel1.Controls.Add(_groupGrid);
            split.Panel1.Controls.Add(Section("Failures grouped by fingerprint · double-click or press Enter to inspect the traces"));
            split.Panel2.Controls.Add(_groupDetail);
            _tabGroups.Controls.Add(split);
        }

        private void BuildTraces()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, BackColor = Theme.Card, Padding = new Padding(8, 6, 8, 4), WrapContents = false };
            bar.Controls.Add(new Label { Text = "Search", AutoSize = true, Margin = new Padding(0, 5, 4, 0), ForeColor = Theme.Muted });
            _txtSearch = new TextBox { Width = 260 };
            _searchTimer = new Timer { Interval = 300 };
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); RefreshTraceList(); };
            _txtSearch.TextChanged += (s, e) => { _searchTimer.Stop(); _searchTimer.Start(); };
            bar.Controls.Add(_txtSearch);
            _chkErrorsOnly = new CheckBox { Text = "Exceptions only", AutoSize = true, Margin = new Padding(12, 4, 0, 0) };
            _chkErrorsOnly.CheckedChanged += (s, e) => RefreshTraceList();
            _chkSlowOnly = new CheckBox { Text = "Slow only", AutoSize = true, Margin = new Padding(8, 4, 0, 0) };
            _chkSlowOnly.CheckedChanged += (s, e) => RefreshTraceList();
            bar.Controls.Add(_chkErrorsOnly);
            bar.Controls.Add(_chkSlowOnly);
            _lblGroupFilter = new Label { AutoSize = true, Margin = new Padding(14, 5, 4, 0), ForeColor = Theme.Brand, Font = Theme.Bold, Visible = false };
            _btnClearGroup = Theme.SecondaryButton("✕ Show all", (s, e) => { _groupFilter = null; _traceFilterIds = null; RefreshTraceList(); });
            _btnClearGroup.Visible = false;
            _btnClearGroup.Margin = new Padding(0, 0, 0, 0);
            bar.Controls.Add(_lblGroupFilter);
            bar.Controls.Add(_btnClearGroup);
            _lblTraceCount = new Label { AutoSize = true, Margin = new Padding(14, 5, 0, 0), ForeColor = Theme.Muted };
            bar.Controls.Add(_lblTraceCount);

            _traceGrid = new VirtualGrid<PluginTraceRecord>(new[]
            {
                new GridColumn<PluginTraceRecord>("Created (UTC)", t => t.CreatedOnUtc, 130) { Display = t => t.CreatedOnUtc.ToString("yyyy-MM-dd HH:mm:ss") },
                new GridColumn<PluginTraceRecord>("!", t => t.HasException ? 1 : 0, 26) { Display = t => t.HasException ? "✖" : "", ForeColor = t => t.HasException ? Theme.Error : (Color?)null },
                new GridColumn<PluginTraceRecord>("Plug-in type", t => t.ShortTypeName, 170, fill: true),
                new GridColumn<PluginTraceRecord>("Message", t => t.MessageName, 90),
                new GridColumn<PluginTraceRecord>("Table", t => t.PrimaryEntity, 90),
                new GridColumn<PluginTraceRecord>("Mode", t => t.ModeText, 55),
                new GridColumn<PluginTraceRecord>("Depth", t => t.Depth ?? -1, 48, rightAlign: true) { Display = t => t.Depth?.ToString() ?? "" },
                new GridColumn<PluginTraceRecord>("Duration", t => t.ExecutionDurationMs ?? -1, 80, rightAlign: true)
                    { Display = t => t.ExecutionDurationMs.HasValue ? t.ExecutionDurationMs.Value.ToString("N0") + " ms" : "", ForeColor = t => IsSlow(t) ? Theme.Warning : (Color?)null },
                new GridColumn<PluginTraceRecord>("Correlation ID", t => t.CorrelationId?.ToString() ?? "", 120),
            }) { Dock = DockStyle.Fill, EmptyText = "Run an analysis to explore traces." };
            _traceGrid.SelectionChanged += (s, e) => ShowTraceDetail(_traceGrid.SelectedItem);

            // Detail panel
            _lblDetailTitle = new Label { Dock = DockStyle.Top, Height = 28, Font = Theme.Heading, ForeColor = Theme.Brand, Padding = new Padding(4, 4, 0, 0), Text = "Select a trace" };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false, Padding = new Padding(2, 2, 2, 2) };
            actions.Controls.Add(Theme.SecondaryButton("Copy ID", (s, e) => CopyToClipboard(_traceGrid.SelectedItem?.Id.ToString(), "Trace ID")));
            actions.Controls.Add(Theme.SecondaryButton("Copy correlation ID", (s, e) => CopyToClipboard(_traceGrid.SelectedItem?.CorrelationId?.ToString(), "Correlation ID")));
            actions.Controls.Add(Theme.SecondaryButton("Copy request ID", (s, e) => CopyToClipboard(_traceGrid.SelectedItem?.RequestId?.ToString(), "Request ID")));
            actions.Controls.Add(Theme.SecondaryButton("Copy diagnostic summary", (s, e) => CopyToClipboard(DiagnosticSummary(_traceGrid.SelectedItem), "Diagnostic summary")));
            _btnPrevInChain = Theme.SecondaryButton("◀ Chain", (s, e) => MoveInChain(-1));
            _btnNextInChain = Theme.SecondaryButton("Chain ▶", (s, e) => MoveInChain(1));
            actions.Controls.Add(_btnPrevInChain);
            actions.Controls.Add(_btnNextInChain);

            var find = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, WrapContents = false, Padding = new Padding(2, 2, 2, 2) };
            find.Controls.Add(new Label { Text = "Find in detail", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(2, 6, 4, 0) });
            _txtFind = new TextBox { Width = 200 };
            _txtFind.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { FindInDetail(); e.SuppressKeyPress = true; } };
            find.Controls.Add(_txtFind);
            find.Controls.Add(Theme.SecondaryButton("Find next", (s, e) => FindInDetail()));

            _traceDetail = DetailBox();

            _relatedGrid = new VirtualGrid<RelatedTrace>(new[]
            {
                new GridColumn<RelatedTrace>("Evidence", r => (int)r.Level, 190) { Display = r => EvidenceText.Label(r.Level) + (r.Role != null ? " · " + r.Role : ""), ForeColor = r => Theme.For(r.Level) },
                new GridColumn<RelatedTrace>("Created (UTC)", r => r.Trace.CreatedOnUtc, 125) { Display = r => r.Trace.CreatedOnUtc.ToString("yyyy-MM-dd HH:mm:ss") },
                new GridColumn<RelatedTrace>("!", r => r.Trace.HasException ? 1 : 0, 26) { Display = r => r.Trace.HasException ? "✖" : "", ForeColor = r => r.Trace.HasException ? Theme.Error : (Color?)null },
                new GridColumn<RelatedTrace>("Plug-in type", r => r.Trace.ShortTypeName, 150, fill: true),
                new GridColumn<RelatedTrace>("Message", r => r.Trace.MessageName, 80),
                new GridColumn<RelatedTrace>("Depth", r => r.Trace.Depth ?? -1, 48, rightAlign: true) { Display = r => r.Trace.Depth?.ToString() ?? "" },
            }) { Dock = DockStyle.Fill, EmptyText = "No related traces." };
            _relatedGrid.SelectionChanged += (s, e) => _lblRelatedHint.Text = _relatedGrid.SelectedItem?.Explanation ?? "Select a related trace to see why it is linked. Double-click to open it.";
            _relatedGrid.CellDoubleClick += (s, e) => OpenRelated(_relatedGrid.SelectedItem);
            _lblRelatedHint = new Label { Dock = DockStyle.Bottom, Height = 52, ForeColor = Theme.Muted, Padding = new Padding(4), Text = "Select a related trace to see why it is linked. Double-click to open it." };

            var related = new Panel { Dock = DockStyle.Fill };
            related.Controls.Add(_relatedGrid);
            related.Controls.Add(_lblRelatedHint);
            related.Controls.Add(Section("Related traces (strongest evidence first)"));

            var detailSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.Line };
            detailSplit.Panel1.Controls.Add(_traceDetail);
            detailSplit.Panel2.Controls.Add(related);
            detailSplit.HandleCreated += (s, e) => { try { detailSplit.SplitterDistance = (int)(detailSplit.Height * 0.58); } catch (InvalidOperationException) { } };

            var detailPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card };
            detailPanel.Controls.Add(detailSplit);
            detailPanel.Controls.Add(find);
            detailPanel.Controls.Add(actions);
            detailPanel.Controls.Add(_lblDetailTitle);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Theme.Line };
            split.Panel1.Controls.Add(_traceGrid);
            split.Panel2.Controls.Add(detailPanel);
            split.HandleCreated += (s, e) => { try { split.SplitterDistance = (int)(split.Width * 0.52); } catch (InvalidOperationException) { } };

            _tabTraces.Controls.Add(split);
            _tabTraces.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });
            _tabTraces.Controls.Add(bar);
        }

        private void BuildReport()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Card, Padding = new Padding(8, 5, 8, 4), WrapContents = false };
            bar.Controls.Add(Theme.SecondaryButton("⟳ Refresh preview", (s, e) => RefreshReportPreview()));
            bar.Controls.Add(Theme.SecondaryButton("Save HTML…", (s, e) => Export("html")));
            bar.Controls.Add(Theme.SecondaryButton("Save JSON…", (s, e) => Export("json")));
            bar.Controls.Add(Theme.SecondaryButton("Save findings CSV…", (s, e) => Export("findings")));
            _lblReportHint = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(12, 7, 0, 0) };
            bar.Controls.Add(_lblReportHint);

            _reportBrowser = new WebBrowser
            {
                Dock = DockStyle.Fill, ScriptErrorsSuppressed = true, IsWebBrowserContextMenuEnabled = false,
                WebBrowserShortcutsEnabled = true, AllowWebBrowserDrop = false,
            };
            // The report has no links to follow; block every navigation except the in-memory document.
            _reportBrowser.Navigating += (s, e) => { if (e.Url != null && e.Url.Scheme != "about") e.Cancel = true; };

            _tabReport.Controls.Add(_reportBrowser);
            _tabReport.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });
            _tabReport.Controls.Add(bar);
        }

        private static RichTextBox DetailBox() => new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Card, Font = Theme.Base,
            DetectUrls = false, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical, HideSelection = false,
        };

        private static Label Section(string text) => new Label
        {
            Dock = DockStyle.Top, Height = 26, Text = text, ForeColor = Theme.BrandDark, Font = Theme.Bold, BackColor = Theme.Card, Padding = new Padding(6, 5, 0, 0),
        };

        // ================================================================ range helpers

        private bool _settingRange;

        private void ApplyRangePreset()
        {
            var hours = RangePresets[Math.Max(0, _cboRange.SelectedIndex)].Hours;
            if (hours <= 0) return;
            _settingRange = true;
            var now = DateTime.Now;
            _dtTo.Value = now;
            _dtFrom.Value = now.AddHours(-hours);
            _settingRange = false;
        }

        private void SelectRangePreset(int hours)
        {
            var index = Array.FindIndex(RangePresets, p => p.Hours == hours && hours > 0);
            _cboRange.SelectedIndex = index >= 0 ? index : 1;
            ApplyRangePreset();
        }

        private void MarkCustomRange()
        {
            if (_settingRange) return;
            _cboRange.SelectedIndex = RangePresets.Length - 1;
        }

        private int RangeHours() => RangePresets[Math.Max(0, _cboRange.SelectedIndex)].Hours;

        private (DateTime From, DateTime To) CurrentRange()
        {
            if (RangeHours() > 0) ApplyRangePreset();   // presets are relative to now
            return (_dtFrom.Value.ToUniversalTime(), _dtTo.Value.ToUniversalTime());
        }

        private void ClearFilters()
        {
            _cboExceptions.SelectedIndex = 0;
            _cboMessage.Text = "";
            _txtTable.Text = _txtType.Text = _txtCorrelation.Text = _txtRequest.Text = "";
            _numMinDuration.Value = 0;
        }
    }
}
