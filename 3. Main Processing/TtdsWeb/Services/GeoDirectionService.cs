using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TtdsWeb.Models;

namespace TtdsWeb.Services
{
    public interface IGeoDirectionService
    {
        string ComputeDatasetDirection(List<TripRow> rows);
        (double minLat, double maxLat, double minLon, double maxLon) ComputeBbox(List<TripRow> df, double bufferMeters = 500.0);
    }

    public class GeoDirectionService : IGeoDirectionService
    {
        private static double ToRad(double d) => d * Math.PI / 180.0;
        private static double ToDeg(double r) => r * 180.0 / Math.PI;

        private static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            var phi1 = ToRad(lat1);
            var phi2 = ToRad(lat2);
            var dLam = ToRad(lon2 - lon1);
            var y = Math.Sin(dLam) * Math.Cos(phi2);
            var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLam);
            var theta = Math.Atan2(y, x);
            return (ToDeg(theta) + 360.0) % 360.0;
        }

        private static string AxisDirection(double brng)
        {
            double dN = Math.Min(Math.Abs(brng - 0), 360 - Math.Abs(brng - 0));
            double dE = Math.Min(Math.Abs(brng - 90), 360 - Math.Abs(brng - 90));
            double dS = Math.Min(Math.Abs(brng - 180), 360 - Math.Abs(brng - 180));
            double dW = Math.Min(Math.Abs(brng - 270), 360 - Math.Abs(brng - 270));
            return new[] { ("NB", dN), ("EB", dE), ("SB", dS), ("WB", dW) }
                .OrderBy(t => t.Item2)
                .First().Item1;
        }

        public (double minLat, double maxLat, double minLon, double maxLon) ComputeBbox(List<TripRow> df, double bufferMeters = 500.0)
        {
            var validRows = df
                .Where(r => !double.IsNaN(r.SnappedLat) && !double.IsNaN(r.SnappedLon)
                         && !double.IsInfinity(r.SnappedLat) && !double.IsInfinity(r.SnappedLon)
                         && Math.Abs(r.SnappedLat) <= 90 && Math.Abs(r.SnappedLon) <= 180)
                .ToList();

            if (validRows.Count == 0)
                return (0, 0, 0, 0);

            var minLat = validRows.Min(r => r.SnappedLat);
            var maxLat = validRows.Max(r => r.SnappedLat);
            var minLon = validRows.Min(r => r.SnappedLon);
            var maxLon = validRows.Max(r => r.SnappedLon);

            double latMid = (minLat + maxLat) / 2.0;
            double dLat = bufferMeters / 111320.0;
            double dLon = bufferMeters / (111320.0 * Math.Cos(latMid * Math.PI / 180.0));
            return (minLat - dLat, maxLat + dLat, minLon - dLon, maxLon + dLon);
        }

        public string ComputeDatasetDirection(List<TripRow> rows)
        {
            var pts = rows
                .Where(r => !double.IsNaN(r.SnappedLat) && !double.IsNaN(r.SnappedLon)
                         && !double.IsInfinity(r.SnappedLat) && !double.IsInfinity(r.SnappedLon)
                         && Math.Abs(r.SnappedLat) <= 90 && Math.Abs(r.SnappedLon) <= 180)
                .Select(r => (lat: r.SnappedLat, lon: r.SnappedLon))
                .ToList();
            if (pts.Count < 2) return "Unknown";

            int i = 0, j = pts.Count - 1;
            while (i < j && Math.Abs(pts[i].lat - pts[i + 1].lat) < 1e-7 &&
                            Math.Abs(pts[i].lon - pts[i + 1].lon) < 1e-7) i++;
            while (j > i && Math.Abs(pts[j].lat - pts[j - 1].lat) < 1e-7 &&
                            Math.Abs(pts[j].lon - pts[j - 1].lon) < 1e-7) j--;

            if (i >= j) return "Unknown";
            var brng = Bearing(pts[i].lat, pts[i].lon, pts[j].lat, pts[j].lon);
            return AxisDirection(brng);
        }

        private static double Finite(double v) => (double.IsNaN(v) || double.IsInfinity(v)) ? 0.0 : v;

        public static double SpeedKph(TripRow r)
        {
            if (r != null && r.Speed.HasValue)
            {
                var s = Finite(r.Speed.Value);
                if (s >= 0 && s < 300) return s;
            }
            double secs = Math.Max(Finite(r?.secDiff ?? 0.0), 1e-6);
            double distM = Math.Max(Finite(r?.distanceDiff ?? 0.0), 0.0);
            return (distM / secs) * 3.6;
        }
    }
}