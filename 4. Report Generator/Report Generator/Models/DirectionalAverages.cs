namespace Report_Generator.Models
{
    public class DirectionalAverages
    {
        public string Direction { get; set; }
        public string Period { get; set; }
        public double AvgTravelTimeSec { get; set; }
        public double AvgDistanceM { get; set; }
        public double AvgTravelSpeedKph { get; set; }
        public double AvgRunningSpeedKph { get; set; } 
        public double AvgDelayTimeSec { get; set; }
        public double AvgDelayLengthM { get; set; }
    }
}
