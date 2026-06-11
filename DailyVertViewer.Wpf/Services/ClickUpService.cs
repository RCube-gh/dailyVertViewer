using System.Text.Json;
using System.Net.Http;
using DailyVertViewer.Wpf.Models;

namespace DailyVertViewer.Wpf.Services;

public sealed class ClickUpService
{
    private readonly HttpClient _httpClient;
    private readonly AppEnvironment _environment;
    private readonly TimeZoneInfo _jst;

    public ClickUpService(HttpClient httpClient, AppEnvironment environment, TimeZoneInfo jst)
    {
        _httpClient = httpClient;
        _environment = environment;
        _jst = jst;
    }

    public async Task<(IReadOnlyList<ClickUpTaskItem> ParentTasks, IReadOnlyDictionary<string, IReadOnlyList<ClickUpTaskItem>> SubtaskMap)>
        FetchTodayTasksAsync(CancellationToken cancellationToken)
    {
        var listId = _environment.Get("CLICKUP_LIST_ID");
        var token = _environment.Get("CLICKUP_API_TOKEN");
        if (string.IsNullOrWhiteSpace(listId) || string.IsNullOrWhiteSpace(token))
        {
            return ([], new Dictionary<string, IReadOnlyList<ClickUpTaskItem>>());
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.clickup.com/api/v2/list/{listId}/task?archived=false&subtasks=true");
        request.Headers.Add("Authorization", token);
        request.Headers.Add("Accept", "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ([], new Dictionary<string, IReadOnlyList<ClickUpTaskItem>>());
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var tasks = new List<ClickUpTaskItem>();
        foreach (var item in doc.RootElement.GetProperty("tasks").EnumerateArray())
        {
            tasks.Add(new ClickUpTaskItem
            {
                Id = item.GetProperty("id").GetString() ?? string.Empty,
                ParentId = item.TryGetProperty("parent", out var parentProp) && parentProp.ValueKind != JsonValueKind.Null
                    ? parentProp.GetString()
                    : null,
                Name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "(Untitled)" : "(Untitled)",
                DueDateUnixMs = TryParseDueDate(item)
            });
        }

        var parentLookup = tasks.ToDictionary(task => task.Id, task => task);
        var (todayStartMs, tomorrowStartMs) = GetTodayRangeUnixMs();
        var todayTasks = tasks
            .Where(task => IsDueToday(task, parentLookup, todayStartMs, tomorrowStartMs))
            .ToList();

        var parents = new List<ClickUpTaskItem>();
        var subtasks = new Dictionary<string, List<ClickUpTaskItem>>();
        foreach (var task in todayTasks)
        {
            if (!string.IsNullOrWhiteSpace(task.ParentId))
            {
                if (!subtasks.TryGetValue(task.ParentId, out var children))
                {
                    children = [];
                    subtasks[task.ParentId] = children;
                }

                children.Add(task);
            }
            else
            {
                parents.Add(task);
            }
        }

        return (
            parents,
            subtasks.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ClickUpTaskItem>)pair.Value));
    }

    private long? GetEffectiveDueDate(ClickUpTaskItem task, IReadOnlyDictionary<string, ClickUpTaskItem> parentLookup)
    {
        if (task.DueDateUnixMs.HasValue)
        {
            return task.DueDateUnixMs.Value;
        }

        return !string.IsNullOrWhiteSpace(task.ParentId) &&
            parentLookup.TryGetValue(task.ParentId, out var parent) &&
            parent.DueDateUnixMs.HasValue
                ? parent.DueDateUnixMs.Value
                : null;
    }

    private bool IsDueToday(
        ClickUpTaskItem task,
        IReadOnlyDictionary<string, ClickUpTaskItem> parentLookup,
        long todayStartMs,
        long tomorrowStartMs)
    {
        var due = GetEffectiveDueDate(task, parentLookup);
        return due.HasValue && due.Value >= todayStartMs && due.Value < tomorrowStartMs;
    }

    private (long TodayStartMs, long TomorrowStartMs) GetTodayRangeUnixMs()
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _jst);
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var tomorrow = today.AddDays(1);
        return (today.ToUnixTimeMilliseconds(), tomorrow.ToUnixTimeMilliseconds());
    }

    private static long? TryParseDueDate(JsonElement task)
    {
        if (!task.TryGetProperty("due_date", out var dueDateProp) || dueDateProp.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return long.TryParse(dueDateProp.GetString(), out var result) ? result : null;
    }
}
