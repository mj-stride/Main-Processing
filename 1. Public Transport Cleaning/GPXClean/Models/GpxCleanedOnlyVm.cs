namespace Travel_Time_and_Delay_Web_Application.Models
{
    public class GpxCleanedOnlyVm
    {
        public string BatchId { get; set; } = "";
        public List<GpxCleanedTripVm> Trips { get; set; } = new();
    }

    public class GpxCleanedTripVm
    {
        public string? DetectedRegionId { get; set; }
        public string? DetectedRoadName { get; set; }
        public string SourceZipFileName { get; set; } = "";
        public List<string> SourceZipFiles { get; set; } = new();
        public int PointsCount => Points?.Count ?? 0;

        public string VehicleCode { get; set; } = "";
        public string TripId { get; set; } = "";
        public string DtToken { get; set; } = "";
        public string Direction { get; set; } = "UNK";
        public int PartIndex { get; set; }

        // map points (cleaned only)
        public List<LatLng> Points { get; set; } = new();

        public LatLng? Start { get; set; }
        public LatLng? End { get; set; }

        // optional: for table/list
        //public int PointsCount => Points?.Count ?? 0;
        public string DisplayName => $"{VehicleCode} {TripId} {Direction}-{PartIndex}";
    }
}
