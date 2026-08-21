using System;
using System.Collections.Generic;
using System.Linq;
using Numenius.Core.Models;

namespace Numenius.Core.Utilities
{
    public class DeduplicationService
    {
        private readonly List<CachedMessage> _buffer = new();
        private readonly object _lock = new();
        private readonly int _timeWindowSeconds = 120;
        private readonly double _similarityThresholdHigh = 0.9;
        private readonly double _similarityThresholdLow = 0.85;
        private readonly int _bufferMaxSize = 50;

        public DeduplicationService(int timeWindowSeconds = 120, double thresholdHigh = 0.9, double thresholdLow = 0.85, int bufferMaxSize = 50)
        {
            _timeWindowSeconds = timeWindowSeconds;
            _similarityThresholdHigh = thresholdHigh;
            _similarityThresholdLow = thresholdLow;
            _bufferMaxSize = bufferMaxSize;
        }

        public bool IsDuplicate(string text, string sender, out double similarity, out string dupSender, out string? dupOriginalMessage)
        {
            similarity = 0;
            dupSender = string.Empty;
            dupOriginalMessage = null;

            string normalized = StringUtilities.NormalizeForDeduplication(text);
            string hash = StringUtilities.ComputeHash(normalized);

            lock (_lock)
            {
                CleanOldMessages();

                var exact = _buffer.Find(m => m.Hash == hash);
                if (exact != null)
                {
                    similarity = 1.0;
                    dupSender = exact.OriginalSender;
                    dupOriginalMessage = exact.OriginalMessage;
                    return true;
                }

                double best = 0;
                CachedMessage? bestMsg = null;
                foreach (var m in _buffer)
                {
                    double sim = StringUtilities.LevenshteinSimilarity(normalized, m.NormalizedText);
                    if (sim > best) { best = sim; bestMsg = m; }
                }
                if (best >= _similarityThresholdHigh)
                {
                    similarity = best;
                    dupSender = bestMsg?.OriginalSender ?? string.Empty;
                    dupOriginalMessage = bestMsg?.OriginalMessage ?? string.Empty;
                    return true;
                }
                if (best >= _similarityThresholdLow && bestMsg != null)
                {
                    // Дополнительная проверка Jaccard
                    double jaccard = StringUtilities.LevenshteinSimilarity(normalized, bestMsg.NormalizedText); // упрощённо
                    if (jaccard >= _similarityThresholdHigh)
                    {
                        similarity = best;
                        dupSender = bestMsg.OriginalSender;
                        dupOriginalMessage = bestMsg.OriginalMessage;
                        return true;
                    }
                }
                return false;
            }
        }

        public void AddToBuffer(string text, string sender, string originalMessage)
        {
            string normalized = StringUtilities.NormalizeForDeduplication(text);
            string hash = StringUtilities.ComputeHash(normalized);
            lock (_lock)
            {
                _buffer.Add(new CachedMessage
                {
                    NormalizedText = normalized,
                    Hash = hash,
                    OriginalSender = sender,
                    OriginalMessage = originalMessage,
                    ReceivedAt = DateTime.UtcNow
                });
                while (_buffer.Count > _bufferMaxSize)
                    _buffer.RemoveAt(0);
            }
        }

        public void ClearBuffer()
        {
            lock (_lock) _buffer.Clear();
        }

        private void CleanOldMessages()
        {
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-_timeWindowSeconds);
            _buffer.RemoveAll(m => m.ReceivedAt < cutoff);
        }

        private class CachedMessage
        {
            public string NormalizedText { get; set; } = string.Empty;
            public string Hash { get; set; } = string.Empty;
            public string OriginalSender { get; set; } = string.Empty;
            public string OriginalMessage { get; set; } = string.Empty;
            public DateTime ReceivedAt { get; set; }
        }
    }
}