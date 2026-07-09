// TtdsWeb/Services/AppState.cs
using System.Collections.Concurrent;
using TtdsWeb.Models;
using TtdsWeb.Services;

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

public class AppStateStore
{
    private class Entry
    {
        public AppState State { get; } = new();
        public DateTime LastAccessedUtc = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public AppState GetOrCreate(string sessionId)
    {
        var entry = _entries.GetOrAdd(sessionId, _ => new Entry());
        entry.LastAccessedUtc = DateTime.UtcNow;
        return entry.State;
    }

    // called periodically by the cleanup service below
    public void RemoveExpired(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var kv in _entries)
            if (kv.Value.LastAccessedUtc < cutoff)
                _entries.TryRemove(kv.Key, out _);
    }
}

// Services/AppStateAccessor.cs
public interface IAppStateAccessor
{
    AppState Current { get; }
}

public class AppStateAccessor : IAppStateAccessor
{
    private readonly Lazy<AppState> _current;

    public AppStateAccessor(IHttpContextAccessor httpContextAccessor, AppStateStore store)
    {
        _current = new Lazy<AppState>(() =>
        {
            var ctx = httpContextAccessor.HttpContext!;

            // Session.Id is only reliably populated once something has been
            // written — force it so the ID exists (and the cookie gets set)
            // on the very first request, not the second.
            if (!ctx.Session.Keys.Contains("_ttds_seeded"))
                ctx.Session.SetString("_ttds_seeded", "1");

            return store.GetOrCreate(ctx.Session.Id);
        });
    }

    public AppState Current => _current.Value;
}

public class AppStateCleanupService : BackgroundService
{
    private readonly AppStateStore _store;
    public AppStateCleanupService(AppStateStore store) => _store = store;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _store.RemoveExpired(TimeSpan.FromHours(4));
            await Task.Delay(TimeSpan.FromMinutes(15), ct);
        }
    }
}