using System;
using System.Collections.Generic;

namespace ChartDesignerPro.Editing
{
    /// <summary>Text plus caret position, as stored in the undo history.</summary>
    public struct TextSnapshot
    {
        public TextSnapshot(string text, int caret)
        {
            Text = text ?? string.Empty;
            Caret = caret;
        }

        public string Text { get; }
        public int Caret { get; }
    }

    /// <summary>
    /// Undo/redo of whole-text snapshots. The editor re-colours text by replacing it, which clears the RichTextBox's
    /// own undo stack, so the editor keeps its history here. Typing bursts within <see cref="CoalesceWindow"/> become one step.
    /// Chart XML is small (a few KB), so whole-text snapshots are cheap; the history is capped.
    /// </summary>
    public sealed class UndoHistory
    {
        private readonly List<TextSnapshot> _undo = new List<TextSnapshot>();
        private readonly List<TextSnapshot> _redo = new List<TextSnapshot>();
        private TextSnapshot _current;
        private DateTime _lastChangeUtc = DateTime.MinValue;

        public UndoHistory(string initialText, int maxSteps = 200)
        {
            _current = new TextSnapshot(initialText, 0);
            MaxSteps = Math.Max(1, maxSteps);
        }

        public int MaxSteps { get; }
        public TimeSpan CoalesceWindow { get; set; } = TimeSpan.FromSeconds(1);
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public TextSnapshot Current => _current;

        /// <summary>Records a new state. Identical text is ignored. <paramref name="coalesce"/> merges with the previous step when typing quickly.</summary>
        public void Record(string text, int caret, DateTime utcNow, bool coalesce = true)
        {
            text = text ?? string.Empty;
            if (string.Equals(text, _current.Text, StringComparison.Ordinal)) { _current = new TextSnapshot(text, caret); return; }

            var merge = coalesce && _undo.Count > 0 && utcNow - _lastChangeUtc < CoalesceWindow;
            if (!merge)
            {
                _undo.Add(_current);
                if (_undo.Count > MaxSteps) _undo.RemoveAt(0);
            }
            _current = new TextSnapshot(text, caret);
            _redo.Clear();
            _lastChangeUtc = utcNow;
        }

        /// <summary>Forces the next change to start a new undo step (e.g. after Format or Replace All).</summary>
        public void Break() => _lastChangeUtc = DateTime.MinValue;

        public bool TryUndo(out TextSnapshot snapshot)
        {
            snapshot = _current;
            if (_undo.Count == 0) return false;
            _redo.Add(_current);
            _current = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            snapshot = _current;
            Break();
            return true;
        }

        public bool TryRedo(out TextSnapshot snapshot)
        {
            snapshot = _current;
            if (_redo.Count == 0) return false;
            _undo.Add(_current);
            _current = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            snapshot = _current;
            Break();
            return true;
        }

        /// <summary>Starts over with a new baseline (new chart opened, or changes discarded).</summary>
        public void Reset(string text)
        {
            _undo.Clear();
            _redo.Clear();
            _current = new TextSnapshot(text, 0);
            Break();
        }
    }

    /// <summary>Find and replace over plain text. Pure functions, so they are unit-tested.</summary>
    public static class FindReplace
    {
        /// <summary>
        /// Index of the next match at or after <paramref name="start"/> (or before it when searching backwards), wrapping around
        /// the end of the text. Returns -1 when there is no match.
        /// </summary>
        public static int FindNext(string text, string term, int start, bool matchCase, bool backwards = false)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return -1;
            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            start = Math.Max(0, Math.Min(start, text.Length));

            if (!backwards)
            {
                var i = text.IndexOf(term, start, comparison);
                if (i < 0 && start > 0) i = text.IndexOf(term, 0, comparison);
                return i;
            }

            var from = start - 1;
            var j = from >= 0 ? text.LastIndexOf(term, from, comparison) : -1;
            if (j < 0) j = text.LastIndexOf(term, text.Length - 1, comparison);
            return j;
        }

        public static int Count(string text, string term, bool matchCase)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return 0;
            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var count = 0;
            for (var i = text.IndexOf(term, 0, comparison); i >= 0; i = text.IndexOf(term, i + term.Length, comparison)) count++;
            return count;
        }

        /// <summary>Replaces every match and returns the new text; <paramref name="count"/> is the number of replacements.</summary>
        public static string ReplaceAll(string text, string term, string replacement, bool matchCase, out int count)
        {
            count = 0;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return text;
            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var sb = new System.Text.StringBuilder(text.Length);
            var pos = 0;
            for (var i = text.IndexOf(term, 0, comparison); i >= 0; i = text.IndexOf(term, pos, comparison))
            {
                sb.Append(text, pos, i - pos).Append(replacement ?? string.Empty);
                pos = i + term.Length;
                count++;
            }
            sb.Append(text, pos, text.Length - pos);
            return sb.ToString();
        }

        /// <summary>Converts a 1-based line number to the index of the first character of that line (clamped to the text).</summary>
        public static int IndexOfLine(string text, int line)
        {
            if (string.IsNullOrEmpty(text) || line <= 1) return 0;
            var current = 1;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                current++;
                if (current == line) return i + 1;
            }
            return text.Length;
        }
    }
}
