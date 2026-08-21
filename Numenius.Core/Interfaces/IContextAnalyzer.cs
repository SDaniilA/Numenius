using Numenius.Core.Models;

namespace Numenius.Core.Interfaces
{
    /// <summary>
    /// Анализатор контекста: хранит историю сообщений и определяет, относится ли новое сообщение
    /// к какому-либо предыдущему (например, уточняет направление или населённый пункт).
    /// </summary>
    public interface IContextAnalyzer
    {
        // Добавляет сообщение в историю для последующего анализа.
        void AddMessage(ParsedMessage message);

        // Ищет подходящий контекст для текущего сообщения (если оно не содержит топонимов, но содержит тип угрозы и т.д.).
        ParsedMessage? FindContext(ParsedMessage current, string rawText);
    }
}