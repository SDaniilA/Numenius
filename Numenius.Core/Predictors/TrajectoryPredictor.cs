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
    /// <summary>
    /// Предиктор на основе экстраполяции траектории движения по последним точкам.
    /// Вычисляет скорость, направление и прогнозирует следующую точку с учётом времени.
    /// </summary>
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
            if (incident == null || incident.Points == null || incident.Points.Count < 2)
                return null;

            if (incident.Status == IncidentStatus.Terminated || incident.Status == IncidentStatus.Expired)
                return null;

            // Берём последние точки (не менее 2, максимум 5)
            var points = incident.Points.OrderBy(p => p.Time).TakeLast(Math.Min(5, incident.Points.Count)).ToList();
            if (points.Count < 2)
                return null;

            // Вычисляем среднюю скорость и направление (азимут)
            var (speedKmh, azimuthDeg) = CalculateMotion(points);

            // Если скорость близка к нулю, прогноз не имеет смысла
            if (speedKmh < 0.1)
                return null;

            // Экстраполируем позицию на время окна атаки (по умолчанию AttackWindowMinutes)
            double windowMinutes = _heuristics.AttackWindowMinutes;
            double hours = windowMinutes / 60.0;
            double distanceKm = speedKmh * hours;

            // Предсказанная точка (координаты)
            var lastPoint = points.Last();
            var predictedPoint = ProjectPoint(lastPoint.Lat, lastPoint.Lon, azimuthDeg, distanceKm);

            // Ближайший населённый пункт к предсказанной точке
            var nearestSettlement = FindNearestSettlement(predictedPoint.Lat, predictedPoint.Lon, _config.MaxSearchRadiusKm);

            // Время прибытия (от последней точки)
            DateTime arrivalTime = lastPoint.Time.AddHours(distanceKm / speedKmh);

            // Окно атаки: +/- 20% от расчётного времени
            double uncertainty = _config.UncertaintyPercent / 100.0;
            DateTime windowStart = arrivalTime.AddHours(-arrivalTime.Subtract(lastPoint.Time).TotalHours * uncertainty);
            DateTime windowEnd = arrivalTime.AddHours(arrivalTime.Subtract(lastPoint.Time).TotalHours * uncertainty);

            // Уверенность: зависит от количества точек, скорости, расстояния до ближайшего НП
            double confidence = CalculateConfidence(points, speedKmh, distanceKm, nearestSettlement);

            // Строим геозону: полигон вокруг предсказанной точки с радиусом, зависящим от неопределённости
            string zoneGeoJson = BuildZoneGeoJson(predictedPoint, nearestSettlement, distanceKm * uncertainty);

            // Список затронутых поселений
            var affectedSettlements = new List<string>();
            if (nearestSettlement != null)
                affectedSettlements.Add(nearestSettlement.Name);
            else
                affectedSettlements.Add($"({predictedPoint.Lat:F4}, {predictedPoint.Lon:F4})");

            var prediction = new Prediction
            {
                IncidentId = incident.Id,
                ThreatType = incident.ThreatType,
                ZoneGeoJson = zoneGeoJson,
                AffectedSettlements = affectedSettlements,
                AttackWindowStart = windowStart,
                AttackWindowEnd = windowEnd,
                Confidence = confidence,
                Notes = $"Траекторный прогноз: скорость {speedKmh:F1} км/ч, азимут {azimuthDeg:F1}°, дальность {distanceKm:F1} км"
            };

            GraphLogger.LogPrediction(incident.Id, incident.ThreatType, prediction.Confidence, string.Join(", ", prediction.AffectedSettlements));
			// DEBUG
			Console.WriteLine($"   [Trajectory] Уверенность: {prediction.Confidence:P0}, зоны: {string.Join(", ", prediction.AffectedSettlements)}");
			// DEBUG
            return prediction;
        }

        /// <summary>
        /// Вычисляет среднюю скорость (км/ч) и азимут (градусы) по цепочке точек.
        /// </summary>
        private (double SpeedKmh, double AzimuthDeg) CalculateMotion(List<IncidentPoint> points)
        {
            if (points.Count < 2)
                return (0, 0);

            double totalDist = 0;
            double totalTimeHours = 0;
            double bearingSum = 0;
            int bearingCount = 0;

            for (int i = 1; i < points.Count; i++)
            {
                var p1 = points[i - 1];
                var p2 = points[i];
                double dist = _geo.CalculateDistance(p1.Lat, p1.Lon, p2.Lat, p2.Lon);
                double hours = (p2.Time - p1.Time).TotalHours;
                if (hours < 0.001) continue; // избегаем деления на ноль

                totalDist += dist;
                totalTimeHours += hours;

                double bearing = CalculateBearing(p1.Lat, p1.Lon, p2.Lat, p2.Lon);
                bearingSum += bearing;
                bearingCount++;
            }

            if (totalTimeHours < 0.001 || bearingCount == 0)
                return (0, 0);

            double speed = totalDist / totalTimeHours;
            double avgBearing = bearingSum / bearingCount;
            return (speed, avgBearing);
        }

        /// <summary>
        /// Вычисляет азимут (в градусах) между двумя точками.
        /// </summary>
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

        /// <summary>
        /// Проецирует точку на заданное расстояние по азимуту.
        /// </summary>
        private IncidentPoint ProjectPoint(double lat, double lon, double azimuthDeg, double distanceKm)
        {
            const double R = 6371; // радиус Земли в км
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

        /// <summary>
        /// Находит ближайший населённый пункт к заданным координатам в радиусе maxRadius км.
        /// </summary>
        private Settlement? FindNearestSettlement(double lat, double lon, double maxRadius)
        {
            Settlement? nearest = null;
            double bestDist = maxRadius;

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

        /// <summary>
        /// Строит GeoJSON-полигон вокруг точки с радиусом.
        /// </summary>
        private string BuildZoneGeoJson(IncidentPoint center, Settlement? nearest, double radiusKm)
        {
            if (radiusKm < 0.1) radiusKm = 1.0;
            var points = new List<IncidentPoint> { center };
            return _geo.BuildZoneGeoJson(points, radiusKm * 2); // Используем ширину зоны = 2*радиус
        }

        /// <summary>
        /// Вычисляет уверенность прогноза.
        /// </summary>
        private double CalculateConfidence(List<IncidentPoint> points, double speedKmh, double distanceKm, Settlement? nearest)
        {
            double confidence = 0.5; // базовая

            // Чем больше точек, тем выше уверенность
            double pointFactor = Math.Min(1.0, points.Count / 5.0);
            confidence += pointFactor * 0.2;

            // Чем стабильнее скорость (малая вариация), тем выше уверенность
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

            // Если есть ближайший НП в разумном радиусе, повышаем уверенность
            if (nearest != null)
            {
                double distToNp = _geo.CalculateDistance(points.Last().Lat, points.Last().Lon, nearest.Lat, nearest.Lon);
                if (distToNp < _config.MaxSearchRadiusKm)
                    confidence += 0.1;
            }

            // Если прогнозная дальность слишком большая, снижаем уверенность
            if (distanceKm > 50)
                confidence -= 0.1;

            return Math.Clamp(confidence, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Конфигурация для TrajectoryPredictor.
    /// </summary>
    /* public class TrajectoryPredictorConfig
    {
        public double MaxSearchRadiusKm { get; set; } = 30.0; // радиус поиска ближайшего НП
        public double UncertaintyPercent { get; set; } = 20.0; // неопределённость окна (% от времени)
    } */
}