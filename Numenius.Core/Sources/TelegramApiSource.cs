using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TL;
using WTelegram;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;
using Numenius.Core.Config;

namespace Numenius.Core.Sources
{
    public class TelegramApiSource : IMessageSource, IDisposable
    {
        private readonly TelegramApiConfig _config;
        private Client? _client;
        private CancellationTokenSource? _cts;
        private Task? _pollTask;
        private readonly string _sourceType = "TelegramApi";
        private bool _disposed;

        public event EventHandler<RawMessage>? OnMessageReceived;

        public TelegramApiSource(TelegramApiConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                _client = new Client(_config.ApiId, _config.ApiHash);
                _client.OnUpdate += OnUpdate;
                _client.OnLogin += OnLogin;
                _client.SessionPath = _config.SessionPath;

                // Подключение
                await _client.ConnectAsync();
                if (_client.User == null)
                {
                    await _client.Login(_config.Phone);
                }

                Console.WriteLine("✅ Telegram API источник запущен.");
                _pollTask = Task.Run(() => PollUpdatesAsync(_cts.Token), _cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка запуска Telegram API источника: {ex.Message}");
                _client?.Dispose();
                _client = null;
            }
        }

        private async Task PollUpdatesAsync(CancellationToken token)
        {
            try
            {
                // Ожидаем обновления
                await _client?.Updates_GetStateAsync();
                while (!token.IsCancellationRequested)
                {
                    // WTelegramClient автоматически получает обновления через OnUpdate, но для надежности можно использовать GetDifference
                    await Task.Delay(2000, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Ошибка в цикле получения обновлений: {ex.Message}");
            }
        }

        private async Task OnLogin(string authCode)
        {
            Console.Write("Введите код подтверждения: ");
            var code = Console.ReadLine();
            await Task.CompletedTask;
            // Код будет передан через this.Login
            // В реальности нужно передать в _client.Login(code), но здесь обработаем иначе
            // WTelegramClient сам вызовет OnLogin и передаст строку кода, которую мы вводим
        }

        private void OnUpdate(Client client, UpdatesBase updates)
        {
            // Обрабатываем обновления
            if (updates is Updates.NewMessage newMessage)
            {
                foreach (var update in newMessage.Updates)
                {
                    if (update is TL.UpdateNewMessage updateNew)
                    {
                        ProcessMessage(updateNew.Message);
                    }
                }
            }
        }

        private void ProcessMessage(Message message)
        {
            // Проверяем, что сообщение текстовое и не пустое
            if (message == null || string.IsNullOrEmpty(message.Message)) return;

            // Получаем отправителя
            string sender = GetSenderName(message);
            if (string.IsNullOrEmpty(sender)) return;

            // Фильтр по каналам
            if (_config.AllowedChannels.Count > 0 && !_config.AllowedChannels.Contains(sender, StringComparer.OrdinalIgnoreCase))
                return;

            // Время публикации
            DateTime eventTime = DateTimeOffset.FromUnixTimeSeconds(message.Date).UtcDateTime;

            // ReplyToMessageId
            string? replyTo = null;
            if (message.ReplyTo != null)
            {
                replyTo = message.ReplyTo.ReplyToMsgId?.ToString();
            }

            var raw = new RawMessage
            {
                Id = $"telegram_api_{message.Id}",
                SourceType = _sourceType,
                Sender = sender,
                Text = message.Message,
                ReceivedAt = DateTime.UtcNow,
                EventTime = eventTime,
                ReplyToMessageId = replyTo,
                Priority = DeterminePriority(message.Message)
            };

            // Вызываем событие
            OnMessageReceived?.Invoke(this, raw);
        }

        private string GetSenderName(Message message)
        {
            // Для каналов
            if (message.Chat is Channel channel)
                return channel.Title ?? "Unknown";

            // Для чатов
            if (message.Chat is Chat chat)
                return chat.Title ?? "Unknown";

            // Для пользователей
            if (message.From is User user)
                return $"{user.FirstName} {user.LastName}".Trim() ?? "Unknown";

            return "Unknown";
        }

        private int DeterminePriority(string text)
        {
            string lower = text.ToLowerInvariant();
            if (lower.Contains("ракет") || lower.Contains("пуск") || lower.Contains("уаб"))
                return Priority.RocketMissile;
            if (lower.Contains("хорнет") || lower.Contains("дартс") || lower.Contains("ударный"))
                return Priority.StrikeDrone;
            if (lower.Contains("шарк") || lower.Contains("лелека") || lower.Contains("разведчик"))
                return Priority.ReconDrone;
            if (lower.Contains("фпв") || lower.Contains("fpv"))
                return Priority.FPV_Activity;
            if (lower.Contains("отбой") || (lower.Contains("внимание") && lower.Contains("режим")))
                return Priority.WatchTerminate;
            return Priority.LowPriority;
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            if (_pollTask != null)
                await _pollTask;
            _client?.Dispose();
            _client = null;
            Console.WriteLine("🛑 Telegram API источник остановлен.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Dispose();
            _client?.Dispose();
        }
    }
}