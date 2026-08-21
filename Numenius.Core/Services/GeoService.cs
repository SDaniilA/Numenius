using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;

namespace Numenius.Core.Services
{
    public class GeoService : IGeoService
    {
        private readonly IDatabaseService _db;
        private readonly Dictionary<string, Settlement> _settlements = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _settlementsFilePath;

        public GeoService(IDatabaseService db, string settlementsFilePath = "appsettings/settlements.json")
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _settlementsFilePath = settlementsFilePath;
            LoadSettlementsFromDb().GetAwaiter().GetResult();
        }

        private async Task LoadSettlementsFromDb()
        {
            try
            {
                var dbSettlements = await _db.GetAllSettlementsAsync();
                foreach (var s in dbSettlements)
                {
                    if (!_settlements.ContainsKey(s.Name))
                        _settlements[s.Name] = s;
                }
                Console.WriteLine($"✅ Загружено {_settlements.Count} населённых пунктов из БД.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Ошибка загрузки НП из БД: {ex.Message}");
                LoadSettlementsFromJson();
            }
        }

        private void LoadSettlementsFromJson()
        {
            if (!File.Exists(_settlementsFilePath))
            {
                Console.WriteLine($"⚠️ Файл координат не найден: {_settlementsFilePath}");
                return;
            }
            try
            {
                var json = File.ReadAllText(_settlementsFilePath);
                var list = JsonConvert.DeserializeObject<List<Settlement>>(json);
                if (list != null)
                {
                    foreach (var s in list)
                    {
                        if (!_settlements.ContainsKey(s.Name))
                            _settlements[s.Name] = s;
                    }
                    Console.WriteLine($"✅ Загружено {_settlements.Count} НП из JSON (резерв).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка загрузки координат из JSON: {ex.Message}");
            }
        }

        public Settlement? FindSettlement(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            _settlements.TryGetValue(name, out var result);
            return result;
        }

        public List<Settlement> FindAllSettlements(string text)
        {
            var result = new List<Settlement>();
            var words = text.Split(new[] { ' ', ',', ';', '-', '–', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                if (word.Length < 3) continue;
                var s = FindSettlement(word);
                if (s != null && !result.Any(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase)))
                    result.Add(s);
            }
            return result;
        }

        public async Task<Settlement?> FindOrRequestSettlement(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            // 1. Кэш сессии
            if (SettlementCache.Get(name) is Settlement cached)
                return cached;

            // 2. Локальный словарь
            var local = FindSettlement(name);
            if (local != null)
            {
                SettlementCache.Add(name, local);
                return local;
            }

            // 3. Ручной ввод координат
            return await ManualCoordinateEntry(name);
        }

		private async Task<Settlement?> ManualCoordinateEntry(string name)
		{
			// Нормализуем название (удаляем лишние пробелы)
			name = name?.Trim();
			if (string.IsNullOrEmpty(name))
			{
				Console.WriteLine("⚠️ Название НП не может быть пустым.");
				return null;
			}

			Console.WriteLine($"❗ НП \"{name}\" не найден в базе.");
			Console.WriteLine("   Введите координаты в формате: широта,долгота (например: 50.1880,38.0187)");
			Console.Write("   или нажмите Enter, чтобы пропустить: ");
			string input = Console.ReadLine()?.Trim();
			if (string.IsNullOrEmpty(input))
			{
				Console.WriteLine($"ℹ️ НП \"{name}\" пропущен.");
				SettlementCache.Add(name, new Settlement { Name = name, Lat = 0, Lon = 0 });
				return null;
			}

			var parts = input.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 2)
			{
				Console.WriteLine("❌ Неверный формат. Ожидается: широта,долгота");
				return null;
			}

			if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double lat) ||
				!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double lon))
			{
				Console.WriteLine("❌ Не удалось распознать координаты.");
				return null;
			}

			if (Math.Abs(lat) < 0.0001 && Math.Abs(lon) < 0.0001)
			{
				Console.WriteLine("⚠️ Координаты не могут быть нулевыми. Пропускаем.");
				return null;
			}

			var settlement = new Settlement { Name = name, Lat = lat, Lon = lon, NeedsReview = false };
			_settlements[name] = settlement;
			await _db.SaveSettlementAsync(settlement);
			SettlementCache.Add(name, settlement);
			Console.WriteLine($"✅ НП \"{name}\" добавлен в БД с координатами ({lat:F4}, {lon:F4}).");
			return settlement;
		}

        public double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371;
            double dLat = (lat2 - lat1) * Math.PI / 180;
            double dLon = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        public string BuildZoneGeoJson(IEnumerable<IncidentPoint> points, double widthKm)
        {
            if (points == null || !points.Any()) return "{}";
            var list = points.ToList();
            double minLat = list.Min(p => p.Lat);
            double maxLat = list.Max(p => p.Lat);
            double minLon = list.Min(p => p.Lon);
            double maxLon = list.Max(p => p.Lon);

            double delta = widthKm / 111.0 / 2;
            minLat -= delta; maxLat += delta;
            minLon -= delta; maxLon += delta;

            var coords = new[]
            {
                new[] { minLon, minLat },
                new[] { maxLon, minLat },
                new[] { maxLon, maxLat },
                new[] { minLon, maxLat },
                new[] { minLon, minLat }
            };

            return $"{{\"type\":\"Polygon\",\"coordinates\":[{JsonConvert.SerializeObject(coords)}]}}";
        }

        public List<Settlement> GetSettlementsInZone(string geoJsonPolygon)
        {
            return _settlements.Values.ToList();
        }

        public IEnumerable<string> GetAllSettlementNames()
        {
            return _settlements.Keys;
        }
    }
}