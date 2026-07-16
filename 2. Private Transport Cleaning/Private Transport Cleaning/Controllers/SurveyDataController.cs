using System;
using System.Collections.Generic;
using System.Globalization;
using Ttds.Shared;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PrivateTransportCleaning.Models;
using PrivateTransportCleaning.Services;
using System.Collections.Concurrent;

namespace PrivateTransportCleaning.Controllers
{
    public class SurveyDataController : Controller
    {
        private readonly GpxProcessingService _gpxService;
        private readonly FileNamingService _fileNamingService;
        private readonly ServiceOptions _services;
        private readonly GeoUtilityService _geo;

        private string RootDir =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

        private string UploadPath => Path.Combine(RootDir, "Uploads");
        private string ExtractPath => Path.Combine(Path.GetTempPath(), "PTC_Extracted");
        private string OutputPath => Path.Combine(RootDir, "Output");
        private string KmDbPath => Path.Combine(RootDir, "Data", "kilometer_post.db");

        public SurveyDataController(
            GpxProcessingService gpxService,
            FileNamingService fileNamingService,
            GeoUtilityService geo,
            IOptions<ServiceOptions> options)
        {
            _gpxService = gpxService;
            _fileNamingService = fileNamingService;
            _geo = geo;
            _services = options.Value;
        }

        public IActionResult GoToDashboard()
        {
            return Redirect(_services.Dashboard);
        }

        public IActionResult GoToMainProc()
        {
            return Redirect(_services.MainProc);
        }

        private string? ExtractDate(string fileName)
        {
            var csvMatch = Regex.Match(fileName, @"(\d{4}-\d{2}-\d{2})");
            if (csvMatch.Success)
                return csvMatch.Groups[1].Value.Replace("-", "");

            var zipMatch = Regex.Match(fileName, @"_(\d{8})-\d{6}");
            if (zipMatch.Success)
                return zipMatch.Groups[1].Value;

            return null;
        }

        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public IActionResult Index(IFormFile csvFile, List<IFormFile> zipFiles, string? sessionId)
        {
            bool track = !string.IsNullOrWhiteSpace(sessionId);

            if (track) SetProgress(sessionId!, 5, "Validating…", "Checking uploaded files.");

            if (csvFile == null)
            {
                var msg = "CSV file is missing.";
                if (track) { SetProgress(sessionId!, 0, "Error", "", error: msg); return Ok(); }
                return Json(new { success = false, message = msg });
            }

            if (zipFiles == null || zipFiles.Count == 0)
            {
                var msg = "Please select at least one ZIP package.";
                if (track) { SetProgress(sessionId!, 0, "Error", "", error: msg); return Ok(); }
                return Json(new { success = false, message = msg });
            }

            var csvDate = ExtractDate(csvFile.FileName);
            if (csvDate == null)
            {
                var msg = "Could not determine CSV date.";
                if (track) { SetProgress(sessionId!, 0, "Error", "", error: msg); return Ok(); }
                return Json(new { success = false, message = msg });
            }

            foreach (var zip in zipFiles)
            {
                var zipDate = ExtractDate(zip.FileName);
                if (zipDate == null)
                {
                    var msg = $"Could not determine ZIP date: {zip.FileName}";
                    if (track) { SetProgress(sessionId!, 0, "Error", "", error: msg); return Ok(); }
                    return Json(new { success = false, message = msg });
                }

                if (zipDate != csvDate)
                {
                    var msg = "Process Error: Dates do not match.";
                    if (track) { SetProgress(sessionId!, 0, "Error", "", error: msg); return Ok(); }
                    return Json(new { success = false, code = "DATE_MISMATCH", message = msg });
                }
            }

            Directory.CreateDirectory(OutputPath);
            foreach (var f in Directory.GetFiles(OutputPath))
                System.IO.File.Delete(f);

            var runId = Guid.NewGuid().ToString("N");
            var producedFiles = new List<string>();

            if (track) SetProgress(sessionId!, 10, "Saving CSV log…", "Writing Geotab centerline file to disk.");

            // ================= 1. PARSE CSV FIRST =================
            var csvPath = Path.Combine(UploadPath, runId + "_" + csvFile.FileName);
            Directory.CreateDirectory(UploadPath);

            using (var fs = new FileStream(csvPath, FileMode.Create))
                csvFile.CopyTo(fs);

            if (track) SetProgress(sessionId!, 15, "Parsing centerline…", "Reading latitude/longitude points from CSV.");

            var centerline = new List<(double lat, double lon)>();

            using (var reader = new StreamReader(csvPath))
            {
                reader.ReadLine(); // Skip header

                while (!reader.EndOfStream)
                {
                    var parts = reader.ReadLine()?.Split(',');
                    if (parts == null || parts.Length < 2)
                        continue;

                    if (double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var lat) &&
                        double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var lon))
                    {
                        centerline.Add((lat, lon));
                    }
                }
            }

            // ================= 2. PROCESS ZIPS (MEMORY STREAMING) =================
            int totalZips = zipFiles.Count(z => z != null && z.Length > 0);
            int zipIndex = 0;

            foreach (var zip in zipFiles)
            {
                if (zip == null || zip.Length == 0)
                    continue;

                zipIndex++;
                if (track)
                {
                    int pct = 20 + (int)(70.0 * zipIndex / Math.Max(totalZips, 1));
                    SetProgress(sessionId!, pct, $"Cleaning package {zipIndex} of {totalZips}…", $"Snapping GPS points in {zip.FileName}.");
                }

                var zipRunId = Guid.NewGuid().ToString("N");
                var zipPath = Path.Combine(UploadPath, zipRunId + "_" + zip.FileName);

                using (var fs = new FileStream(zipPath, FileMode.Create))
                {
                    zip.CopyTo(fs);
                }

                var allPoints = new List<GpxPoint>();

                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase))
                        {
                            using var entryStream = entry.Open();
                            allPoints.AddRange(ParseGpxStream(entryStream));
                        }
                    }
                }

                if (System.IO.File.Exists(zipPath))
                {
                    System.IO.File.Delete(zipPath);
                }

                if (allPoints.Count == 0)
                    continue;

                var processed = _gpxService.Process(allPoints, centerline);
                if (processed.Count == 0)
                    continue;

                var sample = processed
                    .Select(p => (p.SnappedLat, p.SnappedLon))
                    .ToList();

                var filename = _fileNamingService.BuildName(
                    KmDbPath,
                    sample,
                    zip.FileName
                );

                var outputFile = Path.Combine(OutputPath, filename);

                using (var writer = new StreamWriter(outputFile, false))
                {
                    writer.WriteLine(
                        "OriginalLat,OriginalLon,SnappedLat,SnappedLon,DeviationMeters,Timestamp,Speed," +
                        "DeviceID,TrackingID,UserID,ModeID,CauseID,KilometerPostID,FilePath,DistrictID," +
                        "secDiff,distanceDiff,IsBreak"
                    );

                    foreach (var r in processed)
                    {
                        writer.WriteLine(
                            $"{r.OriginalLat.ToString(CultureInfo.InvariantCulture)},{r.OriginalLon.ToString(CultureInfo.InvariantCulture)},{r.SnappedLat.ToString(CultureInfo.InvariantCulture)},{r.SnappedLon.ToString(CultureInfo.InvariantCulture)},{r.DeviationMeters.ToString(CultureInfo.InvariantCulture)},{r.Timestamp},{r.Speed.ToString(CultureInfo.InvariantCulture)}," +
                            $"{r.DeviceID},{r.TrackingID},{r.UserID},{r.ModeID},{r.CauseID},{r.KilometerPostID},{r.FilePath},{r.DistrictID}," +
                            $"{r.SecDiff?.ToString(CultureInfo.InvariantCulture)},{r.DistanceDiff?.ToString(CultureInfo.InvariantCulture)},{r.IsBreak}"
                        );
                    }
                }

                Console.WriteLine("OUTPUT FILE CREATED: " + outputFile);
                producedFiles.Add(outputFile);
            }

            if (track) SetProgress(sessionId!, 95, "Finalizing batch…", "Copying cleaned files to shared storage.");

            var shareDir = Path.Combine(_services.BatchStorageRoot, runId, "gpxclean");
            Directory.CreateDirectory(shareDir);
            foreach (var f in producedFiles)
                System.IO.File.Copy(f, Path.Combine(shareDir, Path.GetFileName(f)), overwrite: true);

            var redirectUrl = Url.Action("Trips", "SurveyData", new { runId });

            if (track)
            {
                SetProgress(sessionId!, 100, "Ready!", "Launching results…", redirect: redirectUrl);
                return Ok();
            }

            return Json(new { success = true, redirect = redirectUrl });
        }

        private static readonly ConcurrentDictionary<string, ProgressUpdate> _progress = new();

        public class ProgressUpdate
        {
            public int Percent { get; set; }
            public string Title { get; set; } = "";
            public string Message { get; set; } = "";
            public string? Redirect { get; set; }
            public string? Error { get; set; }
        }

        private static void SetProgress(string sessionId, int percent, string title, string message,
            string? redirect = null, string? error = null)
        {
            _progress[sessionId] = new ProgressUpdate
            {
                Percent = percent,
                Title = title,
                Message = message,
                Redirect = redirect,
                Error = error
            };
        }

        [HttpGet("/SurveyData/Progress/{sessionId}")]
        public async Task ProgressStream(string sessionId)
        {
            Response.Headers["Content-Type"] = "text/event-stream";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";

            var timeout = DateTime.UtcNow.AddMinutes(10);

            while (DateTime.UtcNow < timeout)
            {
                if (_progress.TryGetValue(sessionId, out var update))
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        percent = update.Percent,
                        title = update.Title,
                        message = update.Message,
                        redirect = update.Redirect,
                        error = update.Error
                    });

                    await Response.WriteAsync($"data: {json}\n\n");
                    await Response.Body.FlushAsync();

                    if (update.Redirect != null || update.Error != null)
                    {
                        _progress.TryRemove(sessionId, out _);
                        break;
                    }
                }

                await Task.Delay(300);
            }
        }

        [HttpPost]
        public IActionResult MergeSelected(List<string> files, string? mergedName)
        {
            if (files == null || files.Count < 2)
                return Json(new { success = false, message = "Select at least two files to merge." });

            var allRows = new List<(SnappedResult row, string sourceFile)>();
            var filesToDelete = new List<string>();

            foreach (var file in files)
            {
                var safeFile = Path.GetFileName(file);
                var path = Path.Combine(OutputPath, safeFile);
                if (!System.IO.File.Exists(path))
                    continue;

                filesToDelete.Add(path);

                foreach (var line in System.IO.File.ReadLines(path).Skip(1))
                {
                    var row = ParseCsvRow(line, safeFile);
                    if (row != null)
                        allRows.Add((row, safeFile));
                }
            }

            if (allRows.Count == 0)
                return Json(new { success = false, message = "No valid rows found in selected files." });

            var earliestFile = allRows
                .GroupBy(r => r.sourceFile)
                .Select(g => new { File = g.Key, MinTimestamp = g.Min(x => x.row.Timestamp) })
                .OrderBy(x => x.MinTimestamp)
                .First()
                .File;

            var merged = allRows
                .Select(r => r.row)
                .OrderBy(r => r.Timestamp)
                .GroupBy(r => r.Timestamp)
                .Select(g => g.First())
                .ToList();

            const double MAX_GAP = 2.0;
            SnappedResult? prev = null;

            foreach (var row in merged)
            {
                if (prev == null)
                {
                    row.SecDiff = null;
                    row.DistanceDiff = null;
                    row.IsBreak = false;
                }
                else
                {
                    var secDiff = (row.Timestamp - prev.Timestamp).TotalSeconds;
                    var distDiff = _geo.Haversine(row.SnappedLat, row.SnappedLon, prev.SnappedLat, prev.SnappedLon);

                    row.SecDiff = secDiff;
                    row.DistanceDiff = distDiff;
                    row.IsBreak = secDiff > MAX_GAP;
                }

                prev = row;
            }

            var sample = merged.Select(r => (r.SnappedLat, r.SnappedLon)).ToList();

            var baseForNaming = Path.GetFileNameWithoutExtension(earliestFile);
            if (baseForNaming.EndsWith("_snapped", StringComparison.OrdinalIgnoreCase))
                baseForNaming = baseForNaming[..^"_snapped".Length];

            // Automatically prepend 'Merged_' to distinguish it as a multi-source file
            var sourceNameForNaming = string.IsNullOrWhiteSpace(mergedName)
                ? $"Merged_{baseForNaming}"
                : mergedName;

            var filename = _fileNamingService.BuildName(KmDbPath, sample, sourceNameForNaming);

            Directory.CreateDirectory(OutputPath);
            var outputFile = Path.Combine(OutputPath, filename);

            if (System.IO.File.Exists(outputFile))
            {
                var nameNoExt = Path.GetFileNameWithoutExtension(filename);
                var ext = Path.GetExtension(filename);
                var counter = 2;

                while (System.IO.File.Exists(outputFile))
                {
                    filename = $"{nameNoExt}_{counter}{ext}";
                    outputFile = Path.Combine(OutputPath, filename);
                    counter++;
                }
            }

            using (var writer = new StreamWriter(outputFile, false))
            {
                writer.WriteLine(
                    "OriginalLat,OriginalLon,SnappedLat,SnappedLon,DeviationMeters,Timestamp,Speed," +
                    "DeviceID,TrackingID,UserID,ModeID,CauseID,KilometerPostID,FilePath,DistrictID," +
                    "secDiff,distanceDiff,IsBreak"
                );

                foreach (var r in merged)
                {
                    writer.WriteLine(
                        $"{r.OriginalLat.ToString(CultureInfo.InvariantCulture)},{r.OriginalLon.ToString(CultureInfo.InvariantCulture)},{r.SnappedLat.ToString(CultureInfo.InvariantCulture)},{r.SnappedLon.ToString(CultureInfo.InvariantCulture)},{r.DeviationMeters.ToString(CultureInfo.InvariantCulture)},{r.Timestamp},{r.Speed.ToString(CultureInfo.InvariantCulture)}," +
                        $"{r.DeviceID},{r.TrackingID},{r.UserID},{r.ModeID},{r.CauseID},{r.KilometerPostID},{r.FilePath},{r.DistrictID}," +
                        $"{r.SecDiff?.ToString(CultureInfo.InvariantCulture)},{r.DistanceDiff?.ToString(CultureInfo.InvariantCulture)},{r.IsBreak}"
                    );
                }
            }

            // DELETE THE TWO OR MORE SEPARATE FILES AFTER SUCCESSFUL WRITING
            foreach (var fileToDelete in filesToDelete)
            {
                if (fileToDelete != outputFile && System.IO.File.Exists(fileToDelete))
                {
                    System.IO.File.Delete(fileToDelete);
                }
            }

            return Json(new { success = true, redirect = "/SurveyData/Trips", mergedFile = filename });
        }

        [HttpPost]
        public IActionResult ContinueToProcessing(List<string> files, string? runId)
        {
            if (files == null || files.Count == 0)
                return Json(new { success = false, message = "Select at least one file to continue." });

            // Reuse the runId if the caller has one (keeps it consistent with the
            // OutputPath produced during Index()); otherwise mint a fresh batch id.
            var batchId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId;

            var shareDir = Path.Combine(_services.BatchStorageRoot, batchId, "gpxclean");
            Directory.CreateDirectory(shareDir);

            int copied = 0;
            foreach (var file in files)
            {
                var safeFile = Path.GetFileName(file);
                var path = Path.Combine(OutputPath, safeFile);
                if (!System.IO.File.Exists(path))
                    continue;

                System.IO.File.Copy(path, Path.Combine(shareDir, safeFile), overwrite: true);
                copied++;
            }

            if (copied == 0)
                return Json(new { success = false, message = "None of the selected files could be found on disk." });

            // Main Processing (TtdsWeb) reads this batch folder directly via its
            // /import/{batchId} endpoint — same handoff mechanism GPXClean uses.
            return Json(new
            {
                success = true,
                redirect = $"{_services.MainProc}/import/{batchId}"
            });
        }

        [HttpGet]
        public IActionResult Trips(string? runId)
        {
            var files = Directory.Exists(OutputPath)
                ? Directory.GetFiles(OutputPath, "*.csv")
                : Array.Empty<string>();

            var model = files.Select(f => new TripFile
            {
                FileName = Path.GetFileName(f),
                FileSize = new FileInfo(f).Length,
                ViewUrl = Url.Action("Preview", "SurveyData", new { file = Path.GetFileName(f) })!,
                DownloadUrl = Url.Action("Download", "SurveyData", new { file = Path.GetFileName(f) })!
            }).ToList();

            return View(model);
        }

        [HttpGet]
        public IActionResult Preview(string file)
        {
            if (string.IsNullOrEmpty(file)) return NotFound();
            var path = Path.Combine(OutputPath, Path.GetFileName(file));
            if (!System.IO.File.Exists(path))
                return NotFound();

            var original = new List<double[]>();
            var snapped = new List<double[]>();

            foreach (var line in System.IO.File.ReadLines(path).Skip(1))
            {
                var p = line.Split(',');

                if (p.Length < 4)
                    continue;

                if (double.TryParse(p[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var o1) &&
                    double.TryParse(p[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var o2) &&
                    double.TryParse(p[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var s1) &&
                    double.TryParse(p[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var s2))
                {
                    original.Add(new[] { o1, o2 });
                    snapped.Add(new[] { s1, s2 });
                }
            }

            ViewBag.Filename = Path.GetFileName(file);
            ViewBag.OriginalPointsJson = JsonSerializer.Serialize(original);
            ViewBag.SnappedPointsJson = JsonSerializer.Serialize(snapped);

            return View();
        }

        [HttpGet]
        public IActionResult GetTripData(string file)
        {
            if (string.IsNullOrEmpty(file)) return NotFound();
            var path = Path.Combine(OutputPath, Path.GetFileName(file));

            if (!System.IO.File.Exists(path))
                return NotFound();

            var points = System.IO.File.ReadLines(path)
                .Skip(1)
                .Select(l => l.Split(','))
                .Where(p => p.Length >= 2)
                .Select(p =>
                {
                    double.TryParse(p[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var lat);
                    double.TryParse(p[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var lon);
                    return new[] { lat, lon };
                })
                .ToList();

            return Json(points);
        }

        [HttpGet]
        public IActionResult Download(string file)
        {
            if (string.IsNullOrEmpty(file)) return NotFound();
            var path = Path.Combine(OutputPath, Path.GetFileName(file));
            if (!System.IO.File.Exists(path))
                return NotFound();

            return File(System.IO.File.ReadAllBytes(path), "text/csv", Path.GetFileName(file));
        }

        private List<GpxPoint> ParseGpxStream(Stream stream)
        {
            var doc = XDocument.Load(stream);
            XNamespace ns = "http://www.topografix.com/GPX/1/1";
            var points = new List<GpxPoint>();

            foreach (var p in doc.Descendants(ns + "trkpt"))
            {
                if (!double.TryParse(p.Attribute("lat")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var lat))
                    continue;

                if (!double.TryParse(p.Attribute("lon")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var lon))
                    continue;

                var timeEl = p.Element(ns + "time");
                if (timeEl == null || string.IsNullOrWhiteSpace(timeEl.Value))
                    continue;

                if (!DateTime.TryParse(timeEl.Value.Replace("Z", ""), out var timestamp))
                    continue;

                var speedEl = p.Element(ns + "speed");
                double speed = 0;

                if (speedEl != null)
                    double.TryParse(speedEl.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out speed);

                if (speed == 0)
                    continue;

                points.Add(new GpxPoint
                {
                    Latitude = lat,
                    Longitude = lon,
                    Timestamp = timestamp,
                    Speed = speed,
                    DeviceID = GetText(p, "deviceId"),
                    TrackingID = GetText(p, "trackingId"),
                    UserID = GetText(p, "userId"),
                    ModeID = GetText(p, "modeId"),
                    CauseID = GetText(p, "causeId"),
                    KilometerPostID = GetText(p, "kilometerPostId"),
                    FilePath = GetText(p, "filePath"),
                    DistrictID = GetText(p, "districtId")
                });
            }

            return points;
        }

        private string GetText(XElement parent, string tag)
        {
            var ns = "http://www.topografix.com/GPX/1/1";
            var el = parent.Element(XName.Get(tag, ns));
            return el?.Value?.Trim() ?? "";
        }

        [HttpPost]
        public IActionResult DownloadSelected(List<string> files)
        {
            if (files == null || files.Count == 0)
                return Content("No files selected.");

            var memory = new MemoryStream();
            var sample = new List<(double lat, double lon)>();

            foreach (var file in files)
            {
                var safeFile = Path.GetFileName(file);
                var path = Path.Combine(OutputPath, safeFile);

                if (!System.IO.File.Exists(path))
                    continue;

                foreach (var line in System.IO.File.ReadLines(path).Skip(1))
                {
                    var parts = line.Split(',');
                    if (parts.Length < 4)
                        continue;

                    if (double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var lat) &&
                        double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var lon))
                    {
                        sample.Add((lat, lon));
                    }
                }
            }

            string zipName;
            if (sample.Count > 0)
            {
                zipName = _fileNamingService.BuildZipName(KmDbPath, sample);
                if (!zipName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    zipName += ".zip";
            }
            else
            {
                zipName = $"SelectedTrips_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            }

            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                foreach (var file in files)
                {
                    var safeFile = Path.GetFileName(file);
                    var path = Path.Combine(OutputPath, safeFile);

                    if (!System.IO.File.Exists(path))
                        continue;

                    var entry = archive.CreateEntry(safeFile, CompressionLevel.Optimal);

                    using var entryStream = entry.Open();
                    using var fileStream = System.IO.File.OpenRead(path);
                    fileStream.CopyTo(entryStream);
                }
            }

            memory.Position = 0;
            return File(memory, "application/zip", zipName);
        }

        private SnappedResult? ParseCsvRow(string line, string sourceFile)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            var p = line.Split(',');
            if (p.Length < 4) return null;

            bool ok = double.TryParse(p[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var originalLat);
            ok &= double.TryParse(p[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var originalLon);
            ok &= double.TryParse(p[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var snappedLat);
            ok &= double.TryParse(p[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var snappedLon);
            if (!ok) return null;

            DateTime timestamp = DateTime.MinValue;
            if (p.Length > 5)
                DateTime.TryParse(p[5], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);

            double.TryParse(p.ElementAtOrDefault(4) ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out var deviation);
            double.TryParse(p.ElementAtOrDefault(6) ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out var speed);
            var deviceId = p.ElementAtOrDefault(7) ?? "";
            var trackingId = p.ElementAtOrDefault(8) ?? "";
            var userId = p.ElementAtOrDefault(9) ?? "";
            var modeId = p.ElementAtOrDefault(10) ?? "";
            var causeId = p.ElementAtOrDefault(11) ?? "";
            var kmId = p.ElementAtOrDefault(12) ?? "";
            var filePath = p.ElementAtOrDefault(13) ?? sourceFile;
            var districtId = p.ElementAtOrDefault(14) ?? "";

            double? secDiff = null;
            if (double.TryParse(p.ElementAtOrDefault(15) ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out var tmpSec))
                secDiff = tmpSec;

            double? distanceDiff = null;
            if (double.TryParse(p.ElementAtOrDefault(16) ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out var tmpDist))
                distanceDiff = tmpDist;

            bool isBreak = false;
            if (p.Length > 17)
                bool.TryParse(p[17], out isBreak);

            return new SnappedResult
            {
                OriginalLat = originalLat,
                OriginalLon = originalLon,
                SnappedLat = snappedLat,
                SnappedLon = snappedLon,
                DeviationMeters = deviation,
                Timestamp = timestamp == DateTime.MinValue ? DateTime.MinValue : timestamp,
                Speed = speed,
                DeviceID = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId,
                TrackingID = string.IsNullOrWhiteSpace(trackingId) ? null : trackingId,
                UserID = string.IsNullOrWhiteSpace(userId) ? null : userId,
                ModeID = string.IsNullOrWhiteSpace(modeId) ? null : modeId,
                CauseID = string.IsNullOrWhiteSpace(causeId) ? null : causeId,
                KilometerPostID = string.IsNullOrWhiteSpace(kmId) ? null : kmId,
                FilePath = string.IsNullOrWhiteSpace(filePath) ? sourceFile : filePath,
                DistrictID = string.IsNullOrWhiteSpace(districtId) ? null : districtId,
                SecDiff = secDiff,
                DistanceDiff = distanceDiff,
                IsBreak = isBreak
            };
        }
    }
}