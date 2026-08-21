using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Numenius.Core.Config;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;

namespace Numenius.Core.Services
{
    public class ReportGenerator
    {
        private readonly IDatabaseService _db;
        private readonly HeuristicsConfig _heuristics;

        public ReportGenerator(IDatabaseService db, HeuristicsConfig heuristics)
        {
            _db = db;
            _heuristics = heuristics;
        }

        public async Task<string> GenerateReportAsync(DateTime start, DateTime end, string title = "Отчёт")
        {
            var incidents = await _db.GetIncidentsForPeriodAsync(start, end);
            var sb = new StringBuilder();

            sb.AppendLine($"📊 {title} за {start:dd.MM.yyyy} – {end:dd.MM.yyyy}");
            sb.AppendLine(new string('=', 50));

            if (!incidents.Any())
            {
                sb.AppendLine("Нет данных за указанный период.");
                return sb.ToString();
            }

            var total = incidents.Count();
            sb.AppendLine($"Всего инцидентов: {total}");

            // 1. Распределение по типам
            var typeGroups = incidents.GroupBy(i => i.ThreatType ?? "Unknown")
                                      .Select(g => new { Type = g.Key, Count = g.Count() })
                                      .OrderByDescending(g => g.Count);
            sb.AppendLine("\n📌 РАСПРЕДЕЛЕНИЕ ПО ТИПАМ:");
            foreach (var g in typeGroups)
            {
                var percent = 100.0 * g.Count / total;
                sb.AppendLine($"  {g.Type}: {g.Count} ({percent:F1}%)");
            }

            // 2. Топ-5 направлений (переходов между НП)
            var transitions = new Dictionary<string, int>();
            foreach (var inc in incidents)
            {
                if (inc.Points.Count < 2) continue;
                for (int i = 1; i < inc.Points.Count; i++)
                {
                    var from = inc.Points[i - 1].SettlementName;
                    var to = inc.Points[i].SettlementName;
                    if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) continue;
                    var key = $"{from} → {to}";
                    transitions.TryGetValue(key, out int val);
                    transitions[key] = val + 1;
                }
            }
            var topTransitions = transitions.OrderByDescending(kv => kv.Value).Take(5);
            sb.AppendLine("\n🚀 ТОП-5 НАПРАВЛЕНИЙ:");
            int rank = 1;
            foreach (var kv in topTransitions)
            {
                sb.AppendLine($"  {rank}. {kv.Key} ({kv.Value} раз)");
                rank++;
            }

            // 3. Пики активности по часам (с учётом смещения)
            var hourGroups = incidents
                .GroupBy(i => i.FirstSeen.AddHours(_heuristics.TimeZoneOffsetHours).Hour)
                .Select(g => new { Hour = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .Take(3);
            sb.AppendLine("\n⏰ ПИКИ АКТИВНОСТИ (по часам):");
            foreach (var g in hourGroups)
            {
                sb.AppendLine($"  {g.Hour:00}:00 – {g.Hour + 1:00}:00: {g.Count} инцидентов");
            }

            // 4. Топ-5 населённых пунктов по упоминаниям (из точек)
            var allPoints = incidents.SelectMany(i => i.Points);
            var npGroups = allPoints.GroupBy(p => p.SettlementName)
                                    .Select(g => new { Name = g.Key, Count = g.Count() })
                                    .Where(g => !string.IsNullOrEmpty(g.Name))
                                    .OrderByDescending(g => g.Count)
                                    .Take(5);
            sb.AppendLine("\n🏘️ НАСЕЛЁННЫЕ ПУНКТЫ (топ по упоминаниям):");
            foreach (var g in npGroups)
            {
                sb.AppendLine($"  {g.Name}: {g.Count}");
            }

            // 5. Конечные точки (где зафиксирован отбой или завершение)
            var endPoints = new Dictionary<string, int>();
            foreach (var inc in incidents)
            {
                if (inc.Points.Count == 0) continue;
                var last = inc.Points.Last();
                if (!string.IsNullOrEmpty(last.SettlementName))
                {
                    endPoints.TryGetValue(last.SettlementName, out int val);
                    endPoints[last.SettlementName] = val + 1;
                }
            }
            var topEndPoints = endPoints.OrderByDescending(kv => kv.Value).Take(5);
            sb.AppendLine("\n🎯 КОНЕЧНЫЕ ТОЧКИ (где зафиксирован отбой или завершение):");
            foreach (var kv in topEndPoints)
            {
                sb.AppendLine($"  {kv.Key}: {kv.Value} раз");
            }

            // 6. Средняя уверенность
            var avgConf = incidents.Average(i => i.Confidence);
            sb.AppendLine($"\n📈 Средняя уверенность прогнозов: {avgConf:P0}");

            // 7. Доля завершённых отбоем
            var terminated = incidents.Count(i => i.Status == IncidentStatus.Terminated);
            sb.AppendLine($"🔄 Завершено (отбой): {terminated} из {total} ({100.0 * terminated / total:F1}%)");

            // 8. Сравнение предикторов (прогнозы)
            var predictions = await _db.GetPredictionsForPeriodAsync(start, end);
            if (predictions.Any())
            {
                var byPredictor = predictions.GroupBy(p => p.PredictorType)
                                             .Select(g => new { Type = g.Key, Count = g.Count(), AvgConf = g.Average(p => p.Confidence) })
                                             .OrderByDescending(g => g.Count);
                sb.AppendLine("\n🧠 СРАВНЕНИЕ ПРЕДИКТОРОВ:");
                foreach (var p in byPredictor)
                {
                    sb.AppendLine($"  {p.Type}: {p.Count} прогнозов, средняя уверенность {p.AvgConf:P0}");
                }

                // 9. Совпадение прогнозов (Graph vs Statistical)
                await AppendPredictionComparison(sb, predictions);
            }

            return sb.ToString();
        }

        private async Task AppendPredictionComparison(StringBuilder sb, IEnumerable<Prediction> predictions)
        {
            var byIncident = predictions.GroupBy(p => p.IncidentId);
            int totalCompared = 0;
            int totalMatched = 0;
            var details = new List<string>();

            foreach (var group in byIncident)
            {
                var graphPred = group.FirstOrDefault(p => p.PredictorType == "Graph");
                var statPred = group.FirstOrDefault(p => p.PredictorType == "Statistical");
                if (graphPred == null || statPred == null)
                    continue;

                totalCompared++;
                var graphZones = new HashSet<string>(graphPred.AffectedSettlements.Select(s => s.ToLowerInvariant()));
                var statZones = new HashSet<string>(statPred.AffectedSettlements.Select(s => s.ToLowerInvariant()));
                if (graphZones.Count == 0 && statZones.Count == 0)
                    continue;

                var intersection = graphZones.Intersect(statZones).Count();
                var union = graphZones.Union(statZones).Count();
                if (union > 0)
                {
                    double matchPercent = 100.0 * intersection / union;
                    if (matchPercent >= 50)
                        totalMatched++;
                    details.Add($"  Инц.#{group.Key}: совпадение зон {matchPercent:F0}% (Graph: {string.Join(",", graphZones)}, Stat: {string.Join(",", statZones)})");
                }
            }

            if (totalCompared > 0)
            {
                double overallMatch = 100.0 * totalMatched / totalCompared;
                sb.AppendLine($"\n📊 СОВПАДЕНИЕ ПРОГНОЗОВ (Graph vs Statistical):");
                sb.AppendLine($"  Всего сравнений: {totalCompared}");
                sb.AppendLine($"  Совпадают (зоны пересекаются >50%): {totalMatched} ({overallMatch:F1}%)");
                if (details.Count <= 5)
                {
                    foreach (var d in details)
                        sb.AppendLine(d);
                }
                else
                {
                    sb.AppendLine($"  (показано первых 5 из {details.Count})");
                    foreach (var d in details.Take(5))
                        sb.AppendLine(d);
                }
            }
        }

        public async Task SaveReportAsync(DateTime start, DateTime end, string folder = "Reports")
        {
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            var title = $"Отчёт за {start:yyyyMMdd}-{end:yyyyMMdd}";
            var content = await GenerateReportAsync(start, end, title);
            var filePath = Path.Combine(folder, $"report_{start:yyyyMMdd}_{end:yyyyMMdd}.txt");
            await File.WriteAllTextAsync(filePath, content);
            Console.WriteLine($"✅ Отчёт сохранён: {filePath}");
        }
    }
}