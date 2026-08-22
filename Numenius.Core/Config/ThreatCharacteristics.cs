using Newtonsoft.Json;

namespace Numenius.Core.Config
{
    public class ThreatCharacteristics
    {
        [JsonProperty("MaxSpeedKmh")]
        public double MaxSpeedKmh { get; set; }

        [JsonProperty("MaxDistanceKm")]
        public double MaxDistanceKm { get; set; }

        [JsonProperty("LifetimeMinutes")]
        public int LifetimeMinutes { get; set; }
    }
}