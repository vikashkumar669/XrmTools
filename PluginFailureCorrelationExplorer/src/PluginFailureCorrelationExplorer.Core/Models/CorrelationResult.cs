using System;
using System.Collections.Generic;
using System.Linq;

namespace PluginFailureCorrelationExplorer.Models
{
    /// <summary>
    /// How strong the evidence for a relationship between two traces is. Ordered from strongest to weakest.
    /// </summary>
    public enum EvidenceLevel
    {
        /// <summary>Same request ID: an exact identifier match for one message request.</summary>
        ExactRequest = 0,

        /// <summary>Same correlation ID: the same causal chain, which may span several operations and async continuations.</summary>
        SameCausalChain = 1,

        /// <summary>Inferred parent → child inside one causal chain (depth + timing). Never confirmed.</summary>
        SuspectedParentChild = 2,

        /// <summary>No shared identifier: close in time with matching context. A lead, not proof.</summary>
        Heuristic = 3,

        /// <summary>Same failure fingerprint: recurrence of the same failure, not a shared transaction.</summary>
        SamePattern = 4,
    }

    /// <summary>Individual signals that contribute to a correlation.</summary>
    [Flags]
    public enum CorrelationSignal
    {
        None = 0,
        RequestId = 1,
        CorrelationId = 2,
        DepthOrder = 4,
        TimeContainment = 8,
        TimeProximity = 16,
        SamePluginType = 32,
        SameMessage = 64,
        SamePrimaryEntity = 128,
        SameFingerprint = 256,
        SameStep = 512,
    }

    /// <summary>A trace related to a selected trace, with the evidence for the relationship.</summary>
    public sealed class RelatedTrace
    {
        public PluginTraceRecord Trace { get; set; }
        public EvidenceLevel Level { get; set; }
        public CorrelationSignal Signals { get; set; }

        /// <summary>Plain-language explanation of which signals produced the relationship and what they do and don't prove.</summary>
        public string Explanation { get; set; }

        /// <summary>For suspected parent/child: "parent" or "child" relative to the selected trace.</summary>
        public string Role { get; set; }
    }

    /// <summary>All traces sharing one correlation ID, ordered by start time then depth.</summary>
    public sealed class CorrelationCluster
    {
        public Guid CorrelationId { get; set; }
        public List<PluginTraceRecord> Traces { get; } = new List<PluginTraceRecord>();

        /// <summary>Suspected parent → child edges inside the cluster (inferred, never confirmed).</summary>
        public List<(PluginTraceRecord Parent, PluginTraceRecord Child, CorrelationSignal Signals)> SuspectedEdges { get; } =
            new List<(PluginTraceRecord, PluginTraceRecord, CorrelationSignal)>();

        public int FailureCount => Traces.Count(t => t.HasException);
        public int DistinctRequests => Traces.Where(t => t.RequestId.HasValue).Select(t => t.RequestId.Value).Distinct().Count();
        public int MaxDepth => Traces.Max(t => t.Depth ?? 0);
        public IReadOnlyList<string> PluginTypes => Traces.Select(t => t.FullTypeName).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        public DateTime FirstUtc => Traces.Min(t => t.StartUtc);
        public DateTime LastUtc => Traces.Max(t => t.EndUtc);
    }

    /// <summary>Plain-language labels for evidence levels.</summary>
    public static class EvidenceText
    {
        public static string Label(EvidenceLevel level)
        {
            switch (level)
            {
                case EvidenceLevel.ExactRequest: return "Exact match (request ID)";
                case EvidenceLevel.SameCausalChain: return "Same causal chain (correlation ID)";
                case EvidenceLevel.SuspectedParentChild: return "Suspected parent/child";
                case EvidenceLevel.Heuristic: return "Heuristic (no shared identifier)";
                case EvidenceLevel.SamePattern: return "Same failure pattern";
                default: return level.ToString();
            }
        }

        /// <summary>What the level does and doesn't establish.</summary>
        public static string Meaning(EvidenceLevel level)
        {
            switch (level)
            {
                case EvidenceLevel.ExactRequest:
                    return "Both traces carry the same request ID, so they belong to the same message request.";
                case EvidenceLevel.SameCausalChain:
                    return "Both traces carry the same correlation ID, so they are part of one causal chain. That chain can span several operations and asynchronous continuations, so this does not prove they are the same immediate operation.";
                case EvidenceLevel.SuspectedParentChild:
                    return "Inferred from depth and timing inside one causal chain. Dataverse does not record parent/child links, so this is a suspicion, not a confirmed relationship.";
                case EvidenceLevel.Heuristic:
                    return "No identifier is shared. The traces are close in time and share context. Treat this as a lead to check, not as proof of a shared transaction.";
                case EvidenceLevel.SamePattern:
                    return "Both traces have the same failure fingerprint. This shows recurrence of the same failure, not that they belong to the same transaction.";
                default:
                    return "";
            }
        }

        public static string Signals(CorrelationSignal signals)
        {
            var names = new List<string>();
            if (signals.HasFlag(CorrelationSignal.RequestId)) names.Add("same request ID");
            if (signals.HasFlag(CorrelationSignal.CorrelationId)) names.Add("same correlation ID");
            if (signals.HasFlag(CorrelationSignal.DepthOrder)) names.Add("depth differs by one");
            if (signals.HasFlag(CorrelationSignal.TimeContainment)) names.Add("runs inside the other's execution window");
            if (signals.HasFlag(CorrelationSignal.TimeProximity)) names.Add("close in time");
            if (signals.HasFlag(CorrelationSignal.SamePluginType)) names.Add("same plug-in type");
            if (signals.HasFlag(CorrelationSignal.SameStep)) names.Add("same step");
            if (signals.HasFlag(CorrelationSignal.SameMessage)) names.Add("same message");
            if (signals.HasFlag(CorrelationSignal.SamePrimaryEntity)) names.Add("same table");
            if (signals.HasFlag(CorrelationSignal.SameFingerprint)) names.Add("same failure fingerprint");
            return names.Count == 0 ? "none" : string.Join(", ", names);
        }
    }
}
