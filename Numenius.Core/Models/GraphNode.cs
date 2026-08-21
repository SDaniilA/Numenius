using System.Collections.Generic;

namespace Numenius.Core.Models
{
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
        public List<GraphEdge> Edges { get; set; } = new();
    }
}