using System.Text.Json;
using CSharpScraper.Interfaces;
using CSharpScraper.Models;
using Microsoft.Extensions.Logging;
using System.IO;

namespace CSharpScraper.Services.Agents;

public class VisualCoordinateAgent : IInnerLoopAgent
{
    private readonly ILogger<VisualCoordinateAgent> _logger;

    public VisualCoordinateAgent(ILogger<VisualCoordinateAgent> logger)
    {
        _logger = logger;
    }

    public virtual async Task<ScrapeStepLog> DecideNextActionAsync(
        string goal, 
        int stepNumber, 
        IExecutionDriver driver, 
        LlmClient llmClient, 
        string modelName, 
        string historySummary)
    {
        _logger.LogInformation("VisualCoordinateAgent deciding next action for step {StepNumber}", stepNumber);

        // 1. Gather page context details
        var url = await driver.GetUrlAsync();
        var title = await driver.GetTitleAsync();

        // 2. Capture screenshot to a temp location and read it
        var tempFolder = Path.Combine(Directory.GetCurrentDirectory(), "temp_screenshots");
        var screenshotPath = await driver.CaptureScreenshotAsync(tempFolder, stepNumber);
        
        var bytes = await File.ReadAllBytesAsync(screenshotPath);
        var base64Image = Convert.ToBase64String(bytes);

        // 3. Assemble Prompts
        var systemPrompt = @"You are an autonomous computer-use agent. Your job is to navigate the screen and perform actions to achieve the user's goal.

You are interacting with a web browser page of dimensions 1280x800. You see the screen in the attached screenshot.

You can perform one of the following actions at each step:
1. Click a coordinate:
   { ""type"": ""click_coordinate"", ""x"": X, ""y"": Y }
2. Type text (this will type text into the currently active/focused input box):
   { ""type"": ""type_text"", ""selector"": ""body"", ""text"": ""your text"" } (Note: Always click the coordinate of the input box FIRST to focus it, then use this type action in your next step).
3. Scroll:
   { ""type"": ""scroll"", ""direction"": ""down"" } or { ""type"": ""scroll"", ""direction"": ""up"" }
4. Wait:
   { ""type"": ""wait"", ""durationMs"": 2000 }
5. Complete goal and extract target structured data (only do this when you have successfully collected/read the requested data from the screen):
   { ""type"": ""extract_data"", ""data"": { ""your_extracted_field"": ""value"" } }
6. Declare failure:
   { ""type"": ""fail"", ""reason"": ""explanation"" }

CRITICAL RULES:
- Identify the exact coordinates (X, Y) of inputs, buttons, and links visually from the screenshot.
- X must be between 0 and 1280. Y must be between 0 and 800.
- Provide your output in STRICT JSON format. No extra text before or after the JSON block.

Expected JSON output format:
{
  ""thought"": ""A brief explanation of what you see in the screenshot and what coordinate you want to click next."",
  ""action"": {
    ""type"": ""click_coordinate"",
    ""x"": 340,
    ""y"": 450
  }
}";

        var userPrompt = $@"Goal: {goal}
Current Step: {stepNumber}

---
Page URL: {url}
Page Title: {title}
---
History of Actions Taken So Far:
{historySummary}

Look at the screenshot and history, reason about where to click or what to do next, and respond with your action.";

        // 4. Query Vision LLM
        var rawResponse = await llmClient.GetVisionCompletionAsync(systemPrompt, userPrompt, base64Image, modelName, forceJson: true);
        
        _logger.LogDebug("Vision LLM response for step {Step}: {Response}", stepNumber, rawResponse);

        // 5. Parse action response
        AgentDecision? decision = null;
        try
        {
            decision = JsonSerializer.Deserialize<AgentDecision>(rawResponse, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse visual agent decision JSON. Raw content: {Raw}", rawResponse);
            try
            {
                var cleanJson = ExtractJsonBlock(rawResponse);
                decision = JsonSerializer.Deserialize<AgentDecision>(cleanJson, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });
            }
            catch
            {
                decision = new AgentDecision
                {
                    Thought = "Failed to parse visual model decision. Waiting.",
                    Action = new ScrapeAction { Type = "wait", DurationMs = 2000 }
                };
            }
        }

        if (decision == null || decision.Action == null)
        {
            decision = new AgentDecision
            {
                Thought = "Visual model returned empty action. Waiting.",
                Action = new ScrapeAction { Type = "wait", DurationMs = 1000 }
            };
        }

        var log = new ScrapeStepLog
        {
            StepNumber = stepNumber,
            Thought = decision.Thought ?? "No thought recorded.",
            Action = decision.Action,
            ScreenshotPath = screenshotPath
        };

        return log;
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

    private class AgentDecision
    {
        public string? Thought { get; set; }
        public ScrapeAction? Action { get; set; }
    }
}
