using Ttds.Shared;
using TtdsWeb.Models;

namespace TtdsWeb.Services
{
    public interface IPeakPeriodService
    {
        PeakPeriod GetPeakPeriod(DateTime dt);
        string PeakLabel(PeakPeriod p);
        PeakPeriod ComputeDatasetPeak(List<TripRow> rows);
        string PeakFolder(string? peakCode);
        string FormatMinToHHMMSS(double minutes);
        string FullDirName(string code);
        double Round2(double v);
    }

    public enum PeakPeriod
    {
        AM,
        MID,
        PM,
        OFF
    }

    public class PeakPeriodService : IPeakPeriodService
    {
        public PeakPeriod GetPeakPeriod(DateTime dt)
        {
            var t = dt.TimeOfDay;

            // AM peak: 06:00 onwards (interpret as 06:00–10:29:59)
            var amStart = new TimeSpan(6, 0, 0);
            var amEnd = new TimeSpan(10, 29, 59);

            // MID peak: 10:30–15:00
            var midStart = new TimeSpan(10, 30, 0);
            var midEnd = new TimeSpan(15, 0, 0);

            // PM peak: 15:30–19:30
            var pmStart = new TimeSpan(15, 30, 0);
            var pmEnd = new TimeSpan(19, 30, 0);

            if (t >= amStart && t <= amEnd) return PeakPeriod.AM;
            if (t >= midStart && t <= midEnd) return PeakPeriod.MID;
            if (t >= pmStart && t <= pmEnd) return PeakPeriod.PM;

            return PeakPeriod.OFF;
        }

        public string PeakLabel(PeakPeriod p) => p switch
        {
            PeakPeriod.AM => "AM Peak (07:00–10:00)",
            PeakPeriod.MID => "Mid Peak (11:00–14:00)",
            PeakPeriod.PM => "PM Peak (16:00–19:00)",
            _ => "Off-Peak"
        };

        public PeakPeriod ComputeDatasetPeak(List<TripRow> rows)
        {
            var t = rows.Select(r => r.Timestamp).FirstOrDefault(x => x.HasValue);
            if (!t.HasValue) return PeakPeriod.OFF;
            return GetPeakPeriod(t.Value);
        }

        public string PeakFolder(string? peakCode)
        {
            peakCode = (peakCode ?? "").Trim().ToUpperInvariant();
            return peakCode switch
            {
                "AM" => "AM",
                "MID" => "MID",
                "PM" => "PM",
                _ => "OFF"
            };
        }

        public string FormatMinToHHMMSS(double minutes)
        {
            if (minutes <= 0) return "00:00:00";
            var ts = TimeSpan.FromMinutes(minutes);
            return ts.ToString(@"hh\:mm\:ss");
        }

        public string FullDirName(string code) => code switch
        {
            "SB" => "Southbound",
            "NB" => "Northbound",
            "EB" => "Eastbound",
            "WB" => "Westbound",
            _ => "Unknown"
        };

        public double Round2(double v) => Math.Round(v, 2);
    }
}