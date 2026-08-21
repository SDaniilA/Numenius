using System;
using System.Threading;
using System.Threading.Tasks;
using Numenius.Core.Models;

namespace Numenius.Core.Interfaces
{
    /// <summary>
    /// Источник сообщений (перехватчик)
    /// </summary>
    public interface IMessageSource
    {
        event EventHandler<RawMessage> OnMessageReceived;
        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync();
    }
}