using System;
using System.Collections.Generic;
using System.Linq;
using DiagnosticsWorkbench.Models;

namespace DiagnosticsWorkbench.Analysis
{
    /// <summary>
    /// Links logs that share a correlation ID. Logs without one stand alone: links are never guessed from timing.
    /// </summary>
    public static class CorrelationBuilder
    {
        public static List<CorrelationChain> Build(IEnumerable<TraceLog> logs, int minimumEntries = 1)
        {
            var chains = new List<CorrelationChain>();
            var withId = (logs ?? Enumerable.Empty<TraceLog>()).Where(l => l?.CorrelationId != null && l.CorrelationId.Value != Guid.Empty);

            foreach (var group in withId.GroupBy(l => l.CorrelationId.Value))
            {
                var entries = group
                    .OrderBy(l => l.ExecutionStartTime ?? l.CreatedOn)
                    .ThenBy(l => l.Depth)
                    .ThenBy(l => l.CreatedOn)
                    .ToList();
                if (entries.Count < minimumEntries) continue;

                var start = entries.Min(StartOf);
                var end = entries.Max(EndOf);
                var errors = entries.Count(e => e.IsError);

                chains.Add(new CorrelationChain
                {
                    CorrelationId = group.Key,
                    Entries = entries,
                    Start = start,
                    End = end,
                    SpanMs = Math.Max(0, (long)(end - start).TotalMilliseconds),
                    MaxDepth = entries.Max(e => e.Depth),
                    ErrorCount = errors,
                    Failed = errors > 0,
                    RequestCount = entries.Select(e => e.RequestId ?? Guid.Empty).Where(g => g != Guid.Empty).Distinct().Count(),
                });
            }

            return chains
                .OrderByDescending(c => c.Failed)
                .ThenByDescending(c => c.Entries.Count)
                .ThenByDescending(c => c.Start)
                .ToList();
        }

        /// <summary>Start of an execution: performanceexecutionstarttime, else createdon minus duration.</summary>
        public static DateTime StartOf(TraceLog log)
        {
            if (log.ExecutionStartTime.HasValue) return log.ExecutionStartTime.Value;
            return log.ExecutionDurationMs.HasValue ? log.CreatedOn.AddMilliseconds(-log.ExecutionDurationMs.Value) : log.CreatedOn;
        }

        /// <summary>End of an execution: start plus duration, else createdon.</summary>
        public static DateTime EndOf(TraceLog log)
        {
            if (log.ExecutionStartTime.HasValue && log.ExecutionDurationMs.HasValue)
                return log.ExecutionStartTime.Value.AddMilliseconds(log.ExecutionDurationMs.Value);
            return log.CreatedOn;
        }
    }
}
