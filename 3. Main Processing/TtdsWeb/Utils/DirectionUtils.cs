using System;
using System.Collections.Generic;
using System.Linq;

public static class DirectionUtils
{
    // Bearing from point A to B (degrees 0..360, 0=N)
    public static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        double rlat1 = Deg2Rad(lat1), rlat2 = Deg2Rad(lat2);
        double dLon = Deg2Rad(lon2 - lon1);
        double y = Math.Sin(dLon) * Math.Cos(rlat2);
        double x = Math.Cos(rlat1) * Math.Sin(rlat2) -
                   Math.Sin(rlat1) * Math.Cos(rlat2) * Math.Cos(dLon);
        double brng = Math.Atan2(y, x); // radians
        brng = Rad2Deg(brng);
        if (brng < 0) brng += 360.0;
        return brng;
    }

    // Circular (vector) mean of headings, optionally distance-weighted
    public static double CircularMeanDegrees(IEnumerable<(double headingDeg, double weight)> samples)
    {
        double sumSin = 0, sumCos = 0;
        foreach (var (h, w) in samples)
        {
            double r = Deg2Rad(h);
            sumSin += Math.Sin(r) * w;
            sumCos += Math.Cos(r) * w;
        }
        if (Math.Abs(sumSin) < 1e-12 && Math.Abs(sumCos) < 1e-12) return 0;
        double mean = Math.Atan2(sumSin, sumCos);
        mean = Rad2Deg(mean);
        if (mean < 0) mean += 360.0;
        return mean;
    }

    // Map a heading to NB/EB/SB/WB
    public static string HeadingToNESW(double headingDeg)
    {
        double h = (headingDeg % 360.0 + 360.0) % 360.0;
        if (h >= 315 || h < 45) return "NB";
        if (h >= 45 && h < 135) return "EB";
        if (h >= 135 && h < 225) return "SB";
        return "WB"; // 225..315
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
    private static double Rad2Deg(double r) => r * 180.0 / Math.PI;
}
