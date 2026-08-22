using Newtonsoft.Json;

namespace Numenius.Core.Config
{
    public class TelegramApiConfig
    {
        [JsonProperty("ApiId")]
        public int ApiId { get; set; }

        [JsonProperty("ApiHash")]
        public string ApiHash { get; set; } = string.Empty;

        [JsonProperty("Phone")]
        public string Phone { get; set; } = string.Empty;

        [JsonProperty("SessionPath")]
        public string SessionPath { get; set; } = "telegram.session";

        [JsonProperty("AllowedChannels")]
        public List<string> AllowedChannels { get; set; } = new();
    }
}