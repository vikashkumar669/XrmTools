using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using DataQualityStudio.Models;
using Microsoft.Xrm.Sdk;

namespace DataQualityStudio.Services
{
    /// <summary>
    /// Validates every mapped cell against the column's Dataverse metadata and produces the transformed preview.
    /// Unmapped or invalid mappings are reported, never fatal: validation always runs to the end.
    /// </summary>
    public sealed class ValidationEngine
    {
        private readonly IOrganizationService _service;
        private readonly MetadataService _metadata;
        private readonly Action<string> _progress;
        private readonly Func<bool> _cancelled;

        private ValidationResult _result;
        private ValidationOptions _options;
        private LookupResolver _lookups;

        public ValidationEngine(IOrganizationService service, MetadataService metadata, Action<string> progress = null, Func<bool> cancelled = null)
        {
            _service = service;
            _metadata = metadata;
            _progress = progress ?? (s => { });
            _cancelled = cancelled ?? (() => false);
        }

        public ValidationResult Run(SourceTable table, DvEntity entity, IList<ColumnMapping> mappings, ValidationOptions options)
        {
            var watch = Stopwatch.StartNew();
            _options = options;
            _result = new ValidationResult
            {
                TotalRows = table.Rows.Count,
                EntityLogicalName = entity.LogicalName,
                EntityDisplayName = entity.DisplayName,
                Options = options
            };

            BindColumns(table, entity, mappings);

            var cols = _result.Columns;
            var rows = table.Rows;
            _result.Preview = new string[rows.Count][];
            _result.CellSeverity = new byte[rows.Count][];
            _result.RowSeverity = new byte[rows.Count];

            // Prepared (transformed) values, needed twice for lookups
            var prepared = new string[rows.Count][];
            var usedDefault = new bool[rows.Count][];
            for (int r = 0; r < rows.Count; r++)
            {
                prepared[r] = new string[cols.Count];
                usedDefault[r] = new bool[cols.Count];
                for (int c = 0; c < cols.Count; c++)
                {
                    prepared[r][c] = Prepare(cols[c], rows[r].Get(cols[c].SourceIndex), out var d);
                    usedDefault[r][c] = d;
                }
            }

            ResolveLookups(prepared);

            var stateCol = cols.FindIndex(c => c.Field.Kind == FieldKind.State);
            var statusCol = cols.FindIndex(c => c.Field.Kind == FieldKind.Status);
            var keyCols = Enumerable.Range(0, cols.Count).Where(i => cols[i].Mapping.IsKey).ToList();
            var seenKeys = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int r = 0; r < rows.Count; r++)
            {
                if (r % 500 == 0)
                {
                    if (_cancelled()) throw new OperationCanceledException();
                    _progress($"Validating row {r + 1:N0} of {rows.Count:N0}...");
                }
                var row = rows[r];
                var preview = new string[cols.Count];
                var severity = new byte[cols.Count];
                var parsed = new object[cols.Count];

                for (int c = 0; c < cols.Count; c++)
                {
                    var sev = Validate(r, row, c, prepared[r][c], usedDefault[r][c], out var display, out var value);
                    preview[c] = display;
                    severity[c] = (byte)sev;
                    parsed[c] = value;
                }

                // Status reason must belong to the status
                if (stateCol >= 0 && statusCol >= 0 && parsed[stateCol] is int state && parsed[statusCol] is int status)
                {
                    var option = cols[statusCol].Field.Options.FirstOrDefault(o => o.Value == status);
                    if (option?.State != null && option.State.Value != state)
                    {
                        var valid = cols[statusCol].Field.Options.Where(o => o.State == state).Select(o => o.Label).ToList();
                        var stateLabel = cols[stateCol].Field.Options.FirstOrDefault(o => o.Value == state)?.Label ?? state.ToString();
                        AddIssue(r, row, statusCol, Severity.Error, prepared[r][statusCol],
                            $"Status reason '{option.Label}' doesn't belong to status '{stateLabel}'.",
                            valid.Count > 0 ? "Use one of: " + string.Join(", ", valid) : null,
                            valid.Count == 1 ? valid[0] : null, severity);
                    }
                }

                // Duplicate keys inside the file
                if (keyCols.Count > 0)
                {
                    var parts = keyCols.Select(k => (preview[k] ?? "").Trim().ToLowerInvariant()).ToList();
                    if (parts.Any(p => p.Length > 0))
                    {
                        var key = string.Join("\u001F", parts);
                        if (seenKeys.TryGetValue(key, out var firstRow))
                        {
                            AddIssue(r, row, keyCols[0], Severity.Warning, string.Join(" | ", keyCols.Select(k => prepared[r][k])),
                                $"Duplicate key: same {string.Join(" + ", keyCols.Select(k => cols[k].Header))} as row {firstRow}.",
                                "Remove the duplicate row or merge it with the first one.", null, severity);
                        }
                        else seenKeys[key] = row.RowNumber;
                    }
                }

                _result.Preview[r] = preview;
                _result.CellSeverity[r] = severity;
                _result.RowSeverity[r] = severity.Length == 0 ? (byte)0 : severity.Max();
            }

            watch.Stop();
            _result.Elapsed = watch.Elapsed;
            return _result;
        }

        // ============================== Mapping-level checks ==============================

        private void BindColumns(SourceTable table, DvEntity entity, IList<ColumnMapping> mappings)
        {
            var byHeader = mappings.Where(m => m != null && !string.IsNullOrEmpty(m.SourceColumn))
                .GroupBy(m => m.SourceColumn, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var usedFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < table.Headers.Count; i++)
            {
                var header = table.Headers[i];
                byHeader.TryGetValue(header, out var m);
                if (m == null || !m.IsMapped)
                {
                    _result.UnmappedColumns.Add(header);
                    var closest = MappingSuggester.Closest(header, entity.Fields, out var score);
                    var hasData = table.Rows.Any(r => !string.IsNullOrWhiteSpace(r.Get(i)));
                    AddMappingIssue(Severity.Info, header, null,
                        $"Column '{header}' isn't mapped and will be ignored{(hasData ? "" : " (it's empty)")}.",
                        closest != null && score >= 0.65 ? $"Closest Dataverse column: {closest.ComboText}" : null, i);
                    continue;
                }

                var field = entity.GetField(m.TargetField);
                if (field == null)
                {
                    _result.UnmappedColumns.Add(header);
                    AddMappingIssue(Severity.Error, header, m.TargetField,
                        $"Column '{header}' is mapped to '{m.TargetField}', which doesn't exist on {entity.DisplayName}. It was skipped.",
                        "Pick another column in the mapping, or refresh metadata if it was just created.", i);
                    continue;
                }
                if (usedFields.TryGetValue(field.LogicalName, out var otherHeader))
                {
                    AddMappingIssue(Severity.Error, header, field.LogicalName,
                        $"'{header}' and '{otherHeader}' are both mapped to {field.ComboText}. Only '{otherHeader}' is validated.",
                        "Map one of the columns to a different field or set it to (not mapped).", i);
                    _result.UnmappedColumns.Add(header);
                    continue;
                }
                if (_options.Mode == ImportMode.Create && !field.IsValidForCreate)
                    AddMappingIssue(Severity.Warning, header, field.LogicalName,
                        $"{field.ComboText} can't be set when creating records; Dataverse will ignore or reject it.",
                        "Use Update mode, or remove this mapping.", i);
                if (_options.Mode == ImportMode.Update && !field.IsValidForUpdate)
                    AddMappingIssue(Severity.Warning, header, field.LogicalName,
                        $"{field.ComboText} can't be changed on existing records.",
                        "Remove this mapping for update imports.", i);

                usedFields[field.LogicalName] = header;
                _result.Columns.Add(new BoundColumn { Mapping = m.Clone(), Field = field, SourceIndex = i });
            }

            if (_options.Mode == ImportMode.Create)
            {
                foreach (var f in entity.Fields.Where(f => f.IsRequired && !usedFields.ContainsKey(f.LogicalName)))
                {
                    AddMappingIssue(Severity.Error, null, f.LogicalName,
                        $"Required column {f.ComboText} isn't mapped, so every new record would fail.",
                        "Add a source column for it, or map an existing column and use its Default value.", -1);
                }
            }
        }

        // ============================== Lookups ==============================

        private void ResolveLookups(string[][] prepared)
        {
            var lookupCols = _result.Columns.Select((c, i) => new { c, i }).Where(x => x.c.Field.IsLookup).ToList();
            if (lookupCols.Count == 0) return;
            if (!_options.CheckLookups || _service == null) return;

            _lookups = new LookupResolver(_service, _metadata);
            foreach (var x in lookupCols)
            {
                if (_cancelled()) throw new OperationCanceledException();
                var values = prepared.Select(p => p[x.i]).Where(v => !string.IsNullOrWhiteSpace(v));
                var before = _lookups.MappingProblems.Count;
                _lookups.Resolve(x.c, values, _progress, _cancelled);
                foreach (var problem in _lookups.MappingProblems.Skip(before))
                    AddMappingIssue(Severity.Error, x.c.Header, x.c.Field.LogicalName, problem,
                        "Fix the 'Lookup match column' in the mapping (blank = primary name column).", x.c.SourceIndex, x.i);
            }
        }

        // ============================== Cell validation ==============================

        private string Prepare(BoundColumn col, string raw, out bool usedDefault)
        {
            usedDefault = false;
            var value = raw ?? "";
            if (_options.TrimWhitespace) value = value.Trim();

            var map = col.Mapping.ParseValueMap();
            if (map.Count > 0 && map.TryGetValue(value.Trim(), out var mapped)) value = mapped;

            value = TextUtil.ApplyTransform(col.Mapping.Transform, value);

            if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrEmpty(col.Mapping.DefaultValue))
            {
                value = col.Mapping.DefaultValue;
                usedDefault = true;
            }
            return value ?? "";
        }

        private Severity Validate(int r, SourceRow row, int c, string value, bool usedDefault, out string display, out object parsed)
        {
            var col = _result.Columns[c];
            var f = col.Field;
            display = value;
            parsed = null;
            var sev = (Severity)0;

            Severity Issue(Severity s, string message, string suggestion = null, string corrected = null)
            {
                AddIssue(r, row, c, s, value, message, suggestion, corrected, null);
                if (s > sev) sev = s;
                return sev;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                display = "";
                if (f.IsRequired && _options.Mode == ImportMode.Create)
                    Issue(Severity.Error, $"{f.DisplayName} is required but empty.", "Enter a value, or set a Default value for this column in the mapping.");
                else if (f.IsRequired)
                    Issue(Severity.Warning, $"{f.DisplayName} is required; an empty value would clear it on the existing record.", "Leave the column out of update imports or fill it in.");
                return sev;
            }

            switch (f.Kind)
            {
                case FieldKind.Text:
                case FieldKind.Memo:
                    ValidateText(f, value, ref display, Issue);
                    break;

                case FieldKind.WholeNumber:
                case FieldKind.BigInt:
                    {
                        if (!ValueParser.TryParseNumber(value, _options.NumberFormat, out var n, out var symbols))
                        {
                            var digits = new string(value.Where(ch => char.IsDigit(ch) || ch == '-').ToArray());
                            Issue(Severity.Error, $"{TextUtil.Quote(value)} isn't a whole number.",
                                digits.Length > 0 && digits.Any(char.IsDigit) ? $"Use {digits}" : "Enter a whole number.",
                                digits.Length > 0 && long.TryParse(digits, out _) ? digits : null);
                            break;
                        }
                        if (n != decimal.Truncate(n))
                        {
                            var rounded = ValueParser.FormatNumber(decimal.Round(n, 0, MidpointRounding.AwayFromZero));
                            Issue(Severity.Error, $"{TextUtil.Quote(value)} has decimals but {f.DisplayName} only accepts whole numbers.", $"Use {rounded}", rounded);
                            break;
                        }
                        CheckRange(f, n, value, Issue);
                        display = ValueParser.FormatNumber(n);
                        parsed = n;
                        if (symbols) Issue(Severity.Warning, $"Symbols were removed from {TextUtil.Quote(value)}.", $"Use {display}", display);
                        break;
                    }

                case FieldKind.Decimal:
                case FieldKind.Double:
                case FieldKind.Money:
                    {
                        if (!ValueParser.TryParseNumber(value, _options.NumberFormat, out var n, out var symbols))
                        {
                            Issue(Severity.Error, $"{TextUtil.Quote(value)} isn't a number.", "Enter a number such as 1234.56 (no text or units).");
                            break;
                        }
                        display = ValueParser.FormatNumber(n);
                        parsed = n;
                        CheckRange(f, n, value, Issue);
                        if (f.Precision.HasValue && ValueParser.DecimalPlaces(n) > f.Precision.Value)
                        {
                            var rounded = ValueParser.FormatNumber(decimal.Round(n, Math.Min(f.Precision.Value, 28), MidpointRounding.AwayFromZero));
                            Issue(Severity.Warning, $"{display} has more than {f.Precision} decimal place(s); Dataverse will round it.", $"Use {rounded}", rounded);
                            display = rounded;
                        }
                        else if (symbols) Issue(Severity.Warning, $"Currency or other symbols were removed from {TextUtil.Quote(value)}.", $"Use {display}", display);
                        break;
                    }

                case FieldKind.Boolean:
                    if (ValueParser.TryParseBool(value, f.TrueLabel, f.FalseLabel, out var b))
                    {
                        display = b ? f.TrueLabel : f.FalseLabel;
                        parsed = b;
                    }
                    else
                    {
                        var guess = TextUtil.Closest(value, new[] { f.TrueLabel, f.FalseLabel }, 0.6, out _);
                        Issue(Severity.Error, $"{TextUtil.Quote(value)} isn't a valid Yes/No value for {f.DisplayName}.",
                            guess != null ? $"Did you mean '{guess}'?" : BooleanHint(f), guess);
                    }
                    break;

                case FieldKind.DateTime:
                    ValidateDate(f, value, ref display, ref parsed, Issue);
                    break;

                case FieldKind.Choice:
                case FieldKind.State:
                case FieldKind.Status:
                    {
                        var option = MatchOption(f, value, out var loose);
                        if (option == null) ChoiceError(f, value, Issue);
                        else
                        {
                            display = option.ToString();
                            parsed = option.Value;
                            if (loose) Issue(Severity.Warning, $"{TextUtil.Quote(value)} was matched loosely to '{option.Label}'.", $"Use '{option.Label}'", option.Label);
                        }
                        break;
                    }

                case FieldKind.MultiChoice:
                    {
                        var parts = SplitMulti(value);
                        var labels = new List<string>();
                        var corrected = new List<string>();
                        var bad = false;
                        foreach (var p in parts)
                        {
                            var option = MatchOption(f, p, out _);
                            if (option == null)
                            {
                                bad = true;
                                var guess = TextUtil.Closest(p, f.Options.Select(o => o.Label), 0.5, out _, true);
                                corrected.Add(guess ?? p);
                                Issue(Severity.Error, $"{TextUtil.Quote(p)} isn't a valid choice for {f.DisplayName}.",
                                    guess != null ? $"Did you mean '{guess}'?" : "Allowed: " + AllowedList(f));
                            }
                            else
                            {
                                labels.Add(option.ToString());
                                corrected.Add(option.Label);
                            }
                        }
                        display = string.Join("; ", labels);
                        if (bad && sev == Severity.Error)
                        {
                            // Attach the combined correction to the last issue for this cell
                            var last = _result.Issues.LastOrDefault();
                            if (last != null && last.RowIndex == r && last.MappedIndex == c && corrected.All(x => f.Options.Any(o => o.Label == x)))
                                last.CorrectedValue = string.Join("; ", corrected);
                        }
                        break;
                    }

                case FieldKind.Lookup:
                case FieldKind.Customer:
                case FieldKind.Owner:
                    ValidateLookup(col, value, ref display, Issue);
                    break;

                case FieldKind.UniqueId:
                    if (Guid.TryParse(value.Trim('{', '}'), out var g)) display = g.ToString("D");
                    else Issue(Severity.Error, $"{TextUtil.Quote(value)} isn't a valid GUID.", "Use the format 00000000-0000-0000-0000-000000000000.");
                    break;
            }

            if (usedDefault && sev == 0) display = display + "  (default)";
            return sev;
        }

        private void ValidateText(DvField f, string value, ref string display, Func<Severity, string, string, string, Severity> issue)
        {
            if (f.MaxLength.HasValue && value.Length > f.MaxLength.Value)
            {
                var cut = value.Substring(0, f.MaxLength.Value);
                issue(Severity.Error, $"Text is {value.Length:N0} characters; {f.DisplayName} allows {f.MaxLength:N0}.", $"Shorten to {f.MaxLength:N0} characters", cut);
                display = cut + "…";
                return;
            }
            if (value.Contains("\n") && f.Kind == FieldKind.Text)
                issue(Severity.Warning, "Line breaks aren't kept in single-line text.", "Remove the line breaks", System.Text.RegularExpressions.Regex.Replace(value, @"\s*\r?\n\s*", " "));

            if (string.Equals(f.Format, "Email", StringComparison.OrdinalIgnoreCase) && !ValueParser.IsEmail(value))
            {
                var fixedValue = new string(value.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).Trim('.', ',', ';').Replace(",", ".");
                if (fixedValue.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) fixedValue = fixedValue.Substring(7);
                issue(Severity.Warning, $"{TextUtil.Quote(value)} doesn't look like an e-mail address.",
                    ValueParser.IsEmail(fixedValue) ? $"Use {fixedValue}" : "Check the address (name@domain.com).",
                    ValueParser.IsEmail(fixedValue) ? fixedValue : null);
            }
            else if (string.Equals(f.Format, "Url", StringComparison.OrdinalIgnoreCase) && !ValueParser.IsUrl(value))
            {
                var withScheme = "https://" + value.Trim().TrimStart('/');
                var ok = !value.Contains(" ") && ValueParser.IsUrl(withScheme);
                issue(Severity.Warning, $"{TextUtil.Quote(value)} isn't a full URL.", ok ? $"Use {withScheme}" : "Use a full address such as https://www.contoso.com.", ok ? withScheme : null);
            }
        }

        private void ValidateDate(DvField f, string value, ref string display, ref object parsed, Func<Severity, string, string, string, Severity> issue)
        {
            if (!ValueParser.TryParseDate(value, _options.DateOrder, out var dt, out var ambiguous, out var note))
            {
                issue(Severity.Error, $"{TextUtil.Quote(value)} isn't a recognizable date.",
                    _options.DateOrder == DateOrder.Auto ? "Use yyyy-MM-dd (e.g. 2026-03-31) or set the date format in Options." : $"Expected {_options.DateOrder} order; or use yyyy-MM-dd.", null);
                return;
            }
            parsed = dt;
            if (dt.Year < 1753 || dt.Year > 9999)
            {
                issue(Severity.Error, $"{ValueParser.FormatDate(dt, true)} is outside the range Dataverse accepts (1753–9999).", "Check the year.", null);
                return;
            }
            display = ValueParser.FormatDate(dt, f.DateOnly);
            if (ambiguous)
                issue(Severity.Warning, $"Ambiguous date {TextUtil.Quote(value)} was {note} as {dt:d MMM yyyy}.",
                    $"Confirm, or set the date format in Options. Unambiguous: {display}", display);
            else if (note != null && dt.Year < 1950)
                issue(Severity.Warning, $"{TextUtil.Quote(value)} was {note}: {display}.", $"Use {display}", display);
            if (f.DateOnly && dt.TimeOfDay != TimeSpan.Zero)
                issue(Severity.Warning, $"{f.DisplayName} is date-only; the time {dt:HH:mm} will be dropped.", $"Use {display}", display);
        }

        private void ValidateLookup(BoundColumn col, string value, ref string display, Func<Severity, string, string, string, Severity> issue)
        {
            var f = col.Field;
            if (_lookups == null)
            {
                display = "→ " + value + (_options.CheckLookups ? "" : "  (not checked)");
                return;
            }
            var o = _lookups.Get(col, value);
            if (o == null) { display = "→ " + value; return; }
            switch (o.Status)
            {
                case LookupStatus.Found:
                    display = $"→ {o.Name} [{o.Table}]";
                    if (o.Inactive)
                        issue(Severity.Warning, $"{TextUtil.Quote(value)} matches an inactive {o.Table} record.", "Reactivate it, or point to an active record.", null);
                    break;
                case LookupStatus.Ambiguous:
                    display = $"→ {value}  ({o.MatchCount} matches)";
                    issue(Severity.Warning, o.Message + "; the import may link the wrong record or fail.",
                        "Make the value unique, use a unique match column, or enter the record GUID" +
                        (o.Suggestions.Count > 0 ? " (e.g. " + string.Join(", ", o.Suggestions) + ")" : "") + ".", null);
                    break;
                case LookupStatus.NotFound:
                    display = $"→ {value}  (not found)";
                    var hint = o.Suggestions.Count > 0
                        ? "Did you mean: " + string.Join(", ", o.Suggestions.Select(s => "'" + s + "'")) + "?"
                        : $"Check the spelling, or create the {o.Table} record before importing.";
                    // Similar names are only suggested, never applied: linking the wrong record is worse than an error
                    issue(Severity.Error, $"No {o.Table} record has {o.MatchField ?? "name"} = {TextUtil.Quote(value)}.", hint, null);
                    break;
                default:
                    display = $"→ {value}  (not verified)";
                    issue(Severity.Warning, "Couldn't verify this lookup: " + o.Message, "Check that you can read the target table.", null);
                    break;
            }
        }

        private static void CheckRange(DvField f, decimal n, string value, Func<Severity, string, string, string, Severity> issue)
        {
            if ((f.MinValue.HasValue && n < f.MinValue.Value) || (f.MaxValue.HasValue && n > f.MaxValue.Value))
            {
                issue(Severity.Error,
                    $"{ValueParser.FormatNumber(n)} is outside the allowed range {Fmt(f.MinValue)} to {Fmt(f.MaxValue)} for {f.DisplayName}.",
                    $"Enter a value between {Fmt(f.MinValue)} and {Fmt(f.MaxValue)}.", null);
            }
        }

        private static string Fmt(decimal? d) => d.HasValue ? d.Value.ToString("#,##0.##", CultureInfo.InvariantCulture) : "∞";

        private static DvOption MatchOption(DvField f, string value, out bool loose)
        {
            loose = false;
            var v = value.Trim();
            var exact = f.Options.FirstOrDefault(o => o.AllLabels.Any(l => string.Equals(l.Trim(), v, StringComparison.OrdinalIgnoreCase)));
            if (exact != null) return exact;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                var byValue = f.Options.FirstOrDefault(o => o.Value == number);
                if (byValue != null) return byValue;
            }
            // "Value - Label" or "Label (Value)" as produced by some exports
            var m = System.Text.RegularExpressions.Regex.Match(v, @"^\s*(\d+)\s*[-–:]|\((\d+)\)\s*$");
            if (m.Success && int.TryParse(m.Groups[1].Success && m.Groups[1].Length > 0 ? m.Groups[1].Value : m.Groups[2].Value, out var embedded))
            {
                var byEmbedded = f.Options.FirstOrDefault(o => o.Value == embedded);
                if (byEmbedded != null) { loose = true; return byEmbedded; }
            }
            var norm = TextUtil.Normalize(v);
            var normalized = f.Options.Where(o => o.AllLabels.Any(l => TextUtil.Normalize(l) == norm)).ToList();
            if (normalized.Count == 1) { loose = true; return normalized[0]; }
            return null;
        }

        private static void ChoiceError(DvField f, string value, Func<Severity, string, string, string, Severity> issue)
        {
            var guess = TextUtil.Closest(value, f.Options.SelectMany(o => o.AllLabels), 0.5, out _, true);
            var label = guess == null ? null : f.Options.First(o => o.AllLabels.Contains(guess)).Label;
            if (label == null) label = UniquePrefixLabel(f, value);
            issue(Severity.Error, $"{TextUtil.Quote(value)} isn't a valid choice for {f.DisplayName}.",
                label != null ? $"Did you mean '{label}'? Allowed: {AllowedList(f)}" : "Allowed: " + AllowedList(f), label);
        }

        /// <summary>Short codes such as "M" or "Fem" that start exactly one label ("Male", "Female").</summary>
        private static string UniquePrefixLabel(DvField f, string value)
        {
            var v = TextUtil.Normalize(value);
            if (v.Length == 0 || v.Length > 3) return null;
            var matches = f.Options.Where(o => o.AllLabels.Any(l => TextUtil.Normalize(l).StartsWith(v, StringComparison.Ordinal))).ToList();
            return matches.Count == 1 ? matches[0].Label : null;
        }

        private static string BooleanHint(DvField f)
        {
            var labels = string.Equals(f.TrueLabel, "Yes", StringComparison.OrdinalIgnoreCase) && string.Equals(f.FalseLabel, "No", StringComparison.OrdinalIgnoreCase)
                ? "Yes/No" : $"{f.TrueLabel}/{f.FalseLabel}, Yes/No";
            return $"Use {labels}, true/false or 1/0.";
        }

        private static string AllowedList(DvField f)
        {
            var labels = f.Options.Select(o => o.Label).ToList();
            return labels.Count <= 12 ? string.Join(", ", labels) : string.Join(", ", labels.Take(12)) + $" … (+{labels.Count - 12} more)";
        }

        private static List<string> SplitMulti(string value)
        {
            var sep = value.Contains(";") ? ';' : value.Contains("|") ? '|' : ',';
            return value.Split(sep).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        }

        // ============================== Issues ==============================

        private void AddIssue(int r, SourceRow row, int c, Severity s, string value, string message, string suggestion, string corrected, byte[] cellSeverity)
        {
            var col = _result.Columns[c];
            Count(s);
            if (cellSeverity != null && (byte)s > cellSeverity[c]) cellSeverity[c] = (byte)s;
            if (_result.Issues.Count >= _options.MaxIssues) { _result.IssuesTruncated = true; return; }
            _result.Issues.Add(new ValidationIssue
            {
                RowIndex = r,
                RowNumber = row.RowNumber,
                SourceIndex = col.SourceIndex,
                MappedIndex = c,
                Column = col.Header,
                Field = col.Field.LogicalName,
                Severity = s,
                Value = value,
                Message = message,
                Suggestion = suggestion,
                CorrectedValue = corrected
            });
        }

        private void AddMappingIssue(Severity s, string column, string field, string message, string suggestion, int sourceIndex, int mappedIndex = -1)
        {
            Count(s);
            _result.Issues.Add(new ValidationIssue
            {
                SourceIndex = sourceIndex,
                MappedIndex = mappedIndex,
                Column = column ?? "",
                Field = field ?? "",
                Severity = s,
                Message = message,
                Suggestion = suggestion
            });
        }

        private void Count(Severity s)
        {
            if (s == Severity.Error) _result.ErrorCount++;
            else if (s == Severity.Warning) _result.WarningCount++;
            else _result.InfoCount++;
        }
    }
}
