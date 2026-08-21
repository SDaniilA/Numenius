using System;
using System.Collections.Generic;
using System.Linq;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;
using Numenius.Core.Services;

namespace Numenius.Core.Services
{
    public class GraphBuilder
    {
        private readonly IGeoService _geo;
        private readonly FeatureExtractor _featureExtractor = new();
        private readonly double _defaultDistanceThreshold = 50.0; // км
        private readonly double _fpvDistanceThreshold = 10.0; // км
        private readonly double _maxAgeDays = 30.0; // для затухания

        public GraphBuilder(IGeoService geo)
        {
            _geo = geo ?? throw new ArgumentNullException(nameof(geo));
        }

        public Graph BuildGraph(IEnumerable<Incident> incidents, IDatabaseService db)
        {
            // Получаем веса источников (для всех отправителей)
            var sourceWeights = db.GetAllSourceWeightsAsync().GetAwaiter().GetResult();

            var graph = new Graph();
            var incidentList = incidents.OrderBy(i => i.FirstSeen).ToList();
            if (incidentList.Count == 0) return graph;

            // 1. Создаём узлы на основе извлечённых признаков
            var nodeMap = new Dictionary<string, GraphNode>();
            var incidentFeatures = incidentList.Select(i => new
            {
                Incident = i,
                Features = _featureExtractor.ExtractFeatures(i)
            }).ToList();

            // Для каждого инцидента создаём/обновляем узел
            foreach (var item in incidentFeatures)
            {
                var f = item.Features;
                // Ключ узла: ThreatType + TimeOfDay + Season + DayOfWeek + HasRecon + AvgSpeedBin + RegionCluster
                // Для региона используем кластеризацию по координатам центра масс точек
                string regionKey = GetRegionKey(item.Incident.Points);
                string key = $"{f.ThreatType}|{f.TimeOfDay}|{f.Season}|{f.DayOfWeek}|{f.HasRecon}|{GetSpeedBin(f.AverageSpeed)}|{regionKey}";

                if (!nodeMap.TryGetValue(key, out var node))
                {
                    node = new GraphNode
                    {
                        ThreatType = f.ThreatType,
                        Category = f.Category.ToString(),
                        TimeOfDay = f.TimeOfDay,
                        DayOfWeek = f.DayOfWeek,
                        Season = f.Season,
                        Region = regionKey,
                        HasRecon = f.HasRecon,
                        OccurrenceCount = 0,
                        Weight = 0,
                        // Дополнительные метрики
                        AvgSpeed = f.AverageSpeed,
                        TotalDistance = f.TotalDistance,
                        PointsCount = f.PointsCount
                    };
                    nodeMap[key] = node;
                }
                // Увеличиваем счётчик и учитываем вес источника
                node.OccurrenceCount++;
                // Средний вес источника для узла
                double incidentSourceWeight = GetAverageSourceWeight(item.Incident, sourceWeights);
                node.Weight = (node.Weight * (node.OccurrenceCount - 1) + incidentSourceWeight) / node.OccurrenceCount;
            }

            // Присваиваем ID узлам
            int nodeId = 1;
            foreach (var node in nodeMap.Values)
            {
                node.Id = nodeId++;
            }

            // 2. Строим рёбра на основе последовательности инцидентов
            for (int i = 1; i < incidentList.Count; i++)
            {
                var prev = incidentList[i - 1];
                var curr = incidentList[i];

                // Получаем признаки для ключей узлов
                var prevFeatures = _featureExtractor.ExtractFeatures(prev);
                var currFeatures = _featureExtractor.ExtractFeatures(curr);

                string prevRegion = GetRegionKey(prev.Points);
                string currRegion = GetRegionKey(curr.Points);

                string prevKey = $"{prevFeatures.ThreatType}|{prevFeatures.TimeOfDay}|{prevFeatures.Season}|{prevFeatures.DayOfWeek}|{prevFeatures.HasRecon}|{GetSpeedBin(prevFeatures.AverageSpeed)}|{prevRegion}";
                string currKey = $"{currFeatures.ThreatType}|{currFeatures.TimeOfDay}|{currFeatures.Season}|{currFeatures.DayOfWeek}|{currFeatures.HasRecon}|{GetSpeedBin(currFeatures.AverageSpeed)}|{currRegion}";

                if (!nodeMap.TryGetValue(prevKey, out var sourceNode) || !nodeMap.TryGetValue(currKey, out var targetNode))
                    continue;

                // Вычисляем расстояние между последней точкой prev и первой точкой curr
                double distance = 999999;
                if (prev.Points.Count > 0 && curr.Points.Count > 0)
                {
                    var lastPrev = prev.Points.Last();
                    var firstCurr = curr.Points.First();
                    distance = _geo.CalculateDistance(lastPrev.Lat, lastPrev.Lon, firstCurr.Lat, firstCurr.Lon);
                }

                // Порог расстояния зависит от типа угрозы
                double threshold = _defaultDistanceThreshold;
                if (prev.ThreatType == "FPV" || curr.ThreatType == "FPV")
                    threshold = _fpvDistanceThreshold;
                else if (prev.ThreatType == "Rocket" || curr.ThreatType == "Rocket")
                    threshold = _defaultDistanceThreshold * 2; // ракеты могут иметь большую дальность

                if (distance > threshold) continue;

                // Проверяем временной промежуток: если слишком большой, возможно, не связаны
                double hoursBetween = (curr.FirstSeen - prev.FirstSeen).TotalHours;
                if (hoursBetween > 24) continue; // слишком большой разрыв

                // Находим или создаём ребро
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
                        LastUpdated = DateTime.UtcNow,
                        // Сохраняем среднее расстояние между инцидентами
                        AverageDistance = distance,
                        StdDevDistance = 0
                    };
                    sourceNode.Edges.Add(edge);
                }

                // Обновляем статистику ребра
                edge.TransitionCount++;
                edge.LastUpdated = DateTime.UtcNow;
                // Обновляем среднюю задержку
                double oldAvg = edge.AverageDelayHours;
                edge.AverageDelayHours = (oldAvg * (edge.TransitionCount - 1) + hoursBetween) / edge.TransitionCount;
                // Обновляем среднеквадратичное отклонение (упрощённо через скользящую дисперсию)
                if (edge.TransitionCount > 1)
                {
                    double diff = hoursBetween - oldAvg;
                    double oldVar = edge.StdDevDelayHours * edge.StdDevDelayHours;
                    edge.StdDevDelayHours = Math.Sqrt((oldVar * (edge.TransitionCount - 2) + diff * diff) / (edge.TransitionCount - 1));
                }
                else
                {
                    edge.StdDevDelayHours = 0;
                }

                // Обновляем среднее расстояние
                double oldDist = edge.AverageDistance;
                edge.AverageDistance = (oldDist * (edge.TransitionCount - 1) + distance) / edge.TransitionCount;
            }

            // 3. Пересчёт вероятностей с байесовским сглаживанием
            const double alpha = 1.0; // параметр сглаживания
            foreach (var node in nodeMap.Values)
            {
                double totalOut = node.Edges.Sum(e => e.TransitionCount);
                if (totalOut == 0) continue;
                // Количество возможных целевых узлов (для сглаживания)
                int k = node.Edges.Count;
                foreach (var edge in node.Edges)
                {
                    // Байесовская оценка: (count + alpha) / (total + alpha * k)
                    edge.Probability = (edge.TransitionCount + alpha) / (totalOut + alpha * k);
                }
            }

            graph.Nodes = nodeMap.Values.ToList();
            graph.BuildTime = DateTime.UtcNow;
            return graph;
        }

        private string GetRegionKey(List<IncidentPoint> points)
        {
            if (points == null || points.Count == 0) return "Unknown";
            // Берём первую и последнюю точки для определения региона (можно кластеризовать)
            var first = points.First();
            var last = points.Last();
            if (first.SettlementName == last.SettlementName)
                return first.SettlementName;
            // Или используем координатный квадрат (например, округление до 0.1 градуса)
            double latRound = Math.Round((first.Lat + last.Lat) / 2, 1);
            double lonRound = Math.Round((first.Lon + last.Lon) / 2, 1);
            return $"{latRound:F1}_{lonRound:F1}";
        }

        private string GetSpeedBin(double speed)
        {
            if (speed < 10) return "Slow";
            if (speed < 50) return "Medium";
            if (speed < 150) return "Fast";
            return "VeryFast";
        }

        private double GetAverageSourceWeight(Incident incident, Dictionary<string, double> sourceWeights)
        {
            // Предполагаем, что у нас нет прямого источника в инциденте, но можно получить из сообщений
            // Здесь упрощённо: берём средний вес из всех источников инцидента (если хранятся)
            // Поскольку в текущей модели источник не сохраняется в Incident, берём средний по всем источникам
            if (sourceWeights.Count == 0) return 0.5;
            return sourceWeights.Values.Average();
        }
    }

    // Дополним класс GraphNode и GraphEdge новыми полями
    public class GraphNode
    {
        public int Id { get; set; }
        public string ThreatType { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string TimeOfDay { get; set; } = string.Empty;
        public string DayOfWeek { get; set; } = string.Empty;
        public string Season { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public bool HasRecon { get; set; }
        public double OccurrenceCount { get; set; }
        public double Weight { get; set; }
        public double AvgSpeed { get; set; }
        public double TotalDistance { get; set; }
        public int PointsCount { get; set; }
        public List<GraphEdge> Edges { get; set; } = new();
    }

    public class GraphEdge
    {
        public int Id { get; set; }
        public int SourceNodeId { get; set; }
        public int TargetNodeId { get; set; }
        public double Probability { get; set; }
        public double TransitionCount { get; set; }
        public double AverageDelayHours { get; set; }
        public double StdDevDelayHours { get; set; }
        public double AverageDistance { get; set; }
        public double StdDevDistance { get; set; }
        public DateTime LastUpdated { get; set; }
        public GraphNode? SourceNode { get; set; }
        public GraphNode? TargetNode { get; set; }
    }

    public class Graph
    {
        public List<GraphNode> Nodes { get; set; } = new();
        public DateTime BuildTime { get; set; }
    }
}