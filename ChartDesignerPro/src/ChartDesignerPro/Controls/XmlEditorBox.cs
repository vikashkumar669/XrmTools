using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ChartDesignerPro.Editing;
using ChartDesignerPro.Xml;

namespace ChartDesignerPro.Controls
{
    /// <summary>
    /// XML editor (milestone M3) built on RichTextBox, with no third-party control:
    /// colouring, a line-number gutter, its own undo/redo, a find/replace bar, Format and Go to line.
    ///
    /// Line endings: RichTextBox stores line breaks as "\n". The editor remembers whether the loaded text used "\r\n"
    /// and converts back in <see cref="EditorText"/>, so opening a chart never makes it look changed.
    /// </summary>
    internal sealed class XmlEditorBox : UserControl
    {
        private readonly EditorRichTextBox _rtb;
        private readonly LineGutter _gutter;
        private readonly Panel _findBar;
        private readonly TextBox _txtFind, _txtReplace;
        private readonly CheckBox _chkCase;
        private readonly Label _lblFind;
        private readonly Button _btnReplace, _btnReplaceAll;
        private readonly Timer _recolourTimer;
        private UndoHistory _history = new UndoHistory(string.Empty);
        private string _newLine = "\n";
        private bool _suppress;

        public XmlEditorBox()
        {
            _rtb = new EditorRichTextBox(this)
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 10F),
                WordWrap = false,
                DetectUrls = false,
                HideSelection = false,
                AcceptsTab = true,
                ScrollBars = RichTextBoxScrollBars.Both,
                ReadOnly = true,
                BackColor = SystemColors.Window
            };
            _rtb.TextChanged += OnTextChanged;
            _rtb.VScroll += (s, e) => _gutter.Invalidate();
            _rtb.Resize += (s, e) => _gutter.Invalidate();
            _rtb.SelectionChanged += (s, e) => CaretMoved?.Invoke(this, EventArgs.Empty);

            _gutter = new LineGutter(_rtb) { Dock = DockStyle.Left, Width = 46 };

            // ---- Find / replace bar ----
            _findBar = new Panel { Dock = DockStyle.Top, Height = 32, Visible = false, BackColor = Color.FromArgb(0xF3, 0xF6, 0xF5), Padding = new Padding(6, 4, 6, 4) };
            _txtFind = new TextBox { Width = 200, AccessibleName = "Find" };
            _txtReplace = new TextBox { Width = 200, AccessibleName = "Replace with" };
            _chkCase = new CheckBox { Text = "Match case", AutoSize = true };
            _lblFind = new Label { AutoSize = true, ForeColor = Palette.Muted, Padding = new Padding(6, 4, 0, 0) };
            var btnNext = SmallButton("Next ↓", (s, e) => FindNext(false));
            var btnPrev = SmallButton("Previous ↑", (s, e) => FindNext(true));
            _btnReplace = SmallButton("Replace", (s, e) => ReplaceOne());
            _btnReplaceAll = SmallButton("Replace all", (s, e) => ReplaceAll());
            var btnClose = SmallButton("✕", (s, e) => HideFind());
            btnClose.Width = 28;
            _txtFind.TextChanged += (s, e) => UpdateFindCount();
            _chkCase.CheckedChanged += (s, e) => UpdateFindCount();
            _txtFind.KeyDown += FindKeyDown;
            _txtReplace.KeyDown += FindKeyDown;

            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = false };
            flow.Controls.AddRange(new Control[]
            {
                new Label { Text = "Find:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, _txtFind, btnNext, btnPrev,
                new Label { Text = "Replace:", AutoSize = true, Padding = new Padding(8, 4, 0, 0) }, _txtReplace, _btnReplace, _btnReplaceAll,
                _chkCase, _lblFind, btnClose
            });
            _findBar.Controls.Add(flow);

            _recolourTimer = new Timer { Interval = 350 };
            _recolourTimer.Tick += (s, e) => { _recolourTimer.Stop(); Recolour(); };

            Controls.Add(_rtb);
            Controls.Add(_gutter);
            Controls.Add(_findBar);
        }

        /// <summary>Raised when the user changes the text (not when text is loaded from code).</summary>
        public event EventHandler EditorTextChanged;

        public event EventHandler CaretMoved;

        public bool ReadOnly
        {
            get => _rtb.ReadOnly;
            set
            {
                _rtb.ReadOnly = value;
                _rtb.AcceptsTab = !value;
                _rtb.BackColor = value ? Color.FromArgb(0xFA, 0xFA, 0xFA) : SystemColors.Window;
                _btnReplace.Enabled = _btnReplaceAll.Enabled = _txtReplace.Enabled = !value;
            }
        }

        /// <summary>The text with the line endings it was loaded with.</summary>
        public string EditorText => _newLine == "\n" ? _rtb.Text : _rtb.Text.Replace("\n", _newLine);

        /// <summary>1-based line and column of the caret.</summary>
        public Point Caret
        {
            get
            {
                var index = _rtb.SelectionStart;
                var line = _rtb.GetLineFromCharIndex(index);
                return new Point(index - _rtb.GetFirstCharIndexFromLine(line) + 1, line + 1);
            }
        }

        /// <summary>Loads text from code (opening a chart, switching view): resets undo and doesn't raise <see cref="EditorTextChanged"/>.</summary>
        public void LoadText(string text)
        {
            text = text ?? string.Empty;
            _newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            var internalText = text.Replace("\r\n", "\n");
            SetInternalText(internalText, 0, colour: true);
            _history.Reset(internalText);
            _rtb.SelectionStart = 0;
            _rtb.ScrollToCaret();
        }

        /// <summary>Replaces the whole text as a user edit (Format, Revert): undoable and raises <see cref="EditorTextChanged"/>.</summary>
        public void ReplaceAllText(string text)
        {
            var internalText = (text ?? string.Empty).Replace("\r\n", "\n");
            _history.Break();
            SetInternalText(internalText, Math.Min(_rtb.SelectionStart, internalText.Length), colour: true);
            _history.Record(internalText, _rtb.SelectionStart, DateTime.UtcNow, coalesce: false);
            _history.Break();
            EditorTextChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Indents the XML (an edit). Returns false when the XML isn't well-formed.</summary>
        public bool FormatXml()
        {
            var formatted = SafeXml.Format(EditorText);
            if (formatted == null) return false;
            ReplaceAllText(formatted);
            return true;
        }

        public void GoTo(int line, int column)
        {
            var text = _rtb.Text;
            var index = FindReplace.IndexOfLine(text, line) + Math.Max(0, column - 1);
            index = Math.Max(0, Math.Min(index, text.Length));
            _rtb.Focus();
            _rtb.Select(index, 0);
            _rtb.ScrollToCaret();
        }

        public void ShowFind(bool replace)
        {
            _findBar.Visible = true;
            if (_rtb.SelectionLength > 0 && _rtb.SelectionLength < 200 && !_rtb.SelectedText.Contains("\n")) _txtFind.Text = _rtb.SelectedText;
            (replace && !ReadOnly ? _txtReplace : _txtFind).Focus();
            (replace && !ReadOnly ? _txtReplace : _txtFind).SelectAll();
            UpdateFindCount();
        }

        public void HideFind()
        {
            _findBar.Visible = false;
            _rtb.Focus();
        }

        public void FindNext(bool backwards)
        {
            var term = _txtFind.Text;
            if (string.IsNullOrEmpty(term)) { ShowFind(false); return; }
            var text = _rtb.Text;
            var start = backwards ? _rtb.SelectionStart : _rtb.SelectionStart + _rtb.SelectionLength;
            var i = FindReplace.FindNext(text, term, start, _chkCase.Checked, backwards);
            if (i < 0)
            {
                _lblFind.Text = "No matches";
                return;
            }
            _rtb.Select(i, term.Length);
            _rtb.ScrollToCaret();
        }

        public void Undo()
        {
            if (ReadOnly || !_history.TryUndo(out var s)) return;
            SetInternalText(s.Text, s.Caret, colour: true);
            EditorTextChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Redo()
        {
            if (ReadOnly || !_history.TryRedo(out var s)) return;
            SetInternalText(s.Text, s.Caret, colour: true);
            EditorTextChanged?.Invoke(this, EventArgs.Empty);
        }

        public new bool Focused => _rtb.Focused || _findBar.ContainsFocus;

        public new void Focus() => _rtb.Focus();

        // ------------------------------------------------------------------

        private void ReplaceOne()
        {
            if (ReadOnly) return;
            var term = _txtFind.Text;
            if (string.IsNullOrEmpty(term)) return;
            var comparison = _chkCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (_rtb.SelectionLength == term.Length && string.Equals(_rtb.SelectedText, term, comparison))
            {
                _history.Break();
                _rtb.SelectedText = _txtReplace.Text; // raises TextChanged → recorded
                _history.Break();
            }
            FindNext(false);
            UpdateFindCount();
        }

        private void ReplaceAll()
        {
            if (ReadOnly) return;
            var updated = FindReplace.ReplaceAll(_rtb.Text, _txtFind.Text, _txtReplace.Text, _chkCase.Checked, out var count);
            if (count > 0) ReplaceAllText(updated);
            _lblFind.Text = count == 1 ? "1 replacement" : $"{count} replacements";
        }

        private void UpdateFindCount()
        {
            var n = FindReplace.Count(_rtb.Text, _txtFind.Text, _chkCase.Checked);
            _lblFind.Text = string.IsNullOrEmpty(_txtFind.Text) ? "" : n == 1 ? "1 match" : $"{n} matches";
        }

        private void FindKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (sender == _txtReplace) ReplaceOne(); else FindNext(e.Shift);
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                HideFind();
                e.SuppressKeyPress = true;
            }
        }

        private void OnTextChanged(object sender, EventArgs e)
        {
            _gutter.Invalidate();
            if (_suppress) return;
            _history.Record(_rtb.Text, _rtb.SelectionStart, DateTime.UtcNow);
            _recolourTimer.Stop();
            _recolourTimer.Start();
            EditorTextChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SetInternalText(string text, int caret, bool colour)
        {
            _suppress = true;
            try
            {
                if (colour && text.Length > 0 && text.Length <= XmlRtf.ColourLimit) _rtb.Rtf = XmlRtf.BuildRtf(text, _rtb.Font);
                else { _rtb.Rtf = null; _rtb.Text = text; }
                _rtb.Select(Math.Max(0, Math.Min(caret, _rtb.TextLength)), 0);
            }
            finally { _suppress = false; }
            _gutter.Invalidate();
        }

        /// <summary>Re-applies colouring after typing, keeping the caret, selection and scroll position.</summary>
        private void Recolour()
        {
            var text = _rtb.Text;
            if (text.Length == 0 || text.Length > XmlRtf.ColourLimit) return;
            var start = _rtb.SelectionStart;
            var length = _rtb.SelectionLength;
            var scroll = NativeMethods.GetScrollPos(_rtb);
            _suppress = true;
            NativeMethods.SetRedraw(_rtb, false);
            try
            {
                _rtb.Rtf = XmlRtf.BuildRtf(text, _rtb.Font);
                _rtb.Select(start, length);
                NativeMethods.SetScrollPos(_rtb, scroll);
            }
            finally
            {
                _suppress = false;
                NativeMethods.SetRedraw(_rtb, true);
                _rtb.Invalidate();
                _gutter.Invalidate();
            }
        }

        private static Button SmallButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 24, FlatStyle = FlatStyle.System, Margin = new Padding(3, 0, 0, 0) };
            b.Click += click;
            return b;
        }

        /// <summary>RichTextBox with plain-text paste, two-space Tab and the editor's own undo/redo.</summary>
        private sealed class EditorRichTextBox : RichTextBox
        {
            private readonly XmlEditorBox _owner;
            public EditorRichTextBox(XmlEditorBox owner) { _owner = owner; }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                switch (keyData)
                {
                    case Keys.Control | Keys.Z:
                        _owner.Undo();
                        return true;
                    case Keys.Control | Keys.Y:
                    case Keys.Control | Keys.Shift | Keys.Z:
                        _owner.Redo();
                        return true;
                    case Keys.Control | Keys.V:
                    case Keys.Shift | Keys.Insert:
                        if (!ReadOnly && Clipboard.ContainsText()) SelectedText = Clipboard.GetText().Replace("\r\n", "\n");
                        return true;
                    case Keys.Tab:
                        if (ReadOnly) break; // read-only: Tab moves focus, for keyboard users
                        SelectedText = "  ";
                        return true;
                    case Keys.F3:
                        _owner.FindNext(false);
                        return true;
                    case Keys.Shift | Keys.F3:
                        _owner.FindNext(true);
                        return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }
        }

        /// <summary>Paints line numbers next to the RichTextBox.</summary>
        private sealed class LineGutter : Control
        {
            private readonly RichTextBox _rtb;

            public LineGutter(RichTextBox rtb)
            {
                _rtb = rtb;
                DoubleBuffered = true;
                BackColor = Color.FromArgb(0xF3, 0xF6, 0xF5);
                ForeColor = Palette.Muted;
                Font = new Font("Consolas", 9F);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (_rtb.TextLength == 0) return;
                var first = _rtb.GetLineFromCharIndex(_rtb.GetCharIndexFromPosition(new Point(2, 2)));
                var lineCount = _rtb.GetLineFromCharIndex(_rtb.TextLength) + 1;
                var offset = _rtb.Top - Top; // both docked in the same parent
                for (var line = first; line < lineCount; line++)
                {
                    var index = _rtb.GetFirstCharIndexFromLine(line);
                    if (index < 0) break;
                    var y = _rtb.GetPositionFromCharIndex(index).Y + offset;
                    if (y > Height) break;
                    TextRenderer.DrawText(e.Graphics, (line + 1).ToString(), Font, new Rectangle(0, y + 1, Width - 6, Font.Height + 2),
                        ForeColor, TextFormatFlags.Right | TextFormatFlags.NoPadding);
                }
                using (var pen = new Pen(Color.FromArgb(0xDD, 0xE2, 0xE0)))
                    e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
            }
        }

        private static class NativeMethods
        {
            private const int WM_SETREDRAW = 0x000B;
            private const int WM_USER = 0x0400;
            private const int EM_GETSCROLLPOS = WM_USER + 221;
            private const int EM_SETSCROLLPOS = WM_USER + 222;

            [StructLayout(LayoutKind.Sequential)]
            private struct POINT { public int X; public int Y; }

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref POINT lParam);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

            public static Point GetScrollPos(Control c)
            {
                var p = new POINT();
                if (c.IsHandleCreated) SendMessage(c.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref p);
                return new Point(p.X, p.Y);
            }

            public static void SetScrollPos(Control c, Point pos)
            {
                var p = new POINT { X = pos.X, Y = pos.Y };
                if (c.IsHandleCreated) SendMessage(c.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref p);
            }

            public static void SetRedraw(Control c, bool on)
            {
                if (c.IsHandleCreated) SendMessage(c.Handle, WM_SETREDRAW, on ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
            }
        }
    }
}
