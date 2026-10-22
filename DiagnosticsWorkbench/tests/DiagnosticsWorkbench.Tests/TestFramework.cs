using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace DiagnosticsWorkbench.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute { }

    public sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    public static class Assert
    {
        public static void True(bool condition, string message = "Expected true") { if (!condition) throw new AssertionException(message); }
        public static void False(bool condition, string message = "Expected false") { if (condition) throw new AssertionException(message); }

        public static void Equal<T>(T expected, T actual, string context = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertionException($"{(context == null ? "" : context + ": ")}expected <{expected}> but was <{actual}>");
        }

        public static void NotEqual<T>(T notExpected, T actual, string context = null)
        {
            if (EqualityComparer<T>.Default.Equals(notExpected, actual))
                throw new AssertionException($"{(context == null ? "" : context + ": ")}did not expect <{actual}>");
        }

        public static void Contains(string expected, string actual)
        {
            if (actual == null || actual.IndexOf(expected, StringComparison.Ordinal) < 0)
                throw new AssertionException($"expected text to contain <{expected}> but was <{Short(actual)}>");
        }

        public static void DoesNotContain(string unexpected, string actual)
        {
            if (actual != null && actual.IndexOf(unexpected, StringComparison.Ordinal) >= 0)
                throw new AssertionException($"expected text not to contain <{unexpected}> but was <{Short(actual)}>");
        }

        private static string Short(string s) => s == null ? "null" : s.Length > 400 ? s.Substring(0, 400) + "…" : s;
    }

    public static class Program
    {
        public static int Main(string[] args)
        {
            var filter = args.Length > 0 ? args[0] : null;
            var tests = typeof(Program).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .Where(m => filter == null || (m.DeclaringType.Name + "." + m.Name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(m => m.DeclaringType.Name).ThenBy(m => m.Name)
                .ToList();

            var failed = 0;
            var total = Stopwatch.StartNew();
            foreach (var test in tests)
            {
                var name = test.DeclaringType.Name + "." + test.Name;
                var sw = Stopwatch.StartNew();
                try
                {
                    test.Invoke(null, null);
                    Console.WriteLine($"  PASS  {name} ({sw.ElapsedMilliseconds} ms)");
                }
                catch (TargetInvocationException ex)
                {
                    failed++;
                    var inner = ex.InnerException;
                    Console.WriteLine($"  FAIL  {name}: {inner?.GetType().Name}: {inner?.Message}");
                    if (!(inner is AssertionException)) Console.WriteLine(inner?.StackTrace);
                }
            }
            Console.WriteLine();
            Console.WriteLine($"{tests.Count - failed} passed, {failed} failed, {tests.Count} total in {total.ElapsedMilliseconds} ms");
            return failed == 0 ? 0 : 1;
        }
    }
}
