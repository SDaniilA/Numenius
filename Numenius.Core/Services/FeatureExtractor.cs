using System;
using System.Collections.Generic;
using System.Linq;
using Numenius.Core.Models;

namespace Numenius.Core.Services
{
    /// <summary>
    /// Извлекает признаки из инцидента для использования в предикторах.
    /// </summary>
    public class FeatureExtractor
    {
        /// <summary>
        /// Извлекает все признаки из инцидента.
        /// </summary>
        public IncidentFeatures ExtractFeatures(Incident incident)
        {
            if (incident == null)
                throw new ArgumentNullException(nameof(incident));

            if (incident.Points == null || incident.Points.Count == 0)
                return new IncidentFeatures
                {
                    ThreatType = incident.ThreatType ?? "Unknown",
                    Category = incident.Category,
                    TimeOfDay = "Unknown",
                    DayOfWeek = "Unknown",
                    Season = "Unknown",
                    HasRecon = incident.IsReconCompleted,
                    PointsCount = 0,
                    TotalDistance = 0,
                    AverageSpeed = 0,
                    Region = "Unknown",
                    Settlements = new List<string>()
                };

            var firstSeen = incident.FirstSeen;
            var lastSeen = incident.LastSeen;

            // Время суток
            string timeOfDay = GetTimeOfDay(firstSeen);

            // День недели
            string dayOfWeek = firstSeen.DayOfWeek.ToString();

            // Сезон
            string season = GetSeason(firstSeen);

            // Общее расстояние
            double totalDistance = CalculateTotalDistance(incident.Points);

            // Средняя скорость (км/ч)
            double hours = (lastSeen - firstSeen).TotalHours;
            double avgSpeed = hours > 0.01 ? totalDistance / hours : 0;

            // Регион – определяем как комбинацию первой и последней точки (или кластер)
            string region = DetermineRegion(incident.Points);

            // Список всех уникальных поселений
            var settlements = incident.Points
                .Select(p => p.SettlementName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct()
                .ToList();

            return new IncidentFeatures
            {
                ThreatType = incident.ThreatType ?? "Unknown",
                Category = incident.Category,
                TimeOfDay = timeOfDay,
                DayOfWeek = dayOfWeek,
                Season = season,
                HasRecon = incident.IsReconCompleted,
                PointsCount = incident.Points.Count,
                TotalDistance = totalDistance,
                AverageSpeed = avgSpeed,
                Region = region,
                Settlements = settlements
            };
        }

        /// <summary>
        /// Определяет время суток по часу.
        /// </summary>
        private string GetTimeOfDay(DateTime time)
        {
            int hour = time.Hour;
            if (hour >= 5 && hour < 12) return "Morning";
            if (hour >= 12 && hour < 17) return "Day";
            if (hour >= 17 && hour < 22) return "Evening";
            return "Night";
        }

        /// <summary>
        /// Определяет сезон по месяцу.
        /// </summary>
        private string GetSeason(DateTime time)
        {
            int month = time.Month;
            if (month >= 3 && month <= 5) return "Spring";
            if (month >= 6 && month <= 8) return "Summer";
            if (month >= 9 && month <= 11) return "Autumn";
            return "Winter";
        }

        /// <summary>
        /// Вычисляет общее расстояние (в км) по цепочке точек.
        /// </summary>
        private double CalculateTotalDistance(List<IncidentPoint> points)
        {
            if (points.Count < 2) return 0;
            double total = 0;
            for (int i = 1; i < points.Count; i++)
            {
                total += HaversineDistance(
                    points[i - 1].Lat, points[i - 1].Lon,
                    points[i].Lat, points[i].Lon);
            }
            return total;
        }

        /// <summary>
        /// Вычисляет расстояние по формуле гаверсинуса (в км).
        /// </summary>
        private double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371; // радиус Земли в км
            double dLat = (lat2 - lat1) * Math.PI / 180;
            double dLon = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        /// <summary>
        /// Определяет регион по точкам. Если первая и последняя точки совпадают – возвращает название,
        /// иначе – условный кластер (округлённые средние координаты).
        /// </summary>
        private string DetermineRegion(List<IncidentPoint> points)
        {
            if (points == null || points.Count == 0) return "Unknown";
            var first = points.First();
            var last = points.Last();
            if (first.SettlementName == last.SettlementName && !string.IsNullOrEmpty(first.SettlementName))
                return first.SettlementName;

            // Если названия разные – используем округлённые координаты центра масс
            double avgLat = points.Average(p => p.Lat);
            double avgLon = points.Average(p => p.Lon);
            return $"{Math.Round(avgLat, 1)}_{Math.Round(avgLon, 1)}";
        }
    }

    /// <summary>
    /// Набор признаков инцидента.
    /// </summary>
    public class IncidentFeatures
    {
        public string ThreatType { get; set; } = string.Empty;
        public ThreatCategory Category { get; set; }
        public string TimeOfDay { get; set; } = string.Empty;
        public string DayOfWeek { get; set; } = string.Empty;
        public string Season { get; set; } = string.Empty;
        public bool HasRecon { get; set; }
        public int PointsCount { get; set; }
        public double TotalDistance { get; set; }
        public double AverageSpeed { get; set; }
        public string Region { get; set; } = string.Empty;
        public List<string> Settlements { get; set; } = new();
    }
}