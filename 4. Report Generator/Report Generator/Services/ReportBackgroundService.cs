using Report_Generator.Models;

namespace Report_Generator.Services
{
    public class ReportBackgroundService : BackgroundService
    {
        private readonly ReportJobService _jobRegistry;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ReportBackgroundService> _logger;

        public ReportBackgroundService(
            ReportJobService jobRegistry,
            IServiceScopeFactory scopeFactory,
            ILogger<ReportBackgroundService> logger)
        {
            _jobRegistry = jobRegistry;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Report background worker started.");

            await foreach (var job in _jobRegistry.JobQueue.ReadAllAsync(stoppingToken))
            {
                var originalOut = Console.Out;
                var jobWriter = new JobLogWriter(job.Id, _jobRegistry, originalOut);
                Console.SetOut(jobWriter);
                _jobRegistry.MarkRunning(job.Id);

                try
                {
                    _logger.LogInformation("Processing job {JobId}", job.Id);

                    using var scope = _scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<ReportProcessingService>();

                    var (zipBytes, zipFilename) = await processor.ProcessAsync(
                        job.Files.Cast<Microsoft.AspNetCore.Http.IFormFile>().ToList(),
                        stoppingToken);

                    _jobRegistry.MarkDone(job.Id, zipBytes, zipFilename);
                    _logger.LogInformation("Job {JobId} completed.", job.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Job {JobId} failed.", job.Id);
                    _jobRegistry.MarkFailed(job.Id, ex.Message);

                    // Write the exception message to the job log so the UI shows it
                    Console.WriteLine($"❌ Fatal error: {ex.Message}");
                }
                finally
                {
                    Console.SetOut(originalOut);
                    jobWriter.Dispose();
                }
            }

            _logger.LogInformation("Report background worker stopping.");
        }
    }
}
