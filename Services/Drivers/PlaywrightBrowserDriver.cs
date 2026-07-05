using CSharpScraper.Interfaces;
using Microsoft.Playwright;
using Microsoft.Extensions.Logging;
using System.IO;

namespace CSharpScraper.Services.Drivers;

public class PlaywrightBrowserDriver : IExecutionDriver
{
    private readonly ILogger<PlaywrightBrowserDriver> _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private Guid _jobId;

    public PlaywrightBrowserDriver(ILogger<PlaywrightBrowserDriver> logger)
    {
        _logger = logger;
    }

    public virtual async Task InitializeAsync(Guid jobId)
    {
        _jobId = jobId;
        _logger.LogInformation("Initializing Playwright driver for Job {JobId}", _jobId);

        _playwright = await Playwright.CreateAsync();
        
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[] 
            { 
                "--no-sandbox", 
                "--disable-setuid-sandbox", 
                "--disable-blink-features=AutomationControlled",
                "--disable-dev-shm-usage"
            }
        });

        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            AcceptDownloads = false
        });

        _page = await _context.NewPageAsync();
        
        // Add default timeout
        _page.SetDefaultTimeout(15000);
        _page.SetDefaultNavigationTimeout(20000);
    }

    public virtual async Task NavigateAsync(string url)
    {
        _logger.LogInformation("Job {JobId}: Navigating to {Url}", _jobId, url);
        if (_page == null) throw new InvalidOperationException("Driver not initialized.");
        
        await _page.GotoAsync(url, new PageGotoOptions 
        { 
            WaitUntil = WaitUntilState.Load 
        });
    }

    public virtual async Task ClickSelectorAsync(string selector)
    {
        _logger.LogInformation("Job {JobId}: Clicking selector '{Selector}'", _jobId, selector);
        if (_page == null) throw new InvalidOperationException("Driver not initialized.");

        // First wait for it to be attached/visible
        await _page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });
        await _page.ClickAsync(selector);
    }

    public virtual async Task ClickCoordinateAsync(double x, double y)
    {
        _logger.LogInformation("Job {JobId}: Clicking coordinates ({X}, {Y})", _jobId, x, y);
        if (_page == null) throw new InvalidOperationException("Driver not initialized.");

        await _page.Mouse.ClickAsync((float)x, (float)y);
    }

    public virtual async Task TypeTextAsync(string selector, string text)
    {
        _logger.LogInformation("Job {JobId}: Typing text into '{Selector}'", _jobId, selector);
        if (_page == null) throw new InvalidOperationException("Driver not initialized.");

        await _page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });
        await _page.FocusAsync(selector);
        await _page.FillAsync(selector, "");
        await _page.Locator(selector).PressSequentiallyAsync(text);
    }

    public virtual async Task ScrollAsync(string direction)
    {
        _logger.LogInformation("Job {JobId}: Scrolling {Direction}", _jobId, direction);
        if (_page == null) throw new InvalidOperationException("Driver not initialized.");

        if (direction.ToLower() == "up")
        {
            await _page.EvaluateAsync("window.scrollBy(0, -window.innerHeight * 0.8)");
        }
        else
        {
            await _page.EvaluateAsync("window.scrollBy(0, window.innerHeight * 0.8)");
        }
    }

    public virtual async Task WaitAsync(int durationMs)
    {
        _logger.LogInformation("Job {JobId}: Waiting for {DurationMs}ms", _jobId, durationMs);
        await Task.Delay(durationMs);
    }

    public virtual async Task<string> CaptureScreenshotAsync(string saveDirectory, int stepNumber)
    {
        if (_page == null) throw new InvalidOperationException("Driver not initialized.");

        Directory.CreateDirectory(saveDirectory);
        var filename = $"step_{stepNumber:D2}.png";
        var fullPath = Path.Combine(saveDirectory, filename);

        _logger.LogInformation("Job {JobId}: Capturing screenshot to {FullPath}", _jobId, fullPath);
        await _page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = fullPath,
            Type = ScreenshotType.Png
        });

        return fullPath;
    }

    public virtual async Task<string> GetPageContentAsync()
    {
        if (_page == null) throw new InvalidOperationException("Driver not initialized.");
        return await _page.ContentAsync();
    }

    public virtual async Task<string> EvaluateScriptAsync(string script)
    {
        if (_page == null) throw new InvalidOperationException("Driver not initialized.");
        var result = await _page.EvaluateAsync<string>(script);
        return result ?? string.Empty;
    }

    public virtual Task<string> GetUrlAsync()
    {
        if (_page == null) throw new InvalidOperationException("Driver not initialized.");
        return Task.FromResult(_page.Url);
    }

    public virtual async Task<string> GetTitleAsync()
    {
        if (_page == null) throw new InvalidOperationException("Driver not initialized.");
        return await _page.TitleAsync();
    }

    public virtual async Task CleanupAsync()
    {
        _logger.LogInformation("Cleaning up Playwright driver resources for Job {JobId}", _jobId);
        
        if (_page != null) await _page.CloseAsync().Catch();
        if (_context != null) await _context.CloseAsync().Catch();
        if (_browser != null) await _browser.CloseAsync().Catch();
        if (_playwright != null) _playwright.Dispose();
        
        _page = null;
        _context = null;
        _browser = null;
        _playwright = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
    }
}

public static class TaskExtensions
{
    // Helper to absorb any exceptions during clean shutdown of browser
    public static async Task Catch(this Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // Suppress
        }
    }
}
