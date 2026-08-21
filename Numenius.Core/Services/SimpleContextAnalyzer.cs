using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Numenius.Core.Config;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;

namespace Numenius.Core.Services
{
    /// <summary>
    /// Простой контекстный анализатор на основе пересечения токенов.
    /// Учитывает тип угрозы, свежесть сообщений, стоп-слова.
    /// </summary>
    public class SimpleContextAnalyzer : IContextAnalyzer
    {
        private readonly List<ParsedMessage> _history = new();
        private readonly int _maxHistory;
        private readonly int _timeWindowMinutes;
        private readonly double _minScore;
        private readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "канал", "оповещения", "чистое", "небо", "резервный", "мах", "подпишись",
            "внимание", "вниманию", "режим", "фпв", "fpv", "бпла", "ударный", "дрон"
        };

        public SimpleContextAnalyzer(ContextAnalyzerConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            _maxHistory = config.MaxHistorySize;
            _timeWindowMinutes = config.TimeWindowMinutes;
            _minScore = config.MinScore;
        }

        public void AddMessage(ParsedMessage message)
        {
            if (message == null) return;
            lock (_history)
            {
                _history.Add(message);
                if (_history.Count > _maxHistory)
                    _history.RemoveAt(0);
            }
        }

        public ParsedMessage? FindContext(ParsedMessage current, string rawText)
        {
            // Если уже есть поселения, контекст не нужен
            if (current.Settlements.Count > 0)
                return null;

            var cutoff = DateTime.UtcNow.AddMinutes(-_timeWindowMinutes);
            List<ParsedMessage> candidates;
            lock (_history)
            {
                candidates = _history
                    .Where(m => m.ReceivedAt >= cutoff && m.Settlements.Count > 0)
                    .ToList();
            }

            if (candidates.Count == 0)
                return null;

            var currentTokens = GetTokens(rawText);
            var currentTokenSet = new HashSet<string>(currentTokens, StringComparer.OrdinalIgnoreCase);

            double bestScore = 0;
            ParsedMessage? bestMatch = null;

            foreach (var cand in candidates)
            {
                var candTokens = GetTokens(cand.CleanedText);
                var candTokenSet = new HashSet<string>(candTokens, StringComparer.OrdinalIgnoreCase);

                int overlap = currentTokenSet.Intersect(candTokenSet, StringComparer.OrdinalIgnoreCase).Count();
                double score = (double)overlap / Math.Max(currentTokenSet.Count, candTokenSet.Count);

                // Бонус за совпадение типа угрозы
                if (!string.IsNullOrEmpty(current.ThreatType) &&
                    string.Equals(current.ThreatType, cand.ThreatType, StringComparison.OrdinalIgnoreCase))
                {
                    score += 0.2;
                }

                // Бонус за свежесть (чем новее, тем лучше)
                double ageMinutes = (DateTime.UtcNow - cand.ReceivedAt).TotalMinutes;
                double freshness = Math.Max(0, 1 - ageMinutes / _timeWindowMinutes);
                score += freshness * 0.1;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = cand;
                }
            }

            if (bestScore >= _minScore && bestMatch != null)
                return bestMatch;
            return null;
        }

        private List<string> GetTokens(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();
            var words = Regex.Split(text.ToLowerInvariant(), @"[^\w\-]+")
                .Where(w => w.Length >= 3 && !_stopWords.Contains(w))
                .Distinct()
                .ToList();
            return words;
        }
    }
}