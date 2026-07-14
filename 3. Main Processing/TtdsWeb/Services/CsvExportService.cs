using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using TtdsWeb.Models;

namespace TtdsWeb.Services
{
    public class CsvExportService : ICsvExportService
    {
        private readonly CsvConfiguration _config;

        private static readonly Dictionary<int, string> CAUSE_LABELS = new()
        {
            { 0, "Normal Moving" },
            { 1, "Loading and Unloading" },
            { 2, "Intersection" },
            { 3, "Traffic Light" },
            { 4, "Pedestrian Crossing" },
            { 5, "Animal Crossing" },
            { 6, "Vehicle Crossing" },
            { 7, "Road Construction" },
            { 8, "Blocked by Vehicle" },
            { 9, "Others" }
        };

        public CsvExportService()
        {
            _config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                ShouldQuote = args => true
            };
        }

        // Reverted: Explicitly maps original trip rows to ensure delay reasons and duration are preserved
        public byte[] ExportOriginalTripRowsToCsv(IEnumerable<TripRow> rows)
        {
            if (rows == null || !rows.Any())
                return Encoding.UTF8.GetBytes("No rows.\n");

            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, Encoding.UTF8);
            using var csv = new CsvWriter(writer, _config);

            // Write exact original headers
            csv.WriteField("Timestamp");
            csv.WriteField("Latitude");
            csv.WriteField("Longitude");
            csv.WriteField("SpeedKph");
            csv.WriteField("DistanceDiffMeters");
            csv.WriteField("SecDiff");
            csv.WriteField("Status");
            csv.WriteField("CauseID");
            csv.WriteField("CauseDescription");
            csv.WriteField("DelayDurationSec");
            csv.NextRecord();

            foreach (var r in rows)
            {
                double speed = r.Speed ?? 0.0;
                int causeId = r.CauseID ?? 0;
                bool isDelay = speed < 5.0 || causeId > 0;
                string status = isDelay ? "Delay" : "Moving";
                string causeDesc = CAUSE_LABELS.TryGetValue(causeId, out var desc) ? desc : "Unknown Cause";
                double delaySec = isDelay ? Math.Max(r.secDiff, 0.0) : 0.0;

                csv.WriteField(r.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss") ?? "");
                csv.WriteField(r.SnappedLat.ToString("F7", CultureInfo.InvariantCulture));
                csv.WriteField(r.SnappedLon.ToString("F7", CultureInfo.InvariantCulture));
                csv.WriteField(speed.ToString("F2", CultureInfo.InvariantCulture));
                csv.WriteField(r.distanceDiff.ToString("F2", CultureInfo.InvariantCulture));
                csv.WriteField(r.secDiff.ToString("F2", CultureInfo.InvariantCulture));
                csv.WriteField(status);
                csv.WriteField(causeId);
                csv.WriteField(causeDesc);
                csv.WriteField(delaySec.ToString("F2", CultureInfo.InvariantCulture));
                csv.NextRecord();
            }

            writer.Flush();
            return ms.ToArray();
        }

        public byte[] ExportToCsv<T>(IEnumerable<T> records)
        {
            if (records == null || !records.Any())
                return Encoding.UTF8.GetBytes("No rows.\n");

            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, Encoding.UTF8);
            using var csv = new CsvWriter(writer, _config);

            csv.WriteRecords(records);
            writer.Flush();
            return ms.ToArray();
        }

        public byte[] ExportDictionariesToCsv(IEnumerable<IDictionary<string, object>> rows)
        {
            var list = rows?.ToList() ?? new List<IDictionary<string, object>>();
            if (list.Count == 0) return Encoding.UTF8.GetBytes("No rows.\n");

            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, Encoding.UTF8);
            using var csv = new CsvWriter(writer, _config);

            var headers = list.First().Keys.ToList();
            foreach (var header in headers) csv.WriteField(header);
            csv.NextRecord();

            foreach (var row in list)
            {
                foreach (var header in headers)
                {
                    row.TryGetValue(header, out var val);
                    csv.WriteField(val?.ToString() ?? "");
                }
                csv.NextRecord();
            }

            writer.Flush();
            return ms.ToArray();
        }

        public byte[] ExportStringDictionariesToCsv(IEnumerable<Dictionary<string, string>> rows)
        {
            var list = rows?.ToList() ?? new List<Dictionary<string, string>>();
            if (list.Count == 0) return Encoding.UTF8.GetBytes("No rows.\n");

            var headerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var headers = new List<string>();

            foreach (var row in list)
            {
                foreach (var key in row.Keys)
                {
                    if (headerSet.Add(key)) headers.Add(key);
                }
            }

            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, Encoding.UTF8);
            using var csv = new CsvWriter(writer, _config);

            foreach (var header in headers) csv.WriteField(header);
            csv.NextRecord();

            foreach (var row in list)
            {
                foreach (var header in headers)
                {
                    row.TryGetValue(header, out var val);
                    csv.WriteField(val ?? "");
                }
                csv.NextRecord();
            }

            writer.Flush();
            return ms.ToArray();
        }

        public byte[] BuildDirectionalTableCsvForPeak(List<TripDataset> datasets, string peakCode)
        {
            throw new NotImplementedException("Migrate existing domain calculation here, writing via CsvWriter.");
        }
    }
}