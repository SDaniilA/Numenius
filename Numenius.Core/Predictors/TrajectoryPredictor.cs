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
            _geo = geo ?? throw new ArgumentNullException(nameof(geo));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _heuristics = heuristics ?? throw new ArgumentNullException(nameof(heuristics));
            _config = config ?? throw new ArgumentNullException(nameof(config));
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

            var points = incident.Points.OrderBy(p => p.Time).TakeLast(Math.Min(5, incident.Points.Count)).ToList();
            if (points.Count < 2)
            {
                Console.WriteLine($"[{Name}] Возврат null: точек меньше 2 после сортировки");
                return null;
            }

            var (speedKmh, azimuthDeg) = CalculateMotion(points);
            if (speedKmh < 0.1)
            {
                Console.WriteLine($"[{Name}] Возврат null: скорость слишком мала ({speedKmh:F2} км/ч)");
                return null;
            }

            double windowMinutes = _heuristics.AttackWindowMinutes;
            double hours = windowMinutes / 60.0;
            double distanceKm = speedKmh * hours;
            double maxDistance = _config.MaxPredictionDistanceKm;
            if (distanceKm > maxDistance)
            {
                distanceKm = maxDistance;
                hours = distanceKm / speedKmh;
                windowMinutes = hours * 60.0;
            }

            var lastPoint = points.Last();
            var predictedPoint = ProjectPoint(lastPoint.Lat, lastPoint.Lon, azimuthDeg, distanceKm);

            var nearestSettlement = FindNearestSettlement(predictedPoint.Lat, predictedPoint.Lon);

            DateTime arrivalTime = lastPoint.Time.AddHours(distanceKm / speedKmh);

            double uncertainty = _config.UncertaintyPercent / 100.0;
            DateTime windowStart = arrivalTime.AddHours(-arrivalTime.Subtract(lastPoint.Time).TotalHours * uncertainty);
            DateTime windowEnd = arrivalTime.AddHours(arrivalTime.Subtract(lastPoint.Time).TotalHours * uncertainty);

            double confidence = CalculateConfidence(points, speedKmh, distanceKm, nearestSettlement);

            string zoneGeoJson = BuildZoneGeoJson(predictedPoint, nearestSettlement, distanceKm * uncertainty);

            var affectedSettlements = new List<string>();
            if (nearestSettlement != null)
                affectedSettlements.Add(nearestSettlement.Name);
            else
                affectedSettlements.Add($"({predictedPoint.Lat:F4}, {predictedPoint.Lon:F4})");

            string notes = $"Траекторный прогноз: скорость {speedKmh:F1} км/ч, азимут {azimuthDeg:F1}°, дальность {distanceKm:F1} км";
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

        private (double SpeedKmh, double AzimuthDeg) CalculateMotion(List<IncidentPoint> points)
        {
            if (points.Count < 2)
                return (0, 0);

            var last = points[points.Count - 1];
            var prev = points[points.Count - 2];
            double dist = _geo.CalculateDistance(prev.Lat, prev.Lon, last.Lat, last.Lon);
            double hours = (last.Time - prev.Time).TotalHours;
            if (hours < 0.001)
                return (0, 0);

            double speed = dist / hours;
            double bearing = CalculateBearing(prev.Lat, prev.Lon, last.Lat, last.Lon);
            return (speed, bearing);
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

            if (points.Count >= 3)
            {
                double avgSpeed = 0;
                int count = 0;
                for (int i = 1; i < points.Count; i++)
                {
                    var p1 = points[i - 1];
                    var p2 = points[i];
                    double dist = _geo.CalculateDistance(p1.Lat, p1.Lon, p2.Lat, p2.Lon);
                    double hours = (p2.Time - p1.Time).TotalHours;
                    if (hours > 0.001)
                    {
                        avgSpeed += dist / hours;
                        count++;
                    }
                }
                if (count > 0)
                {
                    avgSpeed /= count;
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
                            variance += Math.Pow(speed - avgSpeed, 2);
                        }
                    }
                    variance /= count;
                    double stdDev = Math.Sqrt(variance);
                    double stability = Math.Max(0, 1 - stdDev / avgSpeed);
                    confidence += stability * 0.15;
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