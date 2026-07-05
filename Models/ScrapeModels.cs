using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpScraper.Models;

public class ScrapeRequest
{
    public required string Url { get; set; }
    public required string Goal { get; set; }
    public string? Model { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public int MaxSteps { get; set; } = 15;
    public string DriverType { get; set; } = "playwright"; // "playwright", "desktop"
    public string AgentType { get; set; } = "dom";        // "dom", "visual"
}

public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Stopped
}

public class ScrapeJob
{
    public Guid JobId { get; set; } = Guid.NewGuid();
    public required string Url { get; set; }
    public required string Goal { get; set; }
    
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JobStatus Status { get; set; } = JobStatus.Queued;
    
    public int CurrentStep { get; set; } = 0;
    public int MaxSteps { get; set; } = 15;
    public string LastAction { get; set; } = "Initializing...";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
    public int TotalPromptTokens { get; set; } = 0;
    public int TotalCompletionTokens { get; set; } = 0;
    
    [JsonIgnore]
    public object? ExtractedData { get; set; }
    
    [JsonIgnore]
    public List<ScrapeStepLog> Steps { get; set; } = new();
}

public class ScrapeAction
{
    [JsonPropertyName("type")]
    public required string Type { get; set; } // "click_selector", "click_coordinate", "type_text", "scroll", "wait", "extract_data", "fail"

    [JsonPropertyName("selector")]
    public string? Selector { get; set; }

    [JsonPropertyName("x")]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    public double? Y { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; } // "up", "down"

    [JsonPropertyName("durationMs")]
    public int? DurationMs { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
    
    public override string ToString()
    {
        return Type switch
        {
            "click_selector" => $"Clicking element '{Selector}'",
            "click_coordinate" => $"Clicking coordinate ({X}, {Y})",
            "type_text" => $"Typing '{Text}' into '{Selector}'",
            "scroll" => $"Scrolling {Direction}",
            "wait" => $"Waiting for {DurationMs ?? 1000}ms",
            "extract_data" => "Extracting target data",
            "fail" => $"Failed: {Reason}",
            _ => $"Unknown action: {Type}"
        };
    }
}

public class ScrapeStepLog
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int StepNumber { get; set; }
    public required string Thought { get; set; }
    public required ScrapeAction Action { get; set; }
    public string? ScreenshotPath { get; set; }
    public int PromptTokens { get; set; } = 0;
    public int CompletionTokens { get; set; } = 0;
}

public class JobStatusResponse
{
    public Guid JobId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int CurrentStep { get; set; }
    public int MaxSteps { get; set; }
    public string LastAction { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
    public int TotalPromptTokens { get; set; }
    public int TotalCompletionTokens { get; set; }
}

public class JobResultResponse
{
    public Guid JobId { get; set; }
    public string Status { get; set; } = string.Empty;
    public object? Data { get; set; }
    public string? Error { get; set; }
    public int TotalPromptTokens { get; set; }
    public int TotalCompletionTokens { get; set; }
}

public class JobLogsResponse
{
    public Guid JobId { get; set; }
    public List<ScrapeStepLog> Logs { get; set; } = new();
}

public class ScrapeCompareRequest
{
    public required List<string> Urls { get; set; }
    public required string Goal { get; set; }
    public string? Model { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public int MaxSteps { get; set; } = 15;
    public string DriverType { get; set; } = "playwright";
    public string AgentType { get; set; } = "dom";
}

public class CompareJobSummary
{
    public string Url { get; set; } = string.Empty;
    public Guid JobId { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class CompareStartResponse
{
    public Guid CompareId { get; set; }
    public string Goal { get; set; } = string.Empty;
    public List<CompareJobSummary> Jobs { get; set; } = new();
}

public class CompareStatusResponse
{
    public Guid CompareId { get; set; }
    public string Goal { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // "Running", "Completed", "Failed"
    public List<JobStatusResponse> Jobs { get; set; } = new();
}

public class CompareResultResponse
{
    public Guid CompareId { get; set; }
    public string Goal { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<JobResultResponse> Results { get; set; } = new();
}

public class ScrapeDiscoverCompareRequest
{
    public required string Query { get; set; }
    public string? Location { get; set; }
    public string? Model { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public int MaxSteps { get; set; } = 6;
    public string DriverType { get; set; } = "playwright";
    public string AgentType { get; set; } = "dom";
}

public class DiscoveryRouting
{
    public string OptimizedSearchQuery { get; set; } = string.Empty;
    public List<string> TargetDomains { get; set; } = new();
}
