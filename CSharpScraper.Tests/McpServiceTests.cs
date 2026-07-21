using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using CSharpScraper.Services;
using CSharpScraper.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace CSharpScraper.Tests;

[TestClass]
public class McpServiceTests
{
    private Mock<ILogger<McpService>> _mockLogger = null!;
    private ScraperJobService _jobService = null!;
    private McpService _mcpService = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<McpService>>();
        
        var mockJobLogger = new Mock<ILogger<ScraperJobService>>();
        var mockSearxngLogger = new Mock<ILogger<SearxngClient>>();
        var mockLlmLogger = new Mock<ILogger<LlmClient>>();
        var mockConfig = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
        var mockHttp = new Mock<System.Net.Http.IHttpClientFactory>();
        var mockServiceProvider = new Mock<System.IServiceProvider>();

        var llmClient = new LlmClient(mockHttp.Object, mockConfig.Object, mockLlmLogger.Object);
        var searxngClient = new SearxngClient(mockHttp.Object, mockConfig.Object, mockSearxngLogger.Object);

        _jobService = new ScraperJobService(mockJobLogger.Object, mockServiceProvider.Object, llmClient, searxngClient);
        _mcpService = new McpService(_jobService, _mockLogger.Object);
    }

    [TestMethod]
    public async Task HandleRequestAsync_ServerDiscover_Returns2026_07_28_Capabilities()
    {
        var request = new McpRequest { Method = "server/discover", Id = 1 };
        var response = await _mcpService.HandleRequestAsync(request);

        Assert.IsNotNull(response.Result);
        var discoverResult = response.Result as McpDiscoverResult;
        Assert.IsNotNull(discoverResult);
        Assert.AreEqual("2026-07-28", discoverResult.ProtocolVersion);
        Assert.IsNotNull(discoverResult.Capabilities.Tools);
        Assert.IsNotNull(discoverResult.Capabilities.Prompts);
        Assert.IsNotNull(discoverResult.Capabilities.Resources);
        Assert.IsNotNull(discoverResult.Capabilities.Tasks);
        Assert.IsNotNull(discoverResult.Capabilities.Completions);
    }

    [TestMethod]
    public async Task HandleRequestAsync_PromptsList_ReturnsTemplates()
    {
        var request = new McpRequest { Method = "prompts/list", Id = 2 };
        var response = await _mcpService.HandleRequestAsync(request);

        Assert.IsNotNull(response.Result);
        var result = response.Result as McpListPromptsResult;
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Prompts.Count >= 3);
        Assert.IsTrue(result.Prompts.Exists(p => p.Name == "e_commerce_scrape"));
    }

    [TestMethod]
    public async Task HandleRequestAsync_PromptsGet_ReturnsFormattedPromptMessage()
    {
        var argsJson = JsonSerializer.SerializeToElement(new
        {
            name = "e_commerce_scrape",
            arguments = new Dictionary<string, string> { { "url", "https://example.com/shoes" } }
        });

        var request = new McpRequest { Method = "prompts/get", Params = argsJson, Id = 3 };
        var response = await _mcpService.HandleRequestAsync(request);

        Assert.IsNotNull(response.Result);
        var result = response.Result as McpGetPromptResult;
        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Messages.Count);
        Assert.IsTrue(result.Messages[0].Content.Text!.Contains("https://example.com/shoes"));
    }

    [TestMethod]
    public async Task HandleRequestAsync_ResourceTemplatesList_ReturnsTemplates()
    {
        var request = new McpRequest { Method = "resources/templates/list", Id = 4 };
        var response = await _mcpService.HandleRequestAsync(request);

        Assert.IsNotNull(response.Result);
        var result = response.Result as McpListResourceTemplatesResult;
        Assert.IsNotNull(result);
        Assert.IsTrue(result.ResourceTemplates.Count >= 4);
        Assert.IsTrue(result.ResourceTemplates.Exists(t => t.UriTemplate == "scraper://jobs/{jobId}"));
    }

    [TestMethod]
    public async Task HandleRequestAsync_CompletionComplete_ReturnsAutocompleteMatches()
    {
        var argsJson = JsonSerializer.SerializeToElement(new
        {
            refObj = new { type = "ref/prompt", name = "e_commerce_scrape" },
            argument = new { name = "model", value = "gemini" }
        });

        var request = new McpRequest { Method = "completion/complete", Params = argsJson, Id = 5 };
        var response = await _mcpService.HandleRequestAsync(request);

        Assert.IsNotNull(response.Result);
        var result = response.Result as McpCompleteResult;
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Completion.Values.Contains("gemini-3.5-flash"));
    }
}
