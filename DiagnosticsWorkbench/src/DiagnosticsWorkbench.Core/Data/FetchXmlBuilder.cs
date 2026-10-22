using System;
using System.Globalization;
using System.Security;
using System.Text;
using DiagnosticsWorkbench.Analysis;

namespace DiagnosticsWorkbench.Data
{
    /// <summary>FetchXML for plugintracelog (kept free of the Dataverse SDK so it can be unit-tested).</summary>
    public static class FetchXmlBuilder
    {
        public const int PageSize = 5000;

        public static readonly string[] Columns =
        {
            "plugintracelogid", "createdon", "typename", "messagename", "primaryentity", "exceptiondetails", "messageblock",
            "performanceexecutionduration", "performanceconstructorduration", "performanceexecutionstarttime",
            "correlationid", "requestid", "depth", "mode", "operationtype", "pluginstepid", "createdby"
        };

        public static string TraceLogs(QueryDescription query, int page, string pagingCookie, int count)
        {
            var sb = new StringBuilder();
            sb.Append("<fetch count=\"").Append(Math.Max(1, Math.Min(PageSize, count)).ToString(CultureInfo.InvariantCulture))
              .Append("\" page=\"").Append(page.ToString(CultureInfo.InvariantCulture)).Append('"');
            if (!string.IsNullOrEmpty(pagingCookie)) sb.Append(" paging-cookie=\"").Append(SecurityElement.Escape(pagingCookie)).Append('"');
            sb.Append(" no-lock=\"true\"><entity name=\"plugintracelog\">");
            foreach (var c in Columns) sb.Append("<attribute name=\"").Append(c).Append("\" />");
            sb.Append("<order attribute=\"createdon\" descending=\"true\" /><filter type=\"and\">");
            sb.Append("<condition attribute=\"createdon\" operator=\"ge\" value=\"").Append(Iso(query.FromUtc)).Append("\" />");
            sb.Append("<condition attribute=\"createdon\" operator=\"le\" value=\"").Append(Iso(query.ToUtc)).Append("\" />");
            if (!string.IsNullOrWhiteSpace(query.Table))
                sb.Append("<condition attribute=\"primaryentity\" operator=\"eq\" value=\"").Append(SecurityElement.Escape(query.Table.Trim())).Append("\" />");
            if (!string.IsNullOrWhiteSpace(query.Message))
                sb.Append("<condition attribute=\"messagename\" operator=\"eq\" value=\"").Append(SecurityElement.Escape(query.Message.Trim())).Append("\" />");
            if (!string.IsNullOrWhiteSpace(query.PluginType))
                sb.Append("<condition attribute=\"typename\" operator=\"like\" value=\"%").Append(SecurityElement.Escape(EscapeLike(query.PluginType.Trim()))).Append("%\" />");
            sb.Append("</filter></entity></fetch>");
            return sb.ToString();
        }

        private static string Iso(DateTime utc) =>
            DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        /// <summary>FetchXML like: [ ] _ and % are wildcards.</summary>
        private static string EscapeLike(string value) => value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    }
}
