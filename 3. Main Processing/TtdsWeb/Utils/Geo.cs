using Ttds.Shared;

namespace TtdsWeb.Utils;
public static class Geo
{
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
        => GeoUtils.HaversineMeters(lat1, lon1, lat2, lon2);
}