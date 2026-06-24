namespace PrivateTransportCleaning.Models
{
    public class TripFile
    {
        public string FileName { get; set; } = "";
        public string ViewUrl { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public long FileSize { get; set; }
    }
}