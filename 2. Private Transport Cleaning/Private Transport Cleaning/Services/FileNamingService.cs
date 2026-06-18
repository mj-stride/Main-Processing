using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace PrivateTransportCleaning.Services
{
    public class FileNamingService
    {
        private readonly RegionRoadDetectionService _rrService;

        public FileNamingService(RegionRoadDetectionService rrService)
        {
            _rrService = rrService;
        }

        public string BuildName(
            string dbPath,
            List<(double lat, double lon)> sampledPoints,
            string originalZipName)
        {
            var (region, road) = _rrService.Detect(dbPath, sampledPoints);

            region = Clean(region);
            road = Clean(road);

            var date = DateTime.Now.ToString("yyyyMMdd");

            var baseName = Path.GetFileNameWithoutExtension(originalZipName);
            baseName = Clean(baseName);

            return $"{baseName}_snapped.csv";
        }

        private string Clean(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "UNKNOWN";

            var cleaned = input;

            // allow letters, numbers, DASH, and underscore
            cleaned = Regex.Replace(cleaned, @"[^a-zA-Z0-9\-_]+", "_");

            // optional: prevent multiple underscores only
            cleaned = Regex.Replace(cleaned, "_+", "_");

            return cleaned.Trim('_');
        }

        public string BuildZipName(
            string dbPath,
            List<(double lat, double lon)> sampledPoints)
        {
            var (region, road) = _rrService.Detect(dbPath, sampledPoints);

            region = Clean(region);
            road = Clean(road);

            var date = DateTime.Now.ToString("yyyyMMdd");

            return $"{region}_{road}_SNAPPED_{date}.zip".ToUpper();
        }
    }
}