using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using CSharpScraper.Services;
using CSharpScraper.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace CSharpScraper.Tests;

[TestClass]
public class ScraperJobServiceTests
{
    private Mock<ILogger<ScraperJobService>> _mockLogger = null!;
    private Mock<IServiceProvider> _mockServiceProvider = null!;
    private Mock<IServiceScopeFactory> _mockScopeFactory = null!;
    private Mock<IServiceScope> _mockServiceScope = null!;
    private Mock<ScraperRunner> _mockScraperRunner = null!;
    private Mock<LlmClient> _mockLlmClient = null!;
    private Mock<SearxngClient> _mockSearxngClient = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<ScraperJobService>>();
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockServiceScope = new Mock<IServiceScope>();
        
        _mockLlmClient = new Mock<LlmClient>(null!, null!, null!);

        var mockHttp = new Mock<IHttpClientFactory>();
        var mockConfig = new Mock<IConfiguration>();
        var mockSearxLogger = new Mock<ILogger<SearxngClient>>();
        _mockSearxngClient = new Mock<SearxngClient>(mockHttp.Object, mockConfig.Object, mockSearxLogger.Object);

        // Set up service provider scoping structure
        _mockServiceProvider.Setup(x => x.GetService(typeof(IServiceScopeFactory))).Returns(_mockScopeFactory.Object);
        _mockScopeFactory.Setup(x => x.CreateScope()).Returns(_mockServiceScope.Object);
        _mockServiceScope.Setup(x => x.ServiceProvider).Returns(_mockServiceProvider.Object);
    }

    [TestMethod]
    public void StartJob_ShouldCreateJobAndEnqueueBackgroundExecution()
    {
        // Arrange
        var mockRunnerLogger = new Mock<ILogger<ScraperRunner>>();
        _mockScraperRunner = new Mock<ScraperRunner>(mockRunnerLogger.Object, _mockLlmClient.Object, _mockServiceProvider.Object);

        // Resolve runner in DI scope
        _mockServiceProvider.Setup(x => x.GetService(typeof(ScraperRunner))).Returns(_mockScraperRunner.Object);

        var service = new ScraperJobService(_mockLogger.Object, _mockServiceProvider.Object, _mockLlmClient.Object, _mockSearxngClient.Object);
        var request = new ScrapeRequest
        {
            Url = "https://example.com",
            Goal = "Scrape titles",
            MaxSteps = 5
        };

        // Act
        var job = service.StartJob(request);

        // Assert
        Assert.IsNotNull(job);
        Assert.AreEqual(request.Url, job.Url);
        Assert.AreEqual(request.Goal, job.Goal);
        Assert.AreEqual(JobStatus.Queued, job.Status);
        
        var retrievedJob = service.GetJob(job.JobId);
        Assert.AreSame(job, retrievedJob);
    }

    [TestMethod]
    public async Task StopJob_ShouldCancelActiveJob()
    {
        // Arrange
        var mockRunnerLogger = new Mock<ILogger<ScraperRunner>>();
        
        // Setup a mock runner that delay-sleeps to simulate a running job
        _mockScraperRunner = new Mock<ScraperRunner>(mockRunnerLogger.Object, _mockLlmClient.Object, _mockServiceProvider.Object);
        
        _mockScraperRunner.Setup(r => r.RunJobAsync(It.IsAny<ScrapeJob>(), It.IsAny<ScrapeRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (ScrapeJob j, ScrapeRequest r, CancellationToken t) =>
            {
                j.Status = JobStatus.Running;
                await Task.Delay(5000, t);
            });

        _mockServiceProvider.Setup(x => x.GetService(typeof(ScraperRunner))).Returns(_mockScraperRunner.Object);

        var service = new ScraperJobService(_mockLogger.Object, _mockServiceProvider.Object, _mockLlmClient.Object, _mockSearxngClient.Object);
        var request = new ScrapeRequest { Url = "https://example.com", Goal = "Goal" };

        var job = service.StartJob(request);
        
        // Artificially change status to Running to simulate background task update
        job.Status = JobStatus.Running;

        // Act
        var stopped = service.StopJob(job.JobId);

        // Assert
        Assert.IsTrue(stopped);
        Assert.AreEqual(JobStatus.Stopped, job.Status);
    }
}
