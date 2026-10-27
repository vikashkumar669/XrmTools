using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PluginFailureCorrelationExplorer.Controls
{
    /// <summary>A column of a <see cref="VirtualGrid{T}"/>.</summary>
    internal sealed class GridColumn<T>
    {
        public GridColumn(string header, Func<T, object> value, int width = 100, bool fill = false, bool rightAlign = false)
        {
            Header = header;
            Value = value;
            Width = width;
            Fill = fill;
            RightAlign = rightAlign;
        }

        public string Header { get; }
        public Func<T, object> Value { get; }
        public int Width { get; }
        public bool Fill { get; }
        public bool RightAlign { get; }

        /// <summary>Optional display text (when the sort value differs from what's shown).</summary>
        public Func<T, string> Display { get; set; }

        /// <summary>Optional cell colour.</summary>
        public Func<T, Color?> ForeColor { get; set; }
    }

    /// <summary>
    /// Read-only, sortable DataGridView in virtual mode: rows are produced on demand, so tens of thousands of items stay fast.
    /// Click a header to sort; click again to reverse.
    /// </summary>
    internal sealed class VirtualGrid<T> : DataGridView where T : class
    {
        private readonly List<GridColumn<T>> _columns;
        private List<T> _items = new List<T>();
        private int _sortColumn = -1;
        private bool _sortDescending;
        private string _emptyText = "Nothing to show.";

        public VirtualGrid(IEnumerable<GridColumn<T>> columns)
        {
            _columns = columns.ToList();
            DoubleBuffered = true;
            VirtualMode = true;
            ReadOnly = true;
            AllowUserToAddRows = false;
            AllowUserToDeleteRows = false;
            AllowUserToResizeRows = false;
            AllowUserToOrderColumns = true;
            RowHeadersVisible = false;
            SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            MultiSelect = false;
            BorderStyle = BorderStyle.None;
            BackgroundColor = Theme.Card;
            GridColor = Theme.Line;
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            EnableHeadersVisualStyles = false;
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            ColumnHeadersDefaultCellStyle.BackColor = Theme.BrandTint;
            ColumnHeadersDefaultCellStyle.ForeColor = Theme.BrandDark;
            ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.BrandTint;
            ColumnHeadersDefaultCellStyle.Font = Theme.Bold;
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            ColumnHeadersHeight = 30;
            RowTemplate.Height = 24;
            DefaultCellStyle.Font = Theme.Base;
            DefaultCellStyle.SelectionBackColor = Color.FromArgb(0xC9, 0xE8, 0xE3);
            DefaultCellStyle.SelectionForeColor = Theme.Ink;
            DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            ShowCellToolTips = true;

            foreach (var c in _columns)
            {
                var col = new DataGridViewTextBoxColumn
                {
                    HeaderText = c.Header,
                    Width = c.Width,
                    SortMode = DataGridViewColumnSortMode.Programmatic,
                    AutoSizeMode = c.Fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
                };
                if (c.Fill) col.MinimumWidth = 120;
                if (c.RightAlign) col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                Columns.Add(col);
            }
        }

        public string EmptyText
        {
            get => _emptyText;
            set { _emptyText = value; Invalidate(); }
        }

        public IReadOnlyList<T> Items => _items;

        public T SelectedItem
        {
            get
            {
                var row = CurrentCell?.RowIndex ?? -1;
                return row >= 0 && row < _items.Count ? _items[row] : null;
            }
        }

        /// <summary>Replaces the rows, keeping the current sort and, when possible, the selection.</summary>
        public void SetItems(IEnumerable<T> items, T select = null)
        {
            var keep = select ?? SelectedItem;
            _items = (items ?? Enumerable.Empty<T>()).ToList();
            ApplySort();
            RowCount = 0;
            RowCount = _items.Count;
            Invalidate();
            if (keep != null) Select(keep);
        }

        public bool Select(T item)
        {
            var index = _items.IndexOf(item);
            if (index < 0 || Columns.Count == 0) return false;
            var col = FirstDisplayedCell?.ColumnIndex ?? 0;
            CurrentCell = Rows[index].Cells[Math.Max(0, col)];
            try
            {
                if (index < FirstDisplayedScrollingRowIndex || index >= FirstDisplayedScrollingRowIndex + DisplayedRowCount(false))
                    FirstDisplayedScrollingRowIndex = Math.Max(0, index - 2);
            }
            catch (InvalidOperationException) { /* grid not laid out yet */ }
            return true;
        }

        protected override void OnCellValueNeeded(DataGridViewCellValueEventArgs e)
        {
            base.OnCellValueNeeded(e);
            if (e.RowIndex < 0 || e.RowIndex >= _items.Count || e.ColumnIndex >= _columns.Count) return;
            var item = _items[e.RowIndex];
            var col = _columns[e.ColumnIndex];
            e.Value = col.Display != null ? col.Display(item) : col.Value(item);
        }

        protected override void OnCellFormatting(DataGridViewCellFormattingEventArgs e)
        {
            base.OnCellFormatting(e);
            if (e.RowIndex < 0 || e.RowIndex >= _items.Count || e.ColumnIndex >= _columns.Count) return;
            var color = _columns[e.ColumnIndex].ForeColor?.Invoke(_items[e.RowIndex]);
            if (color.HasValue)
            {
                e.CellStyle.ForeColor = color.Value;
                e.CellStyle.Font = Theme.Bold;
            }
        }

        protected override void OnColumnHeaderMouseClick(DataGridViewCellMouseEventArgs e)
        {
            base.OnColumnHeaderMouseClick(e);
            var keep = SelectedItem;
            _sortDescending = _sortColumn == e.ColumnIndex && !_sortDescending;
            _sortColumn = e.ColumnIndex;
            foreach (DataGridViewColumn c in Columns) c.HeaderCell.SortGlyphDirection = SortOrder.None;
            Columns[e.ColumnIndex].HeaderCell.SortGlyphDirection = _sortDescending ? SortOrder.Descending : SortOrder.Ascending;
            ApplySort();
            Invalidate();
            if (keep != null) Select(keep);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_items.Count > 0) return;
            var area = new Rectangle(0, ColumnHeadersHeight, Width, Math.Max(0, Height - ColumnHeadersHeight));
            TextRenderer.DrawText(e.Graphics, _emptyText, Theme.Base, area, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        private void ApplySort()
        {
            if (_sortColumn < 0 || _sortColumn >= _columns.Count) return;
            var selector = _columns[_sortColumn].Value;
            var comparer = Comparer<object>.Create(CompareValues);
            _items = (_sortDescending ? _items.OrderByDescending(selector, comparer) : _items.OrderBy(selector, comparer)).ToList();
        }

        private static int CompareValues(object a, object b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            if (a is IComparable ca && a.GetType() == b.GetType()) return ca.CompareTo(b);
            return StringComparer.OrdinalIgnoreCase.Compare(a.ToString(), b.ToString());
        }
    }
}
