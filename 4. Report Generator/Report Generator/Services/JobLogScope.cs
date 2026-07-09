public static class JobLogScope
{
    private static readonly AsyncLocal<Guid?> _current = new();
    public static Guid? CurrentJobId => _current.Value;

    public static IDisposable Push(Guid jobId)
    {
        var previous = _current.Value;
        _current.Value = jobId;
        return new Popper(previous);
    }

    private sealed class Popper : IDisposable
    {
        private readonly Guid? _previous;
        public Popper(Guid? previous) => _previous = previous;
        public void Dispose() => _current.Value = _previous;
    }
}