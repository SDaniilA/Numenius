using System;
using System.Collections.Generic;
using System.Linq;
using Numenius.Core.Models;

namespace Numenius.Core.Services
{
    public class GraphBuilder
    {
        private readonly FeatureExtractor _featureExtractor = new();

        public Graph BuildGraph(IEnumerable<Incident> incidents)
        {
            var graph = new Graph();
            var featureList = incidents.Select(i => _featureExtractor.ExtractFeatures(i)).ToList();

            var nodeMap = new Dictionary<string, GraphNode>();
            foreach (var f in featureList)
            {
                string key = GetNodeKey(f);
                if (!nodeMap.TryGetValue(key, out var node))
                {
                    node = new GraphNode
                    {
                        ThreatType = f.ThreatType,
                        Category = f.Category.ToString(),
                        TimeOfDay = f.TimeOfDay,
                        DayOfWeek = f.DayOfWeek,
                        Season = f.Season,
                        Region = f.Region,
                        HasRecon = f.HasRecon,
                        OccurrenceCount = 0,
                        Weight = 0
                    };
                    nodeMap[key] = node;
                }
                node.OccurrenceCount++;
            }

            // Присваиваем уникальные Id узлам
            int nodeId = 1;
            foreach (var node in nodeMap.Values)
            {
                node.Id = nodeId++;
            }

            var sortedIncidents = incidents.OrderBy(i => i.FirstSeen).ToList();
            for (int i = 1; i < sortedIncidents.Count; i++)
            {
                var prevFeatures = _featureExtractor.ExtractFeatures(sortedIncidents[i - 1]);
                var currFeatures = _featureExtractor.ExtractFeatures(sortedIncidents[i]);

                double dist = 9999;
                if (prevFeatures.Settlements.Count > 0 && currFeatures.Settlements.Count > 0)
                {
                    var lastPrev = prevFeatures.Settlements.LastOrDefault();
                    var firstCurr = currFeatures.Settlements.FirstOrDefault();
                    if (lastPrev != null && firstCurr != null)
                    {
                        // Упрощённо: проверяем совпадение названий (или можно вызвать IGeoService)
                        dist = string.Equals(lastPrev, firstCurr, StringComparison.OrdinalIgnoreCase) ? 0 : 50;
                    }
                }
                if (dist > 50) continue;

                string prevKey = GetNodeKey(prevFeatures);
                string currKey = GetNodeKey(currFeatures);

                if (!nodeMap.TryGetValue(prevKey, out var sourceNode)) continue;
                if (!nodeMap.TryGetValue(currKey, out var targetNode)) continue;

                var edge = sourceNode.Edges.FirstOrDefault(e => e.TargetNodeId == targetNode.Id);
                if (edge == null)
                {
                    edge = new GraphEdge
                    {
                        SourceNodeId = sourceNode.Id,
                        TargetNodeId = targetNode.Id,
                        Probability = 0,
                        TransitionCount = 0,
                        AverageDelayHours = 0,
                        StdDevDelayHours = 0,
                        LastUpdated = DateTime.UtcNow
                    };
                    sourceNode.Edges.Add(edge);
                }

                double delay = (sortedIncidents[i].FirstSeen - sortedIncidents[i - 1].FirstSeen).TotalHours;
                edge.TransitionCount++;
                edge.AverageDelayHours = (edge.AverageDelayHours * (edge.TransitionCount - 1) + delay) / edge.TransitionCount;
                edge.LastUpdated = DateTime.UtcNow;
            }

            // Пересчёт вероятностей
            foreach (var node in nodeMap.Values)
            {
                double totalOut = node.Edges.Sum(e => e.TransitionCount);
                if (totalOut > 0)
                {
                    foreach (var edge in node.Edges)
                    {
                        edge.Probability = edge.TransitionCount / totalOut;
                    }
                }
            }

            graph.Nodes = nodeMap.Values.ToList();
            graph.BuildTime = DateTime.UtcNow;

            return graph;
        }

        private string GetNodeKey(IncidentFeatures features)
        {
            return $"{features.ThreatType}|{features.TimeOfDay}|{features.Region}|{features.HasRecon}";
        }
    }

    public class Graph
    {
        public List<GraphNode> Nodes { get; set; } = new();
        public DateTime BuildTime { get; set; }
    }
}