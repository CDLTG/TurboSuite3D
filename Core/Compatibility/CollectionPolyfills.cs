// Guarded so these drop out on net8 (whose BCL has the matching members) once
// Core multi-targets net48;net8.0-windows.
#if !NET5_0_OR_GREATER
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Compatibility
{
    /// <summary>
    /// Extension-method polyfills for collection APIs added in .NET Core 2.0 /
    /// .NET Standard 2.1 but absent from netstandard2.0 and .NET Framework 4.8.
    /// </summary>
    internal static class CollectionPolyfills
    {
        public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dict, TKey key)
            => dict.TryGetValue(key, out var value) ? value : default!;

        public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dict, TKey key, TValue defaultValue)
            => dict.TryGetValue(key, out var value) ? value : defaultValue;

        /// <summary>
        /// Mirrors HashSet&lt;T&gt;.TryGetValue: returns the element actually stored in
        /// the set (per its comparer) — e.g. the original-casing string for a
        /// case-insensitive set. O(n) here vs the BCL's O(1), acceptable for the
        /// small sets this is used on.
        /// </summary>
        public static bool TryGetValue<T>(this HashSet<T> set, T equalValue, out T actualValue)
        {
            var comparer = set.Comparer;
            foreach (var item in set)
            {
                if (comparer.Equals(item, equalValue))
                {
                    actualValue = item;
                    return true;
                }
            }
            actualValue = default!;
            return false;
        }

        public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, TValue value)
        {
            if (dict.ContainsKey(key)) return false;
            dict.Add(key, value);
            return true;
        }

        // Unlike the other members here, .NET Framework 4.8's reference graph in this
        // project already supplies Enumerable.ToHashSet (via the System.Memory/ClosedXML
        // transitive surface), so emitting it on net48 makes the call ambiguous (CS0121).
        // Keep it only for plain netstandard2.0 consumers, where it is genuinely missing.
#if !NETFRAMEWORK
        public static HashSet<T> ToHashSet<T>(this IEnumerable<T> source)
            => new HashSet<T>(source);

        public static HashSet<T> ToHashSet<T>(this IEnumerable<T> source, IEqualityComparer<T> comparer)
            => new HashSet<T>(source, comparer);
#endif

        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> kvp, out TKey key, out TValue value)
        {
            key = kvp.Key;
            value = kvp.Value;
        }
    }
}
#endif
