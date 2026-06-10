using System.Globalization;
using CsvHelper.Configuration.Attributes;

namespace TtdsWeb.Models;

public class TripRow
{
    // CSV columns expected
    public double SnappedLat { get; set; }
    public double SnappedLon { get; set; }
    public double secDiff { get; set; }
    public double distanceDiff { get; set; }

    // Optional
    public double? Speed { get; set; }
    public int? CauseID { get; set; }

    // Timestamp parsing tolerant
    public DateTime? Timestamp { get; set; }
}
