using NetTopologySuite.Geometries;

namespace Report_Generator.Models
{
    public class ControlPoint
    {
        public string Name { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    // Port of the row dict Python builds inside make_trip_cp2cp_segments.
    public class TripCp2CpSegment
    {
        public string FromCP { get; set; } = "";
        public string ToCP { get; set; } = "";
        public double? SpeedKph { get; set; }
        public string SpeedCat { get; set; } = "";
        public string ColorName { get; set; } = ""; // "red" | "orange" | "yellow" | "green" | "blue" | "gray"
        public float LineWidth { get; set; }
        public LineString Geometry { get; set; } = null!; // WGS84 (EPSG:4326), trip-following sub-curve
    }
}
