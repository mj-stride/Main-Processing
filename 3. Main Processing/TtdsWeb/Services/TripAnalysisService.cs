using CsvHelper;
using System.Globalization;
using TtdsWeb.Models;
using TtdsWeb.Utils;

namespace TtdsWeb.Services
{
    public interface ITripAnalysisService
    {
        (List<SegmentResult> results, List<object> segments, AnalysisSummary summary) AnalyzeTrip(List<TripRow> df, List<ControlPoint> filtered);
        AnalysisSummary Aggregate_MethodA(IEnumerable<AnalysisSummary> sums);
        List<TripRow> ReadTripCsv(string path);
    }

    public class TripAnalysisService : ITripAnalysisService
    {
        private static readonly Dictionary<int, (string Label, string Color)> CAUSE_MAP = new()
        {
            {1,("Loading and Unloading","pink")},
            {2,("Intersection","orange")},
            {3,("Traffic Light","red")},
            {4,("Pedestrian Crossing","purple")},
            {5,("Animal Crossing","brown")},
            {6,("Vehicle Crossing","maroon")},
            {7,("Road Construction","gray")},
            {8,("Blocked by Vehicle","black")},
            {9,("Others","green")}
        };

        public (List<SegmentResult> results, List<object> segments, AnalysisSummary summary) AnalyzeTrip(List<TripRow> df, List<ControlPoint> filtered)
        {
            var visitList = DetectCpVisits(df, filtered, enterRadiusM: 300.0, exitRadiusM: 300.0);

            var visited = visitList
                .Select(v => new { v, cp = filtered.FirstOrDefault(c => c.ControlPointId == v.CpId) })
                .Where(x => x.cp != null)
                .Select(x => (cpId: x.v.CpId, lat: x.cp!.Lat, lon: x.cp!.Lng, idx: x.v.Index))
                .ToList();

            var results = new List<SegmentResult>();
            var segments = new List<object>();
            var usedPairs = new HashSet<string>();

            for (int i = 0; i < visited.Count - 1; i++)
            {
                var cp1 = visited[i];
                var cp2 = visited[i + 1];
                var pairKey = $"{cp1.cpId}|{cp2.cpId}";
                if (usedPairs.Contains(pairKey)) continue;
                usedPairs.Add(pairKey);

                int idx1 = cp1.idx, idx2 = cp2.idx;
                bool reversed = idx1 > idx2;
                string note = reversed ? "Reverse Order" : "✔️";
                if (reversed) { var t = idx1; idx1 = idx2; idx2 = t; }

                var segRows = df.GetRange(idx1, idx2 - idx1 + 1);

                double timeSec = segRows.Sum(r => Finite(r.secDiff));
                double timeMin = Math.Round(timeSec / 60.0, 2);
                double distanceM = segRows.Sum(r => Finite(r.distanceDiff));
                double travelSpeed = timeSec > 0 ? (distanceM / 1000.0) / (timeSec / 3600.0) : 0.0;

                var startTime = segRows.First().Timestamp;
                var endTime = segRows.Last().Timestamp;

                int delayCount = segRows.Count(r => (r.Speed ?? 0) <= 5.0);

                double delayLenTableM = segRows
                    .Where(r => (r.Speed ?? 0) <= 5.0)
                    .Sum(r => Math.Max(Finite(r.distanceDiff), 0.0));

                var subsegments = new List<(string status, List<TripRow> rows)>();
                string? currStatus = null;
                var curr = new List<TripRow>();
                foreach (var r in segRows)
                {
                    double sp = r.Speed ?? 0.0;
                    string status = sp >= 25 ? "moving" : "delay";
                    if (currStatus == null) currStatus = status;
                    if (status == currStatus) curr.Add(r);
                    else
                    {
                        if (curr.Count > 0) subsegments.Add((currStatus, new List<TripRow>(curr)));
                        curr.Clear(); curr.Add(r); currStatus = status;
                    }
                }
                if (curr.Count > 0 && currStatus != null) subsegments.Add((currStatus, curr));

                var delayCauses = new List<string>();
                foreach (var (status, rows) in subsegments)
                {
                    if (rows.Count < 2) continue;

                    var latlngs = rows.Select(r => new[] { Finite(r.SnappedLat), Finite(r.SnappedLon) }).ToList();
                    double avgSpeed = rows.Any(r => r.Speed.HasValue)
                        ? rows.Average(r => Finite(r.Speed ?? 0))
                        : 0.0;

                    double subDistM = rows.Sum(r => Finite(r.distanceDiff));
                    double delayLengthM = (status == "delay") ? Math.Round(subDistM, 2) : 0.0;

                    string label, color;
                    if (status == "delay")
                    {
                        var causeIds = rows.Where(r => r.CauseID.HasValue)
                                           .Select(r => r.CauseID!.Value)
                                           .Where(cid => CAUSE_MAP.ContainsKey(cid))
                                           .ToList();

                        if (causeIds.Any())
                        {
                            foreach (var cid in causeIds) delayCauses.Add(CAUSE_MAP[cid].Label);
                            var mainId = causeIds.GroupBy(c => c)
                                                 .OrderByDescending(g => g.Count())
                                                 .First().Key;
                            (label, color) = CAUSE_MAP[mainId];
                        }
                        else
                        {
                            (label, color) = ("Delay", "blue");
                        }
                    }
                    else
                    {
                        (label, color) = ("Normal Moving", "blue");
                    }

                    segments.Add(new
                    {
                        path = latlngs,
                        color = color,
                        cause = label,
                        speed = Math.Round(avgSpeed, 2),
                        status = status,
                        fromCp = cp1.cpId,
                        toCp = cp2.cpId,
                        delayLengthM = delayLengthM
                    });
                }

                string causesOut = "";
                if (delayCount > 0 && delayLenTableM > 0)
                {
                    causesOut = delayCauses.Any()
                        ? string.Join(", ", delayCauses.Distinct().OrderBy(s => s))
                        : "Others";
                }

                results.Add(new SegmentResult
                {
                    From = cp1.cpId,
                    To = cp2.cpId,
                    StartTime = startTime.HasValue ? startTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "",
                    EndTime = endTime.HasValue ? endTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "",
                    TravelTimeSec = Math.Round(timeSec, 1),
                    TravelTimeMin = timeMin,
                    DistanceM = Math.Round(distanceM, 1),
                    TravelSpeedKph = Math.Round(travelSpeed, 2),

                    RunningSpeedKph = (timeSec - delayCount) > 0
                        ? Math.Round((distanceM * 3.6 / (timeSec - delayCount)), 2)
                        : 0,

                    Delays = delayCount,
                    DelayLengthM = Math.Round(delayLenTableM, 2),
                    DelayCauses = causesOut,
                    Note = note
                });
            }

            var valid = results.Where(r => r.TravelTimeMin.HasValue && r.DistanceM.HasValue).ToList();
            double totalTravelMin = valid.Sum(r => r.TravelTimeSec ?? 0) / 60;
            double totalDistKm = valid.Sum(r => r.DistanceM ?? 0) / 1000.0;
            double totalDelayMin = valid.Sum(r => (r.Delays ?? 0)) / 60.0;
            double totalDelayLen = valid.Sum(r => r.DelayLengthM ?? 0);
            double avgTravel = totalTravelMin > 0 ? totalDistKm / (totalTravelMin / 60.0) : 0.0;
            double avgRunning = (totalTravelMin - totalDelayMin) > 0 ? totalDistKm / ((totalTravelMin - totalDelayMin) / 60.0) : 0.0;

            var summary = new AnalysisSummary
            {
                TotalTravelTimeMin = Math.Round(totalTravelMin, 2),
                TotalDistanceKm = Math.Round(totalDistKm, 2),
                AvgTravelSpeed = Math.Round(avgTravel, 2),
                AvgRunningSpeed = Math.Round(avgRunning, 2),
                TotalDelayMin = Math.Round(totalDelayMin, 2),
                TotalDelayLength = Math.Round(totalDelayLen, 2)
            };

            return (results, segments, summary);
        }

        public AnalysisSummary Aggregate_MethodA(IEnumerable<AnalysisSummary> sums)
        {
            var list = sums?.ToList() ?? new List<AnalysisSummary>();
            if (list.Count == 0) return new AnalysisSummary();

            // These are already "per trip totals" from AnalyzeTrip summary
            double totalTravelMin = list.Sum(x => x.TotalTravelTimeMin);
            double totalDistKm = list.Sum(x => x.TotalDistanceKm);
            double totalDelayMin = list.Sum(x => x.TotalDelayMin);

            // Your TotalDelayLength is in meters in AnalyzeTrip summary
            double totalDelayLenM = list.Sum(x => x.TotalDelayLength);

            double avgTravelKph = totalTravelMin > 0
                ? totalDistKm / (totalTravelMin / 60.0)
                : 0.0;

            double runMin = totalTravelMin - totalDelayMin;
            double avgRunKph = runMin > 0
                ? totalDistKm / (runMin / 60.0)
                : 0.0;

            return new AnalysisSummary
            {
                TotalTravelTimeMin = totalTravelMin,
                TotalDistanceKm = totalDistKm,
                TotalDelayMin = totalDelayMin,
                TotalDelayLength = totalDelayLenM,
                AvgTravelSpeed = avgTravelKph,
                AvgRunningSpeed = avgRunKph
            };
        }

        public List<TripRow> ReadTripCsv(string path)
        {
            using var reader = new StreamReader(path);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

            var rows = new List<TripRow>();
            var records = csv.GetRecords<dynamic>();

            foreach (var rec in records)
            {
                var d = (IDictionary<string, object>)rec;

                static string? GetStr(IDictionary<string, object> dict, string key)
                    => dict.TryGetValue(key, out var val) ? val?.ToString() : null;

                double D(string k)
                {
                    var s = GetStr(d, k);
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
                }

                double? DN(string k)
                {
                    var s = GetStr(d, k);
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
                }

                int? IN(string k)
                {
                    var s = GetStr(d, k);
                    return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
                }

                DateTime? T(string k)
                {
                    var s = GetStr(d, k);
                    if (string.IsNullOrWhiteSpace(s)) return null;
                    return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt) ? dt : null;
                }

                double? speedKph = DN("Speed") ?? DN("SSpeed");

                rows.Add(new TripRow
                {
                    SnappedLat = D("SnappedLat"),
                    SnappedLon = D("SnappedLon"),
                    secDiff = D("secDiff"),
                    distanceDiff = D("distanceDiff"),
                    Speed = speedKph,
                    CauseID = IN("CauseID"),
                    Timestamp = T("Timestamp")
                });
            }

            return rows;
        }

        private static double Finite(double v) => (double.IsNaN(v) || double.IsInfinity(v)) ? 0.0 : v;

        private static List<CpVisit> DetectCpVisits(
            List<TripRow> df,
            List<ControlPoint> cps,
            double enterRadiusM = 300.0,
            double exitRadiusM = 300.0)
        {
            var visits = new List<CpVisit>();

            string? currentCp = null;
            ControlPoint? activeCp = null;
            double bestDist = double.MaxValue;
            int bestIdx = -1;

            var nearestPerCp = new Dictionary<string, (double dist, int idx)>();

            for (int i = 0; i < df.Count; i++)
            {
                var r = df[i];

                foreach (var cp in cps)
                {
                    double d = Geo.DistanceMeters(r.SnappedLat, r.SnappedLon, cp.Lat, cp.Lng);

                    if (!nearestPerCp.ContainsKey(cp.ControlPointId) || d < nearestPerCp[cp.ControlPointId].dist)
                    {
                        nearestPerCp[cp.ControlPointId] = (d, i);
                    }

                    if (currentCp == null && d <= enterRadiusM)
                    {
                        currentCp = cp.ControlPointId;
                        activeCp = cp;
                        bestDist = d;
                        bestIdx = i;
                    }
                    else if (currentCp == cp.ControlPointId && activeCp != null)
                    {
                        if (d <= exitRadiusM)
                        {
                            if (d < bestDist)
                            {
                                bestDist = d;
                                bestIdx = i;
                            }
                        }
                        else
                        {
                            visits.Add(new CpVisit
                            {
                                CpId = currentCp,
                                Index = bestIdx,
                                Lat = df[bestIdx].SnappedLat,
                                Lon = df[bestIdx].SnappedLon
                            });

                            currentCp = null;
                            activeCp = null;
                            bestIdx = -1;
                            bestDist = double.MaxValue;
                        }
                    }
                }
            }

            if (currentCp != null && bestIdx >= 0)
            {
                visits.Add(new CpVisit
                {
                    CpId = currentCp,
                    Index = bestIdx,
                    Lat = df[bestIdx].SnappedLat,
                    Lon = df[bestIdx].SnappedLon
                });
            }

            foreach (var kv in nearestPerCp)
            {
                if (kv.Value.dist <= enterRadiusM)
                {
                    if (!visits.Any(v => v.CpId == kv.Key))
                    {
                        visits.Add(new CpVisit
                        {
                            CpId = kv.Key,
                            Index = kv.Value.idx,
                            Lat = df[kv.Value.idx].SnappedLat,
                            Lon = df[kv.Value.idx].SnappedLon
                        });
                    }
                }
            }

            return visits
                .OrderBy(v => v.Index)
                .GroupBy(v => v.CpId)
                .Select(g => g.First())
                .ToList();
        }

        private sealed class CpVisit
        {
            public string CpId { get; set; } = "";
            public int Index { get; set; }
            public double Lat { get; set; }
            public double Lon { get; set; }
        }
    }
}