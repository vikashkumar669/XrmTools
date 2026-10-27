using System;
using System.Collections.Generic;
using System.Linq;

namespace PluginFailureCorrelationExplorer.Models
{
    /// <summary>
    /// One row of the Dataverse <c>plugintracelog</c> table. Every identifier is optional because Dataverse does not
    /// guarantee that each trace carries each value. The secured and unsecured step configuration columns
    /// (<c>configuration</c>, <c>secureconfiguration</c>) are deliberately never mapped.
    /// </summary>
    public sealed class PluginTraceRecord
    {
        /// <summary><c>plugintracelogid</c>.</summary>
        public Guid Id { get; set; }

        /// <summary><c>createdon</c> in UTC: when Dataverse wrote the log (usually at the end of the execution).</summary>
        public DateTime CreatedOnUtc { get; set; }

        /// <summary><c>typename</c>: plug-in class, sometimes assembly-qualified.</summary>
        public string TypeName { get; set; }

        /// <summary><c>messagename</c>, for example Create, Update or a custom API name.</summary>
        public string MessageName { get; set; }

        /// <summary><c>primaryentity</c>; "none" or empty for messages without a table.</summary>
        public string PrimaryEntity { get; set; }

        /// <summary>
        /// <c>exceptiondetails</c>. Null when the trace has no exception <em>or</em> when it hasn't been loaded yet;
        /// <see cref="ExceptionLoaded"/> tells the two apart.
        /// </summary>
        public string ExceptionDetails { get; set; }

        /// <summary>True once <see cref="ExceptionDetails"/> reflects the server value (it may legitimately be null).</summary>
        public bool ExceptionLoaded { get; set; }

        /// <summary><c>messageblock</c> (trace text). Loaded on demand for the detail view only.</summary>
        public string MessageBlock { get; set; }

        /// <summary>True once <see cref="MessageBlock"/> reflects the server value.</summary>
        public bool MessageBlockLoaded { get; set; }

        /// <summary><c>performanceexecutionduration</c> in milliseconds.</summary>
        public int? ExecutionDurationMs { get; set; }

        /// <summary><c>performanceconstructorduration</c> in milliseconds.</summary>
        public int? ConstructorDurationMs { get; set; }

        /// <summary><c>performanceexecutionstarttime</c> in UTC.</summary>
        public DateTime? ExecutionStartUtc { get; set; }

        /// <summary><c>correlationid</c>: shared by every execution in one causal chain, including async continuations.</summary>
        public Guid? CorrelationId { get; set; }

        /// <summary><c>requestid</c>: identifies one message request.</summary>
        public Guid? RequestId { get; set; }

        /// <summary><c>depth</c>: 1 for the first execution, higher when triggered by other plug-ins or workflows.</summary>
        public int? Depth { get; set; }

        /// <summary><c>mode</c>: 0 synchronous, 1 asynchronous.</summary>
        public int? Mode { get; set; }

        /// <summary><c>operationtype</c>: 0 unknown, 1 plug-in, 2 workflow activity.</summary>
        public int? OperationType { get; set; }

        /// <summary><c>pluginstepid</c>: the <c>sdkmessageprocessingstep</c> that ran.</summary>
        public Guid? PluginStepId { get; set; }

        /// <summary>Step and assembly details resolved from <see cref="PluginStepId"/>, when available.</summary>
        public StepRegistration Step { get; set; }

        /// <summary>True when an exception was recorded. Only meaningful when <see cref="ExceptionLoaded"/> is true.</summary>
        public bool HasException => !string.IsNullOrWhiteSpace(ExceptionDetails);

        public bool IsAsync => Mode == 1;

        public string ModeText => Mode == 1 ? "Async" : Mode == 0 ? "Sync" : "Unknown";

        public string OperationTypeText => OperationType == 2 ? "Workflow activity" : OperationType == 1 ? "Plug-in" : "Unknown";

        /// <summary>Type name without the assembly qualifier.</summary>
        public string FullTypeName => TypeNames.StripAssembly(TypeName);

        /// <summary>Class name without namespace.</summary>
        public string ShortTypeName => TypeNames.ShortName(TypeName);

        /// <summary>Assembly name: from the resolved step, otherwise from an assembly-qualified type name.</summary>
        public string AssemblyName => Step?.AssemblyName ?? TypeNames.AssemblyPart(TypeName);

        /// <summary>Estimated end of the execution: start + duration, or <see cref="CreatedOnUtc"/> when unknown.</summary>
        public DateTime EndUtc => ExecutionStartUtc.HasValue && ExecutionDurationMs.HasValue
            ? ExecutionStartUtc.Value.AddMilliseconds(ExecutionDurationMs.Value)
            : CreatedOnUtc;

        /// <summary>Start of the execution: <see cref="ExecutionStartUtc"/>, or end minus duration, or <see cref="CreatedOnUtc"/>.</summary>
        public DateTime StartUtc => ExecutionStartUtc
            ?? (ExecutionDurationMs.HasValue ? CreatedOnUtc.AddMilliseconds(-ExecutionDurationMs.Value) : CreatedOnUtc);

        /// <summary>Identifiers this record lacks, used for the evidence completeness metric.</summary>
        public IReadOnlyList<string> MissingIdentifiers
        {
            get
            {
                var missing = new List<string>();
                if (!CorrelationId.HasValue) missing.Add("correlationid");
                if (!RequestId.HasValue) missing.Add("requestid");
                if (!PluginStepId.HasValue) missing.Add("pluginstepid");
                if (string.IsNullOrWhiteSpace(TypeName)) missing.Add("typename");
                if (!ExecutionDurationMs.HasValue) missing.Add("performanceexecutionduration");
                if (!Depth.HasValue) missing.Add("depth");
                return missing;
            }
        }

        /// <summary>True when every identifier used for correlation and performance analysis is present.</summary>
        public bool HasCompleteEvidence => MissingIdentifiers.Count == 0;
    }

    /// <summary>Registration details for the step that produced a trace (read from sdkmessageprocessingstep, plugintype, pluginassembly).</summary>
    public sealed class StepRegistration
    {
        public Guid StepId { get; set; }
        public string StepName { get; set; }

        /// <summary>10 pre-validation, 20 pre-operation, 40 post-operation, 45/50 post-operation (deprecated / async).</summary>
        public int? Stage { get; set; }
        public int? Mode { get; set; }
        public int? Rank { get; set; }
        public string PluginTypeName { get; set; }
        public string AssemblyName { get; set; }
        public string AssemblyVersion { get; set; }

        /// <summary>statecode of the step: 0 enabled, 1 disabled.</summary>
        public int? State { get; set; }

        public string StageText
        {
            get
            {
                switch (Stage)
                {
                    case 10: return "PreValidation";
                    case 20: return "PreOperation";
                    case 30: return "MainOperation";
                    case 40: return "PostOperation";
                    case 45:
                    case 50: return "PostOperation (deprecated)";
                    default: return Stage?.ToString() ?? "";
                }
            }
        }
    }

    /// <summary>Helpers for Dataverse type names, which may be assembly-qualified.</summary>
    public static class TypeNames
    {
        /// <summary>"Ns.Class, Asm, Version=…" → "Ns.Class".</summary>
        public static string StripAssembly(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return typeName?.Trim();
            var comma = typeName.IndexOf(',');
            return comma > 0 ? typeName.Substring(0, comma).Trim() : typeName.Trim();
        }

        /// <summary>"Ns.Class, Asm, Version=…" → "Asm"; null when not qualified.</summary>
        public static string AssemblyPart(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            var parts = typeName.Split(',');
            return parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1].Trim() : null;
        }

        /// <summary>
        /// Class name without namespace or assembly. Generic types are shown with C# syntax:
        /// "System.ServiceModel.FaultException`1[Microsoft.Xrm.Sdk.OrganizationServiceFault]" → "FaultException&lt;OrganizationServiceFault&gt;".
        /// </summary>
        public static string ShortName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return "(unknown type)";
            var name = typeName.Trim();
            var bracket = name.IndexOf('[');
            if (bracket > 0 && name.EndsWith("]", StringComparison.Ordinal))
            {
                var outer = Simple(name.Substring(0, bracket));
                var tick = outer.IndexOf('`');
                if (tick > 0) outer = outer.Substring(0, tick);
                var inner = name.Substring(bracket + 1, name.Length - bracket - 2).Trim('[', ']');
                return outer + "<" + string.Join(", ", inner.Split(',').Where(p => p.IndexOf('=') < 0).Select(Simple).Where(p => p.Length > 0)) + ">";
            }
            return Simple(StripAssembly(name));
        }

        private static string Simple(string name)
        {
            var full = (name ?? "").Trim();
            var dot = full.LastIndexOf('.');
            return dot >= 0 && dot < full.Length - 1 ? full.Substring(dot + 1) : full;
        }
    }
}
