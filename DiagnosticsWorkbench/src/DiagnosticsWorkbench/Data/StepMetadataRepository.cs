using System;
using System.Collections.Generic;
using System.Linq;
using DiagnosticsWorkbench.Models;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DiagnosticsWorkbench.Data
{
    /// <summary>Fetches the registration of the steps that appear in the loaded logs (200 IDs per query).</summary>
    public sealed class StepMetadataRepository
    {
        public const int BatchSize = 200;
        private readonly IOrganizationService _service;

        public StepMetadataRepository(IOrganizationService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public List<StepInfo> Load(IEnumerable<Guid> stepIds, Func<bool> keepGoing = null)
        {
            var ids = (stepIds ?? Enumerable.Empty<Guid>()).Where(g => g != Guid.Empty).Distinct().ToList();
            var steps = new Dictionary<Guid, StepInfo>();

            for (var i = 0; i < ids.Count; i += BatchSize)
            {
                if (keepGoing != null && !keepGoing()) break;
                var batch = ids.Skip(i).Take(BatchSize).Cast<object>().ToArray();
                foreach (var step in QuerySteps(batch)) steps[step.Id] = step;
                foreach (var image in QueryImages(batch))
                    if (steps.TryGetValue(image.Key, out var owner)) owner.Images.Add(image.Value);
            }
            return steps.Values.ToList();
        }

        private IEnumerable<StepInfo> QuerySteps(object[] ids)
        {
            var query = new QueryExpression("sdkmessageprocessingstep")
            {
                ColumnSet = new ColumnSet("name", "stage", "mode", "rank", "filteringattributes", "statecode", "sdkmessageid", "plugintypeid"),
                NoLock = true,
            };
            query.Criteria.AddCondition("sdkmessageprocessingstepid", ConditionOperator.In, ids);

            var filter = query.AddLink("sdkmessagefilter", "sdkmessagefilterid", "sdkmessagefilterid", JoinOperator.LeftOuter);
            filter.EntityAlias = "filter";
            filter.Columns = new ColumnSet("primaryobjecttypecode");

            var type = query.AddLink("plugintype", "plugintypeid", "plugintypeid", JoinOperator.LeftOuter);
            type.EntityAlias = "type";
            type.Columns = new ColumnSet("typename", "assemblyname");

            var assembly = type.AddLink("pluginassembly", "pluginassemblyid", "pluginassemblyid", JoinOperator.LeftOuter);
            assembly.EntityAlias = "asm";
            assembly.Columns = new ColumnSet("name", "version", "isolationmode");

            foreach (var e in _service.RetrieveMultiple(query).Entities)
            {
                yield return new StepInfo
                {
                    Id = e.Id,
                    Name = e.GetAttributeValue<string>("name"),
                    MessageName = e.GetAttributeValue<EntityReference>("sdkmessageid")?.Name,
                    PrimaryEntity = Aliased<string>(e, "filter.primaryobjecttypecode"),
                    Stage = e.GetAttributeValue<OptionSetValue>("stage")?.Value ?? 0,
                    Mode = e.GetAttributeValue<OptionSetValue>("mode")?.Value ?? 0,
                    Rank = e.GetAttributeValue<int?>("rank") ?? 1,
                    FilteringAttributes = e.GetAttributeValue<string>("filteringattributes"),
                    StateCode = e.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0,
                    PluginTypeName = Aliased<string>(e, "type.typename") ?? e.GetAttributeValue<EntityReference>("plugintypeid")?.Name,
                    AssemblyName = Aliased<string>(e, "asm.name") ?? Aliased<string>(e, "type.assemblyname"),
                    AssemblyVersion = Aliased<string>(e, "asm.version"),
                    IsolationMode = Aliased<OptionSetValue>(e, "asm.isolationmode")?.Value ?? 0,
                };
            }
        }

        private IEnumerable<KeyValuePair<Guid, StepImage>> QueryImages(object[] ids)
        {
            var query = new QueryExpression("sdkmessageprocessingstepimage")
            {
                ColumnSet = new ColumnSet("name", "entityalias", "imagetype", "attributes", "sdkmessageprocessingstepid"),
                NoLock = true,
            };
            query.Criteria.AddCondition("sdkmessageprocessingstepid", ConditionOperator.In, ids);

            foreach (var e in _service.RetrieveMultiple(query).Entities)
            {
                var stepRef = e.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid");
                if (stepRef == null) continue;
                yield return new KeyValuePair<Guid, StepImage>(stepRef.Id, new StepImage
                {
                    Id = e.Id,
                    Name = e.GetAttributeValue<string>("entityalias") ?? e.GetAttributeValue<string>("name"),
                    ImageType = e.GetAttributeValue<OptionSetValue>("imagetype")?.Value ?? 0,
                    Attributes = e.GetAttributeValue<string>("attributes"),
                });
            }
        }

        private static T Aliased<T>(Entity e, string name) where T : class =>
            e.Contains(name) ? e.GetAttributeValue<AliasedValue>(name)?.Value as T : null;
    }
}
