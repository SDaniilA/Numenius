using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Numenius.Core.Models;

namespace Numenius.Core.Services
{
    public class OutputCache
    {
        private readonly ConcurrentQueue<ParsedMessage> _messages = new();
        private readonly ConcurrentQueue<Prediction> _predictions = new();
        private readonly int _maxSize = 50;

        public event EventHandler<ParsedMessage>? OnNewParsedMessage;
        public event EventHandler<Prediction>? OnNewPrediction;

        public void AddParsedMessage(ParsedMessage msg)
        {
            if (msg == null) return;
            _messages.Enqueue(msg);
            while (_messages.Count > _maxSize)
                _messages.TryDequeue(out _);

            try
            {
                OnNewParsedMessage?.Invoke(this, msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Ошибка в обработчике OnNewParsedMessage: {ex.Message}");
                Console.WriteLine($"   StackTrace: {ex.StackTrace}");
            }
        }

        public void AddPrediction(Prediction pred)
        {
            if (pred == null) return;
            _predictions.Enqueue(pred);
            while (_predictions.Count > _maxSize)
                _predictions.TryDequeue(out _);

            try
            {
                OnNewPrediction?.Invoke(this, pred);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Ошибка в обработчике OnNewPrediction: {ex.Message}");
                Console.WriteLine($"   StackTrace: {ex.StackTrace}");
            }
        }

        public IEnumerable<ParsedMessage> GetRecentMessages() => _messages.ToArray();
        public IEnumerable<Prediction> GetRecentPredictions() => _predictions.ToArray();
    }
}