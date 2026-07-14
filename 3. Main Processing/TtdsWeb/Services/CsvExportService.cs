using CsvHelper;
using CsvHelper.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TtdsWeb.Models;
using TtdsWeb.Utils;

namespace TtdsWeb.Services
{
    public interface ICsvExportService
    {
        byte[] ExportOriginalTripRowsToCsv(IEnumerable<TripRow> rows);
        byte[] ExportToCsv<T>(IEnumerable<T> records);
        byte[] ExportDictionariesToCsv(IEnumerable<IDictionary<string, object>> rows);
        byte[] ExportStringDictionariesToCsv(IEnumerable<Dictionary<string, string>> rows);
        byte[] BuildResultsCsv(List<SegmentResult> rows);
        byte[] BuildAnchorsCsv(IEnumerable<ControlPoint> anchors);
        byte[] BuildDirectionalAveragesCsv(List<TripDataset> datasets);
        byte[] BuildDirectionalAverages_ThreeTablesCsv(IEnumerable<TripDataset> datasets);
        byte[] BuildDirectionalTableCsvForPeak(List<TripDataset> datasets, string peakCode);
    }

    public class CsvExportService : ICsvExportService
    {
        private readonly CsvConfiguration _config;
        private readonly AppState? _state;
        private readonly ITripAnalysisService _analysisService;

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

        public CsvExportService(IAppStateAccessor? appState = null, ITripAnalysisService? analysisService = null)
        {
            _state = appState?.Current;
            _analysisService = analysisService;
            _config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                ShouldQuote = args => true
            };
        }

        public byte[] ExportOriginalTripRowsToCsv(IEnumerable<TripRow> rows)
        {
            if (rows == null || !rows.Any())
                return Encoding.UTF8.GetBytes("No rows.\n");

            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, Encoding.UTF8);
            using var csv = new CsvWriter(writer, _config);

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

        public byte[] BuildResultsCsv(List<SegmentResult> rows)
        {
            if (rows == null || !rows.Any())
                return Encoding.UTF8.GetBytes("No rows.\n");

            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, Encoding.UTF8);
            using var csv = new CsvWriter(writer, _config);

            csv.WriteField("From");
            csv.WriteField("To");
            csv.WriteField("StartTime");
            csv.WriteField("EndTime");
            csv.WriteField("TravelTimeSec");
            csv.WriteField("TravelTimeMin");
            csv.WriteField("DistanceM");
            csv.WriteField("TravelSpeedKph");
            csv.WriteField("RunningSpeedKph");
            csv.WriteField("Delays");
            csv.WriteField("DelayLengthM");
            csv.WriteField("DelayCauses");
            csv.WriteField("Note");
            csv.NextRecord();

            foreach (var r in rows)
            {
                csv.WriteField(r.From ?? "");
                csv.WriteField(r.To ?? "");
                csv.WriteField(r.StartTime ?? "");
                csv.WriteField(r.EndTime ?? "");
                csv.WriteField(r.TravelTimeSec);
                csv.WriteField(r.TravelTimeMin);
                csv.WriteField(r.DistanceM);
                csv.WriteField(r.TravelSpeedKph);
                csv.WriteField(r.RunningSpeedKph);
                csv.WriteField(r.Delays);
                csv.WriteField(r.DelayLengthM);
                csv.WriteField(r.DelayCauses ?? "");
                csv.WriteField(r.Note ?? "");
                csv.NextRecord();
            }

            writer.Flush();
            return ms.ToArray();
        }

        public byte[] BuildAnchorsCsv(IEnumerable<ControlPoint> anchors)
        {
            if (anchors == null || !anchors.Any())
                return Encoding.UTF8.GetBytes("No rows.\n");

            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, Encoding.UTF8);
            using var csv = new CsvWriter(writer, _config);

            csv.WriteField("ControlPoint");
            csv.WriteField("Latitude");
            csv.WriteField("Longitude");
            csv.NextRecord();

            foreach (var cp in anchors)
            {
                csv.WriteField(cp.ControlPointId ?? "");
                csv.WriteField(cp.Lat.ToString("F7", CultureInfo.InvariantCulture));
                csv.WriteField(cp.Lng.ToString("F7", CultureInfo.InvariantCulture));
                csv.NextRecord();
            }

            writer.Flush();
            return ms.ToArray();
        }

        public byte[] BuildDirectionalAveragesCsv(List<TripDataset> datasets)
        {
            EnsureAnalysisServiceAvailable();
            if (datasets == null || !datasets.Any()) return Array.Empty<byte>();

            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, Encoding.UTF8);
            using var csv = new CsvWriter(writer, _config);

            csv.WriteField("Direction");
            csv.WriteField("AvgTravelTime(min)");
            csv.WriteField("AvgDistance(km)");
            csv.WriteField("AvgTravelSpeed(kph)");
            csv.WriteField("AvgRunningSpeed(kph)");
            csv.WriteField("AvgDelay(min)");
            csv.WriteField("AvgDelayLength(km)");
            csv.NextRecord();

            foreach (var grp in datasets.GroupBy(d => _analysisService!.ComputeDatasetDirection(d.Rows)))
            {
                var allSegs = new List<SegmentResult>();

                foreach (var d in grp)
                {
                    var anchors = _analysisService!.GetActiveAnchorsForTrip(d.Rows);
                    anchors = _analysisService.MergeAnchorsInTripOrder(d.Rows, anchors, _state?.ManualCpKm);

                    if (anchors.Count < 2) continue;

                    var (results, _, _) = _analysisService.AnalyzeTrip(d.Rows, anchors);
                    if (results != null && results.Any())
                        allSegs.AddRange(results);
                }

                if (!allSegs.Any()) continue;

                double avgTravelMin = allSegs.Average(x => (x.TravelTimeSec ?? 0) / 60.0);
                double avgDistKm = allSegs.Average(x => (x.DistanceM ?? 0) / 1000.0);
                double avgTravelKph = allSegs.Average(x => x.TravelSpeedKph ?? 0);
                double avgRunKph = allSegs.Average(x => x.RunningSpeedKph ?? 0);
                double avgDelayMin = allSegs.Average(x => (x.Delays ?? 0) / 60.0);
                double avgDelayKm = allSegs.Average(x => (x.DelayLengthM ?? 0) / 1000.0);

                csv.WriteField(SafePathPart(grp.Key));
                csv.WriteField(avgTravelMin.ToString("0.##", CultureInfo.InvariantCulture));
                csv.WriteField(avgDistKm.ToString("0.###", CultureInfo.InvariantCulture));
                csv.WriteField(avgTravelKph.ToString("0.##", CultureInfo.InvariantCulture));
                csv.WriteField(avgRunKph.ToString("0.##", CultureInfo.InvariantCulture));
                csv.WriteField(avgDelayMin.ToString("0.##", CultureInfo.InvariantCulture));
                csv.WriteField(avgDelayKm.ToString("0.###", CultureInfo.InvariantCulture));
                csv.NextRecord();
            }

            writer.Flush();
            return ms.ToArray();
        }

        public byte[] BuildDirectionalAverages_ThreeTablesCsv(IEnumerable<TripDataset> datasets)
        {
            EnsureAnalysisServiceAvailable();
            var dsList = datasets?.ToList() ?? new List<TripDataset>();
            if (!dsList.Any()) return Array.Empty<byte>();

            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, Encoding.UTF8);
            using var csv = new CsvWriter(writer, _config);

            var peakOrder = new[] { "AM", "MID", "PM" };
            var dirOrder = new[] { "SB", "NB", "EB", "WB", "UNKNOWN" };

            foreach (var peak in peakOrder)
            {
                var peakDatasets = dsList
                    .Where(d => (_analysisService!.ComputeDatasetPeak(d.Rows)?.ToString() ?? "").Equals(peak, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (!peakDatasets.Any()) continue;

                csv.WriteField($"{peak} DIRECTIONAL AVERAGES");
                csv.NextRecord();
                csv.WriteField("Direction");
                csv.WriteField("AvgTravelTimeMin");
                csv.WriteField("AvgDistanceKm");
                csv.WriteField("AvgTravelSpeedKph");
                csv.WriteField("AvgRunningSpeedKph");
                csv.WriteField("AvgDelayMin");
                csv.WriteField("AvgDelayLengthKm");
                csv.NextRecord();

                foreach (var dir in dirOrder)
                {
                    var dirDatasets = peakDatasets
                        .Where(d => (_analysisService!.ComputeDatasetDirection(d.Rows) ?? "Unknown")
                        .Equals(dir, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (!dirDatasets.Any()) continue;

                    var summaries = new List<AnalysisSummary>();

                    foreach (var d in dirDatasets)
                    {
                        var anchors = _analysisService!.GetActiveAnchorsForTrip(d.Rows);
                        anchors = _analysisService.MergeAnchorsInTripOrder(d.Rows, anchors, _state?.ManualCpKm);

                        if (anchors.Count < 2) continue;

                        var (_, _, sum) = _analysisService.AnalyzeTrip(d.Rows, anchors);
                        if (sum != null) summaries.Add(sum);
                    }

                    if (!summaries.Any()) continue;

                    csv.WriteField(dir == "UNKNOWN" ? "Unknown" : dir);
                    csv.WriteField(summaries.Average(x => x.TotalTravelTimeMin).ToString("0.##", CultureInfo.InvariantCulture));
                    csv.WriteField(summaries.Average(x => x.TotalDistanceKm).ToString("0.###", CultureInfo.InvariantCulture));
                    csv.WriteField(summaries.Average(x => x.AvgTravelSpeed).ToString("0.##", CultureInfo.InvariantCulture));
                    csv.WriteField(summaries.Average(x => x.AvgRunningSpeed).ToString("0.##", CultureInfo.InvariantCulture));
                    csv.WriteField(summaries.Average(x => x.TotalDelayMin).ToString("0.##", CultureInfo.InvariantCulture));
                    csv.WriteField((summaries.Average(x => x.TotalDelayLength) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
                    csv.NextRecord();
                }

                csv.NextRecord(); // Blank row between tables
            }

            writer.Flush();
            return ms.ToArray();
        }

        public byte[] BuildDirectionalTableCsvForPeak(List<TripDataset> datasets, string peakCode)
        {
            EnsureAnalysisServiceAvailable();
            peakCode = (peakCode ?? "").Trim().ToUpperInvariant();
            if (peakCode != "AM" && peakCode != "MID" && peakCode != "PM")
                return Array.Empty<byte>();

            var dsPeak = datasets
                .Where(d => (_analysisService!.ComputeDatasetPeak(d.Rows)?.ToString() ?? "").Equals(peakCode, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!dsPeak.Any())
                return Array.Empty<byte>();

            var dirOrder = new[] { "NB", "SB", "EB", "WB", "UNKNOWN" };
            var perDir = new Dictionary<string, List<AnalysisSummary>>(StringComparer.OrdinalIgnoreCase);

            foreach (var d in dsPeak)
            {
                var dir = (_analysisService!.ComputeDatasetDirection(d.Rows) ?? "Unknown").Trim().ToUpperInvariant();
                if (dir == "") dir = "UNKNOWN";
                if (!dirOrder.Contains(dir)) dir = "UNKNOWN";

                var anchors = _analysisService.GetActiveAnchorsForTrip(d.Rows);
                anchors = _analysisService.MergeAnchorsInTripOrder(d.Rows, anchors, _state?.ManualCpKm);
                if (anchors.Count < 2) continue;

                var (_, _, sum) = _analysisService.AnalyzeTrip(d.Rows, anchors);
                if (sum != null)
                {
                    if (!perDir.TryGetValue(dir, out var list))
                    {
                        list = new List<AnalysisSummary>();
                        perDir[dir] = list;
                    }
                    list.Add(sum);
                }
            }

            var presentDirs = dirOrder.Where(d => perDir.ContainsKey(d) && perDir[d].Count > 0).ToList();
            if (presentDirs.Count == 0) return Array.Empty<byte>();

            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, Encoding.UTF8);
            using var csv = new CsvWriter(writer, _config);

            // Title Line
            csv.WriteField($"{peakCode} Directional Averages");
            csv.NextRecord();

            // Headers
            csv.WriteField("Metric");
            foreach (var d in presentDirs)
            {
                csv.WriteField(DirFull(d));
            }
            csv.WriteField("Units");
            csv.NextRecord();

            void WriteRow(string metric, Func<AnalysisSummary, string> selector, string units)
            {
                csv.WriteField(metric);
                foreach (var d in presentDirs)
                {
                    var avg = _analysisService!.Aggregate_MethodA(perDir[d]);
                    csv.WriteField(avg != null ? selector(avg) : "");
                }
                csv.WriteField(units);
                csv.NextRecord();
            }

            WriteRow("Avg Travel Time", a => FormatMinToHHMMSS(a.TotalTravelTimeMin), "hh:mm:ss");
            WriteRow("Avg Distance", a => a.TotalDistanceKm.ToString("0.##", CultureInfo.InvariantCulture), "km");
            WriteRow("Avg Travel Speed", a => a.AvgTravelSpeed.ToString("0.##", CultureInfo.InvariantCulture), "kph");
            WriteRow("Avg Running Speed", a => a.AvgRunningSpeed.ToString("0.##", CultureInfo.InvariantCulture), "kph");
            WriteRow("Avg Delay Time", a => FormatMinToHHMMSS(a.TotalDelayMin), "hh:mm:ss");
            WriteRow("Avg Delay Length", a => (a.TotalDelayLength / 1000.0).ToString("0.##", CultureInfo.InvariantCulture), "km");

            writer.Flush();
            return ms.ToArray();
        }

        private void EnsureAnalysisServiceAvailable()
        {
            if (_analysisService == null)
                throw new InvalidOperationException("ITripAnalysisService must be injected into CsvExportService to generate analytical directional CSV tables.");
        }

        private static string DirFull(string d) => d switch
        {
            "NB" => "Northbound",
            "SB" => "Southbound",
            "EB" => "Eastbound",
            "WB" => "Westbound",
            _ => "Unknown"
        };

        private static string FormatMinToHHMMSS(double minutes)
        {
            if (double.IsNaN(minutes) || double.IsInfinity(minutes) || minutes < 0) return "00:00:00";
            var ts = TimeSpan.FromMinutes(minutes);
            return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        }

        private static string SafePathPart(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Unknown";
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                s = s.Replace(c, '_');
            }
            return s.Trim();
        }
    }
}