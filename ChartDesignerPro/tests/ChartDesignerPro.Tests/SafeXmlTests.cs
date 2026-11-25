using System.Linq;
using ChartDesignerPro.Xml;

namespace ChartDesignerPro.Tests
{
    public static class SafeXmlTests
    {
        [Test]
        public static void ParsesValidChartXml()
        {
            Assert.True(SafeXml.TryParse(Samples.ColumnData, out var doc, out var error), error?.ToString());
            Assert.Equal("datadefinition", doc.Root.Name.LocalName);
            Assert.True(SafeXml.IsWellFormed(Samples.ColumnPresentation));
        }

        [Test]
        public static void ReportsLineAndColumnForMalformedXml()
        {
            Assert.False(SafeXml.TryParse(Samples.Malformed, out var doc, out var error));
            Assert.True(doc == null, "document should be null on failure");
            Assert.Equal(4, error.Line, "line");
            Assert.True(error.Column > 0, "column should be set");
            Assert.DoesNotContain(" Line ", error.Message);
        }

        [Test]
        public static void RejectsDtdAndEntityExpansion()
        {
            Assert.False(SafeXml.TryParse(Samples.Dtd, out _, out var error));
            Assert.Contains("DTD", error.Message);
        }

        [Test]
        public static void RejectsExternalEntities()
        {
            Assert.False(SafeXml.TryParse(Samples.ExternalEntity, out _, out var error));
            Assert.Contains("DTD", error.Message);
        }

        [Test]
        public static void RejectsEmptyAndOversizedInput()
        {
            Assert.False(SafeXml.TryParse("   ", out _, out var empty));
            Assert.Contains("empty", empty.Message);

            var huge = "<a>" + new string('x', (int)SafeXml.MaxCharacters) + "</a>";
            Assert.False(SafeXml.TryParse(huge, out _, out var tooLarge));
            Assert.Contains("too large", tooLarge.Message);
        }

        [Test]
        public static void PreservesWhitespaceAndUnknownNodes()
        {
            const string text = "<Chart>\n  <!-- keep me -->\n  <Unknown Foo=\"1\" />\n</Chart>";
            Assert.True(SafeXml.TryParse(text, out var doc, out _));
            Assert.Equal(text, SafeXml.Serialize(doc));
        }

        [Test]
        public static void SerializeKeepsDeclarationAndDoesNotConvertNewlines()
        {
            const string text = "<?xml version=\"1.0\" encoding=\"utf-8\"?><a>\n  <b x=\"1\" />\n</a>";
            Assert.True(SafeXml.TryParse(text, out var doc, out _));
            var output = SafeXml.Serialize(doc);
            Assert.DoesNotContain("\r", output);
            Assert.Contains("<?xml version=\"1.0\"", output);
            Assert.Contains("<a>\n  <b x=\"1\" />\n</a>", output);
        }

        [Test]
        public static void SingleAttributeChangeLeavesEverythingElseIntact()
        {
            Assert.True(SafeXml.TryParse(Samples.ColumnPresentation, out var doc, out _));
            doc.Root.Element("Series").Element("Series").SetAttributeValue("Color", "#008000");
            Assert.Equal(Samples.ColumnPresentation.Replace("#4169E1", "#008000"), SafeXml.Serialize(doc));
        }

        [Test]
        public static void FormatIndentsWithoutChangingContent()
        {
            var formatted = SafeXml.Format(Samples.ColumnData);
            Assert.True(formatted != null, "format returned null");
            Assert.Contains("\r\n  <fetchcollection>", formatted);
            Assert.True(SafeXml.TryParse(formatted, out var a, out _));
            Assert.True(SafeXml.TryParse(Samples.ColumnData, out var b, out _));
            Assert.Equal(b.Descendants().Count(), a.Descendants().Count(), "element count");
            Assert.Equal(b.Descendants().SelectMany(e => e.Attributes()).Count(), a.Descendants().SelectMany(e => e.Attributes()).Count(), "attribute count");
        }

        [Test]
        public static void FormatReturnsNullForInvalidXml()
        {
            Assert.True(SafeXml.Format(Samples.Malformed) == null, "expected null");
        }
    }
}
