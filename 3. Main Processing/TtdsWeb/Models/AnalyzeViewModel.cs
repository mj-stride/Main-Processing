namespace TtdsWeb.Models
{
    public class AnalyzeViewModel
    {
        public List<double[]>? Coords { get; set; }
        public List<SegmentResult>? Results { get; set; }
        public List<object>? Segments { get; set; }
        public List<object>? CpData { get; set; }

        public AnalysisSummary Summary { get; set; } = new();
    }
}
