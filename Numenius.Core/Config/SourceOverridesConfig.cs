using System.Collections.Generic;
using Newtonsoft.Json;

namespace Numenius.Core.Config
{
    public class SourceOverridesConfig
    {
        [JsonProperty("Overrides")]
        public Dictionary<string, double> Overrides { get; set; } = new();

        [JsonProperty("AllowedSenders")]
        public List<string> AllowedSenders { get; set; } = new();

        [JsonProperty("BlacklistedSenders")]
        public List<string> BlacklistedSenders { get; set; } = new();
    }
}