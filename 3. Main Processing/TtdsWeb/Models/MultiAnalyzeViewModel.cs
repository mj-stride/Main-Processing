using System.Collections.Generic;

namespace TtdsWeb.Models
{
    public class MultiAnalyzeViewModel
    {
        // Backward-compatible (old view references)
        public List<DatasetAnalysis> Datasets { get; set; } = new();
        public AnalysisSummary? OverallSummary { get; set; }
        public List<DirectionalSummary> DirectionSummaries { get; set; } = new();

        // New grouped view
        public List<PeakAnalysisGroup> PeakGroups { get; set; } = new();

        // map CP markers
        public List<object> CpData { get; set; } = new();
        public List<string> RegionList { get; set; } = new();
        public Dictionary<string, List<string>> RoadsByRegion { get; set; } = new();

        public string? SelectedRegion { get; set; }
        public string? SelectedRoad { get; set; }

        public class DatasetAnalysis
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";

            public string? PeakCode { get; set; }
            public string? PeakLabel { get; set; }
            public string? Direction { get; set; }

            public List<SegmentResult> Results { get; set; } = new();
            public List<object> Segments { get; set; } = new();
            public AnalysisSummary Summary { get; set; } = new();
        }
    }

    public class PeakAnalysisGroup
    {
        public string PeakCode { get; set; } = "";
        public string PeakLabel { get; set; } = "";

        public List<MultiAnalyzeViewModel.DatasetAnalysis> Datasets { get; set; } = new();

        public AnalysisSummary? OverallSummary { get; set; }
        public List<DirectionalSummary> DirectionSummaries { get; set; } = new();

        public List<SegmentResult> SegmentResults { get; set; } = new();
        public List<object> Segments { get; set; } = new();
    }

    public class AnalysisSummary
    {
        public double TotalTravelTimeMin { get; set; }
        public double TotalDistanceKm { get; set; }
        public double AvgTravelSpeed { get; set; }
        public double AvgRunningSpeed { get; set; }
        public double TotalDelayMin { get; set; }
        public double TotalDelayLength { get; set; }
    }

    public class DirectionalSummary
    {
        public string Direction { get; set; } = "";
        public string Name { get; set; } = "";
        public double AvgTravelTimeMin { get; set; }
        public double AvgDistanceKm { get; set; }
        public double AvgTravelSpeed { get; set; }
        public double AvgRunningSpeed { get; set; }
        public double AvgDelayMin { get; set; }
        public double AvgDelayLength { get; set; }
    }
}
