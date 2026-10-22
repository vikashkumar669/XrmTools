using System.Collections.Generic;

namespace DiagnosticsWorkbench.Models
{
    /// <summary>Per-connection settings. Public properties only, so XrmToolBox's SettingsManager can XML-serialize it.</summary>
    public class WorkbenchSettings
    {
        public const int DefaultSyncThresholdMs = 2000;
        public const int DefaultAsyncThresholdMs = 10000;
        public const int DefaultNearTimeoutMs = 60000;
        public const int DefaultRowCap = 50000;

        public int SyncThresholdMs { get; set; } = DefaultSyncThresholdMs;
        public int AsyncThresholdMs { get; set; } = DefaultAsyncThresholdMs;

        /// <summary>Synchronous runs above this are flagged as close to the 2-minute platform timeout.</summary>
        public int NearTimeoutMs { get; set; } = DefaultNearTimeoutMs;

        public int RowCap { get; set; } = DefaultRowCap;

        /// <summary>Last date range preset used, in hours (0 = custom).</summary>
        public int LastRangeHours { get; set; } = 24;

        public List<ThresholdOverride> ThresholdOverrides { get; set; } = new List<ThresholdOverride>();
        public List<NormalizationRule> CustomRules { get; set; } = new List<NormalizationRule>();

        public WorkbenchSettings Clone()
        {
            var copy = (WorkbenchSettings)MemberwiseClone();
            copy.ThresholdOverrides = new List<ThresholdOverride>();
            foreach (var o in ThresholdOverrides ?? new List<ThresholdOverride>())
                copy.ThresholdOverrides.Add(new ThresholdOverride { TypeName = o.TypeName, MessageName = o.MessageName, ThresholdMs = o.ThresholdMs });
            copy.CustomRules = new List<NormalizationRule>();
            foreach (var r in CustomRules ?? new List<NormalizationRule>())
                copy.CustomRules.Add(new NormalizationRule { Name = r.Name, Pattern = r.Pattern, Token = r.Token, Enabled = r.Enabled });
            return copy;
        }

        /// <summary>Repairs values that a hand-edited or older settings file may carry.</summary>
        public void Normalize()
        {
            if (SyncThresholdMs <= 0) SyncThresholdMs = DefaultSyncThresholdMs;
            if (AsyncThresholdMs <= 0) AsyncThresholdMs = DefaultAsyncThresholdMs;
            if (NearTimeoutMs <= 0) NearTimeoutMs = DefaultNearTimeoutMs;
            if (RowCap <= 0) RowCap = DefaultRowCap;
            ThresholdOverrides = ThresholdOverrides ?? new List<ThresholdOverride>();
            CustomRules = CustomRules ?? new List<NormalizationRule>();
            ThresholdOverrides.RemoveAll(o => o == null || string.IsNullOrWhiteSpace(o.TypeName) || o.ThresholdMs <= 0);
            CustomRules.RemoveAll(r => r == null || string.IsNullOrWhiteSpace(r.Pattern));
        }
    }

    /// <summary>Slow-run threshold for one plugin type, optionally for one message only.</summary>
    public class ThresholdOverride
    {
        /// <summary>Full or short plugin type name (case-insensitive).</summary>
        public string TypeName { get; set; }

        /// <summary>Message (Create, Update…) or empty for every message.</summary>
        public string MessageName { get; set; }

        public int ThresholdMs { get; set; }
    }

    /// <summary>User-defined normalization: every regex match is replaced by Token before fingerprinting.</summary>
    public class NormalizationRule
    {
        public string Name { get; set; }
        public string Pattern { get; set; }
        public string Token { get; set; }
        public bool Enabled { get; set; } = true;
    }
}
