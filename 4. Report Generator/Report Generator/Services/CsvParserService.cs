using CsvHelper;
using System;
using System.Globalization;
using Report_Generator.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CsvHelper.Configuration;


namespace Report_Generator.Services
{
    public class CsvParserService
    {
        private static readonly string[] RequiredColumns =
        {
            "From", "To", "TravelTimeSec", "DistanceM",
            "TravelSpeedKph", "RunningSpeedKph", "Delays", "DelayLengthM"
        };

        private readonly CsvConfiguration _config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,   // Ignore missing fields
            HeaderValidated = null,     // Ignore header validation
            BadDataFound = null         // Ignore bad data
        };

        public (List<TripData> rows, List<string> missingColumns) ReadTripCsv(Stream fileStream)
        {
            using var reader = new StreamReader(fileStream);
            using var csv = new CsvReader(reader, _config);

            csv.Read();
            csv.ReadHeader();

            var headers = csv.HeaderRecord ?? Array.Empty<string>();
            var missing = RequiredColumns
                .Where(c => !headers.Any(h => h.Trim().Equals(c, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (missing.Any())
                return (new List<TripData>(), missing);   // caller logs and skips

            var rows = csv.GetRecords<TripData>().ToList();
            return (rows, new List<string>());
        }
    }
}
