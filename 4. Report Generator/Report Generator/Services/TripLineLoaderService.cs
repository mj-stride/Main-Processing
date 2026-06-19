using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using NetTopologySuite.Geometries;
using Report_Generator.Models;

namespace Report_Generator.Services
{
    public class TripLineLoaderService
    {
        private readonly GeometryFactory _geomFactory = new GeometryFactory(new PrecisionModel(), 4326);

        private static readonly char[] HeaderTrimChars = { ' ', '\r', '\n', '\uFEFF', '"' };

        // Mirrors Python's is_trip_file bad_words list.
        private static readonly string[] BadWords = { "anchor", "cp", "detected", "table", "summary" };

        // ⚠️ PLACEHOLDER — your Python SNAP_RE / _time_in_period weren't included in what
        // you pasted. This regex + the period windows below are a best-effort stand-in so
        // the rest of the pipeline compiles and runs. Replace the pattern with whatever your
        // actual Snapped filenames look like (e.g. "12_07-45-30-NB.csv" -> group 2/3/4 =
        // hh/mm/ss), and adjust the AM/MID/PM cutoffs to your real survey windows.
        private static readonly Regex SnapTimeRegex =
            new Regex(@"^(\d+)_(\d{2})[-:](\d{2})[-:](\d{2})", RegexOptions.Compiled);

        private static readonly Dictionary<string, (TimeSpan Start, TimeSpan End)> PeriodWindows = new()
        {
            ["AM"] = (new TimeSpan(6, 0, 0), new TimeSpan(10, 0, 0)),
            ["MID"] = (new TimeSpan(10, 0, 0), new TimeSpan(15, 0, 0)),
            ["PM"] = (new TimeSpan(15, 0, 0), new TimeSpan(19, 0, 0)),
        };

        private static bool IsTripFile(string fileName)
        {
            string name = Path.GetFileName(fileName).ToLowerInvariant();
            return !BadWords.Any(name.Contains);
        }

        private static bool TimeInPeriod(string hh, string mm, string ss, string period)
        {
            if (!PeriodWindows.TryGetValue(period.ToUpperInvariant(), out var window)) return true;
            if (!int.TryParse(hh, out int h) || !int.TryParse(mm, out int m) || !int.TryParse(ss, out int s)) return true;
            var t = new TimeSpan(h, m, s);
            return t >= window.Start && t <= window.End;
        }

        /// <summary>
        /// Port of Python's load_trip_linestring. `vehicleScopedFiles` should already be
        /// filtered to one Region/RoadName/SurveyDate/VehicleType survey (not just the
        /// SegmentAnalysis subfolder — Snapped and KM-CP Detected are siblings of it).
        /// </summary>
        public LineString? LoadTripLinestring(IEnumerable<IFormFile> vehicleScopedFiles, string period, string direction)
        {
            var all = vehicleScopedFiles.ToList();

            var snapped = all.Where(f =>
                    f.FileName.Contains("/Snapped/", StringComparison.OrdinalIgnoreCase) &&
                    f.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
                    IsTripFile(f.FileName))
                .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var cleaned = all.Where(f =>
                    f.FileName.Contains("/Cleaned/", StringComparison.OrdinalIgnoreCase) &&
                    f.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
                    IsTripFile(f.FileName))
                .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine($"    [TRIP] Snapped files found: {snapped.Count}");
            Console.WriteLine($"    [TRIP] Cleaned files found: {cleaned.Count}");

            var candidates = snapped.Concat(cleaned).ToList();
            if (!candidates.Any())
            {
                Console.WriteLine("    ⚠️ No valid trip CSV files found.");
                return null;
            }

            // Filter by period (best-effort — see SnapTimeRegex note above).
            var periodFiles = new List<IFormFile>();
            foreach (var f in candidates)
            {
                var name = Path.GetFileName(f.FileName);
                var m = SnapTimeRegex.Match(name);
                if (m.Success)
                {
                    if (TimeInPeriod(m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, period))
                        periodFiles.Add(f);
                }
                else
                {
                    periodFiles.Add(f); // fallback include, same as Python
                }
            }
            if (!periodFiles.Any()) periodFiles = candidates;

            // Filter by direction.
            string dirUpper = direction.ToUpperInvariant();
            IFormFile chosen = periodFiles.FirstOrDefault(f =>
            {
                var fname = Path.GetFileName(f.FileName).ToUpperInvariant();
                return fname.Contains($"-{dirUpper}") || fname.Contains($"_{dirUpper}") || fname.Contains(dirUpper);
            }) ?? periodFiles.First();

            Console.WriteLine($"    [TRIP] Selected trip file: {Path.GetFileName(chosen.FileName)}");

            using var stream = chosen.OpenReadStream();
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!lines.Any())
            {
                Console.WriteLine($"    ⚠️ Failed reading trip CSV: {chosen.FileName}");
                return null;
            }

            var headers = lines[0].Split(',').Select(h => h.Trim(HeaderTrimChars)).ToArray();

            string? latCol = headers.FirstOrDefault(h => h.Equals("SnappedLat", StringComparison.OrdinalIgnoreCase));
            string? lonCol = headers.FirstOrDefault(h => h.Equals("SnappedLon", StringComparison.OrdinalIgnoreCase));

            if (latCol == null || lonCol == null)
            {
                Console.WriteLine($"    ⚠️ No supported coordinate columns in {chosen.FileName}");
                return null;
            }

            int latIdx = Array.IndexOf(headers, latCol);
            int lonIdx = Array.IndexOf(headers, lonCol);

            var coords = new List<Coordinate>();
            foreach (var line in lines.Skip(1))
            {
                var vals = line.Split(',');
                if (vals.Length > Math.Max(latIdx, lonIdx) &&
                    double.TryParse(vals[lonIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) &&
                    double.TryParse(vals[latIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat))
                {
                    coords.Add(new Coordinate(lon, lat));
                }
            }

            if (coords.Count < 2)
            {
                Console.WriteLine($"    ⚠️ Not enough valid GPS points in {chosen.FileName} (parsed {coords.Count})");
                return null;
            }

            Console.WriteLine($"    ✅ Trip LineString created with {coords.Count} points");
            return _geomFactory.CreateLineString(coords.ToArray());
        }

        /// <summary>
        /// Port of Python's load_cp_points_from_excel. Looks in
        /// "KM-CP Detected/{period}/tables/" for one CSV (prefers a "-NB" filename).
        /// </summary>
        public List<ControlPoint> LoadControlPoints(IEnumerable<IFormFile> vehicleScopedFiles, string period)
        {
            var cps = new List<ControlPoint>();

            var tableFiles = vehicleScopedFiles.Where(f =>
                    f.FileName.Contains("KM-CP Detected", StringComparison.OrdinalIgnoreCase) &&
                    f.FileName.Contains($"/{period}/", StringComparison.OrdinalIgnoreCase) &&
                    f.FileName.Contains("/tables/", StringComparison.OrdinalIgnoreCase) &&
                    f.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine($"    [CP] Found {tableFiles.Count} CSV file(s) in KM-CP Detected/{period}/tables");

            if (!tableFiles.Any())
            {
                Console.WriteLine("    [CP] ❌ Folder not found / no CSVs");
                return cps;
            }

            var chosen = tableFiles.FirstOrDefault(f => Path.GetFileName(f.FileName).Contains("-NB", StringComparison.OrdinalIgnoreCase))
                         ?? tableFiles.First();

            Console.WriteLine($"    [CP] Using file: {Path.GetFileName(chosen.FileName)}");

            using var stream = chosen.OpenReadStream();
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!lines.Any())
            {
                Console.WriteLine("    [CP] ❌ Failed to read file (empty)");
                return cps;
            }

            var headers = lines[0].Split(',').Select(h => h.Trim(HeaderTrimChars)).ToArray();
            Console.WriteLine($"    [CP] Columns detected: {string.Join(", ", headers)}");

            string? latCol = headers.FirstOrDefault(h => new[] { "latitude", "lat", "y" }.Contains(h.ToLowerInvariant()));
            string? lonCol = headers.FirstOrDefault(h => new[] { "longitude", "lon", "lng", "x" }.Contains(h.ToLowerInvariant()));
            string? nameCol = headers.FirstOrDefault(h => new[] { "controlpoint", "cp", "kmcp", "km_cp", "km-cp" }.Contains(h.ToLowerInvariant()));

            if (latCol == null || lonCol == null)
            {
                Console.WriteLine("    [CP] ❌ Latitude/Longitude columns not found");
                return cps;
            }

            int latIdx = Array.IndexOf(headers, latCol);
            int lonIdx = Array.IndexOf(headers, lonCol);
            int nameIdx = nameCol != null ? Array.IndexOf(headers, nameCol) : -1;

            foreach (var line in lines.Skip(1))
            {
                var vals = line.Split(',');
                if (vals.Length > Math.Max(latIdx, lonIdx) &&
                    double.TryParse(vals[lonIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) &&
                    double.TryParse(vals[latIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat))
                {
                    string name = nameIdx >= 0 && vals.Length > nameIdx ? vals[nameIdx].Trim(HeaderTrimChars) : "CP";
                    cps.Add(new ControlPoint { Name = name, Latitude = lat, Longitude = lon });
                }
            }

            if (!cps.Any())
                Console.WriteLine("    [CP] ❌ No valid coordinate rows");
            else
                Console.WriteLine($"    [CP] ✅ Loaded {cps.Count} CP points");

            return cps;
        }
    }
}
