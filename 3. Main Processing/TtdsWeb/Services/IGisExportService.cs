using System.Collections.Generic;
using TtdsWeb.Models;

namespace TtdsWeb.Services
{
    public interface IGisExportService
    {
        string WriteDelayLinesShapeFile(TripDataset dataset, string outFolder, string baseNameNoExt);
        string WriteTripPointsShapeFile(TripDataset dataset, string outFolder, string baseNameNoExt);
        byte[] BuildAnchorsGeoJson(IEnumerable<ControlPoint> anchors);
    }
}