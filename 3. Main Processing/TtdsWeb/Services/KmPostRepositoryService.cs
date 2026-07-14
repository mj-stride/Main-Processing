using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using TtdsWeb.Models;
using TtdsWeb.Utils;

namespace TtdsWeb.Services
{
    public class KmPostRepositoryService : IKmPostRepositoryService
    {
        private readonly AppState _state;
        private readonly IConfiguration _config;

        public KmPostRepositoryService(IAppStateAccessor appState, IConfiguration config)
        {
            _state = appState.Current;
            _config = config;
        }

        public string ResolveKmDbPath()
        {
            // 1️⃣ Base directory (works for EXE)
            var baseDir = AppContext.BaseDirectory;

            // 2️⃣ If user manually set path → use it
            if (!string.IsNullOrWhiteSpace(_state.KmDbPath) &&
                System.IO.File.Exists(_state.KmDbPath))
                return _state.KmDbPath!;

            // 3️⃣ From config (appsettings.json)
            var cfg = _config["KmPostDbPath"];
            if (!string.IsNullOrWhiteSpace(cfg))
            {
                var cfgPath = Path.IsPathRooted(cfg)
                    ? cfg
                    : Path.Combine(baseDir, cfg.Replace('/', Path.DirectorySeparatorChar));

                if (System.IO.File.Exists(cfgPath))
                    return cfgPath;
            }

            // 4️⃣ Default: Data folder beside EXE
            var path1 = Path.Combine(baseDir, "Data", "kilometer_post.db");
            if (System.IO.File.Exists(path1))
                return path1;

            // 5️⃣ Fallback: same folder as EXE
            var path2 = Path.Combine(baseDir, "kilometer_post.db");
            if (System.IO.File.Exists(path2))
                return path2;

            // ❌ Not found → clear error
            throw new FileNotFoundException(
                $"kilometer_post.db not found.\nChecked:\n{path1}\n{path2}"
            );
        }

        public List<KmPostRow> LoadKmPostsForTrip(
            List<TripRow> df,
            string? dbPath,
            string? region,
            IEnumerable<string>? roads,
            double bufferMeters = 500.0)
        {
            var list = new List<KmPostRow>();
            if (string.IsNullOrWhiteSpace(dbPath) || !System.IO.File.Exists(dbPath))
                return list;

            var (qMinLat, qMaxLat, qMinLon, qMaxLon) = ComputeBbox(df, bufferMeters);
            using var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
            con.Open();

            var cmd = con.CreateCommand();
            var sb = new StringBuilder(@"
                SELECT id, kilometerPost, latitude AS lat, longitude AS lon, regionId, roadName
                FROM tblKilometerPost
                WHERE latitude BETWEEN @minLat AND @maxLat
                  AND longitude BETWEEN @minLon AND @maxLon ");

            cmd.Parameters.AddWithValue("@minLat", qMinLat);
            cmd.Parameters.AddWithValue("@maxLat", qMaxLat);
            cmd.Parameters.AddWithValue("@minLon", qMinLon);
            cmd.Parameters.AddWithValue("@maxLon", qMaxLon);

            if (!string.IsNullOrWhiteSpace(region))
            {
                sb.Append(" AND regionId = @region ");
                cmd.Parameters.AddWithValue("@region", region);
            }

            var roadList = roads?.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList() ?? new List<string>();
            if (roadList.Count > 0)
            {
                var prm = new List<string>();
                for (int i = 0; i < roadList.Count; i++)
                {
                    var p = $"@road{i}";
                    prm.Add(p);
                    cmd.Parameters.AddWithValue(p, roadList[i]);
                }
                sb.Append(" AND roadName IN (" + string.Join(",", prm) + ") ");
            }

            sb.Append(";");
            cmd.CommandText = sb.ToString();

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var label = rdr["kilometerPost"]?.ToString() ?? "";
                double kmNum = 0.0;
                double.TryParse(label, NumberStyles.Float, CultureInfo.InvariantCulture, out kmNum);

                list.Add(new KmPostRow
                {
                    Id = rdr["id"]?.ToString() ?? "",
                    KilometerPost = label,
                    Km = kmNum,
                    Lat = double.TryParse(rdr["lat"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var la) ? la : 0.0,
                    Lon = double.TryParse(rdr["lon"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) ? lo : 0.0,
                    Region = rdr["regionId"]?.ToString(),
                    Road = rdr["roadName"]?.ToString()
                });
            }
            return list;
        }

        public List<string> GetKmRegions(string? dbPath)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(dbPath) || !System.IO.File.Exists(dbPath))
                return list;

            try
            {
                using var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
                con.Open();

                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT regionId FROM tblKilometerPost ORDER BY regionId;";

                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                    if (!string.IsNullOrWhiteSpace(rdr["regionId"]?.ToString()))
                        list.Add(rdr["regionId"]!.ToString()!);
            }
            catch { }

            return list;
        }

        public List<string> GetKmRoads(string? dbPath, string? region)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(dbPath) || !System.IO.File.Exists(dbPath))
                return list;

            try
            {
                using var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
                con.Open();

                using var cmd = con.CreateCommand();
                var sb = new StringBuilder(@"
                    SELECT DISTINCT roadName
                    FROM tblKilometerPost
                    WHERE 1=1 ");

                if (!string.IsNullOrWhiteSpace(region))
                {
                    sb.Append(" AND regionId = @region ");
                    cmd.Parameters.AddWithValue("@region", region);
                }

                sb.Append(" ORDER BY roadName;");
                cmd.CommandText = sb.ToString();

                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                    if (!string.IsNullOrWhiteSpace(rdr["roadName"]?.ToString()))
                        list.Add(rdr["roadName"]!.ToString()!);
            }
            catch { }

            return list;
        }

        /// <summary>
        /// Computes a bounding box from trip rows with optional buffer in meters
        /// </summary>
        private static (double minLat, double maxLat, double minLon, double maxLon) ComputeBbox(List<TripRow> df, double bufferMeters = 500.0)
        {
            var validRows = df
                .Where(r => !double.IsNaN(r.SnappedLat) && !double.IsNaN(r.SnappedLon)
                         && !double.IsInfinity(r.SnappedLat) && !double.IsInfinity(r.SnappedLon)
                         && Math.Abs(r.SnappedLat) <= 90 && Math.Abs(r.SnappedLon) <= 180)
                .ToList();

            if (validRows.Count == 0)
                return (0, 0, 0, 0);

            var minLat = validRows.Min(r => r.SnappedLat);
            var maxLat = validRows.Max(r => r.SnappedLat);
            var minLon = validRows.Min(r => r.SnappedLon);
            var maxLon = validRows.Max(r => r.SnappedLon);

            double latMid = (minLat + maxLat) / 2.0;
            double dLat = bufferMeters / 111320.0;
            double dLon = bufferMeters / (111320.0 * Math.Cos(latMid * Math.PI / 180.0));
            return (minLat - dLat, maxLat + dLat, minLon - dLon, maxLon + dLon);
        }

        /// <summary>
        /// Splits a comma-separated string into a list of distinct trimmed values
        /// </summary>
        public static List<string>? SplitCsv(string? csv) =>
            string.IsNullOrWhiteSpace(csv)
                ? null
                : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Distinct()
                     .ToList();
    }

    public interface IKmPostRepositoryService
    {
        string ResolveKmDbPath();
        List<KmPostRow> LoadKmPostsForTrip(List<TripRow> df, string? dbPath, string? region, IEnumerable<string>? roads, double bufferMeters = 500.0);
        List<string> GetKmRegions(string? dbPath);
        List<string> GetKmRoads(string? dbPath, string? region);
    }

    public sealed class KmPostRow
    {
        public string Id { get; set; } = "";
        public string KilometerPost { get; set; } = "";
        public double Km { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string? Region { get; set; }
        public string? Road { get; set; }
    }
}

