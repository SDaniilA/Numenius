using System;
using System.Collections.Generic;
using System.Linq;
using Numenius.Core.Config;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;

namespace Numenius.Core.Services
{
    /// <summary>
    /// TF-IDF анализатор контекста: вычисляет TF-IDF векторы и косинусное сходство.
    /// </summary>
    public class TfIdfContextAnalyzer : IContextAnalyzer
    {
        private readonly ContextAnalyzerConfig _config;
        private readonly List<ParsedMessage> _history = new();
        private Dictionary<string, double> _idf = new();
        private DateTime _lastIdfUpdate = DateTime.MinValue;

        public TfIdfContextAnalyzer(ContextAnalyzerConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public void AddMessage(ParsedMessage message)
        {
            if (message == null) return;
            lock (_history)
            {
                _history.Add(message);
                if (_history.Count > _config.MaxHistorySize)
                    _history.RemoveAt(0);
            }
            // Сбрасываем кэш IDF, так как словарь мог измениться
            _lastIdfUpdate = DateTime.MinValue;
        }

        public ParsedMessage? FindContext(ParsedMessage current, string rawText)
        {
            if (current.Settlements.Count > 0)
                return null;

            var cutoff = DateTime.UtcNow.AddMinutes(-_config.TimeWindowMinutes);
            List<ParsedMessage> candidates;
            lock (_history)
            {
                candidates = _history
                    .Where(m => m.ReceivedAt >= cutoff && m.Settlements.Count > 0)
                    .ToList();
            }

            if (candidates.Count == 0)
                return null;

            // Обновляем IDF, если необходимо
            UpdateIdf(candidates);

            // Вектор текущего сообщения
            var currentTokens = TextUtils.GetTokens(rawText);
            var currentVector = ComputeTfIdfVector(currentTokens);

            double bestScore = 0;
            ParsedMessage? bestMatch = null;

            foreach (var cand in candidates)
            {
                var candTokens = TextUtils.GetTokens(cand.CleanedText);
                var candVector = ComputeTfIdfVector(candTokens);
                double similarity = CosineSimilarity(currentVector, candVector);

                // Бонус за совпадение типа угрозы
                if (!string.IsNullOrEmpty(current.ThreatType) &&
                    string.Equals(current.ThreatType, cand.ThreatType, StringComparison.OrdinalIgnoreCase))
                    similarity += 0.1;

                // Бонус за свежесть
                double ageMinutes = (DateTime.UtcNow - cand.ReceivedAt).TotalMinutes;
                double freshness = Math.Max(0, 1 - ageMinutes / _config.TimeWindowMinutes);
                similarity += freshness * 0.05;

                if (similarity > bestScore)
                {
                    bestScore = similarity;
                    bestMatch = cand;
                }
            }

            return bestScore >= _config.MinScore ? bestMatch : null;
        }

        private void UpdateIdf(List<ParsedMessage> docs)
        {
            // Пересчитываем IDF только если прошло достаточно времени или словарь изменился
            if ((DateTime.UtcNow - _lastIdfUpdate).TotalMinutes < 1)
                return;

            var allTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var docFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var doc in docs)
            {
                var tokens = TextUtils.GetTokens(doc.CleanedText);
                foreach (var token in tokens)
                {
                    allTokens.Add(token);
                    if (!docFrequency.ContainsKey(token))
                        docFrequency[token] = 0;
                    docFrequency[token]++;
                }
            }

            int totalDocs = docs.Count;
            _idf = allTokens.ToDictionary(
                token => token,
                token => Math.Log((totalDocs + 1) / (docFrequency[token] + 1)) + 1.0
            );

            _lastIdfUpdate = DateTime.UtcNow;
        }

        private Dictionary<string, double> ComputeTfIdfVector(List<string> tokens)
        {
            var tf = tokens.GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                           .ToDictionary(g => g.Key, g => (double)g.Count() / tokens.Count, StringComparer.OrdinalIgnoreCase);

            var vector = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in tf)
            {
                double idf = _idf.TryGetValue(kv.Key, out double v) ? v : 1.0;
                vector[kv.Key] = kv.Value * idf;
            }
            return vector;
        }

        private double CosineSimilarity(Dictionary<string, double> v1, Dictionary<string, double> v2)
        {
            if (v1.Count == 0 || v2.Count == 0) return 0;
            double dot = 0;
            double norm1 = 0;
            double norm2 = 0;
            foreach (var kv in v1)
            {
                norm1 += kv.Value * kv.Value;
                if (v2.TryGetValue(kv.Key, out double val2))
                    dot += kv.Value * val2;
            }
            foreach (var kv in v2)
                norm2 += kv.Value * kv.Value;
            if (norm1 == 0 || norm2 == 0) return 0;
            return dot / (Math.Sqrt(norm1) * Math.Sqrt(norm2));
        }
    }
}