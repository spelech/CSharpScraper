using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using CSharpScraper.Services.Agents;
using CSharpScraper.Services;
using CSharpScraper.Interfaces;
using CSharpScraper.Models;
using CSharpScraper.Utils;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace CSharpScraper.Tests;

[TestClass]
public class DomSelectorAgentTests
{
    private Mock<ILogger<DomSelectorAgent>> _mockLogger = null!;
    private Mock<IExecutionDriver> _mockDriver = null!;
    private Mock<LlmClient> _mockLlmClient = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<DomSelectorAgent>>();
        _mockDriver = new Mock<IExecutionDriver>();
        _mockLlmClient = new Mock<LlmClient>(null!, null!, null!);

        _mockDriver.Setup(d => d.GetUrlAsync()).ReturnsAsync("http://test.com");
        _mockDriver.Setup(d => d.GetTitleAsync()).ReturnsAsync("Test Title");
        _mockDriver.Setup(d => d.GetPageContentAsync()).ReturnsAsync("<html><body></body></html>");
    }

    [TestMethod]
    public async Task DecideNextActionAsync_ShouldParseStandardJsonLlmResponse()
    {
        // Arrange
        var mockElementsJson = @"[
            { ""pgId"": 1, ""tagName"": ""button"", ""isControl"": true, ""text"": ""Login"", ""attributes"": {}, ""boundingBox"": { ""x"": 0, ""y"": 0, ""width"": 50, ""height"": 20 } }
        ]";

        _mockDriver.Setup(d => d.EvaluateScriptAsync(DomParser.ParseScript)).ReturnsAsync(mockElementsJson);

        // LLM returns raw JSON
        var llmResponse = @"{ ""thought"": ""Need to click login"", ""action"": { ""type"": ""click_selector"", ""selector"": ""[data-pg-id='1']"" } }";
        
        _mockLlmClient.Setup(x => x.GetCompletionAsync(
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<bool>()
        )).ReturnsAsync(new LlmResponse { Content = llmResponse, PromptTokens = 100, CompletionTokens = 50 });

        var agent = new DomSelectorAgent(_mockLogger.Object);

        // Act
        var result = await agent.DecideNextActionAsync("Goal", 1, _mockDriver.Object, _mockLlmClient.Object, "model", "history");

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("Need to click login", result.Thought);
        Assert.AreEqual("click_selector", result.Action.Type);
        Assert.AreEqual("[data-pg-id='1']", result.Action.Selector);
    }

    [TestMethod]
    public async Task DecideNextActionAsync_ShouldHandleMarkdownWrappedJsonLlmResponse()
    {
        // Arrange
        var mockElementsJson = "[]";
        _mockDriver.Setup(d => d.EvaluateScriptAsync(DomParser.ParseScript)).ReturnsAsync(mockElementsJson);

        // LLM returns JSON wrapped in markdown triple-backticks
        var llmResponse = @"
Some leading conversational text that should be ignored.
```json
{
  ""thought"": ""Let us wait for 2 seconds"",
  ""action"": {
    ""type"": ""wait"",
    ""durationMs"": 2000
  }
}
```
Some trailing text.
";

        _mockLlmClient.Setup(x => x.GetCompletionAsync(
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<bool>()
        )).ReturnsAsync(new LlmResponse { Content = llmResponse, PromptTokens = 80, CompletionTokens = 40 });

        var agent = new DomSelectorAgent(_mockLogger.Object);

        // Act
        var result = await agent.DecideNextActionAsync("Goal", 1, _mockDriver.Object, _mockLlmClient.Object, "model", "history");

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("Let us wait for 2 seconds", result.Thought);
        Assert.AreEqual("wait", result.Action.Type);
        Assert.AreEqual(2000, result.Action.DurationMs);
    }
}
