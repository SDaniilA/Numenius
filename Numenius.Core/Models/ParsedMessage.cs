using System;
using System.Collections.Generic;

namespace Numenius.Core.Models
{
    /// <summary>
    /// Структурированное сообщение после NLP-обработки
    /// </summary>
    public class ParsedMessage
    {
        public string Id { get; set; } = string.Empty;            // Ссылка на RawMessage.Id
        public string SourceType { get; set; } = string.Empty;
        public string Sender { get; set; } = string.Empty;
        public DateTime ReceivedAt { get; set; }

        /// <summary>Тип угрозы (FPV, Hornet, Shark, Recon, Rocket, Unknown)</summary>
        public string ThreatType { get; set; } = "Unknown";

        /// <summary>Категория угрозы (Drone, Missile, Weather, Unknown)</summary>
        public ThreatCategory Category { get; set; } = ThreatCategory.Unknown;

        /// <summary>Список населённых пунктов, упомянутых в сообщении (с координатами)</summary>
        public List<Settlement> Settlements { get; set; } = new();

        /// <summary>Направление движения (строка типа "A->B" или null)</summary>
        public string? Direction { get; set; }

        /// <summary>Статус (Active, Terminated, Watch, Confirmed)</summary>
        public string Status { get; set; } = "Active";

        /// <summary>Флаг дубликата</summary>
        public bool IsDuplicate { get; set; }

        /// <summary>Уверенность в извлечённых данных (0-1)</summary>
        public double Confidence { get; set; } = 0.5;

        /// <summary>Исходный очищенный текст (для логов)</summary>
        public string CleanedText { get; set; } = string.Empty;

        /// <summary>Дополнительные флаги (например, "отбой", "уничтожен")</summary>
        public HashSet<string> Flags { get; set; } = new();
    }

    public enum ThreatCategory
    {
        Unknown,
        Drone,      // БПЛА всех типов (FPV, Hornet, Shark, Leleka)
        Missile,    // Ракеты, РСЗО, УАБ, авиабомбы
        Weather,    // Природные явления (ветер, туман)
        Other
    }
}