using ClosedXML.Excel;
using CsvHelper;
using DocumentFormat.OpenXml.Bibliography;
using DocumentFormat.OpenXml.Drawing.Charts;
using DocumentFormat.OpenXml.Drawing.Diagrams;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
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
using Ttds.Shared;
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
        private readonly ITripAnalysisService _analysisService;
        private readonly IPeakPeriodService _peakService;
        private readonly IGeoDirectionService _geoService;
        private readonly IKmPostRepositoryService _kmRepository;
        private readonly IAnchorDetectionService _anchorService;
        private readonly IZipPackagingService _zipPackagingService;
        private const double CP_DETECT_RADIUS_M = 300.0;

        public HomeController(
            IAppStateAccessor stateAccessor,
            IConfiguration config,
            IWebHostEnvironment env,
            ITripAnalysisService analysisService,
            IPeakPeriodService peakService,
            IGeoDirectionService geoService,
            IKmPostRepositoryService kmRepository,
            IAnchorDetectionService anchorService,
            IZipPackagingService zipPackagingService)
        {
            _state = stateAccessor.Current;
            _config = config;
            _env = env;
            _analysisService = analysisService;
            _peakService = peakService;
            _geoService = geoService;
            _kmRepository = kmRepository;
            _anchorService = anchorService;
            _zipPackagingService = zipPackagingService;
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

        [HttpGet("/")]
        public IActionResult Index() => View();

        [HttpPost("/upload")]
        [DisableRequestSizeLimit]
        [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
        public async Task<IActionResult> Upload(List<IFormFile> files)
        {
            _state.KmRoad = Request.HasFormContentType ? Request.Form["kmRoad"].ToString() : null;

            _state.Datasets.Clear();
            _state.ControlPoints.Clear();
            _state.ManualCpKm.Clear();
            _state.KmGeneratedPoints.Clear();
            _state.LastTripPath = null;
            _state.AnchorSource = "cp";
            _state.KmRegion = null;
            _state.KmRoads = new List<string>();

            var uploadRoot = UploadRoot;

            if (files != null)
            {
                foreach (var f in files)
                {
                    if (f == null || f.Length == 0)
                        continue;

                    var ext = Path.GetExtension(f.FileName);

                    if (ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
                        await ProcessCsvUploadAsync(f, uploadRoot);
                    else if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                        await ProcessZipUploadAsync(f, uploadRoot);
                }
            }

            if (!_state.Datasets.Any())
                return BadRequest("No valid CSV files uploaded (checked direct .csv uploads and .csv entries inside .zip packages).");

            // ---- Auto-apply the KM preview filter ----
            // Instead of requiring the user to open the Anchor Filter panel, switch
            // to KM, pick Region/Road, and click Apply, detect the best-matching
            // Region/Road straight from the uploaded trip's coordinates and pre-load it.
            var (detectedRegion, detectedRoads) = AutoDetectRegionRoad(_state.Datasets);
            if (!string.IsNullOrWhiteSpace(detectedRegion) && detectedRoads.Any())
            {
                _state.AnchorSource = "km";
                _state.KmRegion = detectedRegion;
                _state.KmRoads = detectedRoads;
                _state.KmRoad = string.Join(",", detectedRoads);

                SyncControlPointsFromKmSelection();
            }
            // else: no confident KM match within range of the trip — stays in CP
            // mode (previous default behavior) so manual control points still work.

            return RenderMapMulti();
        }

        // ---------------------------------------------------------------
        // Upload helpers: accepts both loose .csv files and .zip packages.
        // ---------------------------------------------------------------
        private async Task ProcessCsvUploadAsync(IFormFile f, string uploadRoot)
        {
            var safeName = EnsureUniqueFileName(uploadRoot, Path.GetFileName(f.FileName));
            var path = Path.Combine(uploadRoot, safeName);

            await using (var fs = System.IO.File.Create(path))
                await f.CopyToAsync(fs);

            AddDatasetFromCsvFile(path, f.FileName);
        }

        private async Task ProcessZipUploadAsync(IFormFile f, string uploadRoot)
        {
            var tempZipPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(f.FileName));

            await using (var fs = System.IO.File.Create(tempZipPath))
                await f.CopyToAsync(fs);

            try
            {
                using var archive = ZipFile.OpenRead(tempZipPath);

                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    if (!entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;

                    var safeName = EnsureUniqueFileName(uploadRoot, entry.Name);
                    var destPath = Path.Combine(uploadRoot, safeName);

                    entry.ExtractToFile(destPath, overwrite: true);
                    AddDatasetFromCsvFile(destPath, entry.FullName);
                }
            }
            finally
            {
                try { System.IO.File.Delete(tempZipPath); } catch { /* best-effort cleanup */ }
            }
        }

        private void AddDatasetFromCsvFile(string path, string originalFileName)
        {
            var rows = _analysisService.ReadTripCsv(path);
            if (!rows.Any()) return;

            _state.Datasets.Add(new TripDataset
            {
                FileName = originalFileName,
                Path = path,
                Rows = rows,
                Coords = rows.Select(r => new[] { r.SnappedLat, r.SnappedLon }).ToList()
            });

            _state.LastTripPath = path;
        }

        private static string EnsureUniqueFileName(string folder, string fileName)
        {
            fileName = Path.GetFileName(fileName);
            var candidate = fileName;
            var nameNoExt = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            var counter = 2;

            while (System.IO.File.Exists(Path.Combine(folder, candidate)))
            {
                candidate = $"{nameNoExt}_{counter}{ext}";
                counter++;
            }

            return candidate;
        }

        // ---------------------------------------------------------------
        // Auto-detects the (Region, Road) that best matches the uploaded trip(s),
        // by nearest-KM-post voting — same approach used in GPXClean's
        // DetectRegionRoad / RegionRoadDetectionService, adapted for TtdsWeb's
        // KmPostRepositoryService-backed KM post table.
        // ---------------------------------------------------------------
        private (string? region, List<string> roads) AutoDetectRegionRoad(List<TripDataset> datasets)
        {
            const int SAMPLE_EVERY = 10;
            const double MAX_MATCH_METERS = 300.0; // matches CP_DETECT_RADIUS_M used elsewhere

            string dbPath;
            try
            {
                dbPath = _kmRepository.ResolveKmDbPath();
            }
            catch (FileNotFoundException)
            {
                return (null, new List<string>());
            }

            if (string.IsNullOrWhiteSpace(dbPath) || !System.IO.File.Exists(dbPath))
                return (null, new List<string>());

            var votes = new Dictionary<(string region, string road), int>();

            foreach (var ds in datasets)
            {
                var kmPosts = _kmRepository.LoadKmPostsForTrip(ds.Rows, dbPath, region: null, roads: null, bufferMeters: 3000.0);
                if (kmPosts.Count == 0) continue;

                for (int i = 0; i < ds.Rows.Count; i += SAMPLE_EVERY)
                {
                    var r = ds.Rows[i];

                    KmPostRow? best = null;
                    double bestDist = double.MaxValue;

                    foreach (var km in kmPosts)
                    {
                        var d = Geo.DistanceMeters(r.SnappedLat, r.SnappedLon, km.Lat, km.Lon);
                        if (d < bestDist) { bestDist = d; best = km; }
                    }

                    if (best != null && bestDist <= MAX_MATCH_METERS &&
                        !string.IsNullOrWhiteSpace(best.Region) && !string.IsNullOrWhiteSpace(best.Road))
                    {
                        var key = (best.Region!, best.Road!);
                        votes[key] = votes.TryGetValue(key, out var c) ? c + 1 : 1;
                    }
                }
            }

            if (votes.Count == 0) return (null, new List<string>());

            // Winning region = whichever region collected the most matched samples overall.
            var topRegion = votes
                .GroupBy(v => v.Key.region)
                .OrderByDescending(g => g.Sum(x => x.Value))
                .First().Key;

            // Every road within that region that got votes — covers corridors made
            // of multiple road segments/names.
            var roadsForRegion = votes
                .Where(v => v.Key.region == topRegion)
                .OrderByDescending(v => v.Value)
                .Select(v => v.Key.road)
                .Distinct()
                .ToList();

            return (topRegion, roadsForRegion);
        }

        private IActionResult RenderMapMulti()
        {
            if (!_state.Datasets.Any())
                return RedirectToAction("Index");

            var vm = new MultiMapViewModel
            {
                Items = _state.Datasets.Select(d =>
                {
                    var peak = _peakService.ComputeDatasetPeak(d.Rows);
                    return new MultiMapViewModel.Item
                    {
                        Id = d.Id,
                        Name = d.FileName,
                        Coords = d.Coords,
                        Direction = _geoService.ComputeDatasetDirection(d.Rows),
                        PeakCode = peak.ToString(),
                        PeakLabel = _peakService.PeakLabel(peak)
                    };
                }).ToList()
            };

            var allAnchors = new List<ControlPoint>();

            foreach (var ds in _state.Datasets)
            {
                var a = GetActiveAnchorsForTrip(ds.Rows);
                if (a != null && a.Count > 0)
                    allAnchors.AddRange(a);
            }

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

        [HttpGet("/km/regions")]
        public IActionResult GetKmRegions()
        {
            if (!_state.Datasets.Any()) return Json(new List<string>());

            var dbPath = _kmRepository.ResolveKmDbPath();
            if (!System.IO.File.Exists(dbPath)) return Json(new List<string>());

            var preview = _state.Datasets.First();
            var (minLat, maxLat, minLon, maxLon) = _geoService.ComputeBbox(preview.Rows, bufferMeters: 50);

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

            var dbPath = _kmRepository.ResolveKmDbPath();
            if (!System.IO.File.Exists(dbPath)) return Json(new List<string>());

            var preview = _state.Datasets.First();
            var (minLat, maxLat, minLon, maxLon) = _geoService.ComputeBbox(preview.Rows, bufferMeters: 3000);

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

        private List<ControlPoint> GetActiveAnchorsForTrip(List<TripRow> df)
        {
            var mode = (_state.AnchorSource ?? "cp");

            if (mode == "km")
            {
                var kmAnchors = _anchorService.BuildKmAnchorsForRows(df);

                return kmAnchors
                    .Concat(_state.ManualCpKm)
                    .GroupBy(a => a.ControlPointId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
            }

            return _state.ControlPoints.ToList();
        }
        private void AddCleanedDatasetsToZip(ZipArchive zip, List<TripDataset> datasets, string zipBaseFolder)
        {
            foreach (var d in datasets)
            {
                if (string.IsNullOrWhiteSpace(d.Path) || !System.IO.File.Exists(d.Path))
                    continue;

                var info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path);
                string entryName;

                if (info != null)
                {
                    var (tripNo, dtToken, _, _, _) = info.Value;
                    var dir = _geoService.ComputeDatasetDirection(d.Rows) ?? "UNK";
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

        private void SyncControlPointsFromKmSelection()
        {
            if (!_state.Datasets.Any()) return;

            var preview = _state.Datasets.First();
            var df = preview.Rows;

            var dbPath = _kmRepository.ResolveKmDbPath();
            if (!System.IO.File.Exists(dbPath)) return;

            IEnumerable<string>? roads = null;
            if (_state.KmRoads?.Count > 0) roads = _state.KmRoads;
            else if (!string.IsNullOrWhiteSpace(_state.KmRoad)) roads = KmPostRepositoryService.SplitCsv(_state.KmRoad);

            var kmPosts = _kmRepository.LoadKmPostsForTrip(df, dbPath, _state.KmRegion, roads, bufferMeters: 3000.0);
            if (kmPosts.Count < 2) return;

            var kmAnchors = _anchorService.BuildKmAnchorsForTrip(df, kmPosts);
            if (kmAnchors.Count < 2) return;

            var filtered = _anchorService.FilterAnchorsToVisited(df, kmAnchors, CP_DETECT_RADIUS_M, CP_DETECT_RADIUS_M);

            _state.KmGeneratedPoints.Clear();
            _state.KmGeneratedPoints.AddRange(filtered.Count >= 2 ? filtered : kmAnchors);
        }

        [HttpPost("/anchor_preview")]
        public IActionResult AnchorPreview(string source, string? region, [FromForm] string[]? roads)
        {
            _state.AnchorSource = (source?.Trim().ToLowerInvariant() == "km") ? "km" : "cp";

            _state.KmRegion = string.IsNullOrWhiteSpace(region) ? null : region.Trim();

            _state.KmRoads = roads?.Where(r => !string.IsNullOrWhiteSpace(r))
                                   .Select(r => r.Trim())
                                   .Distinct()
                                   .ToList() ?? new List<string>();

            _state.KmRoad = (_state.KmRoads.Count > 0)
                ? string.Join(",", _state.KmRoads)
                : null;

            if (_state.AnchorSource == "km")
                SyncControlPointsFromKmSelection();

            return RenderMapMulti();
        }

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

            var analyzed = new List<MultiAnalyzeViewModel.DatasetAnalysis>();

            foreach (var d in chosen)
            {
                var anchors = GetActiveAnchorsForTrip(d.Rows);
                anchors = MergeAnchorsInTripOrder(d.Rows, anchors, _state.ManualCpKm);

                if (anchors.Count < 2) continue;

                var (results, segments, summary) = _analysisService.AnalyzeTrip(d.Rows, anchors);

                var peak = _peakService.ComputeDatasetPeak(d.Rows);
                var dir = _geoService.ComputeDatasetDirection(d.Rows);

                analyzed.Add(new MultiAnalyzeViewModel.DatasetAnalysis
                {
                    Id = d.Id,
                    Name = d.FileName,
                    Results = results,
                    Segments = segments.Cast<object>().ToList(),
                    Summary = summary,
                    PeakCode = peak.ToString(),
                    PeakLabel = _peakService.PeakLabel(peak),
                    Direction = dir
                });
            }

            if (analyzed.Count == 0)
                return BadRequest("No usable datasets (not enough anchors / empty analysis).");

            vm.Datasets = analyzed;

            vm.OverallSummary = new AnalysisSummary
            {
                TotalTravelTimeMin = _peakService.Round2(analyzed.Average(x => x.Summary.TotalTravelTimeMin)),
                TotalDistanceKm = _peakService.Round2(analyzed.Average(x => x.Summary.TotalDistanceKm)),
                AvgTravelSpeed = _peakService.Round2(analyzed.Average(x => x.Summary.AvgTravelSpeed)),
                AvgRunningSpeed = _peakService.Round2(analyzed.Average(x => x.Summary.AvgRunningSpeed)),
                TotalDelayMin = _peakService.Round2(analyzed.Average(x => x.Summary.TotalDelayMin)),
                TotalDelayLength = _peakService.Round2(analyzed.Average(x => x.Summary.TotalDelayLength))
            };

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
                    AvgTravelTimeMin = _peakService.Round2(s.AvgTravelTimeMin),
                    AvgDistanceKm = _peakService.Round2(s.AvgDistanceKm),
                    AvgTravelSpeed = _peakService.Round2(s.AvgTravelSpeed),
                    AvgRunningSpeed = _peakService.Round2(s.AvgRunningSpeed),
                    AvgDelayMin = _peakService.Round2(s.AvgDelayMin),
                    AvgDelayLength = _peakService.Round2(s.AvgDelayLength)
                });
            }

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
                    PeakLabel = groupDatasets.FirstOrDefault()?.PeakLabel ?? ""
                };

                g.Datasets.AddRange(groupDatasets);

                g.OverallSummary = new AnalysisSummary
                {
                    TotalTravelTimeMin = _peakService.Round2(g.Datasets.Average(x => x.Summary.TotalTravelTimeMin)),
                    TotalDistanceKm = _peakService.Round2(g.Datasets.Average(x => x.Summary.TotalDistanceKm)),
                    AvgTravelSpeed = _peakService.Round2(g.Datasets.Average(x => x.Summary.AvgTravelSpeed)),
                    AvgRunningSpeed = _peakService.Round2(g.Datasets.Average(x => x.Summary.AvgRunningSpeed)),
                    TotalDelayMin = _peakService.Round2(g.Datasets.Average(x => x.Summary.TotalDelayMin)),
                    TotalDelayLength = _peakService.Round2(g.Datasets.Average(x => x.Summary.TotalDelayLength))
                };

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
                        AvgTravelTimeMin = _peakService.Round2(s.AvgTravelTimeMin),
                        AvgDistanceKm = _peakService.Round2(s.AvgDistanceKm),
                        AvgTravelSpeed = _peakService.Round2(s.AvgTravelSpeed),
                        AvgRunningSpeed = _peakService.Round2(s.AvgRunningSpeed),
                        AvgDelayMin = _peakService.Round2(s.AvgDelayMin),
                        AvgDelayLength = _peakService.Round2(s.AvgDelayLength)
                    });
                }

                g.SegmentResults = g.Datasets.SelectMany(x => x.Results).ToList();
                g.Segments = g.Datasets.SelectMany(x => x.Segments).ToList();

                vm.PeakGroups.Add(g);
            }

            var preview = chosen.First();
            var anchors2 = GetActiveAnchorsForTrip(preview.Rows);

            vm.CpData = anchors2
                .Select(cp => new { cp_id = cp.ControlPointId, lat = cp.Lat, lon = cp.Lng })
                .Cast<object>()
                .ToList();

            vm.SelectedRegion = _state.KmRegion;

            vm.SelectedRoad = _state.KmRoads?.FirstOrDefault()
                              ?? _state.KmRoad;

            vm.RegionList = string.IsNullOrWhiteSpace(_state.KmRegion)
                ? new List<string>()
                : new List<string> { _state.KmRegion };

            vm.RoadsByRegion = new Dictionary<string, List<string>>();

            if (!string.IsNullOrWhiteSpace(_state.KmRegion))
            {
                vm.RoadsByRegion[_state.KmRegion] =
                    _state.KmRoads?.ToList()
                    ?? new List<string>();
            }

            return View("ResultMulti", vm);
        }

        [HttpPost("/analyze")]
        public IActionResult Analyze()
        {
            if (string.IsNullOrEmpty(_state.LastTripPath))
                return BadRequest("Missing trip data.");

            var df = _analysisService.ReadTripCsv(_state.LastTripPath!);
            if (!df.Any()) return BadRequest("CSV has no rows.");

            var anchors = GetActiveAnchorsForTrip(df);

            anchors = MergeAnchorsInTripOrder(df, anchors, _state.ManualCpKm);

            if (anchors.Count < 2)
                return BadRequest("Not enough anchor points (CP or KM) found for this trip.");

            var selected = (Request.HasFormContentType ? Request.Form["selected_cps"].ToArray() : Array.Empty<string>())
                .Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToHashSet();

            if (selected.Any() && _state.AnchorSource == "cp")
                anchors = anchors.Where(cp => selected.Contains(cp.ControlPointId)).ToList();

            var (results, segments, summary) = _analysisService.AnalyzeTrip(df, anchors);

            var markers = anchors;
            if ((_state.AnchorSource ?? "cp") == "km")
            {
                var filtered = _anchorService.FilterAnchorsToVisited(df, anchors, CP_DETECT_RADIUS_M, CP_DETECT_RADIUS_M);
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

        public class AddCpRequest
        {
            public double lat { get; set; }
            public double lng { get; set; }
            public string? name { get; set; }
            public string? mode { get; set; }
        }

        [IgnoreAntiforgeryToken]
        [HttpPost("/add_cp")]
        public IActionResult AddCp([FromBody] AddCpRequest body)
        {
            if (body == null) return BadRequest("Missing body");

            var cpId = !string.IsNullOrWhiteSpace(body.name)
                ? body.name.Trim()
                : "CP" + DateTime.Now.ToString("HHmmss");

            var target = (body.mode ?? "cp").ToLowerInvariant() == "km"
                ? _state.ManualCpKm
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

        [HttpGet]
        public IActionResult GoToDashboard()
        {
            var dashboardUrl = _config["Services:Dashboard"];

            if (string.IsNullOrWhiteSpace(dashboardUrl))
                return StatusCode(500, "Dashboard URL is not configured.");

            return Redirect(dashboardUrl);
        }

        [HttpGet("/go_to_reportgen")]
        public IActionResult GoToReportGen()
        {
            var reportGenUrl = _config["Services:ReportGen"];

            if (string.IsNullOrWhiteSpace(reportGenUrl))
                return StatusCode(500, "ReportGen URL is not configured.");

            return Redirect(reportGenUrl);
        }

        [HttpPost("/set_anchor")]
        public IActionResult SetAnchor(string source = "cp", string? region = null, string? road = null)
        {
            _state.AnchorSource = (source?.Trim().ToLowerInvariant() == "km") ? "km" : "cp";
            _state.KmRegion = region;
            _state.KmRoad = road;

            if (string.IsNullOrEmpty(_state.KmDbPath))
                _state.KmDbPath = _kmRepository.ResolveKmDbPath();

            return RedirectToAction("Index");
        }

        [IgnoreAntiforgeryToken]
        [HttpPost("/update_cp_position")]
        public IActionResult UpdateCpPosition([FromBody] UpdateCpRequest req)
        {
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

        private static string FullDirName(string code) => code switch
        {
            "SB" => "Southbound",
            "NB" => "Northbound",
            "EB" => "Eastbound",
            "WB" => "Westbound",
            _ => "Unknown"
        };

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

        private (string regionSafe, string roadSafe) ResolveRegionRoad(string? region, string? road)
        {
            bool IsUnknown(string? s) =>
                string.IsNullOrWhiteSpace(s) ||
                s.Equals("UnknownRegion", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("UnknownRoad", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

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

        private byte[] BuildDirectionalTableCsvForPeak(List<TripDataset> datasets, string peakCode)
        {
            peakCode = (peakCode ?? "").Trim().ToUpperInvariant();
            if (peakCode != "AM" && peakCode != "MID" && peakCode != "PM")
                return Array.Empty<byte>();

            var dsPeak = datasets
                .Where(d => _peakService.ComputeDatasetPeak(d.Rows).ToString().Equals(peakCode, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!dsPeak.Any())
                return Array.Empty<byte>();

            var dirOrder = new[] { "NB", "SB", "EB", "WB", "UNKNOWN" };
            var sb = new StringBuilder();

            sb.AppendLine($"{peakCode} Directional Averages");
            sb.Append("Metric");
            foreach (var d in dirOrder) sb.Append(',').Append(GetDirectionLabel(d));
            sb.AppendLine(",Units");

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

        private static string GetDirectionLabel(string code) => code switch
        {
            "NB" => "Northbound",
            "SB" => "Southbound",
            "EB" => "Eastbound",
            "WB" => "Westbound",
            _ => "Unknown"
        };

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

                    var peakCode = _peakService.ComputeDatasetPeak(d.Rows).ToString();
                    var period = _peakService.PeakFolder(peakCode);

                    var direction = _geoService.ComputeDatasetDirection(d.Rows);
                    direction = string.IsNullOrWhiteSpace(direction) ? "UNK" : direction.Trim().ToUpperInvariant();

                    var anchors = GetActiveAnchorsForTrip(d.Rows);
                    anchors = MergeAnchorsInTripOrder(d.Rows, anchors, _state.ManualCpKm);

                    var root = ZipRoot(region, roadNameOrSections, date);
                    var anchorsBaseFolder = $"{root}/SegmentAnalysis/{period}";

                    var anchorsCsv = BuildAnchorsCsv(anchors);
                    var e1 = zip.CreateEntry($"{anchorsBaseFolder}/tables/anchors.csv", CompressionLevel.Fastest);
                    using (var es1 = e1.Open())
                        es1.Write(anchorsCsv, 0, anchorsCsv.Length);

                    var anchorsGeo = BuildAnchorsGeoJson(anchors);
                    var e2 = zip.CreateEntry($"{anchorsBaseFolder}/GIS/anchors.geojson", CompressionLevel.Fastest);
                    using (var es2 = e2.Open())
                        es2.Write(anchorsGeo, 0, anchorsGeo.Length);

                    byte[] csvBytes;
                    if (anchors.Count < 2)
                    {
                        csvBytes = Encoding.UTF8.GetBytes("Not enough anchor points to compute Segment Analysis.\n");
                    }
                    else
                    {
                        var (results, _, _) = _analysisService.AnalyzeTrip(d.Rows, anchors);
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

        private static (string tripNo, string dtToken, string date, string vehCode, string vehName)?
            ParseTripInfoFromFilename(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

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

                    var peakCode = _peakService.ComputeDatasetPeak(d.Rows).ToString();
                    var period = _peakService.PeakFolder(peakCode);

                    var direction = _geoService.ComputeDatasetDirection(d.Rows);
                    direction = string.IsNullOrWhiteSpace(direction) ? "UNK" : direction.Trim().ToUpperInvariant();

                    var tmpRoot = Path.Combine(Path.GetTempPath(), "ttds_shp_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tmpRoot);

                    try
                    {
                        var baseName = $"{tripNo}_{dtToken}-{direction}";

                        var delShp = WriteDelayLinesShapeFile(d, tmpRoot, baseName + "_delays");
                        var ptsShp = WriteTripPointsShapeFile(d, tmpRoot, baseName + "_points");

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
            public List<string>? SelectedIds { get; set; }
            public List<GraphUploadItem>? Graphs { get; set; }
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
                foreach (var kv in byDateVehicle)
                {
                    var date = kv.Key.date;
                    var vehicle = kv.Key.vehicle;
                    var list = kv.Value;

                    var root = $"{ZipRoot(regionSafe, roadSafe, date)}/{vehicle}";

                    AddCleanedDatasetsToZip(zip, list, $"{root}/Snapped-Cleaned");
                    AddSegmentAnalysisToZip(zip, list, $"{root}/SegmentAnalysis");
                    AddShapesToZip(zip, list, $"{root}/Shapes");

                    foreach (var d in list)
                    {
                        var info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path);
                        if (info == null) continue;

                        var (tripNo, dtToken, _, _, _) = info.Value;

                        var peakCode = _peakService.ComputeDatasetPeak(d.Rows).ToString();
                        var period = _peakService.PeakFolder(peakCode);

                        var dir = _geoService.ComputeDatasetDirection(d.Rows);
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

        [HttpGet("/import/{batchId}")]
        public async Task<IActionResult> ImportFromGpx(string batchId)
        {
            if (string.IsNullOrWhiteSpace(batchId))
                return BadRequest("Missing batch ID for import.");

            // 1. Locate the cleaned directory generated by GpxController
            // This matches: Path.Combine(_services.BatchStorageRoot, batchId, "gpxclean")
            var baseStorageRoot = _config["Services:BatchStorageRoot"] ?? Path.Combine(Path.GetTempPath(), "ttds_batches");
            var shareDir = Path.Combine(baseStorageRoot, batchId, "gpxclean");

            if (!Directory.Exists(shareDir))
                return NotFound($"No cleaned data found for batch ID: {batchId}. The session may have expired.");

            // 2. Clear previous session state so we start fresh with the imported batch
            _state.Datasets.Clear();
            _state.ControlPoints.Clear();
            _state.ManualCpKm.Clear();
            _state.KmGeneratedPoints.Clear();
            _state.LastTripPath = null;
            _state.AnchorSource = "cp";
            _state.KmRegion = null;
            _state.KmRoads = new List<string>();

            var uploadRoot = UploadRoot;
            var csvFiles = Directory.GetFiles(shareDir, "*.csv");

            if (csvFiles.Length == 0)
                return BadRequest("The batch folder was found, but it contained no cleaned CSV files.");

            // 3. Copy files to the local upload directory and load them into AppState
            foreach (var file in csvFiles)
            {
                var fileName = Path.GetFileName(file);

                // Skip any manifest files if they were generated
                if (fileName.Equals("MANIFEST.csv", StringComparison.OrdinalIgnoreCase))
                    continue;

                var safeName = EnsureUniqueFileName(uploadRoot, fileName);
                var destPath = Path.Combine(uploadRoot, safeName);

                // Copy from the shared inter-process folder to the local working directory
                System.IO.File.Copy(file, destPath, overwrite: true);

                // Utilize your existing helper to parse and register the dataset
                AddDatasetFromCsvFile(destPath, fileName);
            }

            if (!_state.Datasets.Any())
                return BadRequest("Failed to load any valid trip datasets from the imported batch.");

            // 4. Run Auto-Detection (just like your standard Upload method does)
            var (detectedRegion, detectedRoads) = AutoDetectRegionRoad(_state.Datasets);
            if (!string.IsNullOrWhiteSpace(detectedRegion) && detectedRoads.Any())
            {
                _state.AnchorSource = "km";
                _state.KmRegion = detectedRegion;
                _state.KmRoads = detectedRoads;
                _state.KmRoad = string.Join(",", detectedRoads);

                SyncControlPointsFromKmSelection();
            }

            // 5. Optionally clean up the temporary share directory after successful import
            try { Directory.Delete(shareDir, true); } catch { /* Best effort cleanup */ }

            // 6. Direct the user straight to the multi-map viewer with the loaded data!
            return RenderMapMulti();
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

            var (regionSafe, roadSafe) = ResolveRegionRoad(
                (string.Equals(region, "UnknownRegion", StringComparison.OrdinalIgnoreCase) ? null : region),
                (string.Equals(roadNameOrSections, "UnknownRoad", StringComparison.OrdinalIgnoreCase) ? null : roadNameOrSections)
            );

            var ms = BuildAllZipStream(chosen, regionSafe, roadSafe);

            return File(
                ms.ToArray(),
                "application/zip",
                $"{regionSafe}_{roadSafe}_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
            );
        }

        private MemoryStream BuildAllZipStream(List<TripDataset> chosen, string regionSafe, string roadSafe)
        {
            var byDateVehicle = chosen
                .Select(d => new { ds = d, info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path) })
                .Where(x => x.info != null)
                .GroupBy(x => new
                {
                    date = x.info!.Value.date,
                    vehicle = CanonVehicleFolder(x.info!.Value.vehName)
                })
                .ToDictionary(g => g.Key, g => g.Select(x => x.ds).ToList());

            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var kv in byDateVehicle)
                {
                    var date = kv.Key.date;
                    var vehicle = kv.Key.vehicle;
                    var list = kv.Value;

                    var root = $"{ZipRoot(regionSafe, roadSafe, date)}/{vehicle}";
                    AddCleanedDatasetsToZip(zip, list, $"{root}/Snapped-Cleaned");
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

            ms.Position = 0;
            return ms;
        }

        [HttpPost("/continue_to_reportgen")]
        public IActionResult ContinueToReportGen(string region = "UnknownRegion", string roadNameOrSections = "UnknownRoad")
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

            var (regionSafe, roadSafe) = ResolveRegionRoad(
                (string.Equals(region, "UnknownRegion", StringComparison.OrdinalIgnoreCase) ? null : region),
                (string.Equals(roadNameOrSections, "UnknownRoad", StringComparison.OrdinalIgnoreCase) ? null : roadNameOrSections)
            );

            var batchId = Guid.NewGuid().ToString("N");
            var baseStorageRoot = _config["Services:BatchStorageRoot"] ?? Path.Combine(Path.GetTempPath(), "ttds_batches");
            var destDir = Path.Combine(baseStorageRoot, batchId, "ttdsweb");
            Directory.CreateDirectory(destDir);

            var byDateVehicle = chosen
                .Select(d => new { ds = d, info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path) })
                .Where(x => x.info != null)
                .GroupBy(x => new
                {
                    date = x.info!.Value.date,
                    vehicle = CanonVehicleFolder(x.info!.Value.vehName)
                })
                .ToDictionary(g => g.Key, g => g.Select(x => x.ds).ToList());

            foreach (var kv in byDateVehicle)
            {
                var date = kv.Key.date;
                var vehicle = kv.Key.vehicle;
                var list = kv.Value;

                var root = Path.Combine(destDir, ZipRoot(regionSafe, roadSafe, date), vehicle);

                AddCleanedDatasetsToFolder(Path.Combine(root, "Snapped-Cleaned"), list);
                AddSegmentAnalysisToFolder(Path.Combine(root, "SegmentAnalysis"), list);
                AddShapesToFolder(Path.Combine(root, "Shapes"), list);
            }

            if (byDateVehicle.Count == 0)
            {
                var readmePath = Path.Combine(destDir, "README_NO_FILES_EXPORTED.txt");
                System.IO.File.WriteAllText(readmePath,
                    "No datasets matched expected patterns (GPX_<veh>_... and -<trip>_YYYYMMDD-HHMMSS).\n");
            }

            var reportGenUrl = _config["Services:ReportGen"];
            if (string.IsNullOrWhiteSpace(reportGenUrl))
                return StatusCode(500, "ReportGen URL is not configured.");

            return Redirect($"{reportGenUrl}/import/{batchId}");
        }

        [IgnoreAntiforgeryToken]
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

        // ===== HELPER METHODS FOR ZIP/SHAPEFILE EXPORTS =====

        private const string WGS84_PRJ = @"GEOGCS[""WGS 84"",
            DATUM[""WGS_1984"",
            SPHEROID[""WGS 84"",6378137,298.257223563]],
            PRIMEM[""Greenwich"",0],
            UNIT[""degree"",0.0174532925199433]]";

        private void AddSegmentAnalysisToZip(ZipArchive zip, List<TripDataset> datasets, string zipBaseFolder)
        {
            foreach (var d in datasets)
            {
                var info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path);
                if (info == null) continue;

                var (tripNo, dtToken, date, vehCode, vehName) = info.Value;

                var peak = _peakService.PeakFolder(_peakService.ComputeDatasetPeak(d.Rows).ToString());
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

        private void AddShapesToZip(ZipArchive zip, List<TripDataset> datasets, string zipBaseFolder)
        {
            foreach (var d in datasets)
            {
                var info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path);
                if (info == null) continue;

                var (tripNo, dtToken, date, vehCode, vehName) = info.Value;

                var peak = _peakService.PeakFolder(_peakService.ComputeDatasetPeak(d.Rows).ToString());
                var dir = _geoService.ComputeDatasetDirection(d.Rows) ?? "UNK";

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

        private void AddCleanedDatasetsToFolder(string destBaseFolder, List<TripDataset> datasets)
        {
            foreach (var d in datasets)
            {
                if (string.IsNullOrWhiteSpace(d.Path) || !System.IO.File.Exists(d.Path))
                    continue;

                var info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path);
                string fileName;

                if (info != null)
                {
                    var (tripNo, dtToken, _, _, _) = info.Value;
                    var dir = _geoService.ComputeDatasetDirection(d.Rows) ?? "UNK";
                    fileName = $"{tripNo}_{dtToken}-{dir}.csv";
                }
                else
                {
                    fileName = SafeZipFile(Path.GetFileName(d.FileName));
                }

                Directory.CreateDirectory(destBaseFolder);
                var destPath = Path.Combine(destBaseFolder, fileName);

                using var fs = System.IO.File.OpenRead(d.Path);
                using var outFs = System.IO.File.Create(destPath);
                fs.CopyTo(outFs);
            }
        }

        private void AddSegmentAnalysisToFolder(string destBaseFolder, List<TripDataset> datasets)
        {
            foreach (var d in datasets)
            {
                var info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path);
                if (info == null) continue;

                var (tripNo, dtToken, date, vehCode, vehName) = info.Value;
                var peak = _peakService.PeakFolder(_peakService.ComputeDatasetPeak(d.Rows).ToString());
                var dir = _geoService.ComputeDatasetDirection(d.Rows) ?? "UNK";

                var anchors = GetActiveAnchorsForTrip(d.Rows);
                anchors = MergeAnchorsInTripOrder(d.Rows, anchors, _state.ManualCpKm);
                if (anchors.Count < 2) continue;

                var (results, _, _) = _analysisService.AnalyzeTrip(d.Rows, anchors);
                var csvBytes = BuildResultsCsv(results);

                var destDir = Path.Combine(destBaseFolder, peak);
                Directory.CreateDirectory(destDir);
                var destPath = Path.Combine(destDir, $"{tripNo}_{dtToken}-{dir}.csv");

                System.IO.File.WriteAllBytes(destPath, csvBytes);
            }
        }

        private void AddShapesToFolder(string destBaseFolder, List<TripDataset> datasets)
        {
            foreach (var d in datasets)
            {
                var info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path);
                if (info == null) continue;

                var (tripNo, dtToken, date, vehCode, vehName) = info.Value;
                var peak = _peakService.PeakFolder(_peakService.ComputeDatasetPeak(d.Rows).ToString());
                var dir = _geoService.ComputeDatasetDirection(d.Rows) ?? "UNK";

                var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);

                try
                {
                    var baseName = $"{tripNo}_{dtToken}-{dir}";
                    var del = WriteDelayLinesShapeFile(d, tmp, baseName + "_delays");
                    var pts = WriteTripPointsShapeFile(d, tmp, baseName + "_points");

                    var shapesDestDir = Path.Combine(destBaseFolder, "shp", peak);
                    AddShapeSidecarsToFolder(del, shapesDestDir);
                    AddShapeSidecarsToFolder(pts, shapesDestDir);
                }
                finally
                {
                    try { Directory.Delete(tmp, true); } catch { }
                }
            }
        }

        private static void AddShapeSidecarsToFolder(string shpFile, string destFolder)
        {
            if (string.IsNullOrWhiteSpace(shpFile) || !System.IO.File.Exists(shpFile))
                return;

            Directory.CreateDirectory(destFolder);

            var baseNoExt = Path.Combine(Path.GetDirectoryName(shpFile)!, Path.GetFileNameWithoutExtension(shpFile));
            var exts = new[] { ".shp", ".shx", ".dbf", ".prj", ".cpg" };

            foreach (var ext in exts)
            {
                var fp = baseNoExt + ext;
                if (!System.IO.File.Exists(fp)) continue;

                var destPath = Path.Combine(destFolder, Path.GetFileName(fp));
                System.IO.File.Copy(fp, destPath, overwrite: true);
            }
        }

        private static string CanonVehicleFolder(string? vehName)
        {
            return SafePathPart(vehName ?? "UnknownVehicle").Replace(" ", "");
        }

        private string WriteDelayLinesShapeFile(TripDataset d, string outFolder, string baseNameNoExt)
        {
            Directory.CreateDirectory(outFolder);

            var gf = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
            var feats = new List<IFeature>();

            var chunk = new List<TripRow>();
            string? chunkStatus = null;

            var info = ParseTripInfoFromFilename(d.FileName) ?? ParseTripInfoFromFilename(d.Path);
            var peak = _peakService.PeakFolder(_peakService.ComputeDatasetPeak(d.Rows).ToString());
            var dir = (_geoService.ComputeDatasetDirection(d.Rows) ?? "UNK").Trim().ToUpperInvariant();

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

                var coords = chunk
                    .Select(r => new { r, lon = Finite(r.SnappedLon), lat = Finite(r.SnappedLat) })
                    .Where(x => !double.IsNaN(x.lon) && !double.IsNaN(x.lat) && Math.Abs(x.lat) <= 90 && Math.Abs(x.lon) <= 180)
                    .Select(x => new Coordinate(x.lon, x.lat))
                    .ToArray();

                if (coords.Length < 2) { chunk.Clear(); chunkStatus = null; return; }

                var line = gf.CreateLineString(coords);

                double lenM = chunk.Sum(r => Math.Max(Finite(r.distanceDiff), 0.0));
                double avgKph = chunk.Average(r => (r.Speed ?? GeoDirectionService.SpeedKph(r)));

                string status = chunkStatus ?? "moving";
                string delayType = (status == "delay") ? "Delay" : "Normal Moving";
                string color = "blue";

                if (status == "delay")
                {
                    var causeIds = chunk
                        .Where(r => r.CauseID.HasValue && DelayConstants.IsValidCauseId(r.CauseID.Value))
                        .Select(r => r.CauseID!.Value)
                        .ToList();

                    if (causeIds.Count > 0)
                    {
                        var main = causeIds
                            .GroupBy(x => x)
                            .OrderByDescending(g => g.Count())
                            .First().Key;

                        var cause = DelayConstants.GetCause(main);
                        if (cause.HasValue)
                        {
                            delayType = cause.Value.Label;
                            color = cause.Value.Color;
                        }
                    }
                    else
                    {
                        color = "blue";
                    }
                }

                var at = new AttributesTable();
                at.Add("trip_no", tripNo);
                at.Add("date", date);
                at.Add("period", peak);
                at.Add("dir", dir);

                at.Add("status", status);
                at.Add("color", color);
                at.Add("dly_type", delayType);

                at.Add("avg_kph", Math.Round(avgKph, 2));
                at.Add("len_m", Math.Round(lenM, 2));
                at.Add("dly_len_m", Math.Round(status == "delay" ? lenM : 0.0, 2));

                feats.Add(new Feature(line, at));

                chunk.Clear();
                chunkStatus = null;
            }

            foreach (var r in d.Rows)
            {
                var sp = (r.Speed ?? GeoDirectionService.SpeedKph(r));
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
                at.Add("speed", Math.Round((r.Speed ?? GeoDirectionService.SpeedKph(r)), 3));
                at.Add("cause_id", r.CauseID ?? 0);

                if (r.CauseID.HasValue && DelayConstants.IsValidCauseId(r.CauseID.Value))
                {
                    var cause = DelayConstants.GetCauseLabel(r.CauseID.Value);
                    at.Add("cause", cause ?? "");
                }
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

        private static double Finite(double v) => (double.IsNaN(v) || double.IsInfinity(v)) ? 0.0 : v;
    }


}
