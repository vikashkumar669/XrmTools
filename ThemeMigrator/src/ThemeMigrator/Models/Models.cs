using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

namespace ThemeMigrator.Models
{
    /// <summary>Whether a source record already exists in the target environment.</summary>
    public enum TargetState
    {
        Unknown,
        New,
        Exists
    }

    /// <summary>A web resource (logo image or theme XML) read from the source environment.</summary>
    public class WebResourceInfo
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public int Type { get; set; }
        /// <summary>Base64 content as stored in Dataverse.</summary>
        public string Content { get; set; }

        public byte[] Bytes
        {
            get
            {
                if (string.IsNullOrEmpty(Content)) return new byte[0];
                try { return Convert.FromBase64String(Content); }
                catch (FormatException) { return new byte[0]; }
            }
        }

        public string Text
        {
            get
            {
                var bytes = Bytes;
                if (bytes.Length == 0) return string.Empty;
                return System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿');
            }
        }

        /// <summary>Dataverse web resource types: 5 PNG, 6 JPG, 7 GIF, 10 ICO, 11 SVG, 4 XML.</summary>
        public bool IsRasterImage => Type == 5 || Type == 6 || Type == 7 || Type == 10;
        public bool IsSvg => Type == 11;
    }

    /// <summary>A classic theme (theme table) from the source environment.</summary>
    public class ClassicThemeItem
    {
        public Entity Record { get; set; }
        public Guid Id => Record.Id;
        public string Name => Record.GetAttributeValue<string>("name");
        public bool IsDefault => Record.GetAttributeValue<bool>("isdefaulttheme");
        public string TypeLabel { get; set; }

        /// <summary>Microsoft system theme (CRM Default/Blue/Orange …): same GUID in every environment.</summary>
        // Dataverse stores theme.type as a boolean: true = Custom, false = System (verified against live data).
        public bool IsSystem =>
            Record.Contains("type") && Record["type"] is bool isCustom
                ? !isCustom
                : string.Equals(TypeLabel, "System", StringComparison.OrdinalIgnoreCase);
        public WebResourceInfo Logo { get; set; }
        public TargetState TargetState { get; set; }

        /// <summary>All attributes holding a hex color (#RGB / #RRGGBB / #AARRGGBB).</summary>
        public IEnumerable<KeyValuePair<string, string>> Colors
        {
            get
            {
                foreach (var kv in Record.Attributes)
                {
                    if (kv.Value is string s && ColorUtil.IsHex(s))
                        yield return new KeyValuePair<string, string>(kv.Key, s.Trim());
                }
            }
        }
    }

    public enum SettingScope
    {
        Environment,
        App
    }

    /// <summary>
    /// A modern theme setting value ("Custom theme definition" or "Override app header color"),
    /// at environment level (organizationsetting) or app level (appsetting).
    /// </summary>
    public class ModernSettingItem
    {
        public Guid Id { get; set; }
        public SettingScope Scope { get; set; }
        public string SettingUniqueName { get; set; }
        public string SettingDisplayName { get; set; }
        public string AppUniqueName { get; set; }
        public string AppName { get; set; }
        public string Value { get; set; }
        public Entity Record { get; set; }

        /// <summary>The XML web resource the setting points to (null if the value isn't a web resource name).</summary>
        public WebResourceInfo ThemeResource { get; set; }

        /// <summary>Logo image referenced by logoWebResource inside the theme XML, if any.</summary>
        public WebResourceInfo LogoResource { get; set; }

        public TargetState TargetState { get; set; }

        public string ScopeLabel => Scope == SettingScope.Environment ? "Environment" : "App";
    }

    public class MigrationOptions
    {
        /// <summary>Publish migrated components; the selected classic theme is published, which also makes it the default.</summary>
        public bool PublishAndSetDefault { get; set; }
    }

    public class MigrationResult
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Failed { get; set; }
        public int Warnings { get; set; }

        public override string ToString() =>
            $"{Created} created, {Updated} updated, {Failed} failed, {Warnings} warning(s)";
    }
}
