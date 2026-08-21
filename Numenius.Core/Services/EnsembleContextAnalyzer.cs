using System;
using System.Collections.Generic;
using System.Linq;
using Numenius.Core.Config;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;

namespace Numenius.Core.Services
{
    /// <summary>
    /// Ансамблевый анализатор: использует Simple и TfIdf, выбирает лучший по согласию.
    /// </summary>
    public class EnsembleContextAnalyzer : IContextAnalyzer
    {
        private readonly SimpleContextAnalyzer _simple;
        private readonly TfIdfContextAnalyzer _tfidf;
        private readonly double _minScore;

        public EnsembleContextAnalyzer(ContextAnalyzerConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            _simple = new SimpleContextAnalyzer(config);
            _tfidf = new TfIdfContextAnalyzer(config);
            _minScore = config.MinScore;
        }

        public void AddMessage(ParsedMessage message)
        {
            _simple.AddMessage(message);
            _tfidf.AddMessage(message);
        }

        public ParsedMessage? FindContext(ParsedMessage current, string rawText)
        {
            var simpleResult = _simple.FindContext(current, rawText);
            var tfidfResult = _tfidf.FindContext(current, rawText);

            // Если оба вернули null – нет контекста
            if (simpleResult == null && tfidfResult == null)
                return null;

            // Если один вернул – используем его
            if (simpleResult != null && tfidfResult == null)
                return simpleResult;
            if (simpleResult == null && tfidfResult != null)
                return tfidfResult;

            // Оба вернули – выбираем тот, у которого больше общих топонимов или выше уверенность
            if (simpleResult.Id == tfidfResult.Id)
                return simpleResult;

            // Если они указывают на разные сообщения, выбираем более свежее или с большим количеством поселений
            // В реальной ситуации можно возвращать то, у которого больше Settlements.Count
            if (simpleResult.Settlements.Count > tfidfResult.Settlements.Count)
                return simpleResult;
            else if (simpleResult.Settlements.Count < tfidfResult.Settlements.Count)
                return tfidfResult;
            else
                return simpleResult.ReceivedAt > tfidfResult.ReceivedAt ? simpleResult : tfidfResult;
        }
		public ParsedMessage? FindMessageById(string id)
		{
			var simple = _simple.FindMessageById(id);
			if (simple != null) return simple;
			return _tfidf.FindMessageById(id);
		}
    }
}