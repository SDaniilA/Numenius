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
    public class GraphPredictor : IPredictor
    {
        public string Name => "Graph";

        private readonly IGeoService _geo;
        private readonly IDatabaseService _db;
        private readonly HeuristicsConfig _heuristics;
        private readonly GraphPredictorConfig _config;
        private Graph? _graph;
        private DateTime _lastGraphUpdate = DateTime.MinValue;

        public GraphPredictor(IGeoService geo, IDatabaseService db, HeuristicsConfig heuristics, GraphPredictorConfig config)
        {
            _geo = geo;
            _db = db;
            _heuristics = heuristics;
            _config = config;
        }

        public async Task UpdateGraphAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var incidents = await _db.GetAllIncidentsAsync(_config.MaxNodeAgeDays);
            var builder = new GraphBuilder(_geo);
            _graph = builder.BuildGraph(incidents, _db);
            _lastGraphUpdate = DateTime.UtcNow;
            stopwatch.Stop();
            int nodes = _graph?.Nodes.Count ?? 0;
            int edges = _graph?.Nodes.Sum(n => n.Edges.Count) ?? 0;
            GraphLogger.LogGraphStats(nodes, edges, stopwatch.Elapsed.TotalSeconds);
        }

        public async Task<Prediction?> GeneratePredictionAsync(Incident incident)
        {
            Console.WriteLine($"[{Name}] Попытка прогноза для инц. #{incident.Id}, точек: {incident.Points.Count}");
            if (incident == null || incident.Points == null || incident.Points.Count < 2)
            {
                Console.WriteLine($"[{Name}] Возврат null: недостаточно точек");
                return null;
            }

            if (incident.Status == IncidentStatus.Terminated || incident.Status == IncidentStatus.Expired)
            {
                Console.WriteLine($"[{Name}] Возврат null: инцидент завершён (Terminated/Expired)");
                return null;
            }

            if (_graph == null || (DateTime.UtcNow - _lastGraphUpdate).TotalHours > 1)
            {
                await UpdateGraphAsync();
            }

            if (_graph == null || _graph.Nodes.Count == 0)
            {
                Console.WriteLine($"[{Name}] Возврат null: граф пуст");
                return null;
            }

            // Получаем список населённых пунктов из инцидента
            var settlementNames = incident.Points.Select(p => p.SettlementName).Distinct().ToList();

            // Ищем узел сначала по типу и совпадению с любым из населённых пунктов
            GraphNode? currentNode = null;

            // Попытка 1: точное совпадение типа и региона (как в старом коде)
            var features = new FeatureExtractor().ExtractFeatures(incident);
            string regionKey = GetRegionKey(incident.Points);
            currentNode = _graph.Nodes.FirstOrDefault(n =>
                n.ThreatType == features.ThreatType &&
                n.Region == regionKey);

            // Попытка 2: совпадение по типу и любому из НП (если в узле регион - название или кластер, содержащий НП)
            if (currentNode == null)
            {
                currentNode = _graph.Nodes.FirstOrDefault(n =>
                    n.ThreatType == features.ThreatType &&
                    settlementNames.Any(s => n.Region.Contains(s, StringComparison.OrdinalIgnoreCase)));
            }

            // Попытка 3: просто по типу
            if (currentNode == null)
            {
                currentNode = _graph.Nodes.FirstOrDefault(n => n.ThreatType == features.ThreatType);
            }

            // Попытка 4: если тип Unknown, берём узел с максимальной частотой
            if (currentNode == null && (features.ThreatType == "Unknown" || string.IsNullOrEmpty(features.ThreatType)))
            {
                currentNode = _graph.Nodes.OrderByDescending(n => n.OccurrenceCount).FirstOrDefault();
            }

            if (currentNode == null)
            {
                Console.WriteLine($"[{Name}] Возврат null: не удалось найти узел для типа '{features.ThreatType}'");
                return null;
            }

            Console.WriteLine($"[{Name}] Используем узел ID={currentNode.Id}, регион={currentNode.Region}, тип={currentNode.ThreatType}");

            // Собираем рёбра
            var now = DateTime.UtcNow;
            var candidateEdges = currentNode.Edges
                .Where(e => e.Probability > 0 && e.TransitionCount >= _config.MinOccurrencesForEdge)
                .Select(e => new
                {
                    Edge = e,
                    Weight = e.Probability * Math.Exp(-(now - e.LastUpdated).TotalDays / 7.0)
                })
                .OrderByDescending(x => x.Weight)
                .Take(3)
                .ToList();

            if (candidateEdges.Count == 0)
            {
                Console.WriteLine($"[{Name}] Возврат null: нет рёбер с переходов >= {_config.MinOccurrencesForEdge}");
                return null;
            }

            var bestEdges = candidateEdges.Select(x => x.Edge).ToList();

            // Целевые зоны
            var affectedSettlements = new List<string>();
            foreach (var edge in bestEdges)
            {
                var targetNode = _graph.Nodes.FirstOrDefault(n => n.Id == edge.TargetNodeId);
                if (targetNode != null && !string.IsNullOrEmpty(targetNode.Region))
                {
                    // Разбиваем регион на несколько, если он содержит запятые
                    affectedSettlements.AddRange(targetNode.Region.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(r => r.Trim())
                        .Where(r => !string.IsNullOrEmpty(r)));
                }
            }
            if (affectedSettlements.Count == 0)
                affectedSettlements.Add(regionKey);

            // Окно атаки
            DateTime windowStart, windowEnd;
            if (bestEdges.Count > 0)
            {
                var mainEdge = bestEdges.First();
                double meanDelay = mainEdge.AverageDelayHours;
                double stdDev = Math.Max(mainEdge.StdDevDelayHours, 0.5);
                double offsetStart = Math.Max(meanDelay - stdDev, 0.25);
                double offsetEnd = meanDelay + stdDev;
                windowStart = incident.LastSeen.AddHours(offsetStart);
                windowEnd = incident.LastSeen.AddHours(offsetEnd);
            }
            else
            {
                windowStart = incident.LastSeen.AddMinutes(15);
                windowEnd = incident.LastSeen.AddMinutes(_heuristics.AttackWindowMinutes);
            }

            double avgProb = bestEdges.Average(e => e.Probability);
            double sourceFactor = currentNode.Weight;
            double pointFactor = Math.Min(1.0, incident.Points.Count / 5.0);
            double confidence = avgProb * 0.5 + sourceFactor * 0.3 + pointFactor * 0.2;
            confidence = Math.Clamp(confidence, 0.0, 1.0);

            // Геозона
            string zoneGeoJson = "";
            if (affectedSettlements.Count > 0)
            {
                var settlementPoints = new List<IncidentPoint>();
                foreach (var name in affectedSettlements)
                {
                    var settlement = _geo.FindSettlement(name);
                    if (settlement != null)
                    {
                        settlementPoints.Add(new IncidentPoint
                        {
                            SettlementName = settlement.Name,
                            Lat = settlement.Lat,
                            Lon = settlement.Lon,
                            Time = DateTime.UtcNow
                        });
                    }
                }
                if (settlementPoints.Count > 0)
                {
                    double radiusKm = 5.0;
                    if (features.AverageSpeed > 0)
                    {
                        double hoursUntil = (windowEnd - incident.LastSeen).TotalHours;
                        radiusKm = features.AverageSpeed * hoursUntil * 0.5;
                        radiusKm = Math.Clamp(radiusKm, 2.0, 50.0);
                    }
                    zoneGeoJson = _geo.BuildZoneGeoJson(settlementPoints, radiusKm);
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
                Notes = $"Графовый прогноз: {bestEdges.Count} рёбер, скор={bestEdges.First().Probability:F3}, переходов={bestEdges.First().TransitionCount}",
                PredictorType = Name
            };

            GraphLogger.LogPrediction(incident.Id, incident.ThreatType, prediction.Confidence, string.Join(", ", prediction.AffectedSettlements), Name);
            return prediction;
        }

        private string GetRegionKey(List<IncidentPoint> points)
        {
            if (points == null || points.Count == 0) return "Unknown";
            var first = points.First();
            var last = points.Last();
            if (first.SettlementName == last.SettlementName)
                return first.SettlementName;
            double latRound = Math.Round((first.Lat + last.Lat) / 2, 1);
            double lonRound = Math.Round((first.Lon + last.Lon) / 2, 1);
            return $"{latRound:F1}_{lonRound:F1}";
        }
    }
}