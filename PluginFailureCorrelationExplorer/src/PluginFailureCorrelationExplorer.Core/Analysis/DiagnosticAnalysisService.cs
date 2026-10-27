using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PluginFailureCorrelationExplorer.Models;
using PluginFailureCorrelationExplorer.Rules;

namespace PluginFailureCorrelationExplorer.Analysis
{
    /// <summary>Runs diagnostic rules and orders their findings. A rule that throws never stops the others.</summary>
    public sealed class DiagnosticAnalysisService
    {
        private readonly List<IDiagnosticRule> _rules;

        public DiagnosticAnalysisService(IEnumerable<IDiagnosticRule> rules = null)
        {
            _rules = (rules ?? DefaultRules()).ToList();
            var duplicate = _rules.GroupBy(r => r.RuleId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null) throw new ArgumentException("Duplicate rule ID " + duplicate.Key, nameof(rules));
        }

        public IReadOnlyList<IDiagnosticRule> Rules => _rules;

        /// <summary>The built-in rules, in rule ID order.</summary>
        public static IEnumerable<IDiagnosticRule> DefaultRules()
        {
            yield return new RecurringFailureRule();
            yield return new HighFrequencyFailureRule();
            yield return new SlowExecutionRule();
            yield return new MessageEntityHotspotRule();
            yield return new IncompleteEvidenceRule();
            yield return new IncreasingFailureRule();
            yield return new PerformanceRegressionRule();
            yield return new CorrelatedFailureChainRule();
            yield return new SuspectedRecursionRule();
        }

        /// <summary>Evaluates every rule. Findings are ordered by severity, confidence, rule and title, then numbered F-001…</summary>
        public List<DiagnosticFinding> Run(AnalysisContext context)
        {
            var findings = new List<DiagnosticFinding>();
            foreach (var rule in _rules)
            {
                try
                {
                    findings.AddRange(rule.Evaluate(context).Where(f => f != null));
                }
                catch (Exception ex)
                {
                    var f = new DiagnosticFinding
                    {
                        RuleId = rule.RuleId,
                        Severity = Severity.Information,
                        Confidence = Confidence.Low,
                        Title = $"Rule {rule.RuleId} ({rule.Name}) could not be evaluated",
                        Description = "The rule failed on this data and was skipped. Other findings are unaffected.",
                    };
                    f.Facts.Add(ex.GetType().Name + ": " + ex.Message);
                    findings.Add(f);
                }
            }

            var ordered = findings
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Confidence)
                .ThenBy(f => f.RuleId, StringComparer.Ordinal)
                .ThenBy(f => f.Title, StringComparer.Ordinal)
                .ToList();
            for (var i = 0; i < ordered.Count; i++) ordered[i].Id = "F-" + (i + 1).ToString("000", CultureInfo.InvariantCulture);
            return ordered;
        }
    }
}
