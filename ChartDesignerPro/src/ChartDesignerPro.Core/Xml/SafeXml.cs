using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ChartDesignerPro.Xml
{
    /// <summary>Where and why a document failed to parse. Line and column are 1-based (0 when unknown).</summary>
    public sealed class XmlParseError
    {
        public XmlParseError(string message, int line, int column)
        {
            Message = message;
            Line = line;
            Column = column;
        }

        public string Message { get; }
        public int Line { get; }
        public int Column { get; }

        public override string ToString() => Line > 0 ? $"Line {Line}, column {Column}: {Message}" : Message;
    }

    /// <summary>
    /// The only way the tool parses XML. Chart XML comes from Dataverse or from files the user imports, so it is treated
    /// as untrusted: DTDs are prohibited (no entity expansion), no external resources are resolved, processing
    /// instructions are ignored and the document size is capped.
    /// </summary>
    public static class SafeXml
    {
        /// <summary>Upper bound on characters in one document. Chart XML is normally a few KB.</summary>
        public const long MaxCharacters = 2_000_000;

        public static XmlReaderSettings CreateReaderSettings() => new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreProcessingInstructions = true,
            CloseInput = true
        };

        /// <summary>
        /// Parses <paramref name="text"/>. Returns false with an error (and a null document) when the text is empty,
        /// not well-formed, contains a DTD, or is too large. Whitespace is preserved and line information is kept.
        /// </summary>
        public static bool TryParse(string text, out XDocument document, out XmlParseError error)
        {
            document = null;
            error = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = new XmlParseError("The XML is empty.", 0, 0);
                return false;
            }

            if (text.Length > MaxCharacters)
            {
                error = new XmlParseError($"The XML is too large ({text.Length:N0} characters; the limit is {MaxCharacters:N0}).", 0, 0);
                return false;
            }

            try
            {
                using (var reader = XmlReader.Create(new StringReader(text), CreateReaderSettings()))
                {
                    document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
                }
                return true;
            }
            catch (XmlException ex)
            {
                document = null;
                error = new XmlParseError(Explain(ex), ex.LineNumber, ex.LinePosition);
                return false;
            }
        }

        /// <summary>True when the text parses (see <see cref="TryParse"/>).</summary>
        public static bool IsWellFormed(string text) => TryParse(text, out _, out _);

        private static string Explain(XmlException ex)
        {
            var message = ex.Message ?? "The XML is not well-formed.";
            // XmlException appends " Line x, position y." which is shown separately.
            var cut = message.IndexOf(" Line ", StringComparison.Ordinal);
            if (cut > 0) message = message.Substring(0, cut);
            if (message.IndexOf("DTD", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The XML contains a document type definition (DTD). DTDs aren't allowed in chart XML and are rejected for safety.";
            return message;
        }

        /// <summary>
        /// Returns an indented copy of <paramref name="text"/> for display or for an explicit "Format XML" action.
        /// Returns null when the text doesn't parse. Never used to save a chart unless the user asked to format it.
        /// </summary>
        public static string Format(string text, string indent = "  ")
        {
            if (!TryParse(text, out var document, out _)) return null;

            // Re-parse without preserved whitespace so the writer can indent consistently.
            XDocument compact;
            var readerSettings = CreateReaderSettings();
            readerSettings.IgnoreWhitespace = true; // drop existing indentation so the writer can indent consistently
            using (var reader = XmlReader.Create(new StringReader(text), readerSettings))
            {
                compact = XDocument.Load(reader, LoadOptions.None);
            }

            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = indent,
                OmitXmlDeclaration = document.Declaration == null,
                NewLineChars = "\r\n",
                NewLineHandling = NewLineHandling.Replace,
                Encoding = new UTF8Encoding(false)
            };

            var sb = new StringBuilder();
            using (var writer = XmlWriter.Create(new StringWriterUtf8(sb), settings))
            {
                compact.Save(writer);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Writes a document back to text without reformatting it: no indentation and no newline conversion.
        /// (XDocument.ToString converts "\n" to "\r\n" on .NET Framework, which is the runtime XrmToolBox uses.)
        /// The XML declaration is written only if the document has one. Note that parsing already normalised
        /// "\r\n" inside the document to "\n", as the XML specification requires, which is why the tool keeps the
        /// original text and only serializes documents the user actually changed.
        /// </summary>
        public static string Serialize(XDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var settings = new XmlWriterSettings
            {
                Indent = false,
                OmitXmlDeclaration = document.Declaration == null,
                NewLineHandling = NewLineHandling.None,
                Encoding = new UTF8Encoding(false)
            };
            var sb = new StringBuilder();
            using (var writer = XmlWriter.Create(new StringWriterUtf8(sb), settings))
            {
                document.Save(writer);
            }
            return sb.ToString();
        }

        private sealed class StringWriterUtf8 : StringWriter
        {
            public StringWriterUtf8(StringBuilder sb) : base(sb) { }
            public override Encoding Encoding => new UTF8Encoding(false);
        }
    }
}
