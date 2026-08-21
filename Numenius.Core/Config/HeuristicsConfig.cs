using Newtonsoft.Json;

namespace Numenius.Core.Config
{
    public class HeuristicsConfig
    {
        [JsonProperty("ZoneWidthKm")]
        public double ZoneWidthKm { get; set; } = 5.0;

        [JsonProperty("StrikeDroneLifetimeHours")]
        public double StrikeDroneLifetimeHours { get; set; } = 2.0;

        [JsonProperty("ReconDroneLifetimeHours")]
        public double ReconDroneLifetimeHours { get; set; } = 24.0;

        [JsonProperty("WatchLifetimeHours")]
        public double WatchLifetimeHours { get; set; } = 6.0;

        [JsonProperty("ConfidenceDecayPerHour")]
        public double ConfidenceDecayPerHour { get; set; } = 0.05;

        [JsonProperty("WatchConfidenceBoost")]
        public double WatchConfidenceBoost { get; set; } = 0.2;

        [JsonProperty("AttackWindowMinutes")]
        public int AttackWindowMinutes { get; set; } = 90;

        [JsonProperty("ReconAttackWindowHours")]
        public double ReconAttackWindowHours { get; set; } = 5.0;

        [JsonProperty("MinConfidenceToAlert")]
        public double MinConfidenceToAlert { get; set; } = 0.6;

        [JsonProperty("FpvLifetimeMinutes")]
        public int FpvLifetimeMinutes { get; set; } = 30;

        [JsonProperty("DefaultLifetimeHours")]
        public double DefaultLifetimeHours { get; set; } = 2.0;

        [JsonProperty("TimeZoneOffsetHours")]
        public double TimeZoneOffsetHours { get; set; } = 3.0;
		
		[JsonProperty("GeocodingMode")]
		public string GeocodingMode { get; set; } = "Manual"; // "LocalOnly", "Manual", "OnlineWithFallback"
    }
}