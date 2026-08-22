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

        private async Task UpdateTransitionGraphAsync()
        {
            if ((DateTime.UtcNow - _lastGraphUpdate).TotalHours < 1) return;

            var incidents = await _db.GetAllIncidentsAsync(30);
            if (incidents == null || !incidents.Any()) return;

            lock (_lock)
            {
                _transitionProbabilities.Clear();
                foreach (var typeGroup in incidents.GroupBy(i => i.ThreatType ?? "Unknown"))
                {
                    string threatType = typeGroup.Key;
                    var transitions = new Dictionary<string, Dictionary<string, int>>();
                    foreach (var inc in typeGroup)
                    {
                        if (inc.Points.Count < 2) continue;
                        var zones = inc.Points.Select(p => p.SettlementName).Distinct().ToList();
                        for (int i = 1; i < zones.Count; i++)
                        {
                            string from = zones[i - 1];
                            string to = zones[i];
                            if (!transitions.ContainsKey(from)) transitions[from] = new Dictionary<string, int>();
                            if (!transitions[from].ContainsKey(to)) transitions[from][to] = 0;
                            transitions[from][to]++;
                        }
                    }

                    var probDict = new Dictionary<string, double>();
                    foreach (var from in transitions.Keys)
                    {
                        int total = transitions[from].Values.Sum();
                        foreach (var to in transitions[from].Keys)
                            probDict[$"{from}->{to}"] = (double)transitions[from][to] / total;
                    }
                    _transitionProbabilities[threatType] = probDict;
                }

                _lastGraphUpdate = DateTime.UtcNow;
                GraphLogger.Log($"Байесовский граф обновлён: {_transitionProbabilities.Count} типов");
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

            await UpdateTransitionGraphAsync();

            string threatType = incident.ThreatType ?? "Unknown";
            if (!_threatCharacteristics.TryGetValue(threatType, out var tth))
                tth = new ThreatCharacteristics { MaxSpeedKmh = 160, MaxDistanceKm = 40, LifetimeMinutes = 30 };

            // Для разведчиков используем зону покрытия
            if (threatType == "Recon" || threatType == "Shark")
            {
                var reconZone = _zoneService.CreateReconZone(
                    incident.Points.Select(p => new Settlement
                    {
                        Name = p.SettlementName,
                        Lat = p.Lat,
                        Lon = p.Lon
                    }).ToList());
                if (reconZone == null || reconZone.SettlementNames.Count == 0)
                {
                    Console.WriteLine($"[{Name}] Возврат null: нет зоны покрытия разведчика");
                    return null;
                }

                var affected = reconZone.SettlementNames.Take(3).ToList();
                var reconPrediction = new Prediction
                {
                    IncidentId = incident.Id,
                    ThreatType = incident.ThreatType,
                    ZoneGeoJson = reconZone.ToGeoJson(),
                    AffectedSettlements = affected,
                    AttackWindowStart = incident.LastSeen.AddMinutes(5),
                    AttackWindowEnd = incident.LastSeen.AddMinutes(tth.LifetimeMinutes),
                    Confidence = 0.7, // для разведчика можно задать фиксированную или вычислять
                    Notes = $"Разведывательная зона: {string.Join(", ", affected)}",
                    PredictorType = Name
                };
                GraphLogger.LogPrediction(incident.Id, incident.ThreatType, reconPrediction.Confidence, string.Join(", ", reconPrediction.AffectedSettlements), Name);
                return reconPrediction;
            }

            // Обычный сценарий — радар (сканирование секторов)
            var radarResults = _zoneService.ScanSectors(incident, 3);
            if (radarResults.Count == 0)
            {
                Console.WriteLine($"[{Name}] Возврат null: нет кандидатов из секторов");
                return null;
            }

            Console.WriteLine($"[{Name}] Кандидаты из секторов:");
            foreach (var r in radarResults)
                Console.WriteLine($"   {r.SettlementName} (дист={r.Distance:F1} км, сектор={r.Sector}°, вес={r.ZoneWeight:F2})");

            var scores = new List<(string Name, double Score)>();
            foreach (var radar in radarResults)
            {
                double bayesWeight = GetBayesWeight(threatType, radar.SettlementName);
                double score = radar.ZoneWeight * bayesWeight;
                scores.Add((radar.SettlementName, score));
                Console.WriteLine($"   {radar.SettlementName}: radarWeight={radar.ZoneWeight:F2}, bayesWeight={bayesWeight:F2}, score={score:F2}");
            }

            double totalScore = scores.Sum(s => s.Score);
            if (totalScore == 0) return null;

            var ranked = scores.OrderByDescending(s => s.Score / totalScore).Take(3).ToList();

            var affectedSettlements = ranked.Select(r => r.Name).ToList();
            var zoneGeoJson = _geo.BuildZoneGeoJson(ranked.Select(r => new IncidentPoint
            {
                SettlementName = r.Name,
                Lat = _geo.FindSettlement(r.Name)?.Lat ?? 0,
                Lon = _geo.FindSettlement(r.Name)?.Lon ?? 0,
                Time = DateTime.UtcNow
            }).ToList(), 5.0);

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

        private double GetBayesWeight(string threatType, string settlementName)
        {
            if (_transitionProbabilities.TryGetValue(threatType, out var transitions))
            {
                double maxProb = 0;
                foreach (var kv in transitions)
                {
                    if (kv.Key.EndsWith($"->{settlementName}", StringComparison.OrdinalIgnoreCase))
                        maxProb = Math.Max(maxProb, kv.Value);
                }
                return 0.5 + maxProb;
            }
            return 1.0;
        }
    }
}