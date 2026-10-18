using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace DataQualityStudio.Models
{
    public enum Severity
    {
        Info = 1,
        Warning = 2,
        Error = 3
    }

    public enum ImportMode
    {
        Create,
        Update
    }

    public enum DateOrder
    {
        Auto,
        DayFirst,
        MonthFirst,
        YearFirst
    }

    public enum NumberFormatOption
    {
        Auto,
        DotDecimal,   // 1,234.56
        CommaDecimal  // 1.234,56
    }

    public static class Transforms
    {
        public const string None = "None";
        public const string Upper = "UPPERCASE";
        public const string Lower = "lowercase";
        public const string Title = "Title Case";
        public const string RemoveSpaces = "Remove spaces";
        public const string DigitsOnly = "Digits only";
        public const string CollapseSpaces = "Collapse spaces";

        public static readonly string[] All = { None, Upper, Lower, Title, CollapseSpaces, RemoveSpaces, DigitsOnly };
    }

    // ============================== Source file ==============================

    public sealed class SourceRow
    {
        /// <summary>Row number as shown in Excel (or line-based record number for CSV).</summary>
        public int RowNumber { get; set; }
        public string[] Values { get; set; }

        public string Get(int index) => Values != null && index >= 0 && index < Values.Length ? Values[index] ?? "" : "";
    }

    public sealed class SourceTable
    {
        public string FilePath { get; set; }
        public string SheetName { get; set; }
        public int HeaderRow { get; set; } = 1;
        public List<string> Headers { get; } = new List<string>();
        public List<SourceRow> Rows { get; } = new List<SourceRow>();

        public string FileName => System.IO.Path.GetFileName(FilePath ?? "");

        public IEnumerable<string> SampleValues(int column, int count = 3) =>
            Rows.Select(r => r.Get(column)).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().Take(count);
    }

    // ============================== Dataverse metadata ==============================

    public enum FieldKind
    {
        Text,
        Memo,
        WholeNumber,
        BigInt,
        Decimal,
        Double,
        Money,
        Boolean,
        DateTime,
        Choice,
        State,
        Status,
        MultiChoice,
        Lookup,
        Customer,
        Owner,
        UniqueId,
        Other
    }

    public sealed class DvOption
    {
        public int Value { get; set; }
        public string Label { get; set; }
        public List<string> AllLabels { get; } = new List<string>();
        /// <summary>For Status Reason options: the Status (statecode) value they belong to.</summary>
        public int? State { get; set; }

        public override string ToString() => $"{Label} ({Value})";
    }

    public sealed class DvField
    {
        public string LogicalName { get; set; }
        public string SchemaName { get; set; }
        public string DisplayName { get; set; }
        public FieldKind Kind { get; set; }
        public string TypeLabel { get; set; }
        public string RequiredLevel { get; set; }      // None, Recommended, ApplicationRequired, SystemRequired
        public bool IsRequired { get; set; }           // must have a value when creating
        public bool IsValidForCreate { get; set; }
        public bool IsValidForUpdate { get; set; }
        public bool IsPrimaryName { get; set; }
        public bool IsPrimaryId { get; set; }
        public int? MaxLength { get; set; }
        public decimal? MinValue { get; set; }
        public decimal? MaxValue { get; set; }
        public int? Precision { get; set; }
        public string Format { get; set; }             // Email, Url, Phone, Text...
        public bool DateOnly { get; set; }
        public List<DvOption> Options { get; } = new List<DvOption>();
        public string TrueLabel { get; set; }
        public string FalseLabel { get; set; }
        public string[] Targets { get; set; } = new string[0];

        public bool IsLookup => Kind == FieldKind.Lookup || Kind == FieldKind.Customer || Kind == FieldKind.Owner;
        public bool IsChoice => Kind == FieldKind.Choice || Kind == FieldKind.State || Kind == FieldKind.Status || Kind == FieldKind.MultiChoice;

        public string RequiredLabel =>
            IsRequired ? "Required"
            : RequiredLevel == "Recommended" ? "Recommended"
            : RequiredLevel == "SystemRequired" || RequiredLevel == "ApplicationRequired" ? "Auto/Default"
            : "";

        public string ComboText => $"{DisplayName} ({LogicalName})";

        public override string ToString() => ComboText;
    }

    public sealed class DvEntity
    {
        public string LogicalName { get; set; }
        public string DisplayName { get; set; }
        public string PrimaryIdAttribute { get; set; }
        public string PrimaryNameAttribute { get; set; }
        public bool HasFields => Fields != null;

        /// <summary>All attributes (including read-only ones) keyed by logical name; used for lookup match fields.</summary>
        public Dictionary<string, DvField> AllAttributes { get; set; }

        /// <summary>Fields that can be imported (valid for create or update).</summary>
        public List<DvField> Fields { get; set; }

        public DvField GetField(string logicalName)
        {
            if (string.IsNullOrEmpty(logicalName) || AllAttributes == null) return null;
            AllAttributes.TryGetValue(logicalName, out var f);
            return f;
        }

        public string ComboText => string.Equals(DisplayName, LogicalName, StringComparison.Ordinal)
            ? LogicalName
            : $"{DisplayName} ({LogicalName})";

        public override string ToString() => ComboText;
    }

    // ============================== Mapping & profiles ==============================

    [DataContract]
    public sealed class ColumnMapping
    {
        [DataMember(Order = 1)] public string SourceColumn { get; set; }
        [DataMember(Order = 2, EmitDefaultValue = false)] public string TargetField { get; set; }
        [DataMember(Order = 3, EmitDefaultValue = false)] public string Transform { get; set; }
        [DataMember(Order = 4, EmitDefaultValue = false)] public string ValueMap { get; set; }
        [DataMember(Order = 5, EmitDefaultValue = false)] public string DefaultValue { get; set; }
        [DataMember(Order = 6, EmitDefaultValue = false)] public string LookupMatchField { get; set; }
        [DataMember(Order = 7, EmitDefaultValue = false)] public bool IsKey { get; set; }

        /// <summary>MatchKind of a column the user (or a profile) explicitly left unmapped; auto-map leaves it alone.</summary>
        public const string Ignored = "Ignored";

        /// <summary>How the mapping was made (Auto: display name, Profile, Manual...). Not saved.</summary>
        public string MatchKind { get; set; }

        public bool IsMapped => !string.IsNullOrWhiteSpace(TargetField);

        public ColumnMapping Clone() => (ColumnMapping)MemberwiseClone();

        /// <summary>Parses "old=new; old2=new2" into a case-insensitive dictionary. Use \; or \= for literal characters.</summary>
        public Dictionary<string, string> ParseValueMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(ValueMap)) return map;
            foreach (var pair in SplitEscaped(ValueMap, ';'))
            {
                var parts = SplitEscaped(pair, '=');
                if (parts.Count < 2) continue;
                var key = Unescape(parts[0]).Trim();
                var value = Unescape(string.Join("=", parts.Skip(1))).Trim();
                if (!map.ContainsKey(key)) map[key] = value;
            }
            return map;
        }

        private static string Unescape(string s) => s.Replace("\\;", ";").Replace("\\=", "=").Replace("\\\\", "\\");

        private static List<string> SplitEscaped(string text, char separator)
        {
            var result = new List<string>();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < text.Length && (text[i + 1] == ';' || text[i + 1] == '=' || text[i + 1] == '\\'))
                {
                    // Keep the escape for the inner split unless it escapes this separator
                    if (text[i + 1] != separator) sb.Append(c);
                    sb.Append(text[i + 1]);
                    i++;
                }
                else if (c == separator)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                else sb.Append(c);
            }
            result.Add(sb.ToString());
            return result;
        }
    }

    [DataContract]
    public sealed class MappingProfile
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public string Description { get; set; }
        [DataMember(Order = 3)] public string EntityLogicalName { get; set; }
        [DataMember(Order = 4)] public string Mode { get; set; } = ImportMode.Create.ToString();
        [DataMember(Order = 5)] public string DateOrder { get; set; } = Models.DateOrder.Auto.ToString();
        [DataMember(Order = 6)] public string NumberFormat { get; set; } = NumberFormatOption.Auto.ToString();
        [DataMember(Order = 7)] public bool TrimWhitespace { get; set; } = true;
        [DataMember(Order = 8)] public bool CheckLookups { get; set; } = true;
        [DataMember(Order = 9, EmitDefaultValue = false)] public string SheetName { get; set; }
        [DataMember(Order = 10)] public int HeaderRow { get; set; } = 1;
        [DataMember(Order = 11)] public List<ColumnMapping> Mappings { get; set; } = new List<ColumnMapping>();
        [DataMember(Order = 12)] public string SavedOn { get; set; }
        [DataMember(Order = 13)] public string SavedBy { get; set; }
        [DataMember(Order = 14)] public string ToolVersion { get; set; }
    }

    // ============================== Validation ==============================

    public sealed class ValidationOptions
    {
        public ImportMode Mode { get; set; } = ImportMode.Create;
        public DateOrder DateOrder { get; set; } = DateOrder.Auto;
        public NumberFormatOption NumberFormat { get; set; } = NumberFormatOption.Auto;
        public bool TrimWhitespace { get; set; } = true;
        public bool CheckLookups { get; set; } = true;
        /// <summary>Stop recording individual issues after this many (counts keep going).</summary>
        public int MaxIssues { get; set; } = 200000;
    }

    public sealed class ValidationIssue
    {
        /// <summary>Index into SourceTable.Rows; -1 for mapping-level issues.</summary>
        public int RowIndex { get; set; } = -1;
        public int? RowNumber { get; set; }
        public int SourceIndex { get; set; } = -1;
        /// <summary>Index into ValidationResult.Columns; -1 if not tied to a mapped column.</summary>
        public int MappedIndex { get; set; } = -1;
        public string Column { get; set; }
        public string Field { get; set; }
        public Severity Severity { get; set; }
        public string Value { get; set; }
        public string Message { get; set; }
        public string Suggestion { get; set; }
        /// <summary>A concrete replacement value that fixes the issue, when one is known.</summary>
        public string CorrectedValue { get; set; }

        public string RowText => RowNumber.HasValue ? RowNumber.Value.ToString() : "—";
    }

    /// <summary>A mapping resolved against metadata and the file's columns.</summary>
    public sealed class BoundColumn
    {
        public ColumnMapping Mapping { get; set; }
        public DvField Field { get; set; }
        public int SourceIndex { get; set; }
        public string Header => Mapping.SourceColumn;
    }

    public sealed class ValidationResult
    {
        public List<BoundColumn> Columns { get; } = new List<BoundColumn>();
        public List<ValidationIssue> Issues { get; } = new List<ValidationIssue>();
        /// <summary>[row][column] transformed value as it would be imported.</summary>
        public string[][] Preview { get; set; }
        /// <summary>[row][column] worst severity of the cell (0 = OK).</summary>
        public byte[][] CellSeverity { get; set; }
        public byte[] RowSeverity { get; set; }
        public List<string> UnmappedColumns { get; } = new List<string>();
        public int TotalRows { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public int InfoCount { get; set; }
        public bool IssuesTruncated { get; set; }
        public TimeSpan Elapsed { get; set; }
        public DateTime ValidatedOn { get; set; } = DateTime.Now;
        public string EntityLogicalName { get; set; }
        public string EntityDisplayName { get; set; }
        public ValidationOptions Options { get; set; }

        public int RowsWithErrors => RowSeverity?.Count(s => s >= (byte)Severity.Error) ?? 0;
        public int RowsWithWarnings => RowSeverity?.Count(s => s == (byte)Severity.Warning) ?? 0;
        public int CleanRows => RowSeverity?.Count(s => s < (byte)Severity.Warning) ?? 0;

        public string Summary =>
            $"{TotalRows:N0} rows · {RowsWithErrors:N0} with errors · {RowsWithWarnings:N0} with warnings only · " +
            $"{ErrorCount:N0} errors · {WarningCount:N0} warnings · {UnmappedColumns.Count} unmapped column(s)";
    }
}
