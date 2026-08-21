using System;
using System.Collections.Generic;
using System.Linq;
using Numenius.Core.Models;

namespace Numenius.Core.Services
{
    public class FeatureExtractor
    {
        public IncidentFeatures ExtractFeatures(Incident incident)
        {
            if (incident == null || incident.Points == null || incident.Points.Count == 0)
                return new IncidentFeatures();

            var features = new IncidentFeatures
            {
                ThreatType = incident.ThreatType,
                Category = incident.Category,
                TimeOfDay = GetTimeOfDay(incident.FirstSeen),
                DayOfWeek = incident.FirstSeen.DayOfWeek.ToString(),
                Season = GetSeason(incident.FirstSeen),
                HasRecon = incident.IsReconCompleted,
                PointsCount = incident.Points.Count,
                TotalDistance = CalculateTotalDistance(incident.Points),
                AverageSpeed = CalculateAverageSpeed(incident.Points),
                Region = DetermineRegion(incident.Points),
                Settlements = incident.AffectedSettlements.ToList()
            };

            return features;
        }

        private string GetTimeOfDay(DateTime time)
        {
            int hour = time.Hour;
            if (hour >= 5 && hour < 12) return "Morning";
            if (hour >= 12 && hour < 17) return "Day";
            if (hour >= 17 && hour < 22) return "Evening";
            return "Night";
        }

        private string GetSeason(DateTime time)
        {
            int month = time.Month;
            if (month >= 3 && month <= 5) return "Spring";
            if (month >= 6 && month <= 8) return "Summer";
            if (month >= 9 && month <= 11) return "Autumn";
            return "Winter";
        }

        private double CalculateTotalDistance(List<IncidentPoint> points)
        {
            if (points.Count < 2) return 0;
            double total = 0;
            for (int i = 1; i < points.Count; i++)
            {
                total += HaversineDistance(points[i - 1].Lat, points[i - 1].Lon,
                                            points[i].Lat, points[i].Lon);
            }
            return total;
        }

        private double CalculateAverageSpeed(List<IncidentPoint> points)
        {
            if (points.Count < 2) return 0;
            double totalDist = CalculateTotalDistance(points);
            var first = points.First();
            var last = points.Last();
            double hours = (last.Time - first.Time).TotalHours;
            if (hours < 0.01) return 0;
            return totalDist / hours;
        }

        private string DetermineRegion(List<IncidentPoint> points)
        {
            if (points.Count == 0) return "Unknown";
            var first = points.First();
            var last = points.Last();
            if (first.SettlementName == last.SettlementName)
                return first.SettlementName;
            return $"{first.SettlementName}-{last.SettlementName}";
        }

        private double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371;
            double dLat = (lat2 - lat1) * Math.PI / 180;
            double dLon = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }
    }

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