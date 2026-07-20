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

// Standard MCP endpoints
var mcpSessions = new ConcurrentDictionary<Guid, HttpContext>();

app.MapGet("/mcp/sse", async (HttpContext context, ILogger<Program> logger) =>
{
    context.Response.Headers.Append("Content-Type", "text/event-stream");
    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Connection", "keep-alive");

    var sessionId = Guid.NewGuid();
    mcpSessions[sessionId] = context;
    logger.LogInformation("New MCP client connected. Session: {SessionId}", sessionId);

    // Send the endpoint mapping URI as per MCP SSE spec
    var messageUri = $"/mcp/message?sessionId={sessionId}";
    await context.Response.WriteAsync($"event: endpoint\ndata: {messageUri}\n\n");
    await context.Response.Body.FlushAsync();

    // Keep connection alive
    var tcs = new TaskCompletionSource();
    context.RequestAborted.Register(() =>
    {
        mcpSessions.TryRemove(sessionId, out _);
        logger.LogInformation("MCP client disconnected. Session: {SessionId}", sessionId);
        tcs.SetResult();
    });

    await tcs.Task;
});

app.MapPost("/mcp/message", async (HttpContext context, [FromQuery] Guid sessionId, [FromServices] ScraperJobService jobService, ILogger<Program> logger) =>
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
        return Results.BadRequest(new McpResponse
        {
            Error = new McpError { Code = -32700, Message = "Parse error" }
        });
    }

    if (request == null || string.IsNullOrEmpty(request.Method))
    {
        return Results.BadRequest(new McpResponse
        {
            Error = new McpError { Code = -32600, Message = "Invalid Request" },
            Id = request?.Id
        });
    }

    logger.LogInformation("Received MCP request: {Method} (Id: {Id})", request.Method, request.Id);

    McpResponse response = new McpResponse { Id = request.Id };

    try
    {
        switch (request.Method)
        {
            case "initialize":
                response.Result = new McpInitializeResult();
                break;

            case "tools/list":
                response.Result = new McpListToolsResult
                {
                    Tools = new List<McpTool>
                    {
                        new McpTool
                        {
                            Name = "scrape_url",
                            Description = "Scrape data from a specific URL with an agent goal.",
                            InputSchema = new McpInputSchema
                            {
                                Properties = new Dictionary<string, McpSchemaProperty>
                                {
                                    { "url", new McpSchemaProperty { Type = "string", Description = "The exact HTTP/HTTPS URL to start scraping from." } },
                                    { "goal", new McpSchemaProperty { Type = "string", Description = "The extraction or interaction goal. Explain what information to retrieve." } },
                                    { "model", new McpSchemaProperty { Type = "string", Description = "LLM model to guide the agent (e.g., gemini-3.5-flash)." } },
                                    { "maxSteps", new McpSchemaProperty { Type = "string", Description = "Maximum scrape iteration steps (default 15)." } }
                                },
                                Required = new List<string> { "url", "goal" }
                            }
                        },
                        new McpTool
                        {
                            Name = "scrape_compare",
                            Description = "Compare scraping results from multiple URLs parallelly.",
                            InputSchema = new McpInputSchema
                            {
                                Properties = new Dictionary<string, McpSchemaProperty>
                                {
                                    { "urls", new McpSchemaProperty { Type = "array", Description = "List of URLs to scrape.", Items = new McpSchemaProperty { Type = "string", Description = "URL" } } },
                                    { "goal", new McpSchemaProperty { Type = "string", Description = "The extraction or interaction goal." } },
                                    { "model", new McpSchemaProperty { Type = "string", Description = "LLM model to use." } },
                                    { "maxSteps", new McpSchemaProperty { Type = "string", Description = "Maximum steps per URL." } }
                                },
                                Required = new List<string> { "urls", "goal" }
                            }
                        }
                    }
                };
                break;

            case "tools/call":
                if (request.Params == null)
                {
                    response.Error = new McpError { Code = -32602, Message = "Invalid params: Params is required for tools/call" };
                    break;
                }
                var callParams = JsonSerializer.Deserialize<McpCallToolParams>(request.Params.Value.GetRawText());
                if (callParams == null || string.IsNullOrEmpty(callParams.Name))
                {
                    response.Error = new McpError { Code = -32602, Message = "Invalid params: Name is required" };
                    break;
                }

                if (callParams.Name == "scrape_url")
                {
                    if (callParams.Arguments == null)
                    {
                        response.Error = new McpError { Code = -32602, Message = "Arguments missing" };
                        break;
                    }

                    var doc = JsonDocument.Parse(callParams.Arguments.Value.GetRawText());
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("url", out var urlProp) || !root.TryGetProperty("goal", out var goalProp))
                    {
                        response.Error = new McpError { Code = -32602, Message = "Required arguments 'url' and/or 'goal' missing." };
                        break;
                    }

                    var scrapeRequest = new ScrapeRequest
                    {
                        Url = urlProp.GetString() ?? "",
                        Goal = goalProp.GetString() ?? ""
                    };

                    if (root.TryGetProperty("model", out var mProp)) scrapeRequest.Model = mProp.GetString();
                    if (root.TryGetProperty("maxSteps", out var msProp))
                    {
                        if (msProp.ValueKind == JsonValueKind.Number) scrapeRequest.MaxSteps = msProp.GetInt32();
                        else if (msProp.ValueKind == JsonValueKind.String && int.TryParse(msProp.GetString(), out var msInt)) scrapeRequest.MaxSteps = msInt;
                    }

                    logger.LogInformation("MCP Scrape initiating: {Url} with Goal: {Goal}", scrapeRequest.Url, scrapeRequest.Goal);
                    var job = jobService.StartJob(scrapeRequest);

                    // Block and await completion synchronously to return data directly to the LLM Client
                    while (job.Status == JobStatus.Queued || job.Status == JobStatus.Running)
                    {
                        await Task.Delay(500);
                    }

                    if (job.Status == JobStatus.Completed)
                    {
                        response.Result = new McpCallToolResult
                        {
                            Content = new List<McpContent>
                            {
                                new McpContent { Text = JsonSerializer.Serialize(job.ExtractedData) }
                            }
                        };
                    }
                    else
                    {
                        response.Result = new McpCallToolResult
                        {
                            IsError = true,
                            Content = new List<McpContent>
                            {
                                new McpContent { Text = $"Job failed or stopped. Error: {job.Error ?? "Unknown error"}" }
                            }
                        };
                    }
                }
                else if (callParams.Name == "scrape_compare")
                {
                    if (callParams.Arguments == null)
                    {
                        response.Error = new McpError { Code = -32602, Message = "Arguments missing" };
                        break;
                    }

                    var doc = JsonDocument.Parse(callParams.Arguments.Value.GetRawText());
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("urls", out var urlsProp) || !root.TryGetProperty("goal", out var goalProp))
                    {
                        response.Error = new McpError { Code = -32602, Message = "Required arguments 'urls' and/or 'goal' missing." };
                        break;
                    }

                    var urls = new List<string>();
                    foreach (var element in urlsProp.EnumerateArray())
                    {
                        var u = element.GetString();
                        if (!string.IsNullOrEmpty(u)) urls.Add(u);
                    }

                    var compareRequest = new ScrapeCompareRequest
                    {
                        Urls = urls,
                        Goal = goalProp.GetString() ?? ""
                    };

                    if (root.TryGetProperty("model", out var mProp)) compareRequest.Model = mProp.GetString();
                    if (root.TryGetProperty("maxSteps", out var msProp))
                    {
                        if (msProp.ValueKind == JsonValueKind.Number) compareRequest.MaxSteps = msProp.GetInt32();
                        else if (msProp.ValueKind == JsonValueKind.String && int.TryParse(msProp.GetString(), out var msInt)) compareRequest.MaxSteps = msInt;
                    }

                    logger.LogInformation("MCP Scrape Compare initiating with Goal: {Goal}", compareRequest.Goal);
                    var (compareId, jobs) = jobService.StartCompareJobs(compareRequest);

                    // Block and await completion synchronously
                    while (jobs.Any(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
                    {
                        await Task.Delay(500);
                    }

                    var results = jobs.Select(j => new JobResultResponse
                    {
                        JobId = j.JobId,
                        Status = j.Status.ToString(),
                        Data = j.ExtractedData,
                        Error = j.Error,
                        TotalPromptTokens = j.TotalPromptTokens,
                        TotalCompletionTokens = j.TotalCompletionTokens
                    }).ToList();

                    response.Result = new McpCallToolResult
                    {
                        Content = new List<McpContent>
                        {
                            new McpContent { Text = JsonSerializer.Serialize(results) }
                        }
                    };
                }
                else
                {
                    response.Error = new McpError { Code = -32601, Message = $"Tool not found: {callParams.Name}" };
                }
                break;

            default:
                response.Error = new McpError { Code = -32601, Message = $"Method not found: {request.Method}" };
                break;
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error processing MCP request {Method}", request.Method);
        response.Error = new McpError { Code = -32603, Message = ex.Message };
    }

    // Send the response back through the SSE connection's stream
    var responseJson = JsonSerializer.Serialize(response);
    await sseContext.Response.WriteAsync($"event: message\ndata: {responseJson}\n\n");
    await sseContext.Response.Body.FlushAsync();

    return Results.Ok();
});

app.Run();

