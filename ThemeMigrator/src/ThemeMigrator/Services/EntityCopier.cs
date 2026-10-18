using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace ThemeMigrator.Services
{
    /// <summary>
    /// Builds a create/update payload for the target from a source record, keeping only attributes the
    /// target accepts. Lookups are never copied blindly; the caller remaps the ones it needs.
    /// </summary>
    public class EntityCopier
    {
        private static readonly HashSet<string> SystemAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "createdon", "createdby", "createdonbehalfby", "modifiedon", "modifiedby", "modifiedonbehalfby",
            "ownerid", "owningbusinessunit", "owninguser", "owningteam", "organizationid", "versionnumber",
            "importsequencenumber", "overriddencreatedon", "timezoneruleversionnumber", "utcconversiontimezonecode",
            "statecode", "statuscode", "solutionid", "supportingsolutionid", "componentstate", "ismanaged",
            "overwritetime", "componentidunique", "iscustomizable", "canbedeleted", "ishidden", "introducedversion",
            "webresourceidunique", "settingdefinitionid", "parentappmoduleid"
        };

        private readonly IOrganizationService _target;
        private readonly Dictionary<string, Dictionary<string, AttributeMetadata>> _metadata =
            new Dictionary<string, Dictionary<string, AttributeMetadata>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _primaryIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public EntityCopier(IOrganizationService target)
        {
            _target = target;
        }

        public Entity Build(Entity source, bool forCreate, params string[] extraExcluded)
        {
            var attributes = GetAttributes(source.LogicalName);
            var primaryId = _primaryIds[source.LogicalName];
            var excluded = new HashSet<string>(extraExcluded ?? new string[0], StringComparer.OrdinalIgnoreCase);

            var result = new Entity(source.LogicalName);
            foreach (var kv in source.Attributes)
            {
                if (string.Equals(kv.Key, primaryId, StringComparison.OrdinalIgnoreCase)) continue;
                if (SystemAttributes.Contains(kv.Key) || excluded.Contains(kv.Key)) continue;
                if (kv.Value is EntityReference || kv.Value is EntityCollection || kv.Value is AliasedValue) continue;
                if (kv.Value is BooleanManagedProperty) continue;
                if (!attributes.TryGetValue(kv.Key, out var meta)) continue;
                if (!string.IsNullOrEmpty(meta.AttributeOf)) continue;

                var valid = forCreate ? meta.IsValidForCreate == true : meta.IsValidForUpdate == true;
                if (!valid) continue;

                result[kv.Key] = kv.Value;
            }
            return result;
        }

        public bool HasAttribute(string entity, string attribute, bool forCreate)
        {
            var attributes = GetAttributes(entity);
            if (!attributes.TryGetValue(attribute, out var meta)) return false;
            return forCreate ? meta.IsValidForCreate == true : meta.IsValidForUpdate == true;
        }

        private Dictionary<string, AttributeMetadata> GetAttributes(string entity)
        {
            if (_metadata.TryGetValue(entity, out var cached)) return cached;

            var response = (RetrieveEntityResponse)_target.Execute(new RetrieveEntityRequest
            {
                LogicalName = entity,
                EntityFilters = EntityFilters.Attributes
            });
            var dict = response.EntityMetadata.Attributes
                .GroupBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            _metadata[entity] = dict;
            _primaryIds[entity] = response.EntityMetadata.PrimaryIdAttribute;
            return dict;
        }
    }
}
