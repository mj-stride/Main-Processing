using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Report_Generator.Models;

namespace Report_Generator.Services
{
    public class ShapefileExportService
    {
        private readonly GeometryFactory _gf = new GeometryFactory(new PrecisionModel(), 4326);

        private const string Wgs84Prj =
            "GEOGCS[\"GCS_WGS_1984\"," +
            "DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]]," +
            "PRIMEM[\"Greenwich\",0.0]," +
            "UNIT[\"Degree\",0.0174532925199433]]";

        public List<(string FileName, byte[] Content)> WriteSegmentsShapefile(
            List<TripCp2CpSegment> segments, string baseName)
        {
            if (segments == null || segments.Count == 0)
                return new();

            var features = segments.Select(s =>
            {
                var attrs = new AttributesTable();
                attrs.Add("FromCP",   Truncate(s.FromCP,    50));
                attrs.Add("ToCP",     Truncate(s.ToCP,      50));

                attrs.Add("SpeedKph", s.SpeedKph.HasValue && !double.IsNaN(s.SpeedKph.Value)
                    ? s.SpeedKph.Value : -1.0);

                attrs.Add("SpeedCat", Truncate(s.SpeedCat,   20));
                attrs.Add("Color",    Truncate(s.ColorName,  10));
                attrs.Add("LineWd",   (double)s.LineWidth);

                return (IFeature)new Feature(s.Geometry, attrs);
            }).ToList();

            return WriteToTempAndRead(features, baseName);
        }

        public List<(string FileName, byte[] Content)> WriteTripLineShapefile(
            LineString tripLine, string period, string direction, string baseName)
        {
            if (tripLine == null || tripLine.IsEmpty)
                return new();

            var attrs = new AttributesTable();
            attrs.Add("Period", Truncate(period,    5));
            attrs.Add("Dir",    Truncate(direction, 5));

            return WriteToTempAndRead(new List<IFeature> { new Feature(tripLine, attrs) }, baseName);
        }

        private List<(string FileName, byte[] Content)> WriteToTempAndRead(
            List<IFeature> features, string baseName)
        {
            var tempDir  = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var tempBase = Path.Combine(tempDir, baseName);
            var result   = new List<(string, byte[])>();

            Directory.CreateDirectory(tempDir);

            try
            {
                // Write .shp, .shx, .dbf
                var writer = new ShapefileDataWriter(tempBase, _gf);
                writer.Header = ShapefileDataWriter.GetHeader(features[0], features.Count);
                writer.Write(features);
                
                // Write .prj manually (NTS doesn't do this)
                File.WriteAllText(tempBase + ".prj", Wgs84Prj);

                // Collect every component that was created
                foreach (var ext in new[] { ".shp", ".shx", ".dbf", ".prj", ".cpg" })
                {
                    var path = tempBase + ext;
                    if (File.Exists(path))
                        result.Add((baseName + ext, File.ReadAllBytes(path)));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [SHP] ⚠️ Failed to write '{baseName}': {ex.Message}");
            }
            finally
            {
                // Best-effort cleanup — don't crash the job if this fails
                try { Directory.Delete(tempDir, recursive: true); }
                catch { /* ignored */ }
            }

            return result;
        }

        // dBase character fields have a max of 254 bytes.
        // Silently truncate so the writer never throws on long CP names.
        private static string Truncate(string? s, int max) =>
            (s ?? "").Length > max ? s!.Substring(0, max) : (s ?? "");
    }
}
