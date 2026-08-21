using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            var nlp = new NlpParser(geo);
            var outputCache = new OutputCache();
            var scenarioManager = new ScenarioManager(db, geo, heuristics);
            var predictorConfig = ConfigLoader.LoadModuleConfig<PredictorConfig>(
                Path.Combine(appSettingsPath, "predictor_config.json"));

            IPredictor predictor;
            if (predictorConfig.Graph.Enabled)
            {
                var gp = new GraphPredictor(geo, db, heuristics, predictorConfig.Graph);
                await gp.UpdateGraphAsync();
                predictor = gp;
            }
            else if (predictorConfig.Statistical.Enabled)
            {
                predictor = new StatisticalPredictor(db, geo, heuristics, predictorConfig.Statistical);
            }
            else
            {
                throw new Exception("Нет активных предикторов для импорта.");
            }

            var filterConfig = new FilterConfig();
            //var processor = new MessageProcessor(nlp, geo, db, scenarioManager, new List<IPredictor> { predictor }, outputCache, filterConfig);
			var processor = new MessageProcessor(nlp, geo, db, scenarioManager, new List<IPredictor>(), outputCache, filterConfig);
            var importer = new OfflineImporter(processor, db);
            await importer.ImportAsync(jsonPath);

            if (predictor is GraphPredictor gp2)
                await gp2.UpdateGraphAsync();

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
			// Закрываем старые инциденты (одноразово)
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

            var predictors = new List<IPredictor>();
            if (predictorConfig.Graph.Enabled)
            {
                var gp = new GraphPredictor(geo, db, heuristics, predictorConfig.Graph);
                await gp.UpdateGraphAsync();
                predictors.Add(gp);
                Console.WriteLine("🧠 Графовый предиктор активирован.");
            }
            if (predictorConfig.Statistical.Enabled)
            {
                var sp = new StatisticalPredictor(db, geo, heuristics, predictorConfig.Statistical);
                predictors.Add(sp);
                Console.WriteLine("📊 Статистический предиктор активирован.");
            }
			if (predictorConfig.Trajectory.Enabled)
			{
				var tp = new TrajectoryPredictor(geo, db, heuristics, predictorConfig.Trajectory);
				predictors.Add(tp);
				Console.WriteLine("📈 Траекторный предиктор активирован.");
			}
			
            if (predictors.Count == 0)
                throw new Exception("Нет активных предикторов.");

            var nlp = new NlpParser(geo);
            var outputCache = new OutputCache();
            var scenarioManager = new ScenarioManager(db, geo, heuristics);
            var processor = new MessageProcessor(nlp, geo, db, scenarioManager, predictors, outputCache, filterConfig);

            var consoleOutput = new ConsoleOutputModule();
            string ttsConfigPath = Path.Combine(appSettingsPath, "tts_output.json");
            if (!File.Exists(ttsConfigPath))
            {
                var defaultTts = new TtsOutputConfig { Enabled = false };
                ConfigLoader.SaveConfig(defaultTts, ttsConfigPath);
            }
            var ttsConfig = ConfigLoader.LoadModuleConfig<TtsOutputConfig>(ttsConfigPath);
            var ttsOutput = new TtsOutputModule(ttsConfig);
            await ttsOutput.InitializeAsync();

            // НЕ ПОДПИСЫВАЕМСЯ НА СОБЫТИЯ outputCache, чтобы избежать дублирования
            // Вся передача сообщений идёт через Orchestrator

            var orchestrator = new Orchestrator(orchestratorPath);
            orchestrator.RegisterProcessor(processor);
            orchestrator.RegisterOutput(consoleOutput);
            orchestrator.RegisterOutput(ttsOutput);

            await orchestrator.StartAsync();
			// ===== ФОНОВАЯ ЗАДАЧА: ЗАКРЫТИЕ ПО СРОКУ =====
			_ = Task.Run(async () =>
			{
				// Первый запуск сразу после старта
				await scenarioManager.ExpireOldIncidentsAsync();

				while (true)
				{
					await Task.Delay(60000); // 1 минута
					await scenarioManager.ExpireOldIncidentsAsync();
				}
			});
            // Обработчик команд
            _ = Task.Run(() =>
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
                            Console.WriteLine("Доступные команды: report [day|week|month], exit");
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