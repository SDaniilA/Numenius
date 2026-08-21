using System.Threading.Tasks;
using Numenius.Core.Models;

namespace Numenius.Core.Interfaces
{
    public interface IPredictor
    {
        string Name { get; }
        Task<Prediction?> GeneratePredictionAsync(Incident incident);
    }
}