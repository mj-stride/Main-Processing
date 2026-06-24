using System;
using System.Collections.Generic;
using PrivateTransportCleaning.Models;

namespace PrivateTransportCleaning.Services
{
    public class SnappingService
    {
        private readonly GeoUtilityService _geo;

        public SnappingService(GeoUtilityService geo)
        {
            _geo = geo;
        }

        public SnappedResult SnapToCenterline(
            double lat,
            double lon,
            List<(double lat, double lon)> centerline)
        {
            double minDist = double.MaxValue;

            double snappedLat = lat;
            double snappedLon = lon;

            for (int i = 0; i < centerline.Count - 1; i++)
            {
                var a = centerline[i];
                var b = centerline[i + 1];

                // EXACT MATCH PYTHON ARG ORDER:
                var proj = ProjectPointToSegment(
                    lon, lat,
                    a.lon, a.lat,
                    b.lon, b.lat
                );

                double projLat = proj.lat;
                double projLon = proj.lon;

                double dist = _geo.Haversine(
                    lat, lon,
                    projLat, projLon
                );

                if (dist < minDist)
                {
                    minDist = dist;
                    snappedLat = projLat;
                    snappedLon = projLon;
                }
            }

            return new SnappedResult
            {
                SnappedLat = snappedLat,
                SnappedLon = snappedLon,
                DeviationMeters = minDist
            };
        }

        // 🔥 MUST MATCH PYTHON EXACTLY
        private (double lat, double lon) ProjectPointToSegment(
            double px, double py,
            double ax, double ay,
            double bx, double by)
        {
            double dx = bx - ax;
            double dy = by - ay;

            if (dx == 0 && dy == 0)
                return (ax, ay);

            double t =
                ((px - ax) * dx + (py - ay) * dy)
                / (dx * dx + dy * dy);

            if (t < 0) t = 0;
            if (t > 1) t = 1;

            double projX = ax + t * dx;
            double projY = ay + t * dy;

            return (projY, projX);
        }
    }
}