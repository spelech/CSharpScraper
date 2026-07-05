using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CSharpScraper.Services;

public class LlmClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LlmClient> _logger;

    public LlmClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<LlmClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public virtual async Task<string> GetVisionCompletionAsync(
        string systemPrompt, 
        string userPrompt, 
        string base64Image, 
        string modelName, 
        string? customBaseUrl = null, 
        string? customApiKey = null,
        bool forceJson = true)
    {
        var baseUrl = customBaseUrl 
            ?? _configuration["LLM_BASE_URL"] 
            ?? _configuration["DEFAULT_LLM_BASE_URL"] 
            ?? "http://litellm:4000/v1";
        
        var apiKey = customApiKey 
            ?? _configuration["LLM_API_KEY"] 
            ?? _configuration["DEFAULT_LLM_API_KEY"] 
            ?? "placeholder";

        _logger.LogInformation("Sending Vision LLM request to {BaseUrl} using model {ModelName}", baseUrl, modelName);

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        
        client.DefaultRequestHeaders.Add("HTTP-Referer", "https://spelech/CSharpScraper");
        client.DefaultRequestHeaders.Add("X-Title", "Playwright CSharp Scraper");

        var userContent = new List<object>
        {
            new { type = "text", text = userPrompt },
            new { type = "image_url", image_url = new { url = $"data:image/png;base64,{base64Image}" } }
        };

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userContent }
        };

        var requestBody = new Dictionary<string, object>
        {
            { "model", modelName },
            { "messages", messages },
            { "temperature", 0.1 }
        };

        if (forceJson)
        {
            requestBody.Add("response_format", new { type = "json_object" });
        }

        try
        {
            var response = await client.PostAsJsonAsync("chat/completions", requestBody);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                _logger.LogError("Vision LLM API returned error: StatusCode={Code}, Error={Error}", response.StatusCode, errorText);
                throw new Exception($"Vision LLM Request failed: {response.StatusCode} - {errorText}");
            }

            var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>();
            var text = result?.Choices?.FirstOrDefault()?.Message?.Content;

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new Exception("Received empty completion from Vision LLM API.");
            }

            return text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Vision LLM API");
            throw;
        }
    }

    public virtual async Task<string> GetCompletionAsync(
        string systemPrompt, 
        string userPrompt, 
        string modelName, 
        string? customBaseUrl = null, 
        string? customApiKey = null,
        bool forceJson = true)
    {
        // Resolve configuration (fallback cascade)
        var baseUrl = customBaseUrl 
            ?? _configuration["LLM_BASE_URL"] 
            ?? _configuration["DEFAULT_LLM_BASE_URL"] 
            ?? "http://litellm:4000/v1";
        
        var apiKey = customApiKey 
            ?? _configuration["LLM_API_KEY"] 
            ?? _configuration["DEFAULT_LLM_API_KEY"] 
            ?? "placeholder";

        _logger.LogInformation("Sending LLM completion request to {BaseUrl} using model {ModelName}", baseUrl, modelName);

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        
        // OpenRouter / LiteLLM headers
        client.DefaultRequestHeaders.Add("HTTP-Referer", "https://spelech/CSharpScraper");
        client.DefaultRequestHeaders.Add("X-Title", "Playwright CSharp Scraper");

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt }
        };

        var requestBody = new Dictionary<string, object>
        {
            { "model", modelName },
            { "messages", messages },
            { "temperature", 0.1 }
        };

        if (forceJson)
        {
            // Set standard JSON object response format
            requestBody.Add("response_format", new { type = "json_object" });
        }

        try
        {
            var response = await client.PostAsJsonAsync("chat/completions", requestBody);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                _logger.LogError("LLM API returned error: StatusCode={Code}, Error={Error}", response.StatusCode, errorText);
                throw new Exception($"LLM Request failed: {response.StatusCode} - {errorText}");
            }

            var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>();
            var text = result?.Choices?.FirstOrDefault()?.Message?.Content;

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new Exception("Received empty completion from LLM API.");
            }

            return text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling LLM API");
            throw;
        }
    }

    private class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; set; }
    }

    private class Choice
    {
        [JsonPropertyName("message")]
        public Message? Message { get; set; }
    }

    private class Message
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
