using System;
using System.IO;
using System.Threading;

namespace Numenius.Core.Utilities
{
    public static class GraphLogger
    {
        private static readonly object _lock = new();
        private static readonly string _logPath = "Logs/graph_log.txt";

        static GraphLogger()
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        public static void Log(string message, bool console = true)
        {
            lock (_lock)
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var fullMessage = $"[{timestamp}] {message}";
                try
                {
                    File.AppendAllText(_logPath, fullMessage + Environment.NewLine);
                }
                catch { /* игнорируем ошибки записи */ }
                if (console)
                    Console.WriteLine($"🧠 {message}");
            }
        }

        public static void LogGraphStats(int nodes, int edges, double buildTimeSeconds)
        {
            Log($"Граф построен: {nodes} узлов, {edges} рёбер, за {buildTimeSeconds:F2} с.");
        }

        public static void LogPrediction(int incidentId, string threatType, double confidence, string zones)
        {
            Log($"Прогноз для инцидента #{incidentId}: {threatType}, уверенность {confidence:P0}, зоны: {zones}");
        }

        public static void LogIncidentCreated(int incidentId, string threatType, string settlement)
        {
            Log($"Создан инцидент #{incidentId}: {threatType} в {settlement}");
        }

        public static void LogIncidentUpdated(int incidentId, string threatType, int points)
        {
            Log($"Обновлён инцидент #{incidentId}: {threatType}, точек: {points}");
        }
    }
}