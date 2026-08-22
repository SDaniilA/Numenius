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
using Numenius.Core.Sources;

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

            // Загрузка конфигураций
            var heuristics = ConfigLoader.LoadModuleConfig<HeuristicsConfig>(
                Path.Combine(appSettingsPath, "heuristics_config.json"));
            var predictorConfig = ConfigLoader.LoadModuleConfig<PredictorConfig>(
                Path.Combine(appSettingsPath, "predictor_config.json"));
            var contextConfig = ConfigLoader.LoadModuleConfig<ContextAnalyzerConfig>(
                Path.Combine(appSettingsPath, "context_analyzer.json"));
            var threatCharacteristics = ConfigLoader.LoadThreatCharacteristics(
                Path.Combine(appSettingsPath, "threat_characteristics.json"));

            // Инициализация БД и сервисов
            var db = new DatabaseService(Path.Combine(baseDir, "numenius.db"));
            await db.InitializeAsync();

            var geo = new GeoService(db, Path.Combine(appSettingsPath, "settlements.json"));

            // Загрузка населённых пунктов
            var settlements = (await db.GetAllSettlementsAsync()).ToList();
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
                        settlements = list;
                        Console.WriteLine($"✅ Загружено {settlements.Count} НП из JSON в БД.");
                    }
                }
            }

            // Создание графа и зон
            var settlementGraph = new SettlementGraph(geo, settlements);
            var zoneService = new ZoneService(settlementGraph, threatCharacteristics);

            // Создание менеджера сценариев
            var scenarioManager = new ScenarioManager(db, geo, heuristics, zoneService, threatCharacteristics);

            var normalizer = new PorterStemmerNormalizer();
            var nlp = new NlpParser(geo, normalizer);
            var outputCache = new OutputCache();

            // Контекстный анализатор
            IContextAnalyzer contextAnalyzer = new SimpleContextAnalyzer(contextConfig);
            Console.WriteLine("🧠 Простой контекстный анализатор активирован.");

            // Создание предикторов
            var predictors = new List<IPredictor>();
            if (predictorConfig.Bayesian.Enabled)
            {
                predictors.Add(new BayesianPredictor(geo, db, zoneService, heuristics, threatCharacteristics));
                Console.WriteLine("🧠 Байесовский предиктор активирован.");
            }
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

            // Загрузка конфигураций
            string telegramConfigPath = Path.Combine(appSettingsPath, "telegram_config.json");
			if (File.Exists(telegramConfigPath))
			{
				var telegramConfig = ConfigLoader.LoadModuleConfig<TelegramApiConfig>(telegramConfigPath);
				if (telegramConfig.ApiId != 0 && !string.IsNullOrEmpty(telegramConfig.ApiHash))
				{
					var telegramSource = new TelegramApiSource(telegramConfig);
					orchestrator.RegisterSource(telegramSource);
					Console.WriteLine("📡 Telegram API источник активирован.");
				}
			}
			
			string orchestratorPath = Path.Combine(appSettingsPath, "orchestrator.json");
            if (!File.Exists(orchestratorPath))
            {
                var defaultOrch = new OrchestratorConfig();
                defaultOrch.Sources.Add(new SourceConfig { Type = "Manual", Enabled = true });
                defaultOrch.Outputs.Add(new OutputConfig { Type = "Console", Enabled = true });
                ConfigLoader.SaveConfig(defaultOrch, orchestratorPath);
            }

            var heuristics = ConfigLoader.LoadModuleConfig<HeuristicsConfig>(
                Path.Combine(appSettingsPath, "heuristics_config.json"));
            var predictorConfig = ConfigLoader.LoadModuleConfig<PredictorConfig>(
                Path.Combine(appSettingsPath, "predictor_config.json"));
            var overrides = ConfigLoader.LoadModuleConfig<SourceOverridesConfig>(
                Path.Combine(appSettingsPath, "source_overrides.json"));
            var contextConfig = ConfigLoader.LoadModuleConfig<ContextAnalyzerConfig>(
                Path.Combine(appSettingsPath, "context_analyzer.json"));
            var threatCharacteristics = ConfigLoader.LoadThreatCharacteristics(
                Path.Combine(appSettingsPath, "threat_characteristics.json"));

            var filterConfig = new FilterConfig
            {
                AllowedSenders = overrides.AllowedSenders,
                BlacklistedSenders = overrides.BlacklistedSenders
            };

            // Инициализация БД
            var db = new DatabaseService(Path.Combine(baseDir, "numenius.db"));
            await db.InitializeAsync();
            await db.CloseOldIncidentsAsync();

            // Загрузка населённых пунктов
            var settlements = (await db.GetAllSettlementsAsync()).ToList();
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
                        settlements = list;
                        Console.WriteLine($"✅ Загружено {settlements.Count} НП из JSON в БД.");
                    }
                }
            }

            var geo = new GeoService(db, Path.Combine(appSettingsPath, "settlements.json"));
            var settlementGraph = new SettlementGraph(geo, settlements);
            var zoneService = new ZoneService(settlementGraph, threatCharacteristics);

            var scenarioManager = new ScenarioManager(db, geo, heuristics, zoneService, threatCharacteristics);

            // Создание предикторов
            var predictors = new List<IPredictor>();
            if (predictorConfig.Bayesian.Enabled)
            {
                predictors.Add(new BayesianPredictor(geo, db, zoneService, heuristics, threatCharacteristics));
                Console.WriteLine("🧠 Байесовский предиктор активирован.");
            }
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
							Priority = 3,
							EventTime = DateTime.UtcNow // время ввода = время события
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