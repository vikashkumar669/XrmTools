using System;
using ChartDesignerPro.Abstractions;

namespace ChartDesignerPro.Data
{
    /// <summary>
    /// Routes core log calls to the XrmToolBox log (PluginControlBase.LogInfo / LogWarning / LogError).
    /// Callers pass messages and record names only; connection strings, tokens and passwords are never logged.
    /// </summary>
    internal sealed class PluginLog : ILog
    {
        private readonly Action<string, object[]> _info;
        private readonly Action<string, object[]> _warn;
        private readonly Action<string, object[]> _error;

        public PluginLog(Action<string, object[]> info, Action<string, object[]> warn, Action<string, object[]> error)
        {
            _info = info;
            _warn = warn;
            _error = error;
        }

        public void Info(string message, params object[] args) => Safe(_info, message, args);
        public void Warn(string message, params object[] args) => Safe(_warn, message, args);

        public void Error(Exception exception, string message, params object[] args)
        {
            var text = args == null || args.Length == 0 ? message : string.Format(message, args);
            Safe(_error, "{0}\r\n{1}", new object[] { text, exception?.ToString() });
        }

        private static void Safe(Action<string, object[]> sink, string message, object[] args)
        {
            try { sink?.Invoke(message, args ?? new object[0]); }
            catch { /* logging must never break the tool */ }
        }
    }
}
