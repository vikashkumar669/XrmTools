using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using ThemeMigrator.Models;

namespace ThemeMigrator.Services
{
    /// <summary>Read-side queries shared by the source load, the target comparison and the migration engine.</summary>
    public static class ThemeReader
    {
        /// <summary>Setting definitions that drive modern (New look) theming in model-driven apps.</summary>
        public static readonly string[] ThemeSettingUniqueNames = { "CustomThemeDefinition", "OverrideAppHeaderColor" };

        /// <summary>Fallback match by display name, in case a unique name differs in an environment.</summary>
        public static readonly string[] ThemeSettingDisplayNames = { "Custom theme definition", "Override app header color" };

        // ---------- Classic themes ----------

        public static List<ClassicThemeItem> GetClassicThemes(IOrganizationService service)
        {
            var query = new QueryExpression("theme")
            {
                ColumnSet = new ColumnSet(true),
                Orders = { new OrderExpression("name", OrderType.Ascending) }
            };

            var items = new List<ClassicThemeItem>();
            var logoCache = new Dictionary<Guid, WebResourceInfo>();

            foreach (var theme in RetrieveAll(service, query))
            {
                var item = new ClassicThemeItem { Record = theme };
                item.TypeLabel = theme.FormattedValues.Contains("type")
                    ? theme.FormattedValues["type"]
                    : (theme.Contains("type") ? Convert.ToString(theme["type"]) : "");

                var logoRef = theme.GetAttributeValue<EntityReference>("logoid");
                if (logoRef != null)
                {
                    if (!logoCache.TryGetValue(logoRef.Id, out var logo))
                    {
                        logo = GetWebResourceById(service, logoRef.Id);
                        logoCache[logoRef.Id] = logo;
                    }
                    item.Logo = logo;
                }
                items.Add(item);
            }
            return items;
        }

        // ---------- Modern themes (settings) ----------

        public static List<Entity> GetThemeSettingDefinitions(IOrganizationService service)
        {
            var query = new QueryExpression("settingdefinition")
            {
                ColumnSet = new ColumnSet("uniquename", "displayname")
            };
            var filter = new FilterExpression(LogicalOperator.Or);
            filter.AddCondition("uniquename", ConditionOperator.In, ThemeSettingUniqueNames.Cast<object>().ToArray());
            filter.AddCondition("displayname", ConditionOperator.In, ThemeSettingDisplayNames.Cast<object>().ToArray());
            query.Criteria.AddFilter(filter);
            return RetrieveAll(service, query);
        }

        public static List<ModernSettingItem> GetModernSettings(IOrganizationService service)
        {
            var result = new List<ModernSettingItem>();
            var definitions = GetThemeSettingDefinitions(service);
            if (definitions.Count == 0) return result;

            var definitionIds = definitions.Select(d => (object)d.Id).ToArray();
            var byId = definitions.ToDictionary(d => d.Id);

            // Environment level values
            var orgQuery = new QueryExpression("organizationsetting") { ColumnSet = new ColumnSet(true) };
            orgQuery.Criteria.AddCondition("settingdefinitionid", ConditionOperator.In, definitionIds);
            foreach (var row in RetrieveAll(service, orgQuery))
            {
                var def = byId[row.GetAttributeValue<EntityReference>("settingdefinitionid").Id];
                result.Add(new ModernSettingItem
                {
                    Id = row.Id,
                    Scope = SettingScope.Environment,
                    SettingUniqueName = def.GetAttributeValue<string>("uniquename"),
                    SettingDisplayName = def.GetAttributeValue<string>("displayname"),
                    Value = row.GetAttributeValue<string>("value"),
                    Record = row
                });
            }

            // App level values
            var appQuery = new QueryExpression("appsetting") { ColumnSet = new ColumnSet(true) };
            appQuery.Criteria.AddCondition("settingdefinitionid", ConditionOperator.In, definitionIds);
            var appLink = appQuery.AddLink("appmodule", "parentappmoduleid", "appmoduleid", JoinOperator.LeftOuter);
            appLink.EntityAlias = "app";
            appLink.Columns = new ColumnSet("uniquename", "name");
            foreach (var row in RetrieveAll(service, appQuery))
            {
                var def = byId[row.GetAttributeValue<EntityReference>("settingdefinitionid").Id];
                result.Add(new ModernSettingItem
                {
                    Id = row.Id,
                    Scope = SettingScope.App,
                    SettingUniqueName = def.GetAttributeValue<string>("uniquename"),
                    SettingDisplayName = def.GetAttributeValue<string>("displayname"),
                    AppUniqueName = Aliased<string>(row, "app.uniquename"),
                    AppName = Aliased<string>(row, "app.name"),
                    Value = row.GetAttributeValue<string>("value"),
                    Record = row
                });
            }

            // Resolve the theme XML web resource and any logo it references
            var wrCache = new Dictionary<string, WebResourceInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in result)
            {
                item.ThemeResource = GetCachedByName(service, item.Value, wrCache);
                if (item.ThemeResource != null)
                {
                    var logoName = FindLogoWebResourceName(item.ThemeResource.Text);
                    item.LogoResource = GetCachedByName(service, logoName, wrCache);
                }
            }

            return result
                .OrderBy(r => r.SettingDisplayName)
                .ThenBy(r => r.Scope)
                .ThenBy(r => r.AppName)
                .ToList();
        }

        /// <summary>Reads logoWebResource (any casing) from a CustomTheme XML.</summary>
        public static string FindLogoWebResourceName(string xml)
        {
            var doc = TryParseXml(xml);
            if (doc == null) return null;
            return doc.Descendants()
                .SelectMany(e => e.Attributes())
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, "logoWebResource", StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }

        /// <summary>All hex-color attributes in a theme XML, named "Element.attribute".</summary>
        public static List<KeyValuePair<string, string>> GetXmlColors(string xml)
        {
            var list = new List<KeyValuePair<string, string>>();
            var doc = TryParseXml(xml);
            if (doc == null) return list;
            foreach (var element in doc.Descendants())
            foreach (var attribute in element.Attributes())
            {
                if (ColorUtil.IsHex(attribute.Value))
                    list.Add(new KeyValuePair<string, string>(
                        element.Name.LocalName + "." + attribute.Name.LocalName, attribute.Value.Trim()));
            }
            return list;
        }

        public static XDocument TryParseXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try { return XDocument.Parse(xml); }
            catch { return null; }
        }

        // ---------- Web resources ----------

        private static readonly ColumnSet WebResourceColumns =
            new ColumnSet("name", "displayname", "webresourcetype", "content");

        public static WebResourceInfo GetWebResourceById(IOrganizationService service, Guid id)
        {
            var query = new QueryExpression("webresource") { ColumnSet = WebResourceColumns, TopCount = 1 };
            query.Criteria.AddCondition("webresourceid", ConditionOperator.Equal, id);
            return ToInfo(service.RetrieveMultiple(query).Entities.FirstOrDefault());
        }

        public static WebResourceInfo GetWebResourceByName(IOrganizationService service, string name)
        {
            var entity = FindWebResourceEntityByName(service, name, WebResourceColumns);
            return ToInfo(entity);
        }

        public static Entity FindWebResourceEntityByName(IOrganizationService service, string name, ColumnSet columns)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var query = new QueryExpression("webresource") { ColumnSet = columns, TopCount = 1 };
            query.Criteria.AddCondition("name", ConditionOperator.Equal, name.Trim());
            return service.RetrieveMultiple(query).Entities.FirstOrDefault();
        }

        private static WebResourceInfo GetCachedByName(IOrganizationService service, string name,
            Dictionary<string, WebResourceInfo> cache)
        {
            if (string.IsNullOrWhiteSpace(name) || ColorUtil.IsHex(name)) return null;
            if (!cache.TryGetValue(name, out var info))
            {
                info = GetWebResourceByName(service, name);
                cache[name] = info;
            }
            return info;
        }

        private static WebResourceInfo ToInfo(Entity e)
        {
            if (e == null) return null;
            return new WebResourceInfo
            {
                Id = e.Id,
                Name = e.GetAttributeValue<string>("name"),
                DisplayName = e.GetAttributeValue<string>("displayname"),
                Type = e.GetAttributeValue<OptionSetValue>("webresourcetype")?.Value ?? 0,
                Content = e.GetAttributeValue<string>("content")
            };
        }

        // ---------- Target lookups ----------

        public static bool Exists(IOrganizationService service, string entity, string idAttribute, Guid id)
        {
            var query = new QueryExpression(entity) { ColumnSet = new ColumnSet(false), TopCount = 1 };
            query.Criteria.AddCondition(idAttribute, ConditionOperator.Equal, id);
            return service.RetrieveMultiple(query).Entities.Count > 0;
        }

        public static Guid? FindAppModuleId(IOrganizationService service, string uniqueName)
        {
            if (string.IsNullOrWhiteSpace(uniqueName)) return null;
            var query = new QueryExpression("appmodule") { ColumnSet = new ColumnSet(false), TopCount = 1 };
            query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);
            return service.RetrieveMultiple(query).Entities.FirstOrDefault()?.Id;
        }

        /// <summary>Finds the target setting definition by unique name, falling back to display name.</summary>
        public static Guid? FindSettingDefinitionId(IOrganizationService service, string uniqueName, string displayName)
        {
            var lookups = new[]
            {
                new KeyValuePair<string, string>("uniquename", uniqueName),
                new KeyValuePair<string, string>("displayname", displayName)
            };
            foreach (var lookup in lookups)
            {
                var attribute = lookup.Key;
                var value = lookup.Value;
                if (string.IsNullOrWhiteSpace(value)) continue;
                var query = new QueryExpression("settingdefinition") { ColumnSet = new ColumnSet(false), TopCount = 1 };
                query.Criteria.AddCondition(attribute, ConditionOperator.Equal, value);
                var hit = service.RetrieveMultiple(query).Entities.FirstOrDefault();
                if (hit != null) return hit.Id;
            }
            return null;
        }

        public static Entity FindOrganizationSetting(IOrganizationService service, Guid settingDefinitionId)
        {
            var query = new QueryExpression("organizationsetting") { ColumnSet = new ColumnSet(false), TopCount = 1 };
            query.Criteria.AddCondition("settingdefinitionid", ConditionOperator.Equal, settingDefinitionId);
            return service.RetrieveMultiple(query).Entities.FirstOrDefault();
        }

        public static Entity FindAppSetting(IOrganizationService service, Guid settingDefinitionId, Guid appModuleId)
        {
            var query = new QueryExpression("appsetting") { ColumnSet = new ColumnSet(false), TopCount = 1 };
            query.Criteria.AddCondition("settingdefinitionid", ConditionOperator.Equal, settingDefinitionId);
            query.Criteria.AddCondition("parentappmoduleid", ConditionOperator.Equal, appModuleId);
            return service.RetrieveMultiple(query).Entities.FirstOrDefault();
        }

        /// <summary>Marks each item as New / Exists in the target, matching on the same GUID.</summary>
        public static void MarkTargetState(IOrganizationService target,
            IEnumerable<ClassicThemeItem> themes, IEnumerable<ModernSettingItem> settings)
        {
            foreach (var theme in themes)
                theme.TargetState = Exists(target, "theme", "themeid", theme.Id) ? TargetState.Exists : TargetState.New;

            foreach (var setting in settings)
            {
                var entity = setting.Scope == SettingScope.Environment ? "organizationsetting" : "appsetting";
                var idAttribute = entity + "id";
                try
                {
                    setting.TargetState = Exists(target, entity, idAttribute, setting.Id) ? TargetState.Exists : TargetState.New;
                }
                catch
                {
                    // Target doesn't support settings (older or on-premises version)
                    setting.TargetState = TargetState.Unknown;
                }
            }
        }

        // ---------- Helpers ----------

        public static List<Entity> RetrieveAll(IOrganizationService service, QueryExpression query)
        {
            var all = new List<Entity>();
            query.PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 };
            while (true)
            {
                var page = service.RetrieveMultiple(query);
                all.AddRange(page.Entities);
                if (!page.MoreRecords) break;
                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = page.PagingCookie;
            }
            return all;
        }

        private static T Aliased<T>(Entity e, string name) =>
            e.Contains(name) && e[name] is AliasedValue av && av.Value is T value ? value : default;
    }
}
