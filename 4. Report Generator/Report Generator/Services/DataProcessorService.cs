using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using Report_Generator.Models;

namespace Report_Generator.Services
{
    public class DataProcessorService
    {
        public List<SegmentAverages> CalculateSegmentAverages (List<TripData> AllTrips)
        {
            var segmentAverages = AllTrips
                .GroupBy(row => new {row.Period, row.Direction, row.From, row.To})
                .Select(group => new SegmentAverages
                {
                    Period = group.Key.Period,
                    Direction = group.Key.Direction,
                    From = group.Key.From,
                    To = group.Key.To,

                    TravelTimeSec = Math.Round(group.Average(x => x.TravelTimeSec), 2),
                    DistanceM = Math.Round(group.Average(x => x.DistanceM), 2),
                    TravelSpeedKph = Math.Round(group.Average(x => x.TravelSpeedKph), 2),
                    RunningSpeedKph = Math.Round(group.Average(x => x.RunningSpeedKph), 2), 
                    DelayTimeSec = Math.Round(group.Average(x => x.Delays), 2),
                    DelayLengthM = Math.Round(group.Average(x => x.DelayLengthM), 2),

                    DelayCausesSummary = GenerateDelayCauseSummary(group.Select(x => x.DelayCauses))
                })
                .ToList();

            return segmentAverages;
        }

        public List<DirectionalAverages> CalculateDirectionalAverages(List<TripTotalData> tripTotals)
        {
            return tripTotals
                .GroupBy(t => new { t.Period, t.Direction })
                .Select(group => new DirectionalAverages
                {
                    Period = group.Key.Period,
                    Direction = group.Key.Direction,
                    AvgTravelTimeSec = Math.Round(group.Average(x => x.TotalTravelTimeSec), 2),
                    AvgDistanceM = Math.Round(group.Average(x => x.TotalDistanceM), 2),
                    AvgTravelSpeedKph = Math.Round(group.Average(x => x.AvgTravelSpeedKph), 2),
                    AvgRunningSpeedKph = Math.Round(group.Average(x => x.AvgRunningSpeedKph), 2),
                    AvgDelayTimeSec = Math.Round(group.Average(x => x.TotalDelayTimeSec), 2),
                    AvgDelayLengthM = Math.Round(group.Average(x => x.TotalDelayLengthM), 2),
                })
                .ToList();
        }

        private string GenerateDelayCauseSummary (IEnumerable<string> causes)
        {
            var validCauses = causes.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

            if (!validCauses.Any()) return "";

            var indivCauses = validCauses
                .SelectMany(c => c.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(c => c.Trim())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .ToList();

            if (!indivCauses.Any()) return "";

            int totalCauses = indivCauses.Count;

            var summaryParts = indivCauses
                .GroupBy(c => c)
                .Select(group => new
                {
                    Cause = group.Key,
                    Count = group.Count(),
                    Percentage = Math.Round((double)group.Count() / totalCauses * 100, 1)
                })
                .OrderByDescending(x => x.Count)
                .Select(x => $"{x.Cause} ({x.Count}, {x.Percentage:0.0}%)");

            return string.Join("; ", summaryParts);
        }
    }
}