// Guarded so these extension stubs drop out on net8 (whose BCL has the matching
// instance methods) once Core multi-targets net48;net8.0-windows.
#if !NET5_0_OR_GREATER
using System;
using System.Text;

namespace TurboSuite.Compatibility
{
    /// <summary>
    /// Extension-method polyfills for string APIs that exist on .NET Core 2.1+ / .NET 5+
    /// but NOT on netstandard2.0 or .NET Framework 4.8. They let TurboSuite.Core (and the
    /// future net48 Revit2024 shim) use the modern overloads with zero call-site changes.
    ///
    /// On net8 the BCL provides matching *instance* methods, which always win overload
    /// resolution over extension methods — so these stubs are inert when Core multi-targets
    /// net8.0-windows and only activate on the net48 / netstandard2.0 target.
    /// </summary>
    internal static class StringPolyfills
    {
        public static bool Contains(this string s, string value, StringComparison comparison)
            => s.IndexOf(value, comparison) >= 0;

        public static string[] Split(this string s, char separator, int count)
            => s.Split(new[] { separator }, count, StringSplitOptions.None);

        public static string Replace(this string s, string oldValue, string newValue, StringComparison comparison)
        {
            if (string.IsNullOrEmpty(oldValue)) return s;
            var sb = new StringBuilder();
            int prev = 0, idx;
            while ((idx = s.IndexOf(oldValue, prev, comparison)) >= 0)
            {
                sb.Append(s, prev, idx - prev);
                sb.Append(newValue);
                prev = idx + oldValue.Length;
            }
            sb.Append(s, prev, s.Length - prev);
            return sb.ToString();
        }
    }
}
#endif
