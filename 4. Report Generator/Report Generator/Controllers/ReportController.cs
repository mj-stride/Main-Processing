using CsvHelper;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
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

        public ReportController()
        {
            _folderScanner = new FolderScannerService();
            _csvParser = new CsvParserService();
            _csvExport = new CsvExportService();
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

            foreach(var survey in vehicleDirs)
            {
                Console.WriteLine($"\nPROCESSING: {survey.Region} | {survey.RoadName} | {survey.SurveyDate} | {survey.VehicleType}");

                var surveyTripData = new List<TripData>();
                var surveyFiles = files.Where(f => f.FileName.StartsWith(survey.SegmentAnalysisPath)).ToList();

                foreach (var file in surveyFiles)
                {
                    if (file.FileName.EndsWith(".csv"))
                    {
                        // Filter .csv files with pattern: {TripNo}_{Anything}-NB/SB.csv
                        string csvFileName = System.IO.Path.GetFileNameWithoutExtension(file.FileName);
                        string pattern = @"^(\d+)_.*-(NB|SB)$";
                        Match match = Regex.Match(csvFileName, pattern, RegexOptions.IgnoreCase);

                        if (match.Success)
                        {
                            using (var stream = file.OpenReadStream())
                            {
                                var data = _csvParser.ReadTripCsv(stream);

                                // Get TripNo and Direction from filename
                                int tripNo = int.Parse(match.Groups[1].Value);
                                string direction = match.Groups[2].Value.ToUpper();

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
                    // Directional Averages Test
                    var directionalAverages = dataProcessor.CalculateDirectionalAverages(surveyTripData);
                    string DirAvgAm = _csvExport.GenerateDirectionalAveragesCsv(directionalAverages, "AM");
                    // tmp test
                    string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    string csvSavePath = Path.Combine(desktopPath, $"AM_DirectionalAverages_{survey.VehicleType}.csv");
                    System.IO.File.WriteAllText(csvSavePath, DirAvgAm);
                    Console.WriteLine($"CSV SAVED on: {csvSavePath}");

                    // Segment Averages Test
                    var segmentAverages = dataProcessor.CalculateSegmentAverages(surveyTripData);
                    Console.WriteLine($"Generated {segmentAverages.Count} Segment Averages.");

                    // Delay Causes Summary Test
                    //foreach (var seg in segmentAverages.Where(s => !string.IsNullOrEmpty(s.DelayCausesSummary)))
                    //{
                    //    Console.WriteLine($"Segment {seg.From} to {seg.To} Delays: {seg.DelayCausesSummary}");
                    //}

                }
            }

            return Ok("Files Sent!!");
        }
    }
}