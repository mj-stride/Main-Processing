using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using NetTopologySuite.LinearReferencing;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using Report_Generator.Models;

namespace Report_Generator.Services
{
    public class SpeedSegmentService
    {
        private readonly GeometryFactory _geomFactory = new GeometryFactory(new PrecisionModel(), 4326);
        private readonly CoordinateTransformationFactory _ctFactory = new CoordinateTransformationFactory();
        private readonly MathTransform _toWebMercator;
        private readonly MathTransform _toWgs84;

        public SpeedSegmentService()
        {
            var toWm = _ctFactory.CreateFromCoordinateSystems(GeographicCoordinateSystem.WGS84, ProjectedCoordinateSystem.WebMercator);
            _toWebMercator = ((CoordinateTransformation)toWm).MathTransform;

            var toGeo = _ctFactory.CreateFromCoordinateSystems(ProjectedCoordinateSystem.WebMercator, GeographicCoordinateSystem.WGS84);
            _toWgs84 = ((CoordinateTransformation)toGeo).MathTransform;
        }

        private static string NormCp(string? x) => (x ?? "").Trim();

        // speed_bin_color.
        public static (string Cat, string Color) SpeedBinColor(double? speed)
        {
            if (speed == null || double.IsNaN(speed.Value)) return ("No speed", "gray");
            double s = speed.Value;
            if (s <= 5) return ("0–5 kph", "red");
            if (s <= 15) return ("6–15 kph", "orange");
            if (s <= 30) return ("16–30 kph", "yellow");
            if (s <= 45) return ("31–45 kph", "green");
            return ("46+ kph", "blue");
        }

        // speed_width.
        public static float SpeedWidth(double? speed)
        {
            if (speed == null || double.IsNaN(speed.Value)) return 6;
            double s = speed.Value;
            if (s <= 5) return 12;
            if (s <= 15) return 10;
            if (s <= 30) return 8;
            if (s <= 45) return 7;
            return 6;
        }

        // build_speed_lookup: (From, To) -> TravelSpeedKph.
        public Dictionary<(string From, string To), double?> BuildSpeedLookup(IEnumerable<SegmentAverages> segments)
        {
            var lookup = new Dictionary<(string, string), double?>();
            foreach (var seg in segments)
            {
                string f = NormCp(seg.From);
                string t = NormCp(seg.To);
                if (f.Length > 0 && t.Length > 0)
                {
                    // Store both directions just in case SegmentAverages flipped them
                    lookup[(f, t)] = seg.TravelSpeedKph;
                    lookup[(t, f)] = seg.TravelSpeedKph;
                }
            }
            return lookup;
        }

        private Geometry ToWebMercator(Geometry geom) => Transform(geom, _toWebMercator);
        private Geometry ToWgs84(Geometry geom) => Transform(geom, _toWgs84);

        private static Geometry Transform(Geometry geom, MathTransform t)
        {
            var result = geom.Copy();
            result.Apply(new TransformFilter(t));
            return result;
        }

        private class TransformFilter : ICoordinateSequenceFilter
        {
            private readonly MathTransform _t;
            public TransformFilter(MathTransform t) => _t = t;
            public bool Done => false;
            public bool GeometryChanged => true;
            public void Filter(CoordinateSequence seq, int i)
            {
                double[] p = _t.Transform(new[] { seq.GetX(i), seq.GetY(i) });
                seq.SetX(i, p[0]);
                seq.SetY(i, p[1]);
            }
        }

        public List<TripCp2CpSegment> MakeTripCp2CpSegments(
            LineString tripWgs84,
            List<ControlPoint> controlPoints,
            Dictionary<(string, string), double?> speedLookup)
        {
            var result = new List<TripCp2CpSegment>();
            if (tripWgs84 == null || tripWgs84.IsEmpty || controlPoints == null || controlPoints.Count < 2)
                return result;

            var trip3857 = (LineString)ToWebMercator(tripWgs84);
            var indexed = new LengthIndexedLine(trip3857);

            var projected = controlPoints
                .Select(cp =>
                {
                    var pt3857 = ToWebMercator(_geomFactory.CreatePoint(new Coordinate(cp.Longitude, cp.Latitude)));
                    return new { cp.Name, Distance = indexed.Project(pt3857.Coordinate) };
                })
                .OrderBy(x => x.Distance)
                .ToList();

            for (int i = 0; i < projected.Count - 1; i++)
            {
                var a = projected[i];
                var b = projected[i + 1];
                if (b.Distance <= a.Distance) continue;

                string fromCp = NormCp(a.Name);
                string toCp = NormCp(b.Name);

                // FIX 2: The Fallback Logic
                double? speed = null;

                // 1. Try exact match
                if (speedLookup.TryGetValue((fromCp, toCp), out var exactSpeed))
                {
                    speed = exactSpeed;
                }
                else
                {
                    // 2. Fallback: If CPs swapped or skipped, find any segment starting with FromCP
                    var fallbackFrom = speedLookup.FirstOrDefault(k => k.Key.Item1 == fromCp);
                    if (fallbackFrom.Key.Item1 != null)
                    {
                        speed = fallbackFrom.Value;
                    }
                    else
                    {
                        // 3. Fallback: Find any segment ending with ToCP
                        var fallbackTo = speedLookup.FirstOrDefault(k => k.Key.Item2 == toCp);
                        if (fallbackTo.Key.Item2 != null)
                        {
                            speed = fallbackTo.Value;
                        }
                    }
                }

                var (cat, color) = SpeedBinColor(speed);
                float lw = SpeedWidth(speed);

                var sub3857 = (LineString)indexed.ExtractLine(a.Distance, b.Distance);
                var sub4326 = (LineString)ToWgs84(sub3857);

                result.Add(new TripCp2CpSegment
                {
                    FromCP = fromCp,
                    ToCP = toCp,
                    SpeedKph = speed,
                    SpeedCat = cat,
                    ColorName = color,
                    LineWidth = lw,
                    Geometry = sub4326
                });
            }

            return result;
        }
    }
}
