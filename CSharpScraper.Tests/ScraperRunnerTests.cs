using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using CSharpScraper.Services;
using CSharpScraper.Models;
using CSharpScraper.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task RunJobAsync_ShouldSucceedWhenOuterLoopDeclaresCompleted()
    {
        // Arrange
        var job = new ScrapeJob { Url = "http://test.com", Goal = "Get data" };
        var request = new ScrapeRequest { Url = "http://test.com", Goal = "Get data", DriverType = "playwright", AgentType = "dom" };
        
        // Register mock driver and agent in ServiceProvider
        _mockServiceProvider.Setup(x => x.GetService(typeof(Services.Drivers.PlaywrightBrowserDriver))).Returns(_mockDriver.Object);
        _mockServiceProvider.Setup(x => x.GetService(typeof(Services.Agents.DomSelectorAgent))).Returns(_mockAgent.Object);

        // Mock Outer Loop response (completed status)
        var mockOuterResponse = "{ \"status\": \"completed\", \"extractedData\": { \"price\": \"$9.99\" } }";
        _mockLlmClient.Setup(x => x.GetCompletionAsync(
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.Is<bool>(b => b == true)
        )).ReturnsAsync(mockOuterResponse);

        var runner = new ScraperRunner(_mockLogger.Object, _mockLlmClient.Object, _mockServiceProvider.Object);

        // Act
        await runner.RunJobAsync(job, request, CancellationToken.None);

        // Assert
        Assert.AreEqual(JobStatus.Completed, job.Status);
        Assert.IsNotNull(job.ExtractedData);
        
        var json = JsonSerializer.Serialize(job.ExtractedData);
        Assert.IsTrue(json.Contains("$9.99"));

        _mockDriver.Verify(d => d.InitializeAsync(job.JobId), Times.Once);
        _mockDriver.Verify(d => d.NavigateAsync(job.Url), Times.Once);
        _mockDriver.Verify(d => d.CleanupAsync(), Times.Once);
    }

    [TestMethod]
    public async Task RunJobAsync_ShouldFailWhenOuterLoopDeclaresFailure()
    {
        // Arrange
        var job = new ScrapeJob { Url = "http://test.com", Goal = "Get data" };
        var request = new ScrapeRequest { Url = "http://test.com", Goal = "Get data", DriverType = "playwright", AgentType = "dom" };

        _mockServiceProvider.Setup(x => x.GetService(typeof(Services.Drivers.PlaywrightBrowserDriver))).Returns(_mockDriver.Object);
        _mockServiceProvider.Setup(x => x.GetService(typeof(Services.Agents.DomSelectorAgent))).Returns(_mockAgent.Object);

        // Mock Outer Loop response (failed status)
        var mockOuterResponse = "{ \"status\": \"failed\", \"reason\": \"site blocked by captcha\" }";
        _mockLlmClient.Setup(x => x.GetCompletionAsync(
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.Is<bool>(b => b == true)
        )).ReturnsAsync(mockOuterResponse);

        var runner = new ScraperRunner(_mockLogger.Object, _mockLlmClient.Object, _mockServiceProvider.Object);

        // Act
        await runner.RunJobAsync(job, request, CancellationToken.None);

        // Assert
        Assert.AreEqual(JobStatus.Failed, job.Status);
        Assert.AreEqual("site blocked by captcha", job.Error);
        _mockDriver.Verify(d => d.CleanupAsync(), Times.Once);
    }

    [TestMethod]
    public async Task RunJobAsync_ShouldRunInnerLoopStepsWhenOuterLoopContinues()
    {
        // Arrange
        var job = new ScrapeJob { Url = "http://test.com", Goal = "Get data", MaxSteps = 2 };
        var request = new ScrapeRequest { Url = "http://test.com", Goal = "Get data", DriverType = "playwright", AgentType = "dom", MaxSteps = 2 };

        _mockServiceProvider.Setup(x => x.GetService(typeof(Services.Drivers.PlaywrightBrowserDriver))).Returns(_mockDriver.Object);
        _mockServiceProvider.Setup(x => x.GetService(typeof(Services.Agents.DomSelectorAgent))).Returns(_mockAgent.Object);

        // First call to Outer Loop: continues and gives instructions
        var mockOuterResponse1 = "{ \"status\": \"continue\", \"instructions\": \"Find the shop button\" }";
        // Second call to Outer Loop: goal complete
        var mockOuterResponse2 = "{ \"status\": \"completed\", \"extractedData\": { \"success\": true } }";

        int callCount = 0;
        _mockLlmClient.Setup(x => x.GetCompletionAsync(
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.Is<bool>(b => b == true)
        )).ReturnsAsync(() => 
        {
            callCount++;
            return callCount == 1 ? mockOuterResponse1 : mockOuterResponse2;
        });

        // Mock Inner Loop Agent response
        var action = new ScrapeAction { Type = "click_selector", Selector = "[data-pg-id='4']" };
        var mockAgentStep = new ScrapeStepLog
        {
            StepNumber = 1,
            Thought = "Let's click shop",
            Action = action
        };

        _mockAgent.Setup(a => a.DecideNextActionAsync(
            It.Is<string>(g => g == "Find the shop button"),
            It.Is<int>(s => s == 1),
            It.IsAny<IExecutionDriver>(),
            It.IsAny<LlmClient>(),
            It.IsAny<string>(),
            It.IsAny<string>()
        )).ReturnsAsync(mockAgentStep);

        _mockDriver.Setup(d => d.ClickSelectorAsync(It.Is<string>(s => s == "[data-pg-id='4']"))).Returns(Task.CompletedTask);

        var runner = new ScraperRunner(_mockLogger.Object, _mockLlmClient.Object, _mockServiceProvider.Object);

        // Act
        await runner.RunJobAsync(job, request, CancellationToken.None);

        // Assert
        Assert.AreEqual(JobStatus.Completed, job.Status);
        Assert.AreEqual(2, job.CurrentStep); // Reached step 2 before completing
        Assert.AreEqual(1, job.Steps.Count);
        Assert.AreEqual("Let's click shop", job.Steps[0].Thought);
        Assert.AreEqual("[data-pg-id='4']", job.Steps[0].Action.Selector);

        _mockDriver.Verify(d => d.ClickSelectorAsync("[data-pg-id='4']"), Times.Once);
        _mockDriver.Verify(d => d.CleanupAsync(), Times.Once);
    }
}
