using CsvHelper;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using Report_Generator.Models;
using Report_Generator.Services;
using DocumentFormat.OpenXml.Bibliography;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using DocumentFormat.OpenXml.ExtendedProperties;

namespace Report_Generator.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ReportController : ControllerBase
    {
        private readonly FolderScannerService _folderScanner;
        private readonly CsvParserService _csvParser;
        private readonly CsvExportService _csvExport;
        private readonly DataProcessorService _dataProcessor;
        private readonly ChartGeneratorService _chartGenerator;
        private readonly WordExportService _wordExport;
        private readonly TripLineLoaderService _tripLineLoader;
        private readonly SpeedSegmentService _speedSegment;
        private readonly SpeedMapRenderer _speedMapRenderer;

        public ReportController (FolderScannerService folderScanner, CsvParserService csvParser, CsvExportService csvExport, DataProcessorService dataProcessor, ChartGeneratorService chartGenerator, WordExportService wordExport, TripLineLoaderService tripLineLoader, SpeedSegmentService speedSegment, SpeedMapRenderer speedMapRenderer)
        {
            _folderScanner = folderScanner;
            _csvParser = csvParser;
            _csvExport = csvExport;
            _dataProcessor = dataProcessor;
            _chartGenerator = chartGenerator;
            _wordExport = wordExport;
            _tripLineLoader = tripLineLoader;
            _speedSegment = speedSegment;
            _speedMapRenderer = speedMapRenderer;
        }

        [HttpPost("generate")]
        [DisableRequestSizeLimit]
        [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue, ValueCountLimit = int.MaxValue)]
        public async Task<IActionResult> GenerateReportsAsync([FromForm] List<IFormFile> files)
        {
            if (files == null || !files.Any())
            {
                Console.WriteLine("❌ Error: No files uploaded.");
                return BadRequest("⚠️ No files uploaded.");
            }

            var filePaths = files.Select(f => f.FileName).ToList();
            var vehicleDirs = _folderScanner.IdentifyVehicleFolders(filePaths);

            if (!vehicleDirs.Any())
            {
                Console.WriteLine("⚠️ No VehicleType folders found. Upload the Region Folder");
                return NotFound("⚠️ No VehicleType folders found (no SegmentAnalysis folder detected). Upload the Region Folder");
            }

            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                foreach (var survey in vehicleDirs)
                {
                    Console.WriteLine($"\n▶ Processing: {survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}");
                    Console.WriteLine(" -> Reading CSV files...");

                    var surveyFiles = files.Where(f => f.FileName.StartsWith(survey.SegmentAnalysisPath)).ToList();
                    var surveyTripData = new List<TripData>();
                    var surveyTripTotals = new List<TripTotalData>();

                    foreach (var file in surveyFiles)
                    {
                        if (file.FileName.EndsWith(".csv"))
                        {
                            // Filter .csv files with pattern: {TripNo}_{...}-NB/SB/EB/WB.csv
                            string csvFileName = System.IO.Path.GetFileNameWithoutExtension(file.FileName);
                            string pattern = @"^(\d+)_.*-(NB|SB|EB|WB)$";
                            Match match = Regex.Match(csvFileName, pattern, RegexOptions.IgnoreCase);

                            if (match.Success)
                            {
                                using (var stream = file.OpenReadStream())
                                {
                                    var (data, missing) = _csvParser.ReadTripCsv(stream);
                                    if (missing.Any())
                                    {
                                        Console.WriteLine($"⚠️ SKIPPED {file.FileName} — missing columns: {string.Join(", ", missing)}");
                                        continue;
                                    }

                                    // Get TripNo and Direction from filename
                                    int tripNo = int.Parse(match.Groups[1].Value);
                                    string direction = match.Groups[2].Value.ToUpper();

                                    if (direction == "EB") direction = "NB";
                                    if (direction == "WB") direction = "SB";

                                    // Get Period from parent directory
                                    string? dirPath = Path.GetDirectoryName(file.FileName);
                                    string period = dirPath != null ? Path.GetFileName(dirPath).ToUpper() : string.Empty;

                                    foreach (var row in data)
                                    {
                                        row.TripNo = tripNo;
                                        row.Direction = direction;
                                        row.Period = period;
                                        row.SourceFile = file.FileName;
                                    }

                                    if (data.Any())
                                    {
                                        surveyTripTotals.Add(new TripTotalData
                                        {
                                            Period = period,
                                            Direction = direction,
                                            TotalTravelTimeSec = data.Sum(r => r.TravelTimeSec),
                                            TotalDistanceM = data.Sum(r => r.DistanceM),
                                            AvgTravelSpeedKph = data.Average(r => r.TravelSpeedKph),
                                            AvgRunningSpeedKph = data.Average(r => r.RunningSpeedKph),
                                            TotalDelayTimeSec = data.Sum(r => r.Delays),
                                            TotalDelayLengthM = data.Sum(r => r.DelayLengthM),
                                        });
                                    }

                                    surveyTripData.AddRange(data);
                                }
                            }
                        }
                    }

                    if (surveyTripData.Any())
                    {
                        Console.WriteLine(" -> Calculating Directional Averages...");
                        var directionalAverages = _dataProcessor.CalculateDirectionalAverages(surveyTripTotals);

                        Console.WriteLine(" -> Calculating Segment Averages...");
                        var segmentAverages = _dataProcessor.CalculateSegmentAverages(surveyTripData);
                        
                        string[] periods = { "AM", "MID", "PM" };
                        string[] directions = { "NB", "SB" };
                        var reportImages = new Dictionary<string, byte[]>();

                        foreach (var period in periods)
                        {
                            string DirAvg = _csvExport.GenerateDirectionalAveragesCsv(directionalAverages, period);
                            string csvZipEntry = $"{survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}/DirectionalAverages/{period}_DirectionalAverages.csv";
                            var csvEntry = zip.CreateEntry(csvZipEntry, CompressionLevel.Fastest);

                            using (var entryStream = csvEntry.Open())
                            using (var streamWriter = new StreamWriter(entryStream))
                            {
                                streamWriter.Write(DirAvg);
                            };

                            Console.WriteLine($"  ✅ Saved Directional Averages: {period}_DirectionalAverages.csv");

                            int chartsGenerated = 0;
                            foreach (var direction in directions)
                            {
                                var plotSegments = segmentAverages
                                    .Where(s => s.Period == period && s.Direction == direction)
                                    .ToList();

                                string directionFull = "";
                                if (direction == "NB") directionFull = "Northbound/Eastbound";
                                if (direction == "SB") directionFull = "Southbound/Westbound";

                                if (plotSegments.Any())
                                {
                                    string title = $"{period} - {directionFull} Speed Comparison";
                                    byte[]? imageBytes = _chartGenerator.GenerateSpeedPairChart(plotSegments, title, direction);

                                    if (imageBytes != null)
                                    {
                                        reportImages[$"{period}_{direction}_Chart"] = imageBytes;
                                        string chartZipEntry = $"{survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}/Graphs/{period}_{direction}_SpeedPair.png";
                                        var chartEntry = zip.CreateEntry(chartZipEntry, CompressionLevel.Fastest);

                                        using (var entryStream = chartEntry.Open())
                                        {
                                            entryStream.Write(imageBytes, 0, imageBytes.Length);
                                        }
                                        chartsGenerated++;
                                    }

                                    // Scope file lookups to this survey's VehicleType folder, not just SegmentAnalysis —
                                    // Snapped and KM-CP Detected are sibling folders, not under SegmentAnalysisPath.
                                    string vehicleTypePath = survey.SegmentAnalysisPath.Substring(
                                        0, survey.SegmentAnalysisPath.Length - "SegmentAnalysis".Length);
                                    var vehicleFiles = files.Where(f => f.FileName.StartsWith(vehicleTypePath)).ToList();

                                    // Map Generation
                                    var tripLine = _tripLineLoader.LoadTripLinestring(vehicleFiles, period, direction);
                                    var controlPoints = _tripLineLoader.LoadControlPoints(vehicleFiles, period);

                                    if (tripLine != null && controlPoints.Any())
                                    {
                                        var speedLookup = _speedSegment.BuildSpeedLookup(plotSegments);
                                        var mapSegments = _speedSegment.MakeTripCp2CpSegments(tripLine, controlPoints, speedLookup);

                                        if (mapSegments.Any())
                                        {
                                            string mapTitle = $"{period} {directionFull} Trip Speed Map";
                                            var mapBytes = await _speedMapRenderer.ExportTripSpeedPngAsync(mapSegments, controlPoints, mapTitle);

                                            if (mapBytes != null)
                                            {
                                                reportImages[$"{period}_{direction}_Map"] = mapBytes;
                                                string mapZipEntry = $"{survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}/Graphs/Maps/{period}_{direction}_TripSpeedMap.png";
                                                var mapEntry = zip.CreateEntry(mapZipEntry, CompressionLevel.Fastest);
                                                using (var entryStream = mapEntry.Open())
                                                    entryStream.Write(mapBytes, 0, mapBytes.Length);

                                                Console.WriteLine($"  ✅ Saved Survey Map: Graphs/Maps/{period}_{direction}_TripSpeedMap.png");
                                            }
                                        }
                                        else
                                        {
                                            Console.WriteLine($"  ⚠️ Could not build trip-following CP segments for {period} {direction}.");
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine($"  ⚠️ Missing snapped trip line or control points for {period} {direction}.");
                                    }
                                }
                            }

                            if (chartsGenerated > 0)
                            {
                                Console.WriteLine($"  -> Generated {chartsGenerated} speed charts for {period}.");
                            }

                        }

                        Console.WriteLine($"   ✔ Processing completed for {survey.VehicleType}");

                        // Copy Shapefiles and GeoJSONs from uploaded files
                        var copyFiles = surveyFiles.Where(f => 
                            f.FileName.Contains("/Shapes/shp/") || 
                            f.FileName.Contains("/KM-CP Detected/") && f.FileName.Contains("/GIS/") && f.FileName.EndsWith(".geojson")
                        ).ToList();

                        foreach (var f in copyFiles)
                        {
                            // Extract relative path inside VehicleType
                            int vtIdx = f.FileName.IndexOf($"/{survey.VehicleType}/");
                            if (vtIdx >= 0)
                            {
                                string relativePath = f.FileName.Substring(vtIdx + $"/{survey.VehicleType}/".Length);
                                string zipEntryPath = $"{survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}/{relativePath}";
                                var entry = zip.CreateEntry(zipEntryPath, CompressionLevel.Fastest);
                                using (var entryStream = entry.Open())
                                using (var fileStream = f.OpenReadStream())
                                {
                                    fileStream.CopyTo(entryStream);
                                }
                            }
                        }

                        // REPORT DOCUMENT
                        Console.WriteLine("  -> Generating DOCX Report...");

                        byte[] reportBytes = _wordExport.GenerateSurveyReport(survey.Region, survey.RoadName, survey.SurveyDate, survey.VehicleType, directionalAverages, segmentAverages, reportImages);

                        string reportFilename = $"{survey.Region}_{survey.RoadName.Replace(" ", string.Empty)}_{survey.SurveyDate}_{survey.VehicleType}_Survey_Report.docx";
                        string reportZipEntry = $"{survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}/{reportFilename}";
                        var reportEntry = zip.CreateEntry(reportZipEntry, CompressionLevel.Fastest);

                        using (var entryStream = reportEntry.Open())
                        {
                            entryStream.Write(reportBytes, 0, reportBytes.Length);
                        }

                        Console.WriteLine($"  ✅ Saved Survey Report: {reportFilename}");

                        // ANNEX DOCUMENT
                        Console.WriteLine("  -> Generating ANNEX File...");

                        byte[] annexBytes = _wordExport.GenerateSurveyAnnex(survey.Region, survey.RoadName, survey.SurveyDate, survey.VehicleType, surveyTripData);

                        string annexFilename = $"{survey.Region}_{survey.RoadName.Replace(" ", string.Empty)}_{survey.SurveyDate}_{survey.VehicleType}_Survey_Annex.docx";
                        string annexZipEntry = $"{survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}/{annexFilename}";
                        var annexEntry = zip.CreateEntry(annexZipEntry, CompressionLevel.Fastest);

                        using (var entryStream = annexEntry.Open())
                        {
                            entryStream.Write(annexBytes, 0, annexBytes.Length);
                        }

                        Console.WriteLine($"  ✅ Saved Survey Annex: {annexFilename}");
                    }

                    else
                    {
                        Console.WriteLine($"  ⚠️ No valid survey data found for {survey.VehicleType}.");
                    }
                }
            }

            memory.Position = 0;
            Console.WriteLine("\n✅ All reports bundled successfully into ZIP.");
            return File(memory.ToArray(), "application/zip",$"{vehicleDirs.First().Region}_Reports.zip");
        }
    }
}