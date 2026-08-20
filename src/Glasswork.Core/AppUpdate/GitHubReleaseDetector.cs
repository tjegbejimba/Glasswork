using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    private const string GitRefsUrl = "https://github.com/tjegbejimba/Glasswork.git/info/refs?service=git-upload-pack";

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
                return await GetLatestPublishedTagAsync(stream, response.StatusCode);
            }

            return ParseApiResponse(await response.Content.ReadAsStringAsync(), stream);
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

    private static ReleaseDetectionResult ParseApiResponse(string content, ReleaseStream stream)
    {
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

        return Complete(latest, stream);
    }

    private async Task<ReleaseDetectionResult> GetLatestPublishedTagAsync(
        ReleaseStream stream,
        System.Net.HttpStatusCode apiStatusCode)
    {
        var refsResponse = await _httpClient.GetAsync(GitRefsUrl);
        if (!refsResponse.IsSuccessStatusCode)
        {
            return ReleaseDetectionResult.Failed(
                $"HTTP {(int)apiStatusCode}; Git fallback HTTP {(int)refsResponse.StatusCode}");
        }

        var advertisement = Encoding.UTF8.GetString(
            await refsResponse.Content.ReadAsByteArrayAsync());
        var candidates = Regex.Matches(
                advertisement,
                @"refs/tags/(?<tag>[^\x00\s^]+)",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["tag"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(tag => (Tag: tag, Parsed: TryParseTag(tag, stream, out var version), Version: version))
            .Where(candidate => candidate.Parsed && candidate.Version is not null)
            .OrderByDescending(candidate => candidate.Version)
            .ToArray();

        foreach (var candidate in candidates)
        {
            var assetUrl = GetReleaseAssetUrl(stream, candidate.Tag, candidate.Version!);
            using var request = new HttpRequestMessage(HttpMethod.Head, assetUrl);
            using var assetResponse = await _httpClient.SendAsync(request);
            if (assetResponse.IsSuccessStatusCode)
            {
                return ReleaseDetectionResult.Success(candidate.Version!);
            }
            if (assetResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                return ReleaseDetectionResult.Failed(
                    $"Release asset check HTTP {(int)assetResponse.StatusCode}");
            }
        }

        return ReleaseDetectionResult.Failed(
            $"No published {stream.ToString().ToLowerInvariant()} release tags found");
    }

    private static string GetReleaseAssetUrl(
        ReleaseStream stream,
        string tag,
        AppVersion version)
    {
        var assetName = stream == ReleaseStream.App
            ? "Glasswork-win-x64.zip"
            : $"glasswork-mcp.{version}.nupkg";
        return $"https://github.com/tjegbejimba/Glasswork/releases/download/{tag}/{assetName}";
    }

    private static ReleaseDetectionResult Complete(AppVersion? latest, ReleaseStream stream) =>
        latest is null
            ? ReleaseDetectionResult.Failed(
                $"No {stream.ToString().ToLowerInvariant()} releases found")
            : ReleaseDetectionResult.Success(latest);

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

        if (!Regex.IsMatch(
                versionText,
                @"^\d+\.\d+\.\d+$",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        return AppVersion.TryParse(versionText, out version);
    }

    private static bool IsTrue(JsonElement release, string propertyName) =>
        release.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.True;
}
