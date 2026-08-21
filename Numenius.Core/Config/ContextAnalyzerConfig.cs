using Newtonsoft.Json;

namespace Numenius.Core.Config
{
    public class ContextAnalyzerConfig
    {
        [JsonProperty("Mode")]
        public string Mode { get; set; } = "Simple"; // "Simple", "TfIdf", "Ensemble"

        [JsonProperty("MaxHistorySize")]
        public int MaxHistorySize { get; set; } = 20;

        [JsonProperty("TimeWindowMinutes")]
        public int TimeWindowMinutes { get; set; } = 5;

        [JsonProperty("MinScore")]
        public double MinScore { get; set; } = 0.3;
    }
}