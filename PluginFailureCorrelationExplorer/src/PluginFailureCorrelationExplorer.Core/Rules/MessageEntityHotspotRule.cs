using System;
using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Rules
{
    /// <summary>PFC004: failures concentrated on one message and table.</summary>
    public sealed class MessageEntityHotspotRule : IDiagnosticRule
    {
        /// <summary>At most this many hotspots are reported, largest first.</summary>
        public const int MaxFindings = 5;

        public string RuleId => "PFC004";
        public string Name => "Message and table hotspot";
        public string Description => "A message + table combination with at least the recurring threshold of failures spread over two or more failure groups or plug-in types (a single recurring failure is already covered by PFC001). Severity rises when 25% or more of its traces failed.";

        public IEnumerable<DiagnosticFinding> Evaluate(AnalysisContext context)
        {
            var o = context.Options;
            var pairs = context.Records
                .Where(r => !string.IsNullOrWhiteSpace(r.MessageName))
                .GroupBy(r => (Message: r.MessageName.ToLowerInvariant(), Table: (r.PrimaryEntity ?? "").ToLowerInvariant()))
                .Select(g => new { g.Key, All = g.ToList(), Failed = g.Where(r => r.HasException).ToList() })
                .Where(x => x.Failed.Count >= o.RecurringMinOccurrences)
                .Where(x => x.Failed.Select(t => context.Result.GroupKeys.TryGetValue(t.Id, out var k) ? k : t.Id.ToString()).Distinct().Count() >= 2
                         || x.Failed.Select(t => t.FullTypeName ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2)
                .OrderByDescending(x => x.Failed.Count)
                .Take(MaxFindings);

            foreach (var x in pairs)
            {
                var message = x.Failed[0].MessageName;
                var table = string.IsNullOrEmpty(x.Failed[0].PrimaryEntity) ? "(no table)" : x.Failed[0].PrimaryEntity;
                var share = (double)x.Failed.Count / x.All.Count;
                var groups = x.Failed.Select(t => context.Result.GroupKeys.TryGetValue(t.Id, out var k) ? k : null).Where(k => k != null).Distinct().Count();
                var types = x.Failed.Select(t => t.FullTypeName).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var f = FindingHelper.New(this, share >= 0.25 ? Severity.Warning : Severity.Information, Confidence.High,
                    $"Failures concentrated on {message} of {table}",
                    $"{Fmt.Count(x.Failed.Count)} of {Fmt.Count(x.All.Count)} retrieved traces for {message} on {table} recorded an exception, across {Fmt.Plural(groups, "failure group")} and {Fmt.Plural(types.Count, "plug-in type")}.");
                f.Facts.Add($"Failed traces: {Fmt.Count(x.Failed.Count)}; all traces for this message and table: {Fmt.Count(x.All.Count)}.");
                f.Facts.Add($"Plug-in types with failures: {Fmt.List(types.Select(TypeNames.ShortName))}.");
                f.Metrics.Add($"Failure share of retrieved traces: {Fmt.Percent(x.Failed.Count, x.All.Count)}.");
                if (context.Result.Summary.Query.Exceptions == ExceptionFilter.WithException)
                    f.Metrics.Add("Only traces with an exception were retrieved, so the failure share isn't meaningful for this analysis.");
                else if (context.Result.Summary.Organization.TraceSetting == "Exception")
                    f.Metrics.Add("Trace logging is set to Exception only, so successful executions aren't logged and the failure share is overstated.");
                if (groups > 1)
                    f.PossibleExplanations.Add("Several distinct failures on the same message and table can point to a shared input, such as a particular data pattern or a related customization, rather than to one plug-in defect.");
                f.Recommendations.Add($"List every step registered on {message} of {table} and check their order (stage and rank) and filtering attributes.");
                f.Recommendations.Add("Compare the input data of failing and succeeding executions, if trace output records it.");
                FindingHelper.AddTraces(f, x.Failed);
                yield return f;
            }
        }
    }
}
