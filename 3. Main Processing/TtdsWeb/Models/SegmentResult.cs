using System;

namespace TtdsWeb.Models
{
    public class SegmentResult
    {
        public int SegmentNo { get; set; }

        public string? From { get; set; }
        public string? To { get; set; }

        // You are assigning formatted strings in the controller
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }

        public string? Note { get; set; }

        // Keep these if you use them elsewhere
        public double? DistanceKm { get; set; }
        public double? DistanceM { get; set; }

        public double? TravelTimeSec { get; set; }
        public double? TravelTimeMin { get; set; }

        public double? TravelSpeedKph { get; set; }
        public double? RunningSpeedKph { get; set; }

        // Your controller uses r.Delays ?? 0 and r.DelayLengthM ?? 0
        public double? Delays { get; set; }
        public double? DelayLengthM { get; set; }

        // You are building a comma-separated string (causesOut)
        public string? DelayCauses { get; set; }
    }
}
