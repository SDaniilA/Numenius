using System.Collections.Generic;
using Newtonsoft.Json;

namespace Numenius.Core.Config
{
    public class OrchestratorConfig
    {
        [JsonProperty("Sources")]
        public List<SourceConfig> Sources { get; set; } = new();

        [JsonProperty("Outputs")]
        public List<OutputConfig> Outputs { get; set; } = new();

        [JsonProperty("QueueMaxSize")]
        public int QueueMaxSize { get; set; } = 500;

        [JsonProperty("ShutdownTimeoutSeconds")]
        public int ShutdownTimeoutSeconds { get; set; } = 10;
    }

    public class SourceConfig
    {
        [JsonProperty("Type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("Enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("ConfigFile")]
        public string? ConfigFile { get; set; }
    }

    public class OutputConfig
    {
        [JsonProperty("Type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("Enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("ConfigFile")]
        public string? ConfigFile { get; set; }
    }
}