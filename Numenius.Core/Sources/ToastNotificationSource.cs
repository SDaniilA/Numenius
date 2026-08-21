using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;
using Numenius.Core.Utilities;

namespace Numenius.Core.Sources
{
    public class ToastNotificationSource : IMessageSource, IDisposable
    {
        private readonly ToastSourceConfig _config;
        private UserNotificationListener? _listener;
        private HashSet<uint> _processedIds = new(); // без readonly
        private readonly object _lock = new();
        private Timer? _pollingTimer;
        private bool _disposed;
        private readonly string _sourceType = "Toast";

        public event EventHandler<RawMessage>? OnMessageReceived;

        public ToastNotificationSource(ToastSourceConfig config)
        {
            _config = config ?? new ToastSourceConfig();
        }

        public void LoadProcessedIds()
        {
            if (!_config.SaveProcessedIds) return;
            try
            {
                string filePath = _config.ProcessedIdsFile;
                if (System.IO.File.Exists(filePath))
                {
                    var json = System.IO.File.ReadAllText(filePath);
                    var ids = Newtonsoft.Json.JsonConvert.DeserializeObject<uint[]>(json);
                    if (ids != null)
                    {
                        lock (_lock) { _processedIds = new HashSet<uint>(ids); }
                    }
                    Console.WriteLine($"📊 Загружено ID: {_processedIds.Count}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Ошибка загрузки processedIds: {ex.Message}");
            }
        }

        private void SaveProcessedIds()
        {
            if (!_config.SaveProcessedIds) return;
            try
            {
                uint[] idsCopy;
                lock (_lock) { idsCopy = new List<uint>(_processedIds).ToArray(); }
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(idsCopy);
                System.IO.File.WriteAllText(_config.ProcessedIdsFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Ошибка сохранения processedIds: {ex.Message}");
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Загружаем ранее обработанные ID
                LoadProcessedIds();

                _listener = UserNotificationListener.Current;
                var accessStatus = await _listener.RequestAccessAsync();
                if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
                {
                    Console.WriteLine("❌ Нет доступа к уведомлениям. Разрешите в настройках.");
                    return;
                }

                Console.WriteLine("✅ Toast источник запущен.");
				Console.WriteLine($"🗑️ Режим удаления уведомлений: {_config.DeleteMode}");
                _pollingTimer = new Timer(
                    _ => CheckNotificationsAsync(cancellationToken).Wait(),
                    null,
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(_config.CheckIntervalSeconds)
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка запуска Toast источника: {ex.Message}");
            }
        }

        public Task StopAsync()
        {
            _pollingTimer?.Dispose();
            _pollingTimer = null;
            Console.WriteLine("🛑 Toast источник остановлен.");
            return Task.CompletedTask;
        }

        private async Task CheckNotificationsAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || _listener == null) return;

            try
            {
                var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                var allowedApps = _config.AllowedApps ?? new List<string> { "Telegram" };

                foreach (var notif in notifications)
                {
                    uint id = notif.Id;
                    lock (_lock)
                    {
                        if (_processedIds.Contains(id)) continue;
                    }

                    string appName = notif.AppInfo.DisplayInfo.DisplayName;
                    if (string.IsNullOrEmpty(appName)) continue;

                    bool allowed = false;
                    foreach (var app in allowedApps)
                    {
                        if (appName.Contains(app, StringComparison.OrdinalIgnoreCase))
                        {
                            allowed = true;
                            break;
                        }
                    }
                    if (!allowed) continue;

                    var (sender, message) = ParseNotification(notif);
                    if (string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(message)) continue;

                    string cleaned = StringUtilities.CleanMessage(message);

                    var raw = new RawMessage
                    {
                        Id = $"toast_{id}_{DateTime.UtcNow.Ticks}",
                        SourceType = _sourceType,
                        Sender = sender,
                        Text = cleaned,
                        ReceivedAt = DateTime.UtcNow,
                        Priority = DeterminePriority(cleaned)
                    };

                    OnMessageReceived?.Invoke(this, raw);

                    lock (_lock)
                    {
                        _processedIds.Add(id);
                        if (_processedIds.Count > _config.MaxProcessedIds)
                            _processedIds.Clear();
                    }

                    if (_config.DeleteMode == "immediate")
					{
						try
						{
							_listener.RemoveNotification(id);
							Console.WriteLine($"🗑️ Удалено уведомление {id}");
						}
						catch (Exception ex)
						{
							Console.WriteLine($"⚠️ Не удалось удалить уведомление {id}: {ex.Message}");
						}
					}

                    // Периодически сохраняем ID
                    if (_processedIds.Count % 10 == 0)
                        SaveProcessedIds();
                }
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("⚠️ Потерян доступ к уведомлениям. Переподключаем...");
                await ReinitListenerAsync();
            }
            catch (Exception ex)
            {
                if (_config.DebugMode)
                    Console.WriteLine($"⚠️ Ошибка проверки уведомлений: {ex.Message}");
            }
        }

        private (string? sender, string? message) ParseNotification(UserNotification notif)
        {
            try
            {
                var binding = notif.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding == null) return (null, null);
                var texts = binding.GetTextElements();
                if (texts == null || texts.Count < 2) return (null, null);
                string sender = texts[0]?.Text?.Trim() ?? "";
                string message = texts[1]?.Text?.Trim() ?? "";
                return (sender, message);
            }
            catch { return (null, null); }
        }

        private int DeterminePriority(string text)
        {
            string lower = text.ToLowerInvariant();
            if (lower.Contains("ракет") || lower.Contains("пуск") || lower.Contains("уаб"))
                return Priority.RocketMissile;
            if (lower.Contains("хорнет") || lower.Contains("дартс") || lower.Contains("ударный бпла"))
                return Priority.StrikeDrone;
            if (lower.Contains("шарк") || lower.Contains("лелека") || lower.Contains("разведчик"))
                return Priority.ReconDrone;
            if (lower.Contains("фпв") || lower.Contains("fpv"))
                return Priority.FPV_Activity;
            if (lower.Contains("отбой") || (lower.Contains("внимание") && lower.Contains("режим")))
                return Priority.WatchTerminate;
            return Priority.LowPriority;
        }

        private async Task ReinitListenerAsync()
        {
            try
            {
                _listener = null;
                _listener = UserNotificationListener.Current;
                var status = await _listener.RequestAccessAsync();
                if (status == UserNotificationListenerAccessStatus.Allowed)
                    Console.WriteLine("✅ Доступ восстановлен.");
                else
                    Console.WriteLine("⚠️ Не удалось восстановить доступ.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка переподключения: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pollingTimer?.Dispose();
            SaveProcessedIds();
        }
    }

    public class ToastSourceConfig
    {
        public int CheckIntervalSeconds { get; set; } = 2;
        public List<string> AllowedApps { get; set; } = new() { "Telegram" };
        public string DeleteMode { get; set; } = "on_exit";
        public int MaxProcessedIds { get; set; } = 10000;
        public bool DebugMode { get; set; } = false;
        public bool SaveProcessedIds { get; set; } = true;
        public string ProcessedIdsFile { get; set; } = "processed.json";
    }

    public static class Priority
    {
        public const int RocketMissile = 0;
        public const int StrikeDrone = 1;
        public const int ReconDrone = 2;
        public const int FPV_Activity = 3;
        public const int WatchTerminate = 4;
        public const int LowPriority = 5;
    }
}