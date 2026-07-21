using System.Text.Json;
using CSharpScraper.Models;
using Microsoft.Extensions.Logging;

namespace CSharpScraper.Services;

public class McpService
{
    private readonly ScraperJobService _jobService;
    private readonly ILogger<McpService> _logger;

    private static readonly HashSet<string> SupportedModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "gemini-3.5-flash", "gemini-3.5-pro", "gpt-4o-mini", "gpt-4o", "claude-3-5-sonnet", "claude-5-sonnet", "claude-3-haiku"
    };

    public McpService(ScraperJobService jobService, ILogger<McpService> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public async Task<McpResponse> HandleRequestAsync(McpRequest request, string? protocolVersionHeader = null, string? mcpMethodHeader = null, string? mcpNameHeader = null)
    {
        var method = !string.IsNullOrEmpty(mcpMethodHeader) ? mcpMethodHeader : request.Method;
        _logger.LogInformation("Processing MCP Request method: {Method} (Id: {Id})", method, request.Id);

        var response = new McpResponse { Id = request.Id };

        try
        {
            switch (method)
            {
                case "initialize":
                    response.Result = new McpInitializeResult();
                    break;

                case "server/discover":
                    response.Result = new McpDiscoverResult();
                    break;

                case "tools/list":
                    response.Result = GetListToolsResult();
                    break;

                case "tools/call":
                    response.Result = await HandleToolCallAsync(request.Params);
                    break;

                case "prompts/list":
                    response.Result = GetListPromptsResult();
                    break;

                case "prompts/get":
                    response.Result = HandleGetPrompt(request.Params);
                    break;

                case "resources/list":
                    response.Result = GetListResourcesResult();
                    break;

                case "resources/templates/list":
                    response.Result = GetListResourceTemplatesResult();
                    break;

                case "resources/read":
                    response.Result = HandleReadResource(request.Params);
                    break;

                case "tasks/get":
                    response.Result = HandleGetTask(request.Params);
                    break;

                case "tasks/cancel":
                    response.Result = HandleCancelTask(request.Params);
                    break;

                case "completion/complete":
                    response.Result = HandleComplete(request.Params);
                    break;

                default:
                    response.Error = new McpError { Code = -32601, Message = $"Method not found: {method}" };
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing MCP request for method '{Method}'", method);
            response.Error = new McpError { Code = -32603, Message = ex.Message };
        }

        return response;
    }

    // --- Tools ---

    public McpListToolsResult GetListToolsResult()
    {
        return new McpListToolsResult
        {
            Tools = new List<McpTool>
            {
                new McpTool
                {
                    Name = "scrape_url",
                    Description = "Scrape dynamic web content from a single target URL given an agent goal.",
                    InputSchema = new McpInputSchema
                    {
                        Properties = new Dictionary<string, McpSchemaProperty>
                        {
                            { "url", new McpSchemaProperty { Type = "string", Description = "The HTTP/HTTPS web page URL to scrape." } },
                            { "goal", new McpSchemaProperty { Type = "string", Description = "Interaction or extraction objective (e.g. 'Extract top 5 titles')." } },
                            { "model", new McpSchemaProperty { Type = "string", Description = "LLM model (default gemini-3.5-flash)." } },
                            { "maxSteps", new McpSchemaProperty { Type = "string", Description = "Maximum agent iteration steps (default 15)." } }
                        },
                        Required = new List<string> { "url", "goal" }
                    }
                },
                new McpTool
                {
                    Name = "scrape_compare",
                    Description = "Concurrently scrape and extract structured data from multiple web page URLs.",
                    InputSchema = new McpInputSchema
                    {
                        Properties = new Dictionary<string, McpSchemaProperty>
                        {
                            { "urls", new McpSchemaProperty { Type = "array", Description = "List of target URLs.", Items = new McpSchemaProperty { Type = "string", Description = "URL" } } },
                            { "goal", new McpSchemaProperty { Type = "string", Description = "Comparison extraction objective." } },
                            { "model", new McpSchemaProperty { Type = "string", Description = "LLM model to guide execution." } },
                            { "maxSteps", new McpSchemaProperty { Type = "string", Description = "Maximum steps per URL." } }
                        },
                        Required = new List<string> { "urls", "goal" }
                    }
                }
            }
        };
    }

    private async Task<McpCallToolResult> HandleToolCallAsync(JsonElement? paramsElement)
    {
        if (paramsElement == null)
        {
            throw new Exception("Invalid params: Params is required for tools/call");
        }

        var callParams = JsonSerializer.Deserialize<McpCallToolParams>(paramsElement.Value.GetRawText());
        if (callParams == null || string.IsNullOrEmpty(callParams.Name))
        {
            throw new Exception("Invalid params: Name is required for tools/call");
        }

        if (callParams.Name == "scrape_url")
        {
            if (callParams.Arguments == null) throw new Exception("Arguments missing");

            var doc = JsonDocument.Parse(callParams.Arguments.Value.GetRawText());
            var root = doc.RootElement;
            if (!root.TryGetProperty("url", out var urlProp) || !root.TryGetProperty("goal", out var goalProp))
            {
                throw new Exception("Required arguments 'url' and/or 'goal' missing.");
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

            var job = _jobService.StartJob(scrapeRequest);

            // Await completion synchronously to return data directly
            while (job.Status == JobStatus.Queued || job.Status == JobStatus.Running)
            {
                await Task.Delay(500);
            }

            if (job.Status == JobStatus.Completed)
            {
                return new McpCallToolResult
                {
                    Content = new List<McpContent>
                    {
                        new McpContent { Type = "text", Text = JsonSerializer.Serialize(job.ExtractedData) }
                    }
                };
            }
            else
            {
                return new McpCallToolResult
                {
                    IsError = true,
                    Content = new List<McpContent>
                    {
                        new McpContent { Type = "text", Text = $"Job failed or stopped. Error: {job.Error ?? "Unknown error"}" }
                    }
                };
            }
        }
        else if (callParams.Name == "scrape_compare")
        {
            if (callParams.Arguments == null) throw new Exception("Arguments missing");

            var doc = JsonDocument.Parse(callParams.Arguments.Value.GetRawText());
            var root = doc.RootElement;
            if (!root.TryGetProperty("urls", out var urlsProp) || !root.TryGetProperty("goal", out var goalProp))
            {
                throw new Exception("Required arguments 'urls' and/or 'goal' missing.");
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

            var (compareId, jobs) = _jobService.StartCompareJobs(compareRequest);

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

            return new McpCallToolResult
            {
                Content = new List<McpContent>
                {
                    new McpContent { Type = "text", Text = JsonSerializer.Serialize(results) }
                }
            };
        }
        else
        {
            throw new Exception($"Tool not found: {callParams.Name}");
        }
    }

    // --- Prompts ---

    public McpListPromptsResult GetListPromptsResult()
    {
        return new McpListPromptsResult
        {
            Prompts = new List<McpPrompt>
            {
                new McpPrompt
                {
                    Name = "e_commerce_scrape",
                    Description = "Prompt template for extracting product details, price, availability, and specs from an e-commerce page.",
                    Arguments = new List<McpPromptArgument>
                    {
                        new McpPromptArgument { Name = "url", Description = "Target e-commerce product URL", Required = true },
                        new McpPromptArgument { Name = "location", Description = "Store location or ZIP code", Required = false }
                    }
                },
                new McpPrompt
                {
                    Name = "article_summary_scrape",
                    Description = "Prompt template for scraping and summarizing key takeaways from an news or blog article.",
                    Arguments = new List<McpPromptArgument>
                    {
                        new McpPromptArgument { Name = "url", Description = "Target article URL", Required = true },
                        new McpPromptArgument { Name = "max_points", Description = "Number of key summary points", Required = false }
                    }
                },
                new McpPrompt
                {
                    Name = "multi_retailer_compare",
                    Description = "Prompt template for querying SearXNG auto-discovery and comparing product pricing across retailers.",
                    Arguments = new List<McpPromptArgument>
                    {
                        new McpPromptArgument { Name = "query", Description = "Product name or model query", Required = true },
                        new McpPromptArgument { Name = "location", Description = "City/State or ZIP", Required = false }
                    }
                }
            }
        };
    }

    private McpGetPromptResult HandleGetPrompt(JsonElement? paramsElement)
    {
        if (paramsElement == null) throw new Exception("Params is required for prompts/get");
        var getParams = JsonSerializer.Deserialize<McpGetPromptParams>(paramsElement.Value.GetRawText());
        if (getParams == null || string.IsNullOrEmpty(getParams.Name)) throw new Exception("Prompt name is required.");

        var args = getParams.Arguments ?? new Dictionary<string, string>();
        string promptText = "";
        string desc = "";

        switch (getParams.Name)
        {
            case "e_commerce_scrape":
                desc = "E-commerce extraction prompt";
                var url = args.GetValueOrDefault("url", "https://example.com/product");
                var loc = args.GetValueOrDefault("location", "Standard");
                promptText = $"Extract the full product title, current price, stock availability, rating, and bulleted features for item at {url} (Location: {loc}).";
                break;

            case "article_summary_scrape":
                desc = "Article summary extraction prompt";
                var artUrl = args.GetValueOrDefault("url", "https://news.ycombinator.com");
                var maxPts = args.GetValueOrDefault("max_points", "5");
                promptText = $"Read article at {artUrl} and extract the main title, author, publish date, and summarize top {maxPts} takeaways.";
                break;

            case "multi_retailer_compare":
                desc = "Multi-retailer price comparison prompt";
                var q = args.GetValueOrDefault("query", "Sriracha sauce");
                var l = args.GetValueOrDefault("location", "Schaumburg, IL");
                promptText = $"Perform multi-retailer auto-discovery search for product '{q}' in area '{l}', and extract comparative price, availability, and item links.";
                break;

            default:
                throw new Exception($"Prompt template not found: {getParams.Name}");
        }

        return new McpGetPromptResult
        {
            Description = desc,
            Messages = new List<McpPromptMessage>
            {
                new McpPromptMessage
                {
                    Role = "user",
                    Content = new McpContent { Type = "text", Text = promptText }
                }
            }
        };
    }

    // --- Resources ---

    public McpListResourcesResult GetListResourcesResult()
    {
        // Dynamically list active/recent jobs as resources
        var resources = new List<McpResource>();

        foreach (var job in _jobService.GetAllJobs().Take(20))
        {
            resources.Add(new McpResource
            {
                Uri = $"scraper://jobs/{job.JobId}",
                Name = $"Job {job.JobId:N} ({job.Status})",
                Description = $"Crawl job for target: {job.Url}",
                MimeType = "application/json"
            });
        }

        return new McpListResourcesResult { Resources = resources };
    }

    public McpListResourceTemplatesResult GetListResourceTemplatesResult()
    {
        return new McpListResourceTemplatesResult
        {
            ResourceTemplates = new List<McpResourceTemplate>
            {
                new McpResourceTemplate
                {
                    UriTemplate = "scraper://jobs/{jobId}",
                    Name = "Scrape Job Data",
                    Description = "Retrieves the status and extracted structured JSON data for a specific scrape job.",
                    MimeType = "application/json"
                },
                new McpResourceTemplate
                {
                    UriTemplate = "scraper://jobs/{jobId}/logs",
                    Name = "Scrape Job Execution Logs",
                    Description = "Retrieves step logs including LLM thoughts, browser actions, and token metrics.",
                    MimeType = "application/json"
                },
                new McpResourceTemplate
                {
                    UriTemplate = "scraper://jobs/{jobId}/screenshots/{stepNumber}",
                    Name = "Scrape Job Step Screenshot",
                    Description = "Retrieves a base64 encoded PNG screenshot captured during a specific iteration step.",
                    MimeType = "image/png"
                },
                new McpResourceTemplate
                {
                    UriTemplate = "scraper://compares/{compareId}",
                    Name = "Scrape Comparison Group Data",
                    Description = "Retrieves status and comparative results for a parallel comparison request.",
                    MimeType = "application/json"
                }
            }
        };
    }

    private McpReadResourceResult HandleReadResource(JsonElement? paramsElement)
    {
        if (paramsElement == null) throw new Exception("Params is required for resources/read");
        var readParams = JsonSerializer.Deserialize<McpReadResourceParams>(paramsElement.Value.GetRawText());
        if (readParams == null || string.IsNullOrEmpty(readParams.Uri)) throw new Exception("URI is required for resources/read");

        var uri = readParams.Uri;

        // Parse scraper://jobs/{jobId}
        if (uri.StartsWith("scraper://jobs/"))
        {
            var path = uri.Substring("scraper://jobs/".Length);
            var parts = path.Split('/');
            if (Guid.TryParse(parts[0], out var jobId))
            {
                var job = _jobService.GetJob(jobId);
                if (job == null) throw new Exception($"Job {jobId} not found.");

                if (parts.Length == 1)
                {
                    // Job JSON summary
                    var data = JsonSerializer.Serialize(new
                    {
                        jobId = job.JobId,
                        url = job.Url,
                        goal = job.Goal,
                        status = job.Status.ToString(),
                        currentStep = job.CurrentStep,
                        maxSteps = job.MaxSteps,
                        extractedData = job.ExtractedData,
                        error = job.Error,
                        promptTokens = job.TotalPromptTokens,
                        completionTokens = job.TotalCompletionTokens
                    });

                    return new McpReadResourceResult
                    {
                        Contents = new List<McpResourceContents>
                        {
                            new McpResourceContents { Uri = uri, MimeType = "application/json", Text = data }
                        }
                    };
                }
                else if (parts.Length == 2 && parts[1] == "logs")
                {
                    var logs = _jobService.GetJobLogs(jobId);
                    var data = JsonSerializer.Serialize(logs);
                    return new McpReadResourceResult
                    {
                        Contents = new List<McpResourceContents>
                        {
                            new McpResourceContents { Uri = uri, MimeType = "application/json", Text = data }
                        }
                    };
                }
                else if (parts.Length == 3 && parts[1] == "screenshots" && int.TryParse(parts[2], out var stepNum))
                {
                    var logs = _jobService.GetJobLogs(jobId);
                    var stepLog = logs?.FirstOrDefault(l => l.StepNumber == stepNum);
                    if (stepLog != null && !string.IsNullOrEmpty(stepLog.ScreenshotPath) && File.Exists(stepLog.ScreenshotPath))
                    {
                        var bytes = File.ReadAllBytes(stepLog.ScreenshotPath);
                        var base64 = Convert.ToBase64String(bytes);
                        return new McpReadResourceResult
                        {
                            Contents = new List<McpResourceContents>
                            {
                                new McpResourceContents { Uri = uri, MimeType = "image/png", Blob = base64 }
                            }
                        };
                    }
                    throw new Exception($"Screenshot for step {stepNum} in job {jobId} not found.");
                }
            }
        }
        else if (uri.StartsWith("scraper://compares/"))
        {
            var compareIdStr = uri.Substring("scraper://compares/".Length);
            if (Guid.TryParse(compareIdStr, out var compareId))
            {
                var group = _jobService.GetCompareGroup(compareId);
                if (group != null)
                {
                    var data = JsonSerializer.Serialize(group.Value.Jobs.Select(j => new
                    {
                        jobId = j.JobId,
                        url = j.Url,
                        status = j.Status.ToString(),
                        data = j.ExtractedData,
                        error = j.Error
                    }));

                    return new McpReadResourceResult
                    {
                        Contents = new List<McpResourceContents>
                        {
                            new McpResourceContents { Uri = uri, MimeType = "application/json", Text = data }
                        }
                    };
                }
            }
        }

        throw new Exception($"Resource or URI format invalid: {uri}");
    }

    // --- Tasks Extension ---

    private McpTaskResult HandleGetTask(JsonElement? paramsElement)
    {
        if (paramsElement == null) throw new Exception("Params is required for tasks/get");
        var taskParams = JsonSerializer.Deserialize<McpTaskParams>(paramsElement.Value.GetRawText());
        if (taskParams == null || string.IsNullOrEmpty(taskParams.TaskId)) throw new Exception("TaskId is required for tasks/get");

        if (Guid.TryParse(taskParams.TaskId, out var jobId))
        {
            var job = _jobService.GetJob(jobId);
            if (job != null)
            {
                return new McpTaskResult
                {
                    TaskId = job.JobId.ToString(),
                    Status = job.Status.ToString(),
                    CurrentStep = job.CurrentStep,
                    MaxSteps = job.MaxSteps,
                    LastAction = job.LastAction,
                    Error = job.Error
                };
            }
        }

        throw new Exception($"Task {taskParams?.TaskId} not found.");
    }

    private McpTaskResult HandleCancelTask(JsonElement? paramsElement)
    {
        if (paramsElement == null) throw new Exception("Params is required for tasks/cancel");
        var taskParams = JsonSerializer.Deserialize<McpTaskParams>(paramsElement.Value.GetRawText());
        if (taskParams == null || string.IsNullOrEmpty(taskParams.TaskId)) throw new Exception("TaskId is required for tasks/cancel");

        if (Guid.TryParse(taskParams.TaskId, out var jobId))
        {
            var stopped = _jobService.StopJob(jobId);
            var job = _jobService.GetJob(jobId);
            return new McpTaskResult
            {
                TaskId = jobId.ToString(),
                Status = stopped ? "Stopped" : (job?.Status.ToString() ?? "NotFound"),
                Error = stopped ? "Cancellation requested via MCP task API" : job?.Error
            };
        }

        throw new Exception($"Task {taskParams?.TaskId} not found.");
    }

    // --- Argument Completion ---

    private McpCompleteResult HandleComplete(JsonElement? paramsElement)
    {
        var result = new McpCompleteResult();
        if (paramsElement == null) return result;

        var compParams = JsonSerializer.Deserialize<McpCompleteParams>(paramsElement.Value.GetRawText());
        if (compParams == null) return result;

        var matches = new List<string>();

        if (compParams.Ref.Type == "ref/prompt")
        {
            if (compParams.Argument.Name == "model")
            {
                matches.AddRange(SupportedModels.Where(m => m.Contains(compParams.Argument.Value ?? "", StringComparison.OrdinalIgnoreCase)));
            }
            else if (compParams.Argument.Name == "url")
            {
                matches.Add("https://news.ycombinator.com");
                matches.Add("https://www.target.com");
            }
        }
        else if (compParams.Ref.Type == "ref/resource")
        {
            foreach (var job in _jobService.GetAllJobs().Take(10))
            {
                matches.Add(job.JobId.ToString());
            }
        }

        result.Completion.Values = matches;
        result.Completion.Total = matches.Count;
        return result;
    }
}
