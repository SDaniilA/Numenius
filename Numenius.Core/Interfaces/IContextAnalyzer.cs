using Numenius.Core.Models;

namespace Numenius.Core.Interfaces
{
    public interface IContextAnalyzer
    {
        void AddMessage(ParsedMessage message);
        ParsedMessage? FindContext(ParsedMessage current, string rawText);
        ParsedMessage? FindMessageById(string id); // добавленный метод
    }
}