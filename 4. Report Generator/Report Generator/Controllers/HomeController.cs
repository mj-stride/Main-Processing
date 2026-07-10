using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Report_Generator.Models;
using Report_Generator.Services;
using Ttds.Shared;

namespace Report_Generator.Controllers
{
    public class HomeController : Controller
    {
        private readonly ServiceOptions _services;
        private readonly ReportJobService _jobs;

        public HomeController(IOptions<ServiceOptions> options, ReportJobService jobs)
        {
            _services = options.Value;
            _jobs = jobs;
        }

        [HttpGet("/import/{batchId}")]
        public async Task<IActionResult> ImportBatch(string batchId, [FromServices] ReportJobService jobs)
        {
            var srcDir = Path.Combine(_services.BatchStorageRoot, batchId, "ttdsweb");
            if (!Directory.Exists(srcDir))
                return NotFound($"Batch {batchId} not found or expired.");

            var files = Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    var relativePath = Path.GetRelativePath(srcDir, path).Replace('\\', '/');
                    var bytes = System.IO.File.ReadAllBytes(path);
                    return new InMemoryFormFile(relativePath, bytes);
                })
                .ToList();

            if (!files.Any())
                return BadRequest("No files found in batch.");

            var jobId = await jobs.EnqueueAsync(files);
            return RedirectToAction("ReportGenerator", new { jobId }); // page can auto-poll this jobId
        }

        // GET /Home/ReportGenerator  (also GET / via the default route in Program.cs)
        [HttpGet]
        public IActionResult ReportGenerator()
        {
            return View();
        }

        public IActionResult GoToDashboard()
        {
            return Redirect(_services.Dashboard);
        }
    }
}