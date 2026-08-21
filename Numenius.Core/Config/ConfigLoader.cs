using System;
using System.IO;
using Newtonsoft.Json;

namespace Numenius.Core.Config
{
    public static class ConfigLoader
    {
        public static OrchestratorConfig LoadOrchestratorConfig(string path = "orchestrator.json")
        {
            if (!File.Exists(path))
            {
                var defaultConfig = new OrchestratorConfig();
                defaultConfig.Sources.Add(new SourceConfig { Type = "Manual", Enabled = true });
                defaultConfig.Outputs.Add(new OutputConfig { Type = "Console", Enabled = true });
                SaveConfig(defaultConfig, path);
                return defaultConfig;
            }
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<OrchestratorConfig>(json) ?? new OrchestratorConfig();
        }

        public static T LoadModuleConfig<T>(string? path) where T : new()
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new T();
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<T>(json) ?? new T();
        }

        public static void SaveConfig(object config, string path)
        {
            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(path, json);
        }
    }
}