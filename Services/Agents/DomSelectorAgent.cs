using System.Text.Json;
using CSharpScraper.Interfaces;
using CSharpScraper.Models;
using CSharpScraper.Utils;
using Microsoft.Extensions.Logging;

namespace CSharpScraper.Services.Agents;

public class DomSelectorAgent : IInnerLoopAgent
{
    private readonly ILogger<DomSelectorAgent> _logger;

    public DomSelectorAgent(ILogger<DomSelectorAgent> logger)
    {
        _logger = logger;
    }

    public virtual async Task<ScrapeStepLog> DecideNextActionAsync(
        string goal, 
        int stepNumber, 
        IExecutionDriver driver, 
        LlmClient llmClient, 
        string modelName, 
        string historySummary,
        string? customBaseUrl = null,
        string? customApiKey = null)
    {
        _logger.LogInformation("DomSelectorAgent deciding next action for step {StepNumber}", stepNumber);

        // 1. Gather page state
        var url = await driver.GetUrlAsync();
        var title = await driver.GetTitleAsync();
        var htmlContent = await driver.GetPageContentAsync();

        // 2. Parse elements using JS script
        string elementsJson;
        if (driver is Drivers.PlaywrightBrowserDriver playwrightDriver)
        {
            // Direct script evaluation
            // Execute parse script
            elementsJson = await GetElementsJsonFromBrowser(driver);
        }
        else
        {
            // Standard fallback (though we expect Playwright for DomSelector)
            elementsJson = "[]";
        }

        List<DomParser.ElementInfo> elements;
        try
        {
            elements = JsonSerializer.Deserialize<List<DomParser.ElementInfo>>(elementsJson, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
            }) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize DOM elements JSON.");
            elements = new();
        }

        var formattedElements = DomParser.FormatElementsToXml(elements);

        // 3. Assemble Prompts
        var systemPrompt = @"You are an autonomous browser automation agent. Your job is to navigate the webpage and perform actions to achieve the user's scraping goal.

You are interacting with the page using browser commands. The page is parsed and represented as a list of interactive or structural HTML elements below, each labeled with a unique `pg-id` attribute and a bounding box `bbox=""[x, y, width, height]""`.

You can perform one of the following actions at each step:
1. Click a selector (use for buttons, links, inputs):
   { ""type"": ""click_selector"", ""selector"": ""[data-pg-id='ID']"" }
2. Click coordinates (use if you want to click a specific spot, e.g. based on bbox center):
   { ""type"": ""click_coordinate"", ""x"": X, ""y"": Y }
3. Type text into an input:
   { ""type"": ""type_text"", ""selector"": ""[data-pg-id='ID']"", ""text"": ""your text"" }
4. Scroll page:
   { ""type"": ""scroll"", ""direction"": ""down"" } or { ""type"": ""scroll"", ""direction"": ""up"" }
5. Wait for page loads or actions to complete:
   { ""type"": ""wait"", ""durationMs"": 2000 }
6. Complete goal and extract target structured data (only do this when you have successfully collected the requested data):
   { ""type"": ""extract_data"", ""data"": { ""your_extracted_field"": ""value"" } }
7. Declare failure (if the page shows errors, captcha you cannot bypass, or the goal is impossible):
   { ""type"": ""fail"", ""reason"": ""explanation"" }

CRITICAL RULES:
- Choose elements ONLY by their `data-pg-id` matching what is currently visible on the screen.
- If you need to click a link, button, or focus an input, use `click_selector` with `[data-pg-id='ID']`.
- Provide your output in STRCIT JSON format. No extra text before or after the JSON block.

Expected JSON output format:
{
  ""thought"": ""A brief explanation of what you see and what you need to do next."",
  ""action"": {
    ""type"": ""click_selector"",
    ""selector"": ""[data-pg-id='4']""
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
---
Current Page Elements:
{formattedElements}

Reason about the current page, look at your history and goal, and decide the next action.";

        var llmResponse = await llmClient.GetCompletionAsync(systemPrompt, userPrompt, modelName, customBaseUrl, customApiKey, forceJson: true);
        var rawResponse = llmResponse.Content;
        
        _logger.LogDebug("LLM response for step {Step}: {Response}", stepNumber, rawResponse);

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
            _logger.LogError(ex, "Failed to parse agent decision JSON. Raw content: {Raw}", rawResponse);
            
            // Fallback parsing (in case model outputted nested string or triple-backticks)
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
                // Create a wait action if we fail to parse
                decision = new AgentDecision
                {
                    Thought = "Failed to parse model decision. Retrying with a short wait.",
                    Action = new ScrapeAction { Type = "wait", DurationMs = 2000 }
                };
            }
        }

        if (decision == null || decision.Action == null)
        {
            decision = new AgentDecision
            {
                Thought = "Model returned empty action. Waiting.",
                Action = new ScrapeAction { Type = "wait", DurationMs = 1000 }
            };
        }

        return new ScrapeStepLog
        {
            StepNumber = stepNumber,
            Thought = decision.Thought ?? "No thought recorded.",
            Action = decision.Action,
            PromptTokens = llmResponse.PromptTokens,
            CompletionTokens = llmResponse.CompletionTokens
        };
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

    private async Task<string> GetElementsJsonFromBrowser(IExecutionDriver driver)
    {
        return await driver.EvaluateScriptAsync(DomParser.ParseScript);
    }

    private class AgentDecision
    {
        public string? Thought { get; set; }
        public ScrapeAction? Action { get; set; }
    }
}
