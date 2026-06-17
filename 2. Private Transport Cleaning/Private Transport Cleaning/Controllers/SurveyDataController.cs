using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using System.IO.Compression;
using PrivateTransportCleaning.Models;
using PrivateTransportCleaning.Services;

namespace PrivateTransportCleaning.Controllers
{
    public class SurveyDataController : Controller
    {
        private readonly GpxProcessingService _gpxService;
        private readonly FileNamingService _fileNamingService;

        private string RootDir =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

        private string UploadPath => Path.Combine(RootDir, "Uploads");
        private string ExtractPath => Path.Combine(Path.GetTempPath(), "PTC_Extracted");
        private string OutputPath => Path.Combine(RootDir, "Output");
        private string KmDbPath => Path.Combine(RootDir, "Data", "kilometer_post.db");

        public SurveyDataController(
            GpxProcessingService gpxService,
            FileNamingService fileNamingService)
        {
            _gpxService = gpxService;
            _fileNamingService = fileNamingService;
        }

        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public IActionResult Index(IFormFile csvFile, List<IFormFile> zipFiles)
        {
            if (csvFile == null || zipFiles == null || zipFiles.Count == 0)
                return Content("NO FILES RECEIVED");

            Console.WriteLine("🔥 INDEX POST HIT");

            Directory.CreateDirectory(OutputPath);

            // IMPORTANT: clean only once per run
            foreach (var f in Directory.GetFiles(OutputPath))
                System.IO.File.Delete(f);

            var runId = Guid.NewGuid().ToString("N");

            // ================= CSV =================
            var csvPath = Path.Combine(UploadPath, runId + "_" + csvFile.FileName);
            Directory.CreateDirectory(UploadPath);

            using (var fs = new FileStream(csvPath, FileMode.Create))
                csvFile.CopyTo(fs);

            var centerline = new List<(double lat, double lon)>();

            using (var reader = new StreamReader(csvPath))
            {
                reader.ReadLine();

                while (!reader.EndOfStream)
                {
                    var parts = reader.ReadLine()?.Split(',');

                    if (parts == null || parts.Length < 2)
                        continue;

                    if (double.TryParse(parts[0], out var lat) &&
                        double.TryParse(parts[1], out var lon))
                    {
                        centerline.Add((lat, lon));
                    }
                }
            }

            // ================= PROCESS ZIPS =================
            foreach (var zip in zipFiles)
            {
                if (zip == null || zip.Length == 0)
                    continue;

                var zipRunId = Guid.NewGuid().ToString("N");

                var zipPath = Path.Combine(UploadPath, zipRunId + "_" + zip.FileName);
                using (var fs = new FileStream(zipPath, FileMode.Create))
                    zip.CopyTo(fs);

                var tempFolder = Path.Combine(ExtractPath, zipRunId);
                Directory.CreateDirectory(tempFolder);

                ZipFile.ExtractToDirectory(zipPath, tempFolder, true);

                var gpxFiles = Directory.GetFiles(tempFolder, "*.gpx", SearchOption.AllDirectories);

                var allPoints = new List<GpxPoint>();

                foreach (var gpx in gpxFiles)
                    allPoints.AddRange(ParseGpx(gpx));

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
                        "SecDiff,DistanceDiff,IsBreak"
                    );

                    foreach (var r in processed)
                    {
                        writer.WriteLine(
                            $"{r.OriginalLat},{r.OriginalLon},{r.SnappedLat},{r.SnappedLon},{r.DeviationMeters},{r.Timestamp},{r.Speed}," +
                            $"{r.DeviceID},{r.TrackingID},{r.UserID},{r.ModeID},{r.CauseID},{r.KilometerPostID},{r.FilePath},{r.DistrictID}," +
                            $"{r.SecDiff},{r.DistanceDiff},{r.IsBreak}"
                        );
                    }
                }

                Console.WriteLine("OUTPUT FILE CREATED: " + outputFile);
            }

            return RedirectToAction("Trips");
        }

        [HttpGet]
        public IActionResult Trips()
        {
            var files = Directory.Exists(OutputPath)
                ? Directory.GetFiles(OutputPath, "*.csv")
                : new string[0];

            var model = files.Select(f => new TripFile
            {
                FileName = Path.GetFileName(f),
                FileSize = new FileInfo(f).Length,
                ViewUrl = Url.Action("Preview", "SurveyData", new { file = Path.GetFileName(f) }),
                DownloadUrl = Url.Action("Download", "SurveyData", new { file = Path.GetFileName(f) })
            }).ToList();

            return View(model);
        }

        [HttpGet]
        public IActionResult Preview(string file)
        {
            var path = Path.Combine(OutputPath, file);
            if (!System.IO.File.Exists(path))
                return NotFound();

            var original = new List<double[]>();
            var snapped = new List<double[]>();

            foreach (var line in System.IO.File.ReadLines(path).Skip(1))
            {
                var p = line.Split(',');

                if (p.Length < 4)
                    continue;

                if (double.TryParse(p[0], out var o1) &&
                    double.TryParse(p[1], out var o2) &&
                    double.TryParse(p[2], out var s1) &&
                    double.TryParse(p[3], out var s2))
                {
                    original.Add(new[] { o1, o2 });
                    snapped.Add(new[] { s1, s2 });
                }
            }

            ViewBag.Filename = file;
            ViewBag.OriginalPointsJson = JsonSerializer.Serialize(original);
            ViewBag.SnappedPointsJson = JsonSerializer.Serialize(snapped);

            return View();
        }

        [HttpGet]
        public IActionResult GetTripData(string file)
        {
            var path = Path.Combine(OutputPath, file);

            if (!System.IO.File.Exists(path))
                return NotFound();

            var points = System.IO.File.ReadLines(path)
                .Skip(1)
                .Select(l => l.Split(','))
                .Where(p => p.Length >= 2)
                .Select(p =>
                {
                    double.TryParse(p[0], out var lat);
                    double.TryParse(p[1], out var lon);
                    return new[] { lat, lon };
                })
                .ToList();

            return Json(points);
        }

        [HttpGet]
        public IActionResult Download(string file)
        {
            var path = Path.Combine(OutputPath, file);
            if (!System.IO.File.Exists(path))
                return NotFound();

            return File(System.IO.File.ReadAllBytes(path), "text/csv", file);
        }

        private List<GpxPoint> ParseGpx(string path)
        {
            var doc = XDocument.Load(path);

            XNamespace ns = "http://www.topografix.com/GPX/1/1";

            var points = new List<GpxPoint>();

            foreach (var p in doc.Descendants(ns + "trkpt"))
            {
                double lat, lon;

                if (!double.TryParse(p.Attribute("lat")?.Value, out lat))
                    continue;

                if (!double.TryParse(p.Attribute("lon")?.Value, out lon))
                    continue;

                var timeEl = p.Element(ns + "time");
                if (timeEl == null || string.IsNullOrWhiteSpace(timeEl.Value))
                    continue;

                if (!DateTime.TryParse(timeEl.Value.Replace("Z", ""), out var timestamp))
                    continue;

                var speedEl = p.Element(ns + "speed");
                double speed = 0;

                if (speedEl != null)
                    double.TryParse(speedEl.Value,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out speed);

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
            return el?.Value?.Trim();
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
                var path = Path.Combine(OutputPath, file);

                if (!System.IO.File.Exists(path))
                    continue;

                foreach (var line in System.IO.File.ReadLines(path).Skip(1))
                {
                    var parts = line.Split(',');

                    if (parts.Length < 4)
                        continue;

                    if (double.TryParse(parts[2], out var lat) &&
                        double.TryParse(parts[3], out var lon))
                    {
                        sample.Add((lat, lon));
                    }
                }
            }

            string zipName;

            if (sample.Count > 0)
            {
                // ZIP-specific naming (DO NOT reuse CSV naming method)
                zipName = _fileNamingService.BuildZipName(
                    KmDbPath,
                    sample
                );
            }
            else
            {
                zipName = $"SelectedTrips_{DateTime.Now:yyyyMMdd_HHmmss}";
            }

            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                foreach (var file in files)
                {
                    var path = Path.Combine(OutputPath, file);

                    if (!System.IO.File.Exists(path))
                        continue;

                    var entry = archive.CreateEntry(file, CompressionLevel.Optimal);

                    using var entryStream = entry.Open();
                    using var fileStream = System.IO.File.OpenRead(path);

                    fileStream.CopyTo(entryStream);
                }
            }

            memory.Position = 0;

            return File(memory, "application/zip", zipName);
        }
    }

}