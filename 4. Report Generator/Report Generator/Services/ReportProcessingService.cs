using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Report_Generator.Models;
using Report_Generator.Services;

namespace Report_Generator.Services
{
    public class ReportProcessingService
    {
        private readonly FolderScannerService _folderScanner;
        private readonly CsvParserService _csvParser;
        private readonly CsvExportService _csvExport;
        private readonly ChartGeneratorService _chartGenerator;
        private readonly WordExportService _wordExport;
        private readonly TripLineLoaderService _tripLineLoader;
        private readonly SpeedSegmentService _speedSegmentService;
        private readonly SpeedMapRenderer _mapRenderer;
        private readonly ZipExtractService _zipExtract;

        public ReportProcessingService(
            FolderScannerService folderScanner,
            CsvParserService csvParser,
            CsvExportService csvExport,
            ChartGeneratorService chartGenerator,
            WordExportService wordExport,
            TripLineLoaderService tripLineLoader,
            SpeedSegmentService speedSegmentService,
            SpeedMapRenderer mapRenderer,
            ZipExtractService zipExtract)
        {
            _folderScanner = folderScanner;
            _csvParser = csvParser;
            _csvExport = csvExport;
            _chartGenerator = chartGenerator;
            _wordExport = wordExport;
            _tripLineLoader = tripLineLoader;
            _speedSegmentService = speedSegmentService;
            _mapRenderer = mapRenderer;
            _zipExtract = zipExtract;
        }

        public async Task<(byte[] ZipBytes, string ZipFilename)> ProcessAsync(
            List<IFormFile> files,
            CancellationToken ct = default)
        {
            if (files == null || !files.Any())
                throw new InvalidOperationException("No files provided.");

            var filePaths = files.Select(f => f.FileName).ToList();
            var vehicleDirs = _folderScanner.IdentifyVehicleFolders(filePaths);

            if (!vehicleDirs.Any())
                throw new InvalidOperationException(
                    "No VehicleType folders found (no SegmentAnalysis folder detected). Upload the Region Folder.");

            var dataProcessor = new DataProcessorService();

            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                Console.WriteLine(" -> Copying uploaded files to output ZIP...");
                var addedEntries = new HashSet<string>();
                string prefixToStrip = "";

                if (vehicleDirs.Any())
                {
                    var first = vehicleDirs.First();
                    string normPath = first.SegmentAnalysisPath.Replace('\\', '/');
                    string suffix = $"{first.Region}/{first.RoadName}/{first.SurveyDate}/{first.VehicleType}/SegmentAnalysis";
                    int suffixIdx = normPath.IndexOf(suffix);
                    if (suffixIdx > 0)
                    {
                        prefixToStrip = first.SegmentAnalysisPath.Substring(0, suffixIdx).Replace('\\', '/');
                    }
                }

                foreach (var file in files)
                {
                    string entryPath = file.FileName.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(prefixToStrip) && entryPath.StartsWith(prefixToStrip))
                    {
                        entryPath = entryPath.Substring(prefixToStrip.Length);
                    }

                    bool shouldCopy = entryPath.Contains("/KM-CP Detected/", StringComparison.OrdinalIgnoreCase) ||
                                      entryPath.Contains("/Snapped/", StringComparison.OrdinalIgnoreCase) ||
                                      entryPath.Contains("/SegmentAnalysis/", StringComparison.OrdinalIgnoreCase) ||
                                      entryPath.Contains("/Graphs/AM/", StringComparison.OrdinalIgnoreCase) ||
                                      entryPath.Contains("/Graphs/MID/", StringComparison.OrdinalIgnoreCase) ||
                                      entryPath.Contains("/Graphs/PM/", StringComparison.OrdinalIgnoreCase) ||
                                      entryPath.Contains("/Shapes/", StringComparison.OrdinalIgnoreCase);

                    if (shouldCopy && addedEntries.Add(entryPath))
                    {
                        var entry = zip.CreateEntry(entryPath, CompressionLevel.Fastest);
                        using var es = entry.Open();
                        using var fs = file.OpenReadStream();
                        fs.CopyTo(es);
                    }
                }

                foreach (var survey in vehicleDirs)
                {
                    ct.ThrowIfCancellationRequested();

                    Console.WriteLine($"\n▶ Processing: {survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}");
                    Console.WriteLine(" -> Reading CSV files...");

                    string vtPath = survey.SegmentAnalysisPath[
                        ..(survey.SegmentAnalysisPath.Length - "SegmentAnalysis".Length)];

                    var surveyFiles = files.Where(f => f.FileName.StartsWith(survey.SegmentAnalysisPath)).ToList();
                    var vehicleFiles = files.Where(f => f.FileName.StartsWith(vtPath)).ToList();

                    var surveyTripData = new List<TripData>();
                    var surveyTripTotals = new List<TripTotalData>();

                    foreach (var file in surveyFiles)
                    {
                        if (!file.FileName.EndsWith(".csv")) continue;

                        string csvName = Path.GetFileNameWithoutExtension(file.FileName);
                        var match = Regex.Match(csvName, @"^(\d+)_.*-(NB|SB|EB|WB)$", RegexOptions.IgnoreCase);
                        if (!match.Success) continue;

                        using var stream = file.OpenReadStream();
                        var (data, missing) = _csvParser.ReadTripCsv(stream);
                        if (missing.Any())
                        {
                            Console.WriteLine($"⚠️ SKIPPED {file.FileName} — missing columns: {string.Join(", ", missing)}");
                            continue;
                        }

                        int tripNo = int.Parse(match.Groups[1].Value);
                        string direction = match.Groups[2].Value.ToUpper();
                        if (direction == "EB") direction = "NB";
                        if (direction == "WB") direction = "SB";

                        string? dirPath = Path.GetDirectoryName(file.FileName);
                        string period = dirPath != null ? Path.GetFileName(dirPath).ToUpper() : "";

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

                    if (!surveyTripData.Any())
                    {
                        Console.WriteLine($"  ⚠️ No valid survey data found for {survey.VehicleType}.");
                        continue;
                    }

                    Console.WriteLine(" -> Calculating Directional Averages...");
                    var directionalAverages = dataProcessor.CalculateDirectionalAverages(surveyTripTotals);

                    Console.WriteLine(" -> Calculating Segment Averages...");
                    var segmentAverages = dataProcessor.CalculateSegmentAverages(surveyTripData);

                    string[] periods = { "AM", "MID", "PM" };
                    string[] directions = { "NB", "SB" };
                    var reportImages = new Dictionary<string, byte[]>();

                    foreach (var period in periods)
                    {
                        ct.ThrowIfCancellationRequested();

                        string DirAvg = _csvExport.GenerateDirectionalAveragesCsv(directionalAverages, period);
                        string csvZipEntry = $"{survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}/DirectionalAverages/{period}_DirectionalAverages.csv";
                        var csvEntry = zip.CreateEntry(csvZipEntry, CompressionLevel.Fastest);
                        using (var es = csvEntry.Open())
                        using (var sw = new StreamWriter(es))
                            sw.Write(DirAvg);

                        Console.WriteLine($"  ✅ Saved Directional Averages: {period}_DirectionalAverages.csv");

                        int chartsGenerated = 0;
                        foreach (var direction in directions)
                        {
                            var plotSegments = segmentAverages
                                .Where(s => s.Period == period && s.Direction == direction)
                                .ToList();

                            string directionFull = direction == "NB" ? "Northbound/Eastbound" : "Southbound/Westbound";

                            if (!plotSegments.Any()) continue;

                            // ---- Speed chart ----
                            string chartTitle = $"{period} - {directionFull} Speed Comparison";
                            byte[]? chartBytes = _chartGenerator.GenerateSpeedPairChart(plotSegments, chartTitle, direction);
                            if (chartBytes != null)
                            {
                                reportImages[$"{period}_{direction}_Chart"] = chartBytes;
                                string chartEntry = $"{survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}/Graphs/{period}_{direction}_SpeedPair.png";
                                var ce = zip.CreateEntry(chartEntry, CompressionLevel.Fastest);
                                using var ces = ce.Open();
                                ces.Write(chartBytes, 0, chartBytes.Length);
                                chartsGenerated++;
                            }

                            // ---- Map generation ----
                            var tripLine = _tripLineLoader.LoadTripLinestring(vehicleFiles, period, direction);
                            var controlPoints = _tripLineLoader.LoadControlPoints(vehicleFiles, period);

                            if (tripLine != null && controlPoints.Any())
                            {
                                var speedLookup = _speedSegmentService.BuildSpeedLookup(plotSegments);
                                var mapSegments = _speedSegmentService.MakeTripCp2CpSegments(tripLine, controlPoints, speedLookup);

                                if (mapSegments.Any())
                                {
                                    string mapTitle = $"{period} {directionFull} Trip Speed Map";
                                    var mapBytes = await _mapRenderer.ExportTripSpeedPngAsync(mapSegments, controlPoints, mapTitle);

                                    if (mapBytes != null)
                                    {
                                        reportImages[$"{period}_{direction}_Map"] = mapBytes;
                                        string mapEntry = $"{survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}/Graphs/Maps/{period}_{direction}_TripSpeedMap.png";
                                        var me = zip.CreateEntry(mapEntry, CompressionLevel.Fastest);
                                        using var mes = me.Open();
                                        mes.Write(mapBytes, 0, mapBytes.Length);
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

                        if (chartsGenerated > 0)
                            Console.WriteLine($"  -> Generated {chartsGenerated} speed charts for {period}.");
                    }

                    // ---- DOCX Report ----
                    Console.WriteLine("  -> Generating DOCX Report...");
                    byte[] reportBytes = _wordExport.GenerateSurveyReport(
                        survey.Region, survey.RoadName, survey.SurveyDate, survey.VehicleType,
                        directionalAverages, segmentAverages, reportImages);

                    string reportFilename = $"{survey.Region}_{survey.RoadName.Replace(" ", "")}_{survey.SurveyDate}_{survey.VehicleType}_Survey_Report.docx";
                    var re = zip.CreateEntry($"{survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}/{reportFilename}", CompressionLevel.Fastest);
                    using (var res = re.Open()) res.Write(reportBytes, 0, reportBytes.Length);
                    Console.WriteLine($"  ✅ Saved Survey Report: {reportFilename}");

                    // ---- DOCX Annex ----
                    Console.WriteLine("  -> Generating ANNEX File...");
                    byte[] annexBytes = _wordExport.GenerateSurveyAnnex(
                        survey.Region, survey.RoadName, survey.SurveyDate, survey.VehicleType, surveyTripData);

                    string annexFilename = $"{survey.Region}_{survey.RoadName.Replace(" ", "")}_{survey.SurveyDate}_{survey.VehicleType}_Survey_Annex.docx";
                    var ae = zip.CreateEntry($"{survey.Region}/{survey.RoadName}/{survey.SurveyDate}/{survey.VehicleType}/{annexFilename}", CompressionLevel.Fastest);
                    using (var aes = ae.Open()) aes.Write(annexBytes, 0, annexBytes.Length);
                    Console.WriteLine($"  ✅ Saved Survey Annex: {annexFilename}");

                    Console.WriteLine($"   ✔ Processing completed for {survey.VehicleType}");
                }
            }

            memory.Position = 0;
            string zipName = $"Survey_Reports.zip";
            Console.WriteLine("\n✅ All reports bundled successfully into ZIP.");
            return (memory.ToArray(), zipName);
        }
    }
}
