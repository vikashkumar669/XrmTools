using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using ChartDesignerPro.Editing;
using ChartDesignerPro.Models;
using ChartDesignerPro.Xml;

namespace ChartDesignerPro.Validation
{
    /// <summary>Ordered from least to most serious.</summary>
    public enum Severity { Passed = 0, Information = 1, Warning = 2, Error = 3 }

    public enum RuleCategory { Structure, FetchXml, Metadata, UnifiedInterface }

    /// <summary>Which part of the chart a result is about.</summary>
    public enum ValidationTarget { Data, Presentation, Chart }

    /// <summary>One finding. Results are never applied automatically (brief, section 9).</summary>
    public sealed class ValidationResult
    {
        public string RuleId { get; set; }
        public RuleCategory Category { get; set; }
        public Severity Severity { get; set; }
        public string Title { get; set; }
        public string Explanation { get; set; }
        public string Suggestion { get; set; }
        public ValidationTarget Target { get; set; }

        /// <summary>Element or attribute the result points at, e.g. "&lt;attribute name='estimatedvalue'&gt;".</summary>
        public string Location { get; set; }

        /// <summary>1-based line in the stored text, or 0 when not applicable.</summary>
        public int Line { get; set; }
        public int Column { get; set; }

        public string SeverityText
        {
            get
            {
                switch (Severity)
                {
                    case Severity.Error: return "✕ Error";
                    case Severity.Warning: return "⚠ Warning";
                    case Severity.Information: return "ℹ Info";
                    default: return "✓ Passed";
                }
            }
        }

        public override string ToString() => $"{RuleId} {Severity}: {Title}";
    }

    /// <summary>Everything a rule may look at. Built once per validation run.</summary>
    public sealed class ValidationContext
    {
        private ValidationContext() { }

        public string DataText { get; private set; }
        public string PresentationText { get; private set; }
        public XDocument Data { get; private set; }
        public XDocument Presentation { get; private set; }
        public XmlParseError DataError { get; private set; }
        public XmlParseError PresentationError { get; private set; }
        public DataDescriptionModel DataModel { get; private set; }
        public PresentationModel PresentationModel { get; private set; }

        /// <summary>The chart's table (primaryentitytypecode), when known.</summary>
        public string ChartTable { get; private set; }

        /// <summary>Metadata of the chart's table, or null when it couldn't be read (metadata rules then report "not checked").</summary>
        public TableMetadata Metadata { get; private set; }

        /// <summary>Metadata of a linked table by logical name, or null.</summary>
        public Func<string, TableMetadata> LinkedMetadata { get; private set; }

        public static ValidationContext Create(string dataText, string presentationText, string chartTable,
            TableMetadata metadata = null, Func<string, TableMetadata> linkedMetadata = null)
        {
            var ctx = new ValidationContext
            {
                DataText = dataText ?? string.Empty,
                PresentationText = presentationText ?? string.Empty,
                ChartTable = chartTable,
                Metadata = metadata,
                LinkedMetadata = linkedMetadata ?? (_ => null)
            };
            SafeXml.TryParse(ctx.DataText, out var data, out var dataError);
            SafeXml.TryParse(ctx.PresentationText, out var presentation, out var presentationError);
            ctx.Data = data;
            ctx.DataError = dataError;
            ctx.Presentation = presentation;
            ctx.PresentationError = presentationError;
            ctx.DataModel = data == null ? null : ChartDefinitionParser.ParseData(data);
            ctx.PresentationModel = presentation == null ? null : ChartDefinitionParser.ParsePresentation(presentation);
            return ctx;
        }

        public static ValidationContext For(ChartWorkspace workspace, TableMetadata metadata = null, Func<string, TableMetadata> linkedMetadata = null) =>
            Create(workspace.Data.Current, workspace.Presentation.Current, workspace.Record.TableLogicalName, metadata, linkedMetadata);
    }

    public interface IValidationRule
    {
        string Id { get; }
        RuleCategory Category { get; }

        /// <summary>Short name of what the rule checks, shown when it passes.</summary>
        string Title { get; }

        /// <summary>Findings; an empty sequence means the check passed.</summary>
        IEnumerable<ValidationResult> Check(ValidationContext context);
    }

    public sealed class ValidationReport
    {
        public ValidationReport(IReadOnlyList<ValidationResult> results) { Results = results; }

        public IReadOnlyList<ValidationResult> Results { get; }
        public int Errors => Results.Count(r => r.Severity == Severity.Error);
        public int Warnings => Results.Count(r => r.Severity == Severity.Warning);
        public int Information => Results.Count(r => r.Severity == Severity.Information);
        public int Passed => Results.Count(r => r.Severity == Severity.Passed);

        public string Summary =>
            Errors == 0 && Warnings == 0 ? $"No problems found ({Passed} checks passed, {Information} notes)"
            : $"{Errors} error(s), {Warnings} warning(s), {Information} note(s), {Passed} passed";
    }

    /// <summary>Runs rules and adds one "Passed" result for every rule that found nothing.</summary>
    public sealed class ValidationEngine
    {
        private readonly IReadOnlyList<IValidationRule> _rules;

        public ValidationEngine(IEnumerable<IValidationRule> rules = null)
        {
            _rules = (rules ?? RuleCatalog.All).ToList();
        }

        public IReadOnlyList<IValidationRule> Rules => _rules;

        public ValidationReport Run(ValidationContext context, ISet<string> disabledRuleIds = null)
        {
            var results = new List<ValidationResult>();
            foreach (var rule in _rules)
            {
                if (disabledRuleIds != null && disabledRuleIds.Contains(rule.Id)) continue;
                List<ValidationResult> found;
                try
                {
                    found = (rule.Check(context) ?? Enumerable.Empty<ValidationResult>()).ToList();
                }
                catch (Exception ex)
                {
                    found = new List<ValidationResult>
                    {
                        new ValidationResult
                        {
                            RuleId = rule.Id, Category = rule.Category, Severity = Severity.Information, Target = ValidationTarget.Chart,
                            Title = "Rule could not run: " + rule.Title,
                            Explanation = "The check stopped with an internal error: " + ex.Message,
                            Suggestion = "The other checks are unaffected. Please report this with the chart XML."
                        }
                    };
                }

                foreach (var r in found)
                {
                    r.RuleId = r.RuleId ?? rule.Id;
                    r.Category = rule.Category;
                }
                if (found.Count == 0)
                    found.Add(new ValidationResult { RuleId = rule.Id, Category = rule.Category, Severity = Severity.Passed, Title = rule.Title, Target = ValidationTarget.Chart });
                results.AddRange(found);
            }

            var ordered = results
                .OrderByDescending(r => r.Severity)
                .ThenBy(r => r.Category)
                .ThenBy(r => r.RuleId, StringComparer.Ordinal)
                .ThenBy(r => r.Line)
                .ToList();
            return new ValidationReport(ordered);
        }
    }
}
