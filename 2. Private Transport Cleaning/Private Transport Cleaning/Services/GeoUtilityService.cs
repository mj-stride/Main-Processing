using System;

namespace PrivateTransportCleaning.Services
{
    public class GeoUtilityService
    {
        private const double R = 6371000; // Earth radius in meters

        public double Haversine(double lat1, double lon1, double lat2, double lon2)
        {
            double phi1 = ToRadians(lat1);
            double phi2 = ToRadians(lat2);

            double dPhi = ToRadians(lat2 - lat1);
            double dLambda = ToRadians(lon2 - lon1);

            double a = Math.Pow(Math.Sin(dPhi / 2), 2) +
                       Math.Cos(phi1) * Math.Cos(phi2) *
                       Math.Pow(Math.Sin(dLambda / 2), 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            return R * c;
        }

        private double ToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        public (double lat, double lon) ProjectPointToSegment(
            double px, double py,
            double ax, double ay,
            double bx, double by)
        {
            double dx = bx - ax;
            double dy = by - ay;

            if (dx == 0 && dy == 0)
                return (ax, ay);

            double t = ((px - ax) * dx + (py - ay) * dy) /
                       (dx * dx + dy * dy);

            t = Math.Max(0, Math.Min(1, t));

            double projX = ax + t * dx;
            double projY = ay + t * dy;

            return (projX, projY);
        }
    }
}