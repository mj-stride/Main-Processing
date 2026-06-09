namespace Travel_Time_and_Delay_Web_Application.Models
{
    public sealed class GpxRecord
    {
        public int Group { get; set; }
        public DateTime? Timestamp { get; set; }
        public double SnappedLat { get; set; }
        public double SnappedLon { get; set; }
        public double? Speed { get; set; }
        public double? ModeID { get; set; }
        public double? CauseID { get; set; }
        public double? Boarding { get; set; }
        public int? Alighting { get; set; }
        public int? OnBoard { get; set; }
        public string? KilometerPostID { get; set; }
        public string FilePath { get; set; } = "";
        public string? DistrictID { get; set; }

        // computed (optional)
        public int? Second => Timestamp?.Second;
        public int? SecDiff { get; set; }
        public double? DistanceDiff { get; set; }
    }
}
