using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;

namespace Numenius.Core.Processors
{
    public class NlpParser : INlpParser
    {
        private readonly IGeoService _geo;
        private readonly HashSet<string> _threatKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "фпв", "fpv", "хорнет", "дартс", "шарк", "лелека", "разведчик",
            "ударный", "ракет", "рсзо", "уаб", "авиация", "пуск", "бпла",
            "шторм", "stormshadow", "scalp", "баба яга", "лютый", "фурия"
        };

        private readonly HashSet<string> _statusKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "отбой", "отмен", "уничтожен", "сбит"
        };

        private readonly HashSet<string> _watchKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "режим внимания", "режим внимание", "внимание, режим"
        };

        public NlpParser(IGeoService geo)
        {
            _geo = geo ?? throw new ArgumentNullException(nameof(geo));
        }

        public ParsedMessage Parse(string rawText, string sender, string sourceType, DateTime receivedAt)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return CreateEmptyParsedMessage(sender, sourceType, rawText ?? "", receivedAt);

            string text = rawText.Trim();

            var parsed = new ParsedMessage
            {
                Id = Guid.NewGuid().ToString(),
                SourceType = sourceType,
                Sender = sender,
                ReceivedAt = receivedAt,
                CleanedText = text,
                Status = "Active",
                Confidence = 0.4,
                Flags = new HashSet<string>()
            };

            string lowerText = text.ToLowerInvariant();

            DetectThreatType(lowerText, parsed);

            var settlements = ExtractSettlements(text);
            parsed.Settlements.AddRange(settlements);

            parsed.Direction = ExtractDirection(text, settlements);

            DetectStatusAndFlags(lowerText, parsed);

            CalculateConfidence(parsed);

            if (parsed.Settlements.Count == 0)
            {
                parsed.Confidence = Math.Min(parsed.Confidence, 0.3);
                parsed.Flags.Add("NoSettlements");
            }

            return parsed;
        }

        private ParsedMessage CreateEmptyParsedMessage(string sender, string sourceType, string rawText, DateTime receivedAt)
        {
            return new ParsedMessage
            {
                Id = Guid.NewGuid().ToString(),
                SourceType = sourceType,
                Sender = sender,
                ReceivedAt = receivedAt,
                CleanedText = rawText,
                ThreatType = "Unknown",
                Category = ThreatCategory.Unknown,
                Status = "Active",
                Confidence = 0.3,
                Flags = new HashSet<string> { "EmptyText" }
            };
        }

        private void DetectThreatType(string lowerText, ParsedMessage parsed)
        {
            foreach (var kw in _threatKeywords)
            {
                if (lowerText.Contains(kw))
                {
                    parsed.ThreatType = kw switch
                    {
                        "фпв" or "fpv" => "FPV",
                        "хорнет" => "Hornet",
                        "дартс" => "Dart",
                        "шарк" => "Shark",
                        "лелека" => "Leleka",
                        "разведчик" => "Recon",
                        "ударный" => "StrikeDrone",
                        "ракет" or "рсзо" or "уаб" or "пуск" or "шторм" or "stormshadow" or "scalp" => "Rocket",
                        "бпла" => "Drone",
                        _ => parsed.ThreatType
                    };
                    break;
                }
            }

            if (parsed.ThreatType == "Rocket")
                parsed.Category = ThreatCategory.Missile;
            else if (parsed.ThreatType == "FPV" || parsed.ThreatType == "Hornet" || parsed.ThreatType == "Dart" ||
                     parsed.ThreatType == "Shark" || parsed.ThreatType == "Leleka" || parsed.ThreatType == "Recon" ||
                     parsed.ThreatType == "StrikeDrone" || parsed.ThreatType == "Drone")
                parsed.Category = ThreatCategory.Drone;
            else if (lowerText.Contains("ветер") || lowerText.Contains("туман") || lowerText.Contains("порывы"))
                parsed.Category = ThreatCategory.Weather;
            else
                parsed.Category = ThreatCategory.Unknown;
        }

        private List<Settlement> ExtractSettlements(string text)
        {
            var result = new List<Settlement>();

            var allNames = _geo.GetAllSettlementNames().ToList();

            // Сортируем названия по длине (от самых длинных к коротким), чтобы сначала искать составные названия
            foreach (var name in allNames.OrderByDescending(n => n.Length))
            {
                var pattern = $@"\b{Regex.Escape(name)}\b";
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                {
                    var s = _geo.FindSettlement(name);
                    if (s != null && !result.Any(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase)))
                        result.Add(s);
                }
            }

            return result;
        }

        private string? ExtractDirection(string text, List<Settlement> settlements)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var allNames = _geo.GetAllSettlementNames().ToList();
            if (allNames.Count == 0) return null;

            // Паттерны для поиска направлений
            var patterns = new[]
            {
                @"от\s+(?<from>[^,;]+?)\s+в\s+сторону\s+(?<to>[^,;]+)",
                @"(?<from>[^,;]+?)\s*[-–—]\s*(?<to>[^,;]+)",
                @"(?:в\s+направлении|по\s+направлению\s+к|в\s+сторону)\s+(?<to>[^,;]+)"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string fromStr = match.Groups["from"].Success ? match.Groups["from"].Value.Trim() : "";
                    string toStr = match.Groups["to"].Success ? match.Groups["to"].Value.Trim() : "";

                    Settlement? from = null;
                    Settlement? to = null;

                    if (!string.IsNullOrEmpty(fromStr))
                        from = FindBestSettlement(fromStr, allNames);
                    if (!string.IsNullOrEmpty(toStr))
                        to = FindBestSettlement(toStr, allNames);

                    if (from != null && to != null)
                        return $"{from.Name}->{to.Name}";
                    else if (from != null && to == null)
                        return $"{from.Name}->?";
                    else if (from == null && to != null)
                        return $"->{to.Name}";
                }
            }

            // Если не нашли явное направление, используем извлечённые поселения
            if (settlements.Count >= 2)
            {
                var first = settlements.First();
                var last = settlements.Last();
                if (!string.Equals(first.Name, last.Name, StringComparison.OrdinalIgnoreCase))
                    return $"{first.Name}->{last.Name}";
            }

            return null;
        }

        // Вспомогательный метод: ищет самое длинное название поселения, содержащееся в строке
        private Settlement? FindBestSettlement(string rawName, IEnumerable<string> allNames)
        {
            string normalized = rawName.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(normalized)) return null;

            // Точное совпадение
            Settlement? exact = _geo.FindSettlement(rawName);
            if (exact != null) return exact;

            // Ищем самое длинное название, которое входит в normalized
            Settlement? best = null;
            int bestLength = 0;
            foreach (var name in allNames)
            {
                string normalizedName = name.ToLowerInvariant();
                if (normalizedName.Length > bestLength && normalized.Contains(normalizedName))
                {
                    var s = _geo.FindSettlement(name);
                    if (s != null)
                    {
                        best = s;
                        bestLength = normalizedName.Length;
                    }
                }
            }
            return best;
        }

        private void DetectStatusAndFlags(string lowerText, ParsedMessage parsed)
        {
            foreach (var kw in _statusKeywords)
            {
                if (lowerText.Contains(kw))
                {
                    parsed.Status = "Terminated";
                    if (kw == "уничтожен" || kw == "сбит")
                        parsed.Flags.Add("Destroyed");
                    break;
                }
            }

            if (parsed.Status != "Terminated")
            {
                foreach (var kw in _watchKeywords)
                {
                    if (lowerText.Contains(kw))
                    {
                        parsed.Status = "Watch";
                        break;
                    }
                }
            }

            if (lowerText.Contains("в укрытие") || lowerText.Contains("укрытие"))
                parsed.Flags.Add("TakeCover");

            if (lowerText.Contains("стоп движение") || lowerText.Contains("остановить движение"))
                parsed.Flags.Add("StopMovement");

            if (lowerText.Contains("подтвержден") || lowerText.Contains("подтверждена"))
                parsed.Flags.Add("Confirmed");
        }

        private void CalculateConfidence(ParsedMessage parsed)
        {
            double confidence = 0.4;

            if (parsed.Settlements.Count > 0)
                confidence += Math.Min(0.3, parsed.Settlements.Count * 0.1);

            if (parsed.ThreatType != "Unknown")
                confidence += 0.2;

            if (!string.IsNullOrEmpty(parsed.Direction))
                confidence += 0.1;

            if (parsed.Status == "Active")
                confidence += 0.05;

            if (parsed.Settlements.Count == 0)
                confidence -= 0.1;

            parsed.Confidence = Math.Clamp(confidence, 0.0, 1.0);
        }
    }
}