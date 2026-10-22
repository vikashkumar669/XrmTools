using System;
using System.Threading;
using Microsoft.Xrm.Sdk;

namespace DiagnosticsWorkbench.TestPlugin
{
    /// <summary>
    /// Registered on Update of account. Acts only when the new description starts with "DWTEST:".
    ///   DWTEST:fail       InvalidPluginExecutionException with a GUID and timestamp (same fingerprint every time)
    ///   DWTEST:null       NullReferenceException (a second error group)
    ///   DWTEST:slow       sleeps 3 s (sync threshold is 2 s)
    ///   DWTEST:veryslow   sleeps 62 s (near the 2-minute sync timeout)
    ///   DWTEST:recurse    updates itself until depth 3 (a 3-step chain, possible recursion)
    ///   DWTEST:chainfail  updates itself until depth 3, then fails (a failed chain)
    ///   DWTEST:asyncslow  the asynchronous step sleeps 12 s (async threshold is 10 s)
    /// </summary>
    public sealed class ScenarioPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            if (!(context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity target)) return;

            var command = target.GetAttributeValue<string>("description");
            if (string.IsNullOrEmpty(command) || !command.StartsWith("DWTEST:", StringComparison.OrdinalIgnoreCase)) return;
            command = command.Substring(7).Trim().ToLowerInvariant();
            var async = context.Mode == 1;

            trace.Trace("DWTEST scenario '{0}' on account {1}, depth {2}, {3}", command, target.Id, context.Depth, async ? "async" : "sync");

            if (async)
            {
                if (command == "asyncslow") { trace.Trace("Sleeping 12 s"); Thread.Sleep(12000); }
                return;
            }

            switch (command)
            {
                case "fail":
                    trace.Trace("Checking credit limit");
                    throw new InvalidPluginExecutionException(
                        $"Credit check failed for account {target.Id} at {DateTime.UtcNow:o}: limit 50000 exceeded by {new Random().Next(100, 99999)}.");
                case "null":
                    string missing = null;
                    trace.Trace("About to read a missing value");
                    trace.Trace(missing.Length.ToString());
                    break;
                case "slow":
                    trace.Trace("Calling slow dependency");
                    Thread.Sleep(3000);
                    break;
                case "veryslow":
                    trace.Trace("Calling very slow dependency");
                    Thread.Sleep(62000);
                    break;
                case "recurse":
                case "chainfail":
                    if (context.Depth < 3)
                    {
                        trace.Trace("Re-updating the account (depth {0})", context.Depth);
                        var service = ((IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory))).CreateOrganizationService(context.UserId);
                        service.Update(new Entity("account", target.Id) { ["description"] = "DWTEST:" + command });
                    }
                    else if (command == "chainfail")
                    {
                        throw new InvalidPluginExecutionException($"Sync to ERP failed at depth {context.Depth} for order ORD-{new Random().Next(1000, 9999)}.");
                    }
                    break;
            }
        }
    }
}
