using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using PrivateTransportCleaning.Models;

namespace PrivateTransportCleaning.Services
{
    public class GpxParserService
    {
        private readonly XNamespace ns = "http://www.topografix.com/GPX/1/1";

        public List<GpxPoint> Parse(string filePath)
        {
            var doc = XDocument.Load(filePath);
            var points = new List<GpxPoint>();

            foreach (var trkpt in doc.Descendants(ns + "trkpt"))
            {
                double lat = double.Parse(trkpt.Attribute("lat")?.Value ?? "0", CultureInfo.InvariantCulture);
                double lon = double.Parse(trkpt.Attribute("lon")?.Value ?? "0", CultureInfo.InvariantCulture);

                var timeEl = trkpt.Element(ns + "time");
                if (timeEl == null || string.IsNullOrWhiteSpace(timeEl.Value))
                    continue;

                DateTime timestamp = DateTime.Parse(timeEl.Value.Replace("Z", ""));

                var speedEl = trkpt.Element(ns + "speed");
                double speed = 0;
                if (speedEl != null && double.TryParse(speedEl.Value, out var s))
                    speed = s;

                if (speed == 0)
                    continue;

                points.Add(new GpxPoint
                {
                    Latitude = lat,
                    Longitude = lon,
                    Timestamp = timestamp,
                    Speed = speed,

                    DeviceID = GetText(trkpt, "deviceId"),
                    TrackingID = GetText(trkpt, "trackingId"),
                    UserID = GetText(trkpt, "userId"),
                    ModeID = GetText(trkpt, "modeId"),
                    CauseID = GetText(trkpt, "causeId"),
                    KilometerPostID = GetText(trkpt, "kilometerPostId"),
                    FilePath = GetText(trkpt, "filePath"),
                    DistrictID = GetText(trkpt, "districtId")
                });
            }

            return points;
        }

        private string? GetText(XElement parent, string tag)
        {
            var el = parent.Element(ns + tag);
            return el?.Value?.Trim();
        }
    }
}