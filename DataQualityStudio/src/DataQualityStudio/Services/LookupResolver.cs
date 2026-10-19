using System;
using System.Collections.Generic;
using System.Linq;
using DataQualityStudio.Models;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DataQualityStudio.Services
{
    public enum LookupStatus
    {
        Found,
        NotFound,
        Ambiguous,
        Unverified
    }

    public sealed class LookupOutcome
    {
        public LookupStatus Status;
        public string Table;
        public string MatchField;
        public Guid Id;
        public string Name;
        public int MatchCount;
        public bool Inactive;
        public string Message;
        public List<string> Suggestions = new List<string>();
    }

    /// <summary>
    /// Resolves lookup values against Dataverse in batches: by GUID, by the primary name column or by a chosen match column.
    /// Values may be prefixed with a table name for polymorphic lookups ("contact:Jane Doe").
    /// </summary>
    public sealed class LookupResolver
    {
        private const int BatchSize = 100;
        private const int MaxSuggestionQueries = 30;

        private readonly IOrganizationService _service;
        private readonly MetadataService _metadata;
        private readonly Dictionary<BoundColumn, Dictionary<string, LookupOutcome>> _results = new Dictionary<BoundColumn, Dictionary<string, LookupOutcome>>();

        public LookupResolver(IOrganizationService service, MetadataService metadata)
        {
            _service = service;
            _metadata = metadata;
        }

        /// <summary>Problems with the mapping itself (unknown match column etc.), reported once per column.</summary>
        public List<string> MappingProblems { get; } = new List<string>();

        public LookupOutcome Get(BoundColumn column, string value)
        {
            if (_results.TryGetValue(column, out var map) && map.TryGetValue(Key(value), out var outcome)) return outcome;
            return null;
        }

        private static string Key(string value) => (value ?? "").Trim().ToLowerInvariant();

        private sealed class Request
        {
            public string Value;     // the full cell value
            public string Term;      // value without the table prefix
            public List<string> Tables;
            public Guid? Id;
        }

        private sealed class Hit
        {
            public string Table;
            public Guid Id;
            public string Name;
            public string MatchValue;
            public bool Inactive;
        }

        public void Resolve(BoundColumn column, IEnumerable<string> values, Action<string> progress, Func<bool> cancelled)
        {
            var field = column.Field;
            var map = new Dictionary<string, LookupOutcome>();
            _results[column] = map;
            var targets = field.Targets?.Where(t => !string.IsNullOrEmpty(t)).ToList() ?? new List<string>();
            if (targets.Count == 0) return;

            var requests = new List<Request>();
            foreach (var v in values.Select(v => (v ?? "").Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                requests.Add(Parse(v, targets));

            // Resolve the match column for each target table
            var matchFields = new Dictionary<string, DvField>(StringComparer.OrdinalIgnoreCase);
            var tableInfo = new Dictionary<string, DvEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in targets)
            {
                DvEntity e;
                try { e = _metadata.GetEntityWithFields(t); }
                catch (Exception ex)
                {
                    MappingProblems.Add($"Couldn't read metadata for '{t}': {ex.Message}");
                    continue;
                }
                tableInfo[t] = e;
                var wanted = string.IsNullOrWhiteSpace(column.Mapping.LookupMatchField) ? e.PrimaryNameAttribute : column.Mapping.LookupMatchField.Trim();
                var mf = e.GetField(wanted);
                if (mf == null && targets.Count > 1) mf = e.GetField(e.PrimaryNameAttribute); // polymorphic: fall back per table
                if (mf == null)
                {
                    MappingProblems.Add($"Match column '{wanted}' doesn't exist on table '{t}'.");
                    continue;
                }
                if (!(mf.Kind == FieldKind.Text || mf.Kind == FieldKind.Memo || mf.Kind == FieldKind.WholeNumber || mf.Kind == FieldKind.BigInt || mf.Kind == FieldKind.UniqueId))
                {
                    MappingProblems.Add($"Match column '{mf.LogicalName}' on '{t}' is a {mf.TypeLabel} column; use a text or whole-number column.");
                    continue;
                }
                matchFields[t] = mf;
            }

            var hitsByTerm = new Dictionary<string, List<Hit>>(); // key: table|term
            var failedTables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // ---- GUID values ----
            foreach (var group in requests.Where(r => r.Id.HasValue).SelectMany(r => r.Tables.Select(t => new { t, r })).GroupBy(x => x.t))
            {
                if (!tableInfo.TryGetValue(group.Key, out var e)) continue;
                var ids = group.Select(x => x.r.Id.Value).Distinct().ToList();
                foreach (var chunk in Chunk(ids, BatchSize))
                {
                    if (cancelled()) return;
                    progress?.Invoke($"Checking {field.DisplayName}: {group.Key} by ID...");
                    try
                    {
                        foreach (var hit in Query(e, e.PrimaryIdAttribute, chunk.Cast<object>().ToArray(), null))
                            Add(hitsByTerm, group.Key + "|" + hit.Id.ToString("D").ToLowerInvariant(), hit);
                    }
                    catch (Exception ex) { failedTables[group.Key] = ex.Message; break; }
                }
            }

            // ---- Text values by match column ----
            foreach (var group in requests.Where(r => !r.Id.HasValue).SelectMany(r => r.Tables.Select(t => new { t, r })).GroupBy(x => x.t))
            {
                if (!tableInfo.TryGetValue(group.Key, out var e)) continue;
                var byField = new Dictionary<DvField, List<string>>();
                foreach (var x in group)
                {
                    var mf = MatchFieldFor(group.Key, x.r.Term, matchFields, e);
                    if (mf == null) continue;
                    if (!byField.TryGetValue(mf, out var list)) byField[mf] = list = new List<string>();
                    list.Add(x.r.Term);
                }

                foreach (var kv in byField)
                {
                    var mf = kv.Key;
                    var terms = kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var chunk in Chunk(terms, BatchSize))
                    {
                        if (cancelled()) return;
                        progress?.Invoke($"Checking {field.DisplayName}: {group.Key}.{mf.LogicalName} ({terms.Count:N0} value(s))...");
                        object[] typed;
                        if (mf.Kind == FieldKind.WholeNumber)
                            typed = chunk.Select(c => int.TryParse(c, out var n) ? (object)n : null).Where(o => o != null).ToArray();
                        else if (mf.Kind == FieldKind.BigInt)
                            typed = chunk.Select(c => long.TryParse(c, out var n) ? (object)n : null).Where(o => o != null).ToArray();
                        else if (mf.Kind == FieldKind.UniqueId)
                            typed = chunk.Select(c => Guid.TryParse(c, out var g) ? (object)g : null).Where(o => o != null).ToArray();
                        else
                            typed = chunk.Cast<object>().ToArray();
                        if (typed.Length == 0) continue;

                        try
                        {
                            foreach (var hit in Query(e, mf.LogicalName, typed, mf.LogicalName))
                                Add(hitsByTerm, group.Key + "|" + mf.LogicalName + "|" + Key(hit.MatchValue), hit);
                        }
                        catch (Exception ex) { failedTables[group.Key] = ex.Message; break; }
                    }
                }
            }

            // ---- Build outcomes ----
            var notFound = new List<KeyValuePair<Request, LookupOutcome>>();
            foreach (var r in requests)
            {
                var outcome = new LookupOutcome();
                var hits = new List<Hit>();
                var unverified = new List<string>();
                string usedField = null;
                foreach (var t in r.Tables)
                {
                    if (failedTables.TryGetValue(t, out var err)) { unverified.Add($"{t}: {err}"); continue; }
                    if (!tableInfo.TryGetValue(t, out var e)) { unverified.Add($"{t}: metadata unavailable"); continue; }
                    string key;
                    if (r.Id.HasValue) key = t + "|" + r.Id.Value.ToString("D").ToLowerInvariant();
                    else
                    {
                        var mf = MatchFieldFor(t, r.Term, matchFields, e);
                        if (mf == null) { unverified.Add($"{t}: no usable match column"); continue; }
                        usedField = usedField ?? mf.LogicalName;
                        key = t + "|" + mf.LogicalName + "|" + Key(r.Term);
                    }
                    if (hitsByTerm.TryGetValue(key, out var list)) hits.AddRange(list);
                }

                outcome.MatchField = r.Id.HasValue ? "ID" : usedField;
                outcome.MatchCount = hits.Count;
                if (hits.Count == 1)
                {
                    outcome.Status = LookupStatus.Found;
                    outcome.Table = hits[0].Table;
                    outcome.Id = hits[0].Id;
                    outcome.Name = hits[0].Name;
                    outcome.Inactive = hits[0].Inactive;
                }
                else if (hits.Count > 1)
                {
                    outcome.Status = LookupStatus.Ambiguous;
                    outcome.Table = string.Join(", ", hits.Select(h => h.Table).Distinct());
                    outcome.Message = $"{hits.Count} records match {TextUtil.Quote(r.Term)} in {outcome.Table}";
                    outcome.Suggestions.AddRange(hits.Take(3).Select(h => $"{h.Table}:{h.Id:D}"));
                }
                else if (unverified.Count == r.Tables.Count)
                {
                    outcome.Status = LookupStatus.Unverified;
                    outcome.Message = string.Join("; ", unverified);
                }
                else
                {
                    outcome.Status = LookupStatus.NotFound;
                    outcome.Table = string.Join(", ", r.Tables);
                    if (!r.Id.HasValue) notFound.Add(new KeyValuePair<Request, LookupOutcome>(r, outcome));
                }
                map[Key(r.Value)] = outcome;
            }

            // ---- "Did you mean" for values that weren't found (text match columns only) ----
            foreach (var kv in notFound.Take(MaxSuggestionQueries))
            {
                if (cancelled()) return;
                var r = kv.Key;
                foreach (var t in r.Tables)
                {
                    if (!tableInfo.TryGetValue(t, out var e)) continue;
                    var mf = MatchFieldFor(t, r.Term, matchFields, e);
                    if (mf == null || (mf.Kind != FieldKind.Text && mf.Kind != FieldKind.Memo)) continue;
                    try
                    {
                        var term = r.Term.Length > 60 ? r.Term.Substring(0, 60) : r.Term;
                        var found = Like(e, mf.LogicalName, "%" + EscapeLike(term) + "%", 3);
                        if (found.Count == 0 && term.Length >= 5)
                            found = Like(e, mf.LogicalName, EscapeLike(term.Substring(0, 4)) + "%", 3);
                        foreach (var name in found)
                            if (!kv.Value.Suggestions.Contains(name)) kv.Value.Suggestions.Add(name);
                    }
                    catch { /* suggestions are best effort */ }
                    if (kv.Value.Suggestions.Count >= 3) break;
                }
            }
        }

        private Request Parse(string value, List<string> targets)
        {
            var r = new Request { Value = value, Term = value, Tables = targets };
            var colon = value.IndexOf(':');
            if (colon > 0 && colon < value.Length - 1)
            {
                var prefix = value.Substring(0, colon).Trim();
                var table = targets.FirstOrDefault(t => string.Equals(t, prefix, StringComparison.OrdinalIgnoreCase))
                            ?? targets.FirstOrDefault(t => string.Equals(_metadata.FindEntity(t)?.DisplayName, prefix, StringComparison.OrdinalIgnoreCase));
                if (table != null)
                {
                    r.Tables = new List<string> { table };
                    r.Term = value.Substring(colon + 1).Trim();
                }
            }
            if (Guid.TryParse(r.Term.Trim('{', '}'), out var id)) r.Id = id;
            return r;
        }

        private static DvField MatchFieldFor(string table, string term, Dictionary<string, DvField> matchFields, DvEntity entity)
        {
            // Owners: an e-mail address matches users by their primary e-mail
            if (string.Equals(table, "systemuser", StringComparison.OrdinalIgnoreCase) && term.Contains("@"))
            {
                var mail = entity.GetField("internalemailaddress");
                if (mail != null) return mail;
            }
            matchFields.TryGetValue(table, out var mf);
            return mf;
        }

        private IEnumerable<Hit> Query(DvEntity e, string attribute, object[] values, string returnAttribute)
        {
            var query = new QueryExpression(e.LogicalName)
            {
                ColumnSet = new ColumnSet(new[] { e.PrimaryIdAttribute, e.PrimaryNameAttribute, returnAttribute, HasState(e) ? "statecode" : null }
                    .Where(a => !string.IsNullOrEmpty(a)).Distinct().ToArray()),
                NoLock = true,
                PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
            };
            query.Criteria.AddCondition(new ConditionExpression(attribute, ConditionOperator.In, values));

            while (true)
            {
                var page = _service.RetrieveMultiple(query);
                foreach (var rec in page.Entities)
                {
                    var keyValue = returnAttribute != null && rec.Contains(returnAttribute) ? Convert.ToString(rec[returnAttribute]) : null;
                    var name = rec.GetAttributeValue<string>(e.PrimaryNameAttribute ?? "") ?? rec.Id.ToString("D");
                    var state = rec.GetAttributeValue<OptionSetValue>("statecode");
                    yield return new Hit
                    {
                        Table = e.LogicalName,
                        Id = rec.Id,
                        Name = name,
                        MatchValue = keyValue,
                        Inactive = state != null && state.Value != 0
                    };
                }
                if (!page.MoreRecords) break;
                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = page.PagingCookie;
            }
        }

        private List<string> Like(DvEntity e, string attribute, string pattern, int top)
        {
            var query = new QueryExpression(e.LogicalName) { ColumnSet = new ColumnSet(attribute), TopCount = top, NoLock = true };
            query.Criteria.AddCondition(attribute, ConditionOperator.Like, pattern);
            return _service.RetrieveMultiple(query).Entities
                .Select(r => r.GetAttributeValue<string>(attribute))
                .Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        }

        private static string EscapeLike(string s) => s.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

        private static bool HasState(DvEntity e) => e.GetField("statecode") != null;

        private static void Add(Dictionary<string, List<Hit>> map, string key, Hit hit)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<Hit>();
            if (!list.Any(h => h.Id == hit.Id && h.Table == hit.Table)) list.Add(hit);
        }

        private static IEnumerable<List<T>> Chunk<T>(IList<T> items, int size)
        {
            for (int i = 0; i < items.Count; i += size)
                yield return items.Skip(i).Take(size).ToList();
        }
    }
}
