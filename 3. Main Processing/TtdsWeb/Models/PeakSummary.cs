namespace TtdsWeb.Models
{
    public class PeakSummary
    {
        public string PeakCode { get; set; } = "OFF";
        public string PeakLabel { get; set; } = "Off-Peak";
        public int Count { get; set; }

        public double AvgTravelTimeMin { get; set; }
        public double AvgDistanceKm { get; set; }
        public double AvgTravelSpeed { get; set; }
        public double AvgRunningSpeed { get; set; }
        public double AvgDelayMin { get; set; }
        public double AvgDelayLength { get; set; }
    }
}
