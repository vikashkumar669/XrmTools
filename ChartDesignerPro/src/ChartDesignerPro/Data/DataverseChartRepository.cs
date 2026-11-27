using System;
using System.Collections.Generic;
using System.ServiceModel;
using System.Threading;
using ChartDesignerPro.Abstractions;
using ChartDesignerPro.Models;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace ChartDesignerPro.Data
{
    /// <summary>
    /// Reads system charts (savedqueryvisualization) and personal charts (userqueryvisualization).
    /// Lists are retrieved without the two XML columns and paged by 5,000; the XML is retrieved when a chart is opened.
    /// Writes are limited to <see cref="Update"/> (the two XML columns of one chart) and <see cref="PublishTable"/>
    /// (one table). There are no Create, Delete, Assign or PublishAll calls in this class.
    /// </summary>
    public sealed class DataverseChartRepository : IChartRepository
    {
        public const string SystemTable = "savedqueryvisualization";
        public const string PersonalTable = "userqueryvisualization";
        private const int PageSize = 5000;

        private static readonly string[] SystemListColumns =
        {
            "name", "description", "primaryentitytypecode", "charttype", "webresourceid", "isdefault",
            "ismanaged", "iscustomizable", "modifiedon", "modifiedby"
        };

        private static readonly string[] PersonalListColumns =
        {
            "name", "description", "primaryentitytypecode", "charttype", "webresourceid", "isdefault",
            "modifiedon", "modifiedby", "ownerid"
        };

        private const string DataColumn = "datadescription";
        private const string PresentationColumn = "presentationdescription";

        private readonly IOrganizationService _service;
        private readonly ILog _log;

        public DataverseChartRepository(IOrganizationService service, ILog log)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _log = log;
        }

        public ChartListResult ListCharts(ChartQuery query, CancellationToken ct, IProgress<string> progress)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            var result = new ChartListResult();

            if (query.IncludeSystem)
            {
                // System charts are readable by most roles; a failure here is a real error and is not swallowed.
                Page(SystemTable, SystemListColumns, query.TableLogicalName, ChartScope.System, result.Charts, ct, progress);
            }

            if (query.IncludePersonal && !ct.IsCancellationRequested)
            {
                // Personal charts can fail on their own (privileges). Keep the system charts and report a warning.
                try
                {
                    Page(PersonalTable, PersonalListColumns, query.TableLogicalName, ChartScope.Personal, result.Charts, ct, progress);
                }
                catch (FaultException<OrganizationServiceFault> ex)
                {
                    var message = "Personal charts couldn't be read: " + (ex.Detail?.Message ?? ex.Message);
                    result.Warnings.Add(message);
                    _log?.Warn("{0}", message);
                }
            }

            return result;
        }

        private void Page(string table, string[] columns, string tableFilter, ChartScope scope,
            List<ChartRecord> into, CancellationToken ct, IProgress<string> progress)
        {
            var query = new QueryExpression(table)
            {
                ColumnSet = new ColumnSet(columns),
                PageInfo = new PagingInfo { Count = PageSize, PageNumber = 1 },
                NoLock = true
            };
            query.AddOrder("name", OrderType.Ascending);
            if (!string.IsNullOrEmpty(tableFilter))
            {
                // primaryentitytypecode is an EntityName column: it is compared by table logical name.
                query.Criteria.AddCondition("primaryentitytypecode", ConditionOperator.Equal, tableFilter);
            }

            var label = scope == ChartScope.System ? "system" : "personal";
            var count = 0;
            while (!ct.IsCancellationRequested)
            {
                var page = _service.RetrieveMultiple(query);
                foreach (var entity in page.Entities) into.Add(Map(entity, scope, withXml: false));
                count += page.Entities.Count;
                progress?.Report($"Loaded {count:N0} {label} charts...");

                if (!page.MoreRecords) break;
                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = page.PagingCookie;
            }
        }

        public ChartRecord Retrieve(ChartScope scope, Guid id)
        {
            var table = scope == ChartScope.System ? SystemTable : PersonalTable;
            var baseColumns = scope == ChartScope.System ? SystemListColumns : PersonalListColumns;
            var columns = new List<string>(baseColumns) { DataColumn, PresentationColumn };

            // RetrieveMultiple by id instead of Retrieve, so a deleted chart returns null instead of throwing.
            var query = new QueryExpression(table) { ColumnSet = new ColumnSet(columns.ToArray()), TopCount = 1 };
            query.Criteria.AddCondition(table + "id", ConditionOperator.Equal, id);
            var rows = _service.RetrieveMultiple(query).Entities;
            return rows.Count == 0 ? null : Map(rows[0], scope, withXml: true);
        }

        public void Update(ChartScope scope, Guid id, string dataXml, string presentationXml)
        {
            if (dataXml == null && presentationXml == null) return;
            var entity = new Entity(scope == ChartScope.System ? SystemTable : PersonalTable, id);
            if (dataXml != null) entity[DataColumn] = dataXml;
            if (presentationXml != null) entity[PresentationColumn] = presentationXml;
            _service.Update(entity);
        }

        public void PublishTable(string tableLogicalName)
        {
            if (string.IsNullOrWhiteSpace(tableLogicalName)) throw new ArgumentNullException(nameof(tableLogicalName));
            // Scoped publish: only this table's customizations, never PublishAllXmlRequest.
            var xml = new System.Xml.Linq.XElement("importexportxml",
                new System.Xml.Linq.XElement("entities", new System.Xml.Linq.XElement("entity", tableLogicalName)));
            _service.Execute(new PublishXmlRequest { ParameterXml = xml.ToString(System.Xml.Linq.SaveOptions.DisableFormatting) });
        }

        private static ChartRecord Map(Entity e, ChartScope scope, bool withXml)
        {
            var record = new ChartRecord
            {
                Id = e.Id,
                Scope = scope,
                Name = e.GetAttributeValue<string>("name"),
                Description = e.GetAttributeValue<string>("description"),
                TableLogicalName = ReadTableName(e),
                Engine = ReadEngine(e),
                WebResourceId = e.GetAttributeValue<EntityReference>("webresourceid")?.Id,
                IsDefault = e.GetAttributeValue<bool?>("isdefault") ?? false,
                ModifiedOnUtc = e.Contains("modifiedon") ? Utc(e.GetAttributeValue<DateTime>("modifiedon")) : (DateTime?)null,
                ModifiedByName = e.GetAttributeValue<EntityReference>("modifiedby")?.Name,
            };

            if (scope == ChartScope.System)
            {
                record.IsManaged = e.GetAttributeValue<bool?>("ismanaged");
                record.IsCustomizable = e.GetAttributeValue<BooleanManagedProperty>("iscustomizable")?.Value;
            }
            else
            {
                record.OwnerName = e.GetAttributeValue<EntityReference>("ownerid")?.Name;
            }

            if (withXml)
            {
                record.DataXml = e.GetAttributeValue<string>(DataColumn) ?? string.Empty;
                record.PresentationXml = e.GetAttributeValue<string>(PresentationColumn) ?? string.Empty;
                record.HasXml = true;
            }
            return record;
        }

        /// <summary>primaryentitytypecode comes back as a string logical name; older payloads may carry an int type code.</summary>
        private static string ReadTableName(Entity e)
        {
            if (!e.Attributes.TryGetValue("primaryentitytypecode", out var value) || value == null) return null;
            return value as string ?? value.ToString();
        }

        private static ChartEngine ReadEngine(Entity e)
        {
            var value = e.GetAttributeValue<OptionSetValue>("charttype");
            if (value == null) return ChartEngine.AspNetCharts; // the column defaults to 0 (ASP.NET Charts)
            return Enum.IsDefined(typeof(ChartEngine), value.Value) ? (ChartEngine)value.Value : ChartEngine.Unknown;
        }

        private static DateTime Utc(DateTime value) =>
            value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
