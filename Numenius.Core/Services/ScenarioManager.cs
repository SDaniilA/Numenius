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
                Incident? lastIncident = null;
                lock (_cacheLock)
                {
                    lastIncident = _activeCache.Values
                        .Where(i => i.ThreatType == message.ThreatType || message.ThreatType == "Unknown")
                        .OrderByDescending(i => i.LastSeen)
                        .FirstOrDefault();
                }
                if (lastIncident != null && lastIncident.Status != IncidentStatus.Terminated && lastIncident.Status != IncidentStatus.Expired)
                {
                    lastIncident.Status = IncidentStatus.Terminated;
                    lastIncident.Notes += $" Закрыт по отбою без гео ({message.Sender})";
                    await _db.UpdateIncidentAsync(lastIncident);
                    lock (_cacheLock)
                        _activeCache.Remove(lastIncident.Id);
                    GraphLogger.Log($"Инц. #{lastIncident.Id} закрыт по отбою без НП");
                    return lastIncident;
                }
                return null;
            }

            // ===== ОТБОЙ С НП =====
            if (message.Settlements.Count > 0 && message.Status == "Terminated")
            {
                await RefreshCacheAsync();
                Incident? matchedIncident = null;
                lock (_cacheLock)
                {
                    foreach (var inc in _activeCache.Values)
                    {
                        if (inc.ThreatType != message.ThreatType && message.ThreatType != "Unknown")
                            continue;
                        var common = inc.AffectedSettlements.Intersect(message.Settlements.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
                        if (common.Any())
                        {
                            matchedIncident = inc;
                            break;
                        }
                    }
                    if (matchedIncident == null && message.Settlements.Count > 0)
                    {
                        var first = message.Settlements.First();
                        double bestDist = double.MaxValue;
                        foreach (var inc in _activeCache.Values)
                        {
                            if (inc.ThreatType != message.ThreatType && message.ThreatType != "Unknown")
                                continue;
                            if (inc.Points.Count == 0) continue;
                            var lastPoint = inc.Points.Last();
                            double dist = _geo.CalculateDistance(lastPoint.Lat, lastPoint.Lon, first.Lat, first.Lon);
                            if (dist < bestDist && dist < 50)
                            {
                                bestDist = dist;
                                matchedIncident = inc;
                            }
                        }
                    }
                }
                if (matchedIncident != null && matchedIncident.Status != IncidentStatus.Terminated && matchedIncident.Status != IncidentStatus.Expired)
                {
                    matchedIncident.Status = IncidentStatus.Terminated;
                    matchedIncident.Notes += $" Закрыт по отбою с НП ({message.Sender})";
                    await _db.UpdateIncidentAsync(matchedIncident);
                    lock (_cacheLock)
                        _activeCache.Remove(matchedIncident.Id);
                    GraphLogger.Log($"Инц. #{matchedIncident.Id} закрыт по отбою с НП");
                    return matchedIncident;
                }
                return null;
            }

            // ===== ОБЫЧНОЕ СООБЩЕНИЕ С НП =====
            if (message.Settlements.Count == 0)
                return null;

            await RefreshCacheAsync();

            Incident? matched = null;

            lock (_cacheLock)
            {
                foreach (var inc in _activeCache.Values)
                {
                    if (inc.ThreatType != message.ThreatType && message.ThreatType != "Unknown")
                        continue;

                    double maxLifetimeHours;
                    if (inc.ThreatType == "FPV")
                        maxLifetimeHours = _heuristics.FpvLifetimeMinutes / 60.0;
                    else
                        maxLifetimeHours = _heuristics.DefaultLifetimeHours;

                    if ((DateTime.UtcNow - inc.LastSeen).TotalHours > maxLifetimeHours)
                        continue;

                    if (inc.Points.Count > 0 && message.Settlements.Count > 0)
                    {
                        var last = inc.Points.Last();
                        var first = message.Settlements.First();
                        if (first != null)
                        {
                            double dist = _geo.CalculateDistance(last.Lat, last.Lon, first.Lat, first.Lon);
                            if (dist > 50)
                                continue;
                        }
                    }
                    matched = inc;
                    break;
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
                if (Math.Abs(s.Lat) < 0.0001 && Math.Abs(s.Lon) < 0.0001) continue;

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
            var inc = await _db.GetActiveIncidentsAsync();
            var target = inc.FirstOrDefault(i => i.Id == incidentId);
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
    }
}