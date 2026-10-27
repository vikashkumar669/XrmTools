using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Data
{
    /// <summary>
    /// FetchXML for the plugintracelog table and step metadata. Kept free of the Dataverse SDK so it can be unit-tested.
    /// Column names follow the PluginTraceLog table reference; the configuration columns are never requested.
    /// </summary>
    public static class TraceFetchXml
    {
        /// <summary>Dataverse returns at most 5,000 rows per page.</summary>
        public const int MaxPageSize = 5000;

        /// <summary>IDs per "in" condition when fetching details for known traces.</summary>
        public const int IdBatchSize = 200;

        /// <summary>Summary columns: everything except the large text columns.</summary>
        public static readonly string[] SummaryColumns =
        {
            "plugintracelogid", "createdon", "typename", "messagename", "primaryentity",
            "performanceexecutionduration", "performanceconstructorduration", "performanceexecutionstarttime",
            "correlationid", "requestid", "depth", "mode", "operationtype", "pluginstepid",
        };

        /// <summary>Columns that must never be requested: they can hold secrets from the step registration.</summary>
        public static readonly string[] ForbiddenColumns = { "configuration", "secureconfiguration" };

        /// <summary>Page of summary rows (no exception or trace text), newest first.</summary>
        public static string Summary(TraceQuery query, int page, string pagingCookie, int count)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            var sb = Open(page, pagingCookie, count);
            foreach (var c in SummaryColumns) Attribute(sb, c);
            Order(sb);
            sb.Append("<filter type=\"and\">");
            Filters(sb, query, query.FromUtc);
            sb.Append("</filter>");
            return Close(sb);
        }

        /// <summary>
        /// Page of exception text for failed traces matching the same filters, limited to the time window the summary
        /// retrieval covered (<paramref name="fromUtc"/> is the oldest summary row).
        /// </summary>
        public static string Exceptions(TraceQuery query, DateTime fromUtc, int page, string pagingCookie, int count)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            var sb = Open(page, pagingCookie, count);
            Attribute(sb, "plugintracelogid");
            Attribute(sb, "exceptiondetails");
            Order(sb);
            sb.Append("<filter type=\"and\">");
            var q = query.Clone();
            q.Exceptions = ExceptionFilter.WithException;
            Filters(sb, q, fromUtc < query.FromUtc ? query.FromUtc : fromUtc);
            sb.Append("</filter>");
            return Close(sb);
        }

        /// <summary>Exception text for a batch of known trace IDs (fallback when the not-null filter isn't accepted).</summary>
        public static string ExceptionsByIds(IEnumerable<Guid> ids) => ByIds(ids, "exceptiondetails");

        /// <summary>Trace text and exception for one trace (detail view).</summary>
        public static string Detail(Guid id) => ByIds(new[] { id }, "exceptiondetails", "messageblock");

        /// <summary>Step, plug-in type and assembly for a batch of step IDs.</summary>
        public static string Steps(IEnumerable<Guid> stepIds)
        {
            var list = Distinct(stepIds);
            var sb = new StringBuilder();
            sb.Append("<fetch no-lock=\"true\"><entity name=\"sdkmessageprocessingstep\">");
            foreach (var c in new[] { "sdkmessageprocessingstepid", "name", "stage", "mode", "rank", "statecode", "plugintypeid" }) Attribute(sb, c);
            sb.Append("<filter><condition attribute=\"sdkmessageprocessingstepid\" operator=\"in\">");
            foreach (var id in list) sb.Append("<value>").Append(id.ToString("D")).Append("</value>");
            sb.Append("</condition></filter>");
            sb.Append("<link-entity name=\"plugintype\" from=\"plugintypeid\" to=\"plugintypeid\" link-type=\"outer\" alias=\"type\">");
            Attribute(sb, "typename");
            Attribute(sb, "assemblyname");
            sb.Append("<link-entity name=\"pluginassembly\" from=\"pluginassemblyid\" to=\"pluginassemblyid\" link-type=\"outer\" alias=\"asm\">");
            Attribute(sb, "name");
            Attribute(sb, "version");
            sb.Append("</link-entity></link-entity></entity></fetch>");
            return sb.ToString();
        }

        /// <summary>Splits IDs into batches for "in" conditions.</summary>
        public static IEnumerable<List<Guid>> Batches(IEnumerable<Guid> ids, int size = IdBatchSize)
        {
            var list = Distinct(ids);
            for (var i = 0; i < list.Count; i += size) yield return list.Skip(i).Take(size).ToList();
        }

        // ---------------------------------------------------------------------------------------------------------

        private static string ByIds(IEnumerable<Guid> ids, params string[] columns)
        {
            var list = Distinct(ids);
            if (list.Count == 0) throw new ArgumentException("At least one ID is required.", nameof(ids));
            if (list.Count > IdBatchSize) throw new ArgumentException($"At most {IdBatchSize} IDs per query.", nameof(ids));
            var sb = new StringBuilder();
            sb.Append("<fetch no-lock=\"true\"><entity name=\"plugintracelog\">");
            Attribute(sb, "plugintracelogid");
            foreach (var c in columns) Attribute(sb, c);
            sb.Append("<filter><condition attribute=\"plugintracelogid\" operator=\"in\">");
            foreach (var id in list) sb.Append("<value>").Append(id.ToString("D")).Append("</value>");
            sb.Append("</condition></filter></entity></fetch>");
            return sb.ToString();
        }

        private static List<Guid> Distinct(IEnumerable<Guid> ids) =>
            (ids ?? Enumerable.Empty<Guid>()).Where(g => g != Guid.Empty).Distinct().ToList();

        private static StringBuilder Open(int page, string pagingCookie, int count)
        {
            var sb = new StringBuilder();
            sb.Append("<fetch count=\"").Append(Math.Max(1, Math.Min(MaxPageSize, count)).ToString(CultureInfo.InvariantCulture))
              .Append("\" page=\"").Append(Math.Max(1, page).ToString(CultureInfo.InvariantCulture)).Append('"');
            if (!string.IsNullOrEmpty(pagingCookie)) sb.Append(" paging-cookie=\"").Append(SecurityElement.Escape(pagingCookie)).Append('"');
            sb.Append(" no-lock=\"true\"><entity name=\"plugintracelog\">");
            return sb;
        }

        private static string Close(StringBuilder sb)
        {
            sb.Append("</entity></fetch>");
            return sb.ToString();
        }

        private static void Order(StringBuilder sb)
        {
            sb.Append("<order attribute=\"createdon\" descending=\"true\" />");
            sb.Append("<order attribute=\"plugintracelogid\" descending=\"true\" />");
        }

        private static void Attribute(StringBuilder sb, string name)
        {
            if (ForbiddenColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The step configuration columns are never retrieved.");
            sb.Append("<attribute name=\"").Append(name).Append("\" />");
        }

        private static void Filters(StringBuilder sb, TraceQuery q, DateTime fromUtc)
        {
            Condition(sb, "createdon", "ge", Iso(fromUtc));
            Condition(sb, "createdon", "le", Iso(q.ToUtc));
            if (q.Exceptions == ExceptionFilter.WithException) sb.Append("<condition attribute=\"exceptiondetails\" operator=\"not-null\" />");
            if (q.Exceptions == ExceptionFilter.WithoutException) sb.Append("<condition attribute=\"exceptiondetails\" operator=\"null\" />");
            if (!string.IsNullOrWhiteSpace(q.MessageName)) Condition(sb, "messagename", "eq", q.MessageName.Trim());
            if (!string.IsNullOrWhiteSpace(q.PrimaryEntity)) Condition(sb, "primaryentity", "eq", q.PrimaryEntity.Trim());
            if (!string.IsNullOrWhiteSpace(q.PluginType)) Condition(sb, "typename", "like", "%" + EscapeLike(q.PluginType.Trim()) + "%");
            if (q.CorrelationId.HasValue) Condition(sb, "correlationid", "eq", q.CorrelationId.Value.ToString("D"));
            if (q.RequestId.HasValue) Condition(sb, "requestid", "eq", q.RequestId.Value.ToString("D"));
            if (q.MinDurationMs.HasValue) Condition(sb, "performanceexecutionduration", "ge", q.MinDurationMs.Value.ToString(CultureInfo.InvariantCulture));
        }

        private static void Condition(StringBuilder sb, string attribute, string op, string value)
        {
            sb.Append("<condition attribute=\"").Append(attribute).Append("\" operator=\"").Append(op)
              .Append("\" value=\"").Append(SecurityElement.Escape(value)).Append("\" />");
        }

        /// <summary>UTC ISO 8601 with the Z suffix so Dataverse doesn't apply the user's time zone.</summary>
        public static string Iso(DateTime utc) =>
            (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc))
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        /// <summary>FetchXML like: [ ] _ and % are wildcards and are escaped by bracketing.</summary>
        public static string EscapeLike(string value) => value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
    }
}
