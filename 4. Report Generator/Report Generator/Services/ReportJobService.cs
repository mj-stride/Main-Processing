using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Report_Generator.Models;

namespace Report_Generator.Services
{
    public class ReportJobService
    {
        private readonly ConcurrentDictionary<Guid, ReportJobStatus> _jobs = new();

        // Bounded: if 20 jobs are already queued we slow down new submissions
        private readonly Channel<QueuedReportJob> _queue =
            Channel.CreateBounded<QueuedReportJob>(
                new BoundedChannelOptions(20) { FullMode = BoundedChannelFullMode.Wait });

        public ChannelReader<QueuedReportJob> JobQueue => _queue.Reader;

        // ---- Write side (controller) ----------------------------------------

        public async Task<Guid> EnqueueAsync(List<InMemoryFormFile> files, CancellationToken ct = default)
        {
            CleanupOldJobs(TimeSpan.FromHours(1));

            var status = new ReportJobStatus { Id = Guid.NewGuid() };
            _jobs[status.Id] = status;
            await _queue.Writer.WriteAsync(new QueuedReportJob { Id = status.Id, Files = files }, ct);
            return status.Id;
        }

        // ---- Read side (background service) ------------------------------------

        public ReportJobStatus? Get(Guid id) =>
            _jobs.TryGetValue(id, out var s) ? s : null;

        public void AppendLog(Guid id, string line)
        {
            if (!_jobs.TryGetValue(id, out var s)) return;
            lock (s.Logs) s.Logs.Add(line);
        }

        public void MarkRunning(Guid id)
        {
            if (_jobs.TryGetValue(id, out var s)) s.State = JobState.Running;
        }

        public void MarkDone(Guid id, byte[] zip, string filename)
        {
            if (!_jobs.TryGetValue(id, out var s)) return;
            s.ResultZip = zip;
            s.ZipFilename = filename;
            s.State = JobState.Done;
            s.CompletedAt = DateTime.UtcNow;
        }

        public void MarkFailed(Guid id, string error)
        {
            if (!_jobs.TryGetValue(id, out var s)) return;
            s.Error = error;
            s.State = JobState.Failed;
            s.CompletedAt = DateTime.UtcNow;
        }

        private void CleanupOldJobs(TimeSpan maxAge)
        {
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var kv in _jobs.ToList())
            {
                if (kv.Value.CompletedAt.HasValue && kv.Value.CompletedAt < cutoff)
                    _jobs.TryRemove(kv.Key, out _);
            }
        }
    }

    public sealed class JobLogWriter : TextWriter
    {
        private readonly Guid _jobId;
        private readonly ReportJobService _jobs;
        private readonly TextWriter _original;
        private readonly StringBuilder _buf = new();

        public override Encoding Encoding => Encoding.UTF8;

        public JobLogWriter(Guid jobId, ReportJobService jobs, TextWriter original)
        {
            _jobId = jobId;
            _jobs = jobs;
            _original = original;
        }

        // Capture character-by-character writes (used by Console.Write(char))
        public override void Write(char value)
        {
            _original.Write(value);
            if (value == '\n')
            {
                Flush();
            }
            else
            {
                _buf.Append(value);
            }
        }

        // Capture whole-line writes (used by Console.WriteLine(string))
        public override void WriteLine(string? value)
        {
            _original.WriteLine(value);
            _jobs.AppendLog(_jobId, value ?? "");
            _buf.Clear();
        }

        public override void Flush()
        {
            var line = _buf.ToString().TrimEnd('\r');
            _buf.Clear();
            if (line.Length > 0) _jobs.AppendLog(_jobId, line);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Flush();
            base.Dispose(disposing);
        }
    }
}
