using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using DiagnosticsWorkbench.Models;

namespace DiagnosticsWorkbench.Controls
{
    /// <summary>Thresholds, row cap, threshold overrides and custom normalization rules for the current connection.</summary>
    internal sealed class SettingsDialog : Form
    {
        private readonly NumericUpDown _sync, _async, _near, _cap;
        private readonly FastGrid _overrides, _rules;

        public WorkbenchSettings Result { get; private set; }

        private SettingsDialog(WorkbenchSettings settings, string connectionName, IEnumerable<string> knownTypes)
        {
            Text = "Diagnostics Workbench settings";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(760, 600);
            MinimumSize = new Size(640, 520);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            layout.Controls.Add(new Label
            {
                AutoSize = true, ForeColor = Palette.Muted, Margin = new Padding(0, 0, 0, 8),
                Text = "Saved for this connection: " + (string.IsNullOrEmpty(connectionName) ? "(not connected)" : connectionName)
            });

            var numbers = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Dock = DockStyle.Top };
            _sync = Number(settings.SyncThresholdMs, 1, 600000);
            _async = Number(settings.AsyncThresholdMs, 1, 3600000);
            _near = Number(settings.NearTimeoutMs, 1000, 120000);
            _cap = Number(settings.RowCap, 100, 1000000);
            _cap.Increment = 5000;
            AddPair(numbers, "Slow threshold, synchronous (ms):", _sync, "Slow threshold, asynchronous (ms):", _async);
            AddPair(numbers, "Near-timeout warning (ms, sync):", _near, "Row cap per load:", _cap);
            layout.Controls.Add(numbers);

            layout.Controls.Add(Heading("Threshold overrides  ·  most specific wins: type + message, then type, then the defaults above"));
            _overrides = new FastGrid { Dock = DockStyle.Fill, ReadOnly = false, AllowUserToAddRows = true, AllowUserToDeleteRows = true, SelectionMode = DataGridViewSelectionMode.CellSelect, EditMode = DataGridViewEditMode.EditOnEnter };
            var typeCol = new DataGridViewComboBoxColumn { HeaderText = "Plugin type (full or class name)", Width = 360, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox };
            var types = (knownTypes ?? Enumerable.Empty<string>()).Concat(settings.ThresholdOverrides.Select(o => o.TypeName))
                .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray();
            typeCol.Items.AddRange(types);
            _overrides.Columns.Add(typeCol);
            _overrides.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Message (blank = all)", Width = 160 });
            _overrides.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Threshold (ms)", Width = 120 });
            _overrides.DataError += (s, e) => e.ThrowException = false;
            foreach (var o in settings.ThresholdOverrides) _overrides.Rows.Add(o.TypeName, o.MessageName, o.ThresholdMs.ToString());
            layout.Controls.Add(_overrides);

            layout.Controls.Add(Heading("Custom normalization rules  ·  applied before the built-in ones; each regex match becomes the token"));
            _rules = new FastGrid { Dock = DockStyle.Fill, ReadOnly = false, AllowUserToAddRows = true, AllowUserToDeleteRows = true, SelectionMode = DataGridViewSelectionMode.CellSelect, EditMode = DataGridViewEditMode.EditOnEnter };
            _rules.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "On", Width = 40 });
            _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", Width = 150 });
            _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Regex pattern", Width = 340 });
            _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Token", Width = 110 });
            foreach (var r in settings.CustomRules) _rules.Rows.Add(r.Enabled, r.Name, r.Pattern, r.Token);
            _rules.DefaultValuesNeeded += (s, e) => { e.Row.Cells[0].Value = true; e.Row.Cells[3].Value = "<id>"; };
            layout.Controls.Add(_rules);

            layout.Controls.Add(new Label
            {
                AutoSize = true, ForeColor = Palette.Muted, Margin = new Padding(0, 6, 0, 0),
                Text = "Example rule: name \"order number\", pattern  ORD-\\d+  , token  <order>.  Changing rules re-groups the loaded logs."
            });

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
            var ok = new Button { Text = "Save", Width = 90 };
            var reset = new Button { Text = "Reset defaults", Width = 120 };
            ok.Click += (s, e) => Save();
            reset.Click += (s, e) =>
            {
                _sync.Value = WorkbenchSettings.DefaultSyncThresholdMs;
                _async.Value = WorkbenchSettings.DefaultAsyncThresholdMs;
                _near.Value = WorkbenchSettings.DefaultNearTimeoutMs;
                _cap.Value = WorkbenchSettings.DefaultRowCap;
            };
            buttons.Controls.AddRange(new Control[] { cancel, ok, reset });
            layout.Controls.Add(buttons);

            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = cancel;
            Result = settings;
        }

        public static WorkbenchSettings Edit(IWin32Window owner, WorkbenchSettings settings, string connectionName, IEnumerable<string> knownTypes)
        {
            using (var dlg = new SettingsDialog(settings.Clone(), connectionName, knownTypes))
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.Result : null;
        }

        private void Save()
        {
            _overrides.EndEdit();
            _rules.EndEdit();
            var result = Result.Clone();
            result.SyncThresholdMs = (int)_sync.Value;
            result.AsyncThresholdMs = (int)_async.Value;
            result.NearTimeoutMs = (int)_near.Value;
            result.RowCap = (int)_cap.Value;

            result.ThresholdOverrides = new List<ThresholdOverride>();
            foreach (DataGridViewRow row in _overrides.Rows)
            {
                if (row.IsNewRow) continue;
                var type = Convert.ToString(row.Cells[0].Value)?.Trim();
                var message = Convert.ToString(row.Cells[1].Value)?.Trim();
                var msText = Convert.ToString(row.Cells[2].Value)?.Trim();
                if (string.IsNullOrEmpty(type) && string.IsNullOrEmpty(msText)) continue;
                if (string.IsNullOrEmpty(type) || !int.TryParse(msText, out var ms) || ms <= 0)
                {
                    MessageBox.Show(this, $"Override row {row.Index + 1} needs a plugin type and a threshold above 0 ms.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _overrides.CurrentCell = row.Cells[string.IsNullOrEmpty(type) ? 0 : 2];
                    return;
                }
                result.ThresholdOverrides.Add(new ThresholdOverride { TypeName = type, MessageName = message, ThresholdMs = ms });
            }

            result.CustomRules = new List<NormalizationRule>();
            foreach (DataGridViewRow row in _rules.Rows)
            {
                if (row.IsNewRow) continue;
                var pattern = Convert.ToString(row.Cells[2].Value);
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                try { _ = new Regex(pattern); }
                catch (ArgumentException ex)
                {
                    MessageBox.Show(this, $"Rule row {row.Index + 1}: the pattern isn't a valid regex.\n\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _rules.CurrentCell = row.Cells[2];
                    return;
                }
                result.CustomRules.Add(new NormalizationRule
                {
                    Enabled = row.Cells[0].Value is bool b ? b : true,
                    Name = Convert.ToString(row.Cells[1].Value)?.Trim(),
                    Pattern = pattern,
                    Token = string.IsNullOrWhiteSpace(Convert.ToString(row.Cells[3].Value)) ? "<custom>" : Convert.ToString(row.Cells[3].Value).Trim(),
                });
            }

            Result = result;
            DialogResult = DialogResult.OK;
            Close();
        }

        private static NumericUpDown Number(int value, int min, int max) => new NumericUpDown
        {
            Minimum = min, Maximum = max, Width = 110, ThousandsSeparator = true, Increment = 500,
            Value = Math.Max(min, Math.Min(max, value)), Margin = new Padding(3, 3, 24, 3)
        };

        private static void AddPair(TableLayoutPanel panel, string label1, Control c1, string label2, Control c2)
        {
            panel.Controls.Add(new Label { Text = label1, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 3) });
            panel.Controls.Add(c1);
            panel.Controls.Add(new Label { Text = label2, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 3) });
            panel.Controls.Add(c2);
        }

        private static Label Heading(string text) => new Label
        {
            Text = text, AutoSize = true, ForeColor = Palette.Brand, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Margin = new Padding(0, 12, 0, 4)
        };
    }
}
