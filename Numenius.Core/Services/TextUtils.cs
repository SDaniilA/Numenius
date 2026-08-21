using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Numenius.Core.Services
{
    public static class TextUtils
    {
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "канал", "оповещения", "чистое", "небо", "резервный", "мах", "подпишись",
            "внимание", "вниманию", "режим", "фпв", "fpv", "бпла", "ударный", "дрон",
            "отбой", "опасности", "всем", "покинуть", "автомобили"
        };

        public static List<string> GetTokens(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();
            var words = Regex.Split(text.ToLowerInvariant(), @"[^\w\-]+")
                .Where(w => w.Length >= 2 && !StopWords.Contains(w))
                .Distinct()
                .ToList();
            return words;
        }
    }
}