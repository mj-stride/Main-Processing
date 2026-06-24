using CsvHelper.Configuration.Attributes;

namespace Report_Generator.Models
{
    public class TripData
    {
        [Ignore] public string Direction { get; set; }
        [Ignore] public int TripNo { get; set; }
        [Ignore] public string Period { get; set; }
        [Ignore] public string SourceFile { get; set; }
        public string From { get; set; }
        public string To { get; set; } 
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double TravelTimeSec { get; set; }
        public double TravelTimeMin { get; set; }
        public double DistanceM { get; set; }
        public double TravelSpeedKph { get; set; }
        public double RunningSpeedKph { get; set; }
        public double Delays { get; set; }
        public double DelayLengthM { get; set; }
        public string? DelayCauses { get; set; }
    }
}
