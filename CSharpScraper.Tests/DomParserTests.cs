using Microsoft.VisualStudio.TestTools.UnitTesting;
using CSharpScraper.Utils;
using System.Collections.Generic;

namespace CSharpScraper.Tests;

[TestClass]
public class DomParserTests
{
    [TestMethod]
    public void FormatElementsToXml_ShouldGenerateValidXmlString()
    {
        // Arrange
        var elements = new List<DomParser.ElementInfo>
        {
            new()
            {
                PgId = 1,
                TagName = "button",
                IsControl = true,
                Text = "Click Me <Test>",
                Attributes = new Dictionary<string, string> { { "type", "submit" } },
                BoundingBox = new DomParser.BoundingBox { X = 10, Y = 20, Width = 100, Height = 40 }
            },
            new()
            {
                PgId = 2,
                TagName = "span",
                IsControl = false,
                Text = "Some header text",
                Attributes = new Dictionary<string, string>(),
                BoundingBox = new DomParser.BoundingBox { X = 50, Y = 100, Width = 200, Height = 30 }
            }
        };

        // Act
        var xmlResult = DomParser.FormatElementsToXml(elements);

        // Assert
        Assert.IsTrue(xmlResult.Contains("<page_elements>"));
        Assert.IsTrue(xmlResult.Contains("</page_elements>"));
        
        // Check element 1 (control button)
        Assert.IsTrue(xmlResult.Contains("<button pg-id=\"1\" type=\"submit\" bbox=\"[10,20,100,40]\">Click Me &lt;Test&gt;</button>"));
        
        // Check element 2 (non-control span with text)
        Assert.IsTrue(xmlResult.Contains("<span pg-id=\"2\" bbox=\"[50,100,200,30]\">Some header text</span>"));
    }

    [TestMethod]
    public void FormatElementsToXml_ShouldSkipNonControlWithEmptyText()
    {
        // Arrange
        var elements = new List<DomParser.ElementInfo>
        {
            new()
            {
                PgId = 1,
                TagName = "div",
                IsControl = false,
                Text = "", // Empty text structural element
                Attributes = new Dictionary<string, string>(),
                BoundingBox = new DomParser.BoundingBox { X = 0, Y = 0, Width = 100, Height = 100 }
            }
        };

        // Act
        var xmlResult = DomParser.FormatElementsToXml(elements);

        // Assert
        Assert.IsTrue(xmlResult.Contains("<page_elements>"));
        Assert.IsTrue(xmlResult.Contains("</page_elements>"));
        Assert.IsFalse(xmlResult.Contains("<div"));
    }
}
