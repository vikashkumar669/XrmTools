using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DataQualityStudio.Controls;
using DataQualityStudio.Models;
using DataQualityStudio.Services;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Args;
using XrmToolBox.Extensibility.Interfaces;
using Label = System.Windows.Forms.Label;

namespace DataQualityStudio
{
    /// <summary>
    /// Main UI: open a workbook, pick a Dataverse table, map columns, validate, review the preview and issues, export.
    /// Uses the active XrmToolBox connection.
    /// </summary>
    public class DataQualityStudioControl : PluginControlBase, IGitHubPlugin, IHelpPlugin, IStatusBarMessenger
    {
        // IGitHubPlugin / IHelpPlugin
        public string RepositoryName => "XrmTools";
        public string UserName => "vikashkumar669";
        public string HelpUrl => "https://github.com/vikashkumar669/XrmTools/tree/main/DataQualityStudio#readme";

        public event EventHandler<StatusBarMessageEventArgs> SendMessageToStatusBar;

        private const string NotMapped = "(not mapped)";
        private const string AppName = "Data Quality Studio";

        // State
        private MetadataService _metadata;
        private List<DvEntity> _entities;
        private DvEntity _entity;
        private string _filePath;
        private SourceTable _table;
        private ValidationResult _result;
        private SourceTable _resultTable;
        private List<ColumnMapping> _resultMappings;
        private readonly Dictionary<string, ColumnMapping> _memory = new Dictionary<string, ColumnMapping>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DvField> _fieldByText = new Dictionary<string, DvField>(StringComparer.Ordinal);
        private MappingProfile _pendingProfile;
        private string _pendingEntity;
        private string _preferredSheet;
        private int _preferredHeaderRow = 1;
        private string _profileName;
        private bool _suppress;
        private bool _entitiesRequested;

        // Preview / issues views
        private List<int> _previewRows = new List<int>();
        private List<ValidationIssue> _issueView = new List<ValidationIssue>();
        private Dictionary<long, string> _cellMessages = new Dictionary<long, string>();
        private int _issueSortColumn = -1;
        private bool _issueSortDescending;

        // UI
        private ToolStrip _mainBar, _mapBar, _previewBar, _issueBar;
        private ToolStripComboBox _cboSheet, _cboHeaderRow, _cboEntity, _cboMode, _cboDate, _cboNumber, _cboPreviewFilter, _cboSeverity;
        private ToolStripButton _tsbTrim, _tsbLookups, _tsbValidate;
        private ToolStripDropDownButton _ddExport, _ddProfiles;
        private ToolStripTextBox _txtFind;
        private ToolStripLabel _lblIssueCount, _lblLegend;
        private Label _lblInfo, _lblRequired, _lblSummary;
        private TabControl _tabs;
        private TabPage _tabMapping, _tabPreview, _tabIssues;
        private FastGrid _gridMap, _gridPreview, _gridIssues;
        private DataGridViewComboBoxColumn _colField;
        private Timer _findTimer;

        // Mapping grid columns
        private const int ColSource = 0, ColSample = 1, ColField = 2, ColType = 3, ColRequired = 4, ColStatus = 5,
                          ColTransform = 6, ColValueMap = 7, ColDefault = 8, ColMatch = 9, ColKey = 10;

        public DataQualityStudioControl()
        {
            BuildUi();
            UpdateInfo();
            UpdateButtons();
        }

        // ============================== Connection & metadata ==============================

        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            base.UpdateConnection(newService, detail, actionName, parameter);
            _pendingEntity = _entity?.LogicalName ?? _pendingEntity;
            _metadata = newService == null ? null : new MetadataService(newService);
            _entities = null;
            _entity = null;
            ClearResults();
            RebuildFieldItems();
            PopulateMappingGrid();
            UpdateInfo();
            UpdateButtons();
            if (newService != null && IsHandleCreated) LoadEntities();
        }

        private void LoadEntities()
        {
            if (Service == null) return;
            if (_metadata == null) _metadata = new MetadataService(Service);
            _entitiesRequested = true;
            var metadata = _metadata;
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading tables...",
                Work = (worker, args) => args.Result = metadata.GetEntities(),
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        ShowError("Couldn't load the list of tables", args.Error);
                        return;
                    }
                    _entities = (List<DvEntity>)args.Result;
                    _suppress = true;
                    _cboEntity.ComboBox.BeginUpdate();
                    _cboEntity.Items.Clear();
                    _cboEntity.Items.AddRange(_entities.Cast<object>().ToArray());
                    _cboEntity.ComboBox.EndUpdate();
                    _suppress = false;
                    Status($"{_entities.Count:N0} tables loaded from {ConnectionDetail?.ConnectionName}");

                    var wanted = _pendingProfile?.EntityLogicalName ?? _pendingEntity;
                    _pendingEntity = null;
                    var match = _entities.FirstOrDefault(e => string.Equals(e.LogicalName, wanted, StringComparison.OrdinalIgnoreCase));
                    if (match != null) SelectEntity(match);
                    UpdateButtons();
                }
            });
        }

        private void SelectEntity(DvEntity entity)
        {
            _suppress = true;
            _cboEntity.SelectedItem = entity;
            _suppress = false;
            LoadEntity(entity.LogicalName);
        }

        private void LoadEntity(string logicalName)
        {
            if (_metadata == null) return;
            var metadata = _metadata;
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading columns...",
                Work = (worker, args) => args.Result = metadata.GetEntityWithFields(logicalName),
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        ShowError($"Couldn't load the columns of '{logicalName}'", args.Error);
                        return;
                    }
                    var previous = _entity;
                    _entity = (DvEntity)args.Result;
                    ClearResults();
                    RebuildFieldItems();

                    if (_pendingProfile != null)
                    {
                        // Profile mappings are already in memory
                        _pendingProfile = null;
                        RefreshMapping(false);
                        ReportProfileCoverage();
                    }
                    else if (previous != null && !string.Equals(previous.LogicalName, _entity.LogicalName, StringComparison.OrdinalIgnoreCase))
                    {
                        // A different table: old targets don't apply, map again from scratch (keep per-column settings)
                        foreach (var m in _memory.Values)
                        {
                            m.TargetField = null;
                            m.LookupMatchField = null;
                            m.MatchKind = "Unmapped";
                        }
                        RefreshMapping(true);
                    }
                    else RefreshMapping(false);

                    var required = _entity.Fields.Count(f => f.IsRequired);
                    Status($"{_entity.DisplayName}: {_entity.Fields.Count} importable columns, {required} required");
                    UpdateInfo();
                    UpdateButtons();
                }
            });
        }

        private void RebuildFieldItems()
        {
            _fieldByText.Clear();
            _gridMap.Rows.Clear();
            _colField.Items.Clear();
            _colField.Items.Add(NotMapped);
            if (_entity?.Fields == null) return;
            foreach (var f in _entity.Fields)
            {
                var text = f.ComboText;
                if (_fieldByText.ContainsKey(text)) continue;
                _fieldByText[text] = f;
                _colField.Items.Add(text);
            }
        }

        // ============================== File ==============================

        private void OpenFile()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Open migration workbook",
                Filter = "Excel and CSV files (*.xlsx;*.xlsm;*.csv;*.txt)|*.xlsx;*.xlsm;*.csv;*.txt|Excel workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|CSV files (*.csv;*.txt)|*.csv;*.txt|All files (*.*)|*.*",
                CheckFileExists = true
            })
            {
                if (!string.IsNullOrEmpty(_filePath)) dlg.InitialDirectory = Path.GetDirectoryName(_filePath);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _filePath = dlg.FileName;
            }
            // A new file starts at header row 1 unless the loaded profile says otherwise
            SetHeaderRow(_profileName != null ? _preferredHeaderRow : 1);
            LoadSheets();
        }

        private void LoadSheets()
        {
            var path = _filePath;
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Opening file...",
                Work = (worker, args) => args.Result = SpreadsheetReader.GetSheetNames(path),
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        ShowError("Couldn't open the file", args.Error);
                        return;
                    }
                    var sheets = (List<string>)args.Result;
                    if (sheets.Count == 0)
                    {
                        MessageBox.Show(this, "The workbook doesn't contain any worksheet.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    _suppress = true;
                    _cboSheet.Items.Clear();
                    _cboSheet.Items.AddRange(sheets.Cast<object>().ToArray());
                    var preferred = sheets.FirstOrDefault(s => string.Equals(s, _preferredSheet, StringComparison.OrdinalIgnoreCase)) ?? sheets[0];
                    _cboSheet.SelectedItem = preferred;
                    _cboSheet.Enabled = SpreadsheetReader.IsExcel(path) && sheets.Count > 1;
                    _suppress = false;
                    ReadTable();
                }
            });
        }

        private void ReadTable()
        {
            if (string.IsNullOrEmpty(_filePath)) return;
            var path = _filePath;
            var sheet = _cboSheet.SelectedItem as string;
            var headerRow = HeaderRow;
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Reading rows...",
                Work = (worker, args) => args.Result = SpreadsheetReader.Read(path, sheet, headerRow),
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        ShowError("Couldn't read the sheet", args.Error);
                        return;
                    }
                    _table = (SourceTable)args.Result;
                    ClearResults();
                    RefreshMapping(false);
                    _tabs.SelectedTab = _tabMapping;
                    Status($"{_table.FileName}: {_table.Rows.Count:N0} rows, {_table.Headers.Count} columns");
                    UpdateInfo();
                    UpdateButtons();
                    if (Service == null && !_entitiesRequested)
                        Status("File loaded. Connect to Dataverse to choose a table and validate.");
                }
            });
        }

        private int HeaderRow => int.TryParse(_cboHeaderRow.SelectedItem as string, out var h) ? h : 1;

        private void SetHeaderRow(int row)
        {
            _suppress = true;
            _cboHeaderRow.SelectedItem = Math.Max(1, Math.Min(50, row)).ToString();
            _suppress = false;
        }

        // ============================== Mapping ==============================

        /// <summary>Auto-maps columns that aren't mapped yet (or all with <paramref name="overwrite"/>) and redraws the grid.</summary>
        private void RefreshMapping(bool overwrite)
        {
            // While a profile's table is still loading, its mappings must not be checked against the old table
            if (_table != null && _entity?.Fields != null && _pendingProfile == null)
            {
                var mapped = MappingSuggester.AutoMap(_table.Headers, _entity, _memory, overwrite);
                foreach (var m in mapped)
                {
                    var field = _entity.GetField(m.TargetField);
                    if (field != null && field.IsLookup && string.IsNullOrWhiteSpace(m.LookupMatchField))
                        m.LookupMatchField = DefaultMatchField(field);
                    if (field == null || !field.IsLookup) m.LookupMatchField = null;
                    _memory[m.SourceColumn] = m;
                }
            }
            PopulateMappingGrid();
        }

        private string DefaultMatchField(DvField field)
        {
            if (field?.Targets == null || field.Targets.Length != 1 || _metadata == null) return null;
            try { return _metadata.FindEntity(field.Targets[0])?.PrimaryNameAttribute; }
            catch { return null; }
        }

        private void PopulateMappingGrid()
        {
            _suppress = true;
            _gridMap.SuspendLayout();
            _gridMap.Rows.Clear();
            if (_table != null)
            {
                for (int i = 0; i < _table.Headers.Count; i++)
                {
                    var header = _table.Headers[i];
                    if (!_memory.TryGetValue(header, out var m))
                    {
                        m = new ColumnMapping { SourceColumn = header, MatchKind = "Unmapped" };
                        _memory[header] = m;
                    }
                    var field = _entity?.GetField(m.TargetField);
                    if (_entity?.Fields != null && _pendingProfile == null && m.IsMapped && (field == null || !_fieldByText.ContainsKey(field.ComboText)))
                    {
                        m.MatchKind = $"Unmapped ('{m.TargetField}' not found)";
                        m.TargetField = null;
                        field = null;
                    }

                    var idx = _gridMap.Rows.Add();
                    var row = _gridMap.Rows[idx];
                    row.Tag = m;
                    row.Cells[ColSource].Value = header;
                    row.Cells[ColSample].Value = string.Join("  |  ", _table.SampleValues(i).Select(v => TextUtil.Truncate(v, 40)));
                    row.Cells[ColField].Value = field != null ? field.ComboText : NotMapped;
                    row.Cells[ColTransform].Value = Transforms.All.Contains(m.Transform) ? m.Transform : Transforms.None;
                    row.Cells[ColValueMap].Value = m.ValueMap;
                    row.Cells[ColDefault].Value = m.DefaultValue;
                    row.Cells[ColMatch].Value = m.LookupMatchField;
                    row.Cells[ColKey].Value = m.IsKey;
                    UpdateRowInfo(row);
                }
            }
            _gridMap.ResumeLayout();
            _suppress = false;
            UpdateRequiredLabel();
            _tabMapping.Text = _table == null ? "1  Mapping" : $"1  Mapping ({_memory.Values.Count(m => m.IsMapped && _table.Headers.Contains(m.SourceColumn, StringComparer.OrdinalIgnoreCase))}/{_table.Headers.Count})";
        }

        private void UpdateRowInfo(DataGridViewRow row)
        {
            var m = (ColumnMapping)row.Tag;
            var field = _entity?.GetField(m.TargetField);
            row.Cells[ColType].Value = field?.TypeLabel ?? "";
            row.Cells[ColRequired].Value = field?.RequiredLabel ?? "";
            row.Cells[ColStatus].Value = field == null
                ? (m.MatchKind == ColumnMapping.Ignored ? "Ignored" : m.MatchKind != null && m.MatchKind.StartsWith("Unmapped") ? m.MatchKind : "Unmapped")
                : m.MatchKind ?? "Manual";

            var matchCell = row.Cells[ColMatch];
            matchCell.ReadOnly = field == null || !field.IsLookup;
            matchCell.Style.BackColor = matchCell.ReadOnly ? Color.FromArgb(0xF4, 0xF4, 0xF4) : SystemColors.Window;
            matchCell.ToolTipText = field != null && field.IsLookup
                ? "Column on the target table used to find the record (blank = primary name). GUIDs always work; for several targets prefix values with the table, e.g. contact:Jane Doe."
                : "Only used for lookup columns";

            var statusCell = row.Cells[ColStatus];
            if (field == null)
            {
                row.DefaultCellStyle.ForeColor = Palette.Muted;
                statusCell.Style.ForeColor = Palette.Muted;
            }
            else
            {
                row.DefaultCellStyle.ForeColor = SystemColors.ControlText;
                statusCell.Style.ForeColor = m.MatchKind != null && m.MatchKind.StartsWith("Suggested") ? Palette.WarningFore : Palette.OkFore;
            }
            row.Cells[ColRequired].Style.ForeColor = field != null && field.IsRequired ? Palette.ErrorFore : SystemColors.ControlText;
        }

        private void UpdateRequiredLabel()
        {
            if (_entity?.Fields == null || _table == null)
            {
                _lblRequired.Text = _table == null
                    ? "Open an Excel or CSV file to start (File ▸ Open file…)."
                    : "Choose the Dataverse table to import into (Table box in the toolbar).";
                _lblRequired.ForeColor = Palette.Muted;
                return;
            }
            var mappedFields = new HashSet<string>(_memory.Values
                .Where(m => m.IsMapped && _table.Headers.Contains(m.SourceColumn, StringComparer.OrdinalIgnoreCase))
                .Select(m => m.TargetField), StringComparer.OrdinalIgnoreCase);
            var duplicates = _memory.Values
                .Where(m => m.IsMapped && _table.Headers.Contains(m.SourceColumn, StringComparer.OrdinalIgnoreCase))
                .GroupBy(m => m.TargetField, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            var missing = Mode == ImportMode.Create
                ? _entity.Fields.Where(f => f.IsRequired && !mappedFields.Contains(f.LogicalName)).Select(f => f.DisplayName).ToList()
                : new List<string>();
            var unmapped = _table.Headers.Count(h => !_memory.TryGetValue(h, out var m) || !m.IsMapped);

            var parts = new List<string>();
            if (missing.Count > 0) parts.Add("Required columns not mapped: " + string.Join(", ", missing));
            if (duplicates.Count > 0) parts.Add("Mapped twice: " + string.Join(", ", duplicates));
            if (unmapped > 0) parts.Add($"{unmapped} source column(s) not mapped (they'll be reported and skipped)");
            if (parts.Count == 0) parts.Add("All required columns are mapped. Press Validate (F5).");
            _lblRequired.Text = string.Join("   •   ", parts);
            _lblRequired.ForeColor = missing.Count > 0 || duplicates.Count > 0 ? Palette.ErrorFore : unmapped > 0 ? Palette.WarningFore : Palette.OkFore;

            foreach (DataGridViewRow row in _gridMap.Rows)
            {
                var m = (ColumnMapping)row.Tag;
                var dup = m.IsMapped && duplicates.Contains(m.TargetField, StringComparer.OrdinalIgnoreCase);
                row.Cells[ColField].Style.BackColor = dup ? Palette.ErrorBack : SystemColors.Window;
                row.Cells[ColField].ToolTipText = dup ? "Another source column is mapped to the same Dataverse column." : null;
            }
        }

        private void OnMapCellChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_suppress || e.RowIndex < 0) return;
            var row = _gridMap.Rows[e.RowIndex];
            var m = (ColumnMapping)row.Tag;
            switch (e.ColumnIndex)
            {
                case ColField:
                    {
                        var text = row.Cells[ColField].Value as string;
                        _fieldByText.TryGetValue(text ?? "", out var field);
                        m.TargetField = field?.LogicalName;
                        m.MatchKind = field == null ? ColumnMapping.Ignored : "Manual";
                        m.LookupMatchField = field != null && field.IsLookup ? DefaultMatchField(field) : null;
                        _suppress = true;
                        row.Cells[ColMatch].Value = m.LookupMatchField;
                        _suppress = false;
                        UpdateRowInfo(row);
                        UpdateRequiredLabel();
                        break;
                    }
                case ColTransform:
                    m.Transform = row.Cells[ColTransform].Value as string;
                    break;
                case ColValueMap:
                    m.ValueMap = (row.Cells[ColValueMap].Value as string)?.Trim();
                    break;
                case ColDefault:
                    m.DefaultValue = row.Cells[ColDefault].Value as string;
                    break;
                case ColMatch:
                    m.LookupMatchField = (row.Cells[ColMatch].Value as string)?.Trim();
                    break;
                case ColKey:
                    m.IsKey = row.Cells[ColKey].Value is bool b && b;
                    break;
            }
        }

        private void ClearMappings()
        {
            if (_table == null) return;
            foreach (var h in _table.Headers)
                if (_memory.TryGetValue(h, out var m))
                {
                    m.TargetField = null;
                    m.LookupMatchField = null;
                    m.MatchKind = ColumnMapping.Ignored;
                }
            PopulateMappingGrid();
        }

        private void AutoMapAll(bool overwrite)
        {
            if (_table == null || _entity?.Fields == null)
            {
                MessageBox.Show(this, "Open a file and choose a table first.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (overwrite)
                foreach (var m in _memory.Values.Where(m => m.MatchKind == ColumnMapping.Ignored)) m.MatchKind = "Unmapped";
            RefreshMapping(overwrite);
            var mapped = _table.Headers.Count(h => _memory.TryGetValue(h, out var m) && m.IsMapped);
            Status($"Mapped {mapped} of {_table.Headers.Count} columns");
        }

        private List<ColumnMapping> CurrentMappings() =>
            _table == null
                ? _memory.Values.Select(m => m.Clone()).ToList()
                : _table.Headers.Select(h => _memory.TryGetValue(h, out var m) ? m.Clone() : new ColumnMapping { SourceColumn = h }).ToList();

        // ============================== Validation ==============================

        private ImportMode Mode => _cboMode.SelectedIndex == 1 ? ImportMode.Update : ImportMode.Create;

        private ValidationOptions CurrentOptions() => new ValidationOptions
        {
            Mode = Mode,
            DateOrder = (DateOrder)Math.Max(0, _cboDate.SelectedIndex),
            NumberFormat = (NumberFormatOption)Math.Max(0, _cboNumber.SelectedIndex),
            TrimWhitespace = _tsbTrim.Checked,
            CheckLookups = _tsbLookups.Checked
        };

        private void RunValidation()
        {
            if (_gridMap.IsCurrentCellInEditMode) _gridMap.EndEdit();
            if (_table == null)
            {
                MessageBox.Show(this, "Open an Excel or CSV file first.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_entity?.Fields == null)
            {
                MessageBox.Show(this, "Choose the Dataverse table to validate against.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var table = _table;
            var entity = _entity;
            var mappings = CurrentMappings();
            var options = CurrentOptions();
            var service = Service;
            var metadata = _metadata;
            if (!mappings.Any(m => m.IsMapped))
            {
                MessageBox.Show(this, "No column is mapped yet. Use Auto-map or pick Dataverse columns in the Mapping tab.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Validating...",
                IsCancelable = true,
                Work = (worker, args) =>
                {
                    var engine = new ValidationEngine(service, metadata,
                        msg => worker.ReportProgress(0, msg),
                        () => worker.CancellationPending);
                    args.Result = engine.Run(table, entity, mappings, options);
                },
                ProgressChanged = e =>
                {
                    if (e.UserState is string msg) SetWorkingMessage(msg);
                },
                PostWorkCallBack = args =>
                {
                    if (args.Error is OperationCanceledException || args.Cancelled)
                    {
                        Status("Validation cancelled");
                        return;
                    }
                    if (args.Error != null)
                    {
                        ShowError("Validation failed", args.Error);
                        return;
                    }
                    _result = (ValidationResult)args.Result;
                    _resultTable = table;
                    _resultMappings = mappings;
                    BindResults();
                    _tabs.SelectedTab = _result.ErrorCount + _result.WarningCount > 0 ? _tabIssues : _tabPreview;
                    LogInfo("{0}", $"Validated {table.FileName} against {entity.LogicalName}: {_result.Summary}");
                    Status(_result.Summary);
                }
            });
        }

        private void ClearResults()
        {
            _result = null;
            _resultTable = null;
            _resultMappings = null;
            _previewRows = new List<int>();
            _issueView = new List<ValidationIssue>();
            _cellMessages = new Dictionary<long, string>();
            if (_gridPreview != null)
            {
                _gridPreview.RowCount = 0;
                _gridPreview.Columns.Clear();
                _gridIssues.RowCount = 0;
                _tabPreview.Text = "2  Preview";
                _tabIssues.Text = "3  Issues";
                _lblIssueCount.Text = "";
                _lblSummary.Text = "Not validated yet.";
                _lblSummary.ForeColor = Palette.Muted;
            }
            UpdateButtons();
        }

        private void BindResults()
        {
            var r = _result;
            _cellMessages = new Dictionary<long, string>();
            foreach (var i in r.Issues.Where(i => i.RowIndex >= 0 && i.MappedIndex >= 0))
            {
                var key = CellKey(i.RowIndex, i.MappedIndex);
                var line = $"{i.Severity}: {i.Message}" + (string.IsNullOrEmpty(i.Suggestion) ? "" : Environment.NewLine + "   → " + i.Suggestion);
                _cellMessages[key] = _cellMessages.TryGetValue(key, out var existing) ? existing + Environment.NewLine + line : line;
            }

            // Preview columns
            _gridPreview.RowCount = 0;
            _gridPreview.Columns.Clear();
            _gridPreview.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Row", Width = 60, SortMode = DataGridViewColumnSortMode.NotSortable, Frozen = true });
            _gridPreview.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", Width = 70, SortMode = DataGridViewColumnSortMode.NotSortable, Frozen = true });
            foreach (var c in r.Columns)
            {
                _gridPreview.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = c.Field.DisplayName,
                    ToolTipText = $"{c.Field.ComboText} · {c.Field.TypeLabel}{(c.Field.IsRequired ? " · required" : "")}{Environment.NewLine}Source column: {c.Header}",
                    Width = Math.Max(110, Math.Min(260, c.Field.DisplayName.Length * 8 + 40)),
                    SortMode = DataGridViewColumnSortMode.NotSortable
                });
            }
            ApplyPreviewFilter();

            ApplyIssueFilter();
            _tabPreview.Text = $"2  Preview ({r.TotalRows:N0})";
            _tabIssues.Text = $"3  Issues ({r.ErrorCount:N0} errors, {r.WarningCount:N0} warnings)";
            _lblSummary.Text = r.Summary + $" · {r.Elapsed.TotalSeconds:0.0} s" + (r.IssuesTruncated ? " · issue list truncated" : "");
            _lblSummary.ForeColor = r.ErrorCount > 0 ? Palette.ErrorFore : r.WarningCount > 0 ? Palette.WarningFore : Palette.OkFore;
            UpdateButtons();
        }

        private static long CellKey(int row, int column) => ((long)row << 20) | (uint)column;

        private void ApplyPreviewFilter()
        {
            if (_result == null) return;
            var filter = _cboPreviewFilter.SelectedIndex;
            _previewRows = Enumerable.Range(0, _result.TotalRows)
                .Where(i => filter == 0 || (filter == 1 ? _result.RowSeverity[i] >= (byte)Severity.Error : _result.RowSeverity[i] >= (byte)Severity.Warning))
                .ToList();
            _gridPreview.RowCount = 0;
            _gridPreview.RowCount = _previewRows.Count;
            _gridPreview.Invalidate();
        }

        private void ApplyIssueFilter()
        {
            if (_result == null) return;
            var sev = _cboSeverity.SelectedIndex; // 0 all, 1 errors, 2 warnings, 3 info
            var find = (_txtFind.Text ?? "").Trim();
            IEnumerable<ValidationIssue> q = ResultExporter.OrderedIssues(_result);
            if (sev == 1) q = q.Where(i => i.Severity == Severity.Error);
            else if (sev == 2) q = q.Where(i => i.Severity == Severity.Warning);
            else if (sev == 3) q = q.Where(i => i.Severity == Severity.Info);
            if (find.Length > 0)
                q = q.Where(i => Contains(i.Message, find) || Contains(i.Value, find) || Contains(i.Column, find) || Contains(i.Field, find) || Contains(i.Suggestion, find) || i.RowText == find);
            _issueView = q.ToList();
            if (_issueSortColumn >= 0) SortIssues();
            _gridIssues.RowCount = 0;
            _gridIssues.RowCount = _issueView.Count;
            _gridIssues.Invalidate();
            _lblIssueCount.Text = _issueView.Count == _result.Issues.Count ? $"{_issueView.Count:N0} issue(s)" : $"{_issueView.Count:N0} of {_result.Issues.Count:N0} issue(s)";
        }

        private static bool Contains(string s, string find) => s != null && s.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0;

        private void SortIssues()
        {
            Func<ValidationIssue, IComparable> key;
            switch (_issueSortColumn)
            {
                case 0: key = i => i.RowNumber ?? -1; break;
                case 1: key = i => (int)i.Severity; break;
                case 2: key = i => i.Column ?? ""; break;
                case 3: key = i => i.Field ?? ""; break;
                case 4: key = i => i.Value ?? ""; break;
                case 5: key = i => i.Message ?? ""; break;
                default: key = i => i.Suggestion ?? ""; break;
            }
            _issueView = _issueSortDescending ? _issueView.OrderByDescending(key).ToList() : _issueView.OrderBy(key).ToList();
        }

        private void GoToIssue(ValidationIssue issue)
        {
            if (issue == null) return;
            if (issue.RowIndex < 0)
            {
                // Mapping-level issue: show the mapping row
                _tabs.SelectedTab = _tabMapping;
                foreach (DataGridViewRow row in _gridMap.Rows)
                {
                    if (string.Equals(row.Cells[ColSource].Value as string, issue.Column, StringComparison.OrdinalIgnoreCase))
                    {
                        _gridMap.CurrentCell = row.Cells[ColField];
                        break;
                    }
                }
                return;
            }
            if (_cboPreviewFilter.SelectedIndex != 0) _cboPreviewFilter.SelectedIndex = 0; // triggers ApplyPreviewFilter
            var displayIndex = _previewRows.BinarySearch(issue.RowIndex);
            if (displayIndex < 0) return;
            _tabs.SelectedTab = _tabPreview;
            var col = issue.MappedIndex >= 0 ? issue.MappedIndex + 2 : 1;
            if (col < _gridPreview.ColumnCount)
            {
                _gridPreview.CurrentCell = _gridPreview.Rows[displayIndex].Cells[col];
                _gridPreview.FirstDisplayedScrollingRowIndex = Math.Max(0, displayIndex - 3);
            }
        }

        // ============================== Export ==============================

        private void Export(int kind)
        {
            if (_result == null)
            {
                MessageBox.Show(this, "Validate first; the export contains the validation results.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var baseName = Path.GetFileNameWithoutExtension(_resultTable.FilePath) + "_validation_" + DateTime.Now.ToString("yyyyMMdd_HHmm");
            using (var dlg = new SaveFileDialog
            {
                Title = kind == 0 ? "Export validation results" : kind == 1 ? "Export issues as CSV" : "Export corrected data as CSV",
                Filter = kind == 0 ? "Excel workbook (*.xlsx)|*.xlsx" : "CSV file (*.csv)|*.csv",
                FileName = baseName + (kind == 2 ? "_corrected" : kind == 1 ? "_issues" : ""),
                InitialDirectory = Path.GetDirectoryName(_resultTable.FilePath),
                OverwritePrompt = true
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var path = dlg.FileName;
                var table = _resultTable;
                var result = _result;
                var mappings = _resultMappings;
                var connection = ConnectionDetail?.ConnectionName;
                WorkAsync(new WorkAsyncInfo
                {
                    Message = "Exporting...",
                    Work = (worker, args) =>
                    {
                        if (kind == 0) ResultExporter.ExportExcel(path, table, result, mappings, connection);
                        else if (kind == 1) ResultExporter.ExportCsv(path, result);
                        else ResultExporter.ExportCorrectedCsv(path, table, result);
                    },
                    PostWorkCallBack = args =>
                    {
                        if (args.Error != null)
                        {
                            ShowError("Export failed" + (args.Error is IOException ? " (is the file open in Excel?)" : ""), args.Error);
                            return;
                        }
                        Status("Exported " + Path.GetFileName(path));
                        OfferToOpen(path);
                    }
                });
            }
        }

        private void ExportTemplate()
        {
            if (_entity?.Fields == null)
            {
                MessageBox.Show(this, "Choose a table first.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var mapped = _table == null ? new List<DvField>() :
                _table.Headers.Select(h => _memory.TryGetValue(h, out var m) ? _entity.GetField(m.TargetField) : null).Where(f => f != null).Distinct().ToList();
            List<DvField> fields;
            if (mapped.Count > 0)
            {
                var answer = MessageBox.Show(this,
                    $"Create the template with the {mapped.Count} currently mapped column(s)?\n\nYes = mapped columns\nNo = required, recommended and primary name columns of {_entity.DisplayName}",
                    "Import template", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                fields = answer == DialogResult.Yes ? mapped : null;
            }
            else fields = null;
            if (fields == null)
                fields = _entity.Fields
                    .Where(f => f.IsValidForCreate && (f.IsRequired || f.IsPrimaryName || f.RequiredLevel == "Recommended"))
                    .OrderByDescending(f => f.IsPrimaryName).ThenByDescending(f => f.IsRequired).ThenBy(f => f.DisplayName)
                    .ToList();

            using (var dlg = new SaveFileDialog
            {
                Title = "Save import template",
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                FileName = $"{_entity.DisplayName} import template",
                OverwritePrompt = true
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    ResultExporter.ExportTemplate(dlg.FileName, _entity, fields);
                    Status("Template saved: " + Path.GetFileName(dlg.FileName));
                    OfferToOpen(dlg.FileName);
                }
                catch (Exception ex) { ShowError("Couldn't save the template", ex); }
            }
        }

        private void OfferToOpen(string path)
        {
            if (MessageBox.Show(this, $"Saved {Path.GetFileName(path)}.\n\nOpen it now?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { ShowError("Couldn't open the file", ex); }
        }

        // ============================== Profiles ==============================

        private MappingProfile BuildProfile(string name) => new MappingProfile
        {
            Name = name,
            Description = _table != null ? $"Created from {_table.FileName}" : null,
            EntityLogicalName = _entity?.LogicalName,
            Mode = Mode.ToString(),
            DateOrder = ((DateOrder)Math.Max(0, _cboDate.SelectedIndex)).ToString(),
            NumberFormat = ((NumberFormatOption)Math.Max(0, _cboNumber.SelectedIndex)).ToString(),
            TrimWhitespace = _tsbTrim.Checked,
            CheckLookups = _tsbLookups.Checked,
            SheetName = _table?.SheetName,
            HeaderRow = HeaderRow,
            Mappings = CurrentMappings(),
            SavedOn = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            SavedBy = Environment.UserName,
            ToolVersion = GetType().Assembly.GetName().Version.ToString()
        };

        private void SaveProfile(bool askName, bool toFile)
        {
            if (_table == null && _memory.Count == 0)
            {
                MessageBox.Show(this, "There's no mapping to save yet. Open a file and map its columns first.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_gridMap.IsCurrentCellInEditMode) _gridMap.EndEdit();
            var name = _profileName;
            if (askName || string.IsNullOrEmpty(name))
            {
                var suggested = name ?? (_entity != null ? $"{_entity.DisplayName} import" : Path.GetFileNameWithoutExtension(_table?.FilePath ?? "Profile"));
                name = InputDialog.Ask(this, "Save mapping profile", "Profile name:", suggested);
                if (string.IsNullOrWhiteSpace(name)) return;
            }

            string path;
            if (toFile)
            {
                using (var dlg = new SaveFileDialog
                {
                    Title = "Export mapping profile",
                    Filter = "Mapping profile (*" + ProfileStore.Extension + ")|*" + ProfileStore.Extension + "|JSON (*.json)|*.json",
                    FileName = name + ProfileStore.Extension,
                    OverwritePrompt = true
                })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    path = dlg.FileName;
                }
            }
            else
            {
                path = ProfileStore.PathForName(name);
                if (askName && File.Exists(path) &&
                    MessageBox.Show(this, $"A profile named '{name}' already exists. Replace it?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
            }

            try
            {
                ProfileStore.Save(BuildProfile(name), path);
                _profileName = name;
                UpdateInfo();
                Status($"Profile '{name}' saved");
            }
            catch (Exception ex) { ShowError("Couldn't save the profile", ex); }
        }

        private void LoadProfile(string path)
        {
            MappingProfile profile;
            try { profile = ProfileStore.Load(path); }
            catch (Exception ex)
            {
                ShowError("Couldn't read the profile", ex);
                return;
            }
            ApplyProfile(profile);
        }

        private void ApplyProfile(MappingProfile p)
        {
            _profileName = p.Name;
            _suppress = true;
            _cboMode.SelectedIndex = string.Equals(p.Mode, ImportMode.Update.ToString(), StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _cboDate.SelectedIndex = Enum.TryParse(p.DateOrder, out DateOrder d) ? (int)d : 0;
            _cboNumber.SelectedIndex = Enum.TryParse(p.NumberFormat, out NumberFormatOption n) ? (int)n : 0;
            _tsbTrim.Checked = p.TrimWhitespace;
            _tsbLookups.Checked = p.CheckLookups;
            var headerText = Math.Max(1, Math.Min(50, p.HeaderRow)).ToString();
            var headerChanged = !string.Equals(_cboHeaderRow.SelectedItem as string, headerText);
            _cboHeaderRow.SelectedItem = headerText;
            _suppress = false;

            _memory.Clear();
            foreach (var m in p.Mappings.Where(m => !string.IsNullOrEmpty(m.SourceColumn)))
            {
                var copy = m.Clone();
                copy.MatchKind = copy.IsMapped ? "Profile" : ColumnMapping.Ignored;
                if (string.IsNullOrEmpty(copy.Transform)) copy.Transform = Transforms.None;
                _memory[copy.SourceColumn] = copy;
            }
            _preferredSheet = p.SheetName;
            _preferredHeaderRow = Math.Max(1, p.HeaderRow);
            ClearResults();

            // Re-read the file if the profile points to another sheet or header row
            var sheetChanged = _table != null && !string.IsNullOrEmpty(p.SheetName) &&
                               !string.Equals(_table.SheetName, p.SheetName, StringComparison.OrdinalIgnoreCase) &&
                               _cboSheet.Items.Cast<string>().Any(s => string.Equals(s, p.SheetName, StringComparison.OrdinalIgnoreCase));
            if (sheetChanged)
            {
                _suppress = true;
                _cboSheet.SelectedItem = _cboSheet.Items.Cast<string>().First(s => string.Equals(s, p.SheetName, StringComparison.OrdinalIgnoreCase));
                _suppress = false;
            }

            var entityChanged = !string.IsNullOrEmpty(p.EntityLogicalName) &&
                                !string.Equals(_entity?.LogicalName, p.EntityLogicalName, StringComparison.OrdinalIgnoreCase);
            if (entityChanged)
            {
                var target = _entities?.FirstOrDefault(e => string.Equals(e.LogicalName, p.EntityLogicalName, StringComparison.OrdinalIgnoreCase));
                if (target != null)
                {
                    _pendingProfile = p;
                    SelectEntity(target);
                }
                else if (_entities == null)
                {
                    _pendingProfile = p;
                    _pendingEntity = p.EntityLogicalName;
                    if (Service != null) LoadEntities();
                    else Status($"Profile '{p.Name}' loaded. Connect to an environment to load table '{p.EntityLogicalName}'.");
                }
                else
                {
                    MessageBox.Show(this, $"Table '{p.EntityLogicalName}' from the profile doesn't exist in this environment. The mappings were loaded but can't be applied.",
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            if (_table != null && (sheetChanged || headerChanged)) ReadTable();
            else if (!entityChanged)
            {
                RefreshMapping(false);
                ReportProfileCoverage();
            }
            UpdateInfo();
            Status($"Profile '{p.Name}' loaded");
        }

        private void ReportProfileCoverage()
        {
            if (_table == null) return;
            var missing = _memory.Values
                .Where(m => m.IsMapped && m.MatchKind == "Profile" && !_table.Headers.Contains(m.SourceColumn, StringComparer.OrdinalIgnoreCase))
                .Select(m => m.SourceColumn).ToList();
            if (missing.Count > 0)
                MessageBox.Show(this,
                    $"{missing.Count} column(s) mapped in the profile aren't in this file:\n\n  • " + string.Join("\n  • ", missing.Take(15)) +
                    (missing.Count > 15 ? $"\n  • …and {missing.Count - 15} more" : "") +
                    "\n\nThe other mappings were applied. Columns new to this file were auto-mapped where possible.",
                    "Profile applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void BuildProfilesMenu()
        {
            var items = _ddProfiles.DropDownItems;
            items.Clear();
            items.Add(new ToolStripMenuItem("Save profile", null, (s, e) => SaveProfile(false, false)) { ToolTipText = "Save the mapping and options under the current profile name", ShortcutKeyDisplayString = "Ctrl+S" });
            items.Add(new ToolStripMenuItem("Save profile as…", null, (s, e) => SaveProfile(true, false)));
            items.Add(new ToolStripSeparator());

            var saved = ProfileStore.ListProfiles();
            if (saved.Count == 0) items.Add(new ToolStripMenuItem("(no saved profiles yet)") { Enabled = false });
            foreach (var path in saved)
            {
                var p = path;
                var name = ProfileStore.NameFromPath(p);
                items.Add(new ToolStripMenuItem(name, null, (s, e) => LoadProfile(p)) { Checked = string.Equals(name, _profileName, StringComparison.OrdinalIgnoreCase) });
            }

            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem("Import profile from file…", null, (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Title = "Open mapping profile", Filter = "Mapping profile (*.json)|*.json|All files (*.*)|*.*" })
                    if (dlg.ShowDialog(this) == DialogResult.OK) LoadProfile(dlg.FileName);
            }));
            items.Add(new ToolStripMenuItem("Export profile to file…", null, (s, e) => SaveProfile(true, true)));

            var delete = new ToolStripMenuItem("Delete profile") { Enabled = saved.Count > 0 };
            foreach (var path in saved)
            {
                var p = path;
                var name = ProfileStore.NameFromPath(p);
                delete.DropDownItems.Add(name, null, (s, e) =>
                {
                    if (MessageBox.Show(this, $"Delete profile '{name}'?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    try
                    {
                        File.Delete(p);
                        if (string.Equals(_profileName, name, StringComparison.OrdinalIgnoreCase)) _profileName = null;
                        UpdateInfo();
                    }
                    catch (Exception ex) { ShowError("Couldn't delete the profile", ex); }
                });
            }
            items.Add(delete);
            items.Add(new ToolStripMenuItem("Open profiles folder", null, (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(ProfileStore.Folder) { UseShellExecute = true }); }
                catch (Exception ex) { ShowError("Couldn't open the folder", ex); }
            }));
        }

        // ============================== Helpers ==============================

        private void UpdateInfo()
        {
            var parts = new List<string>();
            parts.Add(_table == null ? "No file" : $"{_table.FileName} · {_table.SheetName} · {_table.Rows.Count:N0} rows · {_table.Headers.Count} columns");
            parts.Add(_entity == null ? "No table" : $"Table: {_entity.ComboText}");
            parts.Add(ConnectionDetail == null ? "Not connected" : $"Environment: {ConnectionDetail.ConnectionName}");
            if (!string.IsNullOrEmpty(_profileName)) parts.Add($"Profile: {_profileName}");
            _lblInfo.Text = string.Join("     |     ", parts);
            UpdateRequiredLabel();
        }

        private void UpdateButtons()
        {
            if (_tsbValidate == null) return;
            _tsbValidate.Enabled = _table != null && _entity?.Fields != null;
            _ddExport.Enabled = _result != null;
            _cboSheet.Enabled = _table != null && SpreadsheetReader.IsExcel(_table.FilePath) && _cboSheet.Items.Count > 1;
        }

        private void Status(string message)
        {
            SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs(message));
        }

        private void ShowError(string title, Exception ex)
        {
            var inner = ex;
            while (inner.InnerException != null && inner is System.Reflection.TargetInvocationException) inner = inner.InnerException;
            LogError("{0}: {1}", title, inner.ToString());
            MessageBox.Show(this, $"{title}.\n\n{inner.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5) { ExecuteMethod(RunValidation); return true; }
            if (keyData == (Keys.Control | Keys.O)) { OpenFile(); return true; }
            if (keyData == (Keys.Control | Keys.S)) { SaveProfile(false, false); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ============================== UI construction ==============================

        private void BuildUi()
        {
            SuspendLayout();
            Font = new Font("Segoe UI", 9F);
            Size = new Size(1200, 760);

            // ---- Main toolbar ----
            _mainBar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2), RenderMode = ToolStripRenderMode.System };
            var tsbClose = ToolStripFactory.Button("Close", "Close this tool", (s, e) => CloseTool());
            var tsbOpen = ToolStripFactory.Button("Open file…", "Open an Excel (.xlsx) or CSV file (Ctrl+O)", (s, e) => OpenFile(), true);
            _cboSheet = ToolStripFactory.Combo(150);
            _cboSheet.ToolTipText = "Worksheet to validate";
            _cboSheet.SelectedIndexChanged += (s, e) => { if (!_suppress) ReadTable(); };
            _cboHeaderRow = ToolStripFactory.Combo(48, Enumerable.Range(1, 50).Select(i => (object)i.ToString()).ToArray());
            _cboHeaderRow.ToolTipText = "Row that contains the column headers";
            _cboHeaderRow.SelectedIndexChanged += (s, e) => { if (!_suppress && _table != null) ReadTable(); };

            _cboEntity = new ToolStripComboBox { AutoSize = false, Width = 300, DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.System, MaxDropDownItems = 25, ToolTipText = "Dataverse table to import into (type to search)" };
            _cboEntity.ComboBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _cboEntity.ComboBox.AutoCompleteSource = AutoCompleteSource.ListItems;
            _cboEntity.SelectedIndexChanged += (s, e) =>
            {
                if (_suppress || !(_cboEntity.SelectedItem is DvEntity entity)) return;
                if (_entity != null && string.Equals(_entity.LogicalName, entity.LogicalName, StringComparison.OrdinalIgnoreCase)) return;
                LoadEntity(entity.LogicalName);
            };
            _cboEntity.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter || _entities == null) return;
                var text = _cboEntity.Text.Trim();
                var match = _entities.FirstOrDefault(x => string.Equals(x.LogicalName, text, StringComparison.OrdinalIgnoreCase) ||
                                                          string.Equals(x.DisplayName, text, StringComparison.OrdinalIgnoreCase) ||
                                                          string.Equals(x.ComboText, text, StringComparison.OrdinalIgnoreCase));
                if (match != null) _cboEntity.SelectedItem = match;
                e.Handled = e.SuppressKeyPress = true;
            };
            _cboEntity.DropDown += (s, e) => { if (_entities == null && !_entitiesRequested) ExecuteMethod(LoadEntities); };
            var tsbRefresh = ToolStripFactory.Button("⟳", "Reload tables and columns from Dataverse (after customizations)", (s, e) =>
            {
                if (Service == null) { ExecuteMethod(LoadEntities); return; }
                _pendingEntity = _entity?.LogicalName;
                _metadata = new MetadataService(Service);
                _entity = null;
                _entities = null;
                ExecuteMethod(LoadEntities);
            });

            _tsbValidate = ToolStripFactory.Button("▶ Validate", "Validate every mapped cell and build the preview (F5)", (s, e) => ExecuteMethod(RunValidation), true);
            _tsbValidate.ForeColor = Palette.Brand;
            _ddExport = new ToolStripDropDownButton("Export results") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Export row-level errors and suggested corrections" };
            _ddExport.DropDownItems.Add("Excel workbook (summary, issues, data with fixes, preview)…", null, (s, e) => Export(0));
            _ddExport.DropDownItems.Add("Issues as CSV…", null, (s, e) => Export(1));
            _ddExport.DropDownItems.Add("Data with suggested fixes as CSV…", null, (s, e) => Export(2));
            var tsbTemplate = ToolStripFactory.Button("Template", "Create an Excel import template with the table's display names and a column reference sheet", (s, e) => ExportTemplate());
            _ddProfiles = new ToolStripDropDownButton("Profiles") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Save and reuse mapping profiles" };
            _ddProfiles.DropDownOpening += (s, e) => BuildProfilesMenu();
            BuildProfilesMenu();

            _mainBar.Items.AddRange(new ToolStripItem[]
            {
                tsbClose, new ToolStripSeparator(),
                tsbOpen, new ToolStripLabel("Sheet:"), _cboSheet, new ToolStripLabel("Header row:"), _cboHeaderRow, new ToolStripSeparator(),
                new ToolStripLabel("Table:"), _cboEntity, tsbRefresh, new ToolStripSeparator(),
                _tsbValidate, _ddExport, new ToolStripSeparator(),
                tsbTemplate, _ddProfiles
            });

            _lblInfo = new Label { Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 5, 8, 0), ForeColor = Palette.Brand, BackColor = Palette.BrandLight, Font = new Font("Segoe UI", 9F, FontStyle.Bold), AutoEllipsis = true };

            // ---- Mapping tab ----
            _mapBar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2), RenderMode = ToolStripRenderMode.System };
            var tsbAuto = ToolStripFactory.Button("Auto-map", "Map unmapped columns by display name, logical name or similarity", (s, e) => AutoMapAll(false), true);
            var tsbRemap = ToolStripFactory.Button("Re-map all", "Discard the current mapping and auto-map every column again", (s, e) =>
            {
                if (MessageBox.Show(this, "Replace the current mapping with a fresh auto-mapping?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    AutoMapAll(true);
            });
            var tsbClear = ToolStripFactory.Button("Clear", "Set every column to (not mapped)", (s, e) => ClearMappings());
            _cboMode = ToolStripFactory.Combo(150, "Create new records", "Update existing records");
            _cboMode.ToolTipText = "Create: required columns must have values. Update: required columns may be omitted.";
            _cboMode.SelectedIndexChanged += (s, e) => { if (!_suppress) UpdateRequiredLabel(); };
            _cboDate = ToolStripFactory.Combo(165, "Dates: auto-detect", "Dates: day/month/year", "Dates: month/day/year", "Dates: year-month-day");
            _cboDate.ToolTipText = "How to read text dates such as 03/04/2026. Real Excel dates are always read correctly.";
            _cboNumber = ToolStripFactory.Combo(165, "Numbers: auto-detect", "Numbers: 1,234.56", "Numbers: 1.234,56");
            _cboNumber.ToolTipText = "Decimal and thousands separators used in text numbers";
            _tsbTrim = ToolStripFactory.Toggle("Trim spaces", "Remove leading and trailing spaces before validating", true);
            _tsbLookups = ToolStripFactory.Toggle("Check lookups", "Query Dataverse to confirm that lookup values exist (batched)", true);
            _mapBar.Items.AddRange(new ToolStripItem[]
            {
                tsbAuto, tsbRemap, tsbClear, new ToolStripSeparator(),
                new ToolStripLabel("Mode:"), _cboMode, _cboDate, _cboNumber, new ToolStripSeparator(), _tsbTrim, _tsbLookups
            });

            _lblRequired = new Label { Dock = DockStyle.Top, Height = 24, Padding = new Padding(6, 4, 6, 0), AutoEllipsis = true };
            var mapHint = new Label
            {
                Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(6, 3, 6, 0), ForeColor = Palette.Muted, AutoEllipsis = true,
                Text = "Value map: Old=New; Old2=New2 (applied before validation)   •   Default: used when the cell is empty   •   Key: flags duplicate rows   •   Lookup match column: blank = primary name"
            };

            _gridMap = new FastGrid { Dock = DockStyle.Fill, EditMode = DataGridViewEditMode.EditOnEnter, SelectionMode = DataGridViewSelectionMode.CellSelect };
            _gridMap.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Source column", Width = 170, ReadOnly = true, Frozen = true, DefaultCellStyle = { Font = new Font("Segoe UI", 9F, FontStyle.Bold) } });
            _gridMap.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sample values", Width = 200, ReadOnly = true, DefaultCellStyle = { ForeColor = Palette.Muted } });
            _colField = new DataGridViewComboBoxColumn
            {
                HeaderText = "Dataverse column", Width = 260, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
                MaxDropDownItems = 25, SortMode = DataGridViewColumnSortMode.NotSortable
            };
            _gridMap.Columns.Add(_colField);
            _gridMap.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type", Width = 130, ReadOnly = true });
            _gridMap.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Required", Width = 85, ReadOnly = true });
            _gridMap.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Match", Width = 150, ReadOnly = true });
            var colTransform = new DataGridViewComboBoxColumn { HeaderText = "Transform", Width = 110, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton };
            colTransform.Items.AddRange(Transforms.All.Cast<object>().ToArray());
            _gridMap.Columns.Add(colTransform);
            _gridMap.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Value map", Width = 170, ToolTipText = "Replace values before validation, e.g.  Y=Yes; N=No; Hot=1" });
            _gridMap.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Default", Width = 100, ToolTipText = "Value used when the cell is empty" });
            _gridMap.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Lookup match column", Width = 150, ToolTipText = "Lookups: logical name of the column used to find the target record (blank = primary name)" });
            _gridMap.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Key", Width = 45, ToolTipText = "Columns that together identify a row; duplicates are flagged" });
            foreach (DataGridViewColumn c in _gridMap.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
            _gridMap.CellValueChanged += OnMapCellChanged;
            _gridMap.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_gridMap.IsCurrentCellDirty && (_gridMap.CurrentCell is DataGridViewComboBoxCell || _gridMap.CurrentCell is DataGridViewCheckBoxCell))
                    _gridMap.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _gridMap.DataError += (s, e) => { e.ThrowException = false; };
            _gridMap.EditingControlShowing += (s, e) =>
            {
                // Typing in the column drop-down jumps to matching entries
                if (e.Control is ComboBox combo) combo.DropDownWidth = Math.Max(combo.DropDownWidth, 360);
            };

            _tabMapping = new TabPage("1  Mapping") { Padding = new Padding(2) };
            _tabMapping.Controls.Add(_gridMap);
            _tabMapping.Controls.Add(mapHint);
            _tabMapping.Controls.Add(_lblRequired);
            _tabMapping.Controls.Add(_mapBar);

            // ---- Preview tab ----
            _previewBar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2), RenderMode = ToolStripRenderMode.System };
            _cboPreviewFilter = ToolStripFactory.Combo(200, "All rows", "Rows with errors", "Rows with errors or warnings");
            _cboPreviewFilter.SelectedIndexChanged += (s, e) => ApplyPreviewFilter();
            _lblLegend = new ToolStripLabel("Values are shown as they would be imported (choices with their value, lookups with the matched record). Hover a colored cell for details.") { ForeColor = Palette.Muted };
            _previewBar.Items.AddRange(new ToolStripItem[] { new ToolStripLabel("Show:"), _cboPreviewFilter, new ToolStripSeparator(), _lblLegend });

            _gridPreview = new FastGrid { Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true };
            _gridPreview.CellValueNeeded += (s, e) =>
            {
                if (_result == null || e.RowIndex < 0 || e.RowIndex >= _previewRows.Count) return;
                var r = _previewRows[e.RowIndex];
                if (e.ColumnIndex == 0) e.Value = _resultTable.Rows[r].RowNumber;
                else if (e.ColumnIndex == 1) e.Value = SeverityText(_result.RowSeverity[r]);
                else if (e.ColumnIndex - 2 < _result.Columns.Count) e.Value = _result.Preview[r][e.ColumnIndex - 2];
            };
            _gridPreview.CellFormatting += (s, e) =>
            {
                if (_result == null || e.RowIndex < 0 || e.RowIndex >= _previewRows.Count) return;
                var r = _previewRows[e.RowIndex];
                byte sev;
                if (e.ColumnIndex == 0) return;
                if (e.ColumnIndex == 1) sev = _result.RowSeverity[r];
                else if (e.ColumnIndex - 2 < _result.Columns.Count) sev = _result.CellSeverity[r][e.ColumnIndex - 2];
                else return;
                ApplySeverityStyle(e.CellStyle, sev, e.ColumnIndex == 1);
            };
            _gridPreview.CellToolTipTextNeeded += (s, e) =>
            {
                if (_result == null || e.RowIndex < 0 || e.RowIndex >= _previewRows.Count || e.ColumnIndex < 2) return;
                if (_cellMessages.TryGetValue(CellKey(_previewRows[e.RowIndex], e.ColumnIndex - 2), out var text)) e.ToolTipText = text;
            };

            _tabPreview = new TabPage("2  Preview") { Padding = new Padding(2) };
            _tabPreview.Controls.Add(_gridPreview);
            _tabPreview.Controls.Add(_previewBar);

            // ---- Issues tab ----
            _issueBar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2), RenderMode = ToolStripRenderMode.System };
            _cboSeverity = ToolStripFactory.Combo(120, "All severities", "Errors", "Warnings", "Info");
            _cboSeverity.SelectedIndexChanged += (s, e) => ApplyIssueFilter();
            _txtFind = new ToolStripTextBox { AutoSize = false, Width = 220, ToolTipText = "Filter by message, value, column or row number" };
            _findTimer = new Timer { Interval = 300 };
            _findTimer.Tick += (s, e) => { _findTimer.Stop(); ApplyIssueFilter(); };
            _txtFind.TextChanged += (s, e) => { _findTimer.Stop(); _findTimer.Start(); };
            _lblIssueCount = new ToolStripLabel("") { ForeColor = Palette.Muted };
            _issueBar.Items.AddRange(new ToolStripItem[]
            {
                new ToolStripLabel("Show:"), _cboSeverity, new ToolStripLabel("Find:"), _txtFind, new ToolStripSeparator(), _lblIssueCount,
                new ToolStripSeparator(), new ToolStripLabel("Double-click an issue to see it in the preview or mapping.") { ForeColor = Palette.Muted }
            });

            _gridIssues = new FastGrid { Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            foreach (var (text, width) in new[] { ("Row", 60), ("Severity", 75), ("Source column", 150), ("Dataverse column", 150), ("Value", 160), ("Message", 430), ("Suggested fix", 330) })
                _gridIssues.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = text, Width = width, SortMode = DataGridViewColumnSortMode.Programmatic });
            _gridIssues.CellValueNeeded += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= _issueView.Count) return;
                var i = _issueView[e.RowIndex];
                switch (e.ColumnIndex)
                {
                    case 0: e.Value = i.RowText; break;
                    case 1: e.Value = i.Severity.ToString(); break;
                    case 2: e.Value = i.Column; break;
                    case 3: e.Value = i.Field; break;
                    case 4: e.Value = i.Value; break;
                    case 5: e.Value = i.Message; break;
                    case 6: e.Value = i.Suggestion; break;
                }
            };
            _gridIssues.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= _issueView.Count || e.ColumnIndex != 1) return;
                ApplySeverityStyle(e.CellStyle, (byte)_issueView[e.RowIndex].Severity, true);
            };
            _gridIssues.CellToolTipTextNeeded += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= _issueView.Count) return;
                var i = _issueView[e.RowIndex];
                e.ToolTipText = i.Message + (string.IsNullOrEmpty(i.Suggestion) ? "" : Environment.NewLine + "→ " + i.Suggestion) +
                                (i.CorrectedValue == null ? "" : Environment.NewLine + "Corrected value: " + i.CorrectedValue);
            };
            _gridIssues.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < _issueView.Count) GoToIssue(_issueView[e.RowIndex]);
            };
            _gridIssues.ColumnHeaderMouseClick += (s, e) =>
            {
                if (_result == null) return;
                _issueSortDescending = _issueSortColumn == e.ColumnIndex && !_issueSortDescending;
                _issueSortColumn = e.ColumnIndex;
                foreach (DataGridViewColumn c in _gridIssues.Columns) c.HeaderCell.SortGlyphDirection = SortOrder.None;
                _gridIssues.Columns[e.ColumnIndex].HeaderCell.SortGlyphDirection = _issueSortDescending ? SortOrder.Descending : SortOrder.Ascending;
                ApplyIssueFilter();
            };

            _tabIssues = new TabPage("3  Issues") { Padding = new Padding(2) };
            _tabIssues.Controls.Add(_gridIssues);
            _tabIssues.Controls.Add(_issueBar);

            _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            _tabs.TabPages.AddRange(new[] { _tabMapping, _tabPreview, _tabIssues });

            _lblSummary = new Label
            {
                Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(8, 5, 8, 0), Text = "Not validated yet.", ForeColor = Palette.Muted,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold), BackColor = Color.FromArgb(0xF6, 0xF8, 0xF7), AutoEllipsis = true
            };

            Controls.Add(_tabs);
            Controls.Add(_lblSummary);
            Controls.Add(_lblInfo);
            Controls.Add(_mainBar);

            RebuildFieldItems();
            Load += (s, e) =>
            {
                if (Service != null && _entities == null && !_entitiesRequested) LoadEntities();
            };
            ResumeLayout();
        }

        private static string SeverityText(byte s) => s >= (byte)Severity.Error ? "Error" : s == (byte)Severity.Warning ? "Warning" : "OK";

        private static void ApplySeverityStyle(DataGridViewCellStyle style, byte sev, bool statusCell)
        {
            if (sev >= (byte)Severity.Error)
            {
                style.BackColor = Palette.ErrorBack;
                style.ForeColor = Palette.ErrorFore;
            }
            else if (sev == (byte)Severity.Warning)
            {
                style.BackColor = Palette.WarningBack;
                style.ForeColor = Palette.WarningFore;
            }
            else if (sev == (byte)Severity.Info)
            {
                style.BackColor = Palette.InfoBack;
            }
            else if (statusCell)
            {
                style.ForeColor = Palette.OkFore;
            }
        }
    }
}
