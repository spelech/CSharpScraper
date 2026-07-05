using Microsoft.AspNetCore.Mvc;
using CSharpScraper.Models;
using CSharpScraper.Services;
using CSharpScraper.Services.Drivers;
using CSharpScraper.Services.Agents;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Add logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Add services
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ScraperJobService>();
builder.Services.AddScoped<ScraperRunner>();
builder.Services.AddSingleton<LlmClient>();

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

// Setup static files to serve screenshots for demos/debugging
var screenshotDir = Path.Combine(Directory.GetCurrentDirectory(), "temp_screenshots");
Directory.CreateDirectory(screenshotDir);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(screenshotDir),
    RequestPath = "/screenshots"
});

// Supported models for validation/warnings
var supportedOuterModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "gemini-3.5-flash", "gemini-3.5-pro", "gpt-4o-mini", "gpt-4o", "claude-3-haiku"
};

var supportedInnerModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "claude-5-sonnet", "claude-3-5-sonnet", "gemini-3.5-pro", "gemini-3.5-flash", "gpt-4o-mini", "gpt-4o"
};

// Endpoints
app.MapGet("/health", () => 
{
    var appVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
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
    if (!string.IsNullOrWhiteSpace(request.OuterModel) && !supportedOuterModels.Contains(request.OuterModel))
    {
        logger.LogWarning("Requested OuterModel '{Model}' is not in the validated list. Proceeding anyway.", request.OuterModel);
    }

    if (!string.IsNullOrWhiteSpace(request.InnerModel) && !supportedInnerModels.Contains(request.InnerModel))
    {
        logger.LogWarning("Requested InnerModel '{Model}' is not in the validated list. Proceeding anyway.", request.InnerModel);
    }

    var job = jobService.StartJob(request);

    return Results.Accepted($"/api/scrape/status/{job.JobId}", new 
    { 
        jobId = job.JobId, 
        status = job.Status.ToString(), 
        message = "Scraping job enqueued." 
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
        Error = job.Error
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
        Error = job.Error
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
            : null
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

app.Run();
