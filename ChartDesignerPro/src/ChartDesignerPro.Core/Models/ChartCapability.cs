namespace ChartDesignerPro.Models
{
    /// <summary>What the tool is allowed to do with a chart, and why not when it can't.</summary>
    public sealed class ChartCapability
    {
        private ChartCapability(bool canEdit, string reason)
        {
            CanEdit = canEdit;
            Reason = reason;
        }

        /// <summary>True when the chart's XML may be edited in this tool (the platform may still refuse the save, e.g. for missing privileges).</summary>
        public bool CanEdit { get; }

        /// <summary>Plain-language reason when <see cref="CanEdit"/> is false; otherwise a short note or null.</summary>
        public string Reason { get; }

        public static ChartCapability Editable(string note = null) => new ChartCapability(true, note);
        public static ChartCapability ReadOnly(string reason) => new ChartCapability(false, reason);

        /// <summary>
        /// Decides editability from the facts on the record only. Rules (Step 1 feasibility matrix, rows 4 and 5):
        /// web-resource and Power BI visualizations are read-only; managed system charts whose IsCustomizable
        /// managed property is false are read-only. Everything else is editable, subject to the user's privileges.
        /// </summary>
        public static ChartCapability For(ChartRecord record)
        {
            if (record == null) return ReadOnly("No chart is selected.");
            if (record.WebResourceId.HasValue)
                return ReadOnly("This visualization is a web resource, not a chart. Chart Designer Pro shows it read-only.");
            if (record.Engine == ChartEngine.PowerBI)
                return ReadOnly("This is a Power BI visualization. Chart Designer Pro only edits ASP.NET (model-driven) charts.");
            if (record.Engine == ChartEngine.Unknown)
                return ReadOnly("The chart type value isn't recognised, so the chart is shown read-only.");
            if (record.Scope == ChartScope.System && record.IsManaged == true && record.IsCustomizable == false)
                return ReadOnly("This system chart is managed and not customizable (IsCustomizable = false). Change it in the solution that owns it.");
            if (record.Scope == ChartScope.System && record.IsManaged == true)
                return Editable("Managed chart: your change goes into an unmanaged layer on top of the managed solution.");
            return Editable();
        }
    }
}
