namespace ChartDesignerPro.Models
{
    /// <summary>Per-connection settings. Public properties only, so XrmToolBox's SettingsManager can XML-serialize it. No secrets are ever stored here.</summary>
    public class ToolSettings
    {
        public bool IncludeSystem { get; set; } = true;
        public bool IncludePersonal { get; set; } = true;

        /// <summary>Show XML indented in the viewer. Display only: never changes what is saved.</summary>
        public bool FormattedView { get; set; } = true;

        /// <summary>Logical name of the last table filter, or null for all tables.</summary>
        public string LastTable { get; set; }

        /// <summary>Favourite charts as "Scope:id" keys (see Explorer.ChartBookmarks).</summary>
        public System.Collections.Generic.List<string> Favourites { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>Recently opened charts as "Scope:id" keys, most recent first.</summary>
        public System.Collections.Generic.List<string> Recent { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>Minutes before table metadata is fetched again.</summary>
        public int MetadataCacheMinutes { get; set; } = 30;
    }
}
