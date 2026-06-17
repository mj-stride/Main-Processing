using PrivateTransportCleaning.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PrivateTransportCleaning.Services
{
    public class RegionRoadDetectionService
    {
        private readonly GeoUtilityService _geo;
        private readonly KilometerPostService _kmService;

        public RegionRoadDetectionService(
            GeoUtilityService geo,
            KilometerPostService kmService)
        {
            _geo = geo;
            _kmService = kmService;
        }

        public (string region, string road) Detect(
            string dbPath,
            List<(double lat, double lon)> sampledPoints)
        {
            var kmPosts = _kmService.Load(dbPath);

            if (kmPosts == null || kmPosts.Count == 0)
                return ("UNKNOWN_REGION", "UNKNOWN_ROAD");

            var votes = new Dictionary<(string region, string road), int>();

            const int SAMPLE_EVERY = 10;
            const double MAX_MATCH_METERS = 80;

            for (int i = 0; i < sampledPoints.Count; i += SAMPLE_EVERY)
            {
                var p = sampledPoints[i];

                KilometerPost? best = null;
                double bestDist = double.MaxValue;

                foreach (var km in kmPosts)
                {
                    var d = _geo.Haversine(
                        p.lat, p.lon,
                        km.Latitude,
                        km.Longitude
                    );

                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = km;
                    }
                }

                if (best != null && bestDist <= MAX_MATCH_METERS)
                {
                    var key = (best.RegionId, best.RoadName);

                    if (votes.ContainsKey(key))
                        votes[key]++;
                    else
                        votes[key] = 1;
                }
            }

            if (votes.Count == 0)
                return ("UNKNOWN_REGION", "UNKNOWN_ROAD");

            var top = votes
                .OrderByDescending(v => v.Value)
                .First()
                .Key;

            return ($"REGION_{top.region}", top.road);
        }
    }
}