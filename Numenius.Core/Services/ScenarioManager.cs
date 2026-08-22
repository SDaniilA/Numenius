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
        private readonly ZoneService _zoneService;
        private readonly Dictionary<int, Incident> _activeCache = new();
        private DateTime _cacheValidUntil = DateTime.MinValue;
        private readonly object _cacheLock = new();

        public ScenarioManager(IDatabaseService db, IGeoService geo, HeuristicsConfig heuristics, ZoneService zoneService)
        {
            _db = db;
            _geo = geo;
            _heuristics = heuristics;
            _zoneService = zoneService ?? throw new ArgumentNullException(nameof(zoneService));
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
                foreach (var inc in activeNow)
                {
                    if ((DateTime.UtcNow - inc.LastSeen).TotalHours < 2)
                    {
                        inc.Status = IncidentStatus.Terminated;
                        inc.Notes += " Закрыт по отбою без НП.";
                        await _db.UpdateIncidentAsync(inc);
                        lock (_cacheLock) _activeCache.Remove(inc.Id);
                        _zoneService.RemoveZoneForIncident(inc.Id);
                        GraphLogger.Log($"Инц. #{inc.Id} закрыт по отбою без НП");
                    }
                }
                return null;
            }

            // ===== ОТБОЙ С НП =====
            if (message.Settlements.Count > 0 && message.Status == "Terminated")
            {
                await RefreshCacheAsync();
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
                    _zoneService.RemoveZoneForIncident(inc.Id);
                    GraphLogger.Log($"Инц. #{inc.Id} закрыт по отбою с НП");
                }
                return toClose.FirstOrDefault();
            }

            // ===== ОБЫЧНОЕ СООБЩЕНИЕ С НП =====
            if (message.Settlements.Count == 0)
                return null;

            await RefreshCacheAsync();

            var newZone = _zoneService.CreateZone(message.Settlements, message.ThreatType);

            Incident? matched = null;

            lock (_cacheLock)
            {
                foreach (var inc in _activeCache.Values)
                {
                    if (inc.Status == IncidentStatus.Terminated || inc.Status == IncidentStatus.Expired)
                        continue;

                    if (!string.IsNullOrEmpty(message.ThreatType) && message.ThreatType != "Unknown" &&
                        !string.Equals(inc.ThreatType, message.ThreatType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var incZone = _zoneService.GetOrCreateZoneForIncident(inc, inc.Points.Select(p => new Settlement
                    {
                        Name = p.SettlementName,
                        Lat = p.Lat,
                        Lon = p.Lon
                    }).ToList(), inc.ThreatType);

                    if (_zoneService.ZonesIntersect(newZone, incZone))
                    {
                        matched = inc;
                        break;
                    }
                }
            }

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

            foreach (var s in message.Settlements)
            {
                if (s == null || string.IsNullOrEmpty(s.Name)) continue;
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
                _zoneService.GetOrCreateZoneForIncident(matched, message.Settlements, matched.ThreatType);
                var firstPoint = matched.Points.FirstOrDefault();
                if (firstPoint != null)
                    GraphLogger.LogIncidentCreated(matched.Id, matched.ThreatType, firstPoint.SettlementName);
            }
            else
            {
                await _db.UpdateIncidentAsync(matched);
                lock (_cacheLock)
                    _activeCache[matched.Id] = matched;
                _zoneService.GetOrCreateZoneForIncident(matched, message.Settlements, matched.ThreatType);
                GraphLogger.LogIncidentUpdated(matched.Id, matched.ThreatType, matched.Points.Count);
            }

            if (matched.Status == IncidentStatus.Terminated || matched.Status == IncidentStatus.Expired)
            {
                lock (_cacheLock)
                    _activeCache.Remove(matched.Id);
                _zoneService.RemoveZoneForIncident(matched.Id);
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
                _zoneService.RemoveZoneForIncident(incidentId);
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
                _zoneService.RemoveZoneForIncident(inc.Id);
                GraphLogger.Log($"Инц. #{inc.Id} закрыт по сроку (TTL истёк)");
            }

            if (expired.Count > 0)
                Console.WriteLine($"🧠 Закрыто по сроку {expired.Count} инцидентов.");
        }
    }
}