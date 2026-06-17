namespace PrivateTransportCleaning.Models
{
    public class KilometerPost
    {
        public string KilometerPostId { get; set; } = "";
        public string RegionId { get; set; } = "";
        public string RoadName { get; set; } = "";

        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }
}