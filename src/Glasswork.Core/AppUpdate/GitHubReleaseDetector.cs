using System.Net.Http;
using System.Text.Json;

namespace Glasswork.Core.AppUpdate;

public enum ReleaseStream
{
    App,
    Mcp,
}

public sealed class GitHubReleaseDetector
{
    private readonly HttpClient _httpClient;
    private const string ReleasesUrl = "https://api.github.com/repos/tjegbejimba/Glasswork/releases?per_page=100";

    public GitHubReleaseDetector() : this(new HttpClientHandler()) { }

    public GitHubReleaseDetector(HttpMessageHandler handler)
    {
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Glasswork-UpdateChecker");
    }

    public async Task<ReleaseDetectionResult> GetLatestReleaseAsync(ReleaseStream stream = ReleaseStream.App)
    {
        try
        {
            var response = await _httpClient.GetAsync(ReleasesUrl);

            if (!response.IsSuccessStatusCode)
            {
                return ReleaseDetectionResult.Failed($"HTTP {(int)response.StatusCode}");
            }

            var content = await response.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(content);

            if (jsonDoc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return ParseSingleRelease(jsonDoc.RootElement, stream);
            }

            if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ReleaseDetectionResult.Failed("Release response is not an array");
            }

            AppVersion? latest = null;
            foreach (var release in jsonDoc.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object ||
                    IsTrue(release, "draft") ||
                    IsTrue(release, "prerelease") ||
                    !release.TryGetProperty("tag_name", out var tagNameElement) ||
                    tagNameElement.ValueKind != JsonValueKind.String ||
                    !TryParseTag(tagNameElement.GetString(), stream, out var version))
                {
                    continue;
                }

                if (latest is null || version!.CompareTo(latest) > 0)
                {
                    latest = version;
                }
            }

            if (latest is null)
            {
                return ReleaseDetectionResult.Failed($"No {stream.ToString().ToLowerInvariant()} releases found");
            }

            return ReleaseDetectionResult.Success(latest);
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

    private static ReleaseDetectionResult ParseSingleRelease(JsonElement release, ReleaseStream stream)
    {
        if (!release.TryGetProperty("tag_name", out var tagNameElement))
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

        if (!TryParseTag(tagName, stream, out var version))
        {
            return ReleaseDetectionResult.Failed($"Invalid version format: {tagName}");
        }

        return ReleaseDetectionResult.Success(version!);
    }

    private static bool TryParseTag(
        string? tagName,
        ReleaseStream stream,
        out AppVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        string versionText;
        if (stream == ReleaseStream.App)
        {
            if (!tagName.StartsWith('v') || tagName.StartsWith("mcp-", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            versionText = tagName[1..];
        }
        else
        {
            const string prefix = "mcp-v";
            if (!tagName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            versionText = tagName[prefix.Length..];
        }

        return AppVersion.TryParse(versionText, out version);
    }

    private static bool IsTrue(JsonElement release, string propertyName) =>
        release.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.True;
}
