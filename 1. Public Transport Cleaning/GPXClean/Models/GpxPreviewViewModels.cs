namespace Travel_Time_and_Delay_Web_Application.Models
{
    public sealed class LatLng
    {
        public double Lat { get; set; }
        public double Lon { get; set; }
    }

    public sealed class GpxPreviewFile
    {
        public string FileName { get; set; } = "";
        public List<LatLng> Points { get; set; } = new();
        public int PointCount => Points.Count;
        public LatLng? Start { get; set; }   // ✅ green
        public LatLng? End { get; set; }     // ✅ red
    }

    public sealed class GpxPreviewVm
    {
        public string BatchId { get; set; } = "";
        public List<GpxPreviewFile> Files { get; set; } = new();
    }
}
