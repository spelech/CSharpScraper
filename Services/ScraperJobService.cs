using System.Collections.Concurrent;
using CSharpScraper.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSharpScraper.Services;

public class ScraperJobService
{
    private readonly ILogger<ScraperJobService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly LlmClient _llmClient;
    private readonly SearxngClient _searxngClient;
    private readonly ConcurrentDictionary<Guid, (ScrapeJob Job, CancellationTokenSource Cts)> _jobs = new();
    private readonly ConcurrentDictionary<Guid, (string Goal, List<Guid> JobIds)> _compareGroups = new();

    public ScraperJobService(
        ILogger<ScraperJobService> logger,
        IServiceProvider serviceProvider,
        LlmClient llmClient,
        SearxngClient searxngClient)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _llmClient = llmClient;
        _searxngClient = searxngClient;
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

    public (Guid CompareId, List<ScrapeJob> Jobs) StartCompareJobs(ScrapeCompareRequest request)
    {
        var compareId = Guid.NewGuid();
        var jobIds = new List<Guid>();
        var jobs = new List<ScrapeJob>();

        _logger.LogInformation("Starting compare group {CompareId} with {Count} URLs.", compareId, request.Urls.Count);

        foreach (var url in request.Urls)
        {
            var singleRequest = new ScrapeRequest
            {
                Url = url,
                Goal = request.Goal,
                Model = request.Model,
                BaseUrl = request.BaseUrl,
                ApiKey = request.ApiKey,
                MaxSteps = request.MaxSteps,
                DriverType = request.DriverType,
                AgentType = request.AgentType
            };

            var job = StartJob(singleRequest);
            jobIds.Add(job.JobId);
            jobs.Add(job);
        }

        _compareGroups[compareId] = (request.Goal, jobIds);
        return (compareId, jobs);
    }

    public (string Goal, List<ScrapeJob> Jobs)? GetCompareGroup(Guid compareId)
    {
        if (!_compareGroups.TryGetValue(compareId, out var groupInfo))
        {
            return null;
        }

        var jobs = new List<ScrapeJob>();
        foreach (var jobId in groupInfo.JobIds)
        {
            var job = GetJob(jobId);
            if (job != null)
            {
                jobs.Add(job);
            }
        }

        return (groupInfo.Goal, jobs);
    }

    public async Task<(Guid CompareId, List<ScrapeJob> Jobs)> StartDiscoveryCompareJobsAsync(ScrapeDiscoverCompareRequest request)
    {
        var compareId = Guid.NewGuid();
        var jobIds = new List<Guid>();
        var jobs = new List<ScrapeJob>();

        _logger.LogInformation("Processing discovery compare request {CompareId} for query '{Query}' near '{Location}'", compareId, request.Query, request.Location);

        // 1. LLM routing decision
        var systemPrompt = @"You are a product search and domain router agent. Your job is to analyze a user's product query and location, and determine:
1. An optimized, concise search query for a search engine (e.g. removing stop words, keeping brand and volume/details).
2. A list of 2 to 4 highly relevant retail or grocery store domain names where this item can be purchased in person (e.g. 'target.com', 'meijer.com', 'walmart.com', 'jewelosco.com', 'bestbuy.com', 'homedepot.com', 'walgreens.com', 'cvs.com').
   - For food/grocery items, prefer grocery domains (meijer.com, jewelosco.com, target.com, walmart.com).
   - For electronics, prefer tech retailers (bestbuy.com, target.com, walmart.com).
   - For tools/home improvement, prefer home stores (homedepot.com, lowes.com, walmart.com).
   - For health/pharmacy, prefer pharmacy stores (walgreens.com, cvs.com, target.com).

Provide your output in STRICT JSON format matching this schema:
{
  ""optimizedSearchQuery"": ""Search query string"",
  ""targetDomains"": [""domain1.com"", ""domain2.com""]
}";

        var userPrompt = $"Product Query: {request.Query}\nLocation: {request.Location ?? "None"}";
        var model = request.Model ?? "gemini-3.5-flash";

        DiscoveryRouting routing;
        try
        {
            var llmResponse = await _llmClient.GetCompletionAsync(systemPrompt, userPrompt, model, request.BaseUrl, request.ApiKey, forceJson: true);
            var cleanJson = ExtractJsonBlock(llmResponse.Content);
            routing = System.Text.Json.JsonSerializer.Deserialize<DiscoveryRouting>(cleanJson, new System.Text.Json.JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            }) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get LLM discovery routing. Falling back to defaults.");
            routing = new DiscoveryRouting
            {
                OptimizedSearchQuery = request.Query,
                TargetDomains = new List<string> { "target.com", "walmart.com" }
            };
        }

        if (string.IsNullOrEmpty(routing.OptimizedSearchQuery))
        {
            routing.OptimizedSearchQuery = request.Query;
        }
        if (routing.TargetDomains == null || routing.TargetDomains.Count == 0)
        {
            routing.TargetDomains = new List<string> { "target.com", "walmart.com" };
        }

        _logger.LogInformation("Routing decision for compare {CompareId}: Query='{OptimizedQuery}', Domains=[{Domains}]", 
            compareId, routing.OptimizedSearchQuery, string.Join(", ", routing.TargetDomains));

        // 2. SearXNG Discovery
        var discoveredUrls = await _searxngClient.SearchProductUrlsAsync(routing.OptimizedSearchQuery, routing.TargetDomains);

        if (discoveredUrls.Count == 0)
        {
            _logger.LogWarning("No URLs discovered for compare {CompareId}.", compareId);
            _compareGroups[compareId] = (request.Query, jobIds);
            return (compareId, jobs);
        }

        // 3. Construct Goal & Start Jobs in Parallel
        var goalBuilder = new System.Text.StringBuilder("Find and extract the product name, price, and store availability");
        if (!string.IsNullOrWhiteSpace(request.Location))
        {
            goalBuilder.Append($" for {request.Location} store");
        }
        goalBuilder.Append(".");
        var constructedGoal = goalBuilder.ToString();

        foreach (var entry in discoveredUrls)
        {
            var singleRequest = new ScrapeRequest
            {
                Url = entry.Value,
                Goal = constructedGoal,
                Model = request.Model,
                BaseUrl = request.BaseUrl,
                ApiKey = request.ApiKey,
                MaxSteps = request.MaxSteps,
                DriverType = request.DriverType,
                AgentType = request.AgentType
            };

            var job = StartJob(singleRequest);
            jobIds.Add(job.JobId);
            jobs.Add(job);
        }

        _compareGroups[compareId] = (request.Query, jobIds);
        return (compareId, jobs);
    }

    private static string ExtractJsonBlock(string text)
    {
        if (text.Contains("```json"))
        {
            var start = text.IndexOf("```json") + 7;
            var end = text.IndexOf("```", start);
            if (end > start)
            {
                return text.Substring(start, end - start).Trim();
            }
        }
        else if (text.Contains("```"))
        {
            var start = text.IndexOf("```") + 3;
            var end = text.IndexOf("```", start);
            if (end > start)
            {
                return text.Substring(start, end - start).Trim();
            }
        }
        return text.Trim();
    }
}
