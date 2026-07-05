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
            
            // Default model from request or env vars
            var model = request.Model ?? "gemini-3.5-flash";

            _logger.LogInformation("Job {JobId}: Using Model={Model}", job.JobId, model);

            for (int step = 1; step <= job.MaxSteps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                job.CurrentStep = step;

                // --- SINGLE-LOOP EXECUTION ---
                // The agent observes the page state, checks goal progression, and decides on a browser interaction or data extraction.
                job.LastAction = $"Planning step {step}...";
                var stepLog = await agent.DecideNextActionAsync(
                    job.Goal, 
                    step, 
                    driver, 
                    _llmClient, 
                    model, 
                    historySummary.ToString(),
                    request.BaseUrl,
                    request.ApiKey
                );

                job.TotalPromptTokens += stepLog.PromptTokens;
                job.TotalCompletionTokens += stepLog.CompletionTokens;

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
                    _logger.LogInformation("Job {JobId}: Streamlined loop extracted data and completed.", job.JobId);
                    job.Status = JobStatus.Completed;
                    
                    if (stepLog.Action.Data.HasValue)
                    {
                        job.ExtractedData = stepLog.Action.Data.Value;
                    }
                    break;
                }
                else if (stepLog.Action.Type == "fail")
                {
                    _logger.LogWarning("Job {JobId}: Streamlined loop failed. Reason: {Reason}", job.JobId, stepLog.Action.Reason);
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


}
