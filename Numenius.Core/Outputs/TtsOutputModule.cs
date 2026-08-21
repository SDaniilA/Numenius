using System;
using System.Collections.Generic;
using System.Speech.Synthesis;
using System.Threading.Tasks;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;
using Numenius.Core.Utilities;

namespace Numenius.Core.Outputs
{
    public class TtsOutputModule : IOutputModule, IDisposable
    {
        private readonly TtsOutputConfig _config;
        private SpeechSynthesizer? _synthesizer;
        private DateTime _lastSpeechTime = DateTime.MinValue;
        private bool _disposed;

        public TtsOutputModule(TtsOutputConfig config)
        {
            _config = config ?? new TtsOutputConfig();
        }

        public Task InitializeAsync()
        {
            if (!_config.Enabled) return Task.CompletedTask;
            try
            {
                _synthesizer = new SpeechSynthesizer();
                _synthesizer.SetOutputToDefaultAudioDevice();
                _synthesizer.Rate = _config.Rate;
                _synthesizer.Volume = _config.Volume;

                if (!string.IsNullOrEmpty(_config.VoiceName))
                {
                    try { _synthesizer.SelectVoice(_config.VoiceName); }
                    catch { Console.WriteLine($"⚠️ Голос '{_config.VoiceName}' не найден"); }
                }
                Console.WriteLine("🔊 TTS модуль инициализирован.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Ошибка TTS: {ex.Message}");
                _synthesizer = null;
            }
            return Task.CompletedTask;
        }

        public Task HandleParsedMessageAsync(ParsedMessage message)
        {
            if (!_config.Enabled || _synthesizer == null || message == null) return Task.CompletedTask;
            if ((DateTime.UtcNow - _lastSpeechTime).TotalMilliseconds < _config.MinDelayMs)
                return Task.CompletedTask;

            try
            {
                string text = BuildSpeechText(message);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _synthesizer.SpeakAsync(text);
                    _lastSpeechTime = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Ошибка TTS: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public Task HandlePredictionAsync(Prediction prediction)
        {
            if (!_config.Enabled || _synthesizer == null || prediction == null) return Task.CompletedTask;
            if (prediction.Confidence < 0.6) return Task.CompletedTask;

            try
            {
                string startStr = prediction.AttackWindowStart.HasValue 
                    ? prediction.AttackWindowStart.Value.ToLocalTime().ToString("HH:mm") 
                    : "??:??";
                string endStr = prediction.AttackWindowEnd.HasValue 
                    ? prediction.AttackWindowEnd.Value.ToLocalTime().ToString("HH:mm") 
                    : "??:??";
                string text = $"Прогноз: {prediction.ThreatType ?? "Unknown"}. Зона: {string.Join(", ", prediction.AffectedSettlements ?? new List<string>())}. Окно: {startStr} – {endStr}. Уверенность {prediction.Confidence:P0}.";
                _synthesizer.SpeakAsync(text);
                _lastSpeechTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Ошибка TTS прогноза: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private string BuildSpeechText(ParsedMessage msg)
        {
            var parts = new List<string>();

            if (_config.SpeakTime)
                parts.Add(SpeechUtilities.FormatTimeForSpeech(msg.ReceivedAt.ToLocalTime(), "hours_minutes"));

            if (_config.SpeakSender && !string.IsNullOrEmpty(msg.Sender))
                parts.Add(msg.Sender);

            if (_config.SpeakMessage && !string.IsNullOrWhiteSpace(msg.CleanedText))
                parts.Add(msg.CleanedText);

            if (!string.IsNullOrEmpty(_config.Suffix))
                parts.Add(_config.Suffix);

            return string.Join(". ", parts);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _synthesizer?.Dispose();
        }
    }

    public class TtsOutputConfig
    {
        public bool Enabled { get; set; } = true;
        public int Rate { get; set; } = 1;
        public int Volume { get; set; } = 30;
        public string VoiceName { get; set; } = "Microsoft Irina Desktop";
        public bool SpeakTime { get; set; } = false;
        public bool SpeakSender { get; set; } = true;
        public bool SpeakMessage { get; set; } = true;
        public string Suffix { get; set; } = "От канала";
        public int MinDelayMs { get; set; } = 500;
    }
}