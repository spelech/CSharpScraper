using CSharpScraper.Interfaces;
using CSharpScraper.Models;
using CSharpScraper.Services.Drivers;
using CSharpScraper.Services.Agents;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text;

namespace CSharpScraper.Services;

public class ScraperRunner
{
    private readonly ILogger<ScraperRunner> _logger;
    private readonly LlmClient _llmClient;
    private readonly IServiceProvider _serviceProvider;

    public ScraperRunner(
        ILogger<ScraperRunner> logger,
        LlmClient llmClient,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _llmClient = llmClient;
        _serviceProvider = serviceProvider;
    }

    public virtual async Task RunJobAsync(ScrapeJob job, ScrapeRequest request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting scraping job {JobId} for URL {Url}", job.JobId, job.Url);
        
        // 1. Resolve Driver
        IExecutionDriver driver = request.DriverType.ToLower() switch
        {
            "playwright" => _serviceProvider.GetRequiredService<PlaywrightBrowserDriver>(),
            // Future desktop or alternative drivers go here
            _ => _serviceProvider.GetRequiredService<PlaywrightBrowserDriver>()
        };

        // 2. Resolve Agent
        IInnerLoopAgent agent = request.AgentType.ToLower() switch
        {
            "dom" => _serviceProvider.GetRequiredService<DomSelectorAgent>(),
            "visual" => _serviceProvider.GetRequiredService<VisualCoordinateAgent>(),
            _ => _serviceProvider.GetRequiredService<DomSelectorAgent>()
        };

        try
        {
            job.Status = JobStatus.Running;
            job.LastAction = "Launching browser...";
            
            await driver.InitializeAsync(job.JobId);
            
            job.LastAction = $"Navigating to {job.Url}...";
            await driver.NavigateAsync(job.Url);

            // Wait a moment for dynamic elements to load
            await driver.WaitAsync(2000);

            var historySummary = new StringBuilder();
            
            // Default models from request or env vars
            var outerModel = request.OuterModel ?? "gemini-3.5-flash";
            var innerModel = request.InnerModel ?? "claude-5-sonnet";

            _logger.LogInformation("Job {JobId}: Using Outer Model={Outer}, Inner Model={Inner}", job.JobId, outerModel, innerModel);

            for (int step = 1; step <= job.MaxSteps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                job.CurrentStep = step;

                string currentSubGoal = job.Goal;

                // --- DUAL-LOOP ARCHITECTURE: OUTER LOOP REASONING ---
                // The Outer Loop acts as the "director", evaluates progress, and issues plans for the inner loop.
                var outerDecision = await RunOuterLoopReasoningAsync(
                    job.Goal, 
                    step, 
                    driver, 
                    outerModel, 
                    request.BaseUrl, 
                    request.ApiKey, 
                    historySummary.ToString()
                );

                if (outerDecision.Status == "completed")
                {
                    _logger.LogInformation("Job {JobId}: Outer loop determined goal completed.", job.JobId);
                    job.Status = JobStatus.Completed;
                    job.ExtractedData = outerDecision.ExtractedData;
                    job.LastAction = "Goal achieved. Data extracted.";
                    break;
                }
                else if (outerDecision.Status == "failed")
                {
                    _logger.LogWarning("Job {JobId}: Outer loop declared failure. Reason: {Reason}", job.JobId, outerDecision.Reason);
                    job.Status = JobStatus.Failed;
                    job.Error = outerDecision.Reason ?? "Outer loop declared failure.";
                    job.LastAction = $"Failed: {job.Error}";
                    break;
                }
                else
                {
                    // Proceed with the sub-goal instructions issued by the outer loop
                    if (!string.IsNullOrWhiteSpace(outerDecision.Instructions))
                    {
                        currentSubGoal = outerDecision.Instructions;
                        _logger.LogInformation("Job {JobId}: Outer loop issued sub-goal: '{SubGoal}'", job.JobId, currentSubGoal);
                    }
                }

                // --- INNER LOOP EXECUTION ---
                // The Inner Loop agent observes the page state and decides on a low-level browser interaction.
                job.LastAction = $"Planning step {step}...";
                var stepLog = await agent.DecideNextActionAsync(
                    currentSubGoal, 
                    step, 
                    driver, 
                    _llmClient, 
                    innerModel, 
                    historySummary.ToString()
                );

                _logger.LogInformation("Job {JobId}: Step {Step} Action: {Action}", job.JobId, step, stepLog.Action);
                job.LastAction = stepLog.Action.ToString();

                // Record screenshot if available
                var tempFolder = Path.Combine(Directory.GetCurrentDirectory(), "temp_screenshots", job.JobId.ToString());
                stepLog.ScreenshotPath = await driver.CaptureScreenshotAsync(tempFolder, step);

                job.Steps.Add(stepLog);

                // Execute the chosen action via Driver
                await ExecuteActionAsync(driver, stepLog.Action);

                // Wait for any network requests or renders to complete
                await driver.WaitAsync(1500);

                // Update history string for subsequent model prompts
                historySummary.AppendLine($"- Step {step}: {stepLog.Thought} -> Executed: {stepLog.Action}");

                if (stepLog.Action.Type == "extract_data")
                {
                    _logger.LogInformation("Job {JobId}: Inner loop extracted data and completed.", job.JobId);
                    job.Status = JobStatus.Completed;
                    
                    // Fallback in case outer loop didn't capture it yet
                    if (stepLog.Action.Data.HasValue)
                    {
                        job.ExtractedData = stepLog.Action.Data.Value;
                    }
                    break;
                }
                else if (stepLog.Action.Type == "fail")
                {
                    _logger.LogWarning("Job {JobId}: Inner loop failed. Reason: {Reason}", job.JobId, stepLog.Action.Reason);
                    job.Status = JobStatus.Failed;
                    job.Error = stepLog.Action.Reason ?? "Inner loop failed.";
                    break;
                }
            }

            if (job.Status == JobStatus.Running)
            {
                // Max steps reached
                job.Status = JobStatus.Failed;
                job.Error = $"Reached max execution steps ({job.MaxSteps}) without accomplishing the goal.";
                job.LastAction = "Failed: Max steps reached.";
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Job {JobId}: Scraping job was cancelled.", job.JobId);
            job.Status = JobStatus.Stopped;
            job.LastAction = "Job stopped by user request.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred during execution of Job {JobId}", job.JobId);
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
            job.LastAction = $"Error: {ex.Message}";
        }
        finally
        {
            job.CompletedAt = DateTime.UtcNow;
            await driver.CleanupAsync();
        }
    }

    private async Task<OuterLoopDecision> RunOuterLoopReasoningAsync(
        string mainGoal, 
        int stepNumber, 
        IExecutionDriver driver, 
        string modelName, 
        string? customBaseUrl, 
        string? customApiKey, 
        string historySummary)
    {
        var url = await driver.GetUrlAsync();
        var title = await driver.GetTitleAsync();

        var systemPrompt = @"You are the Outer Loop Director for an autonomous web agent. Your job is to analyze what the agent has done so far, inspect the current page environment, and decide whether the overall goal is accomplished.

If the goal is accomplished, output a completed status and extract the final structured data.
If the agent is stuck or the goal has failed (e.g. CAPTCHAs, persistent errors, missing data), declare a failure.
If the agent should continue, output a continue status and provide specific instructions / sub-goals for the next step (e.g., 'Find the search bar and input the product name', or 'Scroll down to check if there are more links').

Provide your output in STRICT JSON format. No extra text before or after the JSON block.

Expected JSON output format:
{
  ""status"": ""continue"", // ""continue"", ""completed"", or ""failed""
  ""instructions"": ""Specific sub-goal instructions for the agent on this step."",
  ""extractedData"": null, // Object containing final key-value data if completed (e.g. { ""price"": ""$10.00"", ""title"": ""Thing"" })
  ""reason"": null // String explaining failure if status is failed
}";

        var userPrompt = $@"Goal: {mainGoal}
Current Step: {stepNumber}
Current URL: {url}
Current Title: {title}

---
History of Actions Taken So Far:
{(string.IsNullOrWhiteSpace(historySummary) ? "None (this is the first step)." : historySummary)}

Analyze the progress. Check if the goal is achieved. If not, what sub-goal should the inner loop agent work on next?";

        try
        {
            var rawResponse = await _llmClient.GetCompletionAsync(systemPrompt, userPrompt, modelName, customBaseUrl, customApiKey, forceJson: true);
            var cleanJson = ExtractJsonBlock(rawResponse);
            var decision = JsonSerializer.Deserialize<OuterLoopDecision>(cleanJson, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });

            return decision ?? new OuterLoopDecision { Status = "continue", Instructions = mainGoal };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in outer loop reasoning. Proceeding directly with main goal.");
            return new OuterLoopDecision { Status = "continue", Instructions = mainGoal };
        }
    }

    private async Task ExecuteActionAsync(IExecutionDriver driver, ScrapeAction action)
    {
        switch (action.Type.ToLower())
        {
            case "click_selector":
                if (string.IsNullOrEmpty(action.Selector)) throw new ArgumentException("Selector missing for click_selector action.");
                await driver.ClickSelectorAsync(action.Selector);
                break;
            case "click_coordinate":
                if (!action.X.HasValue || !action.Y.HasValue) throw new ArgumentException("Coordinates missing for click_coordinate action.");
                await driver.ClickCoordinateAsync(action.X.Value, action.Y.Value);
                break;
            case "type_text":
                if (string.IsNullOrEmpty(action.Selector) || action.Text == null) throw new ArgumentException("Selector or text missing for type_text action.");
                await driver.TypeTextAsync(action.Selector, action.Text);
                break;
            case "scroll":
                if (string.IsNullOrEmpty(action.Direction)) throw new ArgumentException("Direction missing for scroll action.");
                await driver.ScrollAsync(action.Direction);
                break;
            case "wait":
                await driver.WaitAsync(action.DurationMs ?? 2000);
                break;
            case "extract_data":
            case "fail":
                // Handled in runner loop
                break;
            default:
                _logger.LogWarning("Unknown action type: {Type}", action.Type);
                break;
        }
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

    private class OuterLoopDecision
    {
        public string Status { get; set; } = "continue"; // "continue", "completed", "failed"
        public string? Instructions { get; set; }
        public object? ExtractedData { get; set; }
        public string? Reason { get; set; }
    }
}
