using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Numenius.Core.Config;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;
using Numenius.Core.Queue;
using Numenius.Core.Utilities;
using Numenius.Core.Sources;
using Numenius.Core.Outputs;   // <-- добавлено для использования внешнего ConsoleOutputModule

namespace Numenius.Core.Orchestrator
{
    public class Orchestrator : IDisposable
    {
        private readonly OrchestratorConfig _config;
        private readonly PriorityMessageQueue _queue;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<IMessageSource> _sources = new();
        private readonly List<IOutputModule> _outputs = new();
        private IMessageProcessor? _processor;
        private Task? _processingTask;
        private bool _disposed;

        public Orchestrator(string configPath = "orchestrator.json")
        {
            _config = ConfigLoader.LoadOrchestratorConfig(configPath);
            _queue = new PriorityMessageQueue();
        }

        public void RegisterProcessor(IMessageProcessor processor)
        {
            _processor = processor;
        }

        public void RegisterSource(IMessageSource source)
        {
            source.OnMessageReceived += (s, msg) => _queue.Enqueue(msg);
            _sources.Add(source);
        }

        public void RegisterOutput(IOutputModule output)
        {
            _outputs.Add(output);
        }

        private void LoadSourcesFromConfig()
        {
            foreach (var srcCfg in _config.Sources)
            {
                if (!srcCfg.Enabled) continue;

                IMessageSource? source = null;
                try
                {
                    switch (srcCfg.Type.ToLower())
                    {
                        case "toast":
                            var toastConfig = ConfigLoader.LoadModuleConfig<ToastSourceConfig>(srcCfg.ConfigFile);
                            source = new ToastNotificationSource(toastConfig);
                            break;
                        case "manual":
                            var manualConfig = ConfigLoader.LoadModuleConfig<ManualInputConfig>(srcCfg.ConfigFile);
                            source = new ManualInputSource(manualConfig);
                            break;
                        default:
                            Console.WriteLine($"⚠️ Неизвестный тип источника: {srcCfg.Type}");
                            continue;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Ошибка загрузки источника {srcCfg.Type}: {ex.Message}");
                    continue;
                }

                if (source != null)
                {
                    source.OnMessageReceived += (s, msg) => _queue.Enqueue(msg);
                    _sources.Add(source);
                }
            }
        }

        private void LoadOutputsFromConfig()
        {
            foreach (var outCfg in _config.Outputs)
            {
                if (!outCfg.Enabled) continue;

                IOutputModule? output = null;
                try
                {
                    switch (outCfg.Type.ToLower())
                    {
                        case "console":
                            // Используем внешний ConsoleOutputModule из Numenius.Core.Outputs
                            var consoleConfig = ConfigLoader.LoadModuleConfig<ConsoleOutputConfig>(outCfg.ConfigFile);
                            output = new ConsoleOutputModule(consoleConfig);
                            break;
                        case "tts":
                            var ttsConfig = ConfigLoader.LoadModuleConfig<TtsOutputConfig>(outCfg.ConfigFile);
                            output = new TtsOutputModule(ttsConfig);
                            break;
                        case "mqtt":
                            var mqttConfig = ConfigLoader.LoadModuleConfig<MqttOutputConfig>(outCfg.ConfigFile);
                            output = new MqttOutputModule(mqttConfig);
                            break;
                        default:
                            Console.WriteLine($"⚠️ Неизвестный тип выхода: {outCfg.Type}");
                            continue;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Ошибка загрузки выхода {outCfg.Type}: {ex.Message}");
                    continue;
                }

                if (output != null)
                    _outputs.Add(output);
            }
        }

        public async Task StartAsync()
        {
            LoadSourcesFromConfig();
            LoadOutputsFromConfig();

            foreach (var output in _outputs)
                await output.InitializeAsync();

            foreach (var source in _sources)
                await source.StartAsync(_cts.Token);

            if (_processor != null)
                _processingTask = Task.Run(ProcessLoop, _cts.Token);

            Console.WriteLine("Numenius Orchestrator started.");
            Console.WriteLine($"Источников: {_sources.Count}, выходов: {_outputs.Count}");
            Console.WriteLine("Press Ctrl+C to stop.");
        }

        private async Task ProcessLoop()
        {
            var dedup = new DeduplicationService();
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var raw = await _queue.DequeueAsync(_cts.Token);
                    if (raw == null) continue;

                    if (dedup.IsDuplicate(raw.Text, raw.Sender, out _, out _, out _))
                        continue;
                    dedup.AddToBuffer(raw.Text, raw.Sender, raw.Text);

                    if (_processor != null)
                    {
                        var parsed = await _processor.ProcessAsync(raw, _cts.Token);
                        if (parsed != null)
                        {
                            foreach (var output in _outputs)
                            {
                                try
                                {
                                    await output.HandleParsedMessageAsync(parsed);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Output error: {ex.Message}");
                                    Console.WriteLine(ex.StackTrace); // всегда выводим стек для отладки
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Processor loop error: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                }
            }
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            if (_processingTask != null)
                await _processingTask;
            foreach (var source in _sources)
                await source.StopAsync();
            Console.WriteLine("Orchestrator stopped.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Dispose();
            _queue.Dispose();
        }
    }
}