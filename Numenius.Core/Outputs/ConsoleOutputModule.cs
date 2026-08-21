using System;
using System.Linq;
using System.Threading.Tasks;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;

namespace Numenius.Core.Outputs
{
    public class ConsoleOutputModule : IOutputModule
    {
        private readonly ConsoleOutputConfig _config;

        public ConsoleOutputModule(ConsoleOutputConfig config = null)
        {
            _config = config ?? new ConsoleOutputConfig();
        }

        public Task InitializeAsync()
        {
            Console.WriteLine("📟 Консольный вывод инициализирован.");
            return Task.CompletedTask;
        }

        public Task HandleParsedMessageAsync(ParsedMessage message)
        {
            if (!_config.Enabled || message == null) return Task.CompletedTask;

            try
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\n📨 [{message.ReceivedAt:HH:mm:ss}] {message.Sender ?? "Unknown"}");
                Console.ResetColor();
                Console.WriteLine($"   Тип: {message.ThreatType ?? "Unknown"} | Категория: {message.Category}");
                Console.WriteLine($"   Текст: {message.CleanedText ?? ""}");
                if (message.Settlements != null && message.Settlements.Count > 0)
                {
                    Console.WriteLine($"   НП: {string.Join(", ", message.Settlements.Select(s => s?.Name ?? "?"))}");
                }
                else
                {
                    Console.WriteLine($"   НП: (нет)");
                }
                if (!string.IsNullOrEmpty(message.Direction))
                    Console.WriteLine($"   Направление: {message.Direction}");
                Console.WriteLine($"   Статус: {message.Status ?? "Active"}, Уверенность: {message.Confidence:P0}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Ошибка вывода сообщения: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public Task HandlePredictionAsync(Prediction prediction)
        {
            if (!_config.Enabled || prediction == null) return Task.CompletedTask;
            if (prediction.Confidence < 0.6) return Task.CompletedTask;

            try
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n🔮 ПРОГНОЗ [{prediction.CreatedAt:HH:mm:ss}]");
                Console.ResetColor();
                Console.WriteLine($"   Тип: {prediction.ThreatType ?? "Unknown"}");
                Console.WriteLine($"   Зона: {string.Join(", ", prediction.AffectedSettlements ?? new System.Collections.Generic.List<string>())}");
                
                string startStr = prediction.AttackWindowStart.HasValue 
                    ? prediction.AttackWindowStart.Value.ToString("HH:mm") 
                    : "??:??";
                string endStr = prediction.AttackWindowEnd.HasValue 
                    ? prediction.AttackWindowEnd.Value.ToString("HH:mm") 
                    : "??:??";
                Console.WriteLine($"   Окно: {startStr} – {endStr}");
                
                Console.WriteLine($"   Уверенность: {prediction.Confidence:P0}");
                Console.WriteLine($"   Предиктор: {prediction.PredictorType ?? "Unknown"}");
                Console.WriteLine($"   {prediction.Notes ?? ""}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Ошибка вывода прогноза: {ex.Message}");
            }

            return Task.CompletedTask;
        }
    }

    public class ConsoleOutputConfig
    {
        public bool Enabled { get; set; } = true;
    }
}