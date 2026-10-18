using System;
using System.Collections.Generic;
using System.Linq;
using DataQualityStudio.Models;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataQualityStudio.Services
{
    /// <summary>Reads and caches table and column metadata from Dataverse.</summary>
    public sealed class MetadataService
    {
        private readonly IOrganizationService _service;
        private readonly object _lock = new object();
        private List<DvEntity> _entities;
        private readonly Dictionary<string, DvEntity> _byName = new Dictionary<string, DvEntity>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Columns that are required by the platform but filled in automatically when a record is created.</summary>
        private static readonly HashSet<string> AutoFilled = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ownerid", "owninguser", "owningteam", "owningbusinessunit", "statecode", "statuscode", "transactioncurrencyid",
            "createdby", "createdon", "modifiedby", "modifiedon", "createdonbehalfby", "modifiedonbehalfby", "overriddencreatedon",
            "exchangerate", "importsequencenumber", "timezoneruleversionnumber", "utcconversiontimezonecode", "versionnumber",
            "processid", "stageid", "traversedpath"
        };

        public MetadataService(IOrganizationService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public List<DvEntity> GetEntities()
        {
            lock (_lock)
            {
                if (_entities != null) return _entities;
                var response = (RetrieveAllEntitiesResponse)_service.Execute(new RetrieveAllEntitiesRequest
                {
                    EntityFilters = EntityFilters.Entity,
                    RetrieveAsIfPublished = false
                });
                _entities = response.EntityMetadata
                    .Where(e => e.IsIntersect != true && !string.IsNullOrEmpty(e.PrimaryIdAttribute))
                    .Select(e => new DvEntity
                    {
                        LogicalName = e.LogicalName,
                        DisplayName = Label(e.DisplayName) ?? e.LogicalName,
                        PrimaryIdAttribute = e.PrimaryIdAttribute,
                        PrimaryNameAttribute = e.PrimaryNameAttribute
                    })
                    .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(e => e.LogicalName)
                    .ToList();
                foreach (var e in _entities) _byName[e.LogicalName] = e;
                return _entities;
            }
        }

        /// <summary>Basic info (no columns) from the cached table list.</summary>
        public DvEntity FindEntity(string logicalName)
        {
            if (string.IsNullOrEmpty(logicalName)) return null;
            GetEntities();
            lock (_lock)
            {
                _byName.TryGetValue(logicalName, out var e);
                return e;
            }
        }

        /// <summary>Table with its columns (cached after the first call).</summary>
        public DvEntity GetEntityWithFields(string logicalName)
        {
            var entity = FindEntity(logicalName) ?? new DvEntity { LogicalName = logicalName, DisplayName = logicalName };
            lock (_lock)
            {
                if (entity.HasFields) return entity;
            }

            var response = (RetrieveEntityResponse)_service.Execute(new RetrieveEntityRequest
            {
                LogicalName = logicalName,
                EntityFilters = EntityFilters.Attributes,
                RetrieveAsIfPublished = false
            });
            var md = response.EntityMetadata;

            var all = new Dictionary<string, DvField>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in md.Attributes)
            {
                var f = ToField(a);
                if (f != null) all[f.LogicalName] = f;
            }

            var importable = md.Attributes
                .Where(a => a.AttributeOf == null && (a.IsValidForCreate == true || a.IsValidForUpdate == true))
                .Select(a => all.TryGetValue(a.LogicalName, out var f) ? f : null)
                .Where(f => f != null && f.Kind != FieldKind.Other && (f.Kind != FieldKind.UniqueId || f.IsPrimaryId))
                .OrderBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(f => f.LogicalName)
                .ToList();

            lock (_lock)
            {
                entity.PrimaryIdAttribute = md.PrimaryIdAttribute ?? entity.PrimaryIdAttribute;
                entity.PrimaryNameAttribute = md.PrimaryNameAttribute ?? entity.PrimaryNameAttribute;
                if (string.IsNullOrEmpty(entity.DisplayName) || entity.DisplayName == logicalName)
                    entity.DisplayName = Label(md.DisplayName) ?? logicalName;
                entity.AllAttributes = all;
                entity.Fields = importable;
                _byName[logicalName] = entity;
            }
            return entity;
        }

        private static DvField ToField(AttributeMetadata a)
        {
            if (string.IsNullOrEmpty(a.LogicalName)) return null;
            var f = new DvField
            {
                LogicalName = a.LogicalName,
                SchemaName = a.SchemaName,
                DisplayName = Label(a.DisplayName) ?? a.LogicalName,
                IsValidForCreate = a.IsValidForCreate == true,
                IsValidForUpdate = a.IsValidForUpdate == true,
                IsPrimaryId = a.IsPrimaryId == true,
                IsPrimaryName = a.IsPrimaryName == true,
                RequiredLevel = a.RequiredLevel?.Value.ToString() ?? "None"
            };

            switch (a)
            {
                case StringAttributeMetadata s:
                    f.Kind = FieldKind.Text;
                    f.MaxLength = s.MaxLength;
                    f.Format = s.FormatName?.Value ?? s.Format?.ToString();
                    f.TypeLabel = string.IsNullOrEmpty(f.Format) || f.Format == "Text" ? "Text" : "Text (" + f.Format + ")";
                    break;
                case MemoAttributeMetadata m:
                    f.Kind = FieldKind.Memo;
                    f.MaxLength = m.MaxLength;
                    f.TypeLabel = "Multiline text";
                    break;
                case IntegerAttributeMetadata i:
                    f.Kind = FieldKind.WholeNumber;
                    f.MinValue = i.MinValue;
                    f.MaxValue = i.MaxValue;
                    f.TypeLabel = "Whole number";
                    break;
                case BigIntAttributeMetadata b:
                    f.Kind = FieldKind.BigInt;
                    f.MinValue = b.MinValue;
                    f.MaxValue = b.MaxValue;
                    f.TypeLabel = "Big integer";
                    break;
                case DecimalAttributeMetadata d:
                    f.Kind = FieldKind.Decimal;
                    f.MinValue = d.MinValue;
                    f.MaxValue = d.MaxValue;
                    f.Precision = d.Precision;
                    f.TypeLabel = "Decimal";
                    break;
                case DoubleAttributeMetadata db:
                    f.Kind = FieldKind.Double;
                    f.MinValue = ToDecimal(db.MinValue);
                    f.MaxValue = ToDecimal(db.MaxValue);
                    f.Precision = db.Precision;
                    f.TypeLabel = "Float";
                    break;
                case MoneyAttributeMetadata mo:
                    f.Kind = FieldKind.Money;
                    f.MinValue = ToDecimal(mo.MinValue);
                    f.MaxValue = ToDecimal(mo.MaxValue);
                    // PrecisionSource 0 = Precision property; 1 = organization pricing precision; 2 = currency precision (max 4)
                    f.Precision = mo.PrecisionSource == 0 ? mo.Precision : 4;
                    f.TypeLabel = "Currency";
                    break;
                case BooleanAttributeMetadata bo:
                    f.Kind = FieldKind.Boolean;
                    f.TrueLabel = Label(bo.OptionSet?.TrueOption?.Label) ?? "Yes";
                    f.FalseLabel = Label(bo.OptionSet?.FalseOption?.Label) ?? "No";
                    f.TypeLabel = "Yes/No";
                    break;
                case DateTimeAttributeMetadata dt:
                    f.Kind = FieldKind.DateTime;
                    f.DateOnly = dt.Format == DateTimeFormat.DateOnly;
                    f.TypeLabel = f.DateOnly ? "Date only" : "Date and time";
                    break;
                case StatusAttributeMetadata st:
                    f.Kind = FieldKind.Status;
                    AddOptions(f, st.OptionSet);
                    f.TypeLabel = "Status reason";
                    break;
                case StateAttributeMetadata sa:
                    f.Kind = FieldKind.State;
                    AddOptions(f, sa.OptionSet);
                    f.TypeLabel = "Status";
                    break;
                case MultiSelectPicklistAttributeMetadata ms:
                    f.Kind = FieldKind.MultiChoice;
                    AddOptions(f, ms.OptionSet);
                    f.TypeLabel = "Choices (multi)";
                    break;
                case PicklistAttributeMetadata p:
                    f.Kind = FieldKind.Choice;
                    AddOptions(f, p.OptionSet);
                    f.TypeLabel = "Choice";
                    break;
                case LookupAttributeMetadata l:
                    f.Targets = l.Targets ?? new string[0];
                    f.Kind = a.AttributeType == AttributeTypeCode.Customer ? FieldKind.Customer
                           : a.AttributeType == AttributeTypeCode.Owner ? FieldKind.Owner
                           : FieldKind.Lookup;
                    if (a.AttributeType == AttributeTypeCode.PartyList) f.Kind = FieldKind.Other;
                    f.TypeLabel = (f.Kind == FieldKind.Customer ? "Customer" : f.Kind == FieldKind.Owner ? "Owner" : "Lookup") +
                                  (f.Targets.Length > 0 ? " → " + string.Join(", ", f.Targets) : "");
                    break;
                default:
                    if (a.AttributeType == AttributeTypeCode.Uniqueidentifier)
                    {
                        f.Kind = FieldKind.UniqueId;
                        f.TypeLabel = "Unique identifier";
                    }
                    else
                    {
                        f.Kind = FieldKind.Other;
                        f.TypeLabel = a.AttributeTypeName?.Value ?? a.AttributeType?.ToString() ?? "Other";
                    }
                    break;
            }

            f.IsRequired = (f.RequiredLevel == "SystemRequired" || f.RequiredLevel == "ApplicationRequired")
                           && f.IsValidForCreate && !f.IsPrimaryId && !AutoFilled.Contains(f.LogicalName)
                           && f.Kind != FieldKind.UniqueId && f.Kind != FieldKind.Other;
            return f;
        }

        private static void AddOptions(DvField f, OptionSetMetadata set)
        {
            if (set?.Options == null) return;
            foreach (var o in set.Options)
            {
                if (!o.Value.HasValue) continue;
                var option = new DvOption { Value = o.Value.Value, Label = Label(o.Label) ?? o.Value.Value.ToString() };
                if (o.Label?.LocalizedLabels != null)
                    option.AllLabels.AddRange(o.Label.LocalizedLabels.Select(l => l.Label).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct());
                if (!option.AllLabels.Contains(option.Label)) option.AllLabels.Insert(0, option.Label);
                if (o is StatusOptionMetadata so) option.State = so.State;
                f.Options.Add(option);
            }
        }

        private static decimal? ToDecimal(double? d)
        {
            if (!d.HasValue) return null;
            if (d.Value >= (double)decimal.MaxValue) return decimal.MaxValue;
            if (d.Value <= (double)decimal.MinValue) return decimal.MinValue;
            return (decimal)d.Value;
        }

        public static string Label(Label label)
        {
            var text = label?.UserLocalizedLabel?.Label;
            if (string.IsNullOrWhiteSpace(text)) text = label?.LocalizedLabels?.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Label))?.Label;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }
}
