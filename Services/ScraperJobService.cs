using System.Collections.Concurrent;
using CSharpScraper.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSharpScraper.Services;

public class ScraperJobService
{
    private readonly ILogger<ScraperJobService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<Guid, (ScrapeJob Job, CancellationTokenSource Cts)> _jobs = new();

    public ScraperJobService(
        ILogger<ScraperJobService> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public ScrapeJob StartJob(ScrapeRequest request)
    {
        var job = new ScrapeJob
        {
            Url = request.Url,
            Goal = request.Goal,
            MaxSteps = request.MaxSteps,
            Status = JobStatus.Queued,
            LastAction = "Queueing job...",
            StartedAt = DateTime.UtcNow
        };

        var cts = new CancellationTokenSource();
        _jobs[job.JobId] = (job, cts);

        _logger.LogInformation("Job {JobId} enqueued for URL: {Url}", job.JobId, job.Url);

        // Run the background work asynchronously
        _ = Task.Run(async () =>
        {
            using var scope = _serviceProvider.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<ScraperRunner>();
            try
            {
                await runner.RunJobAsync(job, request, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in background execution for Job {JobId}", job.JobId);
                job.Status = JobStatus.Failed;
                job.Error = ex.Message;
                job.CompletedAt = DateTime.UtcNow;
            }
        });

        return job;
    }

    public ScrapeJob? GetJob(Guid jobId)
    {
        return _jobs.TryGetValue(jobId, out var pair) ? pair.Job : null;
    }

    public bool StopJob(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var pair))
        {
            if (pair.Job.Status == JobStatus.Running || pair.Job.Status == JobStatus.Queued)
            {
                _logger.LogInformation("Cancelling execution for Job {JobId}", jobId);
                pair.Cts.Cancel();
                pair.Job.Status = JobStatus.Stopped;
                pair.Job.CompletedAt = DateTime.UtcNow;
                pair.Job.LastAction = "Stopped by user request.";
                return true;
            }
        }
        return false;
    }

    public List<ScrapeStepLog>? GetJobLogs(Guid jobId)
    {
        return _jobs.TryGetValue(jobId, out var pair) ? pair.Job.Steps : null;
    }

    public object? GetJobResult(Guid jobId)
    {
        return _jobs.TryGetValue(jobId, out var pair) ? pair.Job.ExtractedData : null;
    }
}
