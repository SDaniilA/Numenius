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

            foreach (var name in allNames)
            {
                var pattern = $@"\b{Regex.Escape(name)}\b";
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                {
                    var s = _geo.FindSettlement(name);
                    if (s != null && !result.Any(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase)))
                        result.Add(s);
                }
            }

            if (result.Count == 0)
            {
                var words = Regex.Split(text, @"[^\w\-]", RegexOptions.IgnoreCase)
                                 .Where(w => w.Length >= 3)
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToList();

                foreach (var word in words)
                {
                    var exact = allNames.FirstOrDefault(n => string.Equals(n, word, StringComparison.OrdinalIgnoreCase));
                    if (exact != null)
                    {
                        var s = _geo.FindSettlement(exact);
                        if (s != null && !result.Any(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase)))
                            result.Add(s);
                    }
                }
            }

            return result;
        }

        private string? ExtractDirection(string text, List<Settlement> settlements)
        {
            string lowerText = text.ToLowerInvariant();

            var match = Regex.Match(lowerText, @"от\s+([^\s,]+)\s+в\s+сторону\s+([^\s,]+)");
            if (match.Success)
            {
                var from = _geo.FindSettlement(match.Groups[1].Value);
                var to = _geo.FindSettlement(match.Groups[2].Value);
                if (from != null && to != null)
                    return $"{from.Name}->{to.Name}";
            }

            match = Regex.Match(lowerText, @"([^\s,]+)\s*[-–]\s*([^\s,]+)");
            if (match.Success)
            {
                var a = _geo.FindSettlement(match.Groups[1].Value);
                var b = _geo.FindSettlement(match.Groups[2].Value);
                if (a != null && b != null)
                    return $"{a.Name}->{b.Name}";
            }

            match = Regex.Match(lowerText, @"(?:в\s+направлении|по\s+направлению\s+к|в\s+сторону)\s+([^\s,]+)");
            if (match.Success)
            {
                var target = _geo.FindSettlement(match.Groups[1].Value);
                if (target != null)
                    return $"->{target.Name}";
            }

            if (settlements.Count >= 2)
            {
                var first = settlements.First();
                var last = settlements.Last();
                if (!string.Equals(first.Name, last.Name, StringComparison.OrdinalIgnoreCase))
                    return $"{first.Name}->{last.Name}";
            }

            return null;
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