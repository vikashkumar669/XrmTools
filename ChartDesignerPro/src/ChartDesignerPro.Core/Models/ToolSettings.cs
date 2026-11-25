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

        /// <summary>Minutes before table metadata is fetched again.</summary>
        public int MetadataCacheMinutes { get; set; } = 30;
    }
}
