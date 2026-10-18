using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using ThemeMigrator.Controls;
using ThemeMigrator.Models;
using ThemeMigrator.Services;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Args;
using XrmToolBox.Extensibility.Interfaces;
using Label = System.Windows.Forms.Label;

namespace ThemeMigrator
{
    /// <summary>
    /// Main UI. The XrmToolBox main connection is the SOURCE; "Select target" adds a second connection (TARGET).
    /// </summary>
    public class ThemeMigratorControl : MultipleConnectionsPluginControlBase, IGitHubPlugin, IHelpPlugin, IStatusBarMessenger
    {
        // IGitHubPlugin / IHelpPlugin
        public string RepositoryName => "XrmTools";
        public string UserName => "vikashkumar669";
        public string HelpUrl => "https://github.com/vikashkumar669/XrmTools#theme-migrator";

        public event EventHandler<StatusBarMessageEventArgs> SendMessageToStatusBar;

        private IOrganizationService _targetService;
        private ConnectionDetail _targetDetail;
        private List<ClassicThemeItem> _classicThemes = new List<ClassicThemeItem>();
        private List<ModernSettingItem> _modernSettings = new List<ModernSettingItem>();

        // UI
        private ToolStrip _toolbar;
        private ToolStripButton _tsbLoad, _tsbTarget, _tsbMigrate, _tsbClose;
        private ToolStripButton _tsbPublish;
        private Label _lblSource, _lblTarget;
        private TabControl _tabs;
        private CheckListView _lvClassic, _lvModern;
        private ThemePreviewPanel _classicPreview, _modernPreview;
        private TextBox _txtModernXml;
        private RichTextBox _log;

        public ThemeMigratorControl()
        {
            BuildUi();
            UpdateConnectionLabels();
            UpdateButtons();
        }

        // ================= Connections =================

        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            base.UpdateConnection(newService, detail, actionName, parameter);
            if (string.IsNullOrEmpty(actionName))
            {
                // Source changed: previous data no longer applies
                _classicThemes.Clear();
                _modernSettings.Clear();
                BindLists();
                Log(LogLevel.Info, $"Source connected: {detail?.ConnectionName}");
            }
            UpdateConnectionLabels();
            UpdateButtons();
        }

        protected override void ConnectionDetailsUpdated(NotifyCollectionChangedEventArgs e)
        {
            // Keep exactly one target connection: the most recently added
            if (e.Action == NotifyCollectionChangedAction.Add && AdditionalConnectionDetails.Count > 1)
            {
                foreach (var old in AdditionalConnectionDetails.Take(AdditionalConnectionDetails.Count - 1).ToList())
                    RemoveAdditionalOrganization(old);
                return; // the removal raises this event again
            }

            _targetDetail = AdditionalConnectionDetails.LastOrDefault();
            _targetService = _targetDetail?.GetCrmServiceClient();

            if (_targetDetail != null && ConnectionDetail != null &&
                string.Equals(_targetDetail.OrganizationServiceUrl, ConnectionDetail.OrganizationServiceUrl, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "The target is the same environment as the source. Please choose a different target.",
                    "Theme Migrator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (_targetDetail != null) Log(LogLevel.Info, $"Target connected: {_targetDetail.ConnectionName}");
            UpdateConnectionLabels();
            UpdateButtons();

            if (_targetService != null && (_classicThemes.Count > 0 || _modernSettings.Count > 0))
                CompareWithTarget();
        }

        private void UpdateConnectionLabels()
        {
            _lblSource.Text = "Source:  " + (ConnectionDetail?.ConnectionName ?? "(not connected — use Connect in XrmToolBox)");
            _lblTarget.Text = "Target:  " + (_targetDetail?.ConnectionName ?? "(not selected — click 'Select target')");
        }

        private void UpdateButtons()
        {
            var hasItems = _classicThemes.Count > 0 || _modernSettings.Count > 0;
            _tsbMigrate.Enabled = Service != null && _targetService != null && hasItems;
        }

        // ================= Load =================

        private void LoadThemes()
        {
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading themes from source...",
                Work = (worker, args) =>
                {
                    var classic = ThemeReader.GetClassicThemes(Service);
                    List<ModernSettingItem> modern;
                    string modernError = null;
                    try
                    {
                        modern = ThemeReader.GetModernSettings(Service);
                    }
                    catch (Exception ex)
                    {
                        modern = new List<ModernSettingItem>();
                        modernError = ex.Message;
                    }

                    string compareError = null;
                    if (_targetService != null)
                    {
                        try { ThemeReader.MarkTargetState(_targetService, classic, modern); }
                        catch (Exception ex) { compareError = ex.Message; }
                    }
                    args.Result = Tuple.Create(classic, modern, modernError, compareError);
                },
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        Log(LogLevel.Error, "Loading failed: " + args.Error.Message);
                        MessageBox.Show(this, args.Error.Message, "Theme Migrator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    var r = (Tuple<List<ClassicThemeItem>, List<ModernSettingItem>, string, string>)args.Result;
                    _classicThemes = r.Item1;
                    _modernSettings = r.Item2;
                    BindLists();
                    Log(LogLevel.Info, $"Loaded {_classicThemes.Count} classic theme(s) and {_modernSettings.Count} modern theme setting value(s).");
                    if (r.Item3 != null) Log(LogLevel.Warning, "Modern theme settings couldn't be read from the source: " + r.Item3);
                    if (r.Item4 != null) Log(LogLevel.Warning, "Couldn't compare with target: " + r.Item4);
                    if (_modernSettings.Count == 0 && r.Item3 == null)
                        Log(LogLevel.Info, "No 'Custom theme definition' or 'Override app header color' values are set in the source.");
                    SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs(
                        $"{_classicThemes.Count} classic theme(s), {_modernSettings.Count} modern setting(s)"));
                    UpdateButtons();
                }
            });
        }

        private void CompareWithTarget()
        {
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Comparing with target...",
                Work = (worker, args) => ThemeReader.MarkTargetState(_targetService, _classicThemes, _modernSettings),
                PostWorkCallBack = args =>
                {
                    if (args.Error != null) Log(LogLevel.Warning, "Couldn't compare with target: " + args.Error.Message);
                    BindLists();
                }
            });
        }

        // ================= Migrate =================

        private void Migrate()
        {
            var themes = _lvClassic.CheckedItems.Cast<ListViewItem>().Select(i => (ClassicThemeItem)i.Tag).ToList();
            var settings = _lvModern.CheckedItems.Cast<ListViewItem>().Select(i => (ModernSettingItem)i.Tag).ToList();

            if (themes.Count == 0 && settings.Count == 0)
            {
                MessageBox.Show(this, "Tick at least one classic theme or modern theme setting to migrate.",
                    "Theme Migrator", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var options = new MigrationOptions { PublishAndSetDefault = _tsbPublish.Checked };

            // System themes ship with the same GUIDs everywhere; overwriting them is rarely intended
            var systemThemes = themes.Where(t => t.IsSystem).ToList();
            if (systemThemes.Count > 0)
            {
                var answer = MessageBox.Show(this,
                    "You ticked Microsoft system theme(s):\n" + string.Join("\n", systemThemes.Select(t => "  • " + t.Name)) +
                    "\n\nThese exist in every environment with the same GUID. Migrating them overwrites the target's copy " +
                    "(Dataverse may also refuse). Usually you should migrate a custom theme instead.\n\nInclude them anyway?",
                    "System themes selected", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.No)
                {
                    themes = themes.Where(t => !t.IsSystem).ToList();
                    if (themes.Count == 0 && settings.Count == 0) return;
                }
            }

            if (options.PublishAndSetDefault && themes.Count > 1)
            {
                MessageBox.Show(this,
                    "Only one classic theme can be the default. Tick exactly one classic theme, or clear 'Publish and set as default' to copy several without publishing.",
                    "Theme Migrator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var lines = new List<string>();
            lines.AddRange(themes.Take(10).Select(t => $"  • Classic theme: {t.Name} ({StateText(t.TargetState)})"));
            if (themes.Count > 10) lines.Add($"  • …and {themes.Count - 10} more classic theme(s)");
            lines.AddRange(settings.Take(10).Select(s =>
                $"  • {s.SettingDisplayName}: {(s.Scope == SettingScope.App ? "app " + (s.AppName ?? s.AppUniqueName) : "environment")} = {s.Value} ({StateText(s.TargetState)})"));
            if (settings.Count > 10) lines.Add($"  • …and {settings.Count - 10} more setting(s)");

            var publishText = options.PublishAndSetDefault
                ? "\n\nThe selected classic theme will be published and set as the default, and customizations will be published."
                : "\n\nNothing will be published.";
            var confirm = MessageBox.Show(this,
                $"Migrate from {ConnectionDetail?.ConnectionName} to {_targetDetail?.ConnectionName}:\n\n" +
                string.Join("\n", lines) +
                "\n\nExisting records with the same GUID in the target will be overwritten." + publishText,
                "Confirm migration", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            Log(LogLevel.Info, new string('─', 60));
            Log(LogLevel.Info, $"Migration started: {ConnectionDetail?.ConnectionName} → {_targetDetail?.ConnectionName}");

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Migrating themes...",
                Work = (worker, args) =>
                {
                    var engine = new MigrationEngine(Service, _targetService,
                        (level, message) => worker.ReportProgress(0, Tuple.Create(level, message)));
                    args.Result = engine.Run(themes, settings, options);
                    ThemeReader.MarkTargetState(_targetService, _classicThemes, _modernSettings);
                },
                ProgressChanged = e =>
                {
                    if (e.UserState is Tuple<LogLevel, string> entry)
                    {
                        Log(entry.Item1, entry.Item2);
                        SetWorkingMessage(entry.Item2.Trim());
                    }
                },
                PostWorkCallBack = args =>
                {
                    BindLists();
                    if (args.Error != null)
                    {
                        Log(LogLevel.Error, "Migration stopped: " + args.Error.Message);
                        MessageBox.Show(this, args.Error.Message, "Theme Migrator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    var result = (MigrationResult)args.Result;
                    Log(result.Failed > 0 ? LogLevel.Warning : LogLevel.Success, "Migration finished: " + result);
                    MessageBox.Show(this, "Migration finished: " + result + (result.Failed > 0 ? "\n\nSee the log for details." : ""),
                        "Theme Migrator", MessageBoxButtons.OK,
                        result.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                }
            });
        }

        // ================= Binding & preview =================

        private void BindLists()
        {
            _lvClassic.BeginUpdate();
            _lvClassic.Items.Clear();
            foreach (var t in _classicThemes)
            {
                var item = new ListViewItem(t.Name ?? "(no name)") { Tag = t };
                item.SubItems.Add(t.TypeLabel ?? "");
                item.SubItems.Add(t.IsDefault ? "Yes" : "");
                item.SubItems.Add(t.Logo?.Name ?? "");
                item.SubItems.Add(StateText(t.TargetState));
                item.SubItems.Add(t.Id.ToString());
                if (t.IsDefault) item.Font = new Font(_lvClassic.Font, FontStyle.Bold);
                if (t.IsSystem) item.ForeColor = SystemColors.GrayText;
                _lvClassic.Items.Add(item);
            }
            _lvClassic.EndUpdate();

            _lvModern.BeginUpdate();
            _lvModern.Items.Clear();
            foreach (var s in _modernSettings)
            {
                var item = new ListViewItem(s.SettingDisplayName ?? s.SettingUniqueName) { Tag = s };
                item.SubItems.Add(s.ScopeLabel);
                item.SubItems.Add(s.Scope == SettingScope.App ? (s.AppName ?? s.AppUniqueName ?? "") : "All apps");
                item.SubItems.Add(s.Value ?? "");
                item.SubItems.Add(s.LogoResource?.Name ?? "");
                item.SubItems.Add(StateText(s.TargetState));
                _lvModern.Items.Add(item);
            }
            _lvModern.EndUpdate();

            _tabs.TabPages[0].Text = $"Classic themes ({_classicThemes.Count})";
            _tabs.TabPages[1].Text = $"Modern themes ({_modernSettings.Count})";
            _classicPreview.Clear();
            _modernPreview.Clear("Select a setting to preview its theme.");
            _txtModernXml.Text = "";
            UpdateButtons();
        }

        private static string StateText(TargetState state) =>
            state == TargetState.Exists ? "Exists → update" : state == TargetState.New ? "New → create" : "";

        private void ShowClassicPreview()
        {
            if (_lvClassic.SelectedItems.Count == 0) { _classicPreview.Clear(); return; }
            var t = (ClassicThemeItem)_lvClassic.SelectedItems[0].Tag;
            var colors = t.Colors.OrderBy(c => c.Key).ToList();
            Func<string, string> Get = attr => t.Record.GetAttributeValue<string>(attr);

            string note = null;
            if (t.Logo == null) note = "No logo set on this theme.";
            else if (t.Logo.IsSvg) note = $"Logo '{t.Logo.Name}' is an SVG (not previewed here, but it will be migrated).";

            _classicPreview.ShowTheme(t.Name, Get("navbarbackgroundcolor") ?? Get("headercolor"),
                Get("headercolor") ?? Get("globallinkcolor"), LoadImage(t.Logo), colors, note);
        }

        private void ShowModernPreview()
        {
            if (_lvModern.SelectedItems.Count == 0)
            {
                _modernPreview.Clear("Select a setting to preview its theme.");
                _txtModernXml.Text = "";
                return;
            }
            var s = (ModernSettingItem)_lvModern.SelectedItems[0].Tag;
            var xml = s.ThemeResource?.Text ?? "";
            _txtModernXml.Text = s.ThemeResource != null
                ? $"<!-- Web resource: {s.ThemeResource.Name} ({s.ThemeResource.Id}) -->{Environment.NewLine}{xml.Replace("\n", Environment.NewLine)}"
                : $"Setting value: {s.Value}{Environment.NewLine}(The value doesn't point to a web resource in the source.)";

            var colors = ThemeReader.GetXmlColors(xml);
            var header = colors.FirstOrDefault(c => c.Key.EndsWith(".background", StringComparison.OrdinalIgnoreCase)).Value
                         ?? colors.FirstOrDefault(c => c.Key.EndsWith(".basePaletteColor", StringComparison.OrdinalIgnoreCase)).Value
                         ?? (ColorUtil.IsHex(s.Value) ? s.Value : null);

            var title = s.Scope == SettingScope.App ? (s.AppName ?? s.AppUniqueName) : "All apps (environment value)";
            _modernPreview.ShowTheme(title, header, null, LoadImage(s.LogoResource), colors,
                s.LogoResource == null ? null : $"Logo: {s.LogoResource.Name}");
        }

        private static Image LoadImage(WebResourceInfo resource)
        {
            if (resource == null || !resource.IsRasterImage) return null;
            try
            {
                using (var ms = new MemoryStream(resource.Bytes))
                using (var img = Image.FromStream(ms))
                    return new Bitmap(img);
            }
            catch
            {
                return null;
            }
        }

        // ================= Log =================

        private void Log(LogLevel level, string message)
        {
            if (_log.InvokeRequired) { _log.Invoke(new Action(() => Log(level, message))); return; }
            var color = level == LogLevel.Error ? Color.Firebrick
                : level == LogLevel.Warning ? Color.DarkOrange
                : level == LogLevel.Success ? Color.ForestGreen
                : SystemColors.WindowText;
            _log.SelectionStart = _log.TextLength;
            _log.SelectionColor = SystemColors.GrayText;
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  ");
            _log.SelectionColor = color;
            _log.AppendText(message + Environment.NewLine);
            _log.SelectionColor = _log.ForeColor;
            _log.ScrollToCaret();

            // Also write to the XrmToolBox log file ("{0}" keeps braces in messages from being treated as format items)
            if (level == LogLevel.Error) LogError("{0}", message);
            else if (level == LogLevel.Warning) LogWarning("{0}", message);
            else LogInfo("{0}", message);
        }

        // ================= UI construction =================

        private void BuildUi()
        {
            SuspendLayout();
            Font = new Font("Segoe UI", 9F);

            // Toolbar
            _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2) };
            _tsbClose = new ToolStripButton("Close") { DisplayStyle = ToolStripItemDisplayStyle.Text };
            _tsbClose.Click += (s, e) => CloseTool();
            _tsbLoad = new ToolStripButton("Load themes") { DisplayStyle = ToolStripItemDisplayStyle.Text, Font = new Font(Font, FontStyle.Bold) };
            _tsbLoad.Click += (s, e) => ExecuteMethod(LoadThemes);
            _tsbTarget = new ToolStripButton("Select target") { DisplayStyle = ToolStripItemDisplayStyle.Text };
            _tsbTarget.Click += (s, e) => AddAdditionalOrganization();
            _tsbMigrate = new ToolStripButton("Migrate selected") { DisplayStyle = ToolStripItemDisplayStyle.Text, Font = new Font(Font, FontStyle.Bold) };
            _tsbMigrate.Click += (s, e) => ExecuteMethod(Migrate);
            // A plain toolbar toggle: a CheckBox hosted in a ToolStrip doesn't reliably receive clicks
            _tsbPublish = new ToolStripButton { DisplayStyle = ToolStripItemDisplayStyle.Text, CheckOnClick = true };
            Action updatePublishText = () =>
            {
                _tsbPublish.Text = (_tsbPublish.Checked ? "☑" : "☐") + " Publish and set as default";
                _tsbPublish.ToolTipText = _tsbPublish.Checked
                    ? "On: after migrating, publish the classic theme (making it the default) and publish customizations"
                    : "Off: records are saved in the target but not published";
            };
            _tsbPublish.CheckedChanged += (s, e) => updatePublishText();
            updatePublishText();
            var selectAll = new ToolStripButton("Select all") { DisplayStyle = ToolStripItemDisplayStyle.Text };
            selectAll.Click += (s, e) => SetAllChecked(true);
            var selectNone = new ToolStripButton("Clear") { DisplayStyle = ToolStripItemDisplayStyle.Text };
            selectNone.Click += (s, e) => SetAllChecked(false);

            _toolbar.Items.AddRange(new ToolStripItem[]
            {
                _tsbClose, new ToolStripSeparator(),
                _tsbLoad, _tsbTarget, new ToolStripSeparator(),
                selectAll, selectNone, new ToolStripSeparator(),
                _tsbPublish, _tsbMigrate
            });

            // Connection banner
            var banner = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, ColumnCount = 1, RowCount = 2, Padding = new Padding(8, 4, 8, 2) };
            _lblSource = new Label { AutoSize = true, ForeColor = Color.FromArgb(0x4B, 0x1A, 0x6E), Font = new Font(Font, FontStyle.Bold) };
            _lblTarget = new Label { AutoSize = true, ForeColor = Color.FromArgb(0xB4, 0x2A, 0x9A), Font = new Font(Font, FontStyle.Bold) };
            banner.Controls.Add(_lblSource, 0, 0);
            banner.Controls.Add(_lblTarget, 0, 1);

            // Classic tab
            _lvClassic = NewListView(("Name", 220), ("Type", 90), ("Default", 60), ("Logo web resource", 180), ("In target", 110), ("Theme ID", 250));
            _lvClassic.SelectedIndexChanged += (s, e) => ShowClassicPreview();
            _classicPreview = new ThemePreviewPanel { Dock = DockStyle.Fill };
            var classicSplit = NewSplit(_lvClassic, ScrollHost(_classicPreview), 560);
            var tabClassic = new TabPage("Classic themes") { Padding = new Padding(4) };
            tabClassic.Controls.Add(classicSplit);

            // Modern tab
            _lvModern = NewListView(("Setting", 180), ("Scope", 90), ("App", 170), ("Value (web resource)", 200), ("Logo", 140), ("In target", 110));
            _lvModern.SelectedIndexChanged += (s, e) => ShowModernPreview();
            _modernPreview = new ThemePreviewPanel { Dock = DockStyle.Fill };
            _txtModernXml = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
                WordWrap = false, Font = new Font("Consolas", 9F), BackColor = SystemColors.Window
            };
            var modernRight = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            modernRight.Panel1.Controls.Add(ScrollHost(_modernPreview));
            modernRight.Panel2.Controls.Add(_txtModernXml);
            var modernSplit = NewSplit(_lvModern, modernRight, 560);
            var modernHint = new Label
            {
                Dock = DockStyle.Top, Height = 34, ForeColor = SystemColors.GrayText, Padding = new Padding(2, 2, 2, 0),
                Text = "Values of 'Custom theme definition' and 'Override app header color' (New look). The theme XML web resource " +
                       "and its logo are migrated too. App-level values need the same app (unique name) in the target."
            };
            var tabModern = new TabPage("Modern themes") { Padding = new Padding(4) };
            tabModern.Controls.Add(modernSplit);
            tabModern.Controls.Add(modernHint);

            _tabs = new TabControl { Dock = DockStyle.Fill };
            _tabs.TabPages.Add(tabClassic);
            _tabs.TabPages.Add(tabModern);

            // Log
            _log = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, BackColor = SystemColors.Window, BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9F), DetectUrls = false
            };
            var logGroup = new GroupBox { Text = "Log", Dock = DockStyle.Fill, Padding = new Padding(6) };
            logGroup.Controls.Add(_log);

            var main = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            main.Panel1.Controls.Add(_tabs);
            main.Panel2.Controls.Add(logGroup);

            Controls.Add(main);
            Controls.Add(banner);
            Controls.Add(_toolbar);
            Size = new Size(1100, 720);

            Load += (s, e) =>
            {
                try
                {
                    main.SplitterDistance = Math.Max(200, (int)(main.Height * 0.68));
                    modernRight.SplitterDistance = Math.Max(120, modernRight.Height / 2);
                }
                catch (InvalidOperationException) { /* window too small for preferred split; keep defaults */ }
                Log(LogLevel.Info, "1) Connect to the SOURCE in XrmToolBox  2) Select target  3) Load themes  4) Tick items  5) Migrate selected");
            };
            ResumeLayout();
        }

        private void SetAllChecked(bool value)
        {
            var list = _tabs.SelectedIndex == 0 ? _lvClassic : _lvModern;
            foreach (ListViewItem item in list.Items)
            {
                // "Select all" skips Microsoft system themes; they can still be ticked one by one
                if (value && item.Tag is ClassicThemeItem theme && theme.IsSystem) continue;
                item.Checked = value;
            }
        }

        private static CheckListView NewListView(params (string text, int width)[] columns)
        {
            var lv = new CheckListView
            {
                Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true,
                HideSelection = false, MultiSelect = false, GridLines = true
            };
            foreach (var column in columns) lv.Columns.Add(column.text, column.width);
            return lv;
        }

        private static SplitContainer NewSplit(Control left, Control right, int distance)
        {
            var split = new SplitContainer { Dock = DockStyle.Fill };
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(right);
            split.HandleCreated += (s, e) =>
            {
                try { split.SplitterDistance = Math.Min(distance, Math.Max(100, split.Width - 250)); } catch { /* size not final yet */ }
            };
            return split;
        }

        private static Panel ScrollHost(Control child)
        {
            var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            host.Controls.Add(child);
            return host;
        }
    }
}
