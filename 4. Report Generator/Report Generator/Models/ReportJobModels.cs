using Microsoft.AspNetCore.Http;

namespace Report_Generator.Models
{
    // -----------------------------------------------------------------------
    // Job state machine
    // -----------------------------------------------------------------------
    public enum JobState { Queued, Running, Done, Failed }

    public class ReportJobStatus
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public JobState State { get; set; } = JobState.Queued;
        public List<string> Logs { get; } = new();   // append-only; lock(Logs) before touching
        public byte[]? ResultZip { get; set; }
        public string? ZipFilename { get; set; }
        public string? Error { get; set; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
    }

    // -----------------------------------------------------------------------
    // Internal queue item — holds buffered file content so the background
    // service can read it after the original HTTP request has ended.
    // -----------------------------------------------------------------------
    public class QueuedReportJob
    {
        public Guid Id { get; init; }
        public List<InMemoryFormFile> Files { get; init; } = new();
    }

    // -----------------------------------------------------------------------
    // IFormFile backed by a byte[] — lets existing services call
    // file.OpenReadStream() / file.FileName / file.CopyToAsync() unchanged.
    // -----------------------------------------------------------------------
    public sealed class InMemoryFormFile : IFormFile
    {
        private readonly byte[] _data;

        public InMemoryFormFile(string fileName, byte[] data)
        {
            FileName = fileName;
            _data = data;
        }

        public string ContentType => "application/octet-stream";
        public string ContentDisposition => $"form-data; name=\"files\"; filename=\"{Path.GetFileName(FileName)}\"";
        public IHeaderDictionary Headers => new HeaderDictionary();
        public long Length => _data.Length;
        public string Name => "files";
        public string FileName { get; }

        public Stream OpenReadStream() => new MemoryStream(_data, writable: false);
        public void CopyTo(Stream target) => OpenReadStream().CopyTo(target);
        public Task CopyToAsync(Stream target, CancellationToken ct = default) =>
            OpenReadStream().CopyToAsync(target, ct);
    }
}
