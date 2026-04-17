using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Glasswork.Core.Services;

public class FeedbackService
{
    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;

    public FeedbackService(string owner, string repo, string? githubToken = null)
    {
        _owner = owner;
        _repo = repo;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Glasswork", "1.0"));
        if (!string.IsNullOrEmpty(githubToken))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", githubToken);
        }
    }

    public async Task<FeedbackResult> SubmitAsync(string title, string body, string category)
    {
        var label = category switch
        {
            "Bug" => "bug",
            "Feature Request" => "enhancement",
            _ => "feedback"
        };

        var payload = new
        {
            title = $"[{category}] {title}",
            body = body,
            labels = new[] { label }
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync(
            $"https://api.github.com/repos/{_owner}/{_repo}/issues", content);

        if (response.IsSuccessStatusCode)
        {
            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            var url = doc.RootElement.GetProperty("html_url").GetString() ?? "";
            var number = doc.RootElement.GetProperty("number").GetInt32();
            return new FeedbackResult(true, number, url);
        }

        var error = await response.Content.ReadAsStringAsync();
        return new FeedbackResult(false, 0, "", $"GitHub API error: {response.StatusCode} — {error}");
    }
}

public record FeedbackResult(bool Success, int IssueNumber, string Url, string? Error = null);
