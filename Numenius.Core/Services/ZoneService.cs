using System;
using System.Collections.Generic;
using System.Linq;
using Numenius.Core.Config;
using Numenius.Core.Models;

namespace Numenius.Core.Services
{
    public class ZoneService
    {
        private readonly SettlementGraph _graph;
        private readonly Dictionary<string, ThreatCharacteristics> _threatCharacteristics;

        public ZoneService(SettlementGraph graph, Dictionary<string, ThreatCharacteristics> threatCharacteristics)
        {
            _graph = graph ?? throw new ArgumentNullException(nameof(graph));
            _threatCharacteristics = threatCharacteristics ?? new Dictionary<string, ThreatCharacteristics>();
        }

        // ========== Создание зоны ==========
        public Zone CreateZone(IEnumerable<Settlement> settlements, string threatType)
        {
            var list = settlements?.ToList() ?? new List<Settlement>();
            if (list.Count == 0) return null;

            double maxDistance = GetMaxDistanceKm(threatType);
            double centerLat = list.Average(s => s.Lat);
            double centerLon = list.Average(s => s.Lon);
            double maxDistFromCenter = list.Max(s => _graph.GetDistance(centerLat, centerLon, s.Lat, s.Lon));
            double radius = Math.Min(maxDistFromCenter + 3.0, maxDistance + 3.0);

            return new Zone
            {
                CenterLat = centerLat,
                CenterLon = centerLon,
                RadiusKm = radius,
                ThreatType = threatType,
                Weight = 1.0,
                LastUpdated = DateTime.UtcNow,
                SettlementNames = list.Select(s => s.Name).ToList(),
                Points = list.Select(s => new IncidentPoint
                {
                    SettlementName = s.Name,
                    Lat = s.Lat,
                    Lon = s.Lon,
                    Time = DateTime.UtcNow
                }).ToList()
            };
        }

        // ========== Проверка, тот же ли дрон ==========
        public bool IsSameDrone(Incident incident, ParsedMessage message, double timeDiffMinutes)
        {
            if (incident == null || message == null || message.Settlements.Count == 0) return false;

            if (!string.IsNullOrEmpty(message.ThreatType) && message.ThreatType != "Unknown" &&
                !string.Equals(incident.ThreatType, message.ThreatType, StringComparison.OrdinalIgnoreCase))
                return false;

            var lastPoint = incident.Points.OrderBy(p => p.Time).LastOrDefault();
            if (lastPoint == null) return false;

            var firstNewPoint = message.Settlements.First();
            double dist = _graph.GetDistance(lastPoint.Lat, lastPoint.Lon, firstNewPoint.Lat, firstNewPoint.Lon);
            double maxDistance = GetMaxDistanceKm(message.ThreatType);

            // Если время < 0.1 мин, проверяем только расстояние
            if (timeDiffMinutes < 0.1)
                return dist <= maxDistance + 3.0;

            double maxSpeed = GetMaxSpeedKmh(message.ThreatType);
            double maxTravelDistance = maxSpeed * (timeDiffMinutes / 60.0);

            if (dist > maxTravelDistance * 1.05 || dist > maxDistance + 3.0)
                return false;

            return true;
        }

        public bool IsFarDrone(Incident incident, ParsedMessage message, double timeDiffMinutes)
        {
            var lastPoint = incident.Points.OrderBy(p => p.Time).LastOrDefault();
            if (lastPoint == null || message.Settlements.Count == 0) return false;

            var firstNewPoint = message.Settlements.First();
            double dist = _graph.GetDistance(lastPoint.Lat, lastPoint.Lon, firstNewPoint.Lat, firstNewPoint.Lon);
            double maxSpeed = GetMaxSpeedKmh(message.ThreatType);
            double maxTravelDistance = maxSpeed * (timeDiffMinutes / 60.0);
            double maxDistance = GetMaxDistanceKm(message.ThreatType);

            if (dist > maxDistance && dist <= maxTravelDistance * 1.05)
                return true;

            return false;
        }

        // ========== Обработка дальнобойного дрона ==========
        public void HandleFarDrone(Incident incident, ParsedMessage message)
        {
            var lastPoint = incident.Points.OrderBy(p => p.Time).LastOrDefault();
            if (lastPoint != null)
            {
                incident.Points.Remove(lastPoint);
                incident.AffectedSettlements.Remove(lastPoint.SettlementName);
            }

            var newZone = CreateZone(message.Settlements, message.ThreatType);
            if (newZone != null)
            {
                incident.Points.AddRange(newZone.Points);
                foreach (var s in newZone.SettlementNames)
                    if (!incident.AffectedSettlements.Contains(s))
                        incident.AffectedSettlements.Add(s);
            }

            incident.LastSeen = message.EventTime ?? message.ReceivedAt;
        }

        // ========== Обновление зоны ==========
        public void UpdateZone(Zone zone, List<Settlement> newSettlements, string threatType)
        {
            if (zone == null || newSettlements == null || newSettlements.Count == 0) return;

            foreach (var s in newSettlements)
            {
                if (!zone.SettlementNames.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
                {
                    zone.SettlementNames.Add(s.Name);
                    zone.Points.Add(new IncidentPoint
                    {
                        SettlementName = s.Name,
                        Lat = s.Lat,
                        Lon = s.Lon,
                        Time = DateTime.UtcNow
                    });
                }
            }

            zone.CenterLat = zone.Points.Average(p => p.Lat);
            zone.CenterLon = zone.Points.Average(p => p.Lon);

            double maxDistance = GetMaxDistanceKm(threatType);
            double maxAllowedRadius = maxDistance + 3.0;
            double maxDistFromCenter = zone.Points.Max(p => _graph.GetDistance(zone.CenterLat, zone.CenterLon, p.Lat, p.Lon));
            zone.RadiusKm = Math.Min(maxDistFromCenter + 3.0, maxAllowedRadius);

            zone.Weight += 1.0;
            zone.LastUpdated = DateTime.UtcNow;
        }

        // ========== Радар (сканирование секторов) ==========
        public List<RadarResult> ScanSectors(Incident incident, int count = 3)
        {
            var points = incident.Points.OrderBy(p => p.Time).ToList();
            if (points.Count == 0) return new List<RadarResult>();

            var origin = points.Last();
            double originLat = origin.Lat;
            double originLon = origin.Lon;

            Console.WriteLine($"[ZoneService] Сканирование секторов от ({originLat:F4}, {originLon:F4}), точек: {points.Count}");

            var sectorWeights = new Dictionary<int, double>();
            for (int deg = 0; deg < 360; deg += 5)
            {
                double start = deg;
                double end = deg + 5;
                var settlementsInSector = _graph.GetSettlementsInSector(originLat, originLon, start, end);

                double weight = 0;
                bool hasNear = false;
                foreach (var s in settlementsInSector)
                {
                    double dist = _graph.GetDistance(originLat, originLon, s.Lat, s.Lon);
                    double zoneWeight = GetZoneWeight(dist, GetMaxDistanceKm(incident.ThreatType));
                    if (zoneWeight >= 3.0)
                        hasNear = true;
                    weight += zoneWeight;
                }

                if (hasNear)
                    weight += 5.0;

                sectorWeights[deg] = weight;
            }

            var topSectors = sectorWeights
                .OrderByDescending(kv => kv.Value)
                .Take(count)
                .ToList();

            Console.WriteLine("[ZoneService] Топ сектора:");
            foreach (var sector in topSectors)
                Console.WriteLine($"   Сектор {sector.Key}°: вес={sector.Value:F2}");

            var results = new List<RadarResult>();
            foreach (var sector in topSectors)
            {
                var nearest = _graph.GetNearestSettlementsInSector(originLat, originLon, sector.Key, sector.Key + 5, 3);
                foreach (var s in nearest)
                {
                    double dist = _graph.GetDistance(originLat, originLon, s.Lat, s.Lon);
                    double zoneWeight = GetZoneWeight(dist, GetMaxDistanceKm(incident.ThreatType));

                    results.Add(new RadarResult
                    {
                        SettlementName = s.Name,
                        Distance = dist,
                        Sector = sector.Key,
                        ZoneWeight = zoneWeight
                    });
                }
            }

            var grouped = results
                .GroupBy(r => r.SettlementName)
                .Select(g => new RadarResult
                {
                    SettlementName = g.Key,
                    Distance = g.Min(r => r.Distance),
                    Sector = g.First().Sector,
                    ZoneWeight = g.Sum(r => r.ZoneWeight)
                })
                .OrderByDescending(r => r.ZoneWeight)
                .ThenBy(r => r.Distance)
                .Take(count)
                .ToList();

            Console.WriteLine("[ZoneService] Итоговые кандидаты:");
            foreach (var r in grouped)
                Console.WriteLine($"   {r.SettlementName}: дист={r.Distance:F1} км, вес={r.ZoneWeight:F2}");

            return grouped;
        }

        // ========== Зона для разведчика ==========
        public Zone CreateReconZone(List<Settlement> settlements, double reconRadiusKm = 7.0)
        {
            var points = settlements.Select(s => new IncidentPoint
            {
                SettlementName = s.Name,
                Lat = s.Lat,
                Lon = s.Lon,
                Time = DateTime.UtcNow
            }).ToList();

            if (points.Count == 0) return null;

            if (points.Count == 1)
            {
                var zone = new Zone
                {
                    CenterLat = points[0].Lat,
                    CenterLon = points[0].Lon,
                    RadiusKm = reconRadiusKm,
                    ThreatType = "Recon",
                    Weight = 1.0,
                    LastUpdated = DateTime.UtcNow,
                    Points = points,
                    SettlementNames = new List<string>()
                };

                foreach (var name in _graph.GetSettlementNames())
                {
                    var s = _graph.GetSettlement(name);
                    if (s != null && _graph.GetDistance(points[0].Lat, points[0].Lon, s.Lat, s.Lon) <= reconRadiusKm)
                        zone.SettlementNames.Add(s.Name);
                }
                return zone;
            }

            var zonePoints = new List<IncidentPoint>();
            for (int i = 0; i < points.Count - 1; i++)
            {
                var p1 = points[i];
                var p2 = points[i + 1];
                double segmentDist = _graph.GetDistance(p1.Lat, p1.Lon, p2.Lat, p2.Lon);
                int steps = Math.Max(2, (int)(segmentDist / 2.0));
                for (int t = 0; t <= steps; t++)
                {
                    double frac = (double)t / steps;
                    double lat = p1.Lat + (p2.Lat - p1.Lat) * frac;
                    double lon = p1.Lon + (p2.Lon - p1.Lon) * frac;
                    zonePoints.Add(new IncidentPoint
                    {
                        SettlementName = $"{p1.SettlementName}-{p2.SettlementName}-{t}",
                        Lat = lat,
                        Lon = lon,
                        Time = DateTime.UtcNow
                    });
                }
            }

            double centerLat = zonePoints.Average(p => p.Lat);
            double centerLon = zonePoints.Average(p => p.Lon);

            var allSettlements = _graph.GetAllSettlements();
            var affected = new List<string>();
            foreach (var s in allSettlements)
            {
                bool inZone = false;
                foreach (var pt in zonePoints)
                {
                    if (_graph.GetDistance(pt.Lat, pt.Lon, s.Lat, s.Lon) <= reconRadiusKm)
                    {
                        inZone = true;
                        break;
                    }
                }
                if (inZone && !affected.Contains(s.Name))
                    affected.Add(s.Name);
            }

            return new Zone
            {
                CenterLat = centerLat,
                CenterLon = centerLon,
                RadiusKm = reconRadiusKm,
                ThreatType = "Recon",
                Weight = points.Count,
                LastUpdated = DateTime.UtcNow,
                SettlementNames = affected,
                Points = points
            };
        }

        // ========== Публичные методы для ScenarioManager ==========
        public double GetMaxSpeedKmh(string threatType)
        {
            if (!string.IsNullOrEmpty(threatType) && _threatCharacteristics.TryGetValue(threatType, out var tth))
                return tth.MaxSpeedKmh;
            return 160;
        }

        public double GetMaxDistanceKm(string threatType)
        {
            if (!string.IsNullOrEmpty(threatType) && _threatCharacteristics.TryGetValue(threatType, out var tth))
                return tth.MaxDistanceKm;
            return 40;
        }

        // ========== Вспомогательные ==========
        private double GetZoneWeight(double distance, double maxDistance)
        {
            if (maxDistance <= 0) return 0.5;
            double ratio = distance / maxDistance;
            if (ratio < 0.1) return 3.0;
            if (ratio < 0.5) return 1.5;
            return 0.5;
        }

        public double GetDistance(double lat1, double lon1, double lat2, double lon2)
        {
            return _graph.GetDistance(lat1, lon1, lat2, lon2);
        }

        public double GetAzimuth(double lat1, double lon1, double lat2, double lon2)
        {
            return _graph.GetAzimuth(lat1, lon1, lat2, lon2);
        }

        public List<Settlement> GetAllSettlements()
        {
            return _graph.GetAllSettlements();
        }

        public string ToGeoJson(Zone zone)
        {
            if (zone == null || zone.Points == null || zone.Points.Count == 0) return "{}";
            double minLat = zone.Points.Min(p => p.Lat) - zone.RadiusKm / 111.0;
            double maxLat = zone.Points.Max(p => p.Lat) + zone.RadiusKm / 111.0;
            double minLon = zone.Points.Min(p => p.Lon) - zone.RadiusKm / (111.0 * Math.Cos(zone.CenterLat * Math.PI / 180));
            double maxLon = zone.Points.Max(p => p.Lon) + zone.RadiusKm / (111.0 * Math.Cos(zone.CenterLat * Math.PI / 180));
            var coords = new[]
            {
                new[] { minLon, minLat },
                new[] { maxLon, minLat },
                new[] { maxLon, maxLat },
                new[] { minLon, maxLat },
                new[] { minLon, minLat }
            };
            return $"{{\"type\":\"Polygon\",\"coordinates\":[{Newtonsoft.Json.JsonConvert.SerializeObject(coords)}]}}";
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
        public List<IncidentPoint> Points { get; set; } = new();

        public string ToGeoJson()
        {
            if (Points == null || Points.Count == 0) return "{}";
            double minLat = Points.Min(p => p.Lat) - RadiusKm / 111.0;
            double maxLat = Points.Max(p => p.Lat) + RadiusKm / 111.0;
            double minLon = Points.Min(p => p.Lon) - RadiusKm / (111.0 * Math.Cos(CenterLat * Math.PI / 180));
            double maxLon = Points.Max(p => p.Lon) + RadiusKm / (111.0 * Math.Cos(CenterLat * Math.PI / 180));
            var coords = new[]
            {
                new[] { minLon, minLat },
                new[] { maxLon, minLat },
                new[] { maxLon, maxLat },
                new[] { minLon, maxLat },
                new[] { minLon, minLat }
            };
            return $"{{\"type\":\"Polygon\",\"coordinates\":[{Newtonsoft.Json.JsonConvert.SerializeObject(coords)}]}}";
        }
    }

    public class RadarResult
    {
        public string SettlementName { get; set; }
        public double Distance { get; set; }
        public int Sector { get; set; }
        public double ZoneWeight { get; set; }
    }
}