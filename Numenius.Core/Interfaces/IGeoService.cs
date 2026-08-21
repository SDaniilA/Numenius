using System.Collections.Generic;
using System.Threading.Tasks;
using Numenius.Core.Models;

namespace Numenius.Core.Interfaces
{
    public interface IGeoService
    {
        Settlement? FindSettlement(string name);
        List<Settlement> FindAllSettlements(string text);
        Task<Settlement?> FindOrRequestSettlement(string name); // НОВЫЙ МЕТОД
        double CalculateDistance(double lat1, double lon1, double lat2, double lon2);
        string BuildZoneGeoJson(IEnumerable<IncidentPoint> points, double widthKm);
        List<Settlement> GetSettlementsInZone(string geoJsonPolygon);
        IEnumerable<string> GetAllSettlementNames();
    }
}