using System;
using System.Collections.Generic;

namespace Numenius.Core.Models
{
    public class Prediction
    {
        public int Id { get; set; }
        public int IncidentId { get; set; }
        public string ThreatType { get; set; } = string.Empty;
        public string? ZoneGeoJson { get; set; }
        public List<string> AffectedSettlements { get; set; } = new();
        public DateTime? AttackWindowStart { get; set; }
        public DateTime? AttackWindowEnd { get; set; }
        public double Confidence { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string Notes { get; set; } = string.Empty;
        public string PredictorType { get; set; } = "Unknown"; // Graph, Statistical, Ensemble, etc.
    }
}