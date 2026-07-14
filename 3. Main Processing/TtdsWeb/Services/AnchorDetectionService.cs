using TtdsWeb.Models;
using TtdsWeb.Utils;

namespace TtdsWeb.Services
{
    public interface IAnchorDetectionService
    {
        List<ControlPoint> BuildKmAnchorsForRows(List<TripRow> df);
        List<ControlPoint> BuildKmAnchorsForTrip(List<TripRow> df, List<KmPostRow> kmPosts, double snapRadiusM = 300.0);
        List<ControlPoint> FilterAnchorsToVisited(List<TripRow> df, List<ControlPoint> anchors, double enterRadiusM = 300.0, double exitRadiusM = 300.0);
    }

    public class AnchorDetectionService : IAnchorDetectionService
    {
        private readonly AppState _state;
        private readonly IKmPostRepositoryService _kmPostRepository;
        private const double CP_DETECT_RADIUS_M = 300.0;

        public AnchorDetectionService(IAppStateAccessor appState, IKmPostRepositoryService kmPostRepository)
        {
            _state = appState.Current;
            _kmPostRepository = kmPostRepository;
        }

        public List<ControlPoint> BuildKmAnchorsForRows(List<TripRow> df)
        {
            var dbPath = _kmPostRepository.ResolveKmDbPath();
            if (!System.IO.File.Exists(dbPath)) return new List<ControlPoint>();

            IEnumerable<string>? roads = null;
            if (_state.KmRoads?.Count > 0) roads = _state.KmRoads;
            else if (!string.IsNullOrWhiteSpace(_state.KmRoad)) roads = KmPostRepositoryService.SplitCsv(_state.KmRoad);

            // bbox-based query for THIS dataset
            var kmPosts = _kmPostRepository.LoadKmPostsForTrip(df, dbPath, _state.KmRegion, roads, bufferMeters: 3000.0);
            if (kmPosts.Count < 2) return new List<ControlPoint>();

            var kmAnchors = BuildKmAnchorsForTrip(df, kmPosts);
            if (kmAnchors.Count < 2) return new List<ControlPoint>();

            // keep only those actually visited (fallback to full if too few)
            var filtered = FilterAnchorsToVisited(df, kmAnchors, CP_DETECT_RADIUS_M, CP_DETECT_RADIUS_M);
            return (filtered.Count >= 2) ? filtered : kmAnchors;
        }

        public List<ControlPoint> BuildKmAnchorsForTrip(List<TripRow> df, List<KmPostRow> kmPosts, double snapRadiusM = 300.0)
        {
            if (df == null || df.Count == 0) return new List<ControlPoint>();
            if (kmPosts == null || kmPosts.Count == 0) return new List<ControlPoint>();

            // For each KM post, find nearest index along trip
            int NearestIdx(double lat, double lon)
            {
                int bestIdx = 0;
                double best = double.MaxValue;
                for (int i = 0; i < df.Count; i++)
                {
                    var d = Geo.DistanceMeters(lat, lon, df[i].SnappedLat, df[i].SnappedLon);
                    if (d < best) { best = d; bestIdx = i; }
                }
                return bestIdx;
            }

            var candidates = new List<(KmPostRow km, int idx, double dist)>();

            foreach (var km in kmPosts)
            {
                int idx = NearestIdx(km.Lat, km.Lon);
                double dist = Geo.DistanceMeters(km.Lat, km.Lon, df[idx].SnappedLat, df[idx].SnappedLon);

                // Only keep KM posts that are actually close to the traveled polyline
                if (dist <= snapRadiusM)
                    candidates.Add((km, idx, dist));
            }

            if (candidates.Count < 2)
            {
                // If too strict, fallback: take nearest ordering anyway (no radius filter)
                candidates.Clear();
                foreach (var km in kmPosts)
                {
                    int idx = NearestIdx(km.Lat, km.Lon);
                    double dist = Geo.DistanceMeters(km.Lat, km.Lon, df[idx].SnappedLat, df[idx].SnappedLon);
                    candidates.Add((km, idx, dist));
                }
            }

            // Sort in travel order
            var ordered = candidates
                .OrderBy(x => x.idx)
                .GroupBy(x => x.km.Id) // avoid duplicates by id
                .Select(g => g.First())
                .ToList();

            // Convert to ControlPoint anchors
            var anchors = ordered.Select(x => new ControlPoint
            {
                ControlPointId = x.km.KilometerPost,  // label like "KM 12" or "12"
                Lat = x.km.Lat,
                Lng = x.km.Lon
            }).ToList();

            // If duplicate labels exist, make them unique
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in anchors)
            {
                var baseId = a.ControlPointId;
                int k = 2;
                while (!seen.Add(a.ControlPointId))
                {
                    a.ControlPointId = $"{baseId}_{k}";
                    k++;
                }
            }

            return anchors;
        }

        public List<ControlPoint> FilterAnchorsToVisited(List<TripRow> df, List<ControlPoint> anchors, double enterRadiusM = 300.0, double exitRadiusM = 300.0)
        {
            if (anchors == null || anchors.Count == 0) return new List<ControlPoint>();
            var visits = DetectCpVisits(df, anchors, enterRadiusM, exitRadiusM);
            if (visits.Count == 0) return new List<ControlPoint>();
            var set = visits.Select(v => v.CpId).ToHashSet();
            return anchors.Where(a => set.Contains(a.ControlPointId)).ToList();
        }

        private static List<CpVisit> DetectCpVisits(List<TripRow> df, List<ControlPoint> cps, double enterRadiusM = 300.0, double exitRadiusM = 300.0)
        {
            var visits = new List<CpVisit>();

            string? currentCp = null;
            ControlPoint? activeCp = null;
            double bestDist = double.MaxValue;
            int bestIdx = -1;

            // Track closest point per CP (fallback)
            var nearestPerCp = new Dictionary<string, (double dist, int idx)>();

            for (int i = 0; i < df.Count; i++)
            {
                var r = df[i];

                foreach (var cp in cps)
                {
                    double d = Geo.DistanceMeters(r.SnappedLat, r.SnappedLon, cp.Lat, cp.Lng);

                    // Always track nearest (fallback)
                    if (!nearestPerCp.ContainsKey(cp.ControlPointId) || d < nearestPerCp[cp.ControlPointId].dist)
                    {
                        nearestPerCp[cp.ControlPointId] = (d, i);
                    }

                    // ENTER
                    if (currentCp == null && d <= enterRadiusM)
                    {
                        currentCp = cp.ControlPointId;
                        activeCp = cp;
                        bestDist = d;
                        bestIdx = i;
                    }
                    // INSIDE
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
                            // EXIT -> finalize
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

            // finalize if still inside
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

            // FALLBACK: add missed CPs
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

            // remove duplicates & sort
            return visits
                .OrderBy(v => v.Index)
                .GroupBy(v => v.CpId)
                .Select(g => g.First())
                .ToList();
        }
    }

    internal class CpVisit
    {
        public string CpId { get; set; } = "";
        public int Index { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
    }
}