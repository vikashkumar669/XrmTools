using System;
using System.Collections.Generic;
using System.Linq;
using DiagnosticsWorkbench.Models;

namespace DiagnosticsWorkbench.Analysis
{
    /// <summary>Finds configuration smells in step registrations and chains.</summary>
    public static class ConfigFlagger
    {
        /// <summary>Flags that come from the step registration alone.</summary>
        public static List<ConfigFlag> ForStep(StepInfo step)
        {
            var flags = new List<ConfigFlag>();
            if (step == null) return flags;
            var name = string.IsNullOrEmpty(step.Name) ? step.PluginTypeName : step.Name;

            if (string.Equals(step.MessageName, "Update", StringComparison.OrdinalIgnoreCase) && !step.HasFilteringAttributes)
                flags.Add(new ConfigFlag
                {
                    Kind = ConfigFlagKind.UpdateWithoutFilteringAttributes,
                    StepId = step.Id,
                    StepName = name,
                    Message = $"Update step '{name}' has no filtering attributes, so it runs on every update of {Table(step)}.",
                });

            foreach (var image in step.Images ?? new List<StepImage>())
                if (image.AllColumns)
                    flags.Add(new ConfigFlag
                    {
                        Kind = ConfigFlagKind.ImageWithAllColumns,
                        StepId = step.Id,
                        StepName = name,
                        Message = $"{image.ImageTypeText} '{image.Name}' on step '{name}' has no column list, so it retrieves the whole record.",
                    });

            if (step.StateCode == 1)
                flags.Add(new ConfigFlag
                {
                    Kind = ConfigFlagKind.StepDisabled,
                    StepId = step.Id,
                    StepName = name,
                    Message = $"Step '{name}' is disabled now; these logs were written while it was enabled.",
                });

            return flags;
        }

        /// <summary>The same step running at two or more depths inside one chain: it is probably triggering itself.</summary>
        public static List<ConfigFlag> ForChain(CorrelationChain chain, IDictionary<Guid, StepInfo> steps)
        {
            var flags = new List<ConfigFlag>();
            if (chain == null) return flags;

            foreach (var byStep in chain.Entries.Where(e => e.StepId.HasValue).GroupBy(e => e.StepId.Value))
            {
                var depths = byStep.Select(e => e.Depth).Distinct().OrderBy(d => d).ToList();
                if (depths.Count < 2 || depths[depths.Count - 1] < 2) continue;

                StepInfo step = null;
                steps?.TryGetValue(byStep.Key, out step);
                var name = step?.Name ?? byStep.First().ShortTypeName;
                flags.Add(new ConfigFlag
                {
                    Kind = ConfigFlagKind.PossibleRecursion,
                    StepId = byStep.Key,
                    StepName = name,
                    Message = $"Step '{name}' ran at depths {string.Join(", ", depths)} in one chain: it may be re-triggering itself.",
                });
            }
            return flags;
        }

        /// <summary>Attaches step and chain flags to groups, stats and chains.</summary>
        public static void Apply(IDictionary<Guid, StepInfo> steps, IEnumerable<ErrorGroup> groups, IEnumerable<PerformanceStat> stats, IEnumerable<CorrelationChain> chains)
        {
            var stepFlags = new Dictionary<Guid, List<ConfigFlag>>();
            if (steps != null)
                foreach (var pair in steps) stepFlags[pair.Key] = ForStep(pair.Value);

            var chainList = (chains ?? Enumerable.Empty<CorrelationChain>()).ToList();
            var recursionByStep = new Dictionary<Guid, ConfigFlag>();
            var recursiveChains = new HashSet<Guid>();
            foreach (var chain in chainList)
            {
                chain.Flags = ForChain(chain, steps);
                foreach (var entry in chain.Entries)
                    if (entry.StepId.HasValue && stepFlags.TryGetValue(entry.StepId.Value, out var f))
                        foreach (var flag in f) if (!chain.Flags.Contains(flag)) chain.Flags.Add(flag);
                foreach (var flag in chain.Flags.Where(x => x.Kind == ConfigFlagKind.PossibleRecursion))
                {
                    if (!recursionByStep.ContainsKey(flag.StepId)) recursionByStep[flag.StepId] = flag;
                    recursiveChains.Add(chain.CorrelationId);
                }
            }

            // Registration flags apply to every log of the step; the recursion flag only to logs whose own execution recursed.
            List<ConfigFlag> FlagsFor(IEnumerable<TraceLog> logs)
            {
                var list = logs.ToList();
                var result = new List<ConfigFlag>();
                foreach (var id in list.Where(l => l.StepId.HasValue).Select(l => l.StepId.Value).Distinct())
                {
                    if (stepFlags.TryGetValue(id, out var f)) foreach (var flag in f) if (!result.Contains(flag)) result.Add(flag);
                    var recursed = list.Any(l => l.StepId == id && l.CorrelationId.HasValue && recursiveChains.Contains(l.CorrelationId.Value));
                    if (recursed && recursionByStep.TryGetValue(id, out var r) && !result.Contains(r)) result.Add(r);
                }
                return result;
            }

            foreach (var group in groups ?? Enumerable.Empty<ErrorGroup>()) group.Flags = FlagsFor(group.Logs);
            foreach (var stat in stats ?? Enumerable.Empty<PerformanceStat>()) stat.Flags = FlagsFor(stat.Logs);
        }

        private static string Table(StepInfo step) => string.IsNullOrEmpty(step.PrimaryEntity) || step.PrimaryEntity == "none" ? "any table" : step.PrimaryEntity;
    }
}
