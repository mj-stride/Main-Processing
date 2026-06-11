using PrivateTransportCleaning.Models;
using System;
using System.Collections.Generic;
using System.IO;
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

        public (string region, string road) Detect(string dbPath, List<string> csvFiles)
        {
            var kmPosts = _kmService.Load(dbPath);

            if (kmPosts.Count == 0)
                return ("UNKNOWN_REGION", "UNKNOWN_ROAD");

            var votes = new Dictionary<(string region, string road), int>();

            const int SAMPLE_EVERY = 10;
            const double MAX_MATCH_METERS = 80;

            foreach (var file in csvFiles)
            {
                if (!File.Exists(file))
                    continue;

                var lines = File.ReadAllLines(file).Skip(1);

                var points = new List<(double lat, double lon)>();

                foreach (var line in lines)
                {
                    var parts = line.Split(',');

                    if (parts.Length < 4)
                        continue;

                    if (double.TryParse(parts[2], out double lat) &&
                        double.TryParse(parts[3], out double lon))
                    {
                        points.Add((lat, lon));
                    }
                }

                for (int i = 0; i < points.Count; i += SAMPLE_EVERY)
                {
                    var p = points[i];

                    KilometerPost best = null;
                    double bestDist = double.MaxValue;

                    foreach (var km in kmPosts)
                    {
                        var d = _geo.Haversine(p.lat, p.lon, km.Latitude, km.Longitude);

                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = km;
                        }
                    }

                    if (best != null && bestDist <= MAX_MATCH_METERS)
                    {
                        var key = (best.RegionId, best.RoadName);

                        if (!votes.ContainsKey(key))
                            votes[key] = 0;

                        votes[key]++;
                    }
                }
            }

            if (votes.Count == 0)
                return ("UNKNOWN_REGION", "UNKNOWN_ROAD");

            var top = votes.OrderByDescending(v => v.Value).First().Key;

            return ($"REGION_{top.region}", top.road);
        }
    }
}