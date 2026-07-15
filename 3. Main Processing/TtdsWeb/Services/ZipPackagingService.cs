using System.IO.Compression;
using System.Text;
using TtdsWeb.Models;
using TtdsWeb.Utils;

namespace TtdsWeb.Services
{
    public interface IZipPackagingService
    {
        void PackageTripsToZip(List<TripDataset> datasets, string zipFilePath);
    }

    public class ZipPackagingService : IZipPackagingService
    {
        private readonly AppState _state;
        private readonly IPeakPeriodService _peakService;
        private readonly IGeoDirectionService _geoService;
        private readonly ITripAnalysisService _analysisService;
        private readonly IGisExportService _gisService;

        public ZipPackagingService(
            AppState state,
            IPeakPeriodService peakService,
            IGeoDirectionService geoService,
            ITripAnalysisService analysisService,
            IGisExportService gisService)
        {
            _state = state;
            _peakService = peakService;
            _geoService = geoService;
            _analysisService = analysisService;
            _gisService = gisService;
        }

        public void PackageTripsToZip(List<TripDataset> datasets, string zipFilePath)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                // Packages original cleaned/snapped files directly into Snapped-Cleaned folder
                AddCleanedDatasetsToZip(zip, datasets, "Snapped-Cleaned");
                AddDirectionalAveragesToZip_ByDate(zip, datasets, "DirectionalAverages");
                AddSegmentAnalysisToZip(zip, datasets, "SegmentAnalysis");
                AddShapesToZip(zip, datasets, "Shapes");
            }

            System.IO.File.WriteAllBytes(zipFilePath, ms.ToArray());
        }

        private void AddCleanedDatasetsToZip(
            ZipArchive zip,
            List<TripDataset> datasets,
            string zipBaseFolder)
        {
            foreach (var d in datasets)
            {
                if (string.IsNullOrWhiteSpace(d.Path) || !System.IO.File.Exists(d.Path))
                    continue;

                var info = ParseTripInfoFromFilename(d.FileName)
                           ?? ParseTripInfoFromFilename(d.Path);
                string entryName;

                if (info != null)
                {
                    var (tripNo, dtToken, _, _, _) = info.Value;
                    var dir = _geoService.ComputeDatasetDirection(d.Rows) ?? "UNK";

                    // Placed directly under the base folder (No AM/MID/PM segregation)
                    entryName = $"{zipBaseFolder}/{tripNo}_{dtToken}-{dir}.csv";
                }
                else
                {
                    var safeFileName = SafeZipFile(Path.GetFileName(d.FileName));
                    entryName = $"{zipBaseFolder}/{safeFileName}";
                }

                var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                using var es = entry.Open();
                using var fs = System.IO.File.OpenRead(d.Path);
                fs.CopyTo(es);
            }
        }

        private void AddDirectionalAveragesToZip_ByDate(
            ZipArchive zip,
            List<TripDataset> datasets,
            string zipBaseFolder)
        {
            var peaks = new[] { "AM", "MID", "PM" };

            foreach (var pk in peaks)
            {
                var bytes = BuildDirectionalTableCsvForPeak(datasets, pk);
                if (bytes.Length == 0) continue;

                var entry = zip.CreateEntry(
                    $"{zipBaseFolder}/{pk}.csv",
                    CompressionLevel.Fastest
                );

                using var es = entry.Open();
                es.Write(bytes, 0, bytes.Length);
            }
        }

        private void AddSegmentAnalysisToZip(
            ZipArchive zip,
            List<TripDataset> datasets,
            string zipBaseFolder)
        {
            foreach (var d in datasets)
            {
                var info = ParseTripInfoFromFilename(d.FileName)
                           ?? ParseTripInfoFromFilename(d.Path);
                if (info == null) continue;

                var (tripNo, dtToken, date, vehCode, vehName) = info.Value;

                var peak = _peakService.PeakFolder(ComputeDatasetPeak(d.Rows).ToString());
                var dir = _geoService.ComputeDatasetDirection(d.Rows) ?? "UNK";

                var anchors = GetActiveAnchorsForTrip(d.Rows);
                anchors = MergeAnchorsInTripOrder(d.Rows, anchors, _state.ManualCpKm);
                if (anchors.Count < 2) continue;

                var (results, _, _) = _analysisService.AnalyzeTrip(d.Rows, anchors);
                var csvBytes = BuildResultsCsv(results);

                var entry = zip.CreateEntry(
                    $"{zipBaseFolder}/{peak}/{tripNo}_{dtToken}-{dir}.csv",
                    CompressionLevel.Fastest
                );

                using var es = entry.Open();
                es.Write(csvBytes, 0, csvBytes.Length);
            }
        }

        private void AddShapesToZip(
            ZipArchive zip,
            List<TripDataset> datasets,
            string zipBaseFolder)
        {
            foreach (var d in datasets)
            {
                var info = ParseTripInfoFromFilename(d.FileName)
                           ?? ParseTripInfoFromFilename(d.Path);
                if (info == null) continue;

                var (tripNo, dtToken, date, vehCode, vehName) = info.Value;

                var peak = _peakService.PeakFolder(ComputeDatasetPeak(d.Rows).ToString());
                var dir = _geoService.ComputeDatasetDirection(d.Rows) ?? "UNK";

                var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);

                try
                {
                    var baseName = $"{tripNo}_{dtToken}-{dir}";
                    var del = _gisService.WriteDelayLinesShapeFile(d, tmp, baseName + "_delays");
                    var pts = _gisService.WriteTripPointsShapeFile(d, tmp, baseName + "_points");

                    AddShapeSidecarsToZip(zip, del, $"{zipBaseFolder}/shp/{peak}");
                    AddShapeSidecarsToZip(zip, pts, $"{zipBaseFolder}/shp/{peak}");
                }
                finally
                {
                    try { Directory.Delete(tmp, true); } catch { }
                }
            }
        }

        private static void AddShapeSidecarsToZip(ZipArchive zip, string shpFile, string zipFolder)
        {
            if (string.IsNullOrWhiteSpace(shpFile)) return;
            if (!System.IO.File.Exists(shpFile)) return;

            zipFolder = SafeZipPath(zipFolder);

            var baseNoExt = Path.Combine(Path.GetDirectoryName(shpFile)!, Path.GetFileNameWithoutExtension(shpFile));
            var exts = new[] { ".shp", ".shx", ".dbf", ".prj", ".cpg" };

            foreach (var ext in exts)
            {
                var fp = baseNoExt + ext;
                if (!System.IO.File.Exists(fp)) continue;

                var entryName = $"{zipFolder}/{Path.GetFileName(fp)}";
                var e = zip.CreateEntry(entryName, CompressionLevel.Fastest);

                using var es = e.Open();
                using var fs = System.IO.File.OpenRead(fp);
                fs.CopyTo(es);
            }
        }

        private byte[] BuildDirectionalTableCsvForPeak(List<TripDataset> datasets, string peakCode)
        {
            peakCode = (peakCode ?? "").Trim().ToUpperInvariant();
            if (peakCode != "AM" && peakCode != "MID" && peakCode != "PM")
                return Array.Empty<byte>();

            var dsPeak = datasets
                .Where(d => ComputeDatasetPeak(d.Rows).ToString().Equals(peakCode, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!dsPeak.Any())
                return Array.Empty<byte>();

            var dirOrder = new[] { "NB", "SB", "EB", "WB", "UNKNOWN" };
            var sb = new StringBuilder();

            sb.AppendLine($"{peakCode} Directional Averages");
            sb.Append("Metric");
            foreach (var d in dirOrder) sb.Append(',').Append(GetDirectionLabel(d));
            sb.AppendLine(",Units");

            // Add summary rows
            sb.Append("Avg Travel Time");
            foreach (var dir in dirOrder)
            {
                var dirDatasets = dsPeak
                    .Where(d => (_geoService.ComputeDatasetDirection(d.Rows) ?? "Unknown")
                        .Equals(dir, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var summaries = new List<AnalysisSummary>();
                foreach (var d in dirDatasets)
                {
                    var anchors = GetActiveAnchorsForTrip(d.Rows);
                    anchors = MergeAnchorsInTripOrder(d.Rows, anchors, _state.ManualCpKm);
                    if (anchors.Count < 2) continue;

                    var (_, _, summary) = _analysisService.AnalyzeTrip(d.Rows, anchors);
                    if (summary != null) summaries.Add(summary);
                }

                if (summaries.Any())
                {
                    var avg = summaries.Average(s => s.TotalTravelTimeMin);
                    sb.Append(',').Append(_peakService.FormatMinToHHMMSS(avg));
                }
                else
                {
                    sb.Append(",");
                }
            }
            sb.AppendLine(",hh:mm:ss");

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private byte[] BuildResultsCsv(List<SegmentResult> rows)
        {
            var sb = new StringBuilder();

            sb.AppendLine(string.Join(",",
                "From", "To", "StartTime", "EndTime",
                "TravelTimeSec", "TravelTimeMin",
                "DistanceM", "TravelSpeedKph", "RunningSpeedKph",
                "Delays", "DelayLengthM", "DelayCauses", "Note"));

            foreach (var r in rows)
            {
                static string Q(object? v)
                {
                    if (v == null) return "";
                    var s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
                        s = "\"" + s.Replace("\"", "\"\"") + "\"";
                    return s;
                }

                sb.AppendLine(string.Join(",",
                    Q(r.From),
                    Q(r.To),
                    Q(r.StartTime),
                    Q(r.EndTime),
                    Q(r.TravelTimeSec),
                    Q(r.TravelTimeMin),
                    Q(r.DistanceM),
                    Q(r.TravelSpeedKph),
                    Q(r.RunningSpeedKph),
                    Q(r.Delays),
                    Q(r.DelayLengthM),
                    Q(r.DelayCauses),
                    Q(r.Note)
                ));
            }
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static string GetDirectionLabel(string code) => code switch
        {
            "NB" => "Northbound",
            "SB" => "Southbound",
            "EB" => "Eastbound",
            "WB" => "Westbound",
            _ => "Unknown"
        };

        private static string RebaseGraphFolder(string folderIn, string regionSafe, string roadSafe)
        {
            if (string.IsNullOrWhiteSpace(folderIn))
                return $"{regionSafe}/{roadSafe}";

            var parts = folderIn
                .Replace("\\", "/")
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            if (parts.Count >= 4
                && parts[0].Equals(regionSafe, StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals(roadSafe, StringComparison.OrdinalIgnoreCase)
                && parts[2].Equals("UnknownRegion", StringComparison.OrdinalIgnoreCase)
                && parts[3].Equals("UnknownRoad", StringComparison.OrdinalIgnoreCase))
            {
                parts.RemoveRange(2, 2);
                return string.Join("/", parts);
            }

            if (parts.Count >= 2)
            {
                var rest = string.Join("/", parts.Skip(2));
                return string.IsNullOrWhiteSpace(rest)
                    ? $"{regionSafe}/{roadSafe}"
                    : $"{regionSafe}/{roadSafe}/{rest}";
            }

            return $"{regionSafe}/{roadSafe}/{parts[0]}";
        }

        private static string CanonVehicleFolder(string? vehName)
        {
            return SafePathPart(vehName ?? "UnknownVehicle").Replace(" ", "");
        }

        private static string SafeZipPath(string? p)
        {
            p = (p ?? "").Replace("\\", "/").Trim();
            p = p.Trim('/');
            p = p.Replace("..", "_");
            return p;
        }

        private static string SafeZipFile(string? f)
        {
            f = (f ?? "").Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
                f = f.Replace(c, '_');
            if (string.IsNullOrWhiteSpace(f)) f = "file.bin";
            return f;
        }

        private static string SafePathPart(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Unknown";
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            s = s.Trim().Trim('.');
            return string.IsNullOrWhiteSpace(s) ? "Unknown" : s;
        }

        private static string ZipRoot(string region, string road, string date)
            => $"{SafePathPart(region)}/{SafePathPart(road)}/{SafePathPart(date)}";

        // Helper methods for stub implementations
        private PeakPeriod ComputeDatasetPeak(List<TripRow> rows)
        {
            var t = rows.Select(r => r.Timestamp).FirstOrDefault(x => x.HasValue);
            if (!t.HasValue) return PeakPeriod.OFF;
            return _peakService.GetPeakPeriod(t.Value);
        }

        private List<ControlPoint> GetActiveAnchorsForTrip(List<TripRow> df)
        {
            var mode = (_state.AnchorSource ?? "cp");
            if (mode == "km")
            {
                return _state.KmGeneratedPoints
                    .Concat(_state.ManualCpKm)
                    .GroupBy(a => a.ControlPointId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
            }
            return _state.ControlPoints.ToList();
        }

        private List<ControlPoint> MergeAnchorsInTripOrder(List<TripRow> df, List<ControlPoint> baseAnchors, List<ControlPoint> extra)
        {
            if (extra == null || extra.Count == 0) return baseAnchors;

            var tripPts = df.Select((r, i) => (r.SnappedLat, r.SnappedLon, i)).ToList();

            int NearestIdx(ControlPoint cp)
            {
                int bestIdx = 0;
                double best = double.MaxValue;
                for (int i = 0; i < tripPts.Count; i++)
                {
                    var d = Geo.DistanceMeters(cp.Lat, cp.Lng, tripPts[i].SnappedLat, tripPts[i].SnappedLon);
                    if (d < best) { best = d; bestIdx = i; }
                }
                return bestIdx;
            }

            return baseAnchors
                .Concat(extra)
                .GroupBy(a => a.ControlPointId)
                .Select(g => g.First())
                .Select(a => new { a, idx = NearestIdx(a) })
                .OrderBy(x => x.idx)
                .Select(x => x.a)
                .ToList();
        }

        private static (string tripNo, string dtToken, string date, string vehCode, string vehName)?
            ParseTripInfoFromFilename(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            // Restored the missing backslash \b in \bGPX_
            var mv = System.Text.RegularExpressions.Regex.Match(
                name,
                @"\bGPX_(\d+)_",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            var vehCode = mv.Success ? mv.Groups[1].Value : "";
            var vehName = VehicleNameFromCode(vehCode);

            var mt = System.Text.RegularExpressions.Regex.Match(
                name,
                @"-(\d+)_((\d{8})-(\d{6}))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            if (!mt.Success) return null;

            var tripNo = mt.Groups[1].Value;
            var dtToken = mt.Groups[2].Value;
            var date = mt.Groups[3].Value;

            return (tripNo, dtToken, date, vehCode, vehName);
        }

        private static string VehicleNameFromCode(string? code) => (code ?? "").Trim() switch
        {
            "1" => "Private Car",
            "2" => "UV",
            "3" => "Jeepney",
            "4" => "Bus",
            _ => "UnknownVehicle"
        };
    }
}