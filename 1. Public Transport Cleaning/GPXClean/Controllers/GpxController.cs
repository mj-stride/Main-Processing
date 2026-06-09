using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Travel_Time_and_Delay_Web_Application.Models;

namespace Travel_Time_and_Delay_Web_Application.Controllers
{
    public class GpxController : Controller
    {
        private readonly ILogger<GpxController> _log;
        public GpxController(ILogger<GpxController> log) => _log = log;

        // ======================
        // MERGE RULES
        // ======================
        // ✅ You want NB and SB to be separate "trip parts", so direction must match to merge.
        private const bool RequireSameDirectionForMerge = true;

        private const int MergeGapSeconds = 8 * 60;
        private const double MergeJumpMeters = 1200.0;

        // ======================
        // CLEAN RULES (line-level only)
        // ======================
        private const double MaxStepMeters = 1500.0;
        private const double MaxKph = 180.0;
        private const double DuplicateMeters = 0.5;

        // Preview decimation
        private const int PreviewStep = 10;

        // ======================
        // DEBUG ROWS
        // ======================
        private sealed class DebugFileRow
        {
            public string ZipFile { get; set; } = "";
            public string VehicleCode { get; set; } = "";
            public string TripId { get; set; } = "";
            public string DtToken { get; set; } = "";
            public string Direction { get; set; } = "";
            public int RawRows { get; set; }
            public DateTime? FirstTimeUtc { get; set; }
            public DateTime? LastTimeUtc { get; set; }
        }

        private sealed class DebugMergeRow
        {
            public string VehicleCode { get; set; } = "";
            public string TripId { get; set; } = "";
            public string DtToken { get; set; } = "";
            public string DatasetZip { get; set; } = "";
            public string Direction { get; set; } = "";
            public int OrderIndex { get; set; }
            public DateTime StartUtc { get; set; }
            public DateTime EndUtc { get; set; }
            public double StartLat { get; set; }
            public double StartLon { get; set; }
            public double EndLat { get; set; }
            public double EndLon { get; set; }
            public double GapSeconds { get; set; }
            public double JumpMeters { get; set; }
            public bool DirectionMatched { get; set; }
            public bool CanMerge { get; set; }
            public int ProducedPartIndex { get; set; }
        }

        private sealed class DebugCleanEventRow
        {
            public string VehicleCode { get; set; } = "";
            public string TripId { get; set; } = "";
            public string DtToken { get; set; } = "";
            public string Direction { get; set; } = "";
            public int PartIndex { get; set; }
            public string Reason { get; set; } = "";
            public DateTime? PrevTime { get; set; }
            public DateTime? CurTime { get; set; }
            public double PrevLat { get; set; }
            public double PrevLon { get; set; }
            public double CurLat { get; set; }
            public double CurLon { get; set; }
            public double DtSeconds { get; set; }
            public double DistMeters { get; set; }
            public double Kph { get; set; }
            public string SourceZip { get; set; } = "";
        }

        private sealed class DebugTripRow
        {
            public string VehicleCode { get; set; } = "";
            public string TripId { get; set; } = "";
            public string DtToken { get; set; } = "";
            public string Direction { get; set; } = "";
            public int PartIndex { get; set; }
            public int CombinedCount { get; set; }
            public int CleanedCount { get; set; }
            public DateTime? FirstCombined { get; set; }
            public DateTime? LastCombined { get; set; }
            public DateTime? FirstCleaned { get; set; }
            public DateTime? LastCleaned { get; set; }
            public string SourceZips { get; set; } = "";
        }

        private static MemoryStream WriteDebugCsv<T>(IEnumerable<T> rows)
        {
            var ms = new MemoryStream();
            using var sw = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);

            var props = typeof(T).GetProperties();
            sw.WriteLine(string.Join(",", props.Select(p => p.Name)));

            static string Csv(object? v)
            {
                if (v == null) return "";
                if (v is DateTime dt)
                    return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

                var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                bool q = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
                if (!q) return s;
                return $"\"{s.Replace("\"", "\"\"")}\"";
            }

            foreach (var r in rows)
            {
                var vals = props.Select(p => Csv(p.GetValue(r)));
                sw.WriteLine(string.Join(",", vals));
            }

            sw.Flush();
            ms.Position = 0;
            return ms;
        }

        // ======================
        // KM POST HELPERS
        // ======================
        private sealed class KmPostRow
        {
            public string KilometerPost { get; set; } = "";
            public string RegionId { get; set; } = "";
            public string RoadName { get; set; } = "";
            public double Lat { get; set; }
            public double Lon { get; set; }
        }

        private static List<KmPostRow> LoadKmPosts(string dbPath)
        {
            var list = new List<KmPostRow>();
            using var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT kilometerPost, regionId, roadName, latitude, longitude
                FROM tblKilometerPost
                WHERE latitude IS NOT NULL AND longitude IS NOT NULL
            ";

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new KmPostRow
                {
                    KilometerPost = r.IsDBNull(0) ? "" : r.GetString(0),
                    RegionId = r.IsDBNull(1) ? "" : r.GetString(1),
                    RoadName = r.IsDBNull(2) ? "" : r.GetString(2),
                    Lat = r.IsDBNull(3) ? 0 : r.GetDouble(3),
                    Lon = r.IsDBNull(4) ? 0 : r.GetDouble(4),
                });
            }
            return list;
        }

        private static (string? regionId, string? roadName) DetectRegionRoad(
            List<GpxRecord> cleaned,
            List<KmPostRow> kmPosts,
            int sampleEvery = 10,
            double maxMatchMeters = 80.0)
        {
            if (cleaned == null || cleaned.Count == 0 || kmPosts.Count == 0)
                return (null, null);

            var votes = new Dictionary<(string region, string road), int>();

            for (int i = 0; i < cleaned.Count; i += sampleEvery)
            {
                var p = cleaned[i];

                KmPostRow? best = null;
                double bestD = double.MaxValue;

                foreach (var k in kmPosts)
                {
                    var d = HaversineMeters(p.SnappedLat, p.SnappedLon, k.Lat, k.Lon);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = k;
                    }
                }

                if (best != null && bestD <= maxMatchMeters)
                {
                    var key = (best.RegionId ?? "", best.RoadName ?? "");
                    votes[key] = votes.TryGetValue(key, out var c) ? c + 1 : 1;
                }
            }

            if (votes.Count == 0) return (null, null);

            var top = votes.OrderByDescending(x => x.Value).First().Key;
            return (top.region, top.road);
        }

        private static string SafeFilePart(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "UNK";

            var cleaned = new string(
                s.Trim()
                 .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
                 .ToArray()
            );

            cleaned = cleaned.Replace(' ', '_');
            if (cleaned.Length > 40) cleaned = cleaned.Substring(0, 40);
            return cleaned.ToUpperInvariant();
        }

        private static (string datePart, string timePart) ExtractDateTimeFromZipName(string zipFileName)
        {
            var name = Path.GetFileNameWithoutExtension(zipFileName);

            var m1 = Regex.Match(name, @"(20\d{6})-(\d{6})");
            if (m1.Success) return (m1.Groups[1].Value, m1.Groups[2].Value);

            var m2 = Regex.Match(name, @"(20\d{6})(\d{6})");
            if (m2.Success) return (m2.Groups[1].Value, m2.Groups[2].Value);

            return ("UNKDATE", "UNKTIME");
        }

        private static string ExtractDateOnlyFromZipName(string zipFileName)
        {
            var name = Path.GetFileNameWithoutExtension(zipFileName);

            var m1 = Regex.Match(name, @"(20\d{6})-(\d{6})");
            if (m1.Success) return m1.Groups[1].Value;

            var m2 = Regex.Match(name, @"(20\d{6})(\d{6})");
            if (m2.Success) return m2.Groups[1].Value;

            return "UNKDATE";
        }

        // ======================
        // ROUTES
        // ======================
        [HttpGet("/gpx/upload")]
        public IActionResult Upload() => View();

        // ✅ prevent 405 if user opens /gpx/preview-map in browser
        [HttpGet("/gpx/preview-map")]
        public IActionResult PreviewMapRootGet()
        {
            return RedirectToAction("Upload");
        }

        // =========================================================
        // PREVIEW MAP (POST)
        // =========================================================
        [HttpPost("/gpx/preview-map")]
        [RequestSizeLimit(1_500_000_000)]
        public async System.Threading.Tasks.Task<IActionResult> PreviewMap(List<IFormFile> files)
        {
            if (files == null || files.Count == 0)
                return BadRequest("No ZIP files were uploaded.");

            var batchId = Guid.NewGuid().ToString("N");
            var batchDir = Path.Combine(Path.GetTempPath(), "gpx_batch_" + batchId);
            Directory.CreateDirectory(batchDir);

            foreach (var zipFormFile in files)
            {
                if (!Path.GetExtension(zipFormFile.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                var savedZipPath = Path.Combine(batchDir, Path.GetFileName(zipFormFile.FileName));
                using var fs = System.IO.File.Create(savedZipPath);
                await zipFormFile.CopyToAsync(fs);
            }

            return Redirect($"/gpx/preview-map/{batchId}");
        }

        // =========================================================
        // PREVIEW MAP (GET)
        // =========================================================
        [HttpGet("/gpx/preview-map/{batchId}")]
        public IActionResult PreviewMapGet(string batchId)
        {
            var batchDir = Path.Combine(Path.GetTempPath(), "gpx_batch_" + batchId);
            if (!Directory.Exists(batchDir))
                return BadRequest("Batch not found or expired.");

            var vm = BuildPreviewVmFromBatch(batchDir, batchId);
            return View("MapPreview", vm);
        }

        private static GpxPreviewVm BuildPreviewVmFromBatch(string batchDir, string batchId)
        {
            var vm = new GpxPreviewVm { BatchId = batchId };
            XNamespace ns = "http://www.topografix.com/GPX/1/1";

            foreach (var zipPath in Directory.GetFiles(batchDir, "*.zip"))
            {
                var fileName = Path.GetFileName(zipPath);
                var preview = new GpxPreviewFile { FileName = fileName };

                var raw = new List<(DateTime t, double lat, double lon)>();

                using var zipStream = System.IO.File.OpenRead(zipPath);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

                foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        using var entryStream = entry.Open();
                        var doc = XDocument.Load(entryStream);

                        foreach (var pt in doc.Descendants(ns + "trkpt"))
                        {
                            var latAttr = pt.Attribute("lat")?.Value;
                            var lonAttr = pt.Attribute("lon")?.Value;
                            var tStr = pt.Element(ns + "time")?.Value;

                            if (latAttr == null || lonAttr == null || string.IsNullOrWhiteSpace(tStr)) continue;
                            if (!double.TryParse(latAttr, NumberStyles.Any, CultureInfo.InvariantCulture, out var lat)) continue;
                            if (!double.TryParse(lonAttr, NumberStyles.Any, CultureInfo.InvariantCulture, out var lon)) continue;

                            if (!DateTime.TryParse(tStr, CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var tdt))
                                continue;

                            raw.Add((tdt, lat, lon));
                        }
                    }
                    catch { }
                }

                if (raw.Count < 2)
                {
                    vm.Files.Add(preview);
                    continue;
                }

                // light clean just for preview visuals
                var cleaned = QuickCleanPreview(raw, MaxStepMeters, MaxKph);

                

                if (cleaned.Count > 0)
                {
                    preview.Start = new LatLng { Lat = cleaned[0].lat, Lon = cleaned[0].lon };
                    preview.End = new LatLng { Lat = cleaned[^1].lat, Lon = cleaned[^1].lon };
                }

                for (int i = 0; i < cleaned.Count; i += PreviewStep)
                    preview.Points.Add(new LatLng { Lat = cleaned[i].lat, Lon = cleaned[i].lon });

                if (cleaned.Count > 0)
                {
                    var last = cleaned[^1];
                    if (preview.Points.Count == 0 ||
                        preview.Points[^1].Lat != last.lat ||
                        preview.Points[^1].Lon != last.lon)
                    {
                        preview.Points.Add(new LatLng { Lat = last.lat, Lon = last.lon });
                    }
                }

                vm.Files.Add(preview);
            }

            return vm;
        }

        // =========================================================
        // CLEANED ONLY VIEW (POST)
        // =========================================================
        [HttpPost("/gpx/cleaned-only-view")]
        [RequestSizeLimit(1_500_000_000)]
        public IActionResult CleanedOnlyView(string batchId, List<string> selectedFiles)
        {
            if (string.IsNullOrWhiteSpace(batchId))
                return BadRequest("Missing batch id.");

            var batchDir = Path.Combine(Path.GetTempPath(), "gpx_batch_" + batchId);
            if (!Directory.Exists(batchDir))
                return BadRequest("Batch not found or expired.");

            if (selectedFiles == null || selectedFiles.Count == 0)
                return BadRequest("Select at least one file.");

            var kmDbPath = Path.Combine(Directory.GetCurrentDirectory(), "kilometer_post.db");
            var kmPosts = System.IO.File.Exists(kmDbPath) ? LoadKmPosts(kmDbPath) : new List<KmPostRow>();

            // debug collectors (optional for view)
            var debugFiles = new List<DebugFileRow>();

            XNamespace ns = "http://www.topografix.com/GPX/1/1";

            var zipDatasets = LoadZipDatasets(batchDir, selectedFiles, ns, debugFiles);
            if (zipDatasets.Count == 0)
                return BadRequest("No GPX points extracted from selected files.");

            HydrateStartsEnds(zipDatasets);

            var mergeDebug = new List<DebugMergeRow>();
            var cleanDebug = new List<DebugCleanEventRow>();

            var outputs = BuildTripOutputs(zipDatasets, mergeDebug, cleanDebug);
            if (outputs.Count == 0)
                return BadRequest("No grouped trips produced.");

            var vm = new GpxCleanedOnlyVm
            {
                BatchId = batchId,
                Trips = outputs
                    .OrderBy(x => x.VehicleCode)
                    .ThenBy(x => x.TripId)
                    .ThenBy(x => x.PartIndex)
                    .Select(o =>
                    {
                        var tripVm = new GpxCleanedTripVm
                        {
                            VehicleCode = o.VehicleCode,
                            TripId = o.TripId,
                            DtToken = o.DtToken,
                            Direction = o.Direction,
                            PartIndex = o.PartIndex,

                            // ✅ IMPORTANT:
                            // This is only for display. The real list is SourceZipFiles.
                            SourceZipFileName = o.SourceZipFiles.FirstOrDefault() ?? "",

                            // ✅ MUST EXIST in your VM model (add if missing):
                            SourceZipFiles = o.SourceZipFiles.ToList()
                        };

                        var (reg, road) = DetectRegionRoad(o.Cleaned, kmPosts, 10, 80);
                        tripVm.DetectedRegionId = reg;
                        tripVm.DetectedRoadName = road;

                        if (o.Cleaned.Count > 0)
                        {
                            tripVm.Start = new LatLng { Lat = o.Cleaned[0].SnappedLat, Lon = o.Cleaned[0].SnappedLon };
                            tripVm.End = new LatLng { Lat = o.Cleaned[^1].SnappedLat, Lon = o.Cleaned[^1].SnappedLon };

                            int step = 5;
                            for (int i = 0; i < o.Cleaned.Count; i += step)
                                tripVm.Points.Add(new LatLng { Lat = o.Cleaned[i].SnappedLat, Lon = o.Cleaned[i].SnappedLon });

                            var last = o.Cleaned[^1];
                            if (tripVm.Points.Count == 0 ||
                                tripVm.Points[^1].Lat != last.SnappedLat ||
                                tripVm.Points[^1].Lon != last.SnappedLon)
                            {
                                tripVm.Points.Add(new LatLng { Lat = last.SnappedLat, Lon = last.SnappedLon });
                            }
                        }

                        return tripVm;
                    })
                    .ToList()
            };

            return View("CleanedOnly", vm);
        }

        // =========================================================
        // DOWNLOAD ZIP (POST) ✅ includes combined + cleaned + debug
        // =========================================================
        [HttpPost("/gpx/process-selected")]
        public async System.Threading.Tasks.Task<IActionResult> ProcessSelected(string batchId, List<string> selectedFiles)
        {
            if (string.IsNullOrWhiteSpace(batchId))
                return BadRequest("Missing batch id.");

            var batchDir = Path.Combine(Path.GetTempPath(), "gpx_batch_" + batchId);
            if (!Directory.Exists(batchDir))
                return BadRequest("Batch not found or expired.");

            if (selectedFiles == null || selectedFiles.Count == 0)
                return BadRequest("Select at least one file.");

            var kmDbPath = Path.Combine(Directory.GetCurrentDirectory(), "kilometer_post.db");
            var kmPosts = System.IO.File.Exists(kmDbPath) ? LoadKmPosts(kmDbPath) : new List<KmPostRow>();

            var debugFiles = new List<DebugFileRow>();
            var debugMerge = new List<DebugMergeRow>();
            var debugClean = new List<DebugCleanEventRow>();
            var debugTrips = new List<DebugTripRow>();

            XNamespace ns = "http://www.topografix.com/GPX/1/1";

            var zipDatasets = LoadZipDatasets(batchDir, selectedFiles, ns, debugFiles);
            if (zipDatasets.Count == 0)
                return BadRequest("No GPX points extracted from selected files.");

            HydrateStartsEnds(zipDatasets);

            var outputs = BuildTripOutputs(zipDatasets, debugMerge, debugClean);
            if (outputs.Count == 0)
                return BadRequest("No grouped trips produced.");

            foreach (var o in outputs)
            {
                var (reg, road) = DetectRegionRoad(o.Cleaned, kmPosts, 10, 80);
                o.DetectedRegionId = reg;
                o.DetectedRoadName = road;

                var originalZipName = o.Cleaned.FirstOrDefault()?.FilePath ?? o.SourceZipFiles.FirstOrDefault() ?? "";
                var (datePart, timePart) = ExtractDateTimeFromZipName(originalZipName);

                o.EntryName =
                    $"GPX_{o.VehicleCode}_{o.TripId}-{o.DtToken}-{o.Direction}-{o.PartIndex}" +
                    $"_{datePart}-{timePart}_cleaned.csv";
            }

            foreach (var o in outputs)
            {
                debugTrips.Add(new DebugTripRow
                {
                    VehicleCode = o.VehicleCode,
                    TripId = o.TripId,
                    DtToken = o.DtToken,
                    Direction = o.Direction,
                    PartIndex = o.PartIndex,
                    CombinedCount = o.Combined.Count,
                    CleanedCount = o.Cleaned.Count,
                    FirstCombined = o.Combined.FirstOrDefault()?.Timestamp,
                    LastCombined = o.Combined.LastOrDefault()?.Timestamp,
                    FirstCleaned = o.Cleaned.FirstOrDefault()?.Timestamp,
                    LastCleaned = o.Cleaned.LastOrDefault()?.Timestamp,
                    SourceZips = string.Join("|", o.SourceZipFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                });
            }

            var mainPair = outputs
                .GroupBy(o => new { R = o.DetectedRegionId ?? "UNK", D = o.DetectedRoadName ?? "UNK" })
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();

            var zipRegionPart = SafeFilePart(mainPair?.R);
            var zipRoadPart = SafeFilePart(mainPair?.D);

            var mainDate = outputs
                .Select(o => ExtractDateOnlyFromZipName(o.SourceZipFiles.FirstOrDefault() ?? ""))
                .Where(d => !string.IsNullOrWhiteSpace(d) && d != "UNKDATE")
                .GroupBy(d => d)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault() ?? "UNKDATE";

            var outZipStream = new MemoryStream();
            using (var zipOut = new ZipArchive(outZipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var o in outputs
                    .OrderBy(x => x.VehicleCode)
                    .ThenBy(x => x.TripId)
                    .ThenBy(x => x.PartIndex))
                {
                    var cleanedCsv = WriteCsv(o.Cleaned, includeComputed: true);
                    var cleanedName = o.EntryName ?? $"{o.VehicleCode}_{o.TripId}_{o.PartIndex}_cleaned.csv";

                    var e1 = zipOut.CreateEntry(cleanedName, CompressionLevel.Fastest);
                    using (var s1 = e1.Open())
                    {
                        cleanedCsv.Position = 0;
                        await cleanedCsv.CopyToAsync(s1);
                    }

                    //var combinedCsv = WriteCsv(o.Combined, includeComputed: true);
                    //var combinedName = cleanedName.Replace("_cleaned.csv", "_combined.csv", StringComparison.OrdinalIgnoreCase);

                    //var e2 = zipOut.CreateEntry(combinedName, CompressionLevel.Fastest);
                    //using (var s2 = e2.Open())
                    //{
                    //    combinedCsv.Position = 0;
                    //    await combinedCsv.CopyToAsync(s2);
                    //}
                }

                var manifest = zipOut.CreateEntry("MANIFEST.csv", CompressionLevel.Fastest);
                using (var sw = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
                {
                    sw.WriteLine("vehicleCode,tripId,dtToken,direction,partIndex,regionId,roadName,pointsCombined,pointsCleaned,sourceZips");
                    foreach (var o in outputs)
                        sw.WriteLine($"{o.VehicleCode},{o.TripId},{o.DtToken},{o.Direction},{o.PartIndex},{o.DetectedRegionId},{o.DetectedRoadName},{o.Combined.Count},{o.Cleaned.Count},\"{string.Join("|", o.SourceZipFiles)}\"");
                }

                //AddDebugEntry(zipOut, "DEBUG_FILES.csv", WriteDebugCsv(debugFiles));
                //AddDebugEntry(zipOut, "DEBUG_MERGE_LOG.csv", WriteDebugCsv(debugMerge));
                //AddDebugEntry(zipOut, "DEBUG_CLEAN_EVENTS.csv", WriteDebugCsv(debugClean));
                //AddDebugEntry(zipOut, "DEBUG_TRIPS.csv", WriteDebugCsv(debugTrips));
            }

            outZipStream.Position = 0;

            // optional cleanup
            try { Directory.Delete(batchDir, true); } catch { }

            var zipName = $"{zipRegionPart}_{zipRoadPart}_CLEANED_{mainDate}.zip";
            return File(outZipStream, "application/zip", zipName);
        }

        private static void AddDebugEntry(ZipArchive zipOut, string entryName, MemoryStream ms)
        {
            var e = zipOut.CreateEntry(entryName, CompressionLevel.Fastest);
            using var s = e.Open();
            ms.Position = 0;
            ms.CopyTo(s);
        }

        // =========================================================
        // LOADING + GROUPING
        // =========================================================
        private static List<ZipDataset> LoadZipDatasets(
            string batchDir,
            List<string> selectedFiles,
            XNamespace ns,
            List<DebugFileRow> debugFiles)
        {
            var zipDatasets = new List<ZipDataset>();

            foreach (var fileName in selectedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var zipPath = Path.Combine(batchDir, fileName);
                if (!System.IO.File.Exists(zipPath)) continue;

                var meta = ParseZipName(fileName);
                if (meta == null) continue;

                var rows = new List<GpxRecord>();

                using var zipStream = System.IO.File.OpenRead(zipPath);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

                foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        using var entryStream = entry.Open();
                        var doc = XDocument.Load(entryStream);

                        foreach (var pt in doc.Descendants(ns + "trkpt"))
                        {
                            var latAttr = pt.Attribute("lat");
                            var lonAttr = pt.Attribute("lon");
                            if (latAttr == null || lonAttr == null) continue;

                            if (!double.TryParse(latAttr.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var lat)) continue;
                            if (!double.TryParse(lonAttr.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var lon)) continue;

                            DateTime? parsedTime = null;
                            var tStr = pt.Element(ns + "time")?.Value;
                            if (!string.IsNullOrWhiteSpace(tStr) &&
                                DateTime.TryParse(tStr, CultureInfo.InvariantCulture,
                                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var tdt))
                            {
                                parsedTime = tdt;
                            }

                            string? Val(XElement parent, XName name) => parent.Element(name)?.Value;

                            double? ToDouble(string? s)
                                => string.IsNullOrWhiteSpace(s) ? null
                                   : double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

                            int? ToInt(string? s)
                                => string.IsNullOrWhiteSpace(s) ? null
                                   : int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var i) ? i : null;

                            rows.Add(new GpxRecord
                            {
                                Timestamp = parsedTime,
                                SnappedLat = lat,
                                SnappedLon = lon,
                                FilePath = fileName,

                                Speed = ToDouble(Val(pt, ns + "speed")),
                                ModeID = ToDouble(Val(pt, ns + "modeId")),
                                CauseID = ToDouble(Val(pt, ns + "causeId")),
                                Boarding = ToDouble(Val(pt, ns + "boarding")),
                                Alighting = ToInt(Val(pt, ns + "alighting")),
                                OnBoard = ToInt(Val(pt, ns + "onBoard")),
                                KilometerPostID = Val(pt, ns + "kilometerPostId"),
                                DistrictID = Val(pt, ns + "districtId"),
                            });
                        }
                    }
                    catch { }
                }

                rows = rows.Where(r => r.Timestamp.HasValue)
                           .OrderBy(r => r.Timestamp!.Value)
                           .ToList();

                if (rows.Count < 2) continue;

                var dir = ComputeDirection100(rows);

                debugFiles.Add(new DebugFileRow
                {
                    ZipFile = fileName,
                    VehicleCode = meta.Value.VehicleCode,
                    TripId = meta.Value.TripId,
                    DtToken = meta.Value.DtToken,
                    Direction = dir,
                    RawRows = rows.Count,
                    FirstTimeUtc = rows.First().Timestamp,
                    LastTimeUtc = rows.Last().Timestamp
                });

                zipDatasets.Add(new ZipDataset
                {
                    FileName = fileName,
                    VehicleCode = meta.Value.VehicleCode,
                    TripId = meta.Value.TripId,
                    DtToken = meta.Value.DtToken,
                    Direction = dir,
                    RawRows = rows
                });
            }

            return zipDatasets;
        }

        private static void HydrateStartsEnds(List<ZipDataset> zipDatasets)
        {
            foreach (var z in zipDatasets)
            {
                z.StartTimeUtc = z.RawRows.First().Timestamp!.Value;
                z.EndTimeUtc = z.RawRows.Last().Timestamp!.Value;
                z.StartLat = z.RawRows.First().SnappedLat;
                z.StartLon = z.RawRows.First().SnappedLon;
                z.EndLat = z.RawRows.Last().SnappedLat;
                z.EndLon = z.RawRows.Last().SnappedLon;
            }
        }

        private static List<TripOutput> BuildTripOutputs(
            List<ZipDataset> zipDatasets,
            List<DebugMergeRow> mergeDebug,
            List<DebugCleanEventRow> cleanDebug)
        {
            var outputs = new List<TripOutput>();

            // ✅ PartIndex resets per device/trip group.
            // If you want "per VehicleCode only", remove TripId from the key.
            var byVehTrip = zipDatasets
                .GroupBy(z => new { z.VehicleCode, z.TripId })
                .ToList();

            foreach (var g in byVehTrip)
            {
                var ordered = g.OrderBy(x => x.StartTimeUtc).ToList();

                int partIndexCounter = 1;
                TripAccumulator? cur = null;
                int orderIndex = 0;

                foreach (var z in ordered)
                {
                    orderIndex++;

                    bool directionMatched = (cur == null)
                        ? true
                        : string.Equals(cur.Direction, z.Direction, StringComparison.OrdinalIgnoreCase);

                    double gapSec = 0;
                    double jumpM = 0;

                    if (cur != null)
                    {
                        gapSec = (z.StartTimeUtc - cur.EndTimeUtc).TotalSeconds;
                        jumpM = HaversineMeters(cur.EndLat, cur.EndLon, z.StartLat, z.StartLon);
                    }

                    bool canMerge =
                        cur != null &&
                        gapSec <= MergeGapSeconds &&
                        jumpM <= MergeJumpMeters &&
                        (!RequireSameDirectionForMerge || directionMatched);

                    if (cur == null || !canMerge)
                    {
                        if (cur != null)
                            outputs.Add(FinalizeTrip(cur, cleanDebug));

                        var dirKey = string.IsNullOrWhiteSpace(z.Direction) ? "UNK" : z.Direction.Trim().ToUpperInvariant();

                        cur = new TripAccumulator
                        {
                            VehicleCode = z.VehicleCode,
                            TripId = z.TripId,
                            DtToken = z.DtToken,
                            Direction = dirKey,
                            PartIndex = partIndexCounter++,
                            Rows = new List<GpxRecord>(),
                            SourceZipFiles = new List<string>(),

                            StartTimeUtc = z.StartTimeUtc,
                            StartLat = z.StartLat,
                            StartLon = z.StartLon,
                            EndTimeUtc = z.EndTimeUtc,
                            EndLat = z.EndLat,
                            EndLon = z.EndLon
                        };
                    }

                    mergeDebug.Add(new DebugMergeRow
                    {
                        VehicleCode = z.VehicleCode,
                        TripId = z.TripId,
                        DtToken = z.DtToken,
                        DatasetZip = z.FileName,
                        Direction = z.Direction,
                        OrderIndex = orderIndex,
                        StartUtc = z.StartTimeUtc,
                        EndUtc = z.EndTimeUtc,
                        StartLat = z.StartLat,
                        StartLon = z.StartLon,
                        EndLat = z.EndLat,
                        EndLon = z.EndLon,
                        GapSeconds = gapSec,
                        JumpMeters = jumpM,
                        DirectionMatched = directionMatched,
                        CanMerge = canMerge,
                        ProducedPartIndex = cur!.PartIndex
                    });

                    cur!.Rows.AddRange(z.RawRows);
                    cur.SourceZipFiles.Add(z.FileName);

                    cur.EndTimeUtc = z.EndTimeUtc;
                    cur.EndLat = z.EndLat;
                    cur.EndLon = z.EndLon;
                }

                if (cur != null)
                    outputs.Add(FinalizeTrip(cur, cleanDebug));
            }

            return outputs;
        }

        private static TripOutput FinalizeTrip(TripAccumulator acc, List<DebugCleanEventRow> cleanDebug)
        {
            var all = acc.Rows
                .Where(r => r.Timestamp.HasValue)
                .OrderBy(r => r.Timestamp!.Value)
                .ToList();

            var combined = ComputeDiffsKeepAll(all);
            var cleaned = CleanKeepGoing(all, acc, cleanDebug);

            return new TripOutput
            {
                VehicleCode = acc.VehicleCode,
                TripId = acc.TripId,
                DtToken = acc.DtToken,
                Direction = acc.Direction,
                PartIndex = acc.PartIndex,
                Combined = combined,
                Cleaned = cleaned,
                SourceZipFiles = acc.SourceZipFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }
        private static List<GpxRecord> CleanKeepGoing(
    List<GpxRecord> ordered,
    TripAccumulator acc,
    List<DebugCleanEventRow> cleanDebug)
        {
            var kept = new List<GpxRecord>();
            if (ordered.Count == 0) return kept;

            // first row always kept
            var first = CloneWithoutComputed(ordered[0]);
            first.SecDiff = null;
            first.DistanceDiff = null;

            kept.Add(first);

            // only inspect first 2 computed rows
            int firstRowsChecked = 0;

            for (int i = 1; i < ordered.Count; i++)
            {
                var prev = kept[^1];
                var cur = CloneWithoutComputed(ordered[i]);

                if (!prev.Timestamp.HasValue || !cur.Timestamp.HasValue)
                    continue;

                var dt = (cur.Timestamp.Value - prev.Timestamp.Value).TotalSeconds;

                var dist = HaversineMeters(
                    prev.SnappedLat,
                    prev.SnappedLon,
                    cur.SnappedLat,
                    cur.SnappedLon);

                // duplicate
                if (dist < DuplicateMeters)
                {
                    cleanDebug.Add(MkCleanDebug(
                        acc,
                        "DUPLICATE",
                        prev,
                        cur,
                        dt,
                        dist,
                        0));

                    continue;
                }

                // invalid time
                if (dt <= 0)
                {
                    cleanDebug.Add(MkCleanDebug(
                        acc,
                        "TIME_RESET",
                        prev,
                        cur,
                        dt,
                        dist,
                        0));

                    continue;
                }

                var kph = (dist / dt) * 3.6;

                // big jump
                if (dist > MaxStepMeters)
                {
                    cleanDebug.Add(MkCleanDebug(
                        acc,
                        "BIG_JUMP",
                        prev,
                        cur,
                        dt,
                        dist,
                        kph));

                    continue;
                }

                // unrealistic speed
                if (kph > MaxKph)
                {
                    cleanDebug.Add(MkCleanDebug(
                        acc,
                        "HIGH_KPH",
                        prev,
                        cur,
                        dt,
                        dist,
                        kph));

                    continue;
                }

                var secDiff = (int)Math.Round(
                    dt,
                    MidpointRounding.AwayFromZero);

                // ==========================================
                // REMOVE ONLY FIRST 2 OUTLIER ROWS
                // ==========================================
                if (firstRowsChecked < 2)
                {
                    firstRowsChecked++;

                    if (secDiff > 500)
                    {
                        cleanDebug.Add(MkCleanDebug(
                            acc,
                            "FIRST_ROWS_OVER_500_REMOVED",
                            prev,
                            cur,
                            dt,
                            dist,
                            kph));

                        // IMPORTANT:
                        // replace baseline row
                        kept[0] = cur;

                        continue;
                    }
                }

                cur.SecDiff = secDiff;
                cur.DistanceDiff = dist;

                kept.Add(cur);
            }

            return kept;
        }

        private static DebugCleanEventRow MkCleanDebug(
            TripAccumulator acc,
            string reason,
            GpxRecord prev,
            GpxRecord cur,
            double dt,
            double dist,
            double kph)
        {
            return new DebugCleanEventRow
            {
                VehicleCode = acc.VehicleCode,
                TripId = acc.TripId,
                DtToken = acc.DtToken,
                Direction = acc.Direction,
                PartIndex = acc.PartIndex,
                Reason = reason,
                PrevTime = prev.Timestamp,
                CurTime = cur.Timestamp,
                PrevLat = prev.SnappedLat,
                PrevLon = prev.SnappedLon,
                CurLat = cur.SnappedLat,
                CurLon = cur.SnappedLon,
                DtSeconds = dt,
                DistMeters = dist,
                Kph = kph,
                SourceZip = cur.FilePath ?? ""
            };
        }

        private static List<GpxRecord> ComputeDiffsKeepAll(List<GpxRecord> list)
        {
            if (list.Count == 0) return list;

            var outList = new List<GpxRecord>();
            var first = CloneWithoutComputed(list[0]);
            first.SecDiff = null;
            first.DistanceDiff = null;
            outList.Add(first);

            for (int i = 1; i < list.Count; i++)
            {
                var prev = outList[^1];
                var cur = CloneWithoutComputed(list[i]);

                if (prev.Timestamp.HasValue && cur.Timestamp.HasValue)
                {
                    var dt = (cur.Timestamp.Value - prev.Timestamp.Value).TotalSeconds;
                    if (dt > 0)
                    {
                        var dist = HaversineMeters(prev.SnappedLat, prev.SnappedLon, cur.SnappedLat, cur.SnappedLon);
                        cur.SecDiff = (int)Math.Round(dt, MidpointRounding.AwayFromZero);
                        cur.DistanceDiff = dist;
                    }
                    else
                    {
                        cur.SecDiff = null;
                        cur.DistanceDiff = null;
                    }
                }

                outList.Add(cur);
            }

            return outList;
        }

        private static GpxRecord CloneWithoutComputed(GpxRecord r)
        {
            return new GpxRecord
            {
                Group = r.Group,
                Timestamp = r.Timestamp,
                SnappedLat = r.SnappedLat,
                SnappedLon = r.SnappedLon,
                Speed = r.Speed,
                ModeID = r.ModeID,
                CauseID = r.CauseID,
                Boarding = r.Boarding,
                Alighting = r.Alighting,
                OnBoard = r.OnBoard,
                KilometerPostID = r.KilometerPostID,
                FilePath = r.FilePath,
                DistrictID = r.DistrictID
            };
        }

        // =========================================================
        // ZIP NAME PARSER
        // =========================================================
        private static (string VehicleCode, string TripId, string DtToken)? ParseZipName(string fileName)
        {
            var baseName = Path.GetFileName(fileName);
            if (!baseName.StartsWith("GPX_", StringComparison.OrdinalIgnoreCase)) return null;

            var us = baseName.LastIndexOf('_');
            if (us < 0) return null;

            var dtToken = baseName.Substring(us + 1).Replace(".zip", "", StringComparison.OrdinalIgnoreCase).Trim();
            var left = baseName.Substring(0, us);

            var parts = left.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return null;

            var vehicleCode = parts[1].Trim();

            var third = parts[2];
            var dash = third.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (dash.Length < 2) return null;

            var tripId = $"{dash[0]}-{dash[1]}";
            return (vehicleCode, tripId, dtToken);
        }

        // =========================================================
        // DIRECTION
        // =========================================================
        private static string ComputeDirection100(List<GpxRecord> rows)
        {
            var pts = rows
                .Where(r => r.Timestamp.HasValue)
                .OrderBy(r => r.Timestamp!.Value)
                .Select(r => (lat: r.SnappedLat, lon: r.SnappedLon))
                .ToList();

            if (pts.Count < 2) return "UNK";

            int targetPts = Math.Min(101, pts.Count);
            if (targetPts < 2) return "UNK";

            var sampled = new List<(double lat, double lon)>(targetPts);
            for (int k = 0; k < targetPts; k++)
            {
                int idx = (int)Math.Round(k * (pts.Count - 1) / (double)(targetPts - 1));
                sampled.Add(pts[idx]);
            }

            int nb = 0, sb = 0, eb = 0, wb = 0;

            for (int i = 0; i < sampled.Count - 1; i++)
            {
                var a = sampled[i];
                var b = sampled[i + 1];

                if (Math.Abs(a.lat - b.lat) < 1e-9 && Math.Abs(a.lon - b.lon) < 1e-9)
                    continue;

                var brng = Bearing(a.lat, a.lon, b.lat, b.lon);
                var dir = BearingToCardinal(brng);

                switch (dir)
                {
                    case "NB": nb++; break;
                    case "SB": sb++; break;
                    case "EB": eb++; break;
                    case "WB": wb++; break;
                }
            }

            int total = nb + sb + eb + wb;
            if (total == 0) return "UNK";

            if (nb >= sb && nb >= eb && nb >= wb) return "NB";
            if (sb >= nb && sb >= eb && sb >= wb) return "SB";
            if (eb >= nb && eb >= sb && eb >= wb) return "EB";
            return "WB";
        }

        private static string BearingToCardinal(double b)
        {
            if (b < 45 || b > 315) return "NB";
            if (b < 135) return "EB";
            if (b < 225) return "SB";
            return "WB";
        }

        // =========================================================
        // HAVERSINE + BEARING
        // =========================================================
        private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000.0;
            static double DegToRad(double d) => d * Math.PI / 180.0;

            double phi1 = DegToRad(lat1);
            double phi2 = DegToRad(lat2);
            double dphi = DegToRad(lat2 - lat1);
            double dlambda = DegToRad(lon2 - lon1);

            double a = Math.Sin(dphi / 2) * Math.Sin(dphi / 2) +
                       Math.Cos(phi1) * Math.Cos(phi2) *
                       Math.Sin(dlambda / 2) * Math.Sin(dlambda / 2);

            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        private static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            static double ToRad(double d) => d * Math.PI / 180.0;
            static double ToDeg(double r) => r * 180.0 / Math.PI;

            var phi1 = ToRad(lat1);
            var phi2 = ToRad(lat2);
            var dLam = ToRad(lon2 - lon1);

            var y = Math.Sin(dLam) * Math.Cos(phi2);
            var x = Math.Cos(phi1) * Math.Sin(phi2) -
                    Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLam);

            var theta = Math.Atan2(y, x);
            return (ToDeg(theta) + 360.0) % 360.0;
        }

        // =========================================================
        // CSV WRITER
        // =========================================================
        private static MemoryStream WriteCsv(List<GpxRecord> rows, bool includeComputed)
        {
            var ms = new MemoryStream();
            using var sw = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);

            var headers = new List<string>
            {
                "Timestamp","SnappedLat","SnappedLon",
                "Speed","ModeID","CauseID","Boarding","Alighting","OnBoard",
                "KilometerPostID","FilePath","DistrictID"
            };
            if (includeComputed) headers.AddRange(new[] { "secDiff", "distanceDiff" });

            sw.WriteLine(string.Join(",", headers));

            static string CsvSafe(string? s)
            {
                if (string.IsNullOrEmpty(s)) return "";
                bool needsQuotes = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
                if (needsQuotes) return $"\"{s.Replace("\"", "\"\"")}\"";
                return s;
            }

            foreach (var r in rows)
            {
                string ts = r.Timestamp.HasValue
                    ? r.Timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : "";

                var fields = new List<string>
                {
                    ts,
                    r.SnappedLat.ToString(CultureInfo.InvariantCulture),
                    r.SnappedLon.ToString(CultureInfo.InvariantCulture),
                    r.Speed?.ToString(CultureInfo.InvariantCulture) ?? "",
                    r.ModeID?.ToString(CultureInfo.InvariantCulture) ?? "",
                    r.CauseID?.ToString(CultureInfo.InvariantCulture) ?? "",
                    r.Boarding?.ToString(CultureInfo.InvariantCulture) ?? "",
                    r.Alighting?.ToString(CultureInfo.InvariantCulture) ?? "",
                    r.OnBoard?.ToString(CultureInfo.InvariantCulture) ?? "",
                    CsvSafe(r.KilometerPostID),
                    CsvSafe(r.FilePath),
                    CsvSafe(r.DistrictID)
                };

                if (includeComputed)
                {
                    fields.Add(r.SecDiff?.ToString(CultureInfo.InvariantCulture) ?? "");
                    fields.Add(r.DistanceDiff?.ToString(CultureInfo.InvariantCulture) ?? "");
                }

                sw.WriteLine(string.Join(",", fields));
            }

            sw.Flush();
            ms.Position = 0;
            return ms;
        }

        // =========================================================
        // PREVIEW CLEANER (light)
        // =========================================================
        private static List<(DateTime t, double lat, double lon)> QuickCleanPreview(
            List<(DateTime t, double lat, double lon)> raw,
            double maxStepMeters,
            double maxKph)
        {
            var ordered = raw.OrderBy(x => x.t).ToList();
            var kept = new List<(DateTime t, double lat, double lon)> { ordered[0] };

            for (int i = 1; i < ordered.Count; i++)
            {
                var prev = kept[^1];
                var cur = ordered[i];

                var dt = (cur.t - prev.t).TotalSeconds;
                if (dt <= 0) continue;

                var dist = HaversineMeters(prev.lat, prev.lon, cur.lat, cur.lon);
                var kph = (dist / dt) * 3.6;

                if (dist > maxStepMeters) continue;
                if (kph > maxKph) continue;

                kept.Add(cur);
            }

            return kept;
        }

        // =========================================================
        // INTERNAL CLASSES
        // =========================================================
        private sealed class ZipDataset
        {
            public string FileName { get; set; } = "";
            public string VehicleCode { get; set; } = "";
            public string TripId { get; set; } = "";
            public string DtToken { get; set; } = "";
            public string Direction { get; set; } = "UNK";
            public List<GpxRecord> RawRows { get; set; } = new();

            public DateTime StartTimeUtc { get; set; }
            public DateTime EndTimeUtc { get; set; }
            public double StartLat { get; set; }
            public double StartLon { get; set; }
            public double EndLat { get; set; }
            public double EndLon { get; set; }
        }

        private sealed class TripAccumulator
        {
            public string VehicleCode { get; set; } = "";
            public string TripId { get; set; } = "";
            public string DtToken { get; set; } = "";
            public string Direction { get; set; } = "UNK";
            public int PartIndex { get; set; }
            public List<GpxRecord> Rows { get; set; } = new();
            public List<string> SourceZipFiles { get; set; } = new();

            public DateTime StartTimeUtc { get; set; }
            public DateTime EndTimeUtc { get; set; }
            public double StartLat { get; set; }
            public double StartLon { get; set; }
            public double EndLat { get; set; }
            public double EndLon { get; set; }
        }

        private sealed class TripOutput
        {
            public string VehicleCode { get; set; } = "";
            public string TripId { get; set; } = "";
            public string DtToken { get; set; } = "";
            public string Direction { get; set; } = "UNK";
            public int PartIndex { get; set; }

            public string? DetectedRegionId { get; set; }
            public string? DetectedRoadName { get; set; }
            public string? EntryName { get; set; }

            public List<GpxRecord> Combined { get; set; } = new();
            public List<GpxRecord> Cleaned { get; set; } = new();
            public List<string> SourceZipFiles { get; set; } = new();
        }
    }
}
