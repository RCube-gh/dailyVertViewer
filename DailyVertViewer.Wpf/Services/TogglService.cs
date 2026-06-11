using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DailyVertViewer.Wpf.Models;

namespace DailyVertViewer.Wpf.Services;

public sealed class TogglService
{
    private readonly AppEnvironment _environment;
    private readonly TimeZoneInfo _jst;

    public TogglService(AppEnvironment environment, TimeZoneInfo jst)
    {
        _environment = environment;
        _jst = jst;
    }

    public async Task<IReadOnlyList<TogglEntryItem>> GetStructuredEntriesAsync(CancellationToken cancellationToken)
    {
        var token = _environment.Get("TOGGL_API_TOKEN");
        var workspaceId = _environment.Get("TOGGL_WORKSPACE_ID");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(workspaceId))
        {
            return [];
        }

        using var client = CreateAuthedClient(token);
        var projects = await FetchProjectsAsync(client, workspaceId, cancellationToken);
        var result = new List<TogglEntryItem>();

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _jst);
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var todayEnd = new DateTimeOffset(now.Year, now.Month, now.Day, 23, 59, 59, now.Offset);
        var entriesUrl =
            $"https://api.track.toggl.com/api/v9/me/time_entries?start_date={Uri.EscapeDataString(todayStart.ToUniversalTime().ToString("O"))}&end_date={Uri.EscapeDataString(todayEnd.ToUniversalTime().ToString("O"))}";

        using var entriesResponse = await client.GetAsync(entriesUrl, cancellationToken);
        if (entriesResponse.IsSuccessStatusCode)
        {
            await using var stream = await entriesResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var formatted = FormatEntry(entry, projects);
                if (formatted is not null)
                {
                    result.Add(formatted);
                }
            }
        }

        using var currentResponse = await client.GetAsync(
            "https://api.track.toggl.com/api/v9/me/time_entries/current",
            cancellationToken);
        if (currentResponse.IsSuccessStatusCode)
        {
            await using var stream = await currentResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                result.Add(FormatCurrentEntry(doc.RootElement, projects));
            }
        }

        return result;
    }

    private static HttpClient CreateAuthedClient(string token)
    {
        var client = new HttpClient();
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{token}:api_token"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        return client;
    }

    private async Task<Dictionary<long, (string Name, string ColorHex)>> FetchProjectsAsync(
        HttpClient client,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"https://api.track.toggl.com/api/v9/workspaces/{workspaceId}/projects",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var result = new Dictionary<long, (string Name, string ColorHex)>();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        foreach (var project in doc.RootElement.EnumerateArray())
        {
            if (!project.TryGetProperty("id", out var idProp))
            {
                continue;
            }

            var color = project.TryGetProperty("color", out var colorProp)
                ? colorProp.GetString()
                : "#cccccc";
            if (string.IsNullOrWhiteSpace(color))
            {
                color = "#cccccc";
            }
            else if (!color.StartsWith('#'))
            {
                color = $"#{color}";
            }

            result[idProp.GetInt64()] = (
                project.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "(no name)" : "(no name)",
                color);
        }

        return result;
    }

    private TogglEntryItem? FormatEntry(
        JsonElement entry,
        Dictionary<long, (string Name, string ColorHex)> projects)
    {
        if (!entry.TryGetProperty("project_id", out var projectIdProp) ||
            projectIdProp.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var duration = entry.GetProperty("duration").GetInt64();
        if (duration < 0)
        {
            return null;
        }

        var projectId = projectIdProp.GetInt64();
        var project = projects.TryGetValue(projectId, out var info)
            ? info
            : (Name: "(no project)", ColorHex: "#cccccc");
        var start = TimeZoneInfo.ConvertTime(
            DateTimeOffset.Parse(entry.GetProperty("start").GetString()!, CultureInfo.InvariantCulture),
            _jst);

        return new TogglEntryItem
        {
            Description = entry.TryGetProperty("description", out var descProp)
                ? descProp.GetString() ?? "(no description)"
                : "(no description)",
            Project = project.Name,
            Start = start,
            End = start.AddSeconds(duration),
            ColorHex = project.ColorHex,
            Running = false
        };
    }

    private TogglEntryItem FormatCurrentEntry(
        JsonElement entry,
        Dictionary<long, (string Name, string ColorHex)> projects)
    {
        long? projectId = null;
        if (entry.TryGetProperty("project_id", out var projectIdProp) && projectIdProp.ValueKind == JsonValueKind.Number)
        {
            projectId = projectIdProp.GetInt64();
        }

        var project = projectId.HasValue && projects.TryGetValue(projectId.Value, out var info)
            ? info
            : (Name: "(current)", ColorHex: "#ff5555");
        var start = TimeZoneInfo.ConvertTime(
            DateTimeOffset.Parse(entry.GetProperty("start").GetString()!, CultureInfo.InvariantCulture),
            _jst);

        return new TogglEntryItem
        {
            Description = entry.TryGetProperty("description", out var descProp)
                ? descProp.GetString() ?? "(running)"
                : "(running)",
            Project = project.Name,
            Start = start,
            End = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _jst),
            ColorHex = project.ColorHex,
            Running = true
        };
    }
}
