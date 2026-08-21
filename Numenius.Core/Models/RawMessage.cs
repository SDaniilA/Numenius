using System;
using System.Collections.Generic;

namespace Numenius.Core.Models
{
    /// <summary>
    /// Сырое сообщение, полученное от источника (Toast, ручной ввод, STT и т.д.)
    /// </summary>
    public class RawMessage
    {
        /// <summary>Уникальный идентификатор (можно генерировать как SourceType + Timestamp + Hash)</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Тип источника ("Toast", "Manual", "STT", "Telegram")</summary>
        public string SourceType { get; set; } = string.Empty;

        /// <summary>Отправитель (имя канала/приложения)</summary>
        public string Sender { get; set; } = string.Empty;

        /// <summary>Исходный текст сообщения</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Время получения</summary>
        public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Приоритет обработки (0 – наивысший, 5 – низший)</summary>
        public int Priority { get; set; } = 3;

        /// <summary>Дополнительные метаданные (можно расширять)</summary>
        public Dictionary<string, object> Metadata { get; set; } = new();
    }
}