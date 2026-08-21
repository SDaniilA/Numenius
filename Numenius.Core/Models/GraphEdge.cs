using System;

namespace Numenius.Core.Models
{
    public class GraphEdge
    {
        public int Id { get; set; }
        public int SourceNodeId { get; set; }
        public int TargetNodeId { get; set; }
        public double Probability { get; set; }
        public double TransitionCount { get; set; }
        public double AverageDelayHours { get; set; }
        public double StdDevDelayHours { get; set; }
        public DateTime LastUpdated { get; set; }
        public GraphNode? SourceNode { get; set; }
        public GraphNode? TargetNode { get; set; }
    }
}