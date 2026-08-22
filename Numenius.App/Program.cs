using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Numenius.Core.Config;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;
using Numenius.Core.Orchestrator;
using Numenius.Core.Outputs;
using Numenius.Core.Predictors;
using Numenius.Core.Processors;
using Numenius.Core.Services;

namespace Numenius.App
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--import" && args.Length > 1)
            {
                await RunImport(args[1]);
                return;
            }

            await RunMonitor();
        }

        private static async Task GenerateAndShowReport(string period, IDatabaseService db, HeuristicsConfig heuristics)
        {
            var now = DateTime.UtcNow.AddHours(heuristics.TimeZoneOffsetHours);
            DateTime start, end;
            switch (period)
            {
                case "week":
                    start = now.Date.AddDays(-7);
                    end = now.Date.AddDays(1).AddSeconds(-1);
                    break;
                case "month":
                    start = now.Date.AddDays(-30);
                    end = now.Date.AddDays(1).AddSeconds(-1);
                    break;
                default:
                    start = now.Date;
                    end = now.Date.AddDays(1).AddSeconds(-1);
                    break;
            }

            var reportGen = new ReportGenerator(db, heuristics);
            var report = await reportGen.GenerateReportAsync(start.ToUniversalTime(), end.ToUniversalTime(), $"Отчёт за {period}");
            Console.WriteLine(report);

            var folder = Path.Combine(AppContext.BaseDirectory, "Reports");
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            var filePath = Path.Combine(folder, $"report_{period}_{now:yyyyMMdd_HHmmss}.txt");
            await File.WriteAllTextAsync(filePath, report);
            Console.WriteLine($"📁 Сохранено в {filePath}");
        }

        private static async Task RunImport(string jsonPath)
        {
            Console.WriteLine("🔄 Запуск оффлайн-импорта...");

            string baseDir = AppContext.BaseDirectory;
            string appSettingsPath = Path.Combine(baseDir, "appsettings");
            if (!Directory.Exists(appSettingsPath))
                Directory.CreateDirectory(appSettingsPath);

            var heuristics = ConfigLoader.LoadModuleConfig<HeuristicsConfig>(
                Path.Combine(appSettingsPath, "heuristics_config.json"));

            var db = new DatabaseService(Path.Combine(baseDir, "numenius.db"));
            await db.InitializeAsync();

            var geo = new GeoService(db, Path.Combine(appSettingsPath, "settlements.json"));
            var zoneService = new ZoneService(geo);
            var scenarioManager = new ScenarioManager(db, geo, heuristics, zoneService);

            var normalizer = new PorterStemmerNormalizer();
            var nlp = new NlpParser(geo, normalizer);
            var outputCache = new OutputCache();
            var predictorConfig = ConfigLoader.LoadModuleConfig<PredictorConfig>(
                Path.Combine(appSettingsPath, "predictor_config.json"));

            // Контекстный анализатор
            string contextConfigPath = Path.Combine(appSettingsPath, "context_analyzer.json");
            if (!File.Exists(contextConfigPath))
            {
                var defaultContext = new ContextAnalyzerConfig();
                ConfigLoader.SaveConfig(defaultContext, contextConfigPath);
            }
            var contextConfig = ConfigLoader.LoadModuleConfig<ContextAnalyzerConfig>(contextConfigPath);
            IContextAnalyzer contextAnalyzer = new SimpleContextAnalyzer(contextConfig);
            Console.WriteLine("🧠 Простой контекстный анализатор активирован.");

            // Загрузка ТТХ
            string threatCharacteristicsPath = Path.Combine(appSettingsPath, "threat_characteristics.json");
            var threatCharacteristics = ConfigLoader.LoadThreatCharacteristics(threatCharacteristicsPath);

            var predictors = new List<IPredictor>();
            if (predictorConfig.Bayesian.Enabled)
            {
                predictors.Add(new BayesianPredictor(geo, db, zoneService, heuristics, threatCharacteristics));
                Console.WriteLine("🧠 Байесовский предиктор активирован.");
            }
            // Другие предикторы можно отключить, но оставим по желанию
            if (predictorConfig.Graph.Enabled)
            {
                var gp = new GraphPredictor(geo, db, heuristics, predictorConfig.Graph);
                await gp.UpdateGraphAsync();
                predictors.Add(gp);
            }
            if (predictorConfig.Statistical.Enabled)
            {
                predictors.Add(new StatisticalPredictor(db, geo, heuristics, predictorConfig.Statistical));
            }
            if (predictorConfig.Trajectory.Enabled)
            {
                predictors.Add(new TrajectoryPredictor(geo, db, heuristics, predictorConfig.Trajectory));
            }

            if (predictors.Count == 0)
                throw new Exception("Нет активных предикторов.");

            var filterConfig = new FilterConfig();
            var processor = new MessageProcessor(nlp, geo, db, scenarioManager, predictors, outputCache, filterConfig, contextAnalyzer);
            var importer = new OfflineImporter(processor, db);
            await importer.ImportAsync(jsonPath);

            Console.WriteLine("✅ Импорт завершён. Нажмите любую клавишу для выхода.");
            Console.ReadKey();
        }

        private static async Task RunMonitor()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("Numenius Argutus – система прогнозирования угроз");
            Console.WriteLine("=================================================");

            string baseDir = AppContext.BaseDirectory;
            string appSettingsPath = Path.Combine(baseDir, "appsettings");
            if (!Directory.Exists(appSettingsPath))
                Directory.CreateDirectory(appSettingsPath);

            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            System.Net.ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;

            string orchestratorPath = Path.Combine(appSettingsPath, "orchestrator.json");
            if (!File.Exists(orchestratorPath))
            {
                var defaultOrch = new OrchestratorConfig();
                defaultOrch.Sources.Add(new SourceConfig { Type = "Manual", Enabled = true });
                defaultOrch.Outputs.Add(new OutputConfig { Type = "Console", Enabled = true });
                ConfigLoader.SaveConfig(defaultOrch, orchestratorPath);
            }

            string heuristicsPath = Path.Combine(appSettingsPath, "heuristics_config.json");
            if (!File.Exists(heuristicsPath))
            {
                var defaultHeur = new HeuristicsConfig();
                ConfigLoader.SaveConfig(defaultHeur, heuristicsPath);
            }
            var heuristics = ConfigLoader.LoadModuleConfig<HeuristicsConfig>(heuristicsPath);

            string predictorPath = Path.Combine(appSettingsPath, "predictor_config.json");
            if (!File.Exists(predictorPath))
            {
                var defaultPred = new PredictorConfig();
                ConfigLoader.SaveConfig(defaultPred, predictorPath);
            }
            var predictorConfig = ConfigLoader.LoadModuleConfig<PredictorConfig>(predictorPath);

            string overridesPath = Path.Combine(appSettingsPath, "source_overrides.json");
            var overrides = ConfigLoader.LoadModuleConfig<SourceOverridesConfig>(overridesPath);

            var filterConfig = new FilterConfig
            {
                AllowedSenders = overrides.AllowedSenders,
                BlacklistedSenders = overrides.BlacklistedSenders
            };

            var db = new DatabaseService(Path.Combine(baseDir, "numenius.db"));
            await db.InitializeAsync();
            await db.CloseOldIncidentsAsync();

            var settlements = await db.GetAllSettlementsAsync();
            if (!settlements.Any())
            {
                string settlementsPath = Path.Combine(appSettingsPath, "settlements.json");
                if (File.Exists(settlementsPath))
                {
                    var json = File.ReadAllText(settlementsPath);
                    var list = JsonConvert.DeserializeObject<List<Settlement>>(json);
                    if (list != null)
                    {
                        foreach (var s in list)
                            await db.SaveSettlementAsync(s);
                        Console.WriteLine($"✅ Загружено {list.Count} НП из JSON в БД.");
                    }
                }
            }

            var geo = new GeoService(db, Path.Combine(appSettingsPath, "settlements.json"));
            var zoneService = new ZoneService(geo);
            var scenarioManager = new ScenarioManager(db, geo, heuristics, zoneService);

            var predictors = new List<IPredictor>();
            // Байесовский предиктор — основной
            if (predictorConfig.Bayesian.Enabled)
            {
                string threatCharacteristicsPath = Path.Combine(appSettingsPath, "threat_characteristics.json");
                var threatCharacteristics = ConfigLoader.LoadThreatCharacteristics(threatCharacteristicsPath);
                predictors.Add(new BayesianPredictor(geo, db, zoneService, heuristics, threatCharacteristics));
                Console.WriteLine("🧠 Байесовский предиктор активирован.");
            }
            // Опционально другие предикторы (по конфигурации)
            if (predictorConfig.Graph.Enabled)
            {
                var gp = new GraphPredictor(geo, db, heuristics, predictorConfig.Graph);
                await gp.UpdateGraphAsync();
                predictors.Add(gp);
                Console.WriteLine("🧠 Графовый предиктор активирован.");
            }
            if (predictorConfig.Statistical.Enabled)
            {
                predictors.Add(new StatisticalPredictor(db, geo, heuristics, predictorConfig.Statistical));
                Console.WriteLine("📊 Статистический предиктор активирован.");
            }
            if (predictorConfig.Trajectory.Enabled)
            {
                predictors.Add(new TrajectoryPredictor(geo, db, heuristics, predictorConfig.Trajectory));
                Console.WriteLine("📈 Траекторный предиктор активирован.");
            }

            if (predictors.Count == 0)
                throw new Exception("Нет активных предикторов.");

            // Контекстный анализатор
            string contextConfigPath = Path.Combine(appSettingsPath, "context_analyzer.json");
            if (!File.Exists(contextConfigPath))
            {
                var defaultContext = new ContextAnalyzerConfig();
                ConfigLoader.SaveConfig(defaultContext, contextConfigPath);
            }
            var contextConfig = ConfigLoader.LoadModuleConfig<ContextAnalyzerConfig>(contextConfigPath);
            IContextAnalyzer contextAnalyzer;
            switch (contextConfig.Mode.ToLowerInvariant())
            {
                case "tfidf":
                    contextAnalyzer = new TfIdfContextAnalyzer(contextConfig);
                    Console.WriteLine("🧠 TF-IDF контекстный анализатор активирован.");
                    break;
                case "ensemble":
                    contextAnalyzer = new EnsembleContextAnalyzer(contextConfig);
                    Console.WriteLine("🧠 Ансамблевый контекстный анализатор активирован.");
                    break;
                default:
                    contextAnalyzer = new SimpleContextAnalyzer(contextConfig);
                    Console.WriteLine("🧠 Простой контекстный анализатор активирован.");
                    break;
            }

            var normalizer = new PorterStemmerNormalizer();
            var nlp = new NlpParser(geo, normalizer);
            var outputCache = new OutputCache();

            var processor = new MessageProcessor(nlp, geo, db, scenarioManager, predictors, outputCache, filterConfig, contextAnalyzer);

            var orchestrator = new Orchestrator(orchestratorPath);
            orchestrator.RegisterProcessor(processor);
            await orchestrator.StartAsync();

            // Фоновая задача: закрытие по сроку
            _ = Task.Run(async () =>
            {
                await scenarioManager.ExpireOldIncidentsAsync();
                while (true)
                {
                    await Task.Delay(60000);
                    await scenarioManager.ExpireOldIncidentsAsync();
                }
            });

            // Командный цикл (включая ручной ввод)
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        Console.Write("\n> ");
                        var line = Console.ReadLine();
                        if (line == null) break;
                        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 0) continue;

                        if (parts[0].Equals("report", StringComparison.OrdinalIgnoreCase))
                        {
                            var period = parts.Length > 1 ? parts[1].ToLower() : "day";
                            _ = Task.Run(async () =>
                            {
                                Console.WriteLine($"📊 Генерация отчёта за {period}...");
                                await GenerateAndShowReport(period, db, heuristics);
                            });
                        }
                        else if (parts[0].Equals("exit", StringComparison.OrdinalIgnoreCase))
                        {
                            Environment.Exit(0);
                        }
                        else
                        {
                            var raw = new RawMessage
                            {
                                Id = $"manual_{DateTime.UtcNow.Ticks}",
                                SourceType = "Manual",
                                Sender = "Ручной ввод",
                                Text = line,
                                ReceivedAt = DateTime.UtcNow,
                                Priority = 3
                            };
                            await processor.ProcessAsync(raw, CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ Ошибка обработки команды: {ex.Message}");
                    }
                }
            });

            await Task.Delay(-1);
        }
    }
}