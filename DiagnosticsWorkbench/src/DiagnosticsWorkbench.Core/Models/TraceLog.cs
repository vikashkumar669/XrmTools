using System;

namespace DiagnosticsWorkbench.Models
{
    /// <summary>One row of the Dataverse plugintracelog table.</summary>
    public sealed class TraceLog
    {
        public Guid Id { get; set; }

        /// <summary>When the log was written (UTC).</summary>
        public DateTime CreatedOn { get; set; }

        public string TypeName { get; set; }
        public string MessageName { get; set; }
        public string PrimaryEntity { get; set; }
        public string ExceptionDetails { get; set; }
        public string MessageBlock { get; set; }

        /// <summary>performanceexecutionduration, in milliseconds.</summary>
        public int? ExecutionDurationMs { get; set; }

        /// <summary>performanceconstructorduration, in milliseconds.</summary>
        public int? ConstructorDurationMs { get; set; }

        /// <summary>performanceexecutionstarttime (UTC), when available.</summary>
        public DateTime? ExecutionStartTime { get; set; }

        public Guid? CorrelationId { get; set; }
        public Guid? RequestId { get; set; }
        public int Depth { get; set; }

        /// <summary>0 = synchronous, 1 = asynchronous.</summary>
        public int Mode { get; set; }

        /// <summary>1 = plug-in, 2 = workflow activity.</summary>
        public int OperationType { get; set; }

        public Guid? StepId { get; set; }
        public string CreatedByName { get; set; }

        public bool IsError => !string.IsNullOrWhiteSpace(ExceptionDetails);
        public bool IsAsync => Mode == 1;
        public string ModeText => Mode == 1 ? "Async" : "Sync";
        public string OperationTypeText => OperationType == 2 ? "Workflow activity" : "Plug-in";

        /// <summary>
        /// Type name without the assembly qualifier. Dataverse often stores
        /// "Namespace.Class, Assembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=…".
        /// </summary>
        public string FullTypeName => StripAssembly(TypeName);

        public static string StripAssembly(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return typeName;
            var comma = typeName.IndexOf(',');
            return comma > 0 ? typeName.Substring(0, comma).Trim() : typeName.Trim();
        }

        /// <summary>Short type name (class name without namespace).</summary>
        public string ShortTypeName
        {
            get
            {
                if (string.IsNullOrEmpty(TypeName)) return "(unknown type)";
                var name = TypeName;
                var comma = name.IndexOf(',');
                if (comma > 0) name = name.Substring(0, comma);
                var dot = name.LastIndexOf('.');
                return dot >= 0 && dot < name.Length - 1 ? name.Substring(dot + 1) : name;
            }
        }
    }
}
