using Microsoft.AspNetCore.Mvc;
using Report_Generator.Models;
using Report_Generator.Services;

namespace Report_Generator.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ReportController : ControllerBase
    {
        private readonly ReportJobService _jobService;

        public ReportController(ReportJobService jobService)
        {
            _jobService = jobService;
        }

        // ------------------------------------------------------------------
        // POST /api/Report/submit
        // Buffers all uploaded files into memory, enqueues the job, and
        // returns the jobId immediately — no blocking processing here.
        // ------------------------------------------------------------------
        [HttpPost("submit")]
        [DisableRequestSizeLimit]
        [RequestFormLimits(
            ValueLengthLimit = int.MaxValue,
            MultipartBodyLengthLimit = long.MaxValue,
            ValueCountLimit = int.MaxValue)]
        public async Task<IActionResult> SubmitAsync([FromForm] List<IFormFile> files)
        {
            if (files == null || !files.Any())
                return BadRequest(new { error = "No files uploaded." });

            var scope = HttpContext.RequestServices;
            var zipExtractor = scope.GetRequiredService<ZipExtractService>();

            var buffered = await zipExtractor.ExpandUploadsAsync(files);

            var jobId = await _jobService.EnqueueAsync(buffered, HttpContext.RequestAborted);
            return Ok(new { jobId });
        }

        // ------------------------------------------------------------------
        // GET /api/Report/status/{jobId}
        // Returns current state + all log lines accumulated so far.
        // The UI polls this every ~1.5 s until state is "Done" or "Failed".
        // ------------------------------------------------------------------
        [HttpGet("status/{jobId:guid}")]
        public IActionResult GetStatus(Guid jobId)
        {
            var job = _jobService.Get(jobId);
            if (job == null) return NotFound(new { error = "Job not found." });

            List<string> logs;
            lock (job.Logs) logs = job.Logs.ToList();

            return Ok(new
            {
                state = job.State.ToString(),   // "Queued" | "Running" | "Done" | "Failed"
                logs,
                error = job.Error
            });
        }

        // ------------------------------------------------------------------
        // GET /api/Report/download/{jobId}
        // Streams the completed ZIP. The UI only calls this once state="Done".
        // ------------------------------------------------------------------
        [HttpGet("download/{jobId:guid}")]
        public IActionResult Download(Guid jobId)
        {
            var job = _jobService.Get(jobId);
            if (job == null) return NotFound(new { error = "Job not found." });
            if (job.State != JobState.Done || job.ResultZip == null)
                return BadRequest(new { error = "Job not complete yet." });

            return File(job.ResultZip, "application/zip", job.ZipFilename ?? "Reports.zip");
        }
    }
}
