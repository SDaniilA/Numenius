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
    /// Статистический предиктор на основе частот направлений, времени суток, дней недели и цепочек переходов.
    /// Учитывает тип угрозы, расстояние и доверительные интервалы.
    /// </summary>
    public class StatisticalPredictor : IPredictor
    {
        public string Name => "Statistical";

        private readonly IDatabaseService _db;
        private readonly IGeoService _geo;
        private readonly HeuristicsConfig _heuristics;
        private readonly StatisticalPredictorConfig _config;
        private readonly FeatureExtractor _featureExtractor = new();

        // Кэш статистики
        private DateTime _lastUpdate = DateTime.MinValue;
        private readonly Dictionary<string, DirectionStats> _directionStats = new(); // ключ: ThreatType
        private readonly Dictionary<string, TimeStats> _timeStats = new(); // ключ: ThreatType + TimeOfDay + DayOfWeek (можно отдельно)
        private readonly object _lock = new();

        public StatisticalPredictor(IDatabaseService db, IGeoService geo, HeuristicsConfig heuristics, StatisticalPredictorConfig config)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _geo = geo ?? throw new ArgumentNullException(nameof(geo));
            _heuristics = heuristics ?? throw new ArgumentNullException(nameof(heuristics));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Обновляет статистики на основе истории инцидентов.
        /// </summary>
        private async Task UpdateStatsAsync()
        {
            // Обновляем не чаще 1 часа
            if ((DateTime.UtcNow - _lastUpdate).TotalHours < 1)
                return;

            var incidents = await _db.GetAllIncidentsAsync(_config.MaxDaysHistory);
            if (incidents == null || !incidents.Any())
                return;

            lock (_lock)
            {
                _directionStats.Clear();
                _timeStats.Clear();

                var sourceWeights = _db.GetAllSourceWeightsAsync().GetAwaiter().GetResult();

                // 1. Группируем по типу угрозы
                var byType = incidents.GroupBy(i => i.ThreatType ?? "Unknown");

                foreach (var typeGroup in byType)
                {
                    string threatType = typeGroup.Key;
                    var typeIncidents = typeGroup.ToList();

                    // Статистика направлений (включая цепочки)
                    var directionCounts = new Dictionary<string, int>();
                    var distanceSum = new Dictionary<string, double>();
                    var distanceCount = new Dictionary<string, int>();
                    var directionWeights = new Dictionary<string, double>(); // сумма весов источников

                    // Статистика времени
                    var timeCounts = new Dictionary<string, int>();

                    foreach (var inc in typeIncidents)
                    {
                        // Вычисляем средний вес источника для этого инцидента
                        double incWeight = GetAverageSourceWeight(inc, sourceWeights);

                        // 1.1 Направления (одношаговые)
                        for (int i = 1; i < inc.Points.Count; i++)
                        {
                            var from = inc.Points[i - 1];
                            var to = inc.Points[i];
                            if (string.IsNullOrEmpty(from.SettlementName) || string.IsNullOrEmpty(to.SettlementName))
                                continue;
                            string dirKey = $"{from.SettlementName}->{to.SettlementName}";

                            if (!directionCounts.ContainsKey(dirKey))
                            {
                                directionCounts[dirKey] = 0;
                                distanceSum[dirKey] = 0;
                                distanceCount[dirKey] = 0;
                                directionWeights[dirKey] = 0;
                            }
                            directionCounts[dirKey]++;
                            double dist = _geo.CalculateDistance(from.Lat, from.Lon, to.Lat, to.Lon);
                            distanceSum[dirKey] += dist;
                            distanceCount[dirKey]++;
                            directionWeights[dirKey] += incWeight;
                        }

                        // 1.2 Цепочки (биграммы) – учитываем две последние точки
                        if (inc.Points.Count >= 3)
                        {
                            var lastPoints = inc.Points.TakeLast(3).ToList();
                            for (int i = 2; i < lastPoints.Count; i++)
                            {
                                var prev2 = lastPoints[i - 2];
                                var prev1 = lastPoints[i - 1];
                                var curr = lastPoints[i];
                                if (string.IsNullOrEmpty(prev2.SettlementName) || string.IsNullOrEmpty(prev1.SettlementName) || string.IsNullOrEmpty(curr.SettlementName))
                                    continue;
                                string chainKey = $"{prev2.SettlementName}->{prev1.SettlementName}->{curr.SettlementName}";
                                if (!directionCounts.ContainsKey(chainKey))
                                {
                                    directionCounts[chainKey] = 0;
                                    distanceSum[chainKey] = 0;
                                    distanceCount[chainKey] = 0;
                                    directionWeights[chainKey] = 0;
                                }
                                directionCounts[chainKey]++;
                                double dist1 = _geo.CalculateDistance(prev2.Lat, prev2.Lon, prev1.Lat, prev1.Lon);
                                double dist2 = _geo.CalculateDistance(prev1.Lat, prev1.Lon, curr.Lat, curr.Lon);
                                double totalDist = dist1 + dist2;
                                distanceSum[chainKey] += totalDist;
                                distanceCount[chainKey] += 2; // среднее расстояние для цепочки
                                directionWeights[chainKey] += incWeight;
                            }
                        }

                        // 1.3 Временные паттерны (время суток + день недели)
                        var features = _featureExtractor.ExtractFeatures(inc);
                        string timeKey = $"{features.TimeOfDay}|{features.DayOfWeek}";
                        if (!timeCounts.ContainsKey(timeKey))
                            timeCounts[timeKey] = 0;
                        timeCounts[timeKey]++;
                    }

                    // 2. Вычисляем вероятности для направлений (байесовское сглаживание)
                    var dirStats = new DirectionStats();
                    double totalTransitions = directionCounts.Values.Sum();
                    int totalDirections = directionCounts.Count;
                    const double alpha = 1.0;

                    // Для каждого направления и цепочки
                    foreach (var kv in directionCounts)
                    {
                        string key = kv.Key;
                        int count = kv.Value;
                        double prob = (count + alpha) / (totalTransitions + alpha * totalDirections);
                        double avgDist = distanceCount.ContainsKey(key) && distanceCount[key] > 0
                            ? distanceSum[key] / distanceCount[key]
                            : 0;
                        double avgWeight = directionWeights.ContainsKey(key) && count > 0
                            ? directionWeights[key] / count
                            : 0.5;

                        dirStats.Probabilities[key] = prob;
                        dirStats.Counts[key] = count;
                        dirStats.AverageDistances[key] = avgDist;
                        dirStats.AverageSourceWeights[key] = avgWeight;
                    }

                    // 3. Временные вероятности (также сглаживание)
                    var timeStats = new TimeStats();
                    double totalTime = timeCounts.Values.Sum();
                    int totalTimeKeys = timeCounts.Count;
                    foreach (var kv in timeCounts)
                    {
                        timeStats.Probabilities[kv.Key] = (kv.Value + alpha) / (totalTime + alpha * totalTimeKeys);
                    }

                    _directionStats[threatType] = dirStats;
                    _timeStats[threatType] = timeStats;
                }

                _lastUpdate = DateTime.UtcNow;
                GraphLogger.Log($"Статистический предиктор обновлён: {_directionStats.Count} типов угроз");
            }
        }

        /// <summary>
        /// Генерирует прогноз на основе текущего инцидента.
        /// </summary>
        public async Task<Prediction?> GeneratePredictionAsync(Incident incident)
        {
            if (incident == null || incident.Points == null || incident.Points.Count < 2)
                return null;

            if (incident.Status == IncidentStatus.Terminated || incident.Status == IncidentStatus.Expired)
                return null;

            await UpdateStatsAsync();

            string threatType = incident.ThreatType ?? "Unknown";
            if (!_directionStats.ContainsKey(threatType))
                return null; // нет статистики для этого типа

            var dirStats = _directionStats[threatType];
            var timeStats = _timeStats.ContainsKey(threatType) ? _timeStats[threatType] : null;

            var features = _featureExtractor.ExtractFeatures(incident);
            string timeKey = $"{features.TimeOfDay}|{features.DayOfWeek}";
            double timeProb = timeStats != null && timeStats.Probabilities.ContainsKey(timeKey)
                ? timeStats.Probabilities[timeKey]
                : 0.1; // дефолтная

            // Собираем кандидатов: учитываем последние 1-2 точки
            var candidates = new List<(string Direction, double Score, double Probability, double AvgWeight)>();

            // 1. Одношаговые переходы из последней точки
            var lastPoint = incident.Points.Last();
            var lastSettlement = lastPoint.SettlementName;
            if (!string.IsNullOrEmpty(lastSettlement))
            {
                var singleStep = dirStats.Probabilities
                    .Where(kv => kv.Key.StartsWith(lastSettlement + "->"))
                    .Select(kv => new
                    {
                        Key = kv.Key,
                        Prob = kv.Value,
                        Count = dirStats.Counts.TryGetValue(kv.Key, out int c) ? c : 0,
                        AvgDist = dirStats.AverageDistances.TryGetValue(kv.Key, out double d) ? d : 0,
                        AvgWeight = dirStats.AverageSourceWeights.TryGetValue(kv.Key, out double w) ? w : 0.5
                    })
                    .OrderByDescending(x => x.Prob)
                    .Take(3);

                foreach (var item in singleStep)
                {
                    // Проверка расстояния: если расстояние слишком большое, снижаем вероятность
                    double distanceFactor = 1.0;
                    if (item.AvgDist > 0 && features.AverageSpeed > 0)
                    {
                        // Ожидаемое время в пути: расстояние / скорость
                        double expectedHours = item.AvgDist / features.AverageSpeed;
                        double windowHours = (_heuristics.AttackWindowMinutes / 60.0);
                        if (expectedHours > windowHours * 2)
                            distanceFactor = 0.3; // слишком далеко
                        else if (expectedHours > windowHours)
                            distanceFactor = 0.7;
                    }

                    double score = item.Prob * timeProb * distanceFactor * item.AvgWeight;
                    candidates.Add((item.Key, score, item.Prob, item.AvgWeight));
                }
            }

            // 2. Двухшаговые цепочки (если есть как минимум две предыдущие точки)
            if (incident.Points.Count >= 3)
            {
                var lastTwo = incident.Points.TakeLast(2).ToList();
                var prev2 = lastTwo[0];
                var prev1 = lastTwo[1];
                string chainPrefix = $"{prev2.SettlementName}->{prev1.SettlementName}->";
                if (!string.IsNullOrEmpty(prev2.SettlementName) && !string.IsNullOrEmpty(prev1.SettlementName))
                {
                    var chainCandidates = dirStats.Probabilities
                        .Where(kv => kv.Key.StartsWith(chainPrefix))
                        .Select(kv => new
                        {
                            Key = kv.Key,
                            Prob = kv.Value,
                            Count = dirStats.Counts.TryGetValue(kv.Key, out int c) ? c : 0,
                            AvgDist = dirStats.AverageDistances.TryGetValue(kv.Key, out double d) ? d : 0,
                            AvgWeight = dirStats.AverageSourceWeights.TryGetValue(kv.Key, out double w) ? w : 0.5
                        })
                        .OrderByDescending(x => x.Prob)
                        .Take(2);

                    foreach (var item in chainCandidates)
                    {
                        double distanceFactor = 1.0;
                        if (item.AvgDist > 0 && features.AverageSpeed > 0)
                        {
                            double expectedHours = item.AvgDist / features.AverageSpeed;
                            double windowHours = (_heuristics.AttackWindowMinutes / 60.0);
                            if (expectedHours > windowHours * 2)
                                distanceFactor = 0.3;
                            else if (expectedHours > windowHours)
                                distanceFactor = 0.7;
                        }
                        double score = item.Prob * timeProb * distanceFactor * item.AvgWeight;
                        candidates.Add((item.Key, score, item.Prob, item.AvgWeight));
                    }
                }
            }

            if (candidates.Count == 0)
                return null;

            // Выбираем топ-3 по Score
            var bestCandidates = candidates
                .OrderByDescending(x => x.Score)
                .Take(3)
                .ToList();

            // Извлекаем целевые поселения из направлений
            var affectedSettlements = new List<string>();
            foreach (var (dir, _, _, _) in bestCandidates)
            {
                var parts = dir.Split("->");
                if (parts.Length >= 2)
                {
                    var target = parts.Last();
                    if (!string.IsNullOrEmpty(target) && !affectedSettlements.Contains(target))
                        affectedSettlements.Add(target);
                }
            }

            // Если не нашли поселений, используем последнее как дефолт
            if (affectedSettlements.Count == 0 && !string.IsNullOrEmpty(lastSettlement))
                affectedSettlements.Add(lastSettlement);

            // Окно атаки: от 15 минут до AttackWindowMinutes (или можно по скорости)
            DateTime windowStart = incident.LastSeen.AddMinutes(15);
            DateTime windowEnd = incident.LastSeen.AddMinutes(_heuristics.AttackWindowMinutes);

            // Уверенность: комбинация вероятности, временного фактора, веса источника и количества точек
            var best = bestCandidates.First();
            double confidence = best.Probability * 0.4 + timeProb * 0.2 + best.AvgWeight * 0.2;
            double pointFactor = Math.Min(1.0, incident.Points.Count / 5.0);
            confidence += pointFactor * 0.2;
            confidence = Math.Clamp(confidence, 0.0, 1.0);

            // Геозона
            string zoneGeoJson = "";
            if (affectedSettlements.Count > 0)
            {
                var points = new List<IncidentPoint>();
                foreach (var name in affectedSettlements)
                {
                    var settlement = _geo.FindSettlement(name);
                    if (settlement != null)
                    {
                        points.Add(new IncidentPoint
                        {
                            SettlementName = settlement.Name,
                            Lat = settlement.Lat,
                            Lon = settlement.Lon,
                            Time = DateTime.UtcNow
                        });
                    }
                }
                if (points.Count > 0)
                {
                    double radiusKm = 5.0;
                    if (features.AverageSpeed > 0)
                    {
                        double hoursUntil = (windowEnd - incident.LastSeen).TotalHours;
                        radiusKm = features.AverageSpeed * hoursUntil * 0.5;
                        radiusKm = Math.Clamp(radiusKm, 2.0, 50.0);
                    }
                    zoneGeoJson = _geo.BuildZoneGeoJson(points, radiusKm);
                }
            }

            var prediction = new Prediction
            {
                IncidentId = incident.Id,
                ThreatType = incident.ThreatType,
                ZoneGeoJson = zoneGeoJson,
                AffectedSettlements = affectedSettlements,
                AttackWindowStart = windowStart,
                AttackWindowEnd = windowEnd,
                Confidence = confidence,
                Notes = $"Статистический прогноз: направление {best.Direction}, переходов={bestCandidates.Count}, уверенность={confidence:P0}",
				PredictorType = Name,   // "Statistical"
            };

            GraphLogger.LogPrediction(incident.Id, incident.ThreatType, prediction.Confidence, string.Join(", ", prediction.AffectedSettlements), Name);
            return prediction;
        }

        /// <summary>
        /// Вычисляет средний вес источника для инцидента (усреднение по всем источникам).
        /// </summary>
        private double GetAverageSourceWeight(Incident incident, Dictionary<string, double> sourceWeights)
        {
            if (sourceWeights == null || sourceWeights.Count == 0)
                return 0.5;
            // В текущей реализации источник не сохраняется в инциденте, поэтому берём среднее по всем
            return sourceWeights.Values.Average();
        }

        // Вспомогательные классы для хранения статистик
        private class DirectionStats
        {
            public Dictionary<string, double> Probabilities { get; set; } = new();
            public Dictionary<string, int> Counts { get; set; } = new();
            public Dictionary<string, double> AverageDistances { get; set; } = new();
            public Dictionary<string, double> AverageSourceWeights { get; set; } = new();
        }

        private class TimeStats
        {
            public Dictionary<string, double> Probabilities { get; set; } = new();
        }
    }
}