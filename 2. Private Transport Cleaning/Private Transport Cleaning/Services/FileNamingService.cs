using System;
using System.Collections.Generic;
using System.IO;

namespace PrivateTransportCleaning.Services
{
    public class FileNamingService
    {
        private readonly RegionRoadDetectionService _rrService;

        public FileNamingService(RegionRoadDetectionService rrService)
        {
            _rrService = rrService;
        }

        public string BuildName(string dbPath, List<string> csvFiles)
        {
            var (region, road) = _rrService.Detect(dbPath, csvFiles);

            region = Clean(region);
            road = Clean(road);

            var date = DateTime.Now.ToString("yyyyMMdd");

            return $"{region}_{road}_SNAPPED_{date}.zip";
        }

        private string Clean(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "UNKNOWN";

            var cleaned = input.ToUpper();
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[^A-Z0-9]+", "_");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "_+", "_");

            return cleaned.Trim('_');
        }
    }
}