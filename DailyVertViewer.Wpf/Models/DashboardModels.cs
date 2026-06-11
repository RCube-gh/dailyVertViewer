using Media = System.Windows.Media;

namespace DailyVertViewer.Wpf.Models;

public sealed class CalendarEventItem
{
    public string Summary { get; init; } = "No Title";
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public string ColorHex { get; init; } = "#a2d5f2";
    public bool IsAllDay { get; init; }
}

public sealed class TogglEntryItem
{
    public string Description { get; init; } = "(no description)";
    public string Project { get; init; } = "(no project)";
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public bool Running { get; init; }
    public string ColorHex { get; init; } = "#cccccc";
}

public sealed class ClickUpTaskItem
{
    public string Id { get; init; } = string.Empty;
    public string? ParentId { get; init; }
    public string Name { get; init; } = "(Untitled)";
    public long? DueDateUnixMs { get; init; }
}

public sealed class DashboardSnapshot
{
    public DateOnly CachedDate { get; init; }
    public IReadOnlyList<CalendarEventItem> TimedEvents { get; init; } = [];
    public IReadOnlyList<CalendarEventItem> AllDayEvents { get; init; } = [];
    public IReadOnlyList<TogglEntryItem> TogglEntries { get; init; } = [];
    public IReadOnlyList<ClickUpTaskItem> ParentTasks { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<ClickUpTaskItem>> SubtaskMap { get; init; } =
        new Dictionary<string, IReadOnlyList<ClickUpTaskItem>>();
}

public sealed class DashboardCache
{
    public DateOnly? CachedDate { get; set; }
    public IReadOnlyList<CalendarEventItem> TimedEvents { get; set; } = [];
    public IReadOnlyList<CalendarEventItem> AllDayEvents { get; set; } = [];
    public IReadOnlyList<TogglEntryItem> TogglEntries { get; set; } = [];
    public IReadOnlyList<ClickUpTaskItem> ParentTasks { get; set; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<ClickUpTaskItem>> SubtaskMap { get; set; } =
        new Dictionary<string, IReadOnlyList<ClickUpTaskItem>>();
}

public sealed class HourLabelItem
{
    public string Text { get; init; } = string.Empty;
    public double Top { get; init; }
}

public sealed class HourLineItem
{
    public double Top { get; init; }
}

public sealed class ScheduleBlockItem
{
    public string Title { get; init; } = string.Empty;
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public Media.Brush Fill { get; init; } = Media.Brushes.SteelBlue;
}

public enum TodoItemKind
{
    Section,
    Task,
    Subtask,
    Info
}

public sealed class TodoItem
{
    public TodoItemKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
}
