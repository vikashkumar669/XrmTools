using System;
using System.Collections.Generic;
using DiagnosticsWorkbench.Analysis;
using DiagnosticsWorkbench.Models;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DiagnosticsWorkbench.Data
{
    /// <summary>Read-only access to plugintracelog and the organization trace setting.</summary>
    public sealed class TraceLogRepository
    {
        public const int PageSize = FetchXmlBuilder.PageSize;

        private readonly IOrganizationService _service;

        public TraceLogRepository(IOrganizationService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        /// <summary>organization.plugintracelogsetting: 0 Off, 1 Exception, 2 All.</summary>
        public TraceSetting GetTraceSetting()
        {
            var query = new QueryExpression("organization") { ColumnSet = new ColumnSet("plugintracelogsetting"), TopCount = 1 };
            var org = _service.RetrieveMultiple(query).Entities;
            if (org.Count == 0) return TraceSetting.Unknown;
            var value = org[0].GetAttributeValue<OptionSetValue>("plugintracelogsetting");
            if (value == null) return TraceSetting.Unknown;
            return Enum.IsDefined(typeof(TraceSetting), value.Value) ? (TraceSetting)value.Value : TraceSetting.Unknown;
        }

        /// <summary>
        /// Newest first, paged by 5,000. Stops at the row cap. <paramref name="progress"/> receives the running count;
        /// returning false from <paramref name="keepGoing"/> cancels between pages.
        /// </summary>
        public List<TraceLog> Load(QueryDescription query, Action<int> progress = null, Func<bool> keepGoing = null)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            var result = new List<TraceLog>();
            var page = 1;
            string cookie = null;
            var cap = query.RowCap > 0 ? query.RowCap : WorkbenchSettings.DefaultRowCap;

            while (true)
            {
                if (keepGoing != null && !keepGoing()) break;
                var fetch = FetchXmlBuilder.TraceLogs(query, page, cookie, Math.Min(PageSize, cap - result.Count));
                var collection = _service.RetrieveMultiple(new FetchExpression(fetch));
                // The page size never exceeds the rows left under the cap, so a full page plus MoreRecords means rows were left behind.
                foreach (var entity in collection.Entities) result.Add(Map(entity));
                progress?.Invoke(result.Count);

                if (result.Count >= cap)
                {
                    query.CapReached = collection.MoreRecords;
                    break;
                }
                if (!collection.MoreRecords) break;
                cookie = collection.PagingCookie;
                page++;
            }
            return result;
        }

        private static TraceLog Map(Entity e)
        {
            return new TraceLog
            {
                Id = e.Id,
                CreatedOn = Utc(e.GetAttributeValue<DateTime>("createdon")),
                TypeName = e.GetAttributeValue<string>("typename"),
                MessageName = e.GetAttributeValue<string>("messagename"),
                PrimaryEntity = e.GetAttributeValue<string>("primaryentity"),
                ExceptionDetails = e.GetAttributeValue<string>("exceptiondetails"),
                MessageBlock = e.GetAttributeValue<string>("messageblock"),
                ExecutionDurationMs = e.GetAttributeValue<int?>("performanceexecutionduration"),
                ConstructorDurationMs = e.GetAttributeValue<int?>("performanceconstructorduration"),
                ExecutionStartTime = e.Contains("performanceexecutionstarttime") ? Utc(e.GetAttributeValue<DateTime>("performanceexecutionstarttime")) : (DateTime?)null,
                CorrelationId = NullIfEmpty(e.GetAttributeValue<Guid?>("correlationid")),
                RequestId = NullIfEmpty(e.GetAttributeValue<Guid?>("requestid")),
                Depth = e.GetAttributeValue<int?>("depth") ?? 0,
                Mode = e.GetAttributeValue<OptionSetValue>("mode")?.Value ?? 0,
                OperationType = e.GetAttributeValue<OptionSetValue>("operationtype")?.Value ?? 1,
                StepId = NullIfEmpty(e.GetAttributeValue<Guid?>("pluginstepid")),
                CreatedByName = e.GetAttributeValue<EntityReference>("createdby")?.Name,
            };
        }

        private static Guid? NullIfEmpty(Guid? g) => g.HasValue && g.Value != Guid.Empty ? g : null;

        private static DateTime Utc(DateTime value) =>
            value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
