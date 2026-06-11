using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using HarfBuzzSharp;
using Report_Generator.Models;

namespace Report_Generator.Services
{
    public class DataProcessorService
    {
        public List<SegmentAnalysis> CalculateSegmentAverages (List<TripData> AllTrips)
        {
            var segmentGroups = AllTrips
                .GroupBy(row => new {row.Period, row.Direction, row.From, row.To})
                .Select(group => new SegmentAnalysis
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

            return segmentGroups;
        }

        public List<DirectionalAverages> CalculateDirectionalAverages (List<TripData> AllTrips)
        {
            var TotalTrips = AllTrips
                .GroupBy(row => new { row.Period, row.Direction, row.TripNo })
                .Select(group => new
                {
                    Period = group.Key.Period,
                    Direction = group.Key.Direction,
                    TotalTime = group.Sum(x => x.TravelTimeSec),
                    TotalDistance = group.Sum(x => x.DistanceM),
                    AvgTravelSpeed = group.Average(x => x.TravelSpeedKph),
                    AvgRunningSpeed = group.Average(x => x.RunningSpeedKph),
                    TotalDelay = group.Sum(x => x.Delays),
                    TotalDelayLength = group.Sum(x => x.DelayLengthM)
                })
                .ToList();

            var directionalAverages = TotalTrips
                .GroupBy(trip => new { trip.Period, trip.Direction })
                .Select(group => new DirectionalAverages
                {
                    Period = group.Key.Period,
                    Direction = group.Key.Direction,
                    AvgTravelTimeSec = Math.Round(group.Average(x => x.TotalTime), 2),
                    AvgDistanceM = Math.Round(group.Average(x => x.TotalDistance), 2),
                    AvgTravelSpeedKph = Math.Round(group.Average(x => x.AvgTravelSpeed), 2),
                    AvgRunningSpeedKph = Math.Round(group.Average(x => x.AvgRunningSpeed), 2),
                    AvgDelayTimeSec = Math.Round(group.Average(x => x.TotalDelay), 2),
                    AvgDelayLengthM = Math.Round(group.Average(x => x.TotalDelayLength), 2)
                })
                .ToList();

            return directionalAverages;
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
                .Select(x => $"{x.Cause} ({x.Count}, {x.Percentage}%)");

            return string.Join("; ", summaryParts);
        }
    }
}