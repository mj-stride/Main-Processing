using System;

namespace PrivateTransportCleaning.Models
{
    public class GpxPoint
    {
        public DateTime Timestamp { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Speed { get; set; }

        public string? DeviceID { get; set; }
        public string? TrackingID { get; set; }
        public string? UserID { get; set; }
        public string? ModeID { get; set; }
        public string? CauseID { get; set; }
        public string? KilometerPostID { get; set; }
        public string? FilePath { get; set; }
        public string? DistrictID { get; set; }
    }
}