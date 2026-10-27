using System;
using System.Collections.Generic;
using PluginFailureCorrelationExplorer.Models;

namespace PluginFailureCorrelationExplorer.Rules
{
    /// <summary>
    /// Possible explanations for well-known exception types and Organization Service error codes. These are hypotheses
    /// offered to guide the investigation; findings always label them as possible explanations, never as the root cause.
    /// </summary>
    public static class KnownFailureHints
    {
        private static readonly Dictionary<string, string> ByErrorCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["-2147220891"] = "Error code -2147220891 (0x80040265) is what Dataverse records when plug-in code throws InvalidPluginExecutionException on purpose. This often means a validation rule rejected the input, so check whether the input data or the rule changed.",
            ["-2147220956"] = "Error code -2147220956 (0x80040224) usually means an unhandled exception escaped the plug-in code rather than being raised on purpose.",
            ["-2147220969"] = "Error code -2147220969 (0x80040217) means a record the code referenced does not exist. It may have been deleted, the ID may be wrong, or the record may not be visible to the calling user.",
            ["-2147220960"] = "Error code -2147220960 (0x80040220) usually means the calling or impersonated user is missing a privilege.",
            ["-2147204784"] = "Error code -2147204784 (0x80044150) is a generic SQL error. Deadlocks, lock timeouts and constraint violations are common causes, especially under concurrent load.",
            ["-2147015902"] = "Error code -2147015902 is a service protection limit on the number of requests: the code (or something running alongside it) sent too many requests.",
            ["-2147015903"] = "Error code -2147015903 is a service protection limit on combined execution time.",
            ["-2147015898"] = "Error code -2147015898 is a service protection limit on concurrent requests.",
        };

        private static readonly (string TypeFragment, string Hint)[] ByType =
        {
            ("NullReferenceException", "A NullReferenceException in plug-in code often means an expected attribute, image, lookup or context value was missing for this execution (for example, an attribute that isn't in Target on every Update)."),
            ("KeyNotFoundException", "\"The given key was not present\" usually means the code read an attribute with entity[\"name\"] when the attribute wasn't in Target or the image. Using GetAttributeValue or checking Contains avoids this."),
            ("InvalidCastException", "An InvalidCastException often means an attribute had a different type than the code expected (for example, OptionSetValue compared with EntityReference, or Money compared with decimal)."),
            ("TimeoutException", "A timeout suggests a long-running operation or a slow external call. Synchronous plug-ins are cancelled after 2 minutes."),
            ("WebException", "A web exception points to an external HTTP call. The external service may be slow, unavailable or rejecting the request."),
            ("HttpRequestException", "An HTTP request exception points to an external call. The external service may be slow, unavailable or rejecting the request."),
            ("SqlException", "A SQL exception is often caused by contention (deadlocks, lock timeouts) under concurrent load."),
            ("ArgumentNullException", "An ArgumentNullException usually means a required value (input parameter, attribute or lookup) was null for this execution."),
            ("SerializationException", "A serialization exception can mean a type or assembly version mismatch, for example after a partial deployment."),
            ("FileNotFoundException", "A FileNotFoundException often means a dependent assembly isn't available in the sandbox. Plug-ins must merge their dependencies or use a plug-in package."),
        };

        /// <summary>Possible explanations for a fingerprint (may be empty).</summary>
        public static List<string> For(FailureFingerprint fingerprint)
        {
            var hints = new List<string>();
            if (fingerprint == null) return hints;
            if (!string.IsNullOrEmpty(fingerprint.ErrorCode) && ByErrorCode.TryGetValue(fingerprint.ErrorCode, out var codeHint)) hints.Add(codeHint);
            foreach (var (fragment, hint) in ByType)
                if ((fingerprint.ExceptionType ?? "").IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) hints.Add(hint);
            var message = fingerprint.Message ?? "";
            if (message.IndexOf("given key was not present", StringComparison.OrdinalIgnoreCase) >= 0 && !hints.Exists(h => h.Contains("given key")))
                hints.Add(ByType[1].Hint);
            if (message.IndexOf("2 minutes", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("two minutes", StringComparison.OrdinalIgnoreCase) >= 0)
                hints.Add("The message mentions the 2-minute limit: the execution probably hit the synchronous plug-in timeout.");
            return hints;
        }
    }
}
