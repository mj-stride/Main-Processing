// TtdsWeb/Services/AppState.cs
using TtdsWeb.Models;

namespace TtdsWeb.Services
{
    public class AppState
    {
        // Manual CPs for CP mode
        public List<ControlPoint> ManualCpPoints { get; } = new();

        // Manual CPs for KM mode (separate!)
        public List<ControlPoint> ManualKmPoints { get; } = new();

        // Generated CPs from KM selection
        public List<ControlPoint> KmGeneratedPoints { get; } = new();
        public string? UploadFolder { get; init; }  // keep as init-only if you want
        public List<TripDataset> Datasets { get; } = new();
        public string? LastTripPath { get; set; }

        // Anchor mode
        public string? AnchorSource { get; set; } = "cp"; // "cp" or "km"

        // KM filter state
        public string? KmDbPath { get; set; }
        public string? KmRegion { get; set; }

        // old single-string support
        public string? KmRoad { get; set; }

        // ✅ multi-select roads
        public List<string> KmRoads { get; set; } = new();
        public List<ControlPoint> ManualCpKm { get; set; } = new();
        // CP list
        public List<ControlPoint> ControlPoints { get; } = new();
    }
}