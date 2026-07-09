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
                var proj = _geo.ProjectPointToSegment(
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
    }
}