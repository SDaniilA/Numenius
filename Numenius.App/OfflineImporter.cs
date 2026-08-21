using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;

namespace Numenius.App
{
    public class OfflineImporter
    {
        private readonly IMessageProcessor _processor;
        private readonly IDatabaseService _db;
        private int _processed;
        private int _errors;
        private readonly List<DateTime> _timestamps = new();
        private readonly Dictionary<string, int> _typeStats = new();

        public OfflineImporter(IMessageProcessor processor, IDatabaseService db)
        {
            _processor = processor;
            _db = db;
        }

        public async Task ImportAsync(string jsonPath)
        {
            if (!File.Exists(jsonPath))
            {
                Console.WriteLine($"❌ Файл не найден: {jsonPath}");
                return;
            }

            Console.WriteLine($"📥 Загрузка {jsonPath}...");
            var json = await File.ReadAllTextAsync(jsonPath);
            var root = JsonConvert.DeserializeObject<RootObject>(json);
            if (root?.Messages == null || root.Messages.Count == 0)
            {
                Console.WriteLine("⚠️ Сообщения не найдены.");
                return;
            }

            Console.WriteLine($"📊 Найдено {root.Messages.Count} сообщений.");
            var cts = new System.Threading.CancellationTokenSource();

            int total = root.Messages.Count;
            int processedWithError = 0;

            foreach (var msg in root.Messages)
            {
                try
                {
                    if (msg.Text == null) continue;
                    string text = ExtractText(msg.Text);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    // Определяем время
                    DateTime receivedAt;
                    if (msg.DateUnixTime > 0)
                    {
                        receivedAt = DateTimeOffset.FromUnixTimeSeconds(msg.DateUnixTime).UtcDateTime;
                    }
                    else
                    {
                        receivedAt = ParseDate(msg.Date);
                        if (receivedAt == DateTime.MinValue)
                        {
                            Console.WriteLine($"⚠️ Не удалось распарсить дату для сообщения {msg.Id}, пропускаем.");
                            processedWithError++;
                            continue;
                        }
                    }

                    _timestamps.Add(receivedAt);

                    var raw = new RawMessage
                    {
                        Id = $"import_{msg.Id}_{msg.Date}",
                        SourceType = "Telegram",
                        Sender = msg.From ?? root.Name ?? "Unknown",
                        Text = text,
                        ReceivedAt = receivedAt,
                        Priority = 3
                    };

                    await _processor.ProcessAsync(raw, cts.Token);
                    _processed++;
                    if (_processed % 100 == 0)
                        Console.WriteLine($"   Обработано {_processed} сообщений...");
                }
                catch (Exception ex)
                {
                    _errors++;
                    if (_errors <= 10)
                        Console.WriteLine($"⚠️ Ошибка обработки сообщения {msg.Id}: {ex.Message}");
                }
            }

            Console.WriteLine($"\n✅ Импорт завершён.");
            Console.WriteLine($"   Обработано: {_processed}");
            Console.WriteLine($"   Ошибок: {_errors}");
            Console.WriteLine($"   Пропущено (без даты): {processedWithError}");

            // ===== DEBUG ОТЧЁТ =====
            Console.WriteLine("\n📊 DEBUG-ОТЧЁТ:");
            Console.WriteLine($"   Всего сообщений в файле: {total}");
            Console.WriteLine($"   Успешно обработано: {_processed}");

            // Примеры первых 5 и последних 5 сообщений с временем
            if (_timestamps.Count > 0)
            {
                var first = _timestamps.Take(5).ToList();
                var last = _timestamps.Skip(Math.Max(0, _timestamps.Count - 5)).ToList();

                Console.WriteLine("\n   ⏰ ПРИМЕРЫ ВРЕМЕНИ (первые 5):");
                foreach (var dt in first)
                    Console.WriteLine($"      {dt:yyyy-MM-dd HH:mm:ss} UTC");

                Console.WriteLine("\n   ⏰ ПРИМЕРЫ ВРЕМЕНИ (последние 5):");
                foreach (var dt in last)
                    Console.WriteLine($"      {dt:yyyy-MM-dd HH:mm:ss} UTC");

                // Распределение по часам (пики активности)
                var hourGroups = _timestamps
                    .GroupBy(dt => dt.Hour)
                    .Select(g => new { Hour = g.Key, Count = g.Count() })
                    .OrderByDescending(g => g.Count)
                    .Take(5);
                Console.WriteLine("\n   📈 ТОП-5 ЧАСОВ АКТИВНОСТИ (по UTC):");
                foreach (var g in hourGroups)
                {
                    Console.WriteLine($"      {g.Hour:00}:00 – {g.Hour + 1:00}:00: {g.Count} сообщений");
                }
            }
            else
            {
                Console.WriteLine("   ⚠️ Нет временных меток для анализа.");
            }

            // Статистика по типам (если есть)
            if (_typeStats.Count > 0)
            {
                Console.WriteLine("\n   📌 ТИПЫ СООБЩЕНИЙ (первые 5):");
                foreach (var kv in _typeStats.OrderByDescending(kv => kv.Value).Take(5))
                {
                    Console.WriteLine($"      {kv.Key}: {kv.Value}");
                }
            }

            Console.WriteLine("\n💡 Рекомендация: проверьте, что время в примерах соответствует реальному времени сообщений (из архива).");
            Console.WriteLine("   Если время не совпадает – проверьте формат date и date_unixtime в исходном JSON.");
        }

        private string ExtractText(object textObj)
        {
            if (textObj is string str)
                return str;

            if (textObj is Newtonsoft.Json.Linq.JArray arr)
            {
                var parts = new List<string>();
                foreach (var item in arr)
                {
                    if (item.Type == Newtonsoft.Json.Linq.JTokenType.String)
                        parts.Add(item.ToString());
                    else if (item.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                    {
                        var textProp = item["text"];
                        if (textProp != null && textProp.Type == Newtonsoft.Json.Linq.JTokenType.String)
                            parts.Add(textProp.ToString());
                    }
                }
                return string.Join(" ", parts);
            }

            return textObj?.ToString() ?? "";
        }

        private DateTime ParseDate(string dateStr)
        {
            if (string.IsNullOrEmpty(dateStr))
                return DateTime.MinValue;

            // Формат из вашего JSON: "2026-08-19T19:06:06"
            if (DateTime.TryParseExact(dateStr, "yyyy-MM-ddTHH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
            {
                return dt.ToUniversalTime();
            }

            // Резервный парсинг
            if (DateTime.TryParse(dateStr, out dt))
                return dt.ToUniversalTime();

            return DateTime.MinValue;
        }

        private class RootObject
        {
            [JsonProperty("name")]
            public string Name { get; set; } = string.Empty;

            [JsonProperty("messages")]
            public List<MessageItem> Messages { get; set; } = new();
        }

        private class MessageItem
        {
            [JsonProperty("id")]
            public long Id { get; set; }

            [JsonProperty("date")]
            public string Date { get; set; } = string.Empty;

            [JsonProperty("date_unixtime")]
            public long DateUnixTime { get; set; }

            [JsonProperty("from")]
            public string From { get; set; } = string.Empty;

            [JsonProperty("text")]
            public object Text { get; set; }
        }
    }
}