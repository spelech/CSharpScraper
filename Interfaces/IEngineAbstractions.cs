using CSharpScraper.Models;
using CSharpScraper.Services;

namespace CSharpScraper.Interfaces;

public interface IExecutionDriver : IAsyncDisposable
{
    Task InitializeAsync(Guid jobId);
    Task NavigateAsync(string url);
    Task ClickSelectorAsync(string selector);
    Task ClickCoordinateAsync(double x, double y);
    Task TypeTextAsync(string selector, string text);
    Task ScrollAsync(string direction);
    Task WaitAsync(int durationMs);
    Task<string> CaptureScreenshotAsync(string saveDirectory, int stepNumber);
    Task<string> GetPageContentAsync();
    Task<string> EvaluateScriptAsync(string script);
    Task<string> GetUrlAsync();
    Task<string> GetTitleAsync();
    Task CleanupAsync();
}

public interface IInnerLoopAgent
{
    Task<ScrapeStepLog> DecideNextActionAsync(
        string goal, 
        int stepNumber, 
        IExecutionDriver driver, 
        LlmClient llmClient, 
        string modelName, 
        string historySummary
    );
}
