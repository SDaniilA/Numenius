using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;
using Numenius.Core.Services;
using Numenius.Core.Config;
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
        private readonly GraphBuilder _graphBuilder;
        private Graph? _graph;
        private DateTime _lastGraphUpdate = DateTime.MinValue;

        public GraphPredictor(IGeoService geo, IDatabaseService db, HeuristicsConfig heuristics, GraphPredictorConfig config)
        {
            _geo = geo;
            _db = db;
            _heuristics = heuristics;
            _config = config;
            _graphBuilder = new GraphBuilder();
        }

        public async Task UpdateGraphAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var incidents = await _db.GetAllIncidentsAsync(_config.MaxNodeAgeDays);
            _graph = _graphBuilder.BuildGraph(incidents);
            _lastGraphUpdate = DateTime.UtcNow;
            stopwatch.Stop();
            int nodes = _graph?.Nodes.Count ?? 0;
            int edges = _graph?.Nodes.Sum(n => n.Edges.Count) ?? 0;
            GraphLogger.LogGraphStats(nodes, edges, stopwatch.Elapsed.TotalSeconds);
        }

        public async Task<Prediction?> GeneratePredictionAsync(Incident incident)
        {
            if (incident == null || incident.Points == null || incident.Points.Count < 2)
                return null;

            if (incident.Status == IncidentStatus.Terminated || incident.Status == IncidentStatus.Expired)
                return null;

            if (_graph == null || (DateTime.UtcNow - _lastGraphUpdate).TotalHours > 1)
            {
                await UpdateGraphAsync();
            }

            if (_graph == null || _graph.Nodes.Count == 0)
                return null;

            var extractor = new FeatureExtractor();
            var features = extractor.ExtractFeatures(incident);

            var candidateNodes = _graph.Nodes
                .Where(n => n.ThreatType == features.ThreatType || string.IsNullOrEmpty(features.ThreatType))
                .ToList();

            if (candidateNodes.Count == 0)
                return null;

            var predictions = new List<(GraphEdge Edge, double Score, string TargetRegion, double Confidence)>();
            foreach (var node in candidateNodes)
            {
                foreach (var edge in node.Edges)
                {
                    if (edge.TransitionCount < _config.MinOccurrencesForEdge) continue;
                    var targetNode = _graph.Nodes.FirstOrDefault(n => n.Id == edge.TargetNodeId);
                    if (targetNode == null) continue;

                    double ageFactor = 1.0 / (1 + (DateTime.UtcNow - edge.LastUpdated).TotalDays);
                    double score = edge.Probability * (edge.TransitionCount / 10.0) * ageFactor;

                    predictions.Add((edge, score, targetNode.Region, edge.Probability));
                }
            }

            int maxZones = 3;
            var bestPredictions = predictions.OrderByDescending(p => p.Score).Take(maxZones).ToList();

            if (bestPredictions.Count == 0 || bestPredictions.First().Score < 0.1)
                return null;

            var affectedSettlements = new List<string>();
            foreach (var (edge, score, targetRegion, conf) in bestPredictions)
            {
                if (!string.IsNullOrEmpty(targetRegion) && !affectedSettlements.Contains(targetRegion))
                    affectedSettlements.Add(targetRegion);
            }

            if (affectedSettlements.Count == 0 && incident.Points.Count > 0)
                affectedSettlements = incident.Points.Select(p => p.SettlementName).Distinct().ToList();

            DateTime windowStart = incident.LastSeen.AddMinutes(15);
            DateTime windowEnd = incident.LastSeen.AddMinutes(_heuristics.AttackWindowMinutes);

            var best = bestPredictions.First();
            if (best.Edge.AverageDelayHours > 0)
            {
                windowStart = incident.LastSeen.AddHours(best.Edge.AverageDelayHours * 0.7);
                windowEnd = incident.LastSeen.AddHours(best.Edge.AverageDelayHours * 1.3);
            }

            double avgConfidence = bestPredictions.Average(p => p.Confidence);
            double predictionConfidence = Math.Clamp(avgConfidence * 0.8 + 0.2 * Math.Min(1, incident.Points.Count / 5.0), 0.0, 1.0);

            var prediction = new Prediction
            {
                IncidentId = incident.Id,
                ThreatType = incident.ThreatType,
                ZoneGeoJson = "",
                AffectedSettlements = affectedSettlements,
                AttackWindowStart = windowStart,
                AttackWindowEnd = windowEnd,
                Confidence = predictionConfidence,
                Notes = $"Графовый прогноз: переходов {bestPredictions.Count}, скор={best.Score:F3}, переходов={best.Edge.TransitionCount}"
            };

            GraphLogger.LogPrediction(incident.Id, incident.ThreatType, prediction.Confidence, string.Join(", ", prediction.AffectedSettlements));

            return prediction;
        }
    }
}