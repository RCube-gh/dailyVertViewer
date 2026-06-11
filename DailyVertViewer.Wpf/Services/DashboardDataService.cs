using DailyVertViewer.Wpf.Models;

namespace DailyVertViewer.Wpf.Services;

public sealed class DashboardDataService
{
    private readonly GoogleCalendarService _calendarService;
    private readonly TogglService _togglService;
    private readonly ClickUpService _clickUpService;
    private readonly TimeZoneInfo _jst;

    public DashboardDataService(
        GoogleCalendarService calendarService,
        TogglService togglService,
        ClickUpService clickUpService,
        TimeZoneInfo jst)
    {
        _calendarService = calendarService;
        _togglService = togglService;
        _clickUpService = clickUpService;
        _jst = jst;
    }

    public async Task<DashboardSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var events = await _calendarService.FetchTodayEventsAsync(cancellationToken);
        var togglEntries = await _togglService.GetStructuredEntriesAsync(cancellationToken);
        var (parentTasks, subtaskMap) = await _clickUpService.FetchTodayTasksAsync(cancellationToken);

        return new DashboardSnapshot
        {
            CachedDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _jst)),
            TimedEvents = events.Where(evt => !evt.IsAllDay).ToList(),
            AllDayEvents = events.Where(evt => evt.IsAllDay).ToList(),
            TogglEntries = togglEntries,
            ParentTasks = parentTasks,
            SubtaskMap = subtaskMap
        };
    }
}
