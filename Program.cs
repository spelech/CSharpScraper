using Microsoft.AspNetCore.Mvc;
using CSharpScraper.Models;
using CSharpScraper.Services;
using CSharpScraper.Services.Drivers;
using CSharpScraper.Services.Agents;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Add logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Add services
builder.Services.AddHttpClient();
builder.Services.AddSingleton<SearxngClient>();
builder.Services.AddSingleton<ScraperJobService>();
builder.Services.AddScoped<ScraperRunner>();
builder.Services.AddSingleton<LlmClient>();
builder.Services.AddSingleton<McpService>();

// Drivers
builder.Services.AddTransient<PlaywrightBrowserDriver>();

// Agents
builder.Services.AddTransient<DomSelectorAgent>();
builder.Services.AddTransient<VisualCoordinateAgent>();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// Setup static files to serve screenshots for demos/debugging
var screenshotDir = Path.Combine(Directory.GetCurrentDirectory(), "temp_screenshots");
Directory.CreateDirectory(screenshotDir);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(screenshotDir),
    RequestPath = "/screenshots"
});

// Supported models for validation/warnings
var supportedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "gemini-3.5-flash", "gemini-3.5-pro", "gpt-4o-mini", "gpt-4o", "claude-3-5-sonnet", "claude-5-sonnet", "claude-3-haiku"
};

// Endpoints
app.MapGet("/health", () => 
{
    var appVersion = CSharpScraper.Utils.AppVersion.Value;
    return Results.Ok(new { status = "ok", service = "CSharpScraper", appVersion, netVersion = "10.0" });
});


app.MapPost("/api/scrape/start", ([FromBody] ScrapeRequest request, [FromServices] ScraperJobService jobService, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Url))
    {
        return Results.BadRequest(new { error = "Url is required." });
    }

    if (string.IsNullOrWhiteSpace(request.Goal))
    {
        return Results.BadRequest(new { error = "Goal is required." });
    }

    // Model validation
    if (!string.IsNullOrWhiteSpace(request.Model) && !supportedModels.Contains(request.Model))
    {
        logger.LogWarning("Requested Model '{Model}' is not in the validated list. Proceeding anyway.", request.Model);
    }

    var job = jobService.StartJob(request);

    return Results.Accepted($"/api/scrape/status/{job.JobId}", new 
    { 
        jobId = job.JobId, 
        status = job.Status.ToString(), 
        message = "Scraping job enqueued." 
    });
});

app.MapPost("/api/scrape/compare", async ([FromBody] ScrapeCompareRequest request, [FromServices] ScraperJobService jobService, [FromQuery] bool? sync, ILogger<Program> logger) =>
{
    if (request.Urls == null || request.Urls.Count == 0)
    {
        return Results.BadRequest(new { error = "Urls list is required and cannot be empty." });
    }

    if (string.IsNullOrWhiteSpace(request.Goal))
    {
        return Results.BadRequest(new { error = "Goal is required." });
    }

    // Model validation
    if (!string.IsNullOrWhiteSpace(request.Model) && !supportedModels.Contains(request.Model))
    {
        logger.LogWarning("Requested Model '{Model}' is not in the validated list. Proceeding anyway.", request.Model);
    }

    var (compareId, jobs) = jobService.StartCompareJobs(request);

    if (sync == true)
    {
        // Block and wait for all jobs to finish
        logger.LogInformation("Blocking comparative request {CompareId} awaiting parallel completion.", compareId);
        
        while (jobs.Any(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
        {
            await Task.Delay(500);
        }

        logger.LogInformation("Comparative request {CompareId} parallel execution completed. Returning aggregated results.", compareId);

        var results = jobs.Select(j => new JobResultResponse
        {
            JobId = j.JobId,
            Status = j.Status.ToString(),
            Data = j.ExtractedData,
            Error = j.Error,
            TotalPromptTokens = j.TotalPromptTokens,
            TotalCompletionTokens = j.TotalCompletionTokens
        }).ToList();

        return Results.Ok(new CompareResultResponse
        {
            CompareId = compareId,
            Goal = request.Goal,
            Status = jobs.All(j => j.Status == JobStatus.Completed) 
                ? "Completed" 
                : jobs.Any(j => j.Status == JobStatus.Failed) ? "Failed" : "Stopped",
            Results = results
        });
    }

    // Asynchronous response
    var summaries = jobs.Select(j => new CompareJobSummary
    {
        Url = j.Url,
        JobId = j.JobId,
        Status = j.Status.ToString()
    }).ToList();

    return Results.Accepted($"/api/scrape/compare/status/{compareId}", new CompareStartResponse
    {
        CompareId = compareId,
        Goal = request.Goal,
        Jobs = summaries
    });
});

app.MapPost("/api/scrape/discover-compare", async ([FromBody] ScrapeDiscoverCompareRequest request, [FromServices] ScraperJobService jobService, [FromQuery] bool? sync, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Query))
    {
        return Results.BadRequest(new { error = "Query is required." });
    }

    // Model validation
    if (!string.IsNullOrWhiteSpace(request.Model) && !supportedModels.Contains(request.Model))
    {
        logger.LogWarning("Requested Model '{Model}' is not in the validated list. Proceeding anyway.", request.Model);
    }

    var (compareId, jobs) = await jobService.StartDiscoveryCompareJobsAsync(request);

    if (jobs.Count == 0)
    {
        return Results.Ok(new CompareResultResponse
        {
            CompareId = compareId,
            Goal = request.Query,
            Status = "Failed",
            Results = new List<JobResultResponse>()
        });
    }

    if (sync == true)
    {
        logger.LogInformation("Blocking discovery comparative request {CompareId} awaiting parallel completion.", compareId);
        
        while (jobs.Any(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
        {
            await Task.Delay(500);
        }

        logger.LogInformation("Discovery comparative request {CompareId} parallel execution completed. Returning aggregated results.", compareId);

        var results = jobs.Select(j => new JobResultResponse
        {
            JobId = j.JobId,
            Status = j.Status.ToString(),
            Data = j.ExtractedData,
            Error = j.Error,
            TotalPromptTokens = j.TotalPromptTokens,
            TotalCompletionTokens = j.TotalCompletionTokens
        }).ToList();

        return Results.Ok(new CompareResultResponse
        {
            CompareId = compareId,
            Goal = request.Query,
            Status = jobs.All(j => j.Status == JobStatus.Completed) 
                ? "Completed" 
                : jobs.Any(j => j.Status == JobStatus.Failed) ? "Failed" : "Stopped",
            Results = results
        });
    }

    // Asynchronous response
    var summaries = jobs.Select(j => new CompareJobSummary
    {
        Url = j.Url,
        JobId = j.JobId,
        Status = j.Status.ToString()
    }).ToList();

    return Results.Accepted($"/api/scrape/compare/status/{compareId}", new CompareStartResponse
    {
        CompareId = compareId,
        Goal = request.Query,
        Jobs = summaries
    });
});

app.MapGet("/api/scrape/compare/status/{compareId:guid}", (Guid compareId, [FromServices] ScraperJobService jobService) =>
{
    var group = jobService.GetCompareGroup(compareId);
    if (group == null)
    {
        return Results.NotFound(new { error = $"Compare group {compareId} not found." });
    }

    var statuses = group.Value.Jobs.Select(j => new JobStatusResponse
    {
        JobId = j.JobId,
        Url = j.Url,
        Goal = j.Goal,
        Status = j.Status.ToString(),
        CurrentStep = j.CurrentStep,
        MaxSteps = j.MaxSteps,
        LastAction = j.LastAction,
        StartedAt = j.StartedAt,
        CompletedAt = j.CompletedAt,
        Error = j.Error,
        TotalPromptTokens = j.TotalPromptTokens,
        TotalCompletionTokens = j.TotalCompletionTokens
    }).ToList();

    string groupStatus = "Completed";
    if (group.Value.Jobs.Any(j => j.Status == JobStatus.Running || j.Status == JobStatus.Queued))
    {
        groupStatus = "Running";
    }
    else if (group.Value.Jobs.Any(j => j.Status == JobStatus.Failed))
    {
        groupStatus = "Failed";
    }
    else if (group.Value.Jobs.Any(j => j.Status == JobStatus.Stopped))
    {
        groupStatus = "Stopped";
    }

    return Results.Ok(new CompareStatusResponse
    {
        CompareId = compareId,
        Goal = group.Value.Goal,
        Status = groupStatus,
        Jobs = statuses
    });
});

app.MapGet("/api/scrape/status/{id:guid}", (Guid id, [FromServices] ScraperJobService jobService) =>
{
    var job = jobService.GetJob(id);
    if (job == null)
    {
        return Results.NotFound(new { error = $"Job {id} not found." });
    }

    return Results.Ok(new JobStatusResponse
    {
        JobId = job.JobId,
        Url = job.Url,
        Goal = job.Goal,
        Status = job.Status.ToString(),
        CurrentStep = job.CurrentStep,
        MaxSteps = job.MaxSteps,
        LastAction = job.LastAction,
        StartedAt = job.StartedAt,
        CompletedAt = job.CompletedAt,
        Error = job.Error,
        TotalPromptTokens = job.TotalPromptTokens,
        TotalCompletionTokens = job.TotalCompletionTokens
    });
});

app.MapGet("/api/scrape/result/{id:guid}", (Guid id, [FromServices] ScraperJobService jobService) =>
{
    var job = jobService.GetJob(id);
    if (job == null)
    {
        return Results.NotFound(new { error = $"Job {id} not found." });
    }

    return Results.Ok(new JobResultResponse
    {
        JobId = job.JobId,
        Status = job.Status.ToString(),
        Data = job.ExtractedData,
        Error = job.Error,
        TotalPromptTokens = job.TotalPromptTokens,
        TotalCompletionTokens = job.TotalCompletionTokens
    });
});

app.MapGet("/api/scrape/logs/{id:guid}", (Guid id, [FromServices] ScraperJobService jobService) =>
{
    var logs = jobService.GetJobLogs(id);
    if (logs == null)
    {
        return Results.NotFound(new { error = $"Job {id} not found." });
    }

    // Format log links to public urls for screenshots
    var formattedLogs = logs.Select(log => new ScrapeStepLog
    {
        Timestamp = log.Timestamp,
        StepNumber = log.StepNumber,
        Thought = log.Thought,
        Action = log.Action,
        ScreenshotPath = !string.IsNullOrEmpty(log.ScreenshotPath) 
            ? $"/screenshots/{id}/step_{log.StepNumber:D2}.png" 
            : null,
        PromptTokens = log.PromptTokens,
        CompletionTokens = log.CompletionTokens
    }).ToList();

    return Results.Ok(new JobLogsResponse
    {
        JobId = id,
        Logs = formattedLogs
    });
});

app.MapPost("/api/scrape/stop/{id:guid}", (Guid id, [FromServices] ScraperJobService jobService) =>
{
    var stopped = jobService.StopJob(id);
    if (!stopped)
    {
        return Results.BadRequest(new { error = $"Job {id} is not active or could not be found." });
    }

    return Results.Ok(new 
    { 
        jobId = id, 
        status = "Stopped", 
        message = "Job cancellation request sent." 
    });
});

// Standard & Streamable MCP endpoints (2026-07-28 RC Spec & 2025-11-25)
var mcpSessions = new ConcurrentDictionary<Guid, HttpContext>();

app.MapPost("/mcp", async (HttpContext context, [FromServices] McpService mcpService, ILogger<Program> logger) =>
{
    var protocolVersion = context.Request.Headers["MCP-Protocol-Version"].ToString();
    var mcpMethod = context.Request.Headers["Mcp-Method"].ToString();
    var mcpName = context.Request.Headers["Mcp-Name"].ToString();

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    McpRequest? request = null;
    if (!string.IsNullOrWhiteSpace(body))
    {
        try
        {
            request = JsonSerializer.Deserialize<McpRequest>(body);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse MCP request JSON.");
            return Results.BadRequest(new McpResponse { Error = new McpError { Code = -32700, Message = "Parse error" } });
        }
    }

    request ??= new McpRequest();
    var response = await mcpService.HandleRequestAsync(request, protocolVersion, mcpMethod, mcpName);

    if (response.Error != null && response.Error.Code == -32601)
    {
        return Results.NotFound(response);
    }

    return Results.Ok(response);
});

app.MapGet("/mcp/sse", async (HttpContext context, ILogger<Program> logger) =>
{
    context.Response.Headers.Append("Content-Type", "text/event-stream");
    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Connection", "keep-alive");

    var sessionId = Guid.NewGuid();
    mcpSessions[sessionId] = context;
    logger.LogInformation("New MCP client connected over SSE. Session: {SessionId}", sessionId);

    var messageUri = $"/mcp/message?sessionId={sessionId}";
    await context.Response.WriteAsync($"event: endpoint\ndata: {messageUri}\n\n");
    await context.Response.Body.FlushAsync();

    var tcs = new TaskCompletionSource();
    context.RequestAborted.Register(() =>
    {
        mcpSessions.TryRemove(sessionId, out _);
        logger.LogInformation("MCP client disconnected from SSE. Session: {SessionId}", sessionId);
        tcs.SetResult();
    });

    await tcs.Task;
});

app.MapPost("/mcp/message", async (HttpContext context, [FromQuery] Guid sessionId, [FromServices] McpService mcpService, ILogger<Program> logger) =>
{
    if (!mcpSessions.TryGetValue(sessionId, out var sseContext))
    {
        return Results.NotFound(new { error = "Session not found." });
    }

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    McpRequest? request;
    try
    {
        request = JsonSerializer.Deserialize<McpRequest>(body);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to parse MCP request");
        return Results.BadRequest(new McpResponse { Error = new McpError { Code = -32700, Message = "Parse error" } });
    }

    request ??= new McpRequest();
    var response = await mcpService.HandleRequestAsync(request);

    var responseJson = JsonSerializer.Serialize(response);
    await sseContext.Response.WriteAsync($"event: message\ndata: {responseJson}\n\n");
    await sseContext.Response.Body.FlushAsync();

    return Results.Ok();
});

app.Run();

