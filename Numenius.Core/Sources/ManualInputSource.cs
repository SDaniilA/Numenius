using System;
using System.Threading;
using System.Threading.Tasks;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;

namespace Numenius.Core.Sources
{
    /// <summary>
    /// Источник ручного ввода сообщений из консоли
    /// </summary>
    public class ManualInputSource : IMessageSource
    {
        private readonly ManualInputConfig _config;
        private CancellationTokenSource? _cts;
        private Task? _inputTask;
        private bool _isRunning;

        public event EventHandler<RawMessage>? OnMessageReceived;

        public ManualInputSource(ManualInputConfig config)
        {
            _config = config ?? new ManualInputConfig();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _isRunning = true;
            _inputTask = Task.Run(() => InputLoop(_cts.Token), _cts.Token);
            Console.WriteLine("🖊️ Ручной ввод включён. Введите сообщение и нажмите Enter (или 'exit' для выхода).");
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            _isRunning = false;
            _cts?.Cancel();
            if (_inputTask != null)
                await _inputTask;
            Console.WriteLine("🛑 Ручной ввод остановлен.");
        }

        private void InputLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _isRunning)
            {
                try
                {
                    string? line = Console.ReadLine();
                    if (line == null) continue;
                    if (line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                        break;

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var raw = new RawMessage
                    {
                        Id = $"manual_{DateTime.UtcNow.Ticks}",
                        SourceType = "Manual",
                        Sender = _config.DefaultSender ?? "Ручной ввод",
                        Text = line,
                        ReceivedAt = DateTime.UtcNow,
                        Priority = Priority.LowPriority // по умолчанию, можно настроить
                    };

                    OnMessageReceived?.Invoke(this, raw);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Ошибка ввода: {ex.Message}");
                }
            }
        }
    }

    public class ManualInputConfig
    {
        public string? DefaultSender { get; set; } = "Ручной ввод";
    }
}