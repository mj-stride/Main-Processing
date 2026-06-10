using System.Collections.Generic;

namespace TtdsWeb.Models
{
    public class MultiMapViewModel
    {
        public List<Item> Items { get; set; } = new();

        public class Item
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public List<double[]> Coords { get; set; } = new();

            public string? Direction { get; set; }
            public string? PeakCode { get; set; }
            public string? PeakLabel { get; set; }
        }
    }
}
