using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Numenius.Core.Config;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;
using Numenius.Core.Utilities;

namespace Numenius.Core.Services
{
    public class ScenarioManager : IScenarioManager
    {
        private readonly IDatabaseService _db;
        private readonly IGeoService _geo;
        private readonly HeuristicsConfig _heuristics;
        private readonly Dictionary<int, Incident> _activeCache = new();
        private DateTime _cacheValidUntil = DateTime.MinValue;
        private readonly object _cacheLock = new();

        public ScenarioManager(IDatabaseService db, IGeoService geo, HeuristicsConfig heuristics)
        {
            _db = db;
            _geo = geo;
            _heuristics = heuristics;
        }

        private async Task RefreshCacheAsync()
        {
            lock (_cacheLock)
            {
                if ((DateTime.UtcNow - _cacheValidUntil).TotalSeconds < 60)
                    return;
                _activeCache.Clear();
            }

            var active = await _db.GetActiveIncidentsAsync();
            foreach (var inc in active)
            {
                if (inc != null && inc.Status != IncidentStatus.Terminated && inc.Status != IncidentStatus.Expired)
                    _activeCache[inc.Id] = inc;
            }

            lock (_cacheLock)
                _cacheValidUntil = DateTime.UtcNow.AddSeconds(60);
        }

        public async Task<Incident?> ProcessParsedMessageAsync(ParsedMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            // ===== ОТБОЙ БЕЗ НП =====
            if (message.Settlements.Count == 0 && message.Status == "Terminated")
            {
                await RefreshCacheAsync();
                var activeNow = _activeCache.Values
                    .Where(i => i.Status != IncidentStatus.Terminated && i.Status != IncidentStatus.Expired)
                    .ToList();
                // Закрываем все активные инциденты, которые были обновлены за последние 2 часа
                foreach (var inc in activeNow)
                {
                    if ((DateTime.UtcNow - inc.LastSeen).TotalHours < 2)
                    {
                        inc.Status = IncidentStatus.Terminated;
                        inc.Notes += " Закрыт по отбою без НП.";
                        await _db.UpdateIncidentAsync(inc);
                        lock (_cacheLock) _activeCache.Remove(inc.Id);
                        GraphLogger.Log($"Инц. #{inc.Id} закрыт по отбою без НП");
                    }
                }
                return null;
            }

            // ===== ОТБОЙ С НП =====
            if (message.Settlements.Count > 0 && message.Status == "Terminated")
            {
                await RefreshCacheAsync();
                // Закрываем все активные инциденты, которые содержат хотя бы один из указанных НП
                var toClose = _activeCache.Values
                    .Where(i => i.Status != IncidentStatus.Terminated && i.Status != IncidentStatus.Expired)
                    .Where(i => i.AffectedSettlements.Any(settlement => 
                        message.Settlements.Any(mS => string.Equals(settlement, mS.Name, StringComparison.OrdinalIgnoreCase))))
                    .ToList();

                foreach (var inc in toClose)
                {
                    inc.Status = IncidentStatus.Terminated;
                    inc.Notes += $" Закрыт по отбою с НП ({string.Join(", ", message.Settlements.Select(s => s.Name))})";
                    await _db.UpdateIncidentAsync(inc);
                    lock (_cacheLock) _activeCache.Remove(inc.Id);
                    GraphLogger.Log($"Инц. #{inc.Id} закрыт по отбою с НП");
                }
                return toClose.FirstOrDefault();
            }

            // ===== ОБЫЧНОЕ СООБЩЕНИЕ С НП =====
            if (message.Settlements.Count == 0)
                return null;

            await RefreshCacheAsync();

            Incident? matched = null;

            lock (_cacheLock)
            {
                // Определяем порог расстояния в зависимости от типа угрозы
                double distanceThreshold = GetDistanceThreshold(message.ThreatType);
                // Ищем подходящий инцидент
                foreach (var inc in _activeCache.Values)
                {
                    // Не объединяем с завершёнными
                    if (inc.Status == IncidentStatus.Terminated || inc.Status == IncidentStatus.Expired)
                        continue;

                    // Тип должен совпадать (или быть Unknown у нового)
                    if (!string.IsNullOrEmpty(message.ThreatType) && message.ThreatType != "Unknown" &&
                        !string.Equals(inc.ThreatType, message.ThreatType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Время с последнего обновления не должно быть слишком большим
                    double maxIdleHours = GetMaxIdleHours(message.ThreatType);
                    if ((DateTime.UtcNow - inc.LastSeen).TotalHours > maxIdleHours)
                        continue;

                    // Проверка расстояния между последней точкой инцидента и первой точкой нового сообщения
                    if (inc.Points.Count > 0 && message.Settlements.Count > 0)
                    {
                        var lastIncidentPoint = inc.Points.Last();
                        var firstNewPoint = message.Settlements.First();
                        double dist = _geo.CalculateDistance(
                            lastIncidentPoint.Lat, lastIncidentPoint.Lon,
                            firstNewPoint.Lat, firstNewPoint.Lon);
                        if (dist > distanceThreshold)
                            continue;
                    }

                    matched = inc;
                    break;
                }
            }

            // Если не нашли – создаём новый
            if (matched == null)
            {
                matched = new Incident
                {
                    ThreatType = message.ThreatType,
                    Category = message.Category,
                    FirstSeen = message.ReceivedAt,
                    LastSeen = message.ReceivedAt,
                    Status = IncidentStatus.Active,
                    Confidence = message.Confidence,
                    Points = new List<IncidentPoint>(),
                    AffectedSettlements = new List<string>(),
                    IsReconCompleted = false,
                    Notes = ""
                };
            }

            // Добавляем точки, избегая дубликатов
            foreach (var s in message.Settlements)
            {
                if (s == null || string.IsNullOrEmpty(s.Name)) continue;
                // Проверка на дубликат по названию и координатам
                bool alreadyExists = matched.Points.Any(p =>
                    string.Equals(p.SettlementName, s.Name, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(p.Lat - s.Lat) < 0.0001 &&
                    Math.Abs(p.Lon - s.Lon) < 0.0001);
                if (alreadyExists) continue;

                matched.Points.Add(new IncidentPoint
                {
                    SettlementName = s.Name,
                    Lat = s.Lat,
                    Lon = s.Lon,
                    Time = message.ReceivedAt
                });
                if (!matched.AffectedSettlements.Contains(s.Name))
                    matched.AffectedSettlements.Add(s.Name);
            }

            matched.LastSeen = message.ReceivedAt;

            if (message.Status == "Watch")
            {
                matched.Status = IncidentStatus.Watch;
                matched.IsReconCompleted = true;
                matched.ReconTime = message.ReceivedAt;
                matched.Confidence += _heuristics.WatchConfidenceBoost;
                matched.Notes += " Режим внимания (разведка завершена).";
            }
            else if (message.Flags != null && message.Flags.Contains("Destroyed"))
            {
                matched.Status = IncidentStatus.Terminated;
                matched.Notes += " Уничтожен.";
            }

            if (message.Settlements.Count > 0)
                matched.Confidence = Math.Min(1.0, matched.Confidence + 0.1);

            if (matched.Id == 0)
            {
                await _db.SaveIncidentAsync(matched);
                lock (_cacheLock)
                    _activeCache[matched.Id] = matched;
                var firstPoint = matched.Points.FirstOrDefault();
                if (firstPoint != null)
                    GraphLogger.LogIncidentCreated(matched.Id, matched.ThreatType, firstPoint.SettlementName);
            }
            else
            {
                await _db.UpdateIncidentAsync(matched);
                lock (_cacheLock)
                    _activeCache[matched.Id] = matched;
                GraphLogger.LogIncidentUpdated(matched.Id, matched.ThreatType, matched.Points.Count);
            }

            if (matched.Status == IncidentStatus.Terminated || matched.Status == IncidentStatus.Expired)
            {
                lock (_cacheLock)
                    _activeCache.Remove(matched.Id);
            }

            return matched;
        }

        public async Task<IEnumerable<Incident>> GetActiveIncidentsAsync()
        {
            await RefreshCacheAsync();
            lock (_cacheLock)
                return _activeCache.Values.ToList();
        }

        public async Task CloseIncidentAsync(int incidentId, string reason)
        {
            var active = await _db.GetActiveIncidentsAsync();
            var target = active.FirstOrDefault(i => i.Id == incidentId);
            if (target != null)
            {
                target.Status = IncidentStatus.Terminated;
                target.Notes += $" Закрыт: {reason}";
                await _db.UpdateIncidentAsync(target);
                lock (_cacheLock)
                    _activeCache.Remove(incidentId);
            }
        }

        public async Task UpdateIncidentAsync(Incident incident)
        {
            await _db.UpdateIncidentAsync(incident);
            lock (_cacheLock)
                _activeCache[incident.Id] = incident;
        }

        public async Task ExpireOldIncidentsAsync()
        {
            await RefreshCacheAsync();
            var now = DateTime.UtcNow;
            var expired = new List<Incident>();

            lock (_cacheLock)
            {
                foreach (var inc in _activeCache.Values)
                {
                    if (inc.Status == IncidentStatus.Terminated || inc.Status == IncidentStatus.Expired)
                        continue;

                    // Время жизни в зависимости от типа
                    double maxLifetimeHours;
                    if (inc.ThreatType == "FPV")
                        maxLifetimeHours = _heuristics.FpvLifetimeMinutes / 60.0;
                    else
                        maxLifetimeHours = _heuristics.DefaultLifetimeHours;

                    if ((now - inc.LastSeen).TotalHours > maxLifetimeHours)
                        expired.Add(inc);
                }
            }

            foreach (var inc in expired)
            {
                inc.Status = IncidentStatus.Expired;
                inc.Notes += $" Закрыт по сроку ({now:yyyy-MM-dd HH:mm})";
                await _db.UpdateIncidentAsync(inc);
                lock (_cacheLock)
                    _activeCache.Remove(inc.Id);
                GraphLogger.Log($"Инц. #{inc.Id} закрыт по сроку (TTL истёк)");
            }

            if (expired.Count > 0)
                Console.WriteLine($"🧠 Закрыто по сроку {expired.Count} инцидентов.");
        }

        private double GetDistanceThreshold(string threatType)
        {
            // Если тип FPV – маленький порог (скорость низкая, но перемещается)
            if (threatType == "FPV")
                return 10.0; // 10 км
            if (threatType == "Rocket" || threatType == "Missile")
                return 50.0;
            if (threatType == "Unknown")
                return 20.0;
            // Для остальных дронов (Hornet, Dart и т.д.)
            return 15.0;
        }

        private double GetMaxIdleHours(string threatType)
        {
            // Максимальное время без сообщений для объединения
            if (threatType == "FPV")
                return 0.5; // 30 минут
            if (threatType == "Rocket")
                return 1.5; // 90 минут
            return 1.0; // 60 минут для остальных
        }
    }
}