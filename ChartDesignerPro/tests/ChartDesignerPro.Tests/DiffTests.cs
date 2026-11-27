using System.Linq;
using ChartDesignerPro.Comparison;

namespace ChartDesignerPro.Tests
{
    public static class DiffTests
    {
        [Test]
        public static void IdenticalTextHasNoChanges()
        {
            var d = LineDiff.Compare("a\nb\nc", "a\nb\nc", false);
            Assert.False(d.HasChanges, "no changes");
            Assert.Equal(3, d.Lines.Count);
            Assert.Equal("No differences", d.Summary);
        }

        [Test]
        public static void DetectsAddedRemovedAndChangedLines()
        {
            var d = LineDiff.Compare("one\ntwo\nthree\nfour", "one\nTWO\nthree\nfour\nfive", false);
            Assert.Equal(1, d.Removed);
            Assert.Equal(2, d.Added);
            var removed = d.Lines.Single(l => l.Kind == DiffKind.Removed);
            Assert.Equal("two", removed.Text);
            Assert.Equal(2, removed.OldNumber);
            Assert.Equal(5, d.Lines.Last().NewNumber);
            Assert.Equal("five", d.Lines.Last().Text);
        }

        [Test]
        public static void CrLfAndLfCompareEqual()
        {
            Assert.False(LineDiff.Compare("a\r\nb", "a\nb", false).HasChanges, "line endings ignored per line");
        }

        [Test]
        public static void IgnoreFormattingTreatsReformattedXmlAsEqual()
        {
            const string compact = "<Chart><Series><Series Color=\"red\" ChartType=\"Column\" /></Series></Chart>";
            const string pretty = "<Chart>\n    <Series>\n      <Series ChartType=\"Column\"   Color=\"red\"/>\n    </Series>\n</Chart>";
            Assert.True(LineDiff.Compare(compact, pretty, false).HasChanges, "exact diff sees formatting");
            var d = LineDiff.Compare(compact, pretty, true);
            Assert.False(d.HasChanges, "attribute order and whitespace ignored");
            Assert.False(d.FellBackToExact, "both parsed");
        }

        [Test]
        public static void IgnoreFormattingStillFindsRealChanges()
        {
            var d = LineDiff.Compare(Samples.ColumnPresentation, Samples.ColumnPresentation.Replace("Column", "Bar"), true);
            Assert.Equal(1, d.Removed);
            Assert.Equal(1, d.Added);
            Assert.Contains("ChartType=\"Bar\"", d.Lines.Single(l => l.Kind == DiffKind.Added).Text);
        }

        [Test]
        public static void InvalidXmlFallsBackToExact()
        {
            var d = LineDiff.Compare("<a>", "<a></a>", true);
            Assert.True(d.FellBackToExact, "fell back");
            Assert.True(d.HasChanges, "still compared");
        }

        [Test]
        public static void NormalizeKeepsCommentsAndSortsAttributes()
        {
            var n = XmlNormalizer.Normalize("<a z=\"1\" b=\"2\"><!-- note --><c/></a>");
            Assert.Contains("<a b=\"2\" z=\"1\">", n);
            Assert.Contains("<!-- note -->", n);
            Assert.True(XmlNormalizer.Normalize("<broken>") == null, "invalid → null");
        }
    }
}
