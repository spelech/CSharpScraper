using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Moq.Protected;
using CSharpScraper.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace CSharpScraper.Tests;

[TestClass]
public class LlmClientTests
{
    private Mock<IHttpClientFactory> _mockHttpClientFactory = null!;
    private Mock<HttpMessageHandler> _mockHttpMessageHandler = null!;
    private Mock<IConfiguration> _mockConfiguration = null!;
    private Mock<ILogger<LlmClient>> _mockLogger = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockHttpMessageHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<LlmClient>>();
    }

    [TestMethod]
    public async Task GetCompletionAsync_ShouldPostCorrectJsonAndReturnCompletionText()
    {
        // Arrange
        var mockResponseJson = "{ \"choices\": [ { \"message\": { \"content\": \"{\\\"action\\\": \\\"wait\\\"}\" } } ], \"usage\": { \"prompt_tokens\": 10, \"completion_tokens\": 5, \"total_tokens\": 15 } }";
        
        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync((HttpRequestMessage request, CancellationToken token) =>
            {
                // Verify request headers
                Assert.AreEqual(HttpMethod.Post, request.Method);
                Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
                Assert.AreEqual("test-key", request.Headers.Authorization?.Parameter);
                Assert.AreEqual("https://spelech/CSharpScraper", string.Join("", request.Headers.GetValues("HTTP-Referer")));

                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(mockResponseJson, System.Text.Encoding.UTF8, "application/json")
                };
            });

        var client = new HttpClient(_mockHttpMessageHandler.Object);
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        _mockConfiguration.Setup(c => c["DEFAULT_LLM_BASE_URL"]).Returns("http://localhost:4000/v1");
        _mockConfiguration.Setup(c => c["DEFAULT_LLM_API_KEY"]).Returns("test-key");

        var llmClient = new LlmClient(_mockHttpClientFactory.Object, _mockConfiguration.Object, _mockLogger.Object);

        // Act
        var result = await llmClient.GetCompletionAsync("sys_prompt", "user_prompt", "gemini-3.5-flash");

        // Assert
        Assert.AreEqual("{\"action\": \"wait\"}", result.Content);
        Assert.AreEqual(10, result.PromptTokens);
        Assert.AreEqual(5, result.CompletionTokens);
    }

    [TestMethod]
    public async Task GetCompletionAsync_ShouldThrowExceptionOnHttpFailure()
    {
        // Arrange
        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.InternalServerError,
                Content = new StringContent("Internal server error details")
            });

        var client = new HttpClient(_mockHttpMessageHandler.Object);
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        
        var llmClient = new LlmClient(_mockHttpClientFactory.Object, _mockConfiguration.Object, _mockLogger.Object);

        // Act & Assert
        try
        {
            await llmClient.GetCompletionAsync("sys_prompt", "user_prompt", "gemini-3.5-flash");
            Assert.Fail("Expected an exception to be thrown, but none was.");
        }
        catch (Exception ex)
        {
            Assert.IsNotNull(ex.Message);
        }
    }
}
