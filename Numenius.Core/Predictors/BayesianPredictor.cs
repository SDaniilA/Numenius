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
    public class BayesianPredictor : IPredictor
    {
        public string Name => "Bayesian";

        private readonly IGeoService _geo;
        private readonly IDatabaseService _db;
        private readonly ZoneService _zoneService;
        private readonly HeuristicsConfig _heuristics;
        private readonly Dictionary<string, ThreatCharacteristics> _threatCharacteristics;
        private readonly Dictionary<string, Dictionary<string, double>> _transitionProbabilities = new();
        private DateTime _lastGraphUpdate = DateTime.MinValue;
        private readonly object _lock = new();

        public BayesianPredictor(
            IGeoService geo,
            IDatabaseService db,
            ZoneService zoneService,
            HeuristicsConfig heuristics,
            Dictionary<string, ThreatCharacteristics> threatCharacteristics)
        {
            _geo = geo ?? throw new ArgumentNullException(nameof(geo));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _zoneService = zoneService ?? throw new ArgumentNullException(nameof(zoneService));
            _heuristics = heuristics ?? throw new ArgumentNullException(nameof(heuristics));
            _threatCharacteristics = threatCharacteristics ?? new Dictionary<string, ThreatCharacteristics>();
        }

        /// <summary>
        /// Обновляет граф переходов между зонами на основе истории инцидентов.
        /// </summary>
        private async Task UpdateTransitionGraphAsync()
        {
            if ((DateTime.UtcNow - _lastGraphUpdate).TotalHours < 1)
                return;

            var incidents = await _db.GetAllIncidentsAsync(30); // последние 30 дней
            if (incidents == null || !incidents.Any())
                return;

            lock (_lock)
            {
                _transitionProbabilities.Clear();

                // Для каждого типа угрозы строим свою матрицу переходов
                foreach (var typeGroup in incidents.GroupBy(i => i.ThreatType ?? "Unknown"))
                {
                    string threatType = typeGroup.Key;
                    var transitions = new Dictionary<string, Dictionary<string, int>>();

                    foreach (var inc in typeGroup)
                    {
                        // Получаем зоны инцидента (по точкам)
                        if (inc.Points.Count < 2) continue;
                        var zones = inc.Points
                            .GroupBy(p => $"{p.SettlementName}")
                            .Select(g => g.First().SettlementName)
                            .ToList();

                        for (int i = 1; i < zones.Count; i++)
                        {
                            string from = zones[i - 1];
                            string to = zones[i];
                            if (!transitions.ContainsKey(from))
                                transitions[from] = new Dictionary<string, int>();
                            if (!transitions[from].ContainsKey(to))
                                transitions[from][to] = 0;
                            transitions[from][to]++;
                        }
                    }

                    // Преобразуем в вероятности
                    var probDict = new Dictionary<string, double>();
                    foreach (var from in transitions.Keys)
                    {
                        int total = transitions[from].Values.Sum();
                        foreach (var to in transitions[from].Keys)
                        {
                            double prob = (double)transitions[from][to] / total;
                            probDict[$"{from}->{to}"] = prob;
                        }
                    }
                    _transitionProbabilities[threatType] = probDict;
                }

                _lastGraphUpdate = DateTime.UtcNow;
            }
        }

        public async Task<Prediction?> GeneratePredictionAsync(Incident incident)
        {
            Console.WriteLine($"[{Name}] Попытка прогноза для инц. #{incident.Id}, точек: {incident.Points.Count}");
            if (incident == null || incident.Points == null || incident.Points.Count < 1)
            {
                Console.WriteLine($"[{Name}] Возврат null: недостаточно точек");
                return null;
            }

            if (incident.Status == IncidentStatus.Terminated || incident.Status == IncidentStatus.Expired)
            {
                Console.WriteLine($"[{Name}] Возврат null: инцидент завершён");
                return null;
            }

            // Обновляем граф переходов
            await UpdateTransitionGraphAsync();

            string threatType = incident.ThreatType ?? "Unknown";
            // Получаем параметры ТТХ, если нет — берём дефолтные
            if (!_threatCharacteristics.TryGetValue(threatType, out var tth))
            {
                tth = new ThreatCharacteristics { MaxSpeedKmh = 160, MaxDistanceKm = 40, LifetimeMinutes = 30 };
            }

            // Определяем текущую зону инцидента
            var currentZone = _zoneService.GetOrCreateZoneForIncident(incident, 
                incident.Points.Select(p => new Settlement
                {
                    Name = p.SettlementName,
                    Lat = p.Lat,
                    Lon = p.Lon
                }).ToList(), threatType);

            if (currentZone == null)
            {
                Console.WriteLine($"[{Name}] Возврат null: не удалось определить зону");
                return null;
            }

            // Получаем все населённые пункты, которые находятся в пределах maxDistance
            var allSettlements = (await _db.GetAllSettlementsAsync()).ToList();
            var candidates = allSettlements
                .Where(s => _geo.CalculateDistance(currentZone.CenterLat, currentZone.CenterLon, s.Lat, s.Lon) <= tth.MaxDistanceKm)
                .ToList();

            if (candidates.Count == 0)
            {
                Console.WriteLine($"[{Name}] Возврат null: нет кандидатов в радиусе {tth.MaxDistanceKm} км");
                return null;
            }

            // Вычисляем вероятности для каждого кандидата
            var scores = new List<(Settlement Settlement, double Score)>();

            foreach (var candidate in candidates)
            {
                double score = 1.0;

                // 1. Априорная вероятность из графа переходов
                if (_transitionProbabilities.TryGetValue(threatType, out var transitions))
                {
                    // Ищем вероятность перехода от текущей зоны к кандидату
                    double transitionProb = 0;
                    foreach (var settlementName in currentZone.SettlementNames)
                    {
                        string key = $"{settlementName}->{candidate.Name}";
                        if (transitions.TryGetValue(key, out double prob))
                        {
                            transitionProb = Math.Max(transitionProb, prob);
                        }
                    }
                    score *= (0.5 + transitionProb); // базовая 0.5 + вероятностный вес
                }

                // 2. Правдоподобие по расстоянию: чем ближе, тем выше
                double dist = _geo.CalculateDistance(currentZone.CenterLat, currentZone.CenterLon, candidate.Lat, candidate.Lon);
                double distanceLikelihood = Math.Exp(-dist / tth.MaxDistanceKm);
                score *= distanceLikelihood;

                // 3. Время: если с последнего сообщения прошло много времени, вероятность падает
                double timeSinceLast = (DateTime.UtcNow - incident.LastSeen).TotalMinutes;
                double timeLikelihood = Math.Exp(-timeSinceLast / tth.LifetimeMinutes);
                score *= timeLikelihood;

                // 4. Частота упоминаний кандидата в последних сообщениях (за 30 минут)
                // Упрощённо: если кандидат есть в AffectedSettlements, добавляем буст
                if (incident.AffectedSettlements.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase))
                    score *= 2.0;

                // 5. Явное направление в последнем сообщении — если есть Direction и совпадает с кандидатом
                /* var lastMessage = await _db.GetLastParsedMessageForIncidentAsync(incident.Id);
				var lastMessage = await GetLastMessageForIncident(incident.Id);
                if (lastMessage?.Direction != null && lastMessage.Direction.Contains(candidate.Name, StringComparison.OrdinalIgnoreCase))
                    score *= 3.0;*/
				
				var lastMessage = await _db.GetLastParsedMessageForIncidentAsync(incident.Id);
				if (lastMessage?.Direction != null && lastMessage.Direction.Contains(candidate.Name, StringComparison.OrdinalIgnoreCase))
					score *= 3.0;
                scores.Add((candidate, score)); 

            }

            // Нормализуем вероятности
            double totalScore = scores.Sum(s => s.Score);
            if (totalScore == 0) return null;

            var ranked = scores
                .OrderByDescending(s => s.Score / totalScore)
                .Take(3)
                .ToList();

            // Формируем Prediction
            var affectedSettlements = ranked.Select(r => r.Settlement.Name).ToList();
            var zoneGeoJson = _geo.BuildZoneGeoJson(ranked.Select(r => new IncidentPoint
            {
                SettlementName = r.Settlement.Name,
                Lat = r.Settlement.Lat,
                Lon = r.Settlement.Lon,
                Time = DateTime.UtcNow
            }).ToList(), Math.Max(currentZone.RadiusKm, 5.0));

            var prediction = new Prediction
            {
                IncidentId = incident.Id,
                ThreatType = incident.ThreatType,
                ZoneGeoJson = zoneGeoJson,
                AffectedSettlements = affectedSettlements,
                AttackWindowStart = incident.LastSeen.AddMinutes(5),
                AttackWindowEnd = incident.LastSeen.AddMinutes(tth.LifetimeMinutes),
                Confidence = ranked.First().Score / totalScore,
                Notes = $"Байесовский прогноз: {string.Join(", ", affectedSettlements)}",
                PredictorType = Name
            };

            GraphLogger.LogPrediction(incident.Id, incident.ThreatType, prediction.Confidence, string.Join(", ", prediction.AffectedSettlements), Name);
            return prediction;
        }

        /* private async Task<ParsedMessage?> GetLastMessageForIncident(int incidentId)
        {
            // В реальной версии нужно хранить связь между сообщениями и инцидентом.
            // Пока упрощённо — берём последнее сообщение из кэша OutputCache (если он есть).
            // Для целостности можно добавить метод в IDatabaseService для получения последнего сообщения по инциденту.
            return null; // Заглушка для последующего улучшения
        } */
    }
}