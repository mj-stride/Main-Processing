using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PrivateTransportCleaning.Models;

namespace PrivateTransportCleaning.Services
{
    public class CsvExportService
    {
        public string Export(List<SnappedResult> results, string outputPath, string fileName)
        {
            Directory.CreateDirectory(outputPath);

            string fullPath = Path.Combine(outputPath, fileName);

            var sb = new StringBuilder();

            sb.AppendLine("SnappedLat,SnappedLon,DeviationMeters");

            foreach (var r in results)
            {
                sb.AppendLine(
                    $"{r.SnappedLat.ToString(CultureInfo.InvariantCulture)}," +
                    $"{r.SnappedLon.ToString(CultureInfo.InvariantCulture)}," +
                    $"{r.DeviationMeters.ToString(CultureInfo.InvariantCulture)}"
                );
            }

            File.WriteAllText(fullPath, sb.ToString());

            return fullPath;
        }
    }
}