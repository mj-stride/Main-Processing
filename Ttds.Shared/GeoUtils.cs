namespace Ttds.Shared;

public static class GeoUtils
{
    private const double EarthRadiusMeters = 6371000.0;

    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        double phi1 = ToRad(lat1), phi2 = ToRad(lat2);
        double dPhi = ToRad(lat2 - lat1);
        double dLambda = ToRad(lon2 - lon1);

        double a = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2) +
                   Math.Cos(phi1) * Math.Cos(phi2) *
                   Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);

        return EarthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        double phi1 = ToRad(lat1), phi2 = ToRad(lat2);
        double dLambda = ToRad(lon2 - lon1);

        double y = Math.Sin(dLambda) * Math.Cos(phi2);
        double x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda);

        return (ToDeg(Math.Atan2(y, x)) + 360.0) % 360.0;
    }

    public static string BearingToCardinal(double bearingDegrees)
    {
        if (bearingDegrees < 45 || bearingDegrees > 315) return "NB";
        if (bearingDegrees < 135) return "EB";
        if (bearingDegrees < 225) return "SB";
        return "WB";
    }

    public static (double lat, double lon) ProjectPointToSegment(
        double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        if (dx == 0 && dy == 0) return (ax, ay);

        double t = ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy);
        t = Math.Max(0, Math.Min(1, t));

        return (ay + t * dy, ax + t * dx); // note: (lat, lon) order to match GeoUtilityService
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
    private static double ToDeg(double rad) => rad * 180.0 / Math.PI;
}