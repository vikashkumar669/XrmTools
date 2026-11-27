using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ChartDesignerPro.Comparison;
using ChartDesignerPro.Editing;
using ChartDesignerPro.Models;
using ChartDesignerPro.Validation;

namespace ChartDesignerPro.Controls
{
    /// <summary>
    /// The last step before a chart is written to Dataverse (Step 2 architecture, section 8 step ⑤): shows where it is
    /// going (environment name and URL), what changes (a diff per changed column), any validation warnings to
    /// acknowledge, the publish choice for system charts, and where the backup goes. Nothing is saved unless the user
    /// clicks Save.
    /// </summary>
    internal sealed class SaveConfirmDialog : Form
    {
        private readonly CheckBox _chkWarnings;
        private readonly RadioButton _rdoPublish;
        private readonly Button _btnSave;

        public SaveConfirmDialog(ChartWorkspace workspace, string environmentName, string environmentUrl,
            IReadOnlyList<ValidationResult> warnings, string conflictMessage, string backupFolder)
        {
            var record = workspace.Record;
            Text = "Chart Designer Pro: save to Dataverse";
            Font = new Font("Segoe UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(900, 720);
            MinimumSize = new Size(700, 560);
            BackColor = Color.White;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 12, 14, 8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // Where it goes
            layout.Controls.Add(new Label
            {
                Text = $"Save '{record.Name}'",
                Font = new Font("Segoe UI", 12F, FontStyle.Bold), ForeColor = Palette.Ink, AutoSize = true, Margin = new Padding(0, 0, 0, 6)
            });
            var target = new Label
            {
                AutoSize = true, MaximumSize = new Size(840, 0), BackColor = Palette.BrandLight, ForeColor = Palette.Ink,
                Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0, 0, 0, 8),
                Text = $"Environment:  {environmentName}\r\n" +
                       $"URL:  {environmentUrl ?? "(not available)"}\r\n" +
                       $"Chart:  {record.Name}  ·  {(record.Scope == ChartScope.System ? "System chart (savedqueryvisualization)" : "Personal chart (userqueryvisualization)")}  ·  table {record.TableText}\r\n" +
                       $"Changes:  {workspace.ChangeSummary()}"
            };
            target.Font = new Font("Segoe UI", 9.5F);
            layout.Controls.Add(target);

            if (!string.IsNullOrEmpty(conflictMessage))
                layout.Controls.Add(Note("⚠ " + conflictMessage + " Saving replaces that version; it is backed up first.", Palette.ErrorBack, Palette.ErrorFore));
            if (record.Scope == ChartScope.System && record.IsManaged == true)
                layout.Controls.Add(Note("ℹ This system chart is managed. Your change goes into an unmanaged layer on top of the managed solution.", Palette.InfoBack, Palette.InfoFore));

            // What changes
            var tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 6) };
            foreach (var doc in new[] { workspace.Data, workspace.Presentation }.Where(d => d.IsDirty))
            {
                // Stored chart XML is usually one long line, so compare indented versions to put each change on its own
                // short line. If only formatting changed, show the exact comparison instead. The XML is saved as edited.
                var diff = LineDiff.Compare(doc.Original, doc.Current, ignoreFormatting: true);
                var formattingOnly = !diff.HasChanges;
                if (formattingOnly) diff = LineDiff.Compare(doc.Original, doc.Current, ignoreFormatting: false);
                var view = new DiffView { Dock = DockStyle.Fill };
                view.ShowDiff(diff, changesOnly: true);
                var page = new TabPage($"{doc.Title}: {diff.Summary}" + (formattingOnly ? " (formatting only)" : "")) { BackColor = Color.White };
                page.Controls.Add(view);
                tabs.TabPages.Add(page);
            }
            layout.Controls.Add(tabs);
            layout.Controls.Add(new Label
            {
                Text = "The diff is shown indented so each change is on its own line. The XML is saved exactly as you edited it.",
                AutoSize = true, ForeColor = Palette.Muted, Margin = new Padding(0, 0, 0, 6)
            });
            layout.RowStyles.Clear();

            // Warnings
            if (warnings != null && warnings.Count > 0)
            {
                var text = string.Join("\r\n", warnings.Take(6).Select(w => $"⚠ {w.RuleId}: {w.Title}")) +
                           (warnings.Count > 6 ? $"\r\n… and {warnings.Count - 6} more (see the Validation tab)" : "");
                layout.Controls.Add(Note(text, Palette.WarnBack, Palette.WarnFore));
                _chkWarnings = new CheckBox { Text = $"I've reviewed the {warnings.Count} warning(s) and want to save anyway", AutoSize = true, Margin = new Padding(0, 2, 0, 6) };
                _chkWarnings.CheckedChanged += (s, e) => UpdateSave();
                layout.Controls.Add(_chkWarnings);
            }

            // Publish
            if (record.Scope == ChartScope.System)
            {
                var table = string.IsNullOrEmpty(record.TableDisplayName) ? record.TableLogicalName : record.TableDisplayName;
                _rdoPublish = new RadioButton { Text = $"Save and publish the {table} table (recommended: users see the change after publishing)", AutoSize = true, Checked = true, Margin = new Padding(0, 2, 0, 0) };
                _rdoPublish.CheckedChanged += (s, e) => UpdateSave();
                var noPublish = new RadioButton { Text = "Save without publishing (the change stays unpublished until you or someone else publishes)", AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
                var group = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
                group.Controls.Add(_rdoPublish);
                group.Controls.Add(noPublish);
                layout.Controls.Add(group);
            }
            else
            {
                layout.Controls.Add(new Label { Text = "Personal charts take effect when saved; they don't need publishing.", AutoSize = true, ForeColor = Palette.Muted, Margin = new Padding(0, 2, 0, 6) });
            }

            layout.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(840, 0), ForeColor = Palette.Muted, Margin = new Padding(0, 0, 0, 8),
                Text = "Before saving, the version currently in Dataverse is backed up to:\r\n" + backupFolder + "\r\n" +
                       "There is no automatic undo. To go back, import the backup into the chart (⇧ Import…) and save it again."
            });

            // Buttons
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(96, 30) };
            _btnSave = new Button
            {
                Text = "Save", DialogResult = DialogResult.OK, Size = new Size(150, 30),
                BackColor = Palette.Brand, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };
            _btnSave.FlatAppearance.BorderSize = 0;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_btnSave);
            layout.Controls.Add(buttons);

            // The diff takes the spare height; every other row sizes to its content.
            for (var i = 0; i < layout.Controls.Count; i++)
                layout.RowStyles.Add(layout.Controls[i] == tabs ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            layout.RowCount = layout.Controls.Count;

            Controls.Add(layout);
            AcceptButton = null; // Enter must not save by accident
            CancelButton = cancel;
            UpdateSave();
            Shown += (s, e) => cancel.Focus();
        }

        /// <summary>System charts: whether the user chose to publish the table after saving.</summary>
        public bool Publish => _rdoPublish?.Checked ?? false;

        private void UpdateSave()
        {
            _btnSave.Enabled = _chkWarnings == null || _chkWarnings.Checked;
            _btnSave.BackColor = _btnSave.Enabled ? Palette.Brand : Color.FromArgb(0xB0, 0xBE, 0xBC);
            _btnSave.Text = Publish ? "Save and publish" : "Save";
        }

        private static Label Note(string text, Color back, Color fore) => new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(840, 0), BackColor = back, ForeColor = fore,
            Padding = new Padding(8, 6, 8, 6), Margin = new Padding(0, 0, 0, 6)
        };
    }
}
