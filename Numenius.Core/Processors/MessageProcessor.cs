using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Numenius.Core.Config;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;
using Numenius.Core.Services;

namespace Numenius.Core.Processors
{
    public class MessageProcessor : IMessageProcessor
    {
        private readonly INlpParser _nlp;
        private readonly IGeoService _geo;
        private readonly IDatabaseService _db;
        private readonly IScenarioManager _scenarioManager;
        private readonly List<IPredictor> _predictors;
        private readonly OutputCache _outputCache;
        private readonly FilterConfig _filterConfig;

        public MessageProcessor(
            INlpParser nlp,
            IGeoService geo,
            IDatabaseService db,
            IScenarioManager scenarioManager,
            List<IPredictor> predictors,
            OutputCache outputCache,
            FilterConfig filterConfig)
        {
            _nlp = nlp;
            _geo = geo;
            _db = db;
            _scenarioManager = scenarioManager;
            _predictors = predictors;
            _outputCache = outputCache;
            _filterConfig = filterConfig ?? new FilterConfig();
        }

        public async Task<ParsedMessage?> ProcessAsync(RawMessage raw, CancellationToken cancellationToken)
        {
            if (_filterConfig.AllowedSenders.Count > 0)
            {
                bool allowed = _filterConfig.AllowedSenders.Any(a =>
                    raw.Sender.Contains(a, StringComparison.OrdinalIgnoreCase));
                if (!allowed) return null;
            }
            if (_filterConfig.BlacklistedSenders.Count > 0)
            {
                bool blocked = _filterConfig.BlacklistedSenders.Any(b =>
                    raw.Sender.Contains(b, StringComparison.OrdinalIgnoreCase));
                if (blocked) return null;
            }

            var parsed = _nlp.Parse(raw.Text, raw.Sender, raw.SourceType, raw.ReceivedAt);

            if (parsed.Settlements.Count == 0 && parsed.Status == "Active" && parsed.Flags.Count == 0)
            {
                await _db.SaveParsedMessageAsync(parsed);
                _outputCache.AddParsedMessage(parsed);
                Console.WriteLine($"ℹ️ Сообщение без НП и без статуса: {parsed.CleanedText}");
                return parsed;
            }

            if (parsed.Settlements.Count == 0 && (parsed.Status != "Active" || parsed.Flags.Count > 0))
            {
                var incidentWithoutGeo = await _scenarioManager.ProcessParsedMessageAsync(parsed);
                if (incidentWithoutGeo != null)
                {
                    foreach (var predictor in _predictors)
                    {
                        var prediction = await predictor.GeneratePredictionAsync(incidentWithoutGeo);
                        if (prediction != null)
                        {
                            await _db.SavePredictionAsync(prediction, predictor.Name);
                            _outputCache.AddPrediction(prediction);
                        }
                    }
                }
                await _db.SaveParsedMessageAsync(parsed);
                _outputCache.AddParsedMessage(parsed);
                return parsed;
            }

            double sourceWeight = await _db.GetSourceWeightAsync(raw.Sender);
            parsed.Confidence *= (0.5 + 0.5 * sourceWeight);
            parsed.Confidence = Math.Clamp(parsed.Confidence, 0.0, 1.0);

            await _db.SaveParsedMessageAsync(parsed);

            var incident = await _scenarioManager.ProcessParsedMessageAsync(parsed);

            if (incident != null && incident.Status != IncidentStatus.Terminated)
            {
                foreach (var predictor in _predictors)
                {
                    var prediction = await predictor.GeneratePredictionAsync(incident);
                    if (prediction != null)
                    {
                        await _db.SavePredictionAsync(prediction, predictor.Name);
                        _outputCache.AddPrediction(prediction);
                    }
                }
            }

            _outputCache.AddParsedMessage(parsed);

            return parsed;
        }
    }

    public class FilterConfig
    {
        public List<string> AllowedSenders { get; set; } = new();
        public List<string> BlacklistedSenders { get; set; } = new();
    }
}