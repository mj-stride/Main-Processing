namespace Report_Generator.Models
{
    public class SegmentAverages
    {
        public string Direction { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public string Period { get; set; }
        public double TravelTimeSec { get; set; }
        public double DistanceM { get; set; }
        public double TravelSpeedKph { get; set; }
        public double RunningSpeedKph { get; set; }
        public double DelayTimeSec { get; set; }
        public double DelayLengthM { get; set; }
        public string? DelayCausesSummary { get; set; }
    }
}
