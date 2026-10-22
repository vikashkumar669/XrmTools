using System;
using System.Collections.Generic;
using System.Linq;
using DiagnosticsWorkbench.Models;

namespace DiagnosticsWorkbench.Analysis
{
    /// <summary>In-memory filters applied to the loaded logs (no Dataverse round-trip).</summary>
    public sealed class ViewFilter
    {
        public bool ErrorsOnly { get; set; }
        public bool SlowOnly { get; set; }

        /// <summary>Case-insensitive text matched against type, message, table, exception and trace output.</summary>
        public string Search { get; set; }

        public bool IsEmpty => !ErrorsOnly && !SlowOnly && string.IsNullOrWhiteSpace(Search);
    }

    /// <summary>What the query asked Dataverse for; carried into reports.</summary>
    public sealed class QueryDescription
    {
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public string Table { get; set; }
        public string Message { get; set; }
        public string PluginType { get; set; }
        public int RowCap { get; set; }
        public bool CapReached { get; set; }

        public string FiltersText
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Table)) parts.Add("table = " + Table);
                if (!string.IsNullOrWhiteSpace(Message)) parts.Add("message = " + Message);
                if (!string.IsNullOrWhiteSpace(PluginType)) parts.Add("plugin type contains " + PluginType);
                return parts.Count == 0 ? "none" : string.Join(", ", parts);
            }
        }
    }

    /// <summary>Everything the views and reports show for the current filters.</summary>
    public sealed class AnalysisResult
    {
        public List<TraceLog> Logs { get; set; } = new List<TraceLog>();
        public List<ErrorGroup> ErrorGroups { get; set; } = new List<ErrorGroup>();
        public List<PerformanceStat> Performance { get; set; } = new List<PerformanceStat>();
        public List<CorrelationChain> Chains { get; set; } = new List<CorrelationChain>();
        public int ErrorCount { get; set; }
        public int SlowCount { get; set; }
        public int NearTimeoutCount { get; set; }
        public int FailedChainCount => Chains.Count(c => c.Failed);
    }

    /// <summary>
    /// Owns the loaded logs, step metadata and settings. The UI asks it to analyze; only the repository talks to Dataverse.
    /// </summary>
    public sealed class DiagnosticsSession
    {
        private WorkbenchSettings _settings = new WorkbenchSettings();
        private ExceptionNormalizer _normalizer = new ExceptionNormalizer();
        private PerformanceAnalyzer _performance;
        private readonly Dictionary<Guid, ErrorGrouper.Signature> _signatures = new Dictionary<Guid, ErrorGrouper.Signature>();

        public DiagnosticsSession()
        {
            _performance = new PerformanceAnalyzer(_settings);
        }

        public List<TraceLog> AllLogs { get; private set; } = new List<TraceLog>();
        public Dictionary<Guid, StepInfo> Steps { get; private set; } = new Dictionary<Guid, StepInfo>();
        public QueryDescription Query { get; private set; }
        public TraceSetting TraceSetting { get; set; } = TraceSetting.Unknown;
        public string EnvironmentName { get; set; }
        public string EnvironmentUrl { get; set; }
        public DateTime? LoadedAtUtc { get; private set; }

        public WorkbenchSettings Settings
        {
            get => _settings;
            set
            {
                _settings = (value ?? new WorkbenchSettings()).Clone();
                _settings.Normalize();
                _normalizer = new ExceptionNormalizer(_settings.CustomRules);
                _performance = new PerformanceAnalyzer(_settings);
                _signatures.Clear();
            }
        }

        public ExceptionNormalizer Normalizer => _normalizer;
        public PerformanceAnalyzer Performance => _performance;
        public bool HasData => LoadedAtUtc.HasValue;

        public void SetLogs(IEnumerable<TraceLog> logs, QueryDescription query)
        {
            AllLogs = (logs ?? Enumerable.Empty<TraceLog>()).Where(l => l != null).OrderByDescending(l => l.CreatedOn).ToList();
            Query = query;
            LoadedAtUtc = DateTime.UtcNow;
            Steps = new Dictionary<Guid, StepInfo>();
            _signatures.Clear();
        }

        public void SetSteps(IEnumerable<StepInfo> steps)
        {
            Steps = new Dictionary<Guid, StepInfo>();
            foreach (var step in steps ?? Enumerable.Empty<StepInfo>())
                if (step != null) Steps[step.Id] = step;
        }

        public IEnumerable<Guid> StepIds => AllLogs.Where(l => l.StepId.HasValue && l.StepId.Value != Guid.Empty).Select(l => l.StepId.Value).Distinct();

        public StepInfo StepFor(TraceLog log) =>
            log?.StepId != null && Steps.TryGetValue(log.StepId.Value, out var step) ? step : null;

        public void Clear()
        {
            AllLogs = new List<TraceLog>();
            Steps = new Dictionary<Guid, StepInfo>();
            Query = null;
            LoadedAtUtc = null;
            _signatures.Clear();
        }

        public bool Matches(TraceLog log, ViewFilter filter)
        {
            if (filter == null || filter.IsEmpty) return true;
            if (filter.ErrorsOnly && !log.IsError) return false;
            if (filter.SlowOnly && !_performance.IsSlow(log)) return false;
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var term = filter.Search.Trim();
                bool Has(string s) => s != null && s.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!(Has(log.TypeName) || Has(log.MessageName) || Has(log.PrimaryEntity) || Has(log.ExceptionDetails) ||
                      Has(log.MessageBlock) || Has(log.CorrelationId?.ToString()) || Has(log.RequestId?.ToString()) || Has(log.Id.ToString())))
                    return false;
            }
            return true;
        }

        /// <summary>Re-groups the loaded set for the given filter. Chains keep all their entries so a cascade is never cut in half.</summary>
        public AnalysisResult Analyze(ViewFilter filter = null)
        {
            var logs = AllLogs.Where(l => Matches(l, filter)).ToList();
            var result = new AnalysisResult
            {
                Logs = logs,
                ErrorGroups = ErrorGrouper.Group(logs, _normalizer, _signatures),
                Performance = _performance.Analyze(logs, Steps),
                ErrorCount = logs.Count(l => l.IsError),
                SlowCount = logs.Count(_performance.IsSlow),
                NearTimeoutCount = logs.Count(_performance.IsNearTimeout),
            };

            var chainIds = new HashSet<Guid>(logs.Where(l => l.CorrelationId.HasValue).Select(l => l.CorrelationId.Value));
            // A chain links several executions; a single execution is already in Errors / All logs.
            result.Chains = CorrelationBuilder.Build(AllLogs.Where(l => l.CorrelationId.HasValue && chainIds.Contains(l.CorrelationId.Value)), minimumEntries: 2);

            ConfigFlagger.Apply(Steps, result.ErrorGroups, result.Performance, result.Chains);
            return result;
        }

        public CorrelationChain ChainFor(TraceLog log, AnalysisResult result) =>
            log?.CorrelationId == null ? null : result?.Chains.FirstOrDefault(c => c.CorrelationId == log.CorrelationId.Value);
    }
}
