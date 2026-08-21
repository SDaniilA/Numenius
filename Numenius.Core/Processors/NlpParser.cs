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
        private readonly ITextNormalizer _normalizer;

        private readonly HashSet<string> _threatKeywords = new(StringComparer.OrdinalIgnoreCase)
		{
			"фпв", "fpv", "хорнет", "дартс", "шарк", "лелека", "разведчик",
			"ударный", "ракет", "рсзо", "уаб", "авиация", "пуск", "бпла",
			"шторм", "stormshadow", "scalp", "баба яга", "лютый", "фурия",
			"обстрел", "артобстрел", "прилет", "прилетел", "взрыв", "взрывы",
			"ракетный удар", "ракетная опасность", "обстрел"
		};

        private readonly HashSet<string> _statusKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "отбой", "отмен", "уничтожен", "сбит"
        };

        private readonly HashSet<string> _watchKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "режим внимания", "режим внимание", "внимание, режим"
        };

        public NlpParser(IGeoService geo, ITextNormalizer normalizer)
        {
            _geo = geo ?? throw new ArgumentNullException(nameof(geo));
            _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
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
						"обстрел" or "артобстрел" or "прилет" or "взрыв" or "ракетный удар" or "ракетная опасность" => "Rocket",
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
            string normalizedText = _normalizer.NormalizeText(text);
            var allNames = _geo.GetAllSettlementNames().ToList();

            // Нормализуем названия и создаём словарь соответствия
            var nameMap = new Dictionary<string, Settlement>();
            foreach (var name in allNames)
            {
                string normName = _normalizer.Normalize(name);
                if (!nameMap.ContainsKey(normName))
                    nameMap[normName] = _geo.FindSettlement(name);
            }

            // Ищем в нормализованном тексте подстроки нормализованных названий
            foreach (var normName in nameMap.Keys.OrderByDescending(n => n.Length))
            {
                if (normName.Length >= 3 && normalizedText.Contains(normName))
                {
                    var s = nameMap[normName];
                    if (s != null && !result.Any(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase)))
                        result.Add(s);
                }
            }

            // Удаляем административные единицы, если есть более конкретные НП
            var specific = result.Where(x => !x.Name.Contains("район") && !x.Name.Contains("округ") && !x.Name.Contains("область")).ToList();
            if (specific.Count > 0)
                result = specific;

            return result;
        }

        private string? ExtractDirection(string text, List<Settlement> settlements)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var allNames = _geo.GetAllSettlementNames().ToList();
            if (allNames.Count == 0) return null;

            string normalizedText = _normalizer.NormalizeText(text);
            Settlement? from = null;
            List<Settlement> toList = new();

            // Ищем "от X" – определяем источник
            var matchFrom = Regex.Match(normalizedText, @"\bот\s+(?<from>[а-яё\- ]+?)(?:\s+(?:в\s+направлении|по\s+направлению|на|к)\s+|$|[,.!?])", RegexOptions.IgnoreCase);
            if (matchFrom.Success)
            {
                string fromStr = matchFrom.Groups["from"].Value.Trim();
                from = FindBestSettlement(fromStr, allNames);
            }

            // Ищем "в направлении Y" или "на Y"
            var matchTo = Regex.Match(normalizedText, @"(?:в\s+направлении|по\s+направлению|на)\s+(?<to>[а-яё\- ,]+)", RegexOptions.IgnoreCase);
            if (matchTo.Success)
            {
                string toStr = matchTo.Groups["to"].Value.Trim();
                var parts = Regex.Split(toStr, @"\s*(?:,|и)\s*");
                foreach (var part in parts)
                {
                    var s = FindBestSettlement(part, allNames);
                    if (s != null && !toList.Contains(s))
                        toList.Add(s);
                }
            }

            // Если нашли FROM, добавляем все остальные найденные НП как TO (кроме самого FROM)
            if (from != null)
            {
                var foundSettlements = ExtractSettlements(text);
                foreach (var s in foundSettlements)
                {
                    if (!string.Equals(s.Name, from.Name, StringComparison.OrdinalIgnoreCase) && !toList.Contains(s))
                        toList.Add(s);
                }
            }

            // Если нет FROM и TO, пробуем через дефис
            if (from == null && toList.Count == 0)
            {
                var matchDash = Regex.Match(text, @"(?<from>[^\-]+?)\s*[-–—]\s*(?<to>[^\-]+)");
                if (matchDash.Success)
                {
                    var fromS = FindBestSettlement(matchDash.Groups["from"].Value, allNames);
                    var toS = FindBestSettlement(matchDash.Groups["to"].Value, allNames);
                    if (fromS != null && toS != null)
                        return $"{fromS.Name}->{toS.Name}";
                }
            }

            // Формируем результат
            if (from != null && toList.Count > 0)
            {
                return $"{from.Name}->{string.Join(",", toList.Select(t => t.Name))}";
            }
            else if (from != null && toList.Count == 0)
            {
                return null;
            }
            else if (from == null && toList.Count > 0)
            {
                return $"->{string.Join(",", toList.Select(t => t.Name))}";
            }

            // Последний вариант – используем извлечённые поселения
            if (settlements.Count >= 2)
            {
                var first = settlements.First();
                var last = settlements.Last();
                if (!string.Equals(first.Name, last.Name, StringComparison.OrdinalIgnoreCase))
                    return $"{first.Name}->{last.Name}";
            }

            return null;
        }

        private Settlement? FindBestSettlement(string rawName, IEnumerable<string> allNames)
        {
            string normalized = _normalizer.Normalize(rawName);
            if (string.IsNullOrEmpty(normalized)) return null;

            // Точное совпадение с нормализованным названием
            foreach (var name in allNames)
            {
                if (_normalizer.Normalize(name) == normalized)
                    return _geo.FindSettlement(name);
            }

            // Ищем самое длинное название, которое является частью normalized
            Settlement? best = null;
            int bestLength = 0;
            foreach (var name in allNames)
            {
                string normName = _normalizer.Normalize(name);
                if (normName.Length > bestLength && normalized.Contains(normName))
                {
                    var s = _geo.FindSettlement(name);
                    if (s != null)
                    {
                        best = s;
                        bestLength = normName.Length;
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