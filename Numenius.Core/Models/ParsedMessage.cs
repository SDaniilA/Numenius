using System;
using System.Collections.Generic;

namespace Numenius.Core.Models
{
    public class ParsedMessage
    {
        public string Id { get; set; } = string.Empty;
        public string SourceType { get; set; } = string.Empty;
        public string Sender { get; set; } = string.Empty;
        public DateTime ReceivedAt { get; set; }

        /// <summary>Время события (публикации) — если доступно.</summary>
        public DateTime? EventTime { get; set; }

        public string ThreatType { get; set; } = "Unknown";
        public ThreatCategory Category { get; set; } = ThreatCategory.Unknown;
        public List<Settlement> Settlements { get; set; } = new();
        public string? Direction { get; set; }
        public string Status { get; set; } = "Active";
        public bool IsDuplicate { get; set; }
        public double Confidence { get; set; } = 0.5;
        public string CleanedText { get; set; } = string.Empty;
        public HashSet<string> Flags { get; set; } = new();
        public string SourceMessageId { get; set; } = string.Empty;
        public int? IncidentId { get; set; }
    }
}