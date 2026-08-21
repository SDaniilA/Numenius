namespace Numenius.Core.Models
{
    /// <summary>
    /// Населённый пункт или топоним с координатами
    /// </summary>
    public class Settlement
    {
        public string Name { get; set; } = string.Empty;
        public double Lat { get; set; }
        public double Lon { get; set; }

        /// <summary>Признак того, что координаты требуют проверки (для оффлайн-импортёра)</summary>
        public bool NeedsReview { get; set; }
    }
}