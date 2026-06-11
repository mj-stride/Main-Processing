using System;
using System.Collections.Generic;
using System.Linq;
using PrivateTransportCleaning.Models;

namespace PrivateTransportCleaning.Services
{
    public class GpxProcessingService
    {
        private readonly GeoUtilityService _geo;
        private readonly SnappingService _snapper;

        public GpxProcessingService(
            GeoUtilityService geo,
            SnappingService snapper)
        {
            _geo = geo;
            _snapper = snapper;
        }

        public List<SnappedResult> Process(
            List<GpxPoint> points,
            List<(double lat, double lon)> centerline)
        {
            if (points == null || points.Count == 0)
                return new List<SnappedResult>();

            var ordered = points
                .OrderBy(p => p.Timestamp)
                .ToList();

            var results = new List<SnappedResult>();

            const double TARGET_SEC = 1.0;
            const double TOL = 0.25;
            const double MAX_GAP = 2.0;

            bool started = false;
            GpxPoint? lastKept = null;

            for (int i = 0; i < ordered.Count; i++)
            {
                var row = ordered[i];
                var prev = i > 0 ? ordered[i - 1] : null;

                double? rawTimeDiff = null;

                if (prev != null)
                    rawTimeDiff = (row.Timestamp - prev.Timestamp).TotalSeconds;

                // START CONDITION
                if (!started)
                {
                    if (rawTimeDiff == null)
                        continue;

                    if (Math.Abs(rawTimeDiff.Value - TARGET_SEC) <= TOL)
                    {
                        started = true;
                        lastKept = null;
                    }
                    else
                    {
                        continue;
                    }
                }

                // BREAK LOGIC
                if (rawTimeDiff != null && rawTimeDiff > MAX_GAP)
                {
                    var snap = _snapper.SnapToCenterline(row.Latitude, row.Longitude, centerline);

                    results.Add(new SnappedResult
                    {
                        SnappedLat = snap.SnappedLat,
                        SnappedLon = snap.SnappedLon,
                        DeviationMeters = snap.DeviationMeters
                    });

                    started = false;
                    lastKept = null;
                    continue;
                }

                double? dist = null;

                if (lastKept != null)
                {
                    dist = _geo.Haversine(row.Latitude, row.Longitude, lastKept.Latitude, lastKept.Longitude);
                }
                else if (prev != null)
                {
                    dist = _geo.Haversine(row.Latitude, row.Longitude, prev.Latitude, prev.Longitude);
                }

                if (dist != null && dist > 200)
                    continue;

                if (row.Speed > 120)
                    continue;

                var snapped = _snapper.SnapToCenterline(row.Latitude, row.Longitude, centerline);

                results.Add(new SnappedResult
                {
                    SnappedLat = snapped.SnappedLat,
                    SnappedLon = snapped.SnappedLon,
                    DeviationMeters = snapped.DeviationMeters
                });

                lastKept = row;
            }

            return results;
        }
    }
}