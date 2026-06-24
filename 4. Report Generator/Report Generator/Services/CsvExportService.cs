using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Report_Generator.Models;
using Microsoft.AspNetCore.CookiePolicy;
using System.Diagnostics.Contracts;

namespace Report_Generator.Services
{
    public class CsvExportService
    {
        public string GenerateDirectionalAveragesCsv(List<DirectionalAverages> averages, string period)
        {
            var periodData = averages.Where(s => s.Period == period).ToList();

            var nb = periodData.FirstOrDefault(d => d.Direction == "NB");
            var sb = periodData.FirstOrDefault(d => d.Direction == "SB");

            var csv = new StringBuilder();
            string formatTime(double? seconds) => seconds.HasValue ? TimeSpan.FromSeconds(Math.Round(seconds.Value)).ToString(@"h\:mm\:ss") : "";
            string formatDistance(double? distance) => distance.HasValue ? (distance.Value / 1000.0).ToString("0.00") : "";
            string formatSpeed(double? speed) => speed.HasValue ? speed.Value.ToString("0.00") : "";

            csv.AppendLine("Metric,NB/EB,SB/WB,Units");
            csv.AppendLine($"Avg Travel Time,{formatTime(nb?.AvgTravelTimeSec)},{formatTime(sb?.AvgTravelTimeSec)},hh:mm:ss");
            csv.AppendLine($"Avg Distance,{formatDistance(nb?.AvgDistanceM)},{formatDistance(sb?.AvgDistanceM)},km");
            csv.AppendLine($"Avg Travel Speed,{formatSpeed(nb?.AvgTravelSpeedKph)},{formatSpeed(sb?.AvgTravelSpeedKph)},kph");
            csv.AppendLine($"Avg Running Speed,{formatSpeed(nb?.AvgRunningSpeedKph)},{formatSpeed(sb?.AvgRunningSpeedKph)},kph");
            csv.AppendLine($"Avg Delay Time,{formatTime(nb?.AvgDelayTimeSec)},{formatTime(sb?.AvgDelayTimeSec)},hh:mm:ss");
            csv.AppendLine($"Avg Delay Length,{formatDistance(nb?.AvgDelayLengthM)},{formatDistance(sb?.AvgDelayLengthM)},km");

            return csv.ToString();
        }
    }
}
