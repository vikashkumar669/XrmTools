using System;
using System.Collections.Generic;
using System.Linq;
using DiagnosticsWorkbench.Models;

namespace DiagnosticsWorkbench.Analysis
{
    /// <summary>Groups failed executions by fingerprint.</summary>
    public static class ErrorGrouper
    {
        /// <summary>Headline, normalized text and fingerprint of one log, cached so re-grouping on a filter change is instant.</summary>
        public sealed class Signature
        {
            public string Headline { get; set; }
            public string Normalized { get; set; }
            public string Fingerprint { get; set; }
        }

        public static Signature SignatureOf(TraceLog log, ExceptionNormalizer normalizer)
        {
            var headline = ExceptionNormalizer.Headline(log.ExceptionDetails);
            var normalized = normalizer.NormalizeText(headline);
            return new Signature
            {
                Headline = headline,
                Normalized = normalized,
                Fingerprint = ExceptionNormalizer.Fingerprint(log.FullTypeName, log.MessageName, normalized),
            };
        }

        public static List<ErrorGroup> Group(IEnumerable<TraceLog> logs, ExceptionNormalizer normalizer, IDictionary<Guid, Signature> cache = null)
        {
            if (normalizer == null) throw new ArgumentNullException(nameof(normalizer));
            var groups = new Dictionary<string, ErrorGroup>(StringComparer.Ordinal);

            foreach (var log in (logs ?? Enumerable.Empty<TraceLog>()).Where(l => l != null && l.IsError))
            {
                Signature sig = null;
                if (cache != null && log.Id != Guid.Empty && cache.TryGetValue(log.Id, out var cached)) sig = cached;
                if (sig == null)
                {
                    sig = SignatureOf(log, normalizer);
                    if (cache != null && log.Id != Guid.Empty) cache[log.Id] = sig;
                }
                var headline = sig.Headline;
                var normalized = sig.Normalized;
                var fingerprint = sig.Fingerprint;

                if (!groups.TryGetValue(fingerprint, out var group))
                {
                    group = new ErrorGroup
                    {
                        Fingerprint = fingerprint,
                        TypeName = log.FullTypeName,
                        MessageName = log.MessageName,
                        PrimaryEntity = log.PrimaryEntity,
                        NormalizedText = normalized,
                        FirstSeen = log.CreatedOn,
                        LastSeen = log.CreatedOn,
                    };
                    groups.Add(fingerprint, group);
                }

                group.Logs.Add(log);
                if (log.CreatedOn < group.FirstSeen) group.FirstSeen = log.CreatedOn;
                if (log.CreatedOn >= group.LastSeen || group.SampleException == null)
                {
                    group.LastSeen = log.CreatedOn > group.LastSeen ? log.CreatedOn : group.LastSeen;
                    group.SampleException = log.ExceptionDetails;
                    group.Headline = headline;
                }
                if (!string.Equals(group.PrimaryEntity, log.PrimaryEntity, StringComparison.OrdinalIgnoreCase)) group.PrimaryEntity = "(several tables)";

                var day = log.CreatedOn.Date;
                group.HitsPerDay.TryGetValue(day, out var hits);
                group.HitsPerDay[day] = hits + 1;
            }

            foreach (var group in groups.Values)
            {
                group.Logs.Sort((a, b) => b.CreatedOn.CompareTo(a.CreatedOn));
                group.DistinctExecutions = group.Logs.Select(l => l.CorrelationId ?? l.Id).Distinct().Count();
            }

            return groups.Values
                .OrderByDescending(g => g.Count)
                .ThenByDescending(g => g.LastSeen)
                .ToList();
        }
    }
}
