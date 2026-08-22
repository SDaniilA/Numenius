using System;
using System.Collections.Generic;

namespace Numenius.Core.Models
{
    public class RawMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string SourceType { get; set; } = string.Empty;
        public string Sender { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Время события (публикации) — если доступно, иначе null.</summary>
        public DateTime? EventTime { get; set; }

        public int Priority { get; set; } = 3;
        public Dictionary<string, object> Metadata { get; set; } = new();
        public string? ReplyToMessageId { get; set; }
    }
}