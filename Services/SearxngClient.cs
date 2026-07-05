using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CSharpScraper.Services;

public class SearxngClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SearxngClient> _logger;

    public SearxngClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<SearxngClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>> SearchProductUrlsAsync(string query, List<string> domains)
    {
        var resultUrls = new Dictionary<string, string>();
        if (domains == null || domains.Count == 0) return resultUrls;

        var baseUrl = _configuration["SEARXNG_BASE_URL"] ?? "http://searxng:8080";
        var client = _httpClientFactory.CreateClient();

        // Build the site query: query (site:domain1 OR site:domain2 OR ...)
        var siteFilterList = new List<string>();
        foreach (var d in domains)
        {
            siteFilterList.Add($"site:{d}");
        }
        var siteFilter = string.Join(" OR ", siteFilterList);
        var fullQuery = $"{query} ({siteFilter})";

        var requestUrl = $"{baseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(fullQuery)}&format=json";
        _logger.LogInformation("Querying SearXNG: {Url}", requestUrl);

        try
        {
            var response = await client.GetFromJsonAsync<SearxngResponse>(requestUrl);
            if (response?.Results == null)
            {
                _logger.LogWarning("SearXNG returned empty or invalid response.");
                return resultUrls;
            }

            foreach (var domain in domains)
            {
                // Find the first result matching this domain in its URL
                foreach (var res in response.Results)
                {
                    if (!string.IsNullOrEmpty(res.Url) && res.Url.Contains(domain, StringComparison.OrdinalIgnoreCase))
                    {
                        resultUrls[domain] = res.Url;
                        _logger.LogInformation("Discovered URL for domain {Domain}: {Url}", domain, res.Url);
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query SearXNG for query '{Query}'.", query);
        }

        return resultUrls;
    }

    private class SearxngResponse
    {
        [JsonPropertyName("results")]
        public List<SearxngResult>? Results { get; set; }
    }

    private class SearxngResult
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }
}
