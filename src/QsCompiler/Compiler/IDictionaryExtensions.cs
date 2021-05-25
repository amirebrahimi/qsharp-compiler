using System.Collections.Generic;

namespace Microsoft.Quantum.QsCompiler
{
    static class IDictionaryExtensions
    {
        public static bool TryAdd(this IDictionary<string, string> dict, string key, string value)
        {
            if (dict.TryGetValue(key, out _))
                return false;

            dict.Add(key, value);

            return true;
        }
    }
}
