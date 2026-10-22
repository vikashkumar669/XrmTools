using System;
using System.Collections.Generic;
using DiagnosticsWorkbench.Models;

namespace DiagnosticsWorkbench.Tests
{
    /// <summary>Realistic plugintracelog content, shaped like what Dataverse writes.</summary>
    public static class Samples
    {
        public static readonly DateTime T0 = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

        public static string FaultException(string message, string errorCode = "-2147220891") =>
            "Unhandled exception: \r\n" +
            "Exception type: System.ServiceModel.FaultException`1[Microsoft.Xrm.Sdk.OrganizationServiceFault]\r\n" +
            "Message: " + message + "\r\n" +
            "Detail: \r\n" +
            "<OrganizationServiceFault xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns=\"http://schemas.microsoft.com/xrm/2011/Contracts\">\r\n" +
            "  <ActivityId>" + Guid.NewGuid() + "</ActivityId>\r\n" +
            "  <ErrorCode>" + errorCode + "</ErrorCode>\r\n" +
            "  <ErrorDetails xmlns:d2p1=\"http://schemas.datacontract.org/2004/07/System.Collections.Generic\" />\r\n" +
            "  <HelpLink i:nil=\"true\" />\r\n" +
            "  <Message>" + message + "</Message>\r\n" +
            "  <Timestamp>" + DateTime.UtcNow.ToString("o") + "</Timestamp>\r\n" +
            "  <ExceptionRetriable>false</ExceptionRetriable>\r\n" +
            "  <ExceptionSource>PluginExecution</ExceptionSource>\r\n" +
            "  <InnerFault i:nil=\"true\" />\r\n" +
            "  <OriginalException>PluginExecution</OriginalException>\r\n" +
            "  <TraceText i:nil=\"true\" />\r\n" +
            "</OrganizationServiceFault>\r\n";

        public static string DotNetException(string type, string message) =>
            type + ": " + message + "\r\n" +
            "   at Contoso.Plugins.AccountPreCreate.Execute(IServiceProvider serviceProvider) in C:\\src\\AccountPreCreate.cs:line 42\r\n" +
            "   at Microsoft.Crm.Sandbox.SandboxCodeUnit.Execute(IExecutionContext context)";

        public static TraceLog Log(string type = "Contoso.Plugins.AccountPreCreate", string message = "Create", string table = "account",
            int minutes = 0, int? duration = 120, string exception = null, Guid? correlation = null, int depth = 1, int mode = 0,
            Guid? step = null, string block = null, Guid? request = null)
        {
            return new TraceLog
            {
                Id = Guid.NewGuid(),
                CreatedOn = T0.AddMinutes(minutes),
                TypeName = type,
                MessageName = message,
                PrimaryEntity = table,
                ExecutionDurationMs = duration,
                ExceptionDetails = exception,
                CorrelationId = correlation,
                RequestId = request,
                Depth = depth,
                Mode = mode,
                StepId = step,
                MessageBlock = block,
                OperationType = 1,
            };
        }

        public static StepInfo Step(Guid id, string name, string message = "Update", string filtering = null, params StepImage[] images) =>
            new StepInfo
            {
                Id = id, Name = name, MessageName = message, PrimaryEntity = "account", Stage = 40, Mode = 0, Rank = 1,
                FilteringAttributes = filtering, PluginTypeName = "Contoso.Plugins.AccountSync", AssemblyName = "Contoso.Plugins",
                AssemblyVersion = "1.0.0.0", IsolationMode = 2, Images = new List<StepImage>(images)
            };
    }
}
