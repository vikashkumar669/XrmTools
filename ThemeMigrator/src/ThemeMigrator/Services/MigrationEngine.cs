using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Text;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using ThemeMigrator.Models;

namespace ThemeMigrator.Services
{
    public enum LogLevel
    {
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// Copies classic themes, modern theme settings and the web resources they use from source to target,
    /// keeping the source GUIDs. Order: web resources → classic themes → modern settings → publish.
    /// </summary>
    public class MigrationEngine
    {
        private readonly IOrganizationService _source;
        private readonly IOrganizationService _target;
        private readonly Action<LogLevel, string> _log;
        private readonly EntityCopier _copier;
        private readonly MigrationResult _result = new MigrationResult();

        // source web resource name -> target web resource id
        private readonly Dictionary<string, Guid> _webResources = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Guid> _webResourcesToPublish = new HashSet<Guid>();

        public MigrationEngine(IOrganizationService source, IOrganizationService target, Action<LogLevel, string> log)
        {
            _source = source;
            _target = target;
            _log = log;
            _copier = new EntityCopier(target);
        }

        public MigrationResult Run(IList<ClassicThemeItem> themes, IList<ModernSettingItem> settings, MigrationOptions options)
        {
            var migratedThemes = new List<ClassicThemeItem>();
            foreach (var theme in themes)
            {
                if (MigrateClassicTheme(theme)) migratedThemes.Add(theme);
            }

            var migratedSettings = 0;
            foreach (var setting in settings)
            {
                if (MigrateModernSetting(setting)) migratedSettings++;
            }

            if (options.PublishAndSetDefault)
                Publish(migratedThemes, migratedSettings > 0);
            else
                _log(LogLevel.Info, "Publish was not selected: records are saved in the target but not published.");

            return _result;
        }

        // ---------------- Classic themes ----------------

        private bool MigrateClassicTheme(ClassicThemeItem theme)
        {
            _log(LogLevel.Info, $"Classic theme '{theme.Name}' ({theme.Id})");
            try
            {
                Guid? logoId = null;
                if (theme.Logo != null)
                    logoId = MigrateWebResource(theme.Logo.Name, "logo");

                var exists = ThemeReader.Exists(_target, "theme", "themeid", theme.Id);
                // isdefaulttheme is controlled by publishing; type marks system themes
                var payload = _copier.Build(theme.Record, !exists, "isdefaulttheme", "type", "logoid");
                payload.Id = theme.Id;

                if (_copier.HasAttribute("theme", "logoid", !exists))
                    payload["logoid"] = logoId.HasValue ? new EntityReference("webresource", logoId.Value) : null;

                if (exists)
                {
                    _target.Update(payload);
                    _result.Updated++;
                    _log(LogLevel.Success, $"  Updated theme '{theme.Name}' (same GUID).");
                }
                else
                {
                    _target.Create(payload);
                    _result.Created++;
                    _log(LogLevel.Success, $"  Created theme '{theme.Name}' with GUID {theme.Id}.");
                }
                return true;
            }
            catch (Exception ex)
            {
                _result.Failed++;
                _log(LogLevel.Error, $"  Failed to migrate theme '{theme.Name}': {Describe(ex)}");
                if (theme.IsDefault || ex.Message.IndexOf("published", StringComparison.OrdinalIgnoreCase) >= 0)
                    _log(LogLevel.Warning, "  Tip: Dataverse may block changes to the theme that is currently published/default in the target. " +
                                           "Publish a different theme in the target first, then run the migration again.");
                return false;
            }
        }

        // ---------------- Modern theme settings ----------------

        private bool MigrateModernSetting(ModernSettingItem setting)
        {
            var label = setting.Scope == SettingScope.Environment
                ? $"'{setting.SettingDisplayName}' (environment value)"
                : $"'{setting.SettingDisplayName}' for app '{setting.AppName ?? setting.AppUniqueName}'";
            _log(LogLevel.Info, $"Modern theme setting {label}");

            try
            {
                // Web resources first: the theme XML, then any logo it references
                if (setting.ThemeResource != null)
                {
                    if (setting.LogoResource != null)
                        MigrateWebResource(setting.LogoResource.Name, "modern theme logo");
                    else
                    {
                        var logoName = ThemeReader.FindLogoWebResourceName(setting.ThemeResource.Text);
                        if (!string.IsNullOrWhiteSpace(logoName))
                            Warn($"  Logo web resource '{logoName}' referenced by the theme XML wasn't found in the source.");
                    }
                    MigrateWebResource(setting.ThemeResource.Name, "modern theme XML");
                }
                else if (!string.IsNullOrWhiteSpace(setting.Value) && !ColorUtil.IsHex(setting.Value))
                {
                    Warn($"  Value '{setting.Value}' isn't a web resource in the source; copying the value as-is.");
                }

                var definitionId = ThemeReader.FindSettingDefinitionId(_target, setting.SettingUniqueName, setting.SettingDisplayName);
                if (definitionId == null)
                {
                    _result.Failed++;
                    _log(LogLevel.Error, $"  Setting definition '{setting.SettingUniqueName}' doesn't exist in the target environment.");
                    return false;
                }

                return setting.Scope == SettingScope.Environment
                    ? UpsertOrganizationSetting(setting, definitionId.Value)
                    : UpsertAppSetting(setting, definitionId.Value);
            }
            catch (Exception ex)
            {
                _result.Failed++;
                _log(LogLevel.Error, $"  Failed: {Describe(ex)}");
                return false;
            }
        }

        private bool UpsertOrganizationSetting(ModernSettingItem setting, Guid definitionId)
        {
            // One environment value per setting definition: prefer the same GUID, else the existing row
            Guid? existingId = ThemeReader.Exists(_target, "organizationsetting", "organizationsettingid", setting.Id)
                ? setting.Id
                : ThemeReader.FindOrganizationSetting(_target, definitionId)?.Id;

            return UpsertSetting(setting, existingId, payload =>
            {
                payload["settingdefinitionid"] = new EntityReference("settingdefinition", definitionId);
            });
        }

        private bool UpsertAppSetting(ModernSettingItem setting, Guid definitionId)
        {
            var appId = ThemeReader.FindAppModuleId(_target, setting.AppUniqueName);
            if (appId == null)
            {
                _result.Failed++;
                _log(LogLevel.Error, $"  App '{setting.AppName}' ({setting.AppUniqueName}) doesn't exist in the target. " +
                                     "Import the app first, then migrate this setting.");
                return false;
            }

            Guid? existingId = ThemeReader.Exists(_target, "appsetting", "appsettingid", setting.Id)
                ? setting.Id
                : ThemeReader.FindAppSetting(_target, definitionId, appId.Value)?.Id;

            return UpsertSetting(setting, existingId, payload =>
            {
                payload["settingdefinitionid"] = new EntityReference("settingdefinition", definitionId);
                payload["parentappmoduleid"] = new EntityReference("appmodule", appId.Value);
            });
        }

        private bool UpsertSetting(ModernSettingItem setting, Guid? existingId, Action<Entity> setLookups)
        {
            var create = existingId == null;
            var payload = _copier.Build(setting.Record, create);
            if (create)
            {
                payload.Id = setting.Id;
                setLookups(payload);
                _target.Create(payload);
                _result.Created++;
                _log(LogLevel.Success, $"  Created with GUID {setting.Id} → value '{setting.Value}'.");
            }
            else
            {
                payload.Id = existingId.Value;
                payload["value"] = setting.Value;
                _target.Update(payload);
                _result.Updated++;
                if (existingId.Value == setting.Id)
                    _log(LogLevel.Success, $"  Updated (same GUID) → value '{setting.Value}'.");
                else
                    Warn($"  Updated existing target row {existingId.Value} → value '{setting.Value}'. " +
                         "The target already had a value for this setting under a different GUID, and only one is allowed.");
            }
            return true;
        }

        // ---------------- Web resources ----------------

        /// <summary>Copies a web resource by name, keeping its GUID. Returns the target id, or null if it couldn't be copied.</summary>
        private Guid? MigrateWebResource(string name, string purpose)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (_webResources.TryGetValue(name, out var done)) return done;

            var sourceRecord = ThemeReader.FindWebResourceEntityByName(_source, name, new ColumnSet(true));
            if (sourceRecord == null)
            {
                Warn($"  Web resource '{name}' ({purpose}) wasn't found in the source.");
                return null;
            }

            try
            {
                Guid targetId;
                var sameId = ThemeReader.Exists(_target, "webresource", "webresourceid", sourceRecord.Id);
                var byName = sameId ? null : ThemeReader.FindWebResourceEntityByName(_target, name, new ColumnSet(false));

                if (sameId || byName != null)
                {
                    targetId = sameId ? sourceRecord.Id : byName.Id;
                    var update = _copier.Build(sourceRecord, false);
                    update.Id = targetId;
                    _target.Update(update);
                    _result.Updated++;
                    if (sameId)
                        _log(LogLevel.Success, $"  Updated web resource '{name}' ({purpose}).");
                    else
                        Warn($"  Updated web resource '{name}' ({purpose}), which exists in the target under a different GUID " +
                             $"({targetId}). Web resource names must be unique, so the target GUID was kept.");
                }
                else
                {
                    var create = _copier.Build(sourceRecord, true);
                    create.Id = sourceRecord.Id;
                    _target.Create(create);
                    targetId = sourceRecord.Id;
                    _result.Created++;
                    _log(LogLevel.Success, $"  Created web resource '{name}' ({purpose}) with GUID {targetId}.");
                }

                _webResources[name] = targetId;
                _webResourcesToPublish.Add(targetId);
                return targetId;
            }
            catch (Exception ex)
            {
                _result.Failed++;
                _log(LogLevel.Error, $"  Failed to migrate web resource '{name}': {Describe(ex)}");
                return null;
            }
        }

        // ---------------- Publish ----------------

        private void Publish(List<ClassicThemeItem> themes, bool modernSettingsChanged)
        {
            if (_webResourcesToPublish.Count > 0)
            {
                try
                {
                    _log(LogLevel.Info, $"Publishing {_webResourcesToPublish.Count} web resource(s)...");
                    var xml = new StringBuilder("<importexportxml><webresources>");
                    foreach (var id in _webResourcesToPublish) xml.Append("<webresource>{").Append(id).Append("}</webresource>");
                    xml.Append("</webresources></importexportxml>");
                    _target.Execute(new PublishXmlRequest { ParameterXml = xml.ToString() });
                    _log(LogLevel.Success, "  Web resources published.");
                }
                catch (Exception ex)
                {
                    _result.Failed++;
                    _log(LogLevel.Error, $"  Web resource publish failed: {Describe(ex)}");
                }
            }

            // Publishing a classic theme also makes it the default theme of the target environment
            foreach (var theme in themes)
            {
                try
                {
                    _log(LogLevel.Info, $"Publishing classic theme '{theme.Name}' and setting it as default...");
                    _target.Execute(new PublishThemeRequest { Target = new EntityReference("theme", theme.Id) });
                    _log(LogLevel.Success, $"  '{theme.Name}' is now the published default theme.");
                }
                catch (Exception ex)
                {
                    _result.Failed++;
                    _log(LogLevel.Error, $"  Publishing theme '{theme.Name}' failed: {Describe(ex)}");
                }
            }

            // Setting values only take effect after "Publish all customizations" (per Microsoft guidance)
            if (modernSettingsChanged)
            {
                try
                {
                    _log(LogLevel.Info, "Publishing all customizations so modern theme settings take effect (this can take a few minutes)...");
                    _target.Execute(new PublishAllXmlRequest());
                    _log(LogLevel.Success, "  All customizations published. Refresh open apps to see the new header/theme.");
                }
                catch (Exception ex)
                {
                    _result.Warnings++;
                    _log(LogLevel.Warning, $"  Publish all didn't confirm: {Describe(ex)}. If it timed out, it may still complete on the server; " +
                                           "otherwise run 'Publish all customizations' in the target.");
                }
            }
        }

        // ---------------- Helpers ----------------

        private void Warn(string message)
        {
            _result.Warnings++;
            _log(LogLevel.Warning, message);
        }

        private static string Describe(Exception ex)
        {
            if (ex is FaultException<OrganizationServiceFault> fault)
                return fault.Detail?.Message ?? fault.Message;
            return ex.InnerException != null ? $"{ex.Message} ({ex.InnerException.Message})" : ex.Message;
        }
    }
}
