using System;
using System.Collections.Generic;

namespace ChartDesignerPro.Models
{
    /// <summary>A table as shown in the table picker.</summary>
    public sealed class TableSummary
    {
        public string LogicalName { get; set; }
        public string DisplayName { get; set; }
        public int? ObjectTypeCode { get; set; }

        public string Text => string.IsNullOrEmpty(LogicalName) ? DisplayName
            : string.IsNullOrEmpty(DisplayName) || DisplayName == LogicalName ? LogicalName
            : DisplayName + " (" + LogicalName + ")";

        public override string ToString() => Text;
    }

    /// <summary>Column kinds relevant to charts (categories, measures, aggregations).</summary>
    public enum ColumnKind
    {
        Other, Text, Memo, WholeNumber, BigInt, Decimal, Float, Money, DateTime,
        Choice, MultiChoice, TwoOptions, State, Status, Lookup, Customer, Owner, UniqueIdentifier
    }

    public sealed class ColumnMetadata
    {
        public string LogicalName { get; set; }
        public string DisplayName { get; set; }
        public ColumnKind Kind { get; set; }

        /// <summary>AttributeMetadata.IsValidForRead.</summary>
        public bool? IsValidForRead { get; set; }

        /// <summary>AttributeMetadata.IsLogical.</summary>
        public bool? IsLogical { get; set; }

        /// <summary>AttributeMetadata.AttributeOf: set for helper columns such as "...name" or "...yominame".</summary>
        public string AttributeOf { get; set; }

        public bool IsNumeric => Kind == ColumnKind.WholeNumber || Kind == ColumnKind.BigInt || Kind == ColumnKind.Decimal
                                 || Kind == ColumnKind.Float || Kind == ColumnKind.Money;
    }

    public sealed class TableMetadata
    {
        public string LogicalName { get; set; }
        public string DisplayName { get; set; }

        /// <summary>Plural display name (EntityMetadata.DisplayCollectionName), e.g. "Opportunities".</summary>
        public string DisplayCollectionName { get; set; }
        public int? ObjectTypeCode { get; set; }
        public string PrimaryIdAttribute { get; set; }
        public string PrimaryNameAttribute { get; set; }
        public IReadOnlyDictionary<string, ColumnMetadata> Columns { get; set; } =
            new Dictionary<string, ColumnMetadata>(StringComparer.OrdinalIgnoreCase);
        public DateTime RetrievedUtc { get; set; }
    }
}
