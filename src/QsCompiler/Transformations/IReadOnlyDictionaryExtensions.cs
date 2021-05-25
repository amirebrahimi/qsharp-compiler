using System.Collections.Generic;

namespace Microsoft.Quantum.QsCompiler
{
    static class IReadOnlyDictionaryExtensions
    {
        public static string GetValueOrDefault(this IReadOnlyDictionary<string, string> dict, string key)
        {
            if (!dict.TryGetValue(key, out var value))
                value = string.Empty;

            return value;
        }
    }
}
