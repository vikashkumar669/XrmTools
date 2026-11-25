using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ChartDesignerPro.Abstractions;
using ChartDesignerPro.Models;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace ChartDesignerPro.Data
{
    /// <summary>
    /// Table and column metadata from Dataverse. No caching here: wrap it in <see cref="Explorer.MetadataCache"/>.
    /// </summary>
    public sealed class DataverseMetadataService : IMetadataProvider
    {
        private readonly IOrganizationService _service;

        public DataverseMetadataService(IOrganizationService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public IReadOnlyList<TableSummary> ListTables(CancellationToken ct)
        {
            var response = (RetrieveAllEntitiesResponse)_service.Execute(new RetrieveAllEntitiesRequest
            {
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = false
            });
            return response.EntityMetadata
                .Where(e => e.IsIntersect != true)
                .Select(e => new TableSummary
                {
                    LogicalName = e.LogicalName,
                    DisplayName = Label(e.DisplayName) ?? e.LogicalName,
                    ObjectTypeCode = e.ObjectTypeCode
                })
                .OrderBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public TableMetadata GetTable(string logicalName)
        {
            if (string.IsNullOrEmpty(logicalName)) throw new ArgumentNullException(nameof(logicalName));

            RetrieveEntityResponse response;
            try
            {
                response = (RetrieveEntityResponse)_service.Execute(new RetrieveEntityRequest
                {
                    LogicalName = logicalName,
                    EntityFilters = EntityFilters.Attributes,
                    RetrieveAsIfPublished = false
                });
            }
            catch (System.ServiceModel.FaultException<OrganizationServiceFault> ex) when (IsNotFound(ex))
            {
                return null;
            }

            var md = response.EntityMetadata;
            var columns = new Dictionary<string, ColumnMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in md.Attributes ?? new AttributeMetadata[0])
            {
                columns[a.LogicalName] = new ColumnMetadata
                {
                    LogicalName = a.LogicalName,
                    DisplayName = Label(a.DisplayName) ?? a.LogicalName,
                    Kind = KindOf(a),
                    IsValidForRead = a.IsValidForRead,
                    IsLogical = a.IsLogical,
                    AttributeOf = a.AttributeOf
                };
            }

            return new TableMetadata
            {
                LogicalName = md.LogicalName,
                DisplayName = Label(md.DisplayName) ?? md.LogicalName,
                DisplayCollectionName = Label(md.DisplayCollectionName),
                ObjectTypeCode = md.ObjectTypeCode,
                PrimaryIdAttribute = md.PrimaryIdAttribute,
                PrimaryNameAttribute = md.PrimaryNameAttribute,
                Columns = columns
            };
        }

        private static bool IsNotFound(System.ServiceModel.FaultException<OrganizationServiceFault> ex)
        {
            var message = ex.Detail?.Message ?? ex.Message ?? string.Empty;
            return message.IndexOf("Could not find", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ColumnKind KindOf(AttributeMetadata a)
        {
            if (a.AttributeTypeName == AttributeTypeDisplayName.MultiSelectPicklistType) return ColumnKind.MultiChoice;
            switch (a.AttributeType)
            {
                case AttributeTypeCode.String: return ColumnKind.Text;
                case AttributeTypeCode.Memo: return ColumnKind.Memo;
                case AttributeTypeCode.Integer: return ColumnKind.WholeNumber;
                case AttributeTypeCode.BigInt: return ColumnKind.BigInt;
                case AttributeTypeCode.Decimal: return ColumnKind.Decimal;
                case AttributeTypeCode.Double: return ColumnKind.Float;
                case AttributeTypeCode.Money: return ColumnKind.Money;
                case AttributeTypeCode.DateTime: return ColumnKind.DateTime;
                case AttributeTypeCode.Picklist: return ColumnKind.Choice;
                case AttributeTypeCode.Boolean: return ColumnKind.TwoOptions;
                case AttributeTypeCode.State: return ColumnKind.State;
                case AttributeTypeCode.Status: return ColumnKind.Status;
                case AttributeTypeCode.Lookup: return ColumnKind.Lookup;
                case AttributeTypeCode.Customer: return ColumnKind.Customer;
                case AttributeTypeCode.Owner: return ColumnKind.Owner;
                case AttributeTypeCode.Uniqueidentifier: return ColumnKind.UniqueIdentifier;
                default: return ColumnKind.Other;
            }
        }

        private static string Label(Label label)
        {
            var text = label?.UserLocalizedLabel?.Label;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }
}
