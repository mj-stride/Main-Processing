using System.Collections.Generic;

namespace TtdsWeb.Utils
{
    public static class DelayConstants
    {
        public static readonly IReadOnlyDictionary<int, (string Label, string Color)> CauseMap =
            new Dictionary<int, (string Label, string Color)>
            {
                { 1, ("Loading and Unloading", "pink") },
                { 2, ("Intersection", "orange") },
                { 3, ("Traffic Light", "red") },
                { 4, ("Pedestrian Crossing", "purple") },
                { 5, ("Animal Crossing", "brown") },
                { 6, ("Vehicle Crossing", "maroon") },
                { 7, ("Road Construction", "gray") },
                { 8, ("Blocked by Vehicle", "black") }
            };

        public static bool IsValidCauseId(int id) => CauseMap.ContainsKey(id);

        public static (string Label, string Color)? GetCause(int id) =>
            CauseMap.TryGetValue(id, out var cause) ? cause : null;

        public static string? GetCauseLabel(int id) =>
            CauseMap.TryGetValue(id, out var cause) ? cause.Label : null;
    }
}