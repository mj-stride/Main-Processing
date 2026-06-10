using ClosedXML.Excel;
using CsvHelper;
using DocumentFormat.OpenXml.Bibliography;
using DocumentFormat.OpenXml.Drawing.Charts;
using DocumentFormat.OpenXml.Drawing.Diagrams;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using NetTopologySuite;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Triangulate.Tri;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TtdsWeb.Models;
using TtdsWeb.Services;   // AppState
using TtdsWeb.Utils;
using static System.Runtime.InteropServices.JavaScript.JSType;


namespace TtdsWeb.Controllers
{
    public class HomeController : Controller
    {
        private readonly AppState _state;
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private const double CP_DETECT_RADIUS_M = 300.0;

        public HomeController(AppState state, IConfiguration config, IWebHostEnvironment env)
        {
            _state = state;
            _config = config;
            _env = env;
        }
        private List<ControlPoint> BuildKmAnchorsForRows(List<TripRow> df)
        {
            var dbPath = ResolveKmDbPath();
            if (!System.IO.File.Exists(dbPath)) return new List<ControlPoint>();

            IEnumerable<string>? roads = null;
            if (_state.KmRoads?.Count > 0) roads = _state.KmRoads;
            else if (!string.IsNullOrWhiteSpace(_state.KmRoad)) roads = SplitCsv(_state.KmRoad);

            // bbox-based query for THIS dataset
            var kmPosts = LoadKmPostsForTrip(df, dbPath, _state.KmRegion, roads, bufferMeters: 3000.0);
            if (kmPosts.Count < 2) return new List<ControlPoint>();

            var kmAnchors = BuildKmAnchorsForTrip(df, kmPosts);
            if (kmAnchors.Count < 2) return new List<ControlPoint>();

            // keep only those actually visited (fallback to full if too few)
            var filtered = FilterAnchorsToVisited(df, kmAnchors, CP_DETECT_RADIUS_M, CP_DETECT_RADIUS_M);
            return (filtered.Count >= 2) ? filtered : kmAnchors;
        }

        private static string PeakFolder(string? peakCode)
        {
            peakCode = (peakCode ?? "").Trim().ToUpperInvariant();
            return peakCode switch
            {
                "AM" => "AM",
                "MID" => "MID",
                "PM" => "PM",
                _ => "OFF"
            };
        }
        [HttpGet("/download_detected_cp")]
        public IActionResult DownloadDetectedCp(string format = "csv")
        {
            if (!_state.Datasets.Any())
                return BadRequest("No uploaded trip data.");

            var allAnchors = new List<ControlPoint>();

            foreach (var ds in _state.Datasets)
            {
                var anchors = GetActiveAnchorsForTrip(ds.Rows);

                if (anchors != null && anchors.Count > 0)
                    allAnchors.AddRange(anchors);
            }

            var uniqueAnchors = allAnchors
                .GroupBy(a => a.ControlPointId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (uniqueAnchors.Count == 0)
                return BadRequest("No detected anchor/control points found.");

            if ((format ?? "").ToLowerInvariant() == "geojson")
            {
                var geojsonBytes = BuildAnchorsGeoJson(uniqueAnchors);
                return File(
                    geojsonBytes,
                    "application/geo+json",
                    "detected_anchor_cp.geojson"
                );
            }

            var csvBytes = BuildAnchorsCsv(uniqueAnchors);
            return File(
                csvBytes,
                "text/csv",
                "detected_anchor_cp.csv"
            );
        }
        [IgnoreAntiforgeryToken]
        [HttpPost("/reset_session")]
        public IActionResult ResetSession()
        {
            _state.Datasets.Clear();
            _state.ControlPoints.Clear();
            _state.ManualCpKm.Clear();
            _state.KmGeneratedPoints.Clear();

            _state.LastTripPath = null;
            _state.AnchorSource = "cp";
            _state.KmRegion = null;
            _state.KmRoad = null;
            _state.KmRoads.Clear();

            return RedirectToAction("Index");
        }
        private static AnalysisSummary Aggregate_MethodA(IEnumerable<AnalysisSummary> sums)
        {
            var list = sums?.ToList() ?? new List<AnalysisSummary>();
            if (list.Count == 0) return new AnalysisSummary();

            // These are already "per trip totals" from AnalyzeTrip summary
            double totalTravelMin = list.Sum(x => x.TotalTravelTimeMin);
            double totalDistKm = list.Sum(x => x.TotalDistanceKm);
            double totalDelayMin = list.Sum(x => x.TotalDelayMin);

            // Your TotalDelayLength is in meters in AnalyzeTrip summary
            double totalDelayLenM = list.Sum(x => x.TotalDelayLength);

            double avgTravelKph = totalTravelMin > 0
                ? totalDistKm / (totalTravelMin / 60.0)
                : 0.0;

            double runMin = totalTravelMin - totalDelayMin;
            double avgRunKph = runMin > 0
                ? totalDistKm / (runMin / 60.0)
                : 0.0;

            return new AnalysisSummary
            {
                TotalTravelTimeMin = totalTravelMin,
                TotalDistanceKm = totalDistKm,
                TotalDelayMin = totalDelayMin,
                TotalDelayLength = totalDelayLenM,
                AvgTravelSpeed = avgTravelKph,
                AvgRunningSpeed = avgRunKph
            };
        }
        private static string RebaseGraphFolder(string folderIn, string regionSafe, string roadSafe)
        {
            if (string.IsNullOrWhiteSpace(folderIn))
                return $"{regionSafe}/{roadSafe}";

            var parts = folderIn
                .Replace("\\", "/")
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            // If it already begins with correct Region/Road, keep it but remove the accidental UnknownRegion/UnknownRoad right after
            if (parts.Count >= 4
                && parts[0].Equals(regionSafe, StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals(roadSafe, StringComparison.OrdinalIgnoreCase)
                && parts[2].Equals("UnknownRegion", StringComparison.OrdinalIgnoreCase)
                && parts[3].Equals("UnknownRoad", StringComparison.OrdinalIgnoreCase))
            {
                parts.RemoveRange(2, 2); // remove UnknownRegion/UnknownRoad
                return string.Join("/", parts);
            }

            // Otherwise, FORCE the first two segments to be regionSafe/roadSafe
            // Example: "UnknownRegion/UnknownRoad/20250715/Bus/Graphs/AM"
            //      -> "NIR/Bacolod North Rd/20250715/Bus/Graphs/AM"
            if (parts.Count >= 2)
            {
                var rest = string.Join("/", parts.Skip(2));
                return string.IsNullOrWhiteSpace(rest)
                    ? $"{regionSafe}/{roadSafe}"
                    : $"{regionSafe}/{roadSafe}/{rest}";
            }

            // Only 1 segment? Just rebase.
            return $"{regionSafe}/{roadSafe}/{parts[0]}";
        }
        private static string CanonVehicleFolder(string? vehName)
        {
            // pick ONE style; here: no spaces
            return SafePathPart(vehName ?? "UnknownVehicle").Replace(" ", "");
        }
        private static byte[] BuildAnchorsGeoJson(IEnumerable<ControlPoint> anchors)
        {
            var features = anchors.Select(cp => $@"
                {{
                  ""type"": ""Feature"",
                  ""properties"": {{
                    ""id"": ""{cp.ControlPointId}""
                  }},
                  ""geometry"": {{
                    ""type"": ""Point"",
                    ""coordinates"": [
                      {cp.Lng.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                      {cp.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}
                    ]
                  }}
                }}");

                        var geojson = $@"
            {{
              ""type"": ""FeatureCollection"",
              ""features"": [
                {string.Join(",", features)}
              ]
            }}";

                        return Encoding.UTF8.GetBytes(geojson);
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

        private static byte[] BuildAnchorsCsv(IEnumerable<ControlPoint> anchors)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ControlPoint,Latitude,Longitude");

            foreach (var cp in anchors)
            {
                sb.AppendLine(string.Join(",",
                    $"\"{cp.ControlPointId.Replace("\"", "\"\"")}\"",
                    cp.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    cp.Lng.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ));
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
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

                        var peak = PeakFolder(ComputeDatasetPeak(d.Rows).ToString());
                        var dir = ComputeDatasetDirection(d.Rows) ?? "UNK";

                        var anchors = GetActiveAnchorsForTrip(d.Rows);
                        anchors = MergeAnchorsInTripOrder(d.Rows, anchors, _state.ManualCpKm);
                        if (anchors.Count < 2) continue;

                        var (results, _, _) = AnalyzeTrip(d.Rows, anchors);
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

                    var peak = PeakFolder(ComputeDatasetPeak(d.Rows).ToString());
                    var dir = ComputeDatasetDirection(d.Rows) ?? "UNK";

                    var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tmp);

                    try
                    {
                        var baseName = $"{tripNo}_{dtToken}-{dir}";
                        var del = WriteDelayLinesShapeFile(d, tmp, baseName + "_delays");
                        var pts = WriteTripPointsShapeFile(d, tmp, baseName + "_points");

                        AddShapeSidecarsToZip(zip, del, $"{zipBaseFolder}/shp/{peak}");
                        AddShapeSidecarsToZip(zip, pts, $"{zipBaseFolder}/shp/{peak}");
                    }
                    finally
                    {
                        try { Directory.Delete(tmp, true); } catch { }
                    }
                }
            }

        private static byte[] BuildOriginalCsvFromRows(IEnumerable<IDictionary<string, object>> rows)
        {
            var list = rows?.ToList() ?? new List<IDictionary<string, object>>();
            if (list.Count == 0) return Encoding.UTF8.GetBytes("");

            var headers = list.First().Keys.ToList();

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", headers.Select(EscapeCsv)));

            foreach (var r in list)
            {
                sb.AppendLine(string.Join(",", headers.Select(h =>
                {
                    r.TryGetValue(h, out var v);
                    return EscapeCsv(v?.ToString() ?? "");
                })));
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        public class GraphZipRequest
        {
            public string? Region { get; set; }
            public string? RoadNameOrSections { get; set; }
            public List<GraphZipItem> Items { get; set; } = new();
        }

        public class GraphZipItem
        {
            public string Folder { get; set; } = "";
            public string FileName { get; set; } = "";
            public string DataUrl { get; set; } = "";
        }

        private static string EscapeCsv(string s)
        {
            if (s == null) return "";
            if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        // ---------- Centralized upload folder ----------
        private string UploadRoot
        {
            get
            {
                var root = _state.UploadFolder;
                if (string.IsNullOrWhiteSpace(root))
                {
                    var baseDir = _env?.ContentRootPath ?? AppContext.BaseDirectory ?? Directory.GetCurrentDirectory();
                    root = Path.Combine(baseDir, "uploads");
                }
                Directory.CreateDirectory(root);
                return root;
            }
        }

        private enum PeakPeriod
        {
            AM,
            MID,
            PM,
            OFF
        }

        private static PeakPeriod GetPeakPeriod(DateTime dt)
        {
            var t = dt.TimeOfDay;

            // AM peak: 06:00 onwards (interpret as 06:00–10:29:59)
            var amStart = new TimeSpan(6, 0, 0);
            var amEnd = new TimeSpan(10, 29, 59);

            // MID peak: 10:30–15:00
            var midStart = new TimeSpan(10, 30, 0);
            var midEnd = new TimeSpan(15, 0, 0);

            // PM peak: 15:30–19:30
            var pmStart = new TimeSpan(15, 30, 0);
            var pmEnd = new TimeSpan(19, 30, 0);

            if (t >= amStart && t <= amEnd) return PeakPeriod.AM;
            if (t >= midStart && t <= midEnd) return PeakPeriod.MID;
            if (t >= pmStart && t <= pmEnd) return PeakPeriod.PM;

            return PeakPeriod.OFF;
        }

        private static string PeakLabel(PeakPeriod p) => p switch
        {
            PeakPeriod.AM => "AM Peak (07:00–10:00)",
            PeakPeriod.MID => "Mid Peak (11:00–14:00)",
            PeakPeriod.PM => "PM Peak (16:00–19:00)",
            _ => "Off-Peak"
        };

        // dataset-level classification: use first valid timestamp (or median if you prefer)
        private static PeakPeriod ComputeDatasetPeak(List<TripRow> rows)
        {
            var t = rows.Select(r => r.Timestamp).FirstOrDefault(x => x.HasValue);
            if (!t.HasValue) return PeakPeriod.OFF;
            return GetPeakPeriod(t.Value);
        }


        // ---------------- Direction helpers ----------------
        private static double ToRad(double d) => d * Math.PI / 180.0;
        private static double ToDeg(double r) => r * 180.0 / Math.PI;

        private static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            var phi1 = ToRad(lat1);
            var phi2 = ToRad(lat2);
            var dLam = ToRad(lon2 - lon1);
            var y = Math.Sin(dLam) * Math.Cos(phi2);
            var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLam);
            var theta = Math.Atan2(y, x);
            return (ToDeg(theta) + 360.0) % 360.0;
        }

        private List<ControlPoint> BuildKmAnchorsForTrip(List<TripRow> df, List<KmPostRow> kmPosts,
                                                 double snapRadiusM = 300.0)
        {
            if (df == null || df.Count == 0) return new List<ControlPoint>();
            if (kmPosts == null || kmPosts.Count == 0) return new List<ControlPoint>();

            // For each KM post, find nearest index along trip
            int NearestIdx(double lat, double lon)
            {
                int bestIdx = 0;
                double best = double.MaxValue;
                for (int i = 0; i < df.Count; i++)
                {
                    var d = Geo.DistanceMeters(lat, lon, df[i].SnappedLat, df[i].SnappedLon);
                    if (d < best) { best = d; bestIdx = i; }
                }
                return bestIdx;
            }

            var candidates = new List<(KmPostRow km, int idx, double dist)>();

            foreach (var km in kmPosts)
            {
                int idx = NearestIdx(km.Lat, km.Lon);
                double dist = Geo.DistanceMeters(km.Lat, km.Lon, df[idx].SnappedLat, df[idx].SnappedLon);

                // Only keep KM posts that are actually close to the traveled polyline
                if (dist <= snapRadiusM)
                    candidates.Add((km, idx, dist));
            }

            if (candidates.Count < 2)
            {
                // If too strict, fallback: take nearest ordering anyway (no radius filter)
                candidates.Clear();
                foreach (var km in kmPosts)
                {
                    int idx = NearestIdx(km.Lat, km.Lon);
                    double dist = Geo.DistanceMeters(km.Lat, km.Lon, df[idx].SnappedLat, df[idx].SnappedLon);
                    candidates.Add((km, idx, dist));
                }
            }

            // Sort in travel order
            var ordered = candidates
                .OrderBy(x => x.idx)
                .GroupBy(x => x.km.Id) // avoid duplicates by id
                .Select(g => g.First())
                .ToList();

            // Convert to ControlPoint anchors
            var anchors = ordered.Select(x => new ControlPoint
            {
                ControlPointId = x.km.KilometerPost,  // label like "KM 12" or "12"
                Lat = x.km.Lat,
                Lng = x.km.Lon
            }).ToList();

            // If duplicate labels exist, make them unique
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in anchors)
            {
                var baseId = a.ControlPointId;
                int k = 2;
                while (!seen.Add(a.ControlPointId))
                {
                    a.ControlPointId = $"{baseId}_{k}";
                    k++;
                }
            }

            return anchors;
        }


        private static string AxisDirection(double brng)
        {
            double dN = Math.Min(Math.Abs(brng - 0), 360 - Math.Abs(brng - 0));
            double dE = Math.Min(Math.Abs(brng - 90), 360 - Math.Abs(brng - 90));
            double dS = Math.Min(Math.Abs(brng - 180), 360 - Math.Abs(brng - 180));
            double dW = Math.Min(Math.Abs(brng - 270), 360 - Math.Abs(brng - 270));
            return new[] { ("NB", dN), ("EB", dE), ("SB", dS), ("WB", dW) }
                .OrderBy(t => t.Item2)
                .First().Item1;
        }

        private static (double minLat, double maxLat, double minLon, double maxLon) ComputeBbox(List<TripRow> df, double bufferMeters = 500.0)
        {
            var minLat = df.Min(r => r.SnappedLat);
            var maxLat = df.Max(r => r.SnappedLat);
            var minLon = df.Min(r => r.SnappedLon);
            var maxLon = df.Max(r => r.SnappedLon);

            double latMid = (minLat + maxLat) / 2.0;
            double dLat = bufferMeters / 111320.0;
            double dLon = bufferMeters / (111320.0 * Math.Cos(latMid * Math.PI / 180.0));
            return (minLat - dLat, maxLat + dLat, minLon - dLon, maxLon + dLon);
        }

        private static List<string>? SplitCsv(string? csv) =>
            string.IsNullOrWhiteSpace(csv)
                ? null
                : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Distinct()
                     .ToList();

        private static string ComputeDatasetDirection(List<TripRow> rows)
        {
            var pts = rows
                .Where(r => !double.IsNaN(r.SnappedLat) && !double.IsNaN(r.SnappedLon))
                .Select(r => (lat: r.SnappedLat, lon: r.SnappedLon))
                .ToList();
            if (pts.Count < 2) return "Unknown";

            int i = 0, j = pts.Count - 1;
            while (i < j && Math.Abs(pts[i].lat - pts[i + 1].lat) < 1e-7 &&
                            Math.Abs(pts[i].lon - pts[i + 1].lon) < 1e-7) i++;
            while (j > i && Math.Abs(pts[j].lat - pts[j - 1].lat) < 1e-7 &&
                            Math.Abs(pts[j].lon - pts[j - 1].lon) < 1e-7) j--;

            if (i >= j) return "Unknown";
            var brng = Bearing(pts[i].lat, pts[i].lon, pts[j].lat, pts[j].lon);
            return AxisDirection(brng);
        }

        // Cause map like Flask
        private static readonly Dictionary<int, (string Label, string Color)> CAUSE_MAP = new()
        {
            {1,("Loading and Unloading","pink")},
            {2,("Intersection","orange")},
            {3,("Traffic Light","red")},
            {4,("Pedestrian Crossing","purple")},
            {5,("Animal Crossing","brown")},
            {6,("Vehicle Crossing","maroon")},
            {7,("Road Construction","gray")},
            {8,("Blocked by Vehicle","black")},
            {9,("Others","green")}
        };

        [HttpGet("/")]
        public IActionResult Index() => View();

        // ------------------------------------------------------------
        // UPLOAD MULTIPLE CSV FILES → show MapMulti with checkboxes
        // ------------------------------------------------------------
        [HttpPost("/upload")]
        public async Task<IActionResult> Upload(List<IFormFile> files)
        {
            _state.KmRoad = Request.HasFormContentType ? Request.Form["kmRoad"].ToString() : null;

            _state.Datasets.Clear();
            _state.ControlPoints.Clear();
            _state.ManualCpKm.Clear();
            _state.KmGeneratedPoints.Clear();
            _state.LastTripPath = null;
            _state.KmRoad = Request.HasFormContentType ? Request.Form["kmRoad"].ToString() : null;
            _state.Datasets.Clear();

            var uploadRoot = UploadRoot;

            foreach (var f in files)
            {
                if (f == null || !f.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    continue;

                var safeName = Path.GetFileName(f.FileName);
                var path = Path.Combine(uploadRoot, safeName);

                await using (var fs = System.IO.File.Create(path))
                    await f.CopyToAsync(fs);

                var rows = ReadTripCsv(path);
                if (!rows.Any()) continue;

                _state.Datasets.Add(new TripDataset
                {
                    FileName = f.FileName,
                    Path = path,
                    Rows = rows,
                    Coords = rows.Select(r => new[] { r.SnappedLat, r.SnappedLon }).ToList()
                });

                _state.LastTripPath = path;
            }

            if (!_state.Datasets.Any())
                return BadRequest("No valid CSV files uploaded.");

            // ✅ Build VM with Direction + Peak
            var vm = new MultiMapViewModel
            {
                Items = _state.Datasets.Select(d =>
                {
                    var peak = ComputeDatasetPeak(d.Rows);
                    return new MultiMapViewModel.Item
                    {
                        Id = d.Id,
                        Name = d.FileName,
                        Coords = d.Coords,
                        Direction = ComputeDatasetDirection(d.Rows),

                        // ✅ add these fields in MultiMapViewModel.Item
                        PeakCode = peak.ToString(),
                        PeakLabel = PeakLabel(peak)
                    };
                }).ToList()
            };

            return View("MapMulti", vm);
        }


        // 🔧 helper to keep only visited anchors (used for KM-only display)
        private static List<ControlPoint> FilterAnchorsToVisited(List<TripRow> df, List<ControlPoint> anchors,
                                                                 double enterRadiusM = 300.0, double exitRadiusM = 300.0)
        {
            if (anchors == null || anchors.Count == 0) return new List<ControlPoint>();
            var visits = DetectCpVisits(df, anchors, enterRadiusM, exitRadiusM);
            if (visits.Count == 0) return new List<ControlPoint>();
            var set = visits.Select(v => v.CpId).ToHashSet();
            return anchors.Where(a => set.Contains(a.ControlPointId)).ToList();
        }

        // ===================== KM POST SUPPORT ======================

        private sealed class KmPostRow
        {
            public string Id { get; set; } = "";
            public string KilometerPost { get; set; } = "";
            public double Km { get; set; }
            public double Lat { get; set; }
            public double Lon { get; set; }
            public string? Region { get; set; }
            public string? Road { get; set; }
        }

        private IActionResult RenderMapMulti()
        {
            if (!_state.Datasets.Any())
                return RedirectToAction("Index");

            var vm = new MultiMapViewModel
            {
                Items = _state.Datasets.Select(d =>
                {
                    var peak = ComputeDatasetPeak(d.Rows);
                    return new MultiMapViewModel.Item
                    {
                        Id = d.Id,
                        Name = d.FileName,
                        Coords = d.Coords,
                        Direction = ComputeDatasetDirection(d.Rows),
                        PeakCode = peak.ToString(),
                        PeakLabel = PeakLabel(peak)
                    };
                }).ToList()
            };

            // ✅ Collect anchors from ALL datasets (not just first file)
            var allAnchors = new List<ControlPoint>();

            foreach (var ds in _state.Datasets)
            {
                var a = GetActiveAnchorsForTrip(ds.Rows); // KM mode => per-file KM detection
                if (a != null && a.Count > 0)
                    allAnchors.AddRange(a);
            }

            // ✅ De-duplicate by ID
            var uniqueAnchors = allAnchors
                .GroupBy(x => x.ControlPointId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            ViewBag.AnchorSource = _state.AnchorSource ?? "cp";
            ViewBag.SelectedRegion = _state.KmRegion ?? "";
            ViewBag.SelectedRoads = _state.KmRoads ?? new List<string>();

            ViewBag.AnchorData = uniqueAnchors
                .Select(cp => new { id = cp.ControlPointId, lat = cp.Lat, lon = cp.Lng })
                .ToList();

            return View("MapMulti", vm);
        }

        private List<KmPostRow> LoadKmPostsForTrip(
            List<TripRow> df,
            string? dbPath,
            string? region,
            IEnumerable<string>? roads,
            double bufferMeters = 500.0)
        {
            var list = new List<KmPostRow>();
            if (string.IsNullOrWhiteSpace(dbPath) || !System.IO.File.Exists(dbPath))
                return list;

            var (qMinLat, qMaxLat, qMinLon, qMaxLon) = ComputeBbox(df, bufferMeters);
            using var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
            con.Open();

            var cmd = con.CreateCommand();
            var sb = new StringBuilder(@"
                SELECT id, kilometerPost, latitude AS lat, longitude AS lon, regionId, roadName
                FROM tblKilometerPost
                WHERE latitude BETWEEN @minLat AND @maxLat
                  AND longitude BETWEEN @minLon AND @maxLon ");

            cmd.Parameters.AddWithValue("@minLat", qMinLat);
            cmd.Parameters.AddWithValue("@maxLat", qMaxLat);
            cmd.Parameters.AddWithValue("@minLon", qMinLon);
            cmd.Parameters.AddWithValue("@maxLon", qMaxLon);

            if (!string.IsNullOrWhiteSpace(region))
            {
                sb.Append(" AND regionId = @region ");
                cmd.Parameters.AddWithValue("@region", region);
            }

            var roadList = roads?.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList() ?? new List<string>();
            if (roadList.Count > 0)
            {
                var prm = new List<string>();
                for (int i = 0; i < roadList.Count; i++)
                {
                    var p = $"@road{i}";
                    prm.Add(p);
                    cmd.Parameters.AddWithValue(p, roadList[i]);
                }
                sb.Append(" AND roadName IN (" + string.Join(",", prm) + ") ");
            }

            sb.Append(";");
            cmd.CommandText = sb.ToString();

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var label = rdr["kilometerPost"]?.ToString() ?? "";
                double kmNum = 0.0; double.TryParse(label, NumberStyles.Float, CultureInfo.InvariantCulture, out kmNum);

                list.Add(new KmPostRow
                {
                    Id = rdr["id"]?.ToString() ?? "",
                    KilometerPost = label,
                    Km = kmNum,
                    Lat = double.TryParse(rdr["lat"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var la) ? la : 0.0,
                    Lon = double.TryParse(rdr["lon"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) ? lo : 0.0,
                    Region = rdr["regionId"]?.ToString(),
                    Road = rdr["roadName"]?.ToString()
                });
            }
            return list;
        }

        [HttpGet("/km/regions")]
        public IActionResult GetKmRegions()
        {
            if (!_state.Datasets.Any()) return Json(new List<string>());

            var dbPath = ResolveKmDbPath();
            if (!System.IO.File.Exists(dbPath)) return Json(new List<string>());

            var preview = _state.Datasets.First();
            var (minLat, maxLat, minLon, maxLon) = ComputeBbox(preview.Rows, bufferMeters: 50);

            var list = new List<string>();
            using var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT DISTINCT regionId
                FROM tblKilometerPost
                WHERE latitude BETWEEN @minLat AND @maxLat
                  AND longitude BETWEEN @minLon AND @maxLon
                ORDER BY regionId;";
            cmd.Parameters.AddWithValue("@minLat", minLat);
            cmd.Parameters.AddWithValue("@maxLat", maxLat);
            cmd.Parameters.AddWithValue("@minLon", minLon);
            cmd.Parameters.AddWithValue("@maxLon", maxLon);

            using (var rdr = cmd.ExecuteReader())
                while (rdr.Read())
                    if (!string.IsNullOrWhiteSpace(rdr["regionId"]?.ToString()))
                        list.Add(rdr["regionId"]!.ToString()!);

            if (list.Count == 0)
            {
                using var cmd2 = con.CreateCommand();
                cmd2.CommandText = "SELECT DISTINCT regionId FROM tblKilometerPost ORDER BY regionId LIMIT 100;";
                using var rdr2 = cmd2.ExecuteReader();
                while (rdr2.Read())
                    if (!string.IsNullOrWhiteSpace(rdr2["regionId"]?.ToString()))
                        list.Add(rdr2["regionId"]!.ToString()!);
            }

            return Json(list);
        }

        [HttpGet("/km/roads")]
        public IActionResult GetKmRoads(string? region)
        {
            if (!_state.Datasets.Any()) return Json(new List<string>());

            var dbPath = ResolveKmDbPath();
            if (!System.IO.File.Exists(dbPath)) return Json(new List<string>());

            var preview = _state.Datasets.First();
            var (minLat, maxLat, minLon, maxLon) = ComputeBbox(preview.Rows, bufferMeters: 3000);

            var list = new List<string>();
            using var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
            con.Open();

            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT DISTINCT roadName
                    FROM tblKilometerPost
                    WHERE latitude BETWEEN @minLat AND @maxLat
                      AND longitude BETWEEN @minLon AND @maxLon
                      /**region**/
                    ORDER BY roadName;";
                cmd.Parameters.AddWithValue("@minLat", minLat);
                cmd.Parameters.AddWithValue("@maxLat", maxLat);
                cmd.Parameters.AddWithValue("@minLon", minLon);
                cmd.Parameters.AddWithValue("@maxLon", maxLon);

                if (!string.IsNullOrWhiteSpace(region))
                {
                    cmd.CommandText = cmd.CommandText.Replace("/**region**/", "AND regionId = @region");
                    cmd.Parameters.AddWithValue("@region", region);
                }
                else cmd.CommandText = cmd.CommandText.Replace("/**region**/", "");

                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                    if (!string.IsNullOrWhiteSpace(rdr["roadName"]?.ToString()))
                        list.Add(rdr["roadName"]!.ToString()!);
            }

            if (list.Count == 0)
            {
                using var cmd2 = con.CreateCommand();
                if (!string.IsNullOrWhiteSpace(region))
                {
                    cmd2.CommandText = @"
                        SELECT DISTINCT roadName
                        FROM tblKilometerPost
                        WHERE regionId = @region
                        ORDER BY roadName
                        LIMIT 300;";
                    cmd2.Parameters.AddWithValue("@region", region);
                }
                else
                {
                    cmd2.CommandText = @"
                        SELECT DISTINCT roadName
                        FROM tblKilometerPost
                        ORDER BY roadName
                        LIMIT 300;";
                }
                using var rdr2 = cmd2.ExecuteReader();
                while (rdr2.Read())
                    if (!string.IsNullOrWhiteSpace(rdr2["roadName"]?.ToString()))
                        list.Add(rdr2["roadName"]!.ToString()!);
            }

            return Json(list);
        }

        private string ResolveKmDbPath()
        {
            // 1️⃣ Base directory (works for EXE)
            var baseDir = AppContext.BaseDirectory;

            // 2️⃣ If user manually set path → use it
            if (!string.IsNullOrWhiteSpace(_state.KmDbPath) &&
                System.IO.File.Exists(_state.KmDbPath))
                return _state.KmDbPath!;

            // 3️⃣ From config (appsettings.json)
            var cfg = _config["KmPostDbPath"];
            if (!string.IsNullOrWhiteSpace(cfg))
            {
                var cfgPath = Path.IsPathRooted(cfg)
                    ? cfg
                    : Path.Combine(baseDir, cfg.Replace('/', Path.DirectorySeparatorChar));

                if (System.IO.File.Exists(cfgPath))
                    return cfgPath;
            }

            // 4️⃣ Default: Data folder beside EXE
            var path1 = Path.Combine(baseDir, "Data", "kilometer_post.db");
            if (System.IO.File.Exists(path1))
                return path1;

            // 5️⃣ Fallback: same folder as EXE
            var path2 = Path.Combine(baseDir, "kilometer_post.db");
            if (System.IO.File.Exists(path2))
                return path2;

            // ❌ Not found → clear error
            throw new FileNotFoundException(
                $"kilometer_post.db not found.\nChecked:\n{path1}\n{path2}"
            );
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

        // ✅ IMPORTANT FIX:
        // KM anchors must include ManualCpKm (the list used by /add_cp when mode=km)
        private List<ControlPoint> GetActiveAnchorsForTrip(List<TripRow> df)
        {
            var mode = (_state.AnchorSource ?? "cp");

            if (mode == "km")
            {
                var kmAnchors = BuildKmAnchorsForRows(df);

                // include manual KM clicks too
                return kmAnchors
                    .Concat(_state.ManualCpKm)
                    .GroupBy(a => a.ControlPointId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
            }

            // CP mode
            return _state.ControlPoints.ToList();
        }

        // 🔁 KM → CP sync
        private void SyncControlPointsFromKmSelection()
        {
            if (!_state.Datasets.Any()) return;

            var preview = _state.Datasets.First();
            var df = preview.Rows;

            var dbPath = ResolveKmDbPath();
            if (!System.IO.File.Exists(dbPath)) return;

            IEnumerable<string>? roads = null;
            if (_state.KmRoads?.Count > 0) roads = _state.KmRoads;
            else if (!string.IsNullOrWhiteSpace(_state.KmRoad)) roads = SplitCsv(_state.KmRoad);

            var kmPosts = LoadKmPostsForTrip(df, dbPath, _state.KmRegion, roads, bufferMeters: 3000.0);
            if (kmPosts.Count < 2) return;

            var kmAnchors = BuildKmAnchorsForTrip(df, kmPosts);
            if (kmAnchors.Count < 2) return;

            var filtered = FilterAnchorsToVisited(df, kmAnchors, CP_DETECT_RADIUS_M, CP_DETECT_RADIUS_M);

            _state.KmGeneratedPoints.Clear();
            _state.KmGeneratedPoints.AddRange(filtered.Count >= 2 ? filtered : kmAnchors);
        }

        // Switch CP/KM + optional region/roads (re-renders MapMulti)
        [HttpPost("/anchor_preview")]
        public IActionResult AnchorPreview(string source, string? region, [FromForm] string[]? roads)

        {
            _state.AnchorSource = (source?.Trim().ToLowerInvariant() == "km") ? "km" : "cp";

            // Save KM selections to state (for auto export)
            _state.KmRegion = string.IsNullOrWhiteSpace(region) ? null : region.Trim();

            _state.KmRoads = roads?.Where(r => !string.IsNullOrWhiteSpace(r))
                                   .Select(r => r.Trim())
                                   .Distinct()
                                   .ToList() ?? new List<string>();

            // Also set KmRoad (single string) for backward compatibility
            _state.KmRoad = (_state.KmRoads.Count > 0)
                ? string.Join(",", _state.KmRoads)
                : null;

            if (_state.AnchorSource == "km")
                SyncControlPointsFromKmSelection();

            return RenderMapMulti();
        }

        // ------------------------------------------------------------
        // MULTI-FILE ANALYZE
        // ------------------------------------------------------------
        [HttpPost("/analyze_multi")]
        public IActionResult AnalyzeMulti()
        {
            if (!_state.Datasets.Any())
                return BadRequest("Upload files first.");

            var selectedIds = (Request.HasFormContentType
                ? Request.Form["selected_files"].ToArray()
                : Array.Empty<string>())
                .ToHashSet();

            var chosen = _state.Datasets.Where(d => selectedIds.Contains(d.Id)).ToList();
            if (!chosen.Any()) return BadRequest("No dataset selected.");

            var vm = new MultiAnalyzeViewModel();

            // 1) analyze all selected datasets first
            var analyzed = new List<MultiAnalyzeViewModel.DatasetAnalysis>();

            foreach (var d in chosen)
            {
                var anchors = GetActiveAnchorsForTrip(d.Rows);
                anchors = MergeAnchorsInTripOrder(d.Rows, anchors, _state.ManualCpKm);

                if (anchors.Count < 2) continue;

                var (results, segments, summary) = AnalyzeTrip(d.Rows, anchors);

                var peak = ComputeDatasetPeak(d.Rows);
                var dir = ComputeDatasetDirection(d.Rows);

                analyzed.Add(new MultiAnalyzeViewModel.DatasetAnalysis
                {
                    Id = d.Id,
                    Name = d.FileName,
                    Results = results,
                    Segments = segments.Cast<object>().ToList(),
                    Summary = summary,
                    PeakCode = peak.ToString(),
                    PeakLabel = PeakLabel(peak),
                    Direction = dir
                });
            }

            if (analyzed.Count == 0)
                return BadRequest("No usable datasets (not enough anchors / empty analysis).");

            // 2) ✅ BACKWARD-COMPAT ROOT FIELDS (old Razor references)
            vm.Datasets = analyzed;

            vm.OverallSummary = new AnalysisSummary
            {
                TotalTravelTimeMin = Round2(analyzed.Average(x => x.Summary.TotalTravelTimeMin)),
                TotalDistanceKm = Round2(analyzed.Average(x => x.Summary.TotalDistanceKm)),
                AvgTravelSpeed = Round2(analyzed.Average(x => x.Summary.AvgTravelSpeed)),
                AvgRunningSpeed = Round2(analyzed.Average(x => x.Summary.AvgRunningSpeed)),
                TotalDelayMin = Round2(analyzed.Average(x => x.Summary.TotalDelayMin)),
                TotalDelayLength = Round2(analyzed.Average(x => x.Summary.TotalDelayLength))
            };

            // overall directional averages (all peaks combined)
            var perDirAll = analyzed
                .GroupBy(x => (x.Direction ?? "Unknown").ToUpperInvariant())
                .ToDictionary(g => g.Key, g => new
                {
                    AvgTravelTimeMin = g.Average(s => s.Summary.TotalTravelTimeMin),
                    AvgDistanceKm = g.Average(s => s.Summary.TotalDistanceKm),
                    AvgTravelSpeed = g.Average(s => s.Summary.AvgTravelSpeed),
                    AvgRunningSpeed = g.Average(s => s.Summary.AvgRunningSpeed),
                    AvgDelayMin = g.Average(s => s.Summary.TotalDelayMin),
                    AvgDelayLength = g.Average(s => s.Summary.TotalDelayLength)
                });

            var dirOrder = new[] { "SB", "NB", "EB", "WB", "UNKNOWN" };
            vm.DirectionSummaries = new List<DirectionalSummary>();

            foreach (var code in dirOrder)
            {
                if (!perDirAll.TryGetValue(code, out var s)) continue;

                vm.DirectionSummaries.Add(new DirectionalSummary
                {
                    Direction = code == "UNKNOWN" ? "Unknown" : code,
                    Name = FullDirName(code == "UNKNOWN" ? "Unknown" : code),
                    AvgTravelTimeMin = Round2(s.AvgTravelTimeMin),
                    AvgDistanceKm = Round2(s.AvgDistanceKm),
                    AvgTravelSpeed = Round2(s.AvgTravelSpeed),
                    AvgRunningSpeed = Round2(s.AvgRunningSpeed),
                    AvgDelayMin = Round2(s.AvgDelayMin),
                    AvgDelayLength = Round2(s.AvgDelayLength)
                });
            }

            // 3) group into PeakGroups (AM/MID/PM/OFF)
            var peakOrder = new[] { "AM", "MID", "PM", "OFF" };

            foreach (var pk in peakOrder)
            {
                var groupDatasets = analyzed
                    .Where(x => (x.PeakCode ?? "OFF").ToUpperInvariant() == pk)
                    .ToList();

                if (!groupDatasets.Any()) continue;

                var g = new PeakAnalysisGroup
                {
                    PeakCode = pk,
                    PeakLabel = groupDatasets.First().PeakLabel
                };

                // ✅ MUST ADD DATASETS TO GROUP
                g.Datasets.AddRange(groupDatasets);

                // peak overall summary (average across datasets in this peak)
                g.OverallSummary = new AnalysisSummary
                {
                    TotalTravelTimeMin = Round2(g.Datasets.Average(x => x.Summary.TotalTravelTimeMin)),
                    TotalDistanceKm = Round2(g.Datasets.Average(x => x.Summary.TotalDistanceKm)),
                    AvgTravelSpeed = Round2(g.Datasets.Average(x => x.Summary.AvgTravelSpeed)),
                    AvgRunningSpeed = Round2(g.Datasets.Average(x => x.Summary.AvgRunningSpeed)),
                    TotalDelayMin = Round2(g.Datasets.Average(x => x.Summary.TotalDelayMin)),
                    TotalDelayLength = Round2(g.Datasets.Average(x => x.Summary.TotalDelayLength))
                };

                // peak directional averages
                var perDirPeak = g.Datasets
                    .GroupBy(x => (x.Direction ?? "Unknown").ToUpperInvariant())
                    .ToDictionary(z => z.Key, z => new
                    {
                        AvgTravelTimeMin = z.Average(s => s.Summary.TotalTravelTimeMin),
                        AvgDistanceKm = z.Average(s => s.Summary.TotalDistanceKm),
                        AvgTravelSpeed = z.Average(s => s.Summary.AvgTravelSpeed),
                        AvgRunningSpeed = z.Average(s => s.Summary.AvgRunningSpeed),
                        AvgDelayMin = z.Average(s => s.Summary.TotalDelayMin),
                        AvgDelayLength = z.Average(s => s.Summary.TotalDelayLength)
                    });

                var peakDirOrder = new[] { "SB", "NB", "EB", "WB", "UNKNOWN" };
                foreach (var code in peakDirOrder)
                {
                    if (!perDirPeak.TryGetValue(code, out var s)) continue;

                    var dirCode = code == "UNKNOWN" ? "Unknown" : code;

                    g.DirectionSummaries.Add(new DirectionalSummary
                    {
                        Direction = dirCode,
                        Name = FullDirName(dirCode),
                        AvgTravelTimeMin = Round2(s.AvgTravelTimeMin),
                        AvgDistanceKm = Round2(s.AvgDistanceKm),
                        AvgTravelSpeed = Round2(s.AvgTravelSpeed),
                        AvgRunningSpeed = Round2(s.AvgRunningSpeed),
                        AvgDelayMin = Round2(s.AvgDelayMin),
                        AvgDelayLength = Round2(s.AvgDelayLength)
                    });
                }

                // peak flattened tables (optional but OK)
                g.SegmentResults = g.Datasets.SelectMany(x => x.Results).ToList();
                g.Segments = g.Datasets.SelectMany(x => x.Segments).ToList();

                vm.PeakGroups.Add(g);
            }

            // 4) CP data for map (use preview dataset anchors)
            var preview = chosen.First();
            var anchors2 = GetActiveAnchorsForTrip(preview.Rows);

            vm.CpData = anchors2
                .Select(cp => new { cp_id = cp.ControlPointId, lat = cp.Lat, lon = cp.Lng })
                .Cast<object>()
                .ToList();

            return View("ResultMulti", vm);
        }




        // ------------------------------------------------------------
        // SINGLE-FILE ANALYZE
        // ------------------------------------------------------------
        [HttpPost("/analyze")]
        public IActionResult Analyze()
        {
            if (string.IsNullOrEmpty(_state.LastTripPath))
                return BadRequest("Missing trip data.");

            var df = ReadTripCsv(_state.LastTripPath!);
            if (!df.Any()) return BadRequest("CSV has no rows.");

            // If KM mode and KmGeneratedPoints not yet built, build now

            var anchors = GetActiveAnchorsForTrip(df);

            // Merge again (safe) so manual points appear in travel order
            anchors = MergeAnchorsInTripOrder(df, anchors, _state.ManualCpKm);

            if (anchors.Count < 2)
                return BadRequest("Not enough anchor points (CP or KM) found for this trip.");

            var selected = (Request.HasFormContentType ? Request.Form["selected_cps"].ToArray() : Array.Empty<string>())
                .Select(s => s.Trim()).ToHashSet();

            if (selected.Any() && _state.AnchorSource == "cp")
                anchors = anchors.Where(cp => selected.Contains(cp.ControlPointId)).ToList();

            var (results, segments, summary) = AnalyzeTrip(df, anchors);

            var markers = anchors;
            if ((_state.AnchorSource ?? "cp") == "km")
            {
                var filtered = FilterAnchorsToVisited(df, anchors, CP_DETECT_RADIUS_M, CP_DETECT_RADIUS_M);
                if (filtered.Count > 0) markers = filtered;
            }

            var vm = new AnalyzeViewModel
            {
                Results = results,
                Segments = segments.Cast<object>().ToList(),
                CpData = markers.Select(cp => new { cp_id = cp.ControlPointId, lat = cp.Lat, lon = cp.Lng })
                                .Cast<object>().ToList(),
                Summary = summary
            };

            return View("Result", vm);
        }


        // ------------------------------------------------------------
        // CP endpoints
        // ------------------------------------------------------------
        public class AddCpRequest
        {
            public double lat { get; set; }
            public double lng { get; set; }
            public string? name { get; set; }
            public string? mode { get; set; } // "cp" or "km"
        }

        // ✅ IMPORTANT FIX: avoid 400 due to anti-forgery when called by fetch(JSON)
        [IgnoreAntiforgeryToken]
        [HttpPost("/add_cp")]
        public IActionResult AddCp([FromBody] AddCpRequest body)
        {
            if (body == null) return BadRequest("Missing body");

            var cpId = !string.IsNullOrWhiteSpace(body.name)
                ? body.name.Trim()
                : "CP" + DateTime.Now.ToString("HHmmss");

            var target = (body.mode ?? "cp").ToLowerInvariant() == "km"
                ? _state.ManualCpKm          // ✅ KM manual clicks stored here
                : _state.ControlPoints;

            if (target.Any(x => x.ControlPointId.Equals(cpId, StringComparison.OrdinalIgnoreCase)))
                cpId = cpId + "_" + (target.Count + 1);

            target.Add(new ControlPoint
            {
                ControlPointId = cpId,
                Lat = body.lat,
                Lng = body.lng
            });

            return Json(new { controlPoint = cpId, mode = (body.mode ?? "cp") });
        }

        [HttpGet("/get_cp")]
        public IActionResult GetCp(string? mode)
        {
            var m = (mode ?? "cp").ToLowerInvariant();

            IEnumerable<ControlPoint> list = m == "km"
                ? _state.KmGeneratedPoints.Concat(_state.ManualCpKm)
                : _state.ControlPoints;

            return Json(list.Select(cp => new { id = cp.ControlPointId, lat = cp.Lat, lng = cp.Lng }));
        }

        [IgnoreAntiforgeryToken]
        [HttpPost("/upload_cp")]
        public async Task<IActionResult> UploadCp(IFormFile cp_file)
        {
            try
            {
                if (cp_file == null || cp_file.Length == 0)
                    return BadRequest("Please select a .csv file.");
                if (!cp_file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    return BadRequest("Invalid file type. Please upload a .csv file.");

                var uploadRoot = GetUploadRoot();
                var path = Path.Combine(uploadRoot, "uploaded_cp.csv");

                await using (var fs = System.IO.File.Create(path))
                    await cp_file.CopyToAsync(fs);

                using var reader = new StreamReader(path);
                using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
                var records = csv.GetRecords<dynamic>().ToList();
                if (records.Count == 0)
                    return BadRequest("Empty CP file.");

                _state.ControlPoints.Clear();

                foreach (var r in records)
                {
                    var dict = (IDictionary<string, object>)r;

                    string? Get(IDictionary<string, object> d, params string[] keys)
                    {
                        foreach (var k in keys)
                            if (d.TryGetValue(k, out var v) && v != null)
                                return Convert.ToString(v);

                        foreach (var kv in d)
                            if (keys.Any(k2 => string.Equals(kv.Key, k2, StringComparison.OrdinalIgnoreCase)))
                                return Convert.ToString(kv.Value);

                        return null;
                    }

                    var cpName = Get(dict, "controlPoint", "cp", "name", "ControlPoint", "CP");
                    var latStr = Get(dict, "latitude", "lat", "Latitude", "Lat");
                    var lonStr = Get(dict, "longitude", "lon", "lng", "Longitude", "Lon", "Lng");

                    if (string.IsNullOrWhiteSpace(latStr) || string.IsNullOrWhiteSpace(lonStr))
                        continue;
                    if (!double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
                        continue;
                    if (!double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
                        continue;

                    if (string.IsNullOrWhiteSpace(cpName))
                        cpName = $"CP{_state.ControlPoints.Count + 1}";

                    _state.ControlPoints.Add(new ControlPoint
                    {
                        ControlPointId = cpName.Trim(),
                        Lat = lat,
                        Lng = lng
                    });
                }

                return Json(new
                {
                    status = "success",
                    message = $"{_state.ControlPoints.Count} control points uploaded.",
                    folder = uploadRoot
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Upload failed: {ex.Message}");
            }
        }

        [HttpGet("/download_cp")]
        public IActionResult DownloadCp()
        {
            // ✅ FIX: use UploadRoot (safe) not _state.UploadFolder (might be empty)
            var path = Path.Combine(UploadRoot, "control_points.csv");
            using var writer = new StreamWriter(path, false, Encoding.UTF8);
            using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
            {
                csv.WriteField("controlPoint"); csv.WriteField("latitude"); csv.WriteField("longitude"); csv.NextRecord();
                foreach (var cp in _state.ControlPoints)
                {
                    csv.WriteField(cp.ControlPointId);
                    csv.WriteField(cp.Lat);
                    csv.WriteField(cp.Lng);
                    csv.NextRecord();
                }
            }
            return PhysicalFile(path, "text/csv", "control_points.csv");
        }

        [HttpPost("/set_anchor")]
        public IActionResult SetAnchor(string source = "cp", string? region = null, string? road = null)
        {
            _state.AnchorSource = (source?.Trim().ToLowerInvariant() == "km") ? "km" : "cp";
            _state.KmRegion = region;
            _state.KmRoad = road;

            if (string.IsNullOrEmpty(_state.KmDbPath))
                _state.KmDbPath = ResolveKmDbPath();

            return RedirectToAction("Index");
        }

        [IgnoreAntiforgeryToken]
        [HttpPost("/update_cp_position")]
        public IActionResult UpdateCpPosition([FromBody] UpdateCpRequest req)
        {
            // NOTE: update only in CP list. If you want drag to work in KM list too, add similar lookup in ManualCpKm.
            var cp = _state.ControlPoints.FirstOrDefault(c => c.ControlPointId == req.cp_id);
            if (cp != null) { cp.Lat = req.lat; cp.Lng = req.lng; }
            return Json(new { status = "success" });
        }

        public class UpdateCpRequest
        {
            public string cp_id { get; set; } = "";
            public double lat { get; set; }
            public double lng { get; set; }
        }

        // ---------------- Small helpers for directional table display ----------------
        private static string FullDirName(string code) => code switch
        {
            "SB" => "Southbound",
            "NB" => "Northbound",
            "EB" => "Eastbound",
            "WB" => "Westbound",
            _ => "Unknown"
        };

        private static double Round2(double v) => Math.Round(v, 2);

        private (string regionSafe, string roadSafe) ResolveRegionRoad(string? region, string? road)
        {
            bool IsUnknown(string? s) =>
                string.IsNullOrWhiteSpace(s) ||
                s.Equals("UnknownRegion", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("UnknownRoad", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

            // If user did not pass region/road OR passed the defaults, fallback to state
            var r = IsUnknown(region) ? (_state.KmRegion ?? "") : region!.Trim();

            string rd;
            if (IsUnknown(road))
            {
                if (_state.KmRoads != null && _state.KmRoads.Count > 0)
                    rd = _state.KmRoads[0];
                else
                    rd = _state.KmRoad ?? "";
            }
            else rd = road!.Trim();

            if (string.IsNullOrWhiteSpace(r)) r = "UnknownRegion";
            if (string.IsNullOrWhiteSpace(rd)) rd = "UnknownRoad";

            return (SafePathPart(r), SafePathPart(rd));
        }
        private static string FormatMinToHHMMSS(double minutes)
        {
            if (minutes <= 0) return "00:00:00";
            var ts = TimeSpan.FromMinutes(minutes);
            return ts.ToString(@"hh\:mm\:ss");
        }

        // --- Helper: always return a usable uploads folder ---
        private string GetUploadRoot()
        {
            var root = _state.UploadFolder;
            if (!string.IsNullOrWhiteSpace(root))
            {
                try { Directory.CreateDirectory(root); } catch { }
                return root;
            }

            var baseDir = _env?.ContentRootPath ?? AppContext.BaseDirectory ?? Directory.GetCurrentDirectory();
            root = Path.Combine(baseDir, "uploads");
            try { Directory.CreateDirectory(root); } catch { }
            return root;
        }


        // ------------------------------------------------------------
        // PRIVATE HELPER: core analyze (used by both single and multi)
        // ------------------------------------------------------------
        private (List<SegmentResult> results, List<object> segments, AnalysisSummary summary)
            AnalyzeTrip(List<TripRow> df, List<ControlPoint> filtered)
        {
            var visitList = DetectCpVisits(df, filtered, enterRadiusM: 300.0, exitRadiusM: 300.0);

            var visited = visitList
                .Select(v => new { v, cp = filtered.FirstOrDefault(c => c.ControlPointId == v.CpId) })
                .Where(x => x.cp != null)
                .Select(x => (cpId: x.v.CpId, lat: x.cp!.Lat, lon: x.cp!.Lng, idx: x.v.Index))
                .ToList();

            var results = new List<SegmentResult>();
            var segments = new List<object>();
            var usedPairs = new HashSet<string>();

            for (int i = 0; i < visited.Count - 1; i++)
            {
                var cp1 = visited[i];
                var cp2 = visited[i + 1];
                var pairKey = $"{cp1.cpId}|{cp2.cpId}";
                if (usedPairs.Contains(pairKey)) continue;
                usedPairs.Add(pairKey);

                int idx1 = cp1.idx, idx2 = cp2.idx;
                bool reversed = idx1 > idx2;
                string note = reversed ? "Reverse Order" : "✔️";
                if (reversed) { var t = idx1; idx1 = idx2; idx2 = t; }

                var segRows = df.GetRange(idx1, idx2 - idx1 + 1);

                double timeSec = segRows.Sum(r => Finite(r.secDiff));
                double timeMin = Math.Round(timeSec / 60.0, 2);
                double distanceM = segRows.Sum(r => Finite(r.distanceDiff));
                double travelSpeed = timeSec > 0 ? (distanceM / 1000.0) / (timeSec / 3600.0) : 0.0;

                var startTime = segRows.First().Timestamp;
                var endTime = segRows.Last().Timestamp;

                int delayCount = segRows.Count(r => (r.Speed ?? 0) <= 5.0);

                double delayLenTableM = segRows
                    .Where(r => (r.Speed ?? 0) <= 5.0)
                    .Sum(r => Math.Max(Finite(r.distanceDiff), 0.0));

                var subsegments = new List<(string status, List<TripRow> rows)>();
                string? currStatus = null;
                var curr = new List<TripRow>();
                foreach (var r in segRows)
                {
                    double sp = r.Speed ?? 0.0;
                    string status = sp >= 25 ? "moving" : "delay";
                    if (currStatus == null) currStatus = status;
                    if (status == currStatus) curr.Add(r);
                    else
                    {
                        if (curr.Count > 0) subsegments.Add((currStatus, new List<TripRow>(curr)));
                        curr.Clear(); curr.Add(r); currStatus = status;
                    }
                }
                if (curr.Count > 0 && currStatus != null) subsegments.Add((currStatus, curr));

                var delayCauses = new List<string>();
                foreach (var (status, rows) in subsegments)
                {
                    if (rows.Count < 2) continue;

                    var latlngs = rows.Select(r => new[] { Finite(r.SnappedLat), Finite(r.SnappedLon) }).ToList();
                    double avgSpeed = rows.Any(r => r.Speed.HasValue)
                        ? rows.Average(r => Finite(r.Speed ?? 0))
                        : 0.0;

                    double subDistM = rows.Sum(r => Finite(r.distanceDiff));
                    double delayLengthM = (status == "delay") ? Math.Round(subDistM, 2) : 0.0;

                    string label, color;
                    if (status == "delay")
                    {
                        var causeIds = rows.Where(r => r.CauseID.HasValue)
                                           .Select(r => r.CauseID!.Value)
                                           .Where(cid => CAUSE_MAP.ContainsKey(cid))
                                           .ToList();

                        if (causeIds.Any())
                        {
                            foreach (var cid in causeIds) delayCauses.Add(CAUSE_MAP[cid].Item1);
                            var mainId = causeIds.GroupBy(c => c)
                                                 .OrderByDescending(g => g.Count())
                                                 .First().Key;
                            (label, color) = CAUSE_MAP[mainId];
                        }
                        else
                        {
                            (label, color) = ("Delay", "blue");
                        }
                    }
                    else
                    {
                        (label, color) = ("Normal Moving", "blue");
                    }

                    segments.Add(new
                    {
                        path = latlngs,
                        color = color,
                        cause = label,
                        speed = Math.Round(avgSpeed, 2),
                        status = status,
                        fromCp = cp1.cpId,
                        toCp = cp2.cpId,
                        delayLengthM = delayLengthM
                    });
                }

                string causesOut = (delayCount > 0 && delayLenTableM > 0)
                    ? string.Join(", ", delayCauses.Distinct().OrderBy(s => s))
                    : "";

                results.Add(new SegmentResult
                {
                    From = cp1.cpId,
                    To = cp2.cpId,
                    StartTime = startTime.HasValue ? startTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "",
                    EndTime = endTime.HasValue ? endTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "",
                    TravelTimeSec = Math.Round(timeSec, 1),
                    TravelTimeMin = timeMin,
                    DistanceM = Math.Round(distanceM, 1),
                    TravelSpeedKph = Math.Round(travelSpeed, 2),

                    RunningSpeedKph = (timeSec - delayCount) > 0
                        ? Math.Round((distanceM * 3.6 / (timeSec - delayCount)), 2)
                        : 0,

                    Delays = delayCount,
                    DelayLengthM = Math.Round(delayLenTableM, 2),
                    DelayCauses = causesOut,
                    Note = note
                });
            }

            var valid = results.Where(r => r.TravelTimeMin.HasValue && r.DistanceM.HasValue).ToList();
            double totalTravelMin = valid.Sum(r => r.TravelTimeSec ?? 0) / 60;
            double totalDistKm = valid.Sum(r => r.DistanceM ?? 0) / 1000.0;
            double totalDelayMin = valid.Sum(r => (r.Delays ?? 0)) / 60.0;
            double totalDelayLen = valid.Sum(r => r.DelayLengthM ?? 0);
            double avgTravel = totalTravelMin > 0 ? totalDistKm / (totalTravelMin / 60.0) : 0.0;
            double avgRunning = (totalTravelMin - totalDelayMin) > 0 ? totalDistKm / ((totalTravelMin - totalDelayMin) / 60.0) : 0.0;

            var summary = new AnalysisSummary
            {
                TotalTravelTimeMin = Math.Round(totalTravelMin, 2),
                TotalDistanceKm = Math.Round(totalDistKm, 2),
                AvgTravelSpeed = Math.Round(avgTravel, 2),
                AvgRunningSpeed = Math.Round(avgRunning, 2),
                TotalDelayMin = Math.Round(totalDelayMin, 2),
                TotalDelayLength = Math.Round(totalDelayLen, 2)
            };

            return (results, segments, summary);
        }

        // ------------------------------------------------------------
        // CSV + CP visit helpers
        // ------------------------------------------------------------
        private static List<TripRow> ReadTripCsv(string path)
        {
            using var reader = new StreamReader(path);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

            var rows = new List<TripRow>();
            var records = csv.GetRecords<dynamic>();

            foreach (var rec in records)
            {
                var d = (IDictionary<string, object>)rec;

                static string? GetStr(IDictionary<string, object> dict, string key)
                    => dict.TryGetValue(key, out var val) ? val?.ToString() : null;

                double D(string k)
                {
                    var s = GetStr(d, k);
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
                }

                double? DN(string k)
                {
                    var s = GetStr(d, k);
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
                }

                int? IN(string k)
                {
                    var s = GetStr(d, k);
                    return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
                }

                DateTime? T(string k)
                {
                    var s = GetStr(d, k);
                    if (string.IsNullOrWhiteSpace(s)) return null;
                    return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt) ? dt : null;
                }

                double? speedKph = DN("Speed") ?? DN("SSpeed");

                rows.Add(new TripRow
                {
                    SnappedLat = D("SnappedLat"),
                    SnappedLon = D("SnappedLon"),
                    secDiff = D("secDiff"),
                    distanceDiff = D("distanceDiff"),
                    Speed = speedKph,
                    CauseID = IN("CauseID"),
                    Timestamp = T("Timestamp")
                });
            }

            return rows;
        }

        private static (ControlPoint? cp, double distM) NearestCp(double lat, double lon, IReadOnlyList<ControlPoint> cps)
        {
            ControlPoint? best = null;
            double bestM = double.MaxValue;
            foreach (var cp in cps)
            {
                var d = Geo.DistanceMeters(lat, lon, cp.Lat, cp.Lng);
                if (d < bestM) { bestM = d; best = cp; }
            }
            return (best, bestM);
        }

        private sealed class CpVisit
        {
            public string CpId { get; set; } = "";
            public int Index { get; set; }
            public double Lat { get; set; }
            public double Lon { get; set; }
        }

        private static List<CpVisit> DetectCpVisits(
    List<TripRow> df,
    List<ControlPoint> cps,
    double enterRadiusM = 300.0,
    double exitRadiusM = 300.0)
        {
            var visits = new List<CpVisit>();

            string? currentCp = null;
            ControlPoint? activeCp = null;
            double bestDist = double.MaxValue;
            int bestIdx = -1;

            // 🔥 Track closest point per CP (fallback)
            var nearestPerCp = new Dictionary<string, (double dist, int idx)>();

            for (int i = 0; i < df.Count; i++)
            {
                var r = df[i];

                foreach (var cp in cps)
                {
                    double d = Geo.DistanceMeters(r.SnappedLat, r.SnappedLon, cp.Lat, cp.Lng);

                    // 🔥 Always track nearest (fallback)
                    if (!nearestPerCp.ContainsKey(cp.ControlPointId) || d < nearestPerCp[cp.ControlPointId].dist)
                    {
                        nearestPerCp[cp.ControlPointId] = (d, i);
                    }

                    // ✅ ENTER
                    if (currentCp == null && d <= enterRadiusM)
                    {
                        currentCp = cp.ControlPointId;
                        activeCp = cp;
                        bestDist = d;
                        bestIdx = i;
                    }

                    // ✅ INSIDE
                    else if (currentCp == cp.ControlPointId && activeCp != null)
                    {
                        if (d <= exitRadiusM)
                        {
                            if (d < bestDist)
                            {
                                bestDist = d;
                                bestIdx = i;
                            }
                        }
                        else
                        {
                            // ✅ EXIT → finalize
                            visits.Add(new CpVisit
                            {
                                CpId = currentCp,
                                Index = bestIdx,
                                Lat = df[bestIdx].SnappedLat,
                                Lon = df[bestIdx].SnappedLon
                            });

                            currentCp = null;
                            activeCp = null;
                            bestIdx = -1;
                            bestDist = double.MaxValue;
                        }
                    }
                }
            }

            // finalize if still inside
            if (currentCp != null && bestIdx >= 0)
            {
                visits.Add(new CpVisit
                {
                    CpId = currentCp,
                    Index = bestIdx,
                    Lat = df[bestIdx].SnappedLat,
                    Lon = df[bestIdx].SnappedLon
                });
            }

            // 🔥 FALLBACK: add missed CPs (VERY IMPORTANT FIX)
            foreach (var kv in nearestPerCp)
            {
                if (kv.Value.dist <= enterRadiusM)
                {
                    if (!visits.Any(v => v.CpId == kv.Key))
                    {
                        visits.Add(new CpVisit
                        {
                            CpId = kv.Key,
                            Index = kv.Value.idx,
                            Lat = df[kv.Value.idx].SnappedLat,
                            Lon = df[kv.Value.idx].SnappedLon
                        });
                    }
                }
            }

            // remove duplicates & sort
            return visits
                .OrderBy(v => v.Index)
                .GroupBy(v => v.CpId)
                .Select(g => g.First())
                .ToList();
        }

        private static double Finite(double v) => (double.IsNaN(v) || double.IsInfinity(v)) ? 0.0 : v;

        private static double SpeedKph(TripRow r)
        {
            if (r != null && r.Speed.HasValue)
            {
                var s = Finite(r.Speed.Value);
                if (s >= 0 && s < 300) return s;
            }
            double secs = Math.Max(Finite(r?.secDiff ?? 0.0), 1e-6);
            double distM = Math.Max(Finite(r?.distanceDiff ?? 0.0), 0.0);
            return (distM / secs) * 3.6;
        }

        private static byte[] BuildResultsCsv(List<SegmentResult> rows)
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
                    var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
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


        // --------------------------------------------------------------------
        // SINGLE DOWNLOAD ZIP (analyzed_result.csv)
        // /download?region=NCR&roadNameOrSections=EDSA&period=AM
        // --------------------------------------------------------------------
        [HttpGet("/download")]
        public IActionResult Download(
            string region = "UnknownRegion",
            string roadNameOrSections = "UnknownRoad",
            string period = "ALL"
        )
        {
            region = SafePathPart(region);
            roadNameOrSections = SafePathPart(roadNameOrSections);
            period = SafePathPart(period);

            var analyzedPath = Path.Combine(UploadRoot, "analyzed_result.csv");
            if (!System.IO.File.Exists(analyzedPath))
                return NotFound("analyzed_result.csv not found.");

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var baseDir = $"{region}/{roadNameOrSections}/{period}/";
                var entry = zip.CreateEntry($"{baseDir}Segment Analysis/analyzed_result.csv", CompressionLevel.Fastest);

                using var es = entry.Open();
                using var fs = System.IO.File.OpenRead(analyzedPath);
                fs.CopyTo(es);
            }

            var outName = $"{region}_{roadNameOrSections}_{period}_{DateTime.Now:yyyyMMddHHmmss}.zip";
            return File(ms.ToArray(), "application/zip", outName);
        }

        // Builds a CSV directly from TripRow objects (no FilePath needed)
        private static byte[] BuildOriginalCsvFromRows(IEnumerable<TtdsWeb.Models.TripRow> rows)
        {
            if (rows == null) return Encoding.UTF8.GetBytes("No rows.\n");

            var list = rows as IList<TtdsWeb.Models.TripRow> ?? rows.ToList();
            if (list.Count == 0) return Encoding.UTF8.GetBytes("No rows.\n");

            // Get public readable properties (columns)
            var props = typeof(TtdsWeb.Models.TripRow)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead)
                .ToArray();

            static string Esc(string? s)
            {
                s ??= "";
                if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
                    return $"\"{s.Replace("\"", "\"\"")}\"";
                return s;
            }

            var sb = new StringBuilder();

            // header
            sb.AppendLine(string.Join(",", props.Select(p => Esc(p.Name))));

            // rows
            foreach (var r in list)
            {
                var cells = props.Select(p =>
                {
                    var v = p.GetValue(r, null);
                    return Esc(v?.ToString());
                });

                sb.AppendLine(string.Join(",", cells));
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // --------------------------------------------------------------------
        // MULTI EXPORT ZIP
        // Creates:
        // Region/Road/period/Directional Averages/*.csv
        // Region/Road/period/Segment Analysis/*.csv
        // Region/Road/period/originalCSV/*.csv
        // --------------------------------------------------------------------

        // 1) Shared builder (NO HttpContext use)
        private byte[] BuildDirectionalAverages_ThreeTablesCsv(IEnumerable<TripDataset> datasets)
        {
            var sb = new StringBuilder();

            var peakOrder = new[] { "AM", "MID", "PM" };
            var dirOrder = new[] { "SB", "NB", "EB", "WB", "UNKNOWN" };

            foreach (var peak in peakOrder)
            {
                var peakDatasets = datasets
                    .Where(d => ComputeDatasetPeak(d.Rows).ToString().Equals(peak, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (!peakDatasets.Any())
                    continue;

                sb.AppendLine($"{peak} DIRECTIONAL AVERAGES");
                sb.AppendLine("Direction,AvgTravelTimeMin,AvgDistanceKm,AvgTravelSpeedKph,AvgRunningSpeedKph,AvgDelayMin,AvgDelayLengthKm");

                foreach (var dir in dirOrder)
                {
                    var dirDatasets = peakDatasets
                        .Where(d => (ComputeDatasetDirection(d.Rows) ?? "Unknown")
                        .Equals(dir, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (!dirDatasets.Any())
                        continue;

                    var summaries = new List<AnalysisSummary>();

                    foreach (var d in dirDatasets)
                    {
                        var anchors = GetActiveAnchorsForTrip(d.Rows);
                        anchors = MergeAnchorsInTripOrder(d.Rows, anchors, _state.ManualCpKm);

                        if (anchors.Count < 2) continue;

                        var (_, _, sum) = AnalyzeTrip(d.Rows, anchors);
                        summaries.Add(sum);
                    }

                    if (!summaries.Any())
                        continue;

                    sb.AppendLine(
                        $"{(dir == "UNKNOWN" ? "Unknown" : dir)}," +
                        $"{summaries.Average(x => x.TotalTravelTimeMin):0.##}," +
                        $"{summaries.Average(x => x.TotalDistanceKm):0.###}," +
                        $"{summaries.Average(x => x.AvgTravelSpeed):0.##}," +
                        $"{summaries.Average(x => x.AvgRunningSpeed):0.##}," +
                        $"{summaries.Average(x => x.TotalDelayMin):0.##}," +
                        $"{summaries.Average(x => x.TotalDelayLength) / 1000.0:0.###}"
                    );
                }

                sb.AppendLine(); // blank line between tables
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        [HttpPost("/export_dir_tables_zip")]
        public IActionResult ExportDirectionalTablesZip(
        string region = "UnknownRegion",
        string roadNameOrSections = "UnknownRoad")
        {
            if (!_state.Datasets.Any())
                return BadRequest("Upload files first.");

            var selectedIds = (Request.HasFormContentType
                    ? Request.Form["selected_files"].ToArray()
                    : Array.Empty<string>())
                .ToHashSet();

            var chosen = _state.Datasets.Where(d => selectedIds.Contains(d.Id)).ToList();
            if (!chosen.Any())
                return BadRequest("No dataset selected.");

            region = SafePathPart(region);
            roadNameOrSections = SafePathPart(roadNameOrSections);

            var peaks = new[] { "AM", "MID", "PM" };

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var byDate = chosen
                    .Select(d => new { ds = d, info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path) })
                    .Where(x => x.info != null)
                    .GroupBy(x => x.info!.Value.date)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.ds).ToList());

                foreach (var kv in byDate)
                {
                    var date = kv.Key;
                    var list = kv.Value;

                    foreach (var pk in peaks)
                    {
                        var bytes = BuildDirectionalTableCsvForPeak(list, pk);
                        if (bytes.Length == 0) continue;

                        var root = ZipRoot(region, roadNameOrSections, date);
                        var entryPath = $"{root}/DirectionalAverages/{pk}.csv";

                        var entry = zip.CreateEntry(entryPath, CompressionLevel.Fastest);
                        using var es = entry.Open();
                        es.Write(bytes, 0, bytes.Length);
                    }
                }
            }


            var outName = $"DirectionalAverages_AM-MID-PM_{region}_{roadNameOrSections}_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            return File(ms.ToArray(), "application/zip", outName);
        }

        // Builds ONE CSV in your exact "Metric x Direction" layout for a given peak (AM/MID/PM)
        private byte[] BuildDirectionalTableCsvForPeak(List<TripDataset> datasets, string peakCode)
        {
            peakCode = (peakCode ?? "").Trim().ToUpperInvariant();
            if (peakCode != "AM" && peakCode != "MID" && peakCode != "PM")
                return Array.Empty<byte>();

            // filter datasets by peak
            var dsPeak = datasets
                .Where(d => ComputeDatasetPeak(d.Rows).ToString().Equals(peakCode, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!dsPeak.Any())
                return Array.Empty<byte>();

            // directions we support, in consistent order
            var dirOrder = new[] { "NB", "SB", "EB", "WB", "UNKNOWN" };

            // per-direction list of summaries (each dataset -> AnalyzeTrip summary)
            var perDir = new Dictionary<string, List<AnalysisSummary>>(StringComparer.OrdinalIgnoreCase);

            foreach (var d in dsPeak)
            {
                var dir = (ComputeDatasetDirection(d.Rows) ?? "Unknown").Trim().ToUpperInvariant();
                if (dir == "") dir = "UNKNOWN";
                if (!dirOrder.Contains(dir)) dir = "UNKNOWN";

                var anchors = GetActiveAnchorsForTrip(d.Rows);
                anchors = MergeAnchorsInTripOrder(d.Rows, anchors, _state.ManualCpKm);
                if (anchors.Count < 2) continue;

                var (_, _, sum) = AnalyzeTrip(d.Rows, anchors);

                if (!perDir.TryGetValue(dir, out var list))
                {
                    list = new List<AnalysisSummary>();
                    perDir[dir] = list;
                }
                list.Add(sum);
            }

            // keep only dirs that exist
            var presentDirs = dirOrder.Where(d => perDir.ContainsKey(d) && perDir[d].Count > 0).ToList();
            if (presentDirs.Count == 0) return Array.Empty<byte>();

            // header: Metric,<dir full names>,Units
            string DirFull(string d) => d switch
            {
                "NB" => "Northbound",
                "SB" => "Southbound",
                "EB" => "Eastbound",
                "WB" => "Westbound",
                _ => "Unknown"
            };

            string Cell(string? s) => EscapeCsv(s ?? "");

            string GetHHMMSS(double minutes) => FormatMinToHHMMSS(minutes);

            // Helper: compute averages for a dir
            AnalysisSummary AvgSum(string dir)
            {
                // Method A: ratio-of-totals across trips
                return Aggregate_MethodA(perDir[dir]);
            }

            var sb = new StringBuilder();

            // Title line (optional but helpful)
            sb.AppendLine($"{peakCode} Directional Averages");
            sb.Append("Metric");
            foreach (var d in presentDirs) sb.Append(',').Append(Cell(DirFull(d)));
            sb.AppendLine(",Units");

            // Row builder
            void Row(string metric, Func<AnalysisSummary, string> selector, string units)
            {
                sb.Append(Cell(metric));
                foreach (var d in presentDirs)
                {
                    var a = AvgSum(d);
                    sb.Append(',').Append(Cell(selector(a)));
                }
                sb.Append(',').Append(Cell(units));
                sb.AppendLine();
            }

            // Your exact rows + formatting
            Row("Avg Travel Time",
                a => GetHHMMSS(a.TotalTravelTimeMin),
                "hh:mm:ss");

            Row("Avg Distance",
                a => a.TotalDistanceKm.ToString("0.##", CultureInfo.InvariantCulture),
                "km");

            Row("Avg Travel Speed",
                a => a.AvgTravelSpeed.ToString("0.##", CultureInfo.InvariantCulture),
                "kph");

            Row("Avg Running Speed",
                a => a.AvgRunningSpeed.ToString("0.##", CultureInfo.InvariantCulture),
                "kph");

            Row("Avg Delay Time",
                a => GetHHMMSS(a.TotalDelayMin),
                "hh:mm:ss");

            Row("Avg Delay Length",
                a => (a.TotalDelayLength / 1000.0).ToString("0.##", CultureInfo.InvariantCulture),
                "km");

            return Encoding.UTF8.GetBytes(sb.ToString());
        }


        // --------------------------------------------------------------------
        // Build CSV bytes from the raw rows you already keep in memory
        // Assumption: each row is Dictionary<string,string>
        // --------------------------------------------------------------------

        private static byte[] BuildOriginalCsvFromRows(List<Dictionary<string, string>> rows)
        {
            if (rows == null || rows.Count == 0)
                return Encoding.UTF8.GetBytes("No rows.\n");

            // union of all headers (stable order: first row keys then others)
            var headers = new List<string>();
            var headerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var k in rows[0].Keys)
            {
                if (headerSet.Add(k)) headers.Add(k);
            }

            foreach (var r in rows)
            {
                foreach (var k in r.Keys)
                {
                    if (headerSet.Add(k)) headers.Add(k);
                }
            }

            static string Esc(string s)
            {
                if (s == null) return "";
                if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
                    return $"\"{s.Replace("\"", "\"\"")}\"";
                return s;
            }

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", headers.Select(Esc)));

            foreach (var r in rows)
            {
                var line = headers.Select(h => r.TryGetValue(h, out var v) ? Esc(v ?? "") : "");
                sb.AppendLine(string.Join(",", line));
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // --------------------------------------------------------------------
        // Directional Averages CSV (group by direction)
        // --------------------------------------------------------------------
        private byte[] BuildDirectionalAveragesCsv(List<TtdsWeb.Models.TripDataset> datasets)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Direction,AvgTravelTime(min),AvgDistance(km),AvgTravelSpeed(kph),AvgRunningSpeed(kph),AvgDelay(min),AvgDelayLength(km)");

            foreach (var grp in datasets.GroupBy(d => ComputeDatasetDirection(d.Rows)))
            {
                var allSegs = new List<TtdsWeb.Models.SegmentResult>();

                foreach (var d in grp)
                {
                    var anchors = GetActiveAnchorsForTrip(d.Rows);
                    anchors = MergeAnchorsInTripOrder(d.Rows, anchors, _state.ManualCpKm);

                    if (anchors.Count < 2) continue;

                    var (results, _, _) = AnalyzeTrip(d.Rows, anchors);
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

                sb.AppendLine(
                    $"{SafePathPart(grp.Key)}," +
                    $"{avgTravelMin:0.##}," +
                    $"{avgDistKm:0.###}," +
                    $"{avgTravelKph:0.##}," +
                    $"{avgRunKph:0.##}," +
                    $"{avgDelayMin:0.##}," +
                    $"{avgDelayKm:0.###}"
                );
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        


        // Replace this with your real method/service that builds PeakGroups + DirectionSummaries
        private MultiAnalyzeViewModel GetMultiAnalyzeViewModel(string? region, string? roadNameOrSections)
        {
            return new MultiAnalyzeViewModel();
        }
        //-----------------------------------------------------------------------------------------------------------------//
        [HttpPost("/export_segment_analysis_zip")]
        public IActionResult ExportSegmentAnalysisZip(string region = "UnknownRegion", string roadNameOrSections = "UnknownRoad")
        {
            if (!_state.Datasets.Any())
                return BadRequest("Upload files first.");

            var selectedIds = (Request.HasFormContentType
                    ? Request.Form["selected_files"].ToArray()
                    : Array.Empty<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var chosen = selectedIds.Count > 0
                ? _state.Datasets.Where(d => selectedIds.Contains(d.Id)).ToList()
                : _state.Datasets.ToList();

            if (!chosen.Any())
                return BadRequest("No dataset selected.");

            region = SafePathPart(region);
            roadNameOrSections = SafePathPart(roadNameOrSections);

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                int written = 0;

                foreach (var d in chosen)
                {
                    var info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path);
                    if (info == null) continue;

                    var (tripNo, dtToken, date, vehCode, vehName) = info.Value;

                    var peakCode = ComputeDatasetPeak(d.Rows).ToString();  // AM/MID/PM/OFF
                    var period = PeakFolder(peakCode);

                    var direction = ComputeDatasetDirection(d.Rows);
                    direction = string.IsNullOrWhiteSpace(direction) ? "UNK" : direction.Trim().ToUpperInvariant();

                    var anchors = GetActiveAnchorsForTrip(d.Rows);
                    anchors = MergeAnchorsInTripOrder(d.Rows, anchors, _state.ManualCpKm);

                    // ✅ define ONCE
                    var root = ZipRoot(region, roadNameOrSections, date);

                    // ✅ keep anchors inside SegmentAnalysis folder (recommended)
                    var anchorsBaseFolder = $"{root}/SegmentAnalysis/{period}";

                    // ===== EXPORT ANCHORS (CP / KM USED IN ANALYSIS) =====
                    var anchorsCsv = BuildAnchorsCsv(anchors);
                    var e1 = zip.CreateEntry($"{anchorsBaseFolder}/tables/anchors.csv", CompressionLevel.Fastest);
                    using (var es1 = e1.Open())
                        es1.Write(anchorsCsv, 0, anchorsCsv.Length);

                    var anchorsGeo = BuildAnchorsGeoJson(anchors);
                    var e2 = zip.CreateEntry($"{anchorsBaseFolder}/GIS/anchors.geojson", CompressionLevel.Fastest);
                    using (var es2 = e2.Open())
                        es2.Write(anchorsGeo, 0, anchorsGeo.Length);

                    // ===== SEGMENT ANALYSIS CSV =====
                    byte[] csvBytes;
                    if (anchors.Count < 2)
                    {
                        csvBytes = Encoding.UTF8.GetBytes("Not enough anchor points to compute Segment Analysis.\n");
                    }
                    else
                    {
                        var (results, _, _) = AnalyzeTrip(d.Rows, anchors);
                        csvBytes = BuildResultsCsv(results);
                    }

                    var zipPath = $"{root}/SegmentAnalysis/{period}/{tripNo}_{dtToken}-{direction}.csv";
                    var entry = zip.CreateEntry(zipPath, CompressionLevel.Fastest);
                    using (var es = entry.Open())
                        es.Write(csvBytes, 0, csvBytes.Length);

                    written++;
                }

                if (written == 0)
                {
                    var note = zip.CreateEntry("README_NO_FILES_EXPORTED.txt", CompressionLevel.Fastest);
                    using (var ns = note.Open())
                    {
                        var msg = Encoding.UTF8.GetBytes(
                            "No files were exported.\n" +
                            "Reason: filenames did not match -<tripNo>_YYYYMMDD-HHMMSS.\n"
                        );
                        ns.Write(msg, 0, msg.Length);
                    }
                }
            }

            var outName = $"SegmentAnalysis_{region}_{roadNameOrSections}_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            return File(ms.ToArray(), "application/zip", outName);
        }


        // ---------- helpers for parsing + folders ----------
        private static (int tripNo, string date, string time)? ParseTripNoDateTime(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            var baseName = Path.GetFileName(fileName);

            // Extract _YYYYMMDD-HHMMSS
            var us = baseName.LastIndexOf('_');
            if (us < 0) return null;

            var after = baseName.Substring(us + 1);
            var token = after.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return null;

            var dtParts = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (dtParts.Length < 2) return null;

            var date = dtParts[0].Trim(); // YYYYMMDD
            var time = dtParts[1].Trim(); // HHMMSS

            // Trip number = number after the 7th '-'
            var dashParts = baseName.Split('-');
            int tripNo = 0;

            if (dashParts.Length > 7)
            {
                var chunk = dashParts[7];                 // e.g. "1_20250716"
                var numStr = chunk.Split('_').FirstOrDefault() ?? "";
                int.TryParse(numStr, out tripNo);
            }

            if (tripNo <= 0) tripNo = 1;
            return (tripNo, date, time);
        }

        private static string VehicleNameFromCode(string? code) => (code ?? "").Trim() switch
        {
            "1" => "Private Car",
            "2" => "UV",
            "3" => "Jeepney",
            "4" => "Bus",
            _ => "UnknownVehicle"
        };

        // Extracts VehicleCode from "GPX_<n>_..."
        // Extracts tripNo + dtToken from "...-<tripNo>_YYYYMMDD-HHMMSS..."
        private static (string tripNo, string dtToken, string date, string vehCode, string vehName)?
            ParseTripInfoFromFilename(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            // vehicle: GPX_1_....
            var mv = System.Text.RegularExpressions.Regex.Match(
                name,
                @"\bGPX_(\d+)_",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            var vehCode = mv.Success ? mv.Groups[1].Value : "";
            var vehName = VehicleNameFromCode(vehCode);

            // trip + datetime: ...-3_20250716-180525...
            var mt = System.Text.RegularExpressions.Regex.Match(
                name,
                @"-(\d+)_((\d{8})-(\d{6}))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            if (!mt.Success) return null;

            var tripNo = mt.Groups[1].Value;
            var dtToken = mt.Groups[2].Value; // YYYYMMDD-HHMMSS
            var date = mt.Groups[3].Value;    // YYYYMMDD

            return (tripNo, dtToken, date, vehCode, vehName);
        }

        // ===================== SHAPEFILE EXPORT HELPERS =====================

        // WGS84 (EPSG:4326) projection file content



        private const string WGS84_PRJ =
        @"GEOGCS[""WGS 84"",
        DATUM[""WGS_1984"",
        SPHEROID[""WGS 84"",6378137,298.257223563]],
        PRIMEM[""Greenwich"",0],
        UNIT[""degree"",0.0174532925199433]]";

        // Writes delay LineString shapefile (speed < 5 kph) + delay type (from CAUSE_MAP mode)
        // Returns full path to .shp file written on disk
        private string WriteDelayLinesShapeFile(TripDataset d, string outFolder, string baseNameNoExt)
        {
            Directory.CreateDirectory(outFolder);

            var gf = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
            var feats = new List<IFeature>();

            // We will chunk by "status" (delay vs moving)
            var chunk = new List<TripRow>();
            string? chunkStatus = null; // "delay" or "moving"

            // cache these once per dataset
            var info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path);
            var peak = PeakFolder(ComputeDatasetPeak(d.Rows).ToString());
            var dir = (ComputeDatasetDirection(d.Rows) ?? "UNK").Trim().ToUpperInvariant();

            string tripNo = "0";
            string date = "";
            string dtToken = "";

            if (info.HasValue)
            {
                var t = info.Value;
                tripNo = t.tripNo;
                dtToken = t.dtToken;
                date = t.date;
            }


            void Flush()
            {
                if (chunk.Count < 2) { chunk.Clear(); chunkStatus = null; return; }

                // build coordinates; skip invalid points
                var coords = chunk
                    .Select(r => new { r, lon = Finite(r.SnappedLon), lat = Finite(r.SnappedLat) })
                    .Where(x => !double.IsNaN(x.lon) && !double.IsNaN(x.lat) && Math.Abs(x.lat) <= 90 && Math.Abs(x.lon) <= 180)
                    .Select(x => new Coordinate(x.lon, x.lat))
                    .ToArray();

                if (coords.Length < 2) { chunk.Clear(); chunkStatus = null; return; }

                var line = gf.CreateLineString(coords);

                double lenM = chunk.Sum(r => Math.Max(Finite(r.distanceDiff), 0.0));
                double avgKph = chunk.Average(r => (r.Speed ?? SpeedKph(r)));

                // delay classification + color
                string status = chunkStatus ?? "moving";
                string delayType = (status == "delay") ? "Delay" : "Normal Moving";
                string color = "blue"; // ✅ normal is always blue

                if (status == "delay")
                {
                    var causeIds = chunk
                        .Where(r => r.CauseID.HasValue && CAUSE_MAP.ContainsKey(r.CauseID.Value))
                        .Select(r => r.CauseID!.Value)
                        .ToList();

                    if (causeIds.Count > 0)
                    {
                        var main = causeIds
                            .GroupBy(x => x)
                            .OrderByDescending(g => g.Count())
                            .First().Key;

                        delayType = CAUSE_MAP[main].Label;
                        color = CAUSE_MAP[main].Color;  // ✅ delay color from your chart
                    }
                    else
                    {
                        // no cause -> keep blue or set your default delay color
                        color = "blue";
                    }
                }

                var at = new AttributesTable();
                at.Add("trip_no", tripNo);
                at.Add("date", date);
                at.Add("period", peak);
                at.Add("dir", dir);

                at.Add("status", status);         // moving/delay
                at.Add("color", color);           // "blue", "red", etc.
                at.Add("dly_type", delayType);    // Normal Moving / Intersection / etc.

                at.Add("avg_kph", Math.Round(avgKph, 2));
                at.Add("len_m", Math.Round(lenM, 2));
                at.Add("dly_len_m", Math.Round(status == "delay" ? lenM : 0.0, 2));

                feats.Add(new Feature(line, at));

                chunk.Clear();
                chunkStatus = null;
            }

            foreach (var r in d.Rows)
            {
                var sp = (r.Speed ?? SpeedKph(r));
                var status = sp < 5.0 ? "delay" : "moving";

                if (chunkStatus == null)
                {
                    chunkStatus = status;
                    chunk.Add(r);
                }
                else if (status == chunkStatus)
                {
                    chunk.Add(r);
                }
                else
                {
                    Flush();
                    chunkStatus = status;
                    chunk.Add(r);
                }
            }
            Flush();

            if (feats.Count == 0)
                return "";

            var shpPath = Path.Combine(outFolder, baseNameNoExt + ".shp");

            var writer = new ShapefileDataWriter(shpPath, gf)
            {
                Header = ShapefileDataWriter.GetHeader(feats[0], feats.Count)
            };
            writer.Write(feats);

            System.IO.File.WriteAllText(Path.Combine(outFolder, baseNameNoExt + ".prj"), WGS84_PRJ);
            System.IO.File.WriteAllText(Path.Combine(outFolder, baseNameNoExt + ".cpg"), "UTF-8");

            return shpPath;
        }


        // Writes points shapefile for ALL rows (same “table columns” you have in TripRow)
        // Returns full path to .shp file written on disk
        private string WriteTripPointsShapeFile(TripDataset d, string outFolder, string baseNameNoExt)
        {
            Directory.CreateDirectory(outFolder);

            var gf = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
            var feats = new List<IFeature>();

            foreach (var r in d.Rows)
            {
                if (double.IsNaN(r.SnappedLat) || double.IsNaN(r.SnappedLon)) continue;

                var pt = gf.CreatePoint(new Coordinate(Finite(r.SnappedLon), Finite(r.SnappedLat)));

                var at = new AttributesTable();
                at.Add("ts", r.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss") ?? "");
                at.Add("lat", Math.Round(Finite(r.SnappedLat), 7));
                at.Add("lon", Math.Round(Finite(r.SnappedLon), 7));
                at.Add("secDiff", Math.Round(Finite(r.secDiff), 3));
                at.Add("dist_m", Math.Round(Finite(r.distanceDiff), 3));
                at.Add("speed", Math.Round((r.Speed ?? SpeedKph(r)), 3));
                at.Add("cause_id", r.CauseID ?? 0);

                if (r.CauseID.HasValue && CAUSE_MAP.ContainsKey(r.CauseID.Value))
                    at.Add("cause", CAUSE_MAP[r.CauseID.Value].Label);
                else
                    at.Add("cause", "");

                feats.Add(new Feature(pt, at));
            }

            if (feats.Count == 0) return "";

            var shpPath = Path.Combine(outFolder, baseNameNoExt + ".shp");

            var writer = new ShapefileDataWriter(shpPath, gf)
            {
                Header = ShapefileDataWriter.GetHeader(feats[0], feats.Count)
            };
            writer.Write(feats);

            System.IO.File.WriteAllText(Path.Combine(outFolder, baseNameNoExt + ".prj"), WGS84_PRJ);
            System.IO.File.WriteAllText(Path.Combine(outFolder, baseNameNoExt + ".cpg"), "UTF-8");

            return shpPath;
        }
        private static string SafeZipPath(string? p)
        {
            p = (p ?? "").Replace("\\", "/").Trim();
            p = p.Trim('/');
            // block weird stuff
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

        // Adds .shp + .shx + .dbf + .prj + .cpg into ZIP under zipFolder
        private static void AddShapeSidecarsToZip(ZipArchive zip, string shpFile, string zipFolder)
        {
            if (string.IsNullOrWhiteSpace(shpFile)) return;
            if (!System.IO.File.Exists(shpFile)) return;

            zipFolder = SafeZipPath(zipFolder); // <- safe folder only

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


        [HttpPost("/export_shapes_zip")]
        public IActionResult ExportShapesZip(string region = "UnknownRegion", string roadNameOrSections = "UnknownRoad")
        {
            if (!_state.Datasets.Any())
                return BadRequest("Upload files first.");

            var selectedIds = (Request.HasFormContentType
                    ? Request.Form["selected_files"].ToArray()
                    : Array.Empty<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var chosen = selectedIds.Count > 0
                ? _state.Datasets.Where(d => selectedIds.Contains(d.Id)).ToList()
                : _state.Datasets.ToList();

            if (!chosen.Any())
                return BadRequest("No dataset selected.");

            region = SafePathPart(region);
            roadNameOrSections = SafePathPart(roadNameOrSections);


            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                int written = 0;

                foreach (var d in chosen)
                {
                    var info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path);
                    if (info == null) continue;

                    var (tripNo, dtToken, date, vehCode, vehName) = info.Value;

                    var peakCode = ComputeDatasetPeak(d.Rows).ToString(); // AM/MID/PM/OFF
                    var period = PeakFolder(peakCode);

                    var direction = ComputeDatasetDirection(d.Rows);
                    direction = string.IsNullOrWhiteSpace(direction) ? "UNK" : direction.Trim().ToUpperInvariant();

                    // temp folder per dataset
                    var tmpRoot = Path.Combine(Path.GetTempPath(), "ttds_shp_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tmpRoot);

                    try
                    {
                        var baseName = $"{tripNo}_{dtToken}-{direction}";

                        // write shapefiles to temp
                        var delShp = WriteDelayLinesShapeFile(d, tmpRoot, baseName + "_delays");

                        var ptsShp = WriteTripPointsShapeFile(d, tmpRoot, baseName + "_points");

                        // add sidecars into zip folder: date/shp/period/
                        var zipFolder = $"{region}/{roadNameOrSections}/{date}/Shapes/shp/{period}";

                        AddShapeSidecarsToZip(zip, delShp, zipFolder);
                        AddShapeSidecarsToZip(zip, ptsShp, zipFolder);

                        written++;
                    }
                    finally
                    {
                        try { Directory.Delete(tmpRoot, true); } catch { }
                    }
                }

                if (written == 0)
                {
                    var note = zip.CreateEntry("README_NO_FILES_EXPORTED.txt", CompressionLevel.Fastest);
                    using (var ns = note.Open())
                    {
                        var msg = Encoding.UTF8.GetBytes(
                            "No shapefiles were exported.\n" +
                            "Reason: filenames did not match -<tripNo>_YYYYMMDD-HHMMSS.\n"
                        );
                        ns.Write(msg, 0, msg.Length);
                    }
                }
            }

            var outName = $"Shapes_{region}_{roadNameOrSections}_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            return File(ms.ToArray(), "application/zip", outName);
        }

        public class GraphUploadItem
        {
            public string Folder { get; set; } = "";
            public string FileName { get; set; } = "";
            public string DataUrl { get; set; } = "";
        }

        public class ExportAllWithGraphsRequest
        {
            public string? Region { get; set; }
            public string? RoadNameOrSections { get; set; }
            public List<string>? SelectedIds { get; set; }     // dataset IDs
            public List<GraphUploadItem>? Graphs { get; set; } // base64 images from browser
        }

        [HttpPost("/export_all_with_graphs_zip")]
        public IActionResult ExportAllWithGraphsZip([FromBody] ExportAllWithGraphsRequest req)
        {
            if (!_state.Datasets.Any())
                return BadRequest("Upload files first.");

            var (regionSafe, roadSafe) = ResolveRegionRoad(req.Region, req.RoadNameOrSections);

            var selectedSet = (req.SelectedIds ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var chosen = selectedSet.Count > 0
                ? _state.Datasets.Where(d => selectedSet.Contains(d.Id)).ToList()
                : _state.Datasets.ToList();

            if (!chosen.Any())
                return BadRequest("No dataset selected.");

            var byDateVehicle = chosen
                .Select(d => new { ds = d, info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path) })
                .Where(x => x.info != null)
                .GroupBy(x => new
                {
                    date = x.info!.Value.date,
                    vehicle = CanonVehicleFolder(x.info!.Value.vehName)
                })
                .ToDictionary(g => g.Key, g => g.Select(x => x.ds).ToList());

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                // ----- datasets -----
                foreach (var kv in byDateVehicle)
                {
                    var date = kv.Key.date;
                    var vehicle = kv.Key.vehicle;
                    var list = kv.Value;

                    // ✅ FIX: define root (you commented it out before)
                    var root = $"{ZipRoot(regionSafe, roadSafe, date)}/{vehicle}";

                    //AddDirectionalAveragesToZip_ByDate(zip, list, $"{root}/DirectionalAverages");
                    AddSegmentAnalysisToZip(zip, list, $"{root}/SegmentAnalysis");
                    AddShapesToZip(zip, list, $"{root}/Shapes");

                    // ✅ OPTIONAL (but you asked): export KM/CP used in analysis (per trip) with lat/lon
                    foreach (var d in list)
                    {
                        var info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path);
                        if (info == null) continue;

                        var (tripNo, dtToken, _, _, _) = info.Value;

                        var peakCode = ComputeDatasetPeak(d.Rows).ToString();
                        var period = PeakFolder(peakCode);

                        var dir = ComputeDatasetDirection(d.Rows);
                        dir = string.IsNullOrWhiteSpace(dir) ? "UNK" : dir.Trim().ToUpperInvariant();

                        var anchors = GetActiveAnchorsForTrip(d.Rows);
                        anchors = MergeAnchorsInTripOrder(d.Rows, anchors, _state.ManualCpKm);

                        if (anchors.Count < 1) continue;

                        var anchorsCsv = BuildAnchorsCsv(anchors);
                        var e1 = zip.CreateEntry($"{root}/KM-CP Detected/{period}/tables/anchors_{tripNo}_{dtToken}-{dir}.csv", CompressionLevel.Fastest);
                        using (var es1 = e1.Open())
                            es1.Write(anchorsCsv, 0, anchorsCsv.Length);

                        var anchorsGeo = BuildAnchorsGeoJson(anchors);
                        var e2 = zip.CreateEntry($"{root}/KM-CP Detected/{period}/GIS/anchors_{tripNo}_{dtToken}-{dir}.geojson", CompressionLevel.Fastest);
                        using (var es2 = e2.Open())
                            es2.Write(anchorsGeo, 0, anchorsGeo.Length);
                    }
                }

                // ----- graphs -----
                int gWritten = 0;

                foreach (var it in req.Graphs ?? new List<GraphUploadItem>())
                {
                    if (string.IsNullOrWhiteSpace(it.DataUrl)) continue;

                    var comma = it.DataUrl.IndexOf(',');
                    if (comma <= 0) continue;

                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(it.DataUrl.Substring(comma + 1)); }
                    catch { continue; }

                    var folderIn = SafeZipPath(it.Folder);
                    var file = SafeZipFile(it.FileName);

                    var folder = RebaseGraphFolder(folderIn, regionSafe, roadSafe);

                    folder = folder.Replace("/UnknownRegion/UnknownRoad/", "/");
                    if (folder.EndsWith("/UnknownRegion/UnknownRoad", StringComparison.OrdinalIgnoreCase))
                        folder = folder[..^("/UnknownRegion/UnknownRoad".Length)];
                    if (folder.Equals("UnknownRegion/UnknownRoad", StringComparison.OrdinalIgnoreCase))
                        folder = $"{regionSafe}/{roadSafe}";

                    var entryName = string.IsNullOrWhiteSpace(folder)
                        ? file
                        : $"{folder.TrimEnd('/')}/{file}".Replace("\\", "/");

                    var e = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                    using var es = e.Open();
                    es.Write(bytes, 0, bytes.Length);

                    gWritten++;
                }

                if (byDateVehicle.Count == 0 && gWritten == 0)
                {
                    var e = zip.CreateEntry("README_NO_FILES_EXPORTED.txt", CompressionLevel.Fastest);
                    using var es = e.Open();
                    var msg = Encoding.UTF8.GetBytes("No datasets matched and no graphs received.\n");
                    es.Write(msg, 0, msg.Length);
                }
            }

            return File(ms.ToArray(), "application/zip",
                $"{regionSafe}_{roadSafe}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        }

        [HttpPost("/export_all_zip")]
        public IActionResult ExportAllZip(string region = "UnknownRegion", string roadNameOrSections = "UnknownRoad")
        {
            if (!_state.Datasets.Any())
                return BadRequest("Upload files first.");

            var selectedIds = (Request.HasFormContentType
                    ? Request.Form["selected_files"].ToArray()
                    : Array.Empty<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var chosen = selectedIds.Count > 0
                ? _state.Datasets.Where(d => selectedIds.Contains(d.Id)).ToList()
                : _state.Datasets.ToList();

            if (!chosen.Any())
                return BadRequest("No dataset selected.");

            // ✅ Region/Road fix (optional but recommended)
            var (regionSafe, roadSafe) = ResolveRegionRoad(
    (string.Equals(region, "UnknownRegion", StringComparison.OrdinalIgnoreCase) ? null : region),
    (string.Equals(roadNameOrSections, "UnknownRoad", StringComparison.OrdinalIgnoreCase) ? null : roadNameOrSections)
);

            // group datasets by (date, vehicle)
            var byDateVehicle = chosen
                .Select(d => new { ds = d, info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path) })
                .Where(x => x.info != null)
                .GroupBy(x => new
                {
                    date = x.info!.Value.date,
                    vehicle = CanonVehicleFolder(x.info!.Value.vehName)

                })
                .ToDictionary(g => g.Key, g => g.Select(x => x.ds).ToList());

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var kv in byDateVehicle)
                {
                    var date = kv.Key.date;
                    var vehicle = kv.Key.vehicle;
                    var list = kv.Value;

                    var root = $"{ZipRoot(regionSafe, roadSafe, date)}/{vehicle}";
                    //AddDirectionalAveragesToZip_ByDate(zip, list, $"{root}/DirectionalAverages");
                    AddSegmentAnalysisToZip(zip, list, $"{root}/SegmentAnalysis");
                    AddShapesToZip(zip, list, $"{root}/Shapes");
                }

                if (byDateVehicle.Count == 0)
                {
                    var e = zip.CreateEntry("README_NO_FILES_EXPORTED.txt", CompressionLevel.Fastest);
                    using var es = e.Open();
                    var msg = Encoding.UTF8.GetBytes(
                        "No datasets matched expected patterns (GPX_<veh>_... and -<trip>_YYYYMMDD-HHMMSS).\n"
                    );
                    es.Write(msg, 0, msg.Length);
                }
            }

            // ✅ THIS RETURN is what fixes CS0161
            return File(
                ms.ToArray(),
                "application/zip",
                $"{regionSafe}_{roadSafe}_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
            );
        }

        [IgnoreAntiforgeryToken]
        //[HttpPost("/export_graphs_zip")]
        [HttpPost("/export_graphs_zip")]
        public IActionResult ExportGraphsZip([FromBody] GraphZipRequest req)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                int written = 0;

                foreach (var it in req.Items ?? new List<GraphZipItem>())
                {
                    if (string.IsNullOrWhiteSpace(it.DataUrl)) continue;

                    var comma = it.DataUrl.IndexOf(',');
                    if (comma <= 0) continue;

                    byte[] bytes;
                    try
                    {
                        var b64 = it.DataUrl.Substring(comma + 1);
                        bytes = Convert.FromBase64String(b64);
                    }
                    catch { continue; }

                    var folder = SafeZipPath(it.Folder);
                    var file = SafeZipFile(it.FileName);

                    var entryName = string.IsNullOrWhiteSpace(folder)
                        ? file
                        : $"{folder.Trim().Trim('/')}/{file}".Replace("\\", "/");

                    var e = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                    using var es = e.Open();
                    es.Write(bytes, 0, bytes.Length);

                    written++;
                }

                if (written == 0)
                {
                    var e = zip.CreateEntry("README_NO_GRAPHS_EXPORTED.txt", CompressionLevel.Fastest);
                    using var es = e.Open();
                    var msg = Encoding.UTF8.GetBytes(
                        "No graphs were exported. Check canvas selector (canvas.trip-graph) and that dataUrl is generated.\n"
                    );
                    es.Write(msg, 0, msg.Length);
                }
            }

            return File(ms.ToArray(), "application/zip", $"GRAPHS_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        }

    }


}

