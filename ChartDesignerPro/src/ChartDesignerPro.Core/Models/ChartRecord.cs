using System;

namespace ChartDesignerPro.Models
{
    /// <summary>System chart = savedqueryvisualization (organization-owned). Personal chart = userqueryvisualization (user-owned).</summary>
    public enum ChartScope { System, Personal }

    /// <summary>Values of the charttype choice column: 0 = ASP.NET Charts, 1 = Power BI.</summary>
    public enum ChartEngine { Unknown = -1, AspNetCharts = 0, PowerBI = 1 }

    /// <summary>
    /// One chart row as retrieved from Dataverse. The Data/ layer of the plugin maps SDK entities into this class,
    /// so the core never depends on the Dataverse SDK. Nullable values mean "not retrieved or not applicable", never "false".
    /// </summary>
    public sealed class ChartRecord
    {
        public Guid Id { get; set; }
        public ChartScope Scope { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }

        /// <summary>Logical name of the table the chart is attached to (primaryentitytypecode).</summary>
        public string TableLogicalName { get; set; }

        /// <summary>Display name of the table, filled in when metadata is available.</summary>
        public string TableDisplayName { get; set; }

        public ChartEngine Engine { get; set; } = ChartEngine.Unknown;

        /// <summary>Set when the visualization is a web resource instead of a chart.</summary>
        public Guid? WebResourceId { get; set; }

        public bool IsDefault { get; set; }

        /// <summary>System charts only.</summary>
        public bool? IsManaged { get; set; }

        /// <summary>System charts only: the IsCustomizable managed property.</summary>
        public bool? IsCustomizable { get; set; }

        public DateTime? ModifiedOnUtc { get; set; }
        public string ModifiedByName { get; set; }

        /// <summary>Personal charts only.</summary>
        public string OwnerName { get; set; }

        /// <summary>datadescription, exactly as retrieved. Null when the list was loaded without XML.</summary>
        public string DataXml { get; set; }

        /// <summary>presentationdescription, exactly as retrieved. Null when the list was loaded without XML.</summary>
        public string PresentationXml { get; set; }

        /// <summary>True once DataXml and PresentationXml have been retrieved (they may still be empty strings).</summary>
        public bool HasXml { get; set; }

        public string ScopeText => Scope == ChartScope.System ? "System" : "Personal";

        public string EngineText
        {
            get
            {
                if (WebResourceId.HasValue) return "Web resource";
                switch (Engine)
                {
                    case ChartEngine.AspNetCharts: return "Chart";
                    case ChartEngine.PowerBI: return "Power BI";
                    default: return "Unknown";
                }
            }
        }

        public string TableText => string.IsNullOrEmpty(TableDisplayName) || TableDisplayName == TableLogicalName
            ? TableLogicalName
            : TableDisplayName + " (" + TableLogicalName + ")";

        public override string ToString() => Name;
    }
}
