using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Data
{
    /// <summary>Progress reported during retrieval.</summary>
    public sealed class RetrievalProgress
    {
        public string Stage { get; set; }
        public int Count { get; set; }
    }

    /// <summary>Records retrieved for one query, plus notes about how they were retrieved.</summary>
    public sealed class RetrievalResult
    {
        public List<PluginTraceRecord> Records { get; } = new List<PluginTraceRecord>();
        public List<string> Notes { get; } = new List<string>();
        public bool Cancelled { get; set; }
    }

    /// <summary>
    /// Read-only access to plugintracelog and the related registration tables through the XrmToolBox connection.
    /// Never creates, updates or deletes anything.
    /// </summary>
    public sealed class DataverseTraceService
    {
        private const int StepCacheLimit = 5000;

        private readonly IOrganizationService _service;
        private readonly Func<bool> _isCancelled;
        private readonly Action<RetrievalProgress> _progress;
        private readonly Dictionary<Guid, StepRegistration> _stepCache;

        public DataverseTraceService(IOrganizationService service, Func<bool> isCancelled = null, Action<RetrievalProgress> progress = null,
            Dictionary<Guid, StepRegistration> stepCache = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _isCancelled = isCancelled ?? (() => false);
            _progress = progress;
            _stepCache = stepCache ?? new Dictionary<Guid, StepRegistration>();
        }

        // ============================================================ organization

        /// <summary>Organization name, version and plug-in trace setting. Each part is optional: failures leave it empty.</summary>
        public OrganizationInfo ReadOrganization(OrganizationInfo known)
        {
            var info = known ?? new OrganizationInfo();
            try
            {
                var q = new QueryExpression("organization") { ColumnSet = new ColumnSet("name", "plugintracelogsetting"), TopCount = 1, NoLock = true };
                var org = Call(() => _service.RetrieveMultiple(q)).Entities.FirstOrDefault();
                if (org != null)
                {
                    info.OrganizationName = info.OrganizationName ?? org.GetAttributeValue<string>("name");
                    var setting = org.GetAttributeValue<OptionSetValue>("plugintracelogsetting")?.Value;
                    info.TraceSetting = setting == 0 ? "Off" : setting == 1 ? "Exception" : setting == 2 ? "All" : null;
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                info.TraceSetting = null;   // unknown; the dashboard says so
            }

            if (string.IsNullOrEmpty(info.OrganizationVersion))
            {
                try { info.OrganizationVersion = ((RetrieveVersionResponse)Call(() => _service.Execute(new RetrieveVersionRequest()))).Version; }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { /* optional */ }
            }
            return info;
        }

        // ============================================================ traces

        /// <summary>
        /// Retrieves summary rows (newest first, paged, up to the result limit), then the exception text of failed rows,
        /// then step registrations. Large trace text (message block) is not retrieved here.
        /// </summary>
        public RetrievalResult Retrieve(TraceQuery query, bool resolveSteps)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            var result = new RetrievalResult();
            var limit = Math.Max(1, Math.Min(TraceQuery.MaxResultLimit, query.ResultLimit));
            query.LimitReached = false;

            // 1. Summary pages
            var page = 1;
            string cookie = null;
            while (true)
            {
                if (_isCancelled()) { result.Cancelled = true; break; }
                var count = Math.Min(Data.TraceFetchXml.MaxPageSize, limit - result.Records.Count);
                var fetch = Data.TraceFetchXml.Summary(query, page, cookie, count);
                var collection = Call(() => _service.RetrieveMultiple(new FetchExpression(fetch)));
                foreach (var e in collection.Entities) result.Records.Add(MapSummary(e, query.Exceptions));
                Report("Retrieving traces", result.Records.Count);

                if (result.Records.Count >= limit)
                {
                    query.LimitReached = collection.MoreRecords;
                    break;
                }
                if (!collection.MoreRecords) break;
                cookie = collection.PagingCookie;
                page++;
            }

            if (result.Cancelled)
            {
                query.LimitReached = true;   // partial: counts are lower bounds
                result.Notes.Add($"Retrieval was cancelled after {result.Records.Count:N0} traces; the analysis covers only those.");
            }
            if (query.LimitReached && !result.Cancelled)
                result.Notes.Add($"The result limit of {limit:N0} was reached; older traces in the date range were not retrieved.");
            if (result.Records.Count == 0 || result.Cancelled) return result;

            // 2. Exception text for failed rows
            if (query.Exceptions != ExceptionFilter.WithoutException) LoadExceptions(query, result);

            // 3. Step registrations
            if (resolveSteps && !_isCancelled()) ResolveSteps(result.Records, result.Notes);
            return result;
        }

        private void LoadExceptions(TraceQuery query, RetrievalResult result)
        {
            var byId = result.Records.ToDictionary(r => r.Id);
            var oldest = result.Records.Min(r => r.CreatedOnUtc);
            try
            {
                var page = 1;
                string cookie = null;
                var loaded = 0;
                var cap = result.Records.Count + 500;   // margin for rows written in the same second as the oldest one
                while (!_isCancelled())
                {
                    var fetch = Data.TraceFetchXml.Exceptions(query, oldest, page, cookie, Math.Min(Data.TraceFetchXml.MaxPageSize, cap - loaded));
                    var collection = Call(() => _service.RetrieveMultiple(new FetchExpression(fetch)));
                    foreach (var e in collection.Entities)
                    {
                        loaded++;
                        if (byId.TryGetValue(e.Id, out var record)) record.ExceptionDetails = e.GetAttributeValue<string>("exceptiondetails");
                    }
                    Report("Loading exception details", loaded);
                    if (!collection.MoreRecords || loaded >= cap) break;
                    cookie = collection.PagingCookie;
                    page++;
                }
                if (_isCancelled()) { result.Cancelled = true; result.Notes.Add("Loading exception details was cancelled; failure grouping is incomplete."); return; }
                foreach (var r in result.Records) r.ExceptionLoaded = true;
            }
            catch (FaultException<OrganizationServiceFault>)
            {
                // Fallback: some environments may reject the not-null condition on the memo column. Load by ID instead.
                result.Notes.Add("The server-side \"has exception\" filter was not accepted, so exception details were loaded by trace ID instead (slower).");
                var done = 0;
                foreach (var batch in Data.TraceFetchXml.Batches(byId.Keys))
                {
                    if (_isCancelled()) { result.Cancelled = true; return; }
                    var collection = Call(() => _service.RetrieveMultiple(new FetchExpression(Data.TraceFetchXml.ExceptionsByIds(batch))));
                    foreach (var e in collection.Entities)
                        if (byId.TryGetValue(e.Id, out var record)) record.ExceptionDetails = e.GetAttributeValue<string>("exceptiondetails");
                    foreach (var id in batch) byId[id].ExceptionLoaded = true;
                    done += batch.Count;
                    Report("Loading exception details", done);
                }
            }
        }

        /// <summary>Loads the trace text and exception for one trace (detail view).</summary>
        public void LoadDetail(PluginTraceRecord record)
        {
            if (record == null || (record.MessageBlockLoaded && record.ExceptionLoaded)) return;
            var collection = Call(() => _service.RetrieveMultiple(new FetchExpression(Data.TraceFetchXml.Detail(record.Id))));
            var e = collection.Entities.FirstOrDefault();
            if (e == null) throw new InvalidOperationException("The trace no longer exists. It may have been deleted by the retention job.");
            record.MessageBlock = e.GetAttributeValue<string>("messageblock");
            record.MessageBlockLoaded = true;
            record.ExceptionDetails = e.GetAttributeValue<string>("exceptiondetails");
            record.ExceptionLoaded = true;
        }

        /// <summary>Resolves step, plug-in type and assembly for the records' step IDs (cached, batched).</summary>
        public void ResolveSteps(IEnumerable<PluginTraceRecord> records, List<string> notes)
        {
            var list = records.Where(r => r.PluginStepId.HasValue).ToList();
            var missing = list.Select(r => r.PluginStepId.Value).Distinct().Where(id => !_stepCache.ContainsKey(id)).ToList();
            try
            {
                var done = 0;
                foreach (var batch in Data.TraceFetchXml.Batches(missing))
                {
                    if (_isCancelled()) return;
                    var collection = Call(() => _service.RetrieveMultiple(new FetchExpression(Data.TraceFetchXml.Steps(batch))));
                    foreach (var e in collection.Entities) Cache(MapStep(e));
                    foreach (var id in batch.Where(id => !_stepCache.ContainsKey(id))) Cache(new StepRegistration { StepId = id, StepName = "(step not found: deleted or not visible)" });
                    done += batch.Count;
                    Report("Resolving plug-in steps", done);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                notes?.Add("Plug-in step and assembly details couldn't be read (" + DataverseErrors.Describe(ex) + "). Type names from the traces are used instead.");
            }
            foreach (var r in list)
                if (_stepCache.TryGetValue(r.PluginStepId.Value, out var step)) r.Step = step;
        }

        private void Cache(StepRegistration step)
        {
            if (_stepCache.Count >= StepCacheLimit) _stepCache.Clear();   // bounded: never grows without limit
            _stepCache[step.StepId] = step;
        }

        // ============================================================ mapping

        private static PluginTraceRecord MapSummary(Entity e, ExceptionFilter filter)
        {
            return new PluginTraceRecord
            {
                Id = e.Id,
                CreatedOnUtc = Utc(e.GetAttributeValue<DateTime>("createdon")),
                TypeName = e.GetAttributeValue<string>("typename"),
                MessageName = e.GetAttributeValue<string>("messagename"),
                PrimaryEntity = e.GetAttributeValue<string>("primaryentity"),
                ExecutionDurationMs = e.GetAttributeValue<int?>("performanceexecutionduration"),
                ConstructorDurationMs = e.GetAttributeValue<int?>("performanceconstructorduration"),
                ExecutionStartUtc = e.Contains("performanceexecutionstarttime") && e["performanceexecutionstarttime"] is DateTime start ? Utc(start) : (DateTime?)null,
                CorrelationId = NullIfEmpty(e.GetAttributeValue<Guid?>("correlationid")),
                RequestId = NullIfEmpty(e.GetAttributeValue<Guid?>("requestid")),
                Depth = e.GetAttributeValue<int?>("depth"),
                Mode = e.GetAttributeValue<OptionSetValue>("mode")?.Value,
                OperationType = e.GetAttributeValue<OptionSetValue>("operationtype")?.Value,
                PluginStepId = NullIfEmpty(e.GetAttributeValue<Guid?>("pluginstepid")),
                // With the "without exception" filter the server guarantees there is none
                ExceptionLoaded = filter == ExceptionFilter.WithoutException,
            };
        }

        private static StepRegistration MapStep(Entity e)
        {
            return new StepRegistration
            {
                StepId = e.Id,
                StepName = e.GetAttributeValue<string>("name"),
                Stage = e.GetAttributeValue<OptionSetValue>("stage")?.Value,
                Mode = e.GetAttributeValue<OptionSetValue>("mode")?.Value,
                Rank = e.GetAttributeValue<int?>("rank"),
                State = e.GetAttributeValue<OptionSetValue>("statecode")?.Value,
                PluginTypeName = Aliased<string>(e, "type.typename") ?? e.GetAttributeValue<EntityReference>("plugintypeid")?.Name,
                AssemblyName = Aliased<string>(e, "asm.name") ?? Aliased<string>(e, "type.assemblyname"),
                AssemblyVersion = Aliased<string>(e, "asm.version"),
            };
        }

        private T Call<T>(Func<T> call) => DataverseRetry.Execute(call, _isCancelled, message => _progress?.Invoke(new RetrievalProgress { Stage = message }));

        private void Report(string stage, int count) => _progress?.Invoke(new RetrievalProgress { Stage = stage, Count = count });

        private static T Aliased<T>(Entity e, string name) where T : class =>
            e.Contains(name) ? e.GetAttributeValue<AliasedValue>(name)?.Value as T : null;

        private static Guid? NullIfEmpty(Guid? g) => g.HasValue && g.Value != Guid.Empty ? g : null;

        private static DateTime Utc(DateTime value) =>
            value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
