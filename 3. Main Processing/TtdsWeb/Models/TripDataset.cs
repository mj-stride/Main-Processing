using System.Collections.Generic;

namespace TtdsWeb.Models
{
    public class TripDataset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string FileName { get; set; } = "";
        public string Path { get; set; } = "";

        // Raw trip rows
        public List<TripRow> Rows { get; set; } = new();

        // Precomputed coords for preview on map
        public List<double[]> Coords { get; set; } = new();

    }
}
