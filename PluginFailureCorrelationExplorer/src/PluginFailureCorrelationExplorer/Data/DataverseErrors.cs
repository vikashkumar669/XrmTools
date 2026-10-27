using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.ServiceModel;
using System.ServiceModel.Security;
using System.Threading;
using Microsoft.Xrm.Sdk;

namespace PluginFailureCorrelationExplorer.Data
{
    /// <summary>What went wrong with a Dataverse call, in terms the user can act on.</summary>
    public enum DataverseErrorKind
    {
        Unknown,
        Throttled,
        InsufficientPrivileges,
        SessionExpired,
        Network,
        Timeout,
        InvalidQuery,
        Cancelled,
    }

    /// <summary>
    /// Classifies Dataverse exceptions and produces messages without stack traces, tokens or connection details.
    /// </summary>
    public static class DataverseErrors
    {
        // Service protection limits
        public const int ThrottleRequests = -2147015902;
        public const int ThrottleExecutionTime = -2147015903;
        public const int ThrottleConcurrency = -2147015898;

        // Security
        /// <summary>0x80040220: the user is missing a privilege. Other privilege errors are recognized by their message.</summary>
        public const int PrivilegeDenied = -2147220960;

        public static DataverseErrorKind Classify(Exception ex)
        {
            ex = Unwrap(ex);
            switch (ex)
            {
                case null: return DataverseErrorKind.Unknown;
                case OperationCanceledException _: return DataverseErrorKind.Cancelled;
                case FaultException<OrganizationServiceFault> fault:
                    var code = fault.Detail?.ErrorCode ?? 0;
                    if (IsThrottle(code)) return DataverseErrorKind.Throttled;
                    if (code == PrivilegeDenied) return DataverseErrorKind.InsufficientPrivileges;
                    var text = fault.Detail?.Message ?? fault.Message ?? "";
                    if (text.IndexOf("privilege", StringComparison.OrdinalIgnoreCase) >= 0) return DataverseErrorKind.InsufficientPrivileges;
                    if (text.IndexOf("fetchxml", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("condition", StringComparison.OrdinalIgnoreCase) >= 0
                        || text.IndexOf("attribute", StringComparison.OrdinalIgnoreCase) >= 0) return DataverseErrorKind.InvalidQuery;
                    return DataverseErrorKind.Unknown;
                case MessageSecurityException _:
                case SecurityAccessDeniedException _:
                    return DataverseErrorKind.SessionExpired;
                case TimeoutException _: return DataverseErrorKind.Timeout;
                case WebException web when web.Status == WebExceptionStatus.Timeout: return DataverseErrorKind.Timeout;
                case WebException web when (web.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.Unauthorized: return DataverseErrorKind.SessionExpired;
                case WebException web when (web.Response as HttpWebResponse)?.StatusCode == (HttpStatusCode)429: return DataverseErrorKind.Throttled;
                case WebException _:
                case SocketException _:
                case CommunicationException _:
                    return DataverseErrorKind.Network;
            }
            var msg = ex.Message ?? "";
            if (msg.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 && msg.IndexOf("expir", StringComparison.OrdinalIgnoreCase) >= 0) return DataverseErrorKind.SessionExpired;
            if (msg.IndexOf("401", StringComparison.Ordinal) >= 0 || msg.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0) return DataverseErrorKind.SessionExpired;
            return DataverseErrorKind.Unknown;
        }

        public static bool IsThrottle(int code) => code == ThrottleRequests || code == ThrottleExecutionTime || code == ThrottleConcurrency;

        /// <summary>A message for the user. Never includes the stack trace.</summary>
        public static string Describe(Exception ex)
        {
            var kind = Classify(ex);
            var inner = Unwrap(ex);
            switch (kind)
            {
                case DataverseErrorKind.Throttled:
                    return "Dataverse service protection limits were reached. Wait a minute and try again, or narrow the date range or lower the result limit.";
                case DataverseErrorKind.InsufficientPrivileges:
                    return "Your user can't read the plug-in trace logs. The tool needs read access to plugintracelog (and, for step details, sdkmessageprocessingstep, plugintype and pluginassembly). Ask an administrator for a role with these privileges, such as System Customizer.";
                case DataverseErrorKind.SessionExpired:
                    return "The connection to Dataverse has expired or was rejected. Reconnect in XrmToolBox and try again.";
                case DataverseErrorKind.Timeout:
                    return "Dataverse didn't answer in time. Narrow the date range or filters and try again.";
                case DataverseErrorKind.Network:
                    return "Dataverse couldn't be reached. Check your network connection and try again.";
                case DataverseErrorKind.InvalidQuery:
                    return "Dataverse rejected the query: " + FirstLine(FaultMessage(inner));
                case DataverseErrorKind.Cancelled:
                    return "The operation was cancelled.";
                default:
                    return "The operation failed: " + FirstLine(FaultMessage(inner));
            }
        }

        /// <summary>Retry-After from a throttling fault, when Dataverse provided one.</summary>
        public static TimeSpan? RetryAfter(Exception ex)
        {
            if (Unwrap(ex) is FaultException<OrganizationServiceFault> fault && fault.Detail?.ErrorDetails != null
                && fault.Detail.ErrorDetails.TryGetValue("Retry-After", out var value))
            {
                if (value is TimeSpan ts) return ts;
                if (value is int seconds) return TimeSpan.FromSeconds(seconds);
                if (value is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return TimeSpan.FromSeconds(d);
            }
            return null;
        }

        private static Exception Unwrap(Exception ex)
        {
            while (ex is AggregateException agg && agg.InnerExceptions.Count == 1) ex = agg.InnerExceptions[0];
            while (ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null) ex = tie.InnerException;
            return ex;
        }

        private static string FaultMessage(Exception ex) =>
            ex is FaultException<OrganizationServiceFault> f ? (f.Detail?.Message ?? f.Message) : ex?.Message ?? "unknown error";

        private static string FirstLine(string text)
        {
            var line = (text ?? "").Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
            line = new Reporting.SensitiveDataRedactor(Reporting.RedactionOptions.None).Redact(line.Trim());
            return line.Length > 300 ? line.Substring(0, 300) + "…" : line;
        }
    }

    /// <summary>
    /// Runs a Dataverse call, retrying service protection faults at most twice and honouring Retry-After (capped).
    /// Other errors are not retried. Waits check for cancellation every 250 ms.
    /// </summary>
    public static class DataverseRetry
    {
        public const int MaxRetries = 2;
        public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

        public static T Execute<T>(Func<T> call, Func<bool> isCancelled = null, Action<string> report = null)
        {
            for (var attempt = 0; ; attempt++)
            {
                if (isCancelled != null && isCancelled()) throw new OperationCanceledException();
                try
                {
                    return call();
                }
                catch (Exception ex) when (attempt < MaxRetries && DataverseErrors.Classify(ex) == DataverseErrorKind.Throttled)
                {
                    var wait = DataverseErrors.RetryAfter(ex) ?? DefaultWait;
                    if (wait > MaxWait) wait = MaxWait;
                    if (wait < TimeSpan.Zero) wait = DefaultWait;
                    report?.Invoke($"Service protection limit reached; waiting {Math.Ceiling(wait.TotalSeconds)} s before retry {attempt + 1} of {MaxRetries}...");
                    var until = DateTime.UtcNow + wait;
                    while (DateTime.UtcNow < until)
                    {
                        if (isCancelled != null && isCancelled()) throw new OperationCanceledException();
                        Thread.Sleep(250);
                    }
                }
            }
        }
    }
}
