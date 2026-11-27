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
            /// <summary>
        /// A personal chart exported with the app's Export Chart on 2026-11-27 (D365 Env), verbatim: no XML declaration,
        /// leading newline and tabs, a &lt;visualizationid&gt;, nested data and presentation XML, isdefault "false".
        /// </summary>
        public const string AppExport =
            "\n\t\t<visualization>\n" +
            "\t\t\t<visualizationid>{0a18e918-3aea-f111-84ea-000d3af0ef1d}</visualizationid>\n" +
            "\t\t\t<name>CDP Test - Accounts by industry (pie)</name>\n" +
            "\t\t\t<description>Chart Designer Pro test: pie with percentage labels.</description>\n" +
            "\t\t\t<primaryentitytypecode>account</primaryentitytypecode>\n" +
            "\t\t\t<datadescription><datadefinition><fetchcollection><fetch mapping=\"logical\" aggregate=\"true\"><entity name=\"account\"><attribute alias=\"aggregate_column\" name=\"accountid\" aggregate=\"count\" /><attribute groupby=\"true\" alias=\"groupby_column\" name=\"industrycode\" /></entity></fetch></fetchcollection><categorycollection><category><measurecollection><measure alias=\"aggregate_column\" /></measurecollection></category></categorycollection></datadefinition></datadescription>\n" +
            "\t\t\t<presentationdescription><Chart Palette=\"None\" PaletteCustomColors=\"0,105,92; 38,166,154; 128,203,196; 255,183,77; 229,115,115\"><Series><Series ChartType=\"Pie\" IsValueShownAsLabel=\"True\" Label=\"#PERCENT{P0}\" CustomProperties=\"PieLabelStyle=Outside\" /></Series><ChartAreas><ChartArea BorderColor=\"White\" /></ChartAreas><Legends><Legend Enabled=\"True\" Docking=\"Right\" /></Legends><Titles><Title Alignment=\"TopLeft\" /></Titles></Chart></presentationdescription>\n" +
            "\t\t\t<isdefault>false</isdefault>\n" +
            "\t\t</visualization>\n";
    }
}
