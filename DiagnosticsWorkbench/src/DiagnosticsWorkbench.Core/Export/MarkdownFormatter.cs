using System.Linq;
using System.Text;
using DiagnosticsWorkbench.Analysis;
using DiagnosticsWorkbench.Models;

namespace DiagnosticsWorkbench.Export
{
    /// <summary>Plain-text snippets for tickets, chat and pull requests ("Copy as Markdown").</summary>
    public static class MarkdownFormatter
    {
        public static string ForLog(TraceLog log, StepInfo step, CorrelationChain chain, PerformanceAnalyzer perf, string environment)
        {
            if (log == null) return "";
            var sb = new StringBuilder();
            sb.Append("### ").Append(log.IsError ? "Plugin failure: " : "Plugin execution: ").Append(log.ShortTypeName).Append(" on ")
              .Append(log.MessageName).Append(" of ").Append(string.IsNullOrEmpty(log.PrimaryEntity) ? "(no table)" : log.PrimaryEntity).Append("\n\n");

            sb.Append("| | |\n|---|---|\n");
            if (!string.IsNullOrEmpty(environment)) Line(sb, "Environment", environment);
            Line(sb, "When (UTC)", Formatting.Iso(log.CreatedOn));
            Line(sb, "Plugin type", "`" + log.FullTypeName + "`");
            Line(sb, "Mode · depth", log.ModeText + " · " + log.Depth);
            var duration = Formatting.Duration(log.ExecutionDurationMs);
            if (perf != null && log.ExecutionDurationMs.HasValue)
                duration += perf.IsNearTimeout(log) ? " (near timeout)" : perf.IsSlow(log) ? $" (slow, threshold {Formatting.Duration(perf.ThresholdFor(log))})" : "";
            Line(sb, "Duration", duration);
            if (step != null)
            {
                Line(sb, "Step", $"{step.Name} ({step.StageText}, {step.ModeText}, rank {step.Rank})");
                Line(sb, "Filtering attributes", step.HasFilteringAttributes ? step.FilteringAttributes : "(all columns)");
                if (!string.IsNullOrEmpty(step.AssemblyName)) Line(sb, "Assembly", step.AssemblyName + (string.IsNullOrEmpty(step.AssemblyVersion) ? "" : " " + step.AssemblyVersion));
            }
            if (log.CorrelationId.HasValue) Line(sb, "Correlation ID", "`" + log.CorrelationId + "`");
            if (log.RequestId.HasValue) Line(sb, "Request ID", "`" + log.RequestId + "`");
            Line(sb, "Trace log ID", "`" + log.Id + "`");

            if (log.IsError)
            {
                sb.Append("\n**Error:** ").Append(ExceptionNormalizer.Headline(log.ExceptionDetails)).Append("\n\n```\n")
                  .Append(Formatting.Truncate(log.ExceptionDetails.Trim(), 6000)).Append("\n```\n");
            }
            if (!string.IsNullOrWhiteSpace(log.MessageBlock))
                sb.Append("\n**Trace output**\n\n```\n").Append(Formatting.Truncate(log.MessageBlock.Trim(), 4000)).Append("\n```\n");

            if (chain != null && chain.Entries.Count > 1)
            {
                sb.Append("\n**Chain** (").Append(chain.Entries.Count).Append(" steps, span ").Append(Formatting.Duration(chain.SpanMs)).Append(")\n\n");
                foreach (var e in chain.Entries)
                    sb.Append(new string(' ', System.Math.Max(0, e.Depth - 1) * 2)).Append("- ").Append(e.Id == log.Id ? "**" : "")
                      .Append("d").Append(e.Depth).Append(' ').Append(e.ShortTypeName).Append(" · ").Append(e.MessageName).Append(" · ")
                      .Append(Formatting.Duration(e.ExecutionDurationMs)).Append(e.IsError ? " · FAILED" : "").Append(e.Id == log.Id ? "**" : "").Append('\n');
            }
            return sb.ToString();
        }

        public static string ForGroup(ErrorGroup group)
        {
            if (group == null) return "";
            var sb = new StringBuilder();
            sb.Append("### Recurring error #").Append(group.Fingerprint).Append(" (").Append(group.Count).Append(" hits)\n\n");
            sb.Append("| | |\n|---|---|\n");
            Line(sb, "Plugin type", "`" + group.TypeName + "`");
            Line(sb, "Message · table", group.MessageName + " · " + group.PrimaryEntity);
            Line(sb, "First seen (UTC)", Formatting.Iso(group.FirstSeen));
            Line(sb, "Last seen (UTC)", Formatting.Iso(group.LastSeen));
            Line(sb, "Executions", group.DistinctExecutions.ToString());
            sb.Append("\n**Error:** ").Append(group.Headline).Append('\n');
            if (group.Flags.Any())
            {
                sb.Append("\n**Configuration flags**\n\n");
                foreach (var f in group.Flags) sb.Append("- ").Append(f.Message).Append('\n');
            }
            sb.Append("\n```\n").Append(Formatting.Truncate((group.SampleException ?? "").Trim(), 6000)).Append("\n```\n");
            return sb.ToString();
        }

        private static void Line(StringBuilder sb, string name, string value) =>
            sb.Append("| ").Append(name).Append(" | ").Append((value ?? "").Replace("|", "\\|").Replace("\n", " ")).Append(" |\n");
    }
}
