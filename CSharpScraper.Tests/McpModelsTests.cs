using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;
using CSharpScraper.Models;

namespace CSharpScraper.Tests;

[TestClass]
public class McpModelsTests
{
    [TestMethod]
    public void McpRequest_Serialization_ShouldProduceCorrectJson()
    {
        // Arrange
        var request = new McpRequest
        {
            Method = "tools/call",
            Id = 1,
            Params = JsonSerializer.SerializeToElement(new { name = "test-tool" })
        };

        // Act
        var json = JsonSerializer.Serialize(request);

        // Assert
        Assert.IsTrue(json.Contains("\"method\":\"tools/call\""));
        Assert.IsTrue(json.Contains("\"id\":1"));
        Assert.IsTrue(json.Contains("\"name\":\"test-tool\""));
    }

    [TestMethod]
    public void McpResponse_Serialization_ShouldFilterNullProperties()
    {
        // Arrange
        var response = new McpResponse
        {
            Id = 1,
            Result = new { status = "success" }
        };

        // Act
        var json = JsonSerializer.Serialize(response);

        // Assert
        Assert.IsTrue(json.Contains("\"result\":{\"status\":\"success\"}"));
        Assert.IsFalse(json.Contains("\"error\""));
    }
}
