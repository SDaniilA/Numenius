using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Numenius.Core.Models;

namespace Numenius.Core.Interfaces
{
    public interface IDatabaseService
    {
        Task InitializeAsync();

        // Сообщения
        Task SaveRawMessageAsync(RawMessage raw);
        Task SaveParsedMessageAsync(ParsedMessage parsed);

        // Инциденты
        Task<int> SaveIncidentAsync(Incident incident);
        Task UpdateIncidentAsync(Incident incident);
        Task<IEnumerable<Incident>> GetActiveIncidentsAsync();
        Task<IEnumerable<Incident>> GetAllIncidentsAsync(int maxAgeDays);
        Task<IEnumerable<Incident>> GetIncidentsForPeriodAsync(DateTime start, DateTime end);
        Task CloseOldIncidentsAsync(); // <-- добавлен

        // Прогнозы
        Task SavePredictionAsync(Prediction prediction, string predictorType = "Graph");
        Task<IEnumerable<Prediction>> GetPredictionsForIncidentAsync(int incidentId);
        Task<IEnumerable<Prediction>> GetPredictionsForPeriodAsync(DateTime start, DateTime end);

        // Источники
        Task<double> GetSourceWeightAsync(string name);
        Task InitializeSourceAsync(string name, double initialWeight);
        Task UpdateSourceStatsAsync(string name, bool confirmed);
        Task<Dictionary<string, double>> GetAllSourceWeightsAsync();
        Task ResetSourceWeightAsync(string name, double newWeight, string reason);

        // Координаты
        Task SaveSettlementAsync(Settlement settlement);
        Task<IEnumerable<Settlement>> GetAllSettlementsAsync();
    }
}