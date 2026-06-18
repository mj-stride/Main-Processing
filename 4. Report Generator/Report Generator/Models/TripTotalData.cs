namespace Report_Generator.Models
{
    public class TripTotalData
    {
        public string Period { get; set; } = "";
        public string Direction { get; set; } = "";
        public double TotalTravelTimeSec { get; set; }
        public double TotalDistanceM { get; set; }
        public double AvgTravelSpeedKph { get; set; }
        public double AvgRunningSpeedKph { get; set; }
        public double TotalDelayTimeSec { get; set; }
        public double TotalDelayLengthM { get; set; }
    }
}
