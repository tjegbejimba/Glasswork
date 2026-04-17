using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Client for Azure DevOps REST API. Searches assigned work items
/// and retrieves details for linking to Glasswork tasks.
/// </summary>
public class AdoService
{
    private readonly HttpClient _http;
    private readonly string _organization;
    private readonly string _project;

    public AdoService(string organization, string project, string personalAccessToken)
    {
        _organization = organization;
        _project = project;
        _http = new HttpClient
        {
            BaseAddress = new Uri($"https://dev.azure.com/{organization}/{project}/")
        };
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{personalAccessToken}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }

    /// <summary>
    /// Search work items assigned to the current user or matching a query.
    /// </summary>
    public async Task<List<AdoWorkItem>> SearchAssignedAsync(string? textFilter = null)
    {
        var wiql = "SELECT [System.Id], [System.Title], [System.State], [System.WorkItemType], [System.AssignedTo], [System.AreaPath], [System.IterationPath] " +
                   "FROM WorkItems WHERE [System.AssignedTo] = @Me AND [System.State] <> 'Closed' AND [System.State] <> 'Removed'";

        if (!string.IsNullOrWhiteSpace(textFilter))
            wiql += $" AND [System.Title] CONTAINS '{textFilter}'";

        wiql += " ORDER BY [System.ChangedDate] DESC";

        return await RunWiqlAsync(wiql);
    }

    /// <summary>
    /// Get a single work item by ID.
    /// </summary>
    public async Task<AdoWorkItem?> GetWorkItemAsync(int id)
    {
        var response = await _http.GetAsync($"_apis/wit/workitems/{id}?api-version=7.0");
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        return ParseWorkItem(JsonDocument.Parse(json).RootElement);
    }

    private async Task<List<AdoWorkItem>> RunWiqlAsync(string wiql)
    {
        var body = JsonSerializer.Serialize(new { query = wiql });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("_apis/wit/wiql?api-version=7.0", content);

        if (!response.IsSuccessStatusCode) return [];

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        var ids = doc.RootElement
            .GetProperty("workItems")
            .EnumerateArray()
            .Select(wi => wi.GetProperty("id").GetInt32())
            .Take(50) // limit results
            .ToList();

        if (ids.Count == 0) return [];

        return await GetWorkItemsByIdsAsync(ids);
    }

    private async Task<List<AdoWorkItem>> GetWorkItemsByIdsAsync(List<int> ids)
    {
        var idList = string.Join(",", ids);
        var fields = "System.Id,System.Title,System.State,System.WorkItemType,System.AssignedTo,System.AreaPath,System.IterationPath";
        var response = await _http.GetAsync($"_apis/wit/workitems?ids={idList}&fields={fields}&api-version=7.0");

        if (!response.IsSuccessStatusCode) return [];

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        return doc.RootElement
            .GetProperty("value")
            .EnumerateArray()
            .Select(ParseWorkItem)
            .ToList();
    }

    /// <summary>
    /// Parse a work item JSON element to AdoWorkItem model.
    /// Public for testability.
    /// </summary>
    public static AdoWorkItem ParseWorkItem(JsonElement element)
    {
        var fields = element.GetProperty("fields");
        var id = element.GetProperty("id").GetInt32();

        return new AdoWorkItem
        {
            Id = id,
            Title = GetStringField(fields, "System.Title"),
            State = GetStringField(fields, "System.State"),
            WorkItemType = GetStringField(fields, "System.WorkItemType"),
            AssignedTo = GetStringField(fields, "System.AssignedTo"),
            AreaPath = GetStringField(fields, "System.AreaPath"),
            IterationPath = GetStringField(fields, "System.IterationPath"),
            Url = element.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
        };
    }

    private static string GetStringField(JsonElement fields, string key)
    {
        if (!fields.TryGetProperty(key, out var val)) return "";
        if (val.ValueKind == JsonValueKind.String) return val.GetString() ?? "";
        // AssignedTo can be an object with displayName
        if (val.ValueKind == JsonValueKind.Object && val.TryGetProperty("displayName", out var name))
            return name.GetString() ?? "";
        return "";
    }
}
