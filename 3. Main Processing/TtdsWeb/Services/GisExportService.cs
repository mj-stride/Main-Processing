using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NetTopologySuite;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using TtdsWeb.Models;
using TtdsWeb.Utils;

namespace TtdsWeb.Services
{
    public class GisExportService : IGisExportService
    {
        private readonly GeometryFactory _gf;
        private const string WGS84_PRJ =
            @"GEOGCS[""WGS 84"",DATUM[""WGS_1984"",SPHEROID[""WGS 84"",6378137,298.257223563]],PRIMEM[""Greenwich"",0],UNIT[""degree"",0.0174532925199433]]";

        // Restored original cause mapping with full textual descriptions and styling
        private static readonly Dictionary<int, (string Label, string Color)> CAUSE_MAP = new()
        {
            { 0, ("Normal Moving", "blue") },
            { 1, ("Loading and Unloading", "pink") },
            { 2, ("Intersection", "orange") },
            { 3, ("Traffic Light", "red") },
            { 4, ("Pedestrian Crossing", "purple") },
            { 5, ("Animal Crossing", "brown") },
            { 6, ("Vehicle Crossing", "maroon") },
            { 7, ("Road Construction", "gray") },
            { 8, ("Blocked by Vehicle", "black") },
            { 9, ("Others", "green") }
        };

        public GisExportService()
        {
            _gf = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        }

        public string WriteDelayLinesShapeFile(TripDataset d, string outFolder, string baseNameNoExt)
        {
            Directory.CreateDirectory(outFolder);
            var feats = new List<IFeature>();
            var chunk = new List<TripRow>();
            string? chunkStatus = null;

            void Flush()
            {
                if (chunk.Count < 2) { chunk.Clear(); chunkStatus = null; return; }

                var coords = chunk
                    .Select(r => new { r, lon = Finite(r.SnappedLon), lat = Finite(r.SnappedLat) })
                    .Where(x => !double.IsNaN(x.lon) && !double.IsNaN(x.lat) && Math.Abs(x.lat) <= 90 && Math.Abs(x.lon) <= 180)
                    .Select(x => new Coordinate(x.lon, x.lat))
                    .ToArray();

                if (coords.Length < 2) { chunk.Clear(); chunkStatus = null; return; }

                var line = _gf.CreateLineString(coords);
                double lenM = chunk.Sum(r => Math.Max(Finite(r.distanceDiff), 0.0));
                double avgKph = chunk.Average(r => (r.Speed ?? SpeedKph(r)));

                // Reverted: Exact delay status logic and cause resolution from original code
                string status = chunkStatus ?? "moving";
                string delayType = "Normal Moving";
                string color = "blue";

                if (status == "delay")
                {
                    // Find the predominant cause ID in the delayed segment
                    var validCauses = chunk
                        .Where(r => r.CauseID.HasValue && r.CauseID.Value > 0)
                        .Select(r => r.CauseID!.Value)
                        .ToList();

                    int primaryCauseId = validCauses.Count > 0
                        ? validCauses.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key
                        : 9; // Default to "Others" if cause is missing during a delay

                    if (CAUSE_MAP.TryGetValue(primaryCauseId, out var causeInfo))
                    {
                        delayType = causeInfo.Label;
                        color = causeInfo.Color;
                    }
                    else
                    {
                        delayType = "Delay (Unspecified)";
                        color = "red";
                    }
                }

                // Reverted: Restored exact attribute table schema for delay lines
                var at = new AttributesTable();
                at.Add("status", status);
                at.Add("color", color);
                at.Add("dly_type", delayType);
                at.Add("avg_kph", Math.Round(avgKph, 2));
                at.Add("len_m", Math.Round(lenM, 2));
                at.Add("dly_len_m", Math.Round(status == "delay" ? lenM : 0.0, 2));

                feats.Add(new Feature(line, at));
                chunk.Clear();
                chunkStatus = null;
            }

            foreach (var r in d.Rows)
            {
                var sp = (r.Speed ?? SpeedKph(r));
                // Reverted: Restored original threshold evaluation (Speed < 5.0 kph or explicit CauseID > 0)
                var status = (sp < 5.0 || (r.CauseID.HasValue && r.CauseID.Value > 0)) ? "delay" : "moving";

                if (chunkStatus == null || status == chunkStatus)
                {
                    chunkStatus = status;
                    chunk.Add(r);
                }
                else
                {
                    // To ensure continuous lines without gaps, duplicate the transition point into the new chunk
                    var lastPoint = chunk.Last();
                    Flush();
                    chunkStatus = status;
                    chunk.Add(lastPoint);
                    chunk.Add(r);
                }
            }
            Flush();

            if (feats.Count == 0) return "";

            var shpPath = Path.Combine(outFolder, baseNameNoExt + ".shp");
            var writer = new ShapefileDataWriter(shpPath, _gf)
            {
                Header = ShapefileDataWriter.GetHeader(feats[0], feats.Count)
            };
            writer.Write(feats);

            File.WriteAllText(Path.Combine(outFolder, baseNameNoExt + ".prj"), WGS84_PRJ);
            File.WriteAllText(Path.Combine(outFolder, baseNameNoExt + ".cpg"), "UTF-8");

            return shpPath;
        }

        public string WriteTripPointsShapeFile(TripDataset d, string outFolder, string baseNameNoExt)
        {
            Directory.CreateDirectory(outFolder);
            var feats = new List<IFeature>();

            foreach (var r in d.Rows)
            {
                if (double.IsNaN(r.SnappedLat) || double.IsNaN(r.SnappedLon)) continue;

                var pt = _gf.CreatePoint(new Coordinate(Finite(r.SnappedLon), Finite(r.SnappedLat)));

                // Reverted: Explicit resolution of cause descriptions and delay flags for every point
                int causeId = r.CauseID ?? 0;
                string causeLabel = CAUSE_MAP.TryGetValue(causeId, out var info) ? info.Label : "Normal Moving";
                double currentSpeed = r.Speed ?? SpeedKph(r);
                bool isDelayed = currentSpeed < 5.0 || causeId > 0;

                var at = new AttributesTable();
                at.Add("ts", r.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss") ?? "");
                at.Add("lat", Math.Round(Finite(r.SnappedLat), 7));
                at.Add("lon", Math.Round(Finite(r.SnappedLon), 7));
                at.Add("speed", Math.Round(currentSpeed, 3));
                at.Add("cause_id", causeId);
                at.Add("cause", causeLabel);
                at.Add("is_delay", isDelayed ? 1 : 0); // Restored delay boolean flag

                feats.Add(new Feature(pt, at));
            }

            if (feats.Count == 0) return "";

            var shpPath = Path.Combine(outFolder, baseNameNoExt + ".shp");
            var writer = new ShapefileDataWriter(shpPath, _gf)
            {
                Header = ShapefileDataWriter.GetHeader(feats[0], feats.Count)
            };
            writer.Write(feats);

            File.WriteAllText(Path.Combine(outFolder, baseNameNoExt + ".prj"), WGS84_PRJ);
            File.WriteAllText(Path.Combine(outFolder, baseNameNoExt + ".cpg"), "UTF-8");

            return shpPath;
        }

        public byte[] BuildAnchorsGeoJson(IEnumerable<ControlPoint> anchors)
        {
            var features = anchors.Select(cp => $@"
                {{
                  ""type"": ""Feature"",
                  ""properties"": {{ ""id"": ""{cp.ControlPointId}"" }},
                  ""geometry"": {{
                    ""type"": ""Point"",
                    ""coordinates"": [{cp.Lng.ToString(CultureInfo.InvariantCulture)}, {cp.Lat.ToString(CultureInfo.InvariantCulture)}]
                  }}
                }}");

            var geojson = $"{{\n  \"type\": \"FeatureCollection\",\n  \"features\": [\n    {string.Join(",", features)}\n  ]\n}}";
            return Encoding.UTF8.GetBytes(geojson);
        }

        private static double Finite(double v) => (double.IsNaN(v) || double.IsInfinity(v)) ? 0.0 : v;

        private static double SpeedKph(TripRow r)
        {
            if (r != null && r.Speed.HasValue)
            {
                var s = Finite(r.Speed.Value);
                if (s >= 0 && s < 300) return s;
            }
            double secs = Math.Max(Finite(r?.secDiff ?? 0.0), 1e-6);
            double distM = Math.Max(Finite(r?.distanceDiff ?? 0.0), 0.0);
            return (distM / secs) * 3.6;
        }
    }
}