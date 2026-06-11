using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using System.IO.Compression;
using PrivateTransportCleaning.Models;

namespace PrivateTransportCleaning.Controllers
{
    public class SurveyDataController : Controller
    {
        private string BaseDir => Directory.GetCurrentDirectory();
        private string UploadPath => Path.Combine(BaseDir, "Uploads");
        private string ExtractPath => Path.Combine(Path.GetTempPath(), "PTC_Extracted");
        private string OutputPath => Path.Combine(BaseDir, "Output");

        // =========================
        // INDEX VIEW
        // =========================
        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        // =========================
        // STEP 3 PIPELINE (FIXED)
        // =========================
        [HttpPost]
        [RequestSizeLimit(long.MaxValue)]
        [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
        public IActionResult Index(IFormFile csvFile, List<IFormFile> zipFiles)
        {
            Console.WriteLine($"CSV NULL: {csvFile == null}");
            Console.WriteLine($"ZIP COUNT: {zipFiles?.Count}");

            if (csvFile == null || zipFiles == null || zipFiles.Count == 0)
                return Content("NO FILES RECEIVED");

            Directory.CreateDirectory(UploadPath);
            Directory.CreateDirectory(OutputPath);
            Directory.CreateDirectory(ExtractPath);

            // CLEAN OUTPUT (safe)
            foreach (var f in Directory.GetFiles(OutputPath))
                System.IO.File.Delete(f);

            foreach (var f in Directory.GetFiles(UploadPath))
                System.IO.File.Delete(f);

            if (Directory.Exists(ExtractPath))
                Directory.Delete(ExtractPath, true);

            Directory.CreateDirectory(ExtractPath);

            // SAVE CSV
            var csvPath = Path.Combine(UploadPath, csvFile.FileName);
            using (var fs = new FileStream(csvPath, FileMode.Create))
                csvFile.CopyTo(fs);

            // PROCESS EACH ZIP (PYTHON STYLE LOOP)
            foreach (var zip in zipFiles)
            {
                if (zip == null || zip.Length == 0)
                    continue;

                var zipPath = Path.Combine(UploadPath, zip.FileName);
                using (var fs = new FileStream(zipPath, FileMode.Create))
                    zip.CopyTo(fs);

                var tempFolder = Path.Combine(
                    ExtractPath,
                    Path.GetFileNameWithoutExtension(zip.FileName)
                        .Replace(" - Copy", "")
                        .Replace(" (1)", "")
                        .Replace(" (2)", "")
                );

                if (Directory.Exists(tempFolder))
                    Directory.Delete(tempFolder, true);

                Directory.CreateDirectory(tempFolder);

                ZipFile.ExtractToDirectory(zipPath, tempFolder);

                var gpxFiles = Directory.GetFiles(tempFolder, "*.gpx", SearchOption.AllDirectories);

                foreach (var gpxFile in gpxFiles)
                {
                    var points = ParseGpx(gpxFile);

                    if (points.Count == 0)
                        continue;

                    var fileName = $"trip_{Guid.NewGuid():N}.csv";
                    var outputFile = Path.Combine(OutputPath, fileName);

                    using (var writer = new StreamWriter(outputFile))
                    {
                        writer.WriteLine("Lat,Lon");

                        foreach (var p in points)
                            writer.WriteLine($"{p.lat},{p.lon}");
                    }
                }
            }

            return RedirectToAction("Trips");
        }

        // =========================
        // TRIPS
        // =========================
        [HttpGet]
        public IActionResult Trips()
        {
            if (!Directory.Exists(OutputPath))
                return View(new List<TripFile>());

            var files = Directory.GetFiles(OutputPath, "*.csv")
                .Select(f => new TripFile
                {
                    FileName = Path.GetFileName(f),
                    ViewUrl = Url.Action("Preview", "SurveyData", new { file = Path.GetFileName(f) }),
                    DownloadUrl = Url.Action("Download", "SurveyData", new { file = Path.GetFileName(f) })
                })
                .ToList();

            return View(files);
        }

        // =========================
        // PREVIEW
        // =========================
        [HttpGet]
        public IActionResult Preview(string file)
        {
            if (string.IsNullOrWhiteSpace(file))
                return NotFound();

            var path = Path.Combine(OutputPath, file);

            if (!System.IO.File.Exists(path))
                return NotFound();

            var points = System.IO.File.ReadAllLines(path)
                .Skip(1)
                .Select(line =>
                {
                    var parts = line.Split(',');
                    if (parts.Length < 2) return null;

                    if (double.TryParse(parts[0], out double lat) &&
                        double.TryParse(parts[1], out double lon))
                    {
                        return new double[] { lat, lon };
                    }

                    return null;
                })
                .Where(x => x != null)
                .ToList();

            ViewBag.Filename = file;
            ViewBag.TripsJson = JsonSerializer.Serialize(points);

            return View();
        }

        // =========================
        // MAP VIEW
        // =========================
        [HttpGet]
        public IActionResult GetTripData(string file)
        {
            if (string.IsNullOrWhiteSpace(file))
                return NotFound();

            var path = Path.Combine(OutputPath, file);

            if (!System.IO.File.Exists(path))
                return NotFound();

            var points = System.IO.File.ReadAllLines(path)
                .Skip(1)
                .Select(line =>
                {
                    var parts = line.Split(',');
                    if (parts.Length < 2) return null;

                    if (double.TryParse(parts[0], out double lat) &&
                        double.TryParse(parts[1], out double lon))
                    {
                        return new double[] { lat, lon };
                    }

                    return null;
                })
                .Where(x => x != null)
                .ToList();

            return Json(points);
        }

        // =========================
        // DOWNLOAD
        // =========================
        [HttpGet]
        public IActionResult Download(string file)
        {
            var path = Path.Combine(OutputPath, file);

            if (!System.IO.File.Exists(path))
                return NotFound();

            return File(System.IO.File.ReadAllBytes(path), "text/csv", file);
        }

        // =========================
        // DOWNLOAD SELECTED
        // =========================
        [HttpPost]
        public IActionResult DownloadSelected(List<string> files)
        {
            if (files == null || files.Count == 0)
                return Content("No files selected");

            var ms = new MemoryStream();

            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                foreach (var f in files.Distinct())
                {
                    var path = Path.Combine(OutputPath, f);

                    if (System.IO.File.Exists(path))
                        zip.CreateEntryFromFile(path, f);
                }
            }

            ms.Position = 0;
            return File(ms, "application/zip", "Trips_Selected.zip");
        }

        // =========================
        // GPX PARSER
        // =========================
        private List<(double lat, double lon)> ParseGpx(string path)
        {
            var doc = XDocument.Load(path);
            XNamespace ns = "http://www.topografix.com/GPX/1/1";

            var result = new List<(double lat, double lon)>();

            foreach (var p in doc.Descendants(ns + "trkpt"))
            {
                var lat = p.Attribute("lat")?.Value;
                var lon = p.Attribute("lon")?.Value;

                if (double.TryParse(lat, out double la) &&
                    double.TryParse(lon, out double lo))
                {
                    result.Add((la, lo));
                }
            }

            return result;
        }
    }
}