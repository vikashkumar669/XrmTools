using System;
using System.Collections.Generic;
using System.Threading;
using ChartDesignerPro.Models;

namespace ChartDesignerPro.Abstractions
{
    /// <summary>What to list in the Chart Explorer.</summary>
    public sealed class ChartQuery
    {
        /// <summary>Logical name of one table, or null for every table.</summary>
        public string TableLogicalName { get; set; }
        public bool IncludeSystem { get; set; } = true;
        public bool IncludePersonal { get; set; } = true;

        public string Describe()
        {
            var scope = IncludeSystem && IncludePersonal ? "system and personal" : IncludeSystem ? "system" : "personal";
            return scope + " charts" + (string.IsNullOrEmpty(TableLogicalName) ? " for all tables" : " for " + TableLogicalName);
        }
    }

    /// <summary>Result of a list call: personal charts can fail independently (privileges) without hiding system charts.</summary>
    public sealed class ChartListResult
    {
        public List<ChartRecord> Charts { get; } = new List<ChartRecord>();
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>Reads and writes chart rows. Implemented in the plugin's Data/ folder.</summary>
    public interface IChartRepository
    {
        /// <summary>Lists charts without their XML columns, paging through all results.</summary>
        ChartListResult ListCharts(ChartQuery query, CancellationToken ct, IProgress<string> progress);

        /// <summary>Retrieves one chart including datadescription and presentationdescription. Returns null when it no longer exists.</summary>
        ChartRecord Retrieve(ChartScope scope, Guid id);

        /// <summary>Updates the chart's XML columns. A null argument leaves that column unchanged (only changed columns are sent).</summary>
        void Update(ChartScope scope, Guid id, string dataXml, string presentationXml);

        /// <summary>Publishes the customizations of one table (PublishXmlRequest scoped to the table), never the whole environment.</summary>
        void PublishTable(string tableLogicalName);
    }

    /// <summary>Table and column metadata. Implemented in the plugin's Data/ folder; cached through <see cref="Explorer.MetadataCache"/>.</summary>
    public interface IMetadataProvider
    {
        IReadOnlyList<TableSummary> ListTables(CancellationToken ct);
        TableMetadata GetTable(string logicalName);
    }

    /// <summary>Diagnostic log. Implementations must never write tokens, passwords or connection strings.</summary>
    public interface ILog
    {
        void Info(string message, params object[] args);
        void Warn(string message, params object[] args);
        void Error(Exception exception, string message, params object[] args);
    }

    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
