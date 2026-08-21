using System.Threading.Tasks;
using Numenius.Core.Models;

namespace Numenius.Core.Interfaces
{
    /// <summary>
    /// Модуль вывода (TTS, MQTT, консоль, файл)
    /// </summary>
    public interface IOutputModule
    {
        Task InitializeAsync();
        Task HandleParsedMessageAsync(ParsedMessage message);
        Task HandlePredictionAsync(Prediction prediction);
    }
}