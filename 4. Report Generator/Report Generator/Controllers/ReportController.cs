using CsvHelper;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using Report_Generator.Models;
using Report_Generator.Services;

namespace Report_Generator.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ReportController : ControllerBase
    {
        private readonly FolderScannerService _folderScanner;
        private readonly CsvParserService _csvParser;
        private readonly CsvExportService _csvExport;

        public ReportController (FolderScannerService folderScanner, CsvParserService csvParser, CsvExportService csvExport)
        {
            _folderScanner = folderScanner;
            _csvParser = csvParser;
            _csvExport = csvExport;
        }

        [HttpPost("generate")]
        [DisableRequestSizeLimit]
        [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue, ValueCountLimit = int.MaxValue)]
        public IActionResult GenerateReports([FromForm] List<IFormFile> files)
        {
            if (files == null || !files.Any())
            {
                return BadRequest("⚠️ No files uploaded.");
            }

            var filePaths = files.Select(f => f.FileName).ToList();
            var vehicleDirs = _folderScanner.IdentifyVehicleFolders(filePaths);

            if (!vehicleDirs.Any())
            {
                return NotFound("⚠️ No VehicleType folders found (no SegmentAnalysis folder detected). Try Uploading the Region Folder");
            }

            var dataProcessor = new DataProcessorService();

            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                foreach (var survey in vehicleDirs)
                {
                    Console.WriteLine($"PROCESSING: {survey.Region} | {survey.RoadName} | {survey.SurveyDate} | {survey.VehicleType}");

                    var surveyTripData = new List<TripData>();
                    var surveyFiles = files.Where(f => f.FileName.StartsWith(survey.SegmentAnalysisPath)).ToList();

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
                                    var data = _csvParser.ReadTripCsv(stream);

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

                                    surveyTripData.AddRange(data);
                                }
                            }
                        }
                    }

                    if (surveyTripData.Any())
                    {
                        // Directional Averages
                        var directionalAverages = dataProcessor.CalculateDirectionalAverages(surveyTripData);
                        string[] periods = { "AM", "MID", "PM" };

                        foreach (var period in periods)
                        {
                            string DirAvg = _csvExport.GenerateDirectionalAveragesCsv(directionalAverages, period);
                            string zipEntryName = $"{survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}/{period}_DirectionalAverages.csv";
                            var zipEntry = zip.CreateEntry(zipEntryName, CompressionLevel.Fastest);

                            using (var entryStream = zipEntry.Open())
                            using (var streamWriter = new StreamWriter(entryStream))
                            {
                                streamWriter.Write(DirAvg);
                            };
                        }

                        // Segment Averages
                        var segmentAverages = dataProcessor.CalculateSegmentAverages(surveyTripData);
                        //Console.WriteLine($"Generated {segmentAverages.Count} Segment Averages.");
                    }
                }
            }

            memory.Position = 0;
            string zipName = $"Report_{DateTime.Now:yyyyMMdd}.zip";

            return File(memory.ToArray(), "application/zip", zipName);
        }
    }
}