using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PluginFailureCorrelationExplorer.Analysis
{
    /// <summary>The structural parts of an exception text.</summary>
    public sealed class ParsedException
    {
        /// <summary>Outer exception type (for Dataverse faults: the FaultException type).</summary>
        public string ExceptionType { get; set; }

        /// <summary>Inner .NET exception type named in the fault message, e.g. System.NullReferenceException.</summary>
        public string InnerExceptionType { get; set; }

        /// <summary>First message line.</summary>
        public string Message { get; set; }

        /// <summary>Organization Service error code, when present.</summary>
        public string ErrorCode { get; set; }

        /// <summary>All stack frames found, as "Namespace.Type.Method(args)" without file or line.</summary>
        public List<string> Frames { get; } = new List<string>();

        /// <summary>Frames outside the platform namespaces (System, Microsoft.Xrm, Microsoft.Crm…).</summary>
        public List<string> UserFrames { get; } = new List<string>();

        /// <summary>The type shown to users: the inner type when the outer one is a generic fault wrapper.</summary>
        public string DisplayType => !string.IsNullOrEmpty(InnerExceptionType) ? InnerExceptionType : ExceptionType;
    }

    /// <summary>
    /// Extracts the exception type, message, error code and stack frames from the text Dataverse stores in
    /// <c>plugintracelog.exceptiondetails</c>. Handles the Dataverse "Unhandled exception / Exception type / Message /
    /// Detail" layout with an OrganizationServiceFault, and plain .NET "Type: message" text with "at …" frames.
    /// </summary>
    public static class ExceptionParser
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        private static readonly Regex ExceptionTypeLine = new Regex(@"^\s*Exception type:\s*(?<v>.+)$", Options | RegexOptions.Multiline);
        private static readonly Regex MessageLine = new Regex(@"^\s*Message:\s*(?<v>.*)$", Options | RegexOptions.Multiline);
        private static readonly Regex ErrorCodeTag = new Regex(@"<ErrorCode>\s*(?<v>-?\d+|0x[0-9a-f]+)\s*</ErrorCode>", Options);
        private static readonly Regex FaultMessageTag = new Regex(@"<Message>\s*(?<v>[^<]*)</Message>", Options);
        private static readonly Regex InnerTypePrefix = new Regex(@"^(?<t>(?:[A-Za-z_][\w`]*\.)+[A-Za-z_][\w`]*(?:Exception|Fault))\s*:", RegexOptions.CultureInvariant);
        private static readonly Regex TypeAndMessage = new Regex(@"^(?<t>[\w.`\[\]]+(?:Exception|Fault)[\w`\[\].]*)\s*:\s*(?<m>.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Frame = new Regex(@"^\s*at\s+(?<m>[^\s(][^(]*\([^)]*\))", RegexOptions.CultureInvariant | RegexOptions.Multiline);
        private static readonly Regex CompilerGenerated = new Regex(@"(<>c__DisplayClass|b__|d__|<>c)\d+(_\d+)?", RegexOptions.CultureInvariant);
        private static readonly Regex GenericArity = new Regex(@"`\d+", RegexOptions.CultureInvariant);

        /// <summary>Namespaces treated as platform frames, not user code.</summary>
        public static readonly string[] PlatformPrefixes =
        {
            "System.", "Microsoft.Xrm.", "Microsoft.Crm.", "Microsoft.PowerPlatform.", "Microsoft.Cds.", "Microsoft.Dynamics.",
            "Microsoft.Identity", "Microsoft.Rest.", "Newtonsoft.", "mscorlib", "lambda_method", "Castle.", "Moq.",
        };

        public static ParsedException Parse(string exceptionDetails)
        {
            var parsed = new ParsedException();
            if (string.IsNullOrWhiteSpace(exceptionDetails)) return parsed;
            var text = exceptionDetails.Replace("\r\n", "\n").Replace('\r', '\n');

            var typeMatch = ExceptionTypeLine.Match(text);
            if (typeMatch.Success) parsed.ExceptionType = typeMatch.Groups["v"].Value.Trim();

            var messageMatch = MessageLine.Match(text);
            if (messageMatch.Success)
            {
                var message = messageMatch.Groups["v"].Value.Trim();
                if (message.Length == 0)
                {
                    // "Message:" followed by the text on the next non-empty line
                    var rest = text.Substring(messageMatch.Index + messageMatch.Length);
                    message = rest.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                }
                parsed.Message = message;
            }

            if (parsed.ExceptionType == null && parsed.Message == null)
            {
                // Plain .NET text: "Namespace.SomeException: message" then "   at ..." frames
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("at ", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("Unhandled exception", StringComparison.OrdinalIgnoreCase) && line.TrimEnd(':').Length <= "Unhandled exception".Length) continue;
                    var tm = TypeAndMessage.Match(line);
                    if (tm.Success)
                    {
                        parsed.ExceptionType = tm.Groups["t"].Value;
                        parsed.Message = tm.Groups["m"].Value.Trim();
                    }
                    else parsed.Message = line;
                    break;
                }
            }

            var code = ErrorCodeTag.Match(text);
            if (code.Success) parsed.ErrorCode = code.Groups["v"].Value.Trim();

            // The fault's <Message> often names the real .NET exception: "System.NullReferenceException: Microsoft Dynamics CRM has experienced an error…"
            foreach (Match m in FaultMessageTag.Matches(text))
            {
                var inner = InnerTypePrefix.Match(System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value.Trim()));
                if (inner.Success)
                {
                    parsed.InnerExceptionType = inner.Groups["t"].Value;
                    break;
                }
            }
            if (parsed.InnerExceptionType == null && parsed.Message != null)
            {
                var inner = InnerTypePrefix.Match(parsed.Message);
                if (inner.Success && !string.Equals(inner.Groups["t"].Value, parsed.ExceptionType, StringComparison.Ordinal))
                    parsed.InnerExceptionType = inner.Groups["t"].Value;
            }

            foreach (Match m in Frame.Matches(text))
            {
                var frame = NormalizeFrame(m.Groups["m"].Value);
                parsed.Frames.Add(frame);
                if (!IsPlatformFrame(frame)) parsed.UserFrames.Add(frame);
            }

            if (parsed.Message != null && parsed.Message.Length > 2000) parsed.Message = parsed.Message.Substring(0, 2000);
            return parsed;
        }

        /// <summary>Removes compiler-generated suffixes and generic arity so a frame survives rebuilds.</summary>
        public static string NormalizeFrame(string frame)
        {
            if (string.IsNullOrWhiteSpace(frame)) return "";
            var f = frame.Trim();
            f = CompilerGenerated.Replace(f, "$1");
            f = GenericArity.Replace(f, "`");
            return f;
        }

        public static bool IsPlatformFrame(string frame) =>
            PlatformPrefixes.Any(p => frame.StartsWith(p, StringComparison.Ordinal));
    }
}
