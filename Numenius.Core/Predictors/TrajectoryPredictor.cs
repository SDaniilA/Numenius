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
    public class TrajectoryPredictor : IPredictor
    {
        public string Name => "Trajectory";

        private readonly IGeoService _geo;
        private readonly IDatabaseService _db;
        private readonly HeuristicsConfig _heuristics;
        private readonly TrajectoryPredictorConfig _config;

        public TrajectoryPredictor(IGeoService geo, IDatabaseService db, HeuristicsConfig heuristics, TrajectoryPredictorConfig config)
        {
            _geo = geo;
            _db = db;
            _heuristics = heuristics;
            _config = config;
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

            // Убираем дубликаты по координатам (округление до 4 знаков)
            var orderedPoints = incident.Points.OrderBy(p => p.Time).ToList();
            var distinctPoints = orderedPoints
                .GroupBy(p => $"{p.Lat:F4}_{p.Lon:F4}")
                .Select(g => g.Last())
                .TakeLast(Math.Min(5, orderedPoints.Count))
                .ToList();

            if (distinctPoints.Count < 2)
            {
                Console.WriteLine($"[{Name}] Возврат null: после фильтрации дубликатов осталось <2 точек с разными координатами");
                return null;
            }

            // Вычисляем скорость по всем последовательным уникальным точкам
            var speeds = new List<double>();
            for (int i = 1; i < distinctPoints.Count; i++)
            {
                var p1 = distinctPoints[i - 1];
                var p2 = distinctPoints[i];
                double dist = _geo.CalculateDistance(p1.Lat, p1.Lon, p2.Lat, p2.Lon);
                double hours = (p2.Time - p1.Time).TotalHours;
                if (hours > 0.001)
                    speeds.Add(dist / hours);
            }

            double avgSpeed = speeds.Count > 0 ? speeds.Average() : 0;
            if (avgSpeed < 0.1)
            {
                // Если скорость всё равно мала, но есть явное движение (расстояние между точками > 1 км),
                // используем среднюю скорость между первой и последней точкой
                var first = distinctPoints.First();
                var last = distinctPoints.Last();
                double totalDist = _geo.CalculateDistance(first.Lat, first.Lon, last.Lat, last.Lon);
                double totalHours = (last.Time - first.Time).TotalHours;
                if (totalHours > 0.001 && totalDist > 0.5)
                    avgSpeed = totalDist / totalHours;
            }

            if (avgSpeed < 0.1)
            {
                Console.WriteLine($"[{Name}] Возврат null: скорость слишком мала ({avgSpeed:F2} км/ч)");
                return null;
            }

            // Направление: последние две уникальные точки
            var lastPoint = distinctPoints.Last();
            var prevPoint = distinctPoints[distinctPoints.Count - 2];
            double bearing = CalculateBearing(prevPoint.Lat, prevPoint.Lon, lastPoint.Lat, lastPoint.Lon);

            // Прогнозируем на время окна атаки
            double windowMinutes = _heuristics.AttackWindowMinutes;
            double hoursAhead = windowMinutes / 60.0;
            double distanceKm = avgSpeed * hoursAhead;
            double maxDistance = _config.MaxPredictionDistanceKm;
            if (distanceKm > maxDistance)
            {
                distanceKm = maxDistance;
                hoursAhead = distanceKm / avgSpeed;
                windowMinutes = hoursAhead * 60.0;
            }

            var predictedPoint = ProjectPoint(lastPoint.Lat, lastPoint.Lon, bearing, distanceKm);
            var nearestSettlement = FindNearestSettlement(predictedPoint.Lat, predictedPoint.Lon);

            DateTime arrivalTime = lastPoint.Time.AddHours(distanceKm / avgSpeed);
            double uncertainty = _config.UncertaintyPercent / 100.0;
            DateTime windowStart = arrivalTime.AddHours(-arrivalTime.Subtract(lastPoint.Time).TotalHours * uncertainty);
            DateTime windowEnd = arrivalTime.AddHours(arrivalTime.Subtract(lastPoint.Time).TotalHours * uncertainty);

            double confidence = CalculateConfidence(distinctPoints, avgSpeed, distanceKm, nearestSettlement);

            string zoneGeoJson = BuildZoneGeoJson(predictedPoint, nearestSettlement, distanceKm * uncertainty);

            var affectedSettlements = new List<string>();
            if (nearestSettlement != null)
                affectedSettlements.Add(nearestSettlement.Name);
            else
                affectedSettlements.Add($"({predictedPoint.Lat:F4}, {predictedPoint.Lon:F4})");

            string notes = $"Траекторный прогноз: скорость {avgSpeed:F1} км/ч, азимут {bearing:F1}°, дальность {distanceKm:F1} км";
            if (nearestSettlement != null)
                notes += $", ближайший НП: {nearestSettlement.Name} ({nearestSettlement.Lat:F4}, {nearestSettlement.Lon:F4})";
            else
                notes += $", предсказанная точка: ({predictedPoint.Lat:F4}, {predictedPoint.Lon:F4})";

            var prediction = new Prediction
            {
                IncidentId = incident.Id,
                ThreatType = incident.ThreatType,
                ZoneGeoJson = zoneGeoJson,
                AffectedSettlements = affectedSettlements,
                AttackWindowStart = windowStart,
                AttackWindowEnd = windowEnd,
                Confidence = confidence,
                Notes = notes,
                PredictorType = Name
            };

            GraphLogger.LogPrediction(incident.Id, incident.ThreatType ?? "Unknown", prediction.Confidence, string.Join(", ", prediction.AffectedSettlements), Name);
            return prediction;
        }

        private double CalculateBearing(double lat1, double lon1, double lat2, double lon2)
        {
            double dLon = (lon2 - lon1) * Math.PI / 180;
            double lat1Rad = lat1 * Math.PI / 180;
            double lat2Rad = lat2 * Math.PI / 180;

            double x = Math.Sin(dLon) * Math.Cos(lat2Rad);
            double y = Math.Cos(lat1Rad) * Math.Sin(lat2Rad) - Math.Sin(lat1Rad) * Math.Cos(lat2Rad) * Math.Cos(dLon);
            double bearing = Math.Atan2(x, y) * 180 / Math.PI;
            return (bearing + 360) % 360;
        }

        private IncidentPoint ProjectPoint(double lat, double lon, double azimuthDeg, double distanceKm)
        {
            const double R = 6371;
            double latRad = lat * Math.PI / 180;
            double lonRad = lon * Math.PI / 180;
            double bearingRad = azimuthDeg * Math.PI / 180;
            double angularDist = distanceKm / R;

            double newLatRad = Math.Asin(Math.Sin(latRad) * Math.Cos(angularDist) +
                                          Math.Cos(latRad) * Math.Sin(angularDist) * Math.Cos(bearingRad));
            double newLonRad = lonRad + Math.Atan2(Math.Sin(bearingRad) * Math.Sin(angularDist) * Math.Cos(latRad),
                                                   Math.Cos(angularDist) - Math.Sin(latRad) * Math.Sin(newLatRad));
            return new IncidentPoint
            {
                Lat = newLatRad * 180 / Math.PI,
                Lon = newLonRad * 180 / Math.PI,
                Time = DateTime.UtcNow
            };
        }

        private Settlement? FindNearestSettlement(double lat, double lon)
        {
            Settlement? nearest = null;
            double bestDist = double.MaxValue;
            foreach (var name in _geo.GetAllSettlementNames())
            {
                var s = _geo.FindSettlement(name);
                if (s == null) continue;
                double dist = _geo.CalculateDistance(lat, lon, s.Lat, s.Lon);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    nearest = s;
                }
            }
            return nearest;
        }

        private string BuildZoneGeoJson(IncidentPoint center, Settlement? nearest, double radiusKm)
        {
            if (radiusKm < 0.1) radiusKm = 1.0;
            var points = new List<IncidentPoint> { center };
            return _geo.BuildZoneGeoJson(points, radiusKm * 2);
        }

        private double CalculateConfidence(List<IncidentPoint> points, double speedKmh, double distanceKm, Settlement? nearest)
        {
            double confidence = 0.5;
            double pointFactor = Math.Min(1.0, points.Count / 5.0);
            confidence += pointFactor * 0.2;

            // Оценка стабильности скорости
            if (points.Count >= 3)
            {
                double avg = 0;
                int count = 0;
                for (int i = 1; i < points.Count; i++)
                {
                    var p1 = points[i - 1];
                    var p2 = points[i];
                    double dist = _geo.CalculateDistance(p1.Lat, p1.Lon, p2.Lat, p2.Lon);
                    double hours = (p2.Time - p1.Time).TotalHours;
                    if (hours > 0.001)
                    {
                        avg += dist / hours;
                        count++;
                    }
                }
                if (count > 0)
                {
                    avg /= count;
                    double variance = 0;
                    for (int i = 1; i < points.Count; i++)
                    {
                        var p1 = points[i - 1];
                        var p2 = points[i];
                        double dist = _geo.CalculateDistance(p1.Lat, p1.Lon, p2.Lat, p2.Lon);
                        double hours = (p2.Time - p1.Time).TotalHours;
                        if (hours > 0.001)
                        {
                            double speed = dist / hours;
                            variance += Math.Pow(speed - avg, 2);
                        }
                    }
                    variance /= count;
                    double stdDev = Math.Sqrt(variance);
                    if (avg > 0)
                    {
                        double stability = Math.Max(0, 1 - stdDev / avg);
                        confidence += stability * 0.15;
                    }
                }
            }

            if (nearest != null)
                confidence += 0.1;

            if (distanceKm > 50)
                confidence -= 0.1;

            return Math.Clamp(confidence, 0.0, 1.0);
        }
    }
}