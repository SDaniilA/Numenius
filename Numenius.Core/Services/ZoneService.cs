using System;
using System.Collections.Generic;
using System.Linq;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;

namespace Numenius.Core.Services
{
    public class ZoneService
    {
        private readonly IGeoService _geo;
        private readonly Dictionary<int, Zone> _zonesByIncident = new();

        public ZoneService(IGeoService geo)
        {
            _geo = geo ?? throw new ArgumentNullException(nameof(geo));
        }

        /// <summary>
        /// Создает зону из набора населённых пунктов (минимальная окружность + буфер)
        /// </summary>
        public Zone CreateZone(IEnumerable<Settlement> settlements, string threatType, double bufferKm = 3.0)
        {
            var list = settlements?.ToList() ?? new List<Settlement>();
            if (list.Count == 0) return null;

            double avgLat = list.Average(s => s.Lat);
            double avgLon = list.Average(s => s.Lon);
            double maxDist = list.Max(s => _geo.CalculateDistance(avgLat, avgLon, s.Lat, s.Lon));
            double radius = maxDist + bufferKm;

            return new Zone
            {
                CenterLat = avgLat,
                CenterLon = avgLon,
                RadiusKm = radius,
                ThreatType = threatType,
                Weight = list.Count,
                LastUpdated = DateTime.UtcNow,
                SettlementNames = list.Select(s => s.Name).ToList()
            };
        }

        /// <summary>
        /// Проверяет пересечение двух зон (кругов)
        /// </summary>
        public bool ZonesIntersect(Zone a, Zone b)
        {
            if (a == null || b == null) return false;
            double dist = _geo.CalculateDistance(a.CenterLat, a.CenterLon, b.CenterLat, b.CenterLon);
            return dist <= (a.RadiusKm + b.RadiusKm);
        }

        /// <summary>
        /// Обновляет зону при поступлении новых сообщений (сужает радиус, увеличивает вес)
        /// </summary>
        public void UpdateZone(Zone zone, IEnumerable<Settlement> newSettlements, double shrinkFactor = 0.8)
        {
            if (zone == null) return;
            var list = newSettlements?.ToList() ?? new List<Settlement>();
            if (list.Count == 0) return;

            zone.RadiusKm *= shrinkFactor;
            zone.Weight += list.Count;
            zone.LastUpdated = DateTime.UtcNow;
            foreach (var s in list)
            {
                if (!zone.SettlementNames.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
                    zone.SettlementNames.Add(s.Name);
            }
        }

        /// <summary>
        /// Кластеризация НП в зоны через DBSCAN (плотностная кластеризация)
        /// </summary>
        public List<Zone> ClusterSettlements(IEnumerable<Settlement> settlements, double epsKm = 5.0, int minPts = 3)
        {
            var list = settlements?.ToList() ?? new List<Settlement>();
            if (list.Count == 0) return new List<Zone>();

            var visited = new bool[list.Count];
            var clusters = new List<List<int>>();

            for (int i = 0; i < list.Count; i++)
            {
                if (visited[i]) continue;
                visited[i] = true;
                var neighbors = GetNeighbors(i, list, epsKm);
                if (neighbors.Count < minPts)
                {
                    // Шум — пропускаем
                    continue;
                }

                var cluster = new List<int> { i };
                var queue = new Queue<int>(neighbors);
                while (queue.Count > 0)
                {
                    int j = queue.Dequeue();
                    if (!visited[j])
                    {
                        visited[j] = true;
                        var jNeighbors = GetNeighbors(j, list, epsKm);
                        if (jNeighbors.Count >= minPts)
                        {
                            foreach (var n in jNeighbors)
                            {
                                if (!cluster.Contains(n))
                                    queue.Enqueue(n);
                            }
                        }
                    }
                    if (!cluster.Contains(j))
                        cluster.Add(j);
                }
                clusters.Add(cluster);
            }

            var zones = new List<Zone>();
            foreach (var cluster in clusters)
            {
                var clusterSettlements = cluster.Select(idx => list[idx]).ToList();
                zones.Add(CreateZone(clusterSettlements, "Unknown"));
            }
            return zones;
        }

        private List<int> GetNeighbors(int index, List<Settlement> settlements, double epsKm)
        {
            var neighbors = new List<int>();
            for (int i = 0; i < settlements.Count; i++)
            {
                if (i == index) continue;
                double dist = _geo.CalculateDistance(
                    settlements[index].Lat, settlements[index].Lon,
                    settlements[i].Lat, settlements[i].Lon);
                if (dist <= epsKm)
                    neighbors.Add(i);
            }
            return neighbors;
        }

        /// <summary>
        /// Получает или создаёт зону для конкретного инцидента
        /// </summary>
        public Zone GetOrCreateZoneForIncident(Incident incident, IEnumerable<Settlement> newSettlements, string threatType)
        {
            if (!_zonesByIncident.TryGetValue(incident.Id, out var zone))
            {
                zone = CreateZone(newSettlements, threatType);
                _zonesByIncident[incident.Id] = zone;
            }
            else
            {
                UpdateZone(zone, newSettlements);
            }
            return zone;
        }

        public void RemoveZoneForIncident(int incidentId)
        {
            if (_zonesByIncident.ContainsKey(incidentId))
                _zonesByIncident.Remove(incidentId);
        }
    }

    public class Zone
    {
        public double CenterLat { get; set; }
        public double CenterLon { get; set; }
        public double RadiusKm { get; set; }
        public string ThreatType { get; set; }
        public double Weight { get; set; }
        public DateTime LastUpdated { get; set; }
        public List<string> SettlementNames { get; set; } = new();
    }
}