using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Numenius.Core.Models;

namespace Numenius.Core.Queue
{
    /// <summary>
    /// Очередь сообщений с приоритетами (0 – высший, 5 – низший)
    /// </summary>
    public class PriorityMessageQueue : IDisposable
    {
        private readonly ConcurrentQueue<RawMessage>[] _queues;
        private readonly SemaphoreSlim _semaphore = new(0);
        private bool _disposed;

        public PriorityMessageQueue()
        {
            _queues = new ConcurrentQueue<RawMessage>[6];
            for (int i = 0; i < 6; i++)
                _queues[i] = new ConcurrentQueue<RawMessage>();
        }

        public void Enqueue(RawMessage message)
        {
            if (message.Priority < 0) message.Priority = 0;
            if (message.Priority > 5) message.Priority = 5;
            _queues[message.Priority].Enqueue(message);
            _semaphore.Release();
        }

        public async Task<RawMessage> DequeueAsync(CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _semaphore.WaitAsync(cancellationToken);
                for (int p = 0; p < 6; p++)
                {
                    if (_queues[p].TryDequeue(out var msg))
                        return msg;
                }
            }
            throw new OperationCanceledException(cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _semaphore.Dispose();
        }
    }
}