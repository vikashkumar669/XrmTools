using System;
using System.Collections.Generic;
using System.Linq;

namespace DiagnosticsWorkbench.Models
{
    /// <summary>Registration of a plug-in step (sdkmessageprocessingstep) with its images and assembly.</summary>
    public sealed class StepInfo
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string MessageName { get; set; }
        public string PrimaryEntity { get; set; }

        /// <summary>10 = pre-validation, 20 = pre-operation, 40 = post-operation, 30 = main operation.</summary>
        public int Stage { get; set; }

        /// <summary>0 = synchronous, 1 = asynchronous.</summary>
        public int Mode { get; set; }

        public int Rank { get; set; }

        /// <summary>Comma-separated filtering attributes; empty means "all columns".</summary>
        public string FilteringAttributes { get; set; }

        public string PluginTypeName { get; set; }
        public string AssemblyName { get; set; }
        public string AssemblyVersion { get; set; }

        /// <summary>1 = none, 2 = sandbox.</summary>
        public int IsolationMode { get; set; }

        /// <summary>0 = enabled, 1 = disabled.</summary>
        public int StateCode { get; set; }

        public List<StepImage> Images { get; set; } = new List<StepImage>();

        public string StageText
        {
            get
            {
                switch (Stage)
                {
                    case 10: return "Pre-validation";
                    case 20: return "Pre-operation";
                    case 30: return "Main operation";
                    case 40: return "Post-operation";
                    default: return Stage == 0 ? "Unknown stage" : "Stage " + Stage;
                }
            }
        }

        public string ModeText => Mode == 1 ? "Async" : "Sync";
        public string IsolationText => IsolationMode == 2 ? "Sandbox" : IsolationMode == 1 ? "None" : "Unknown";
        public bool HasFilteringAttributes => !string.IsNullOrWhiteSpace(FilteringAttributes);

        public IEnumerable<string> FilteringAttributeList =>
            (FilteringAttributes ?? "").Split(',').Select(a => a.Trim()).Where(a => a.Length > 0);
    }

    public sealed class StepImage
    {
        public Guid Id { get; set; }
        public string Name { get; set; }

        /// <summary>0 = pre-image, 1 = post-image, 2 = both.</summary>
        public int ImageType { get; set; }

        /// <summary>Comma-separated columns; empty means the whole record.</summary>
        public string Attributes { get; set; }

        public bool AllColumns => string.IsNullOrWhiteSpace(Attributes);

        public string ImageTypeText
        {
            get
            {
                switch (ImageType)
                {
                    case 0: return "Pre-image";
                    case 1: return "Post-image";
                    case 2: return "Pre- and post-image";
                    default: return "Image";
                }
            }
        }
    }
}
