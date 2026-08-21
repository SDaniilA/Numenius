using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Numenius.Core.Utilities
{
    public static class StringUtilities
    {
        public static string CleanMessage(string text)
        {
            // Удаление ссылок
            text = Regex.Replace(text, @"https?://[^\s]+", "");
            // Удаление эмодзи (базовый набор)
            text = Regex.Replace(text, @"[\uD800-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u26FF]", "");
            // Удаление лишних пробелов
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }

        /// <summary>
        /// Нормализация текста для дедупликации.
        /// </summary>
        /// <param name="text">Исходный текст</param>
        /// <param name="normalizeTime">Заменять время на <TIME>?</param>
        public static string NormalizeForDeduplication(string text, bool normalizeTime = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            
            // Удаляем эмодзи и спецсимволы
            text = Regex.Replace(text, @"\p{Cs}|\p{So}", "");
            text = text.ToLowerInvariant();

            // Удаляем общие фразы, которые не несут смысла для дедупликации
            text = Regex.Replace(text, @"канал\s+в\s+max\s*-?", "");
            text = Regex.Replace(text, @"канал\s+оповещения\s*""[^""]*""", "");
            text = Regex.Replace(text, @"резервный\s+канал\s+мах-подпишись", "");
            text = Regex.Replace(text, @"https?://[^\s]+", "");
            text = Regex.Replace(text, @"\s+", " ").Trim();

            // Нормализация времени
            if (normalizeTime)
                text = Regex.Replace(text, @"\b\d{1,2}[:.-]\d{2}\b", "<TIME>");

            return text;
        }

        public static string ComputeHash(string text)
        {
            using var md5 = MD5.Create();
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = md5.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }

        public static int LevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return t?.Length ?? 0;
            if (string.IsNullOrEmpty(t)) return s.Length;
            var d = new int[s.Length + 1, t.Length + 1];
            for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= t.Length; j++) d[0, j] = j;
            for (int i = 1; i <= s.Length; i++)
                for (int j = 1; j <= t.Length; j++)
                {
                    int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            return d[s.Length, t.Length];
        }

        public static double LevenshteinSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2)) return 1.0;
            int maxLen = Math.Max(s1.Length, s2.Length);
            if (maxLen == 0) return 1.0;
            return 1.0 - (double)LevenshteinDistance(s1, s2) / maxLen;
        }

        public static double JaccardSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0;
            var hashSet1 = new HashSet<string>(GetBigrams(s1));
            var hashSet2 = new HashSet<string>(GetBigrams(s2));
            int intersection = hashSet1.Intersect(hashSet2).Count();
            int union = hashSet1.Union(hashSet2).Count();
            return union == 0 ? 0 : (double)intersection / union;
        }

        private static IEnumerable<string> GetBigrams(string text)
        {
            for (int i = 0; i < text.Length - 1; i++)
                yield return text.Substring(i, 2);
        }

        public static string TruncateToBytes(string text, int maxBytes)
        {
            if (string.IsNullOrEmpty(text)) return text;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length <= maxBytes) return text;
            Array.Resize(ref bytes, maxBytes);
            while (maxBytes > 0 && (bytes[maxBytes - 1] & 0xC0) == 0x80)
            {
                maxBytes--;
                Array.Resize(ref bytes, maxBytes);
            }
            string result = Encoding.UTF8.GetString(bytes);
            if (result.Length < text.Length) result += "...";
            return result;
        }

        public static string SanitizeForMqtt(string sender, string message, int maxBytes)
        {
            string text = $"[Telegram] {message}".Replace("[Telegram]", "TG:");
            text = Regex.Replace(text, @"\p{Cs}", "");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return TruncateToBytes(text, maxBytes);
        }

        public static string NormalizeTextForSpeech(string text, Dictionary<string, string> abbreviations)
        {
            if (abbreviations == null || !abbreviations.Any()) return text;
            foreach (var kv in abbreviations)
                text = Regex.Replace(text, $@"\b{Regex.Escape(kv.Key)}\b", kv.Value, RegexOptions.IgnoreCase);
            return text;
        }
    }
}