using System.Collections.Concurrent;
using Numenius.Core.Models;

namespace Numenius.Core.Services
{
    public static class SettlementCache
    {
        private static readonly ConcurrentDictionary<string, Settlement> _cache = new();

        public static void Add(string name, Settlement settlement)
        {
            _cache[name] = settlement;
        }

        public static Settlement? Get(string name)
        {
            _cache.TryGetValue(name, out var s);
            return s;
        }

        public static bool Contains(string name) => _cache.ContainsKey(name);

        public static void Clear() => _cache.Clear();
    }
}