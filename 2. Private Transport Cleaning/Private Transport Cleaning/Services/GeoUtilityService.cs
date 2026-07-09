using Ttds.Shared;

namespace PrivateTransportCleaning.Services
{
    public class GeoUtilityService
    {
        public double Haversine(double lat1, double lon1, double lat2, double lon2)
            => GeoUtils.HaversineMeters(lat1, lon1, lat2, lon2);

        public (double lat, double lon) ProjectPointToSegment(
            double px, double py,
            double ax, double ay,
            double bx, double by)
            => GeoUtils.ProjectPointToSegment(px, py, ax, ay, bx, by);
    }
}