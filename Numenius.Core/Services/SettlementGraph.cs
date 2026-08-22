using System;
using System.Collections.Generic;
using System.Linq;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;

namespace Numenius.Core.Services
{
    public class SettlementGraph
    {
        private readonly IGeoService _geo;
        private readonly List<Settlement> _settlements;
        private readonly Dictionary<string, int> _nameIndex;
        private readonly double[,] _distanceMatrix;
        private readonly double[,] _azimuthMatrix;

        public SettlementGraph(IGeoService geo, IEnumerable<Settlement> settlements)
        {
            _geo = geo ?? throw new ArgumentNullException(nameof(geo));
            _settlements = settlements?.ToList() ?? new List<Settlement>();
            _nameIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            for (int i = 0; i < _settlements.Count; i++)
            {
                _nameIndex[_settlements[i].Name] = i;
            }

            _distanceMatrix = new double[_settlements.Count, _settlements.Count];
            _azimuthMatrix = new double[_settlements.Count, _settlements.Count];

            BuildMatrix();
        }

        private void BuildMatrix()
        {
            for (int i = 0; i < _settlements.Count; i++)
            {
                for (int j = 0; j < _settlements.Count; j++)
                {
                    if (i == j)
                    {
                        _distanceMatrix[i, j] = 0;
                        _azimuthMatrix[i, j] = 0;
                        continue;
                    }

                    var a = _settlements[i];
                    var b = _settlements[j];
                    _distanceMatrix[i, j] = _geo.CalculateDistance(a.Lat, a.Lon, b.Lat, b.Lon);
                    _azimuthMatrix[i, j] = CalculateAzimuth(a.Lat, a.Lon, b.Lat, b.Lon);
                }
            }
        }

        // ========== Базовые методы ==========

        public double GetDistance(string fromName, string toName)
        {
            if (!_nameIndex.TryGetValue(fromName, out int fromIdx) || !_nameIndex.TryGetValue(toName, out int toIdx))
                return double.MaxValue;
            return _distanceMatrix[fromIdx, toIdx];
        }

        public double GetDistance(int fromIdx, int toIdx)
        {
            if (fromIdx < 0 || fromIdx >= _settlements.Count || toIdx < 0 || toIdx >= _settlements.Count)
                return double.MaxValue;
            return _distanceMatrix[fromIdx, toIdx];
        }

        public double GetDistance(double lat1, double lon1, double lat2, double lon2)
        {
            return _geo.CalculateDistance(lat1, lon1, lat2, lon2);
        }

        public double GetAzimuth(string fromName, string toName)
        {
            if (!_nameIndex.TryGetValue(fromName, out int fromIdx) || !_nameIndex.TryGetValue(toName, out int toIdx))
                return 0;
            return _azimuthMatrix[fromIdx, toIdx];
        }

        public double GetAzimuth(int fromIdx, int toIdx)
        {
            if (fromIdx < 0 || fromIdx >= _settlements.Count || toIdx < 0 || toIdx >= _settlements.Count)
                return 0;
            return _azimuthMatrix[fromIdx, toIdx];
        }

        public double GetAzimuth(double lat1, double lon1, double lat2, double lon2)
        {
            return CalculateAzimuth(lat1, lon1, lat2, lon2);
        }

        // ========== Доступ к спискам ==========

        public List<Settlement> GetAllSettlements()
        {
            return _settlements.ToList();
        }

        public List<string> GetSettlementNames()
        {
            return _settlements.Select(s => s.Name).ToList();
        }

        public Settlement? GetSettlement(string name)
        {
            if (_nameIndex.TryGetValue(name, out int idx))
                return _settlements[idx];
            return null;
        }

        public Settlement? GetSettlement(int idx)
        {
            if (idx < 0 || idx >= _settlements.Count)
                return null;
            return _settlements[idx];
        }

        // ========== Секторные методы ==========

        public List<Settlement> GetSettlementsInSector(double originLat, double originLon, double sectorStartAzimuth, double sectorEndAzimuth)
        {
            var result = new List<Settlement>();
            for (int i = 0; i < _settlements.Count; i++)
            {
                var s = _settlements[i];
                double dist = _geo.CalculateDistance(originLat, originLon, s.Lat, s.Lon);
                if (dist < 0.1) continue; // пропускаем саму точку

                double az = CalculateAzimuth(originLat, originLon, s.Lat, s.Lon);
                if (IsAngleInSector(az, sectorStartAzimuth, sectorEndAzimuth))
                    result.Add(s);
            }
            return result;
        }

        public List<Settlement> GetSettlementsInSector(int originIdx, double sectorStartAzimuth, double sectorEndAzimuth)
        {
            if (originIdx < 0 || originIdx >= _settlements.Count)
                return new List<Settlement>();
            return GetSettlementsInSector(_settlements[originIdx].Lat, _settlements[originIdx].Lon, sectorStartAzimuth, sectorEndAzimuth);
        }

        public List<Settlement> GetNearestSettlementsInSector(double originLat, double originLon, double sectorStartAzimuth, double sectorEndAzimuth, int count)
        {
            var inSector = GetSettlementsInSector(originLat, originLon, sectorStartAzimuth, sectorEndAzimuth);
            return inSector
                .OrderBy(s => _geo.CalculateDistance(originLat, originLon, s.Lat, s.Lon))
                .Take(count)
                .ToList();
        }

        public List<Settlement> GetNearestSettlementsInSector(int originIdx, double sectorStartAzimuth, double sectorEndAzimuth, int count)
        {
            if (originIdx < 0 || originIdx >= _settlements.Count)
                return new List<Settlement>();
            return GetNearestSettlementsInSector(_settlements[originIdx].Lat, _settlements[originIdx].Lon, sectorStartAzimuth, sectorEndAzimuth, count);
        }

        public List<(Settlement Settlement, double Distance, double Azimuth)> GetNearestWithAzimuth(double originLat, double originLon, int count = 5)
        {
            var result = new List<(Settlement, double, double)>();
            for (int i = 0; i < _settlements.Count; i++)
            {
                var s = _settlements[i];
                double dist = _geo.CalculateDistance(originLat, originLon, s.Lat, s.Lon);
                double az = CalculateAzimuth(originLat, originLon, s.Lat, s.Lon);
                result.Add((s, dist, az));
            }
            return result
                .OrderBy(x => x.Item2)
                .Take(count)
                .ToList();
        }

        public List<(Settlement Settlement, double Distance, double Azimuth)> GetNearestWithAzimuth(int originIdx, int count = 5)
        {
            if (originIdx < 0 || originIdx >= _settlements.Count)
                return new List<(Settlement, double, double)>();
            return GetNearestWithAzimuth(_settlements[originIdx].Lat, _settlements[originIdx].Lon, count);
        }

        // ========== Вспомогательные методы ==========

        private double CalculateAzimuth(double lat1, double lon1, double lat2, double lon2)
        {
            double dLon = (lon2 - lon1) * Math.PI / 180;
            double lat1Rad = lat1 * Math.PI / 180;
            double lat2Rad = lat2 * Math.PI / 180;

            double x = Math.Sin(dLon) * Math.Cos(lat2Rad);
            double y = Math.Cos(lat1Rad) * Math.Sin(lat2Rad) - Math.Sin(lat1Rad) * Math.Cos(lat2Rad) * Math.Cos(dLon);

            double bearing = Math.Atan2(x, y) * 180 / Math.PI;
            return (bearing + 360) % 360;
        }

        private bool IsAngleInSector(double angle, double start, double end)
        {
            angle = NormalizeAngle(angle);
            start = NormalizeAngle(start);
            end = NormalizeAngle(end);

            if (start <= end)
                return angle >= start && angle <= end;
            else
                return angle >= start || angle <= end;
        }

        private double NormalizeAngle(double angle)
        {
            angle = angle % 360;
            if (angle < 0) angle += 360;
            return angle;
        }
    }
}