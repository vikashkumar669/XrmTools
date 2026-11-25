namespace ChartDesignerPro.Tests
{
    /// <summary>
    /// Example chart XML for tests. These are hand-written examples in the documented shape, not definitions
    /// guaranteed to be valid in every environment.
    /// </summary>
    internal static class Samples
    {
        public const string ColumnData =
            "<datadefinition><fetchcollection><fetch mapping=\"logical\" aggregate=\"true\"><entity name=\"opportunity\">" +
            "<attribute groupby=\"true\" alias=\"groupby_column\" name=\"stepname\" />" +
            "<attribute alias=\"aggregate_column\" name=\"estimatedvalue\" aggregate=\"sum\" />" +
            "</entity></fetch></fetchcollection><categorycollection><category><measurecollection>" +
            "<measure alias=\"aggregate_column\" /></measurecollection></category></categorycollection></datadefinition>";

        public const string ColumnPresentation =
            "<Chart CustomColorOverride=\"true\"><Series><Series ChartType=\"Column\" IsValueShownAsLabel=\"True\" Color=\"#4169E1\" LabelFormat=\"#,0\" />" +
            "</Series><ChartAreas><ChartArea><AxisY Title=\"Estimated revenue\" /></ChartArea></ChartAreas><Legends><Legend Enabled=\"False\" /></Legends></Chart>";

        public const string Dtd =
            "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;\">]><datadefinition>&lol2;</datadefinition>";

        public const string ExternalEntity =
            "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]><d>&xxe;</d>";

        public const string Malformed =
            "<datadefinition>\n  <fetchcollection>\n    <fetch aggregate=\"true\">\n  </fetchcollection>\n</datadefinition>";
    }
}
