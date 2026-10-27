using System;
using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Models;
using PluginFailureCorrelationExplorer.Rules;

namespace PluginFailureCorrelationExplorer.Analysis
{
    /// <summary>
    /// Runs the full analysis on retrieved traces: fingerprinting and grouping, performance, correlation, the baseline
    /// comparison and the diagnostic rules. Pure and deterministic for a given input and <c>analyzedUtc</c>.
    /// </summary>
    public sealed class AnalysisEngine
    {
        private readonly AnalysisOptions _options;
        private readonly DiagnosticAnalysisService _diagnostics;

        public AnalysisEngine(AnalysisOptions options, IEnumerable<IDiagnosticRule> rules = null)
        {
            _options = (options ?? new AnalysisOptions()).Clone();
            _options.Normalize();
            _diagnostics = new DiagnosticAnalysisService(rules);
            Fingerprints = new ExceptionFingerprintService(_options);
        }

        public ExceptionFingerprintService Fingerprints { get; }

        /// <summary>Signature to store with baselines (see <see cref="ExceptionFingerprintService.GroupingSignature"/>).</summary>
        public string GroupingSignature => Fingerprints.GroupingSignature;

        /// <summary>The correlation index of the last analysis, for the Trace Explorer.</summary>
        public FailureCorrelationService Correlation { get; private set; }

        public AnalysisResult Analyze(IEnumerable<PluginTraceRecord> records, TraceQuery query, OrganizationInfo organization,
            BaselineSnapshot baseline = null, DateTime? analyzedUtc = null, IEnumerable<string> retrievalNotes = null)
        {
            var list = (records ?? Enumerable.Empty<PluginTraceRecord>()).Where(r => r != null)
                .GroupBy(r => r.Id).Select(g => g.First())               // duplicates from paging edges are ignored
                .OrderByDescending(r => r.CreatedOnUtc).ThenBy(r => r.Id).ToList();

            var result = new AnalysisResult { Options = _options, Records = list };
            var s = result.Summary;
            s.AnalyzedUtc = analyzedUtc ?? DateTime.UtcNow;
            s.Query = query?.Clone() ?? new TraceQuery();
            s.Organization = organization ?? new OrganizationInfo();
            if (retrievalNotes != null) s.RetrievalNotes.AddRange(retrievalNotes.Where(n => !string.IsNullOrWhiteSpace(n)));
            foreach (var pair in Fingerprints.Normalizer.InvalidRules) result.InvalidRules[pair.Key] = pair.Value;

            result.FailureGroups = Fingerprints.Group(list, result.Fingerprints, result.GroupKeys);
            var performance = new PerformanceAnalysisService(_options);
            result.PerformanceGroups = performance.Analyze(list);

            Correlation = new FailureCorrelationService(list, _options, result.GroupKeys);
            result.Clusters = Correlation.Clusters.ToList();

            s.TotalTraces = list.Count;
            s.TracesWithException = list.Count(r => r.HasException);
            s.UniqueFailureGroups = result.FailureGroups.Count;
            s.RecurringFailureGroups = result.FailureGroups.Count(g => g.Occurrences >= _options.RecurringMinOccurrences);
            s.SlowExecutions = list.Count(performance.IsSlow);
            s.MeasuredExecutions = list.Count(r => r.ExecutionDurationMs.HasValue);
            s.CompleteEvidence = list.Count(r => r.HasCompleteEvidence);
            s.IncompleteEvidence = list.Count - s.CompleteEvidence;
            foreach (var r in list)
                foreach (var missing in r.MissingIdentifiers)
                    s.MissingIdentifierCounts[missing] = s.MissingIdentifierCounts.TryGetValue(missing, out var n) ? n + 1 : 1;

            result.Trend = baseline == null
                ? TrendComparison.Unavailable("No baseline was selected, so trend analysis is unavailable. Choose a previous period or a saved snapshot to compare with.")
                : BaselineService.Compare(result, baseline, GroupingSignature, _options);

            result.Findings = _diagnostics.Run(new AnalysisContext(result, Correlation));
            return result;
        }
    }
}
