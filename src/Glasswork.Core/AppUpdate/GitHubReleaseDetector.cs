using System.Net.Http;
using System.Text.Json;

namespace Glasswork.Core.AppUpdate;

public sealed class GitHubReleaseDetector
{
    private readonly HttpClient _httpClient;
    private const string LatestReleaseUrl = "https://api.github.com/repos/tjegbejimba/Glasswork/releases/latest";
    
    public GitHubReleaseDetector() : this(new HttpClientHandler()) { }
    
    public GitHubReleaseDetector(HttpMessageHandler handler)
    {
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Glasswork-UpdateChecker");
    }
    
    public async Task<ReleaseDetectionResult> GetLatestReleaseAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync(LatestReleaseUrl);
            
            if (!response.IsSuccessStatusCode)
            {
                return ReleaseDetectionResult.Failed($"HTTP {(int)response.StatusCode}");
            }
            
            var content = await response.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(content);
            
            if (!jsonDoc.RootElement.TryGetProperty("tag_name", out var tagNameElement))
            {
                return ReleaseDetectionResult.Failed("Missing tag_name in response");
            }
            
            if (tagNameElement.ValueKind != JsonValueKind.String)
            {
                return ReleaseDetectionResult.Failed("tag_name is not a string");
            }
            
            var tagName = tagNameElement.GetString();
            if (string.IsNullOrEmpty(tagName))
            {
                return ReleaseDetectionResult.Failed("Empty tag_name in response");
            }
            
            if (!AppVersion.TryParse(tagName, out var version) || version == null)
            {
                return ReleaseDetectionResult.Failed($"Invalid version format: {tagName}");
            }
            
            return ReleaseDetectionResult.Success(version);
        }
        catch (HttpRequestException ex)
        {
            return ReleaseDetectionResult.Failed($"Network error: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return ReleaseDetectionResult.Failed("Request timeout");
        }
        catch (JsonException ex)
        {
            return ReleaseDetectionResult.Failed($"Malformed JSON: {ex.Message}");
        }
    }
}
