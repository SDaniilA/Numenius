using System;
using System.Collections.Generic;

namespace Numenius.Core.Models
{
    /// <summary>
    /// Сценарий (инцидент) – цепочка событий, объединённых по времени и географии
    /// </summary>
    public class Incident
    {
        public int Id { get; set; }                     // Идентификатор в БД (если есть)
        public string ThreatType { get; set; } = string.Empty;
        public ThreatCategory Category { get; set; } = ThreatCategory.Unknown;
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public List<IncidentPoint> Points { get; set; } = new();
        public IncidentStatus Status { get; set; } = IncidentStatus.Active;
        public bool IsReconCompleted { get; set; }      // Разведка завершена (режим внимание)
        public DateTime? ReconTime { get; set; }        // Время завершения разведки
        public double Confidence { get; set; } = 0.5;   // Текущая уверенность
        public string? PredictedZoneGeoJson { get; set; } // Полигон зоны (в формате GeoJSON)
        public List<string> AffectedSettlements { get; set; } = new();
        public DateTime? AttackWindowStart { get; set; }
        public DateTime? AttackWindowEnd { get; set; }
        public string Notes { get; set; } = string.Empty;
    }

    public class IncidentPoint
    {
        public string SettlementName { get; set; } = string.Empty;
        public double Lat { get; set; }
        public double Lon { get; set; }
        public DateTime Time { get; set; }
    }

    public enum IncidentStatus
    {
        Active,      // Активный сценарий (есть движение)
        Watch,       // Режим внимания (разведка завершена, ожидается удар)
        Terminated,  // Завершён (отбой или истекло время)
        Expired      // Устарел (автоматически закрыт)
    }
}