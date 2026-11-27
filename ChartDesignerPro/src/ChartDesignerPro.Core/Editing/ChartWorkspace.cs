using System;
using System.Xml.Linq;
using ChartDesignerPro.Models;
using ChartDesignerPro.Xml;

namespace ChartDesignerPro.Editing
{
    public enum DocumentKind { Data, Presentation }

    /// <summary>
    /// One editable XML column (datadescription or presentationdescription). The original text is a snapshot that never
    /// changes; the current text is what the editor shows. Parsing is cached per text.
    /// </summary>
    public sealed class EditableDocument
    {
        private string _parsedText;
        private XDocument _parsed;
        private XmlParseError _error;

        public EditableDocument(DocumentKind kind, string original)
        {
            Kind = kind;
            Original = original ?? string.Empty;
            Current = Original;
        }

        public DocumentKind Kind { get; }
        public string Original { get; private set; }
        public string Current { get; set; }

        /// <summary>Ordinal comparison: any change, including whitespace, counts.</summary>
        public bool IsDirty => !string.Equals(Original, Current ?? string.Empty, StringComparison.Ordinal);

        public string Title => Kind == DocumentKind.Data ? "Data XML" : "Presentation XML";

        /// <summary>The parsed current text, or null with <paramref name="error"/> set when it isn't well-formed or is empty.</summary>
        public XDocument Parse(out XmlParseError error)
        {
            if (!string.Equals(_parsedText, Current, StringComparison.Ordinal))
            {
                _parsedText = Current;
                SafeXml.TryParse(Current, out _parsed, out _error);
            }
            error = _error;
            return _parsed;
        }

        public void Revert() => Current = Original;

        /// <summary>Makes the current text the new baseline (used after a successful save, milestone M6).</summary>
        public void AcceptCurrent() => Original = Current ?? string.Empty;
    }

    /// <summary>The chart that is open, with its editable documents and edit state. No Dataverse access.</summary>
    public sealed class ChartWorkspace
    {
        public ChartWorkspace(ChartRecord record)
        {
            Record = record ?? throw new ArgumentNullException(nameof(record));
            Data = new EditableDocument(DocumentKind.Data, record.DataXml);
            Presentation = new EditableDocument(DocumentKind.Presentation, record.PresentationXml);
            Capability = ChartCapability.For(record);
        }

        public ChartRecord Record { get; }
        public EditableDocument Data { get; }
        public EditableDocument Presentation { get; }
        public ChartCapability Capability { get; }
        public bool IsEditing { get; private set; }

        public bool IsDirty => Data.IsDirty || Presentation.IsDirty;

        public EditableDocument Document(DocumentKind kind) => kind == DocumentKind.Data ? Data : Presentation;

        /// <summary>Switches to edit mode. Returns false (and stays read-only) when the chart can't be edited.</summary>
        public bool BeginEdit()
        {
            if (!Capability.CanEdit) return false;
            IsEditing = true;
            return true;
        }

        /// <summary>Throws away all changes and returns to read-only mode.</summary>
        public void DiscardChanges()
        {
            Data.Revert();
            Presentation.Revert();
            IsEditing = false;
        }

        /// <summary>A short description of what changed, e.g. "Data XML and Presentation XML changed".</summary>
        public string ChangeSummary()
        {
            if (Data.IsDirty && Presentation.IsDirty) return "Data XML and Presentation XML changed";
            if (Data.IsDirty) return "Data XML changed";
            if (Presentation.IsDirty) return "Presentation XML changed";
            return "No changes";
        }
    }
}
