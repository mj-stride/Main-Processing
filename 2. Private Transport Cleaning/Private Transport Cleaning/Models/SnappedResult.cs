namespace PrivateTransportCleaning.Models
{
    public class SnappedResult
    {
        public double OriginalLat { get; set; }
        public double OriginalLon { get; set; }

        public double SnappedLat { get; set; }
        public double SnappedLon { get; set; }

        public double DeviationMeters { get; set; }

        public DateTime Timestamp { get; set; }
        public double Speed { get; set; }

        public string? DeviceID { get; set; }
        public string? TrackingID { get; set; }
        public string? UserID { get; set; }
        public string? ModeID { get; set; }
        public string? CauseID { get; set; }
        public string? KilometerPostID { get; set; }
        public string? FilePath { get; set; }
        public string? DistrictID { get; set; }

        public double? SecDiff { get; set; }
        public double? DistanceDiff { get; set; }

        public bool IsBreak { get; set; }
    }
}