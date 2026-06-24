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
                .GroupBy(p => p.Timestamp)
                .Select(g => g.First())
                .ToList();

            var results = new List<SnappedResult>();

            GpxPoint? lastKept = null;

            const double TARGET_SEC = 1.0;
            const double TOL = 0.25;
            const double MAX_GAP = 2.0;

            bool started = false;

            for (int i = 0; i < ordered.Count; i++)
            {
                var row = ordered[i];
                var prev = i > 0 ? ordered[i - 1] : null;

                double? rawTimeDiff = null;

                if (prev != null)
                    rawTimeDiff = (row.Timestamp - prev.Timestamp).TotalSeconds;

                // startup logic (unchanged)
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

                double? dist = null;
                double? timeDiff = rawTimeDiff;

                if (lastKept != null)
                {
                    dist = _geo.Haversine(
                        row.Latitude, row.Longitude,
                        lastKept.Latitude, lastKept.Longitude
                    );

                    timeDiff = (row.Timestamp - lastKept.Timestamp).TotalSeconds;
                }
                else if (prev != null)
                {
                    dist = _geo.Haversine(
                        row.Latitude, row.Longitude,
                        prev.Latitude, prev.Longitude
                    );
                }

                // filters (unchanged)
                if (dist != null && dist > 200)
                    continue;

                if (row.Speed > 120)
                    continue;

                // GAP HANDLING (unchanged logic, but FIXED missing fields)
                if (rawTimeDiff != null && rawTimeDiff > MAX_GAP)
                {
                    var snappedGap = _snapper.SnapToCenterline(
                        row.Latitude,
                        row.Longitude,
                        centerline
                    );

                    results.Add(new SnappedResult
                    {
                        OriginalLat = row.Latitude,
                        OriginalLon = row.Longitude,

                        SnappedLat = snappedGap.SnappedLat,
                        SnappedLon = snappedGap.SnappedLon,
                        DeviationMeters = snappedGap.DeviationMeters,

                        Timestamp = row.Timestamp,
                        Speed = row.Speed,

                        DeviceID = row.DeviceID,
                        TrackingID = row.TrackingID,
                        UserID = row.UserID,
                        ModeID = row.ModeID,
                        CauseID = row.CauseID,
                        KilometerPostID = row.KilometerPostID,
                        FilePath = row.FilePath,
                        DistrictID = row.DistrictID,

                        SecDiff = rawTimeDiff,
                        DistanceDiff = dist,
                        IsBreak = true
                    });

                    started = false;
                    lastKept = null;

                    continue;
                }

                var snapped = _snapper.SnapToCenterline(
                    row.Latitude,
                    row.Longitude,
                    centerline
                );

                results.Add(new SnappedResult
                {
                    OriginalLat = row.Latitude,
                    OriginalLon = row.Longitude,

                    SnappedLat = snapped.SnappedLat,
                    SnappedLon = snapped.SnappedLon,
                    DeviationMeters = snapped.DeviationMeters,

                    Timestamp = row.Timestamp,
                    Speed = row.Speed,

                    DeviceID = row.DeviceID,
                    TrackingID = row.TrackingID,
                    UserID = row.UserID,
                    ModeID = row.ModeID,
                    CauseID = row.CauseID,
                    KilometerPostID = row.KilometerPostID,
                    FilePath = row.FilePath,
                    DistrictID = row.DistrictID,

                    SecDiff = timeDiff,
                    DistanceDiff = dist,
                    IsBreak = false
                });

                lastKept = row;
            }

            return results;
        }
    }
}