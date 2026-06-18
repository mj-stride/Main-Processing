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
        private readonly ChartGeneratorService _chartGenerator;
        private readonly WordExportService _wordExport;

        public ReportController (FolderScannerService folderScanner, CsvParserService csvParser, CsvExportService csvExport, ChartGeneratorService chartGenerator, WordExportService wordExport)
        {
            _folderScanner = folderScanner;
            _csvParser = csvParser;
            _csvExport = csvExport;
            _chartGenerator = chartGenerator;
            _wordExport = wordExport;
        }

        [HttpPost("generate")]
        [DisableRequestSizeLimit]
        [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue, ValueCountLimit = int.MaxValue)]
        public IActionResult GenerateReports([FromForm] List<IFormFile> files)
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

            var dataProcessor = new DataProcessorService();

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
                        var directionalAverages = dataProcessor.CalculateDirectionalAverages(surveyTripTotals);

                        Console.WriteLine(" -> Calculating Segment Averages...");
                        var segmentAverages = dataProcessor.CalculateSegmentAverages(surveyTripData);
                        
                        string[] periods = { "AM", "MID", "PM" };
                        string[] directions = { "NB", "SB" };

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
                                        string chartZipEntry = $"{survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}/Graphs/{period}_{direction}_SpeedPair.png";
                                        var chartEntry = zip.CreateEntry(chartZipEntry, CompressionLevel.Fastest);

                                        using (var entryStream = chartEntry.Open())
                                        {
                                            entryStream.Write(imageBytes, 0, imageBytes.Length);
                                        }
                                        chartsGenerated++;
                                    }
                                }
                            }

                            if (chartsGenerated > 0)
                            {
                                Console.WriteLine($"  -> Generated {chartsGenerated} speed charts for {period}.");
                            }

                        }

                        Console.WriteLine($"   ✔ Processing completed for {survey.VehicleType}");

                        // WORD DOCUMENT
                        Console.WriteLine("  -> Generating DOCX Report...");

                        byte[] docxBytes = _wordExport.GenerateReport(survey.Region, survey.RoadName, survey.SurveyDate, survey.VehicleType, directionalAverages, segmentAverages);

                        string docxFilename = $"{survey.Region}_{survey.RoadName.Replace(" ", string.Empty)}_{survey.SurveyDate}_{survey.VehicleType}_Survey_Report.docx";
                        string docxZipEntry = $"{survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}/{docxFilename}";
                        var docEntry = zip.CreateEntry(docxZipEntry, CompressionLevel.Fastest);

                        using (var entryStream = docEntry.Open())
                        {
                            entryStream.Write(docxBytes, 0, docxBytes.Length);
                        }

                        Console.WriteLine($"  ✅ Saved Survey Report: {docxFilename}");
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