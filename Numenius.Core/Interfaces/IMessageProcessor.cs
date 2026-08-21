using System.Threading;
using System.Threading.Tasks;
using Numenius.Core.Models;

namespace Numenius.Core.Interfaces
{
    public interface IMessageProcessor
    {
        Task<ParsedMessage?> ProcessAsync(RawMessage rawMessage, CancellationToken cancellationToken);
    }
}