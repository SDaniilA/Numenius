using System.Collections.Generic;
using System.Threading.Tasks;
using Numenius.Core.Models;

namespace Numenius.Core.Interfaces
{
    public interface IScenarioManager
    {
        Task<Incident?> ProcessParsedMessageAsync(ParsedMessage message);
        Task<IEnumerable<Incident>> GetActiveIncidentsAsync();
        Task CloseIncidentAsync(int incidentId, string reason);
        Task UpdateIncidentAsync(Incident incident);
    }
}