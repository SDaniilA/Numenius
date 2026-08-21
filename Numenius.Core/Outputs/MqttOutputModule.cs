using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;
using Numenius.Core.Utilities;
using Newtonsoft.Json;

namespace Numenius.Core.Outputs
{
    public class MqttOutputModule : IOutputModule, IDisposable
    {
        private readonly MqttOutputConfig _config;
        private IMqttClient? _mqttClient;
        private bool _disposed;
        private bool _isConnected;

        public MqttOutputModule(MqttOutputConfig config)
        {
            _config = config ?? new MqttOutputConfig();
        }

        public async Task InitializeAsync()
        {
            if (!_config.Enabled) return;

            try
            {
                var factory = new MqttFactory();
                _mqttClient = factory.CreateMqttClient();

                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(_config.Broker, _config.Port)
                    .WithClientId(_config.ClientId)
                    .WithCleanSession()
                    .Build();

                var result = await _mqttClient.ConnectAsync(options);
                _isConnected = result.ResultCode == MqttClientConnectResultCode.Success;
                if (_isConnected)
                    Console.WriteLine($"✅ MQTT подключён к {_config.Broker}:{_config.Port}");
                else
                    Console.WriteLine($"⚠️ MQTT не подключился: {result.ResultCode}");

                if (_config.ReconnectEnabled)
                {
                    _mqttClient.DisconnectedAsync += async (e) =>
                    {
                        Console.WriteLine("⚠️ MQTT разорван, переподключение...");
                        await Task.Delay(5000);
                        try
                        {
                            var reconnectResult = await _mqttClient.ConnectAsync(options);
                            _isConnected = reconnectResult.ResultCode == MqttClientConnectResultCode.Success;
                            if (_isConnected)
                                Console.WriteLine("✅ MQTT переподключён");
                            else
                                Console.WriteLine($"❌ MQTT переподключение не удалось: {reconnectResult.ResultCode}");
                        }
                        catch { Console.WriteLine("❌ MQTT переподключение не удалось"); }
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка инициализации MQTT: {ex.Message}");
                _mqttClient = null;
                _isConnected = false;
            }
        }

        public async Task HandleParsedMessageAsync(ParsedMessage message)
        {
            if (!_config.Enabled || _mqttClient == null || !_isConnected) return;

            try
            {
                string payload = BuildMqttPayload(message);
                var topic = _config.Topic;
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel((MQTTnet.Protocol.MqttQualityOfServiceLevel)_config.Qos)
                    .WithRetainFlag(_config.Retain)
                    .Build();

                await _mqttClient.PublishAsync(msg);
                if (_config.DebugMode)
                    Console.WriteLine($"📤 MQTT отправлено: {payload}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ MQTT ошибка отправки: {ex.Message}");
            }
        }

        public Task HandlePredictionAsync(Prediction prediction)
        {
            // Можно отправлять прогнозы, но пока пропускаем
            return Task.CompletedTask;
        }

        private string BuildMqttPayload(ParsedMessage msg)
        {
            var obj = new
            {
                timestamp = _config.IncludeTimestamp ? msg.ReceivedAt.ToString(_config.TimestampFormat) : null,
                sender = msg.Sender,
                threat = msg.ThreatType,
                category = msg.Category.ToString(),
                settlements = msg.Settlements.Count > 0 ? string.Join(",", msg.Settlements.Select(s => s.Name)) : "",
                direction = msg.Direction ?? "",
                status = msg.Status,
                confidence = msg.Confidence,
                text = msg.CleanedText
            };

            string json = JsonConvert.SerializeObject(obj);
            if (json.Length > _config.MaxPayloadBytes)
            {
                json = json.Substring(0, _config.MaxPayloadBytes - 3) + "...";
            }
            return json;
        }

        public async Task DisconnectAsync()
        {
            if (_mqttClient != null && _isConnected)
            {
                await _mqttClient.DisconnectAsync();
                _isConnected = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _mqttClient?.Dispose();
        }
    }

    public class MqttOutputConfig
    {
        public bool Enabled { get; set; } = false;
        public string Broker { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 1883;
        public string Topic { get; set; } = "numenius/alerts";
        public string ClientId { get; set; } = $"numenius_{Environment.MachineName}_{new Random().Next(1000,9999)}";
        public bool IncludeTimestamp { get; set; } = true;
        public string TimestampFormat { get; set; } = "HH:mm:ss";
        public int MaxPayloadBytes { get; set; } = 200;
        public bool ReconnectEnabled { get; set; } = true;
        public int Qos { get; set; } = 0;
        public bool Retain { get; set; } = false;
        public bool DebugMode { get; set; } = false;
    }
}