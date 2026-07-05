using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using CSharpScraper.Services;
using CSharpScraper.Models;
using CSharpScraper.Interfaces;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace CSharpScraper.Tests;

[TestClass]
public class ScraperRunnerTests
{
    private Mock<ILogger<ScraperRunner>> _mockLogger = null!;
    private Mock<LlmClient> _mockLlmClient = null!;
    private Mock<IServiceProvider> _mockServiceProvider = null!;
    private Mock<Services.Drivers.PlaywrightBrowserDriver> _mockDriver = null!;
    private Mock<Services.Agents.DomSelectorAgent> _mockAgent = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<ScraperRunner>>();
        _mockLlmClient = new Mock<LlmClient>(null!, null!, null!);
        _mockServiceProvider = new Mock<IServiceProvider>();
        
        var mockDriverLogger = new Mock<ILogger<Services.Drivers.PlaywrightBrowserDriver>>();
        _mockDriver = new Mock<Services.Drivers.PlaywrightBrowserDriver>(mockDriverLogger.Object);

        var mockAgentLogger = new Mock<ILogger<Services.Agents.DomSelectorAgent>>();
        _mockAgent = new Mock<Services.Agents.DomSelectorAgent>(mockAgentLogger.Object);

        // Set up driver methods
        _mockDriver.Setup(d => d.InitializeAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        _mockDriver.Setup(d => d.NavigateAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        _mockDriver.Setup(d => d.GetUrlAsync()).ReturnsAsync("http://test.com");
        _mockDriver.Setup(d => d.GetTitleAsync()).ReturnsAsync("Test Title");
        _mockDriver.Setup(d => d.CaptureScreenshotAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync("step_01.png");
        _mockDriver.Setup(d => d.WaitAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        _mockDriver.Setup(d => d.CleanupAsync()).Returns(Task.CompletedTask);
    }

    [TestMethod]
    public async Task RunJobAsync_ShouldSucceedWhenAgentExtractsData()
    {
        // Arrange
        var job = new ScrapeJob { Url = "http://test.com", Goal = "Get data" };
        var request = new ScrapeRequest { Url = "http://test.com", Goal = "Get data", DriverType = "playwright", AgentType = "dom" };
        
        _mockServiceProvider.Setup(x => x.GetService(typeof(Services.Drivers.PlaywrightBrowserDriver))).Returns(_mockDriver.Object);
        _mockServiceProvider.Setup(x => x.GetService(typeof(Services.Agents.DomSelectorAgent))).Returns(_mockAgent.Object);

        var elementDataJson = JsonSerializer.Deserialize<JsonElement>("{\"price\": \"$9.99\"}");
        var mockStep = new ScrapeStepLog
        {
            StepNumber = 1,
            Thought = "Goal accomplished",
            Action = new ScrapeAction { Type = "extract_data", Data = elementDataJson },
            PromptTokens = 10,
            CompletionTokens = 5
        };

        _mockAgent.Setup(a => a.DecideNextActionAsync(
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<IExecutionDriver>(),
            It.IsAny<LlmClient>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()
        )).ReturnsAsync(mockStep);

        var runner = new ScraperRunner(_mockLogger.Object, _mockLlmClient.Object, _mockServiceProvider.Object);

        // Act
        await runner.RunJobAsync(job, request, CancellationToken.None);

        // Assert
        Assert.AreEqual(JobStatus.Completed, job.Status);
        Assert.IsNotNull(job.ExtractedData);
        Assert.AreEqual(10, job.TotalPromptTokens);
        Assert.AreEqual(5, job.TotalCompletionTokens);
        
        var json = JsonSerializer.Serialize(job.ExtractedData);
        Assert.IsTrue(json.Contains("$9.99"));

        _mockDriver.Verify(d => d.InitializeAsync(job.JobId), Times.Once);
        _mockDriver.Verify(d => d.NavigateAsync(job.Url), Times.Once);
        _mockDriver.Verify(d => d.CleanupAsync(), Times.Once);
    }

    [TestMethod]
    public async Task RunJobAsync_ShouldFailWhenAgentDeclaresFailure()
    {
        // Arrange
        var job = new ScrapeJob { Url = "http://test.com", Goal = "Get data" };
        var request = new ScrapeRequest { Url = "http://test.com", Goal = "Get data", DriverType = "playwright", AgentType = "dom" };

        _mockServiceProvider.Setup(x => x.GetService(typeof(Services.Drivers.PlaywrightBrowserDriver))).Returns(_mockDriver.Object);
        _mockServiceProvider.Setup(x => x.GetService(typeof(Services.Agents.DomSelectorAgent))).Returns(_mockAgent.Object);

        var mockStep = new ScrapeStepLog
        {
            StepNumber = 1,
            Thought = "Stuck on captcha",
            Action = new ScrapeAction { Type = "fail", Reason = "site blocked by captcha" },
            PromptTokens = 8,
            CompletionTokens = 4
        };

        _mockAgent.Setup(a => a.DecideNextActionAsync(
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<IExecutionDriver>(),
            It.IsAny<LlmClient>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()
        )).ReturnsAsync(mockStep);

        var runner = new ScraperRunner(_mockLogger.Object, _mockLlmClient.Object, _mockServiceProvider.Object);

        // Act
        await runner.RunJobAsync(job, request, CancellationToken.None);

        // Assert
        Assert.AreEqual(JobStatus.Failed, job.Status);
        Assert.AreEqual("site blocked by captcha", job.Error);
        Assert.AreEqual(8, job.TotalPromptTokens);
        Assert.AreEqual(4, job.TotalCompletionTokens);
        _mockDriver.Verify(d => d.CleanupAsync(), Times.Once);
    }

    [TestMethod]
    public async Task RunJobAsync_ShouldExecuteActionsAndIterate()
    {
        // Arrange
        var job = new ScrapeJob { Url = "http://test.com", Goal = "Get data", MaxSteps = 2 };
        var request = new ScrapeRequest { Url = "http://test.com", Goal = "Get data", DriverType = "playwright", AgentType = "dom", MaxSteps = 2 };

        _mockServiceProvider.Setup(x => x.GetService(typeof(Services.Drivers.PlaywrightBrowserDriver))).Returns(_mockDriver.Object);
        _mockServiceProvider.Setup(x => x.GetService(typeof(Services.Agents.DomSelectorAgent))).Returns(_mockAgent.Object);

        var step1 = new ScrapeStepLog
        {
            StepNumber = 1,
            Thought = "Click the button",
            Action = new ScrapeAction { Type = "click_selector", Selector = "[data-pg-id='4']" },
            PromptTokens = 15,
            CompletionTokens = 8
        };

        var elementDataJson = JsonSerializer.Deserialize<JsonElement>("{\"success\": true}");
        var step2 = new ScrapeStepLog
        {
            StepNumber = 2,
            Thought = "Goal achieved",
            Action = new ScrapeAction { Type = "extract_data", Data = elementDataJson },
            PromptTokens = 20,
            CompletionTokens = 10
        };

        int callCount = 0;
        _mockAgent.Setup(a => a.DecideNextActionAsync(
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<IExecutionDriver>(),
            It.IsAny<LlmClient>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()
        )).ReturnsAsync(() =>
        {
            callCount++;
            return callCount == 1 ? step1 : step2;
        });

        _mockDriver.Setup(d => d.ClickSelectorAsync("[data-pg-id='4']")).Returns(Task.CompletedTask);

        var runner = new ScraperRunner(_mockLogger.Object, _mockLlmClient.Object, _mockServiceProvider.Object);

        // Act
        await runner.RunJobAsync(job, request, CancellationToken.None);

        // Assert
        Assert.AreEqual(JobStatus.Completed, job.Status);
        Assert.AreEqual(2, job.CurrentStep);
        Assert.AreEqual(2, job.Steps.Count);
        Assert.AreEqual("Click the button", job.Steps[0].Thought);
        Assert.AreEqual("[data-pg-id='4']", job.Steps[0].Action.Selector);
        
        Assert.AreEqual(35, job.TotalPromptTokens); // 15 + 20
        Assert.AreEqual(18, job.TotalCompletionTokens); // 8 + 10

        _mockDriver.Verify(d => d.ClickSelectorAsync("[data-pg-id='4']"), Times.Once);
        _mockDriver.Verify(d => d.CleanupAsync(), Times.Once);
    }
}
