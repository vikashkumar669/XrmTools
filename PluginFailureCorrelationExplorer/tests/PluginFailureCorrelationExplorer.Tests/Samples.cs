using System;
using System.Collections.Generic;
using System.Linq;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Tests
{
    /// <summary>
    /// Synthetic trace fixtures shaped like what Dataverse writes to plugintracelog. Contoso and Fabrikam are fictional;
    /// no fixture contains real customer data.
    /// </summary>
    public static class Samples
    {
        public static readonly DateTime T0 = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

        public const string AccountPlugin = "Contoso.Plugins.AccountPreCreate";
        public const string ContactPlugin = "Contoso.Plugins.ContactSync";

        /// <summary>Dataverse "Unhandled exception" layout with an OrganizationServiceFault.</summary>
        public static string Fault(string message, string errorCode = "-2147220891", string faultMessage = null, Guid? activityId = null, DateTime? at = null) =>
            "Unhandled exception: \r\n" +
            "Exception type: System.ServiceModel.FaultException`1[Microsoft.Xrm.Sdk.OrganizationServiceFault]\r\n" +
            "Message: " + message + "\r\n" +
            "Detail: \r\n" +
            "<OrganizationServiceFault xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns=\"http://schemas.microsoft.com/xrm/2011/Contracts\">\r\n" +
            "  <ActivityId>" + (activityId ?? Guid.NewGuid()) + "</ActivityId>\r\n" +
            "  <ErrorCode>" + errorCode + "</ErrorCode>\r\n" +
            "  <HelpLink i:nil=\"true\" />\r\n" +
            "  <Message>" + (faultMessage ?? message) + "</Message>\r\n" +
            "  <Timestamp>" + (at ?? T0).ToString("o") + "</Timestamp>\r\n" +
            "  <ExceptionSource>PluginExecution</ExceptionSource>\r\n" +
            "  <InnerFault i:nil=\"true\" />\r\n" +
            "  <TraceText i:nil=\"true\" />\r\n" +
            "</OrganizationServiceFault>\r\n";

        /// <summary>Plain .NET exception text with user and platform frames.</summary>
        public static string DotNet(string type, string message, string userFrame = "Contoso.Plugins.AccountPreCreate.Execute(IServiceProvider serviceProvider)", int line = 42) =>
            type + ": " + message + "\r\n" +
            "   at " + userFrame + " in C:\\src\\Plugin.cs:line " + line + "\r\n" +
            "   at Microsoft.Crm.Sandbox.SandboxCodeUnit.Execute(IExecutionContext context)";

        public static PluginTraceRecord Trace(string type = AccountPlugin, string message = "Create", string table = "account",
            double minutes = 0, int? duration = 120, string exception = null, Guid? correlation = null, Guid? request = null,
            int? depth = 1, int? mode = 0, Guid? step = null, string block = null, bool withStart = true)
        {
            var created = T0.AddMinutes(minutes);
            return new PluginTraceRecord
            {
                Id = Guid.NewGuid(),
                CreatedOnUtc = created,
                TypeName = type,
                MessageName = message,
                PrimaryEntity = table,
                ExecutionDurationMs = duration,
                ExecutionStartUtc = withStart && duration.HasValue ? created.AddMilliseconds(-duration.Value) : (DateTime?)null,
                ExceptionDetails = exception,
                ExceptionLoaded = true,
                MessageBlock = block,
                MessageBlockLoaded = block != null,
                CorrelationId = correlation,
                RequestId = request,
                Depth = depth,
                Mode = mode,
                OperationType = 1,
                PluginStepId = step ?? Guid.NewGuid(),
            };
        }

        /// <summary>Trace with explicit start (ms after T0) and duration, for timing-sensitive correlation tests.</summary>
        public static PluginTraceRecord Timed(double startMs, int durationMs, int depth, Guid? correlation, string type = AccountPlugin,
            string message = "Update", string table = "account", int mode = 0, string exception = null, Guid? request = null)
        {
            var start = T0.AddMilliseconds(startMs);
            var t = Trace(type, message, table, 0, durationMs, exception, correlation, request, depth, mode);
            t.ExecutionStartUtc = start;
            t.CreatedOnUtc = start.AddMilliseconds(durationMs);
            return t;
        }

        public static TraceQuery Query(DateTime? from = null, DateTime? to = null) =>
            new TraceQuery { FromUtc = from ?? T0.AddHours(-1), ToUtc = to ?? T0.AddHours(23), ResultLimit = 5000 };

        public static OrganizationInfo Org() => new OrganizationInfo
        {
            ConnectionName = "Contoso DEV",
            OrganizationName = "contosodev",
            Url = "https://contosodev.crm.dynamics.com",
            EnvironmentId = "00000000-0000-0000-0000-00000000e0e0",
            TraceSetting = "All",
        };

        public static AnalysisResult Analyze(IEnumerable<PluginTraceRecord> traces, AnalysisOptions options = null, BaselineSnapshot baseline = null, TraceQuery query = null)
        {
            var engine = new AnalysisEngine(options ?? new AnalysisOptions());
            return engine.Analyze(traces, query ?? Query(), Org(), baseline, T0.AddDays(1));
        }

        public static List<PluginTraceRecord> Repeat(int count, Func<int, PluginTraceRecord> make) =>
            Enumerable.Range(0, count).Select(make).ToList();
    }

    /// <summary>Assertions the shared runner doesn't provide.</summary>
    public static class Check
    {
        public static void Null(object value, string context = null)
        {
            if (value != null) throw new AssertionException($"{(context == null ? "" : context + ": ")}expected null but was <{value}>");
        }

        public static void NotNull(object value, string context = null)
        {
            if (value == null) throw new AssertionException($"{(context == null ? "" : context + ": ")}expected a value but was null");
        }

        public static T Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T ex) { return ex; }
            catch (Exception ex) { throw new AssertionException($"expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}"); }
            throw new AssertionException($"expected {typeof(T).Name} but nothing was thrown");
        }

        public static void Close(double expected, double? actual, double tolerance = 0.0001, string context = null)
        {
            if (!actual.HasValue || Math.Abs(expected - actual.Value) > tolerance)
                throw new AssertionException($"{(context == null ? "" : context + ": ")}expected {expected} but was {actual?.ToString() ?? "null"}");
        }
    }
}
