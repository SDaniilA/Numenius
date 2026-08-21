using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Numenius.Core.Config;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;
using Numenius.Core.Services;
using Numenius.Core.Utilities;

namespace Numenius.Core.Predictors
{
    /// <summary>
    /// Статистический предиктор на основе частот направлений, времени суток, дней недели.
    /// </summary>
    public class StatisticalPredictor : IPredictor
    {
        public string Name => "Statistical";

        private readonly IDatabaseService _db;
        private readonly HeuristicsConfig _heuristics;
        private readonly StatisticalPredictorConfig _config;
        private readonly FeatureExtractor _featureExtractor = new();
        private DateTime _lastUpdate = DateTime.MinValue;
        private Dictionary<string, double> _directionProbabilities = new();
        private Dictionary<(string TimeOfDay, string DayOfWeek), double> _timeProbabilities = new();

        public StatisticalPredictor(IDatabaseService db, HeuristicsConfig heuristics, StatisticalPredictorConfig config)
        {
            _db = db;
            _heuristics = heuristics;
            _config = config;
        }

        private async Task UpdateStatsAsync()
        {
            if ((DateTime.UtcNow - _lastUpdate).TotalHours < 1) return;

            var incidents = await _db.GetAllIncidentsAsync(_config.MaxDaysHistory);
            if (incidents == null || !incidents.Any())
                return;

            // 1. Частоты направлений
            var directionCounts = new Dictionary<string, int>();
            foreach (var inc in incidents)
            {
                if (inc.Points.Count < 2) continue;
                for (int i = 1; i < inc.Points.Count; i++)
                {
                    var from = inc.Points[i - 1].SettlementName;
                    var to = inc.Points[i].SettlementName;
                    if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) continue;
                    var key = $"{from}->{to}";
                    directionCounts.TryGetValue(key, out int val);
                    directionCounts[key] = val + 1;
                }
            }
            var total = directionCounts.Values.Sum();
            _directionProbabilities = directionCounts.ToDictionary(
                kv => kv.Key,
                kv => total > 0 ? (double)kv.Value / total : 0
            );

            // 2. Частоты по времени суток и дням недели
            var timeCounts = new Dictionary<(string, string), int>();
            foreach (var inc in incidents)
            {
                var features = _featureExtractor.ExtractFeatures(inc);
                var key = (features.TimeOfDay, features.DayOfWeek);
                timeCounts.TryGetValue(key, out int val);
                timeCounts[key] = val + 1;
            }
            var timeTotal = timeCounts.Values.Sum();
            _timeProbabilities = timeCounts.ToDictionary(
                kv => kv.Key,
                kv => timeTotal > 0 ? (double)kv.Value / timeTotal : 0
            );

            _lastUpdate = DateTime.UtcNow;
        }

        public async Task<Prediction?> GeneratePredictionAsync(Incident incident)
        {
            if (incident == null || incident.Points == null || incident.Points.Count < 2)
                return null;

            if (incident.Status == IncidentStatus.Terminated || incident.Status == IncidentStatus.Expired)
                return null;

            await UpdateStatsAsync();

            var features = _featureExtractor.ExtractFeatures(incident);
            var timeKey = (features.TimeOfDay, features.DayOfWeek);
            var timeProb = _timeProbabilities.TryGetValue(timeKey, out double tProb) ? tProb : 0.1;

            var lastPoint = incident.Points.Last();
            var candidateDirections = _directionProbabilities
                .Where(kv => kv.Key.StartsWith(lastPoint.SettlementName + "->"))
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .ToList();

            if (candidateDirections.Count == 0)
                return null;

            var affected = new List<string>();
            foreach (var kv in candidateDirections)
            {
                var parts = kv.Key.Split("->");
                if (parts.Length == 2 && !string.IsNullOrEmpty(parts[1]))
                    affected.Add(parts[1]);
            }

            DateTime windowStart = incident.LastSeen.AddMinutes(15);
            DateTime windowEnd = incident.LastSeen.AddMinutes(_heuristics.AttackWindowMinutes);
            var best = candidateDirections.First();
            var avgConfidence = best.Value * 0.7 + timeProb * 0.3;
            var confidence = Math.Clamp(avgConfidence + 0.1 * Math.Min(1, incident.Points.Count / 3.0), 0.0, 1.0);

            var prediction = new Prediction
            {
                IncidentId = incident.Id,
                ThreatType = incident.ThreatType,
                ZoneGeoJson = "",
                AffectedSettlements = affected,
                AttackWindowStart = windowStart,
                AttackWindowEnd = windowEnd,
                Confidence = confidence,
                Notes = $"Статистический прогноз: направление {best.Key}, переходов={_directionProbabilities.Count}"
            };

            GraphLogger.LogPrediction(incident.Id, incident.ThreatType, prediction.Confidence, string.Join(", ", prediction.AffectedSettlements));

            return prediction;
        }
    }
}