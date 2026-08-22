using System.Collections.Generic;
using Newtonsoft.Json;

namespace Numenius.Core.Config
{
    public class PredictorConfig
    {
        [JsonProperty("ActivePredictor")]
        public string ActivePredictor { get; set; } = "Graph";

        [JsonProperty("Graph")]
        public GraphPredictorConfig Graph { get; set; } = new();

        [JsonProperty("Statistical")]
        public StatisticalPredictorConfig Statistical { get; set; } = new();

        [JsonProperty("Ensemble")]
        public EnsemblePredictorConfig Ensemble { get; set; } = new();
		
		[JsonProperty("Trajectory")]
        public TrajectoryPredictorConfig Trajectory { get; set; } = new();
		
		[JsonProperty("Bayesian")]
		public BayesianPredictorConfig Bayesian { get; set; } = new();
    }

    public class GraphPredictorConfig
    {
        [JsonProperty("Enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("MinOccurrencesForEdge")]
        public int MinOccurrencesForEdge { get; set; } = 3;

        [JsonProperty("MaxNodeAgeDays")]
        public int MaxNodeAgeDays { get; set; } = 30;

        [JsonProperty("ConfidenceBoostPerEdge")]
        public double ConfidenceBoostPerEdge { get; set; } = 0.05;
    }

    public class StatisticalPredictorConfig
    {
        [JsonProperty("Enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("MaxDaysHistory")]
        public int MaxDaysHistory { get; set; } = 30;
    }
	public class TrajectoryPredictorConfig
    {
        [JsonProperty("Enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("MaxPredictionDistanceKm")]
		public double MaxPredictionDistanceKm { get; set; } = 50.0;

        [JsonProperty("UncertaintyPercent")]
        public double UncertaintyPercent { get; set; } = 20.0;
    }
    public class EnsemblePredictorConfig
    {
        [JsonProperty("Enabled")]
        public bool Enabled { get; set; } = false;

        [JsonProperty("Weights")]
        public Dictionary<string, double> Weights { get; set; } = new();
    }
	public class BayesianPredictorConfig
	{
		[JsonProperty("Enabled")]
		public bool Enabled { get; set; } = true;
	}
}