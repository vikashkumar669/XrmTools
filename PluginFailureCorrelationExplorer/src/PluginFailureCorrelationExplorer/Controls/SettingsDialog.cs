using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Controls
{
    /// <summary>Edits <see cref="AnalysisOptions"/>. Returns a new instance; the input is never modified.</summary>
    internal sealed class SettingsDialog : Form
    {
        private readonly AnalysisOptions _options;
        private readonly List<(NumericUpDown Box, Action<AnalysisOptions, decimal> Apply)> _numbers = new List<(NumericUpDown, Action<AnalysisOptions, decimal>)>();
        private ComboBox _grouping;
        private CheckBox _snapshots;
        private DataGridView _rules;
        private TextBox _sensitive;

        private SettingsDialog(AnalysisOptions options, string connectionName)
        {
            _options = options.Clone();
            Text = "Settings" + (string.IsNullOrEmpty(connectionName) ? "" : " – " + connectionName);
            Font = Theme.Base;
            BackColor = Theme.Surface;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(820, 720);
            MinimumSize = new Size(700, 560);
            Build();
        }

        public static AnalysisOptions Edit(IWin32Window owner, AnalysisOptions options, string connectionName)
        {
            using (var dialog = new SettingsDialog(options, connectionName))
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._options : null;
        }

        private void Build()
        {
            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
            tabs.TabPages.Add(AnalysisPage());
            tabs.TabPages.Add(RulesPage());
            tabs.TabPages.Add(PrivacyPage());

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(8), BackColor = Theme.Card };
            var ok = Theme.PrimaryButton("Save", (s, e) => Save());
            ok.Width = 100;
            var cancel = Theme.SecondaryButton("Cancel", (s, e) => { DialogResult = DialogResult.Cancel; Close(); });
            var reset = Theme.SecondaryButton("Restore defaults", (s, e) => RestoreDefaults());
            buttons.Controls.AddRange(new Control[] { ok, cancel, reset });
            CancelButton = cancel;

            Controls.Add(tabs);
            Controls.Add(buttons);
        }

        private TabPage AnalysisPage()
        {
            var page = new TabPage("Analysis") { BackColor = Theme.Card, Padding = new Padding(12), AutoScroll = true };
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            Number(grid, "Slow threshold, synchronous (ms)", _options.SlowSyncThresholdMs, 1, 600000, 0, (o, v) => o.SlowSyncThresholdMs = (int)v, "Synchronous executions at or above this are slow.");
            Number(grid, "Slow threshold, asynchronous (ms)", _options.SlowAsyncThresholdMs, 1, 3600000, 0, (o, v) => o.SlowAsyncThresholdMs = (int)v, "Asynchronous executions at or above this are slow.");
            Number(grid, "Near-timeout limit, synchronous (ms)", _options.NearTimeoutMs, 1000, 120000, 0, (o, v) => o.NearTimeoutMs = (int)v, "Synchronous plug-ins are cancelled at 120,000 ms.");
            Number(grid, "Minimum sample for average and median", _options.MinSampleForStatistics, 1, 1000, 0, (o, v) => o.MinSampleForStatistics = (int)v, "Measured executions needed.");
            Number(grid, "Minimum sample for 95th percentile", _options.MinSampleForPercentile, 2, 10000, 0, (o, v) => o.MinSampleForPercentile = (int)v, "Nearest-rank percentile.");
            Number(grid, "Recurring failure: minimum occurrences", _options.RecurringMinOccurrences, 2, 100000, 0, (o, v) => o.RecurringMinOccurrences = (int)v, "Same fingerprint at least this often.");
            Number(grid, "High frequency: occurrences per hour", (decimal)_options.HighFrequencyPerHour, 1, 100000, 1, (o, v) => o.HighFrequencyPerHour = (double)v, "Over the group's active span (minimum 1 hour).");
            Number(grid, "Heuristic correlation window (seconds)", _options.HeuristicWindowSeconds, 1, 600, 0, (o, v) => o.HeuristicWindowSeconds = (int)v, "Only for traces without a shared correlation ID.");
            Number(grid, "Parent/child timing tolerance (ms)", _options.ParentChildToleranceMs, 0, 60000, 0, (o, v) => o.ParentChildToleranceMs = (int)v, "Clock tolerance for suspected nesting.");
            Number(grid, "Increase ratio vs. baseline", (decimal)_options.IncreaseRatio, 1.1m, 100, 1, (o, v) => o.IncreaseRatio = (double)v, "Hourly rate multiple that counts as increasing.");
            Number(grid, "Regression ratio vs. baseline", (decimal)_options.RegressionRatio, 1.1m, 100, 1, (o, v) => o.RegressionRatio = (double)v, "Median multiple that counts as a regression.");
            Number(grid, "Stack frames in fingerprint", _options.FingerprintStackFrames, 0, 10, 0, (o, v) => o.FingerprintStackFrames = (int)v, "Top user-code frames (0 = message only).");

            _grouping = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
            _grouping.Items.AddRange(new object[]
            {
                "Fingerprint (type, code, message, plug-in, frames)",
                "Exception and message only (across plug-ins)",
                "Fingerprint, split by message and table",
            });
            _grouping.SelectedIndex = (int)_options.Grouping;
            Row(grid, "Group failures by", _grouping, "Changing this makes saved baselines incomparable.");

            _snapshots = new CheckBox { Text = "Save an aggregate snapshot after each analysis", Checked = _options.SaveBaselineSnapshots, AutoSize = true };
            Row(grid, "Baseline snapshots", _snapshots, "Counts and statistics only, stored locally per connection.");

            page.Controls.Add(grid);
            return page;
        }

        private TabPage RulesPage()
        {
            var page = new TabPage("Normalization rules") { BackColor = Theme.Card, Padding = new Padding(12) };
            var help = new Label
            {
                Dock = DockStyle.Top, Height = 54, ForeColor = Theme.Muted,
                Text = "Custom rules replace volatile values (order numbers, record names…) before the built-in rules run, so recurring failures group together. " +
                       "Pattern is a .NET regular expression (case-insensitive, 250 ms timeout). Changing rules makes saved baselines incomparable.",
            };
            _rules = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = true, RowHeadersVisible = false, BackgroundColor = Theme.Card, BorderStyle = BorderStyle.FixedSingle,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            };
            _rules.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "On", Name = "Enabled", FillWeight = 10 });
            _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", Name = "RuleName", FillWeight = 25 });
            _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Pattern (regex)", Name = "Pattern", FillWeight = 45 });
            _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Replace with", Name = "Token", FillWeight = 20 });
            foreach (var r in _options.CustomRules) _rules.Rows.Add(r.Enabled, r.Name, r.Pattern, r.Token);
            page.Controls.Add(_rules);
            page.Controls.Add(help);
            return page;
        }

        private TabPage PrivacyPage()
        {
            var page = new TabPage("Report privacy") { BackColor = Theme.Card, Padding = new Padding(12) };
            var help = new Label
            {
                Dock = DockStyle.Top, Height = 70, ForeColor = Theme.Muted,
                Text = "Values listed here (one per line, matched case-insensitively) are always removed from exported reports, " +
                       "for example customer names or project codes. Credentials such as tokens, passwords and client secrets are always removed, " +
                       "and GUIDs, e-mail addresses and URL query strings are removed when \"Redact exports\" is on.",
            };
            _sensitive = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono, Text = string.Join(Environment.NewLine, _options.SensitiveValues) };
            page.Controls.Add(_sensitive);
            page.Controls.Add(help);
            return page;
        }

        private void Number(TableLayoutPanel grid, string label, decimal value, decimal min, decimal max, int decimals, Action<AnalysisOptions, decimal> apply, string hint)
        {
            var box = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals > 0 ? 0.5m : 1, Width = 110, ThousandsSeparator = true, TextAlign = HorizontalAlignment.Right };
            box.Value = Math.Max(min, Math.Min(max, value));
            _numbers.Add((box, apply));
            Row(grid, label, box, hint);
        }

        private static void Row(TableLayoutPanel grid, string label, Control control, string hint)
        {
            grid.RowCount++;
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 8, 4) });
            control.Margin = new Padding(0, 4, 8, 4);
            grid.Controls.Add(control);
            grid.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(0, 9, 0, 4) });
            if (control is CheckBox || control is ComboBox) grid.SetColumnSpan(control, 1);
        }

        private void Save()
        {
            var rules = new List<NormalizationRule>();
            foreach (DataGridViewRow row in _rules.Rows)
            {
                if (row.IsNewRow) continue;
                var pattern = Convert.ToString(row.Cells["Pattern"].Value)?.Trim();
                if (string.IsNullOrEmpty(pattern)) continue;
                var name = Convert.ToString(row.Cells["RuleName"].Value)?.Trim();
                try { _ = new Regex(pattern); }
                catch (ArgumentException ex)
                {
                    MessageBox.Show(this, $"The pattern of rule \"{name ?? pattern}\" isn't a valid regular expression:\n\n{ex.Message}", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                rules.Add(new NormalizationRule
                {
                    Enabled = row.Cells["Enabled"].Value is bool b ? b : true,
                    Name = string.IsNullOrEmpty(name) ? pattern : name,
                    Pattern = pattern,
                    Token = string.IsNullOrWhiteSpace(Convert.ToString(row.Cells["Token"].Value)) ? "<custom>" : Convert.ToString(row.Cells["Token"].Value).Trim(),
                });
            }

            foreach (var (box, apply) in _numbers) apply(_options, box.Value);
            _options.Grouping = (GroupingMode)_grouping.SelectedIndex;
            _options.SaveBaselineSnapshots = _snapshots.Checked;
            _options.CustomRules = rules;
            _options.SensitiveValues = _sensitive.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
            _options.Normalize();
            DialogResult = DialogResult.OK;
            Close();
        }

        private void RestoreDefaults()
        {
            if (MessageBox.Show(this, "Restore every analysis setting to its default? Custom rules and sensitive values are kept.", "Settings",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var defaults = new AnalysisOptions();
            var map = new Dictionary<int, decimal>
            {
                [0] = defaults.SlowSyncThresholdMs, [1] = defaults.SlowAsyncThresholdMs, [2] = defaults.NearTimeoutMs,
                [3] = defaults.MinSampleForStatistics, [4] = defaults.MinSampleForPercentile, [5] = defaults.RecurringMinOccurrences,
                [6] = (decimal)defaults.HighFrequencyPerHour, [7] = defaults.HeuristicWindowSeconds, [8] = defaults.ParentChildToleranceMs,
                [9] = (decimal)defaults.IncreaseRatio, [10] = (decimal)defaults.RegressionRatio, [11] = defaults.FingerprintStackFrames,
            };
            for (var i = 0; i < _numbers.Count && map.ContainsKey(i); i++) _numbers[i].Box.Value = map[i];
            _grouping.SelectedIndex = (int)defaults.Grouping;
        }
    }
}
