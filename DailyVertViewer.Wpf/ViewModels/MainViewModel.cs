using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using DailyVertViewer.Wpf.Infrastructure;
using DailyVertViewer.Wpf.Models;
using DailyVertViewer.Wpf.Services;

namespace DailyVertViewer.Wpf.ViewModels;

public enum DisplayMode
{
    Calendar,
    Todo
}

public enum CalendarViewMode
{
    Calendar,
    Compare
}

public sealed class MainViewModel : ObservableObject
{
    private readonly DashboardDataService _dashboardDataService;
    private readonly DashboardCache _cache = new();
    private readonly TimeZoneInfo _jst;
    private readonly Action _hideWindow;
    private DisplayMode _displayMode = DisplayMode.Calendar;
    private CalendarViewMode _viewMode = CalendarViewMode.Calendar;
    private bool _isLoading;
    private double _windowHeight;
    private double _pixelsPerHour;
    private double _nowLineY;
    private string? _statusMessage;
    private DateTimeOffset _lastNow;

    public MainViewModel(DashboardDataService dashboardDataService, Action hideWindow)
    {
        _dashboardDataService = dashboardDataService;
        _hideWindow = hideWindow;
        _jst = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
        _lastNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _jst);

        HourLabels = [];
        HourLines = [];
        ScheduleBlocks = [];
        TodoItems = [];

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(force: true));
    }

    public ObservableCollection<HourLabelItem> HourLabels { get; }

    public ObservableCollection<HourLineItem> HourLines { get; }

    public ObservableCollection<ScheduleBlockItem> ScheduleBlocks { get; }

    public ObservableCollection<TodoItem> TodoItems { get; }

    public ICommand RefreshCommand { get; }

    public DisplayMode DisplayMode
    {
        get => _displayMode;
        private set
        {
            if (SetProperty(ref _displayMode, value))
            {
                OnPropertyChanged(nameof(IsCalendarVisible));
                OnPropertyChanged(nameof(IsTodoVisible));
            }
        }
    }

    public CalendarViewMode ViewMode
    {
        get => _viewMode;
        private set => SetProperty(ref _viewMode, value);
    }

    public bool IsCalendarVisible => DisplayMode == DisplayMode.Calendar;

    public bool IsTodoVisible => DisplayMode == DisplayMode.Todo;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public double WindowHeight
    {
        get => _windowHeight;
        private set => SetProperty(ref _windowHeight, value);
    }

    public double NowLineY
    {
        get => _nowLineY;
        private set => SetProperty(ref _nowLineY, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public void UpdateWindowMetrics(double windowHeight)
    {
        WindowHeight = windowHeight;
        _pixelsPerHour = windowHeight / (UiConstants.EndHour - UiConstants.StartHour);
        RebuildHourMarkers();
        UpdateNowLine();
        RebuildVisibleContent();
    }

    public Task ShowAsync()
    {
        DisplayMode = DisplayMode.Calendar;
        ViewMode = CalendarViewMode.Calendar;

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _jst));
        var isTodayCached = _cache.CachedDate == today;
        if (!isTodayCached)
        {
            _ = RefreshAsync(force: false);
        }
        else
        {
            RebuildVisibleContent();
        }

        return Task.CompletedTask;
    }

    public async Task RefreshAsync(bool force)
    {
        IsLoading = true;
        StatusMessage = null;
        try
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _jst));
            if (!force && _cache.CachedDate == today && _cache.TimedEvents.Count > 0)
            {
                RebuildVisibleContent();
                return;
            }

            var snapshot = await _dashboardDataService.FetchAsync(CancellationToken.None);
            _cache.CachedDate = snapshot.CachedDate;
            _cache.TimedEvents = snapshot.TimedEvents;
            _cache.AllDayEvents = snapshot.AllDayEvents;
            _cache.TogglEntries = snapshot.TogglEntries;
            _cache.ParentTasks = snapshot.ParentTasks;
            _cache.SubtaskMap = snapshot.SubtaskMap;

            RebuildVisibleContent();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ToggleCalendarCompare()
    {
        if (DisplayMode != DisplayMode.Calendar)
        {
            return;
        }

        ViewMode = ViewMode == CalendarViewMode.Calendar ? CalendarViewMode.Compare : CalendarViewMode.Calendar;
        RebuildVisibleContent();
    }

    public void ToggleDisplayMode()
    {
        DisplayMode = DisplayMode == DisplayMode.Calendar ? DisplayMode.Todo : DisplayMode.Calendar;
        RebuildVisibleContent();
    }

    public void UpdateNowLine()
    {
        if (_pixelsPerHour <= 0)
        {
            return;
        }

        _lastNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _jst);
        NowLineY = ((_lastNow.Hour + _lastNow.Minute / 60.0 + _lastNow.Second / 3600.0) - UiConstants.StartHour) * _pixelsPerHour;
        if (DisplayMode == DisplayMode.Calendar && ViewMode == CalendarViewMode.Compare)
        {
            RebuildScheduleBlocks();
        }
    }

    public void HandleKey(Key key, Func<Task> showAction)
    {
        switch (key)
        {
            case Key.Escape:
                _hideWindow();
                break;
            case Key.R:
                _ = RefreshAsync(force: true);
                break;
            case Key.T:
                ToggleCalendarCompare();
                break;
            case Key.D:
                ToggleDisplayMode();
                break;
            case Key.F12:
                _ = showAction();
                break;
        }
    }

    public void SetStatusMessage(string? message)
    {
        StatusMessage = message;
    }

    private void RebuildVisibleContent()
    {
        RebuildHourMarkers();
        UpdateNowLine();
        RebuildScheduleBlocks();
        RebuildTodoItems();
    }

    private void RebuildHourMarkers()
    {
        if (_pixelsPerHour <= 0)
        {
            return;
        }

        HourLabels.Clear();
        for (var hour = UiConstants.StartHour; hour < UiConstants.EndHour; hour++)
        {
            HourLabels.Add(new HourLabelItem
            {
                Text = $"{hour:00}:00",
                Top = ((hour - UiConstants.StartHour) * _pixelsPerHour) - 10
            });
        }

        HourLines.Clear();
        for (var hour = UiConstants.StartHour; hour <= UiConstants.EndHour; hour++)
        {
            HourLines.Add(new HourLineItem
            {
                Top = (hour - UiConstants.StartHour) * _pixelsPerHour
            });
        }
    }

    private void RebuildScheduleBlocks()
    {
        ScheduleBlocks.Clear();

        foreach (var evt in _cache.TimedEvents)
        {
            ScheduleBlocks.Add(CreateScheduleBlock(
                evt.Summary,
                evt.StartTime,
                evt.EndTime,
                evt.ColorHex,
                ViewMode == CalendarViewMode.Calendar ? 0 : UiConstants.LeftWidth,
                ViewMode == CalendarViewMode.Calendar
                    ? UiConstants.WindowWidth - UiConstants.SidebarWidth
                    : UiConstants.WindowWidth - UiConstants.SidebarWidth - UiConstants.LeftWidth));
        }

        if (ViewMode == CalendarViewMode.Compare)
        {
            foreach (var toggl in _cache.TogglEntries)
            {
                ScheduleBlocks.Add(CreateScheduleBlock(
                    $"{toggl.Description}  [{toggl.Project}]",
                    toggl.Start,
                    toggl.Running ? _lastNow : toggl.End,
                    toggl.ColorHex,
                    0,
                    UiConstants.LeftWidth));
            }
        }
    }

    private ScheduleBlockItem CreateScheduleBlock(
        string title,
        DateTimeOffset start,
        DateTimeOffset end,
        string colorHex,
        double left,
        double width)
    {
        var top = ((start.Hour + start.Minute / 60.0 + start.Second / 3600.0) - UiConstants.StartHour) * _pixelsPerHour;
        var height = Math.Max(1, (end - start).TotalHours * _pixelsPerHour);

        return new ScheduleBlockItem
        {
            Title = title,
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Fill = (System.Windows.Media.Brush)(new BrushConverter().ConvertFromString(colorHex) ?? System.Windows.Media.Brushes.SteelBlue)
        };
    }

    private void RebuildTodoItems()
    {
        TodoItems.Clear();
        TodoItems.Add(new TodoItem { Kind = TodoItemKind.Section, Text = "Events" });

        if (_cache.AllDayEvents.Count > 0)
        {
            foreach (var evt in _cache.AllDayEvents)
            {
                TodoItems.Add(new TodoItem { Kind = TodoItemKind.Task, Text = evt.Summary });
            }
        }
        else
        {
            TodoItems.Add(new TodoItem { Kind = TodoItemKind.Info, Text = "No Events" });
        }

        TodoItems.Add(new TodoItem { Kind = TodoItemKind.Section, Text = "Tasks" });
        if (_cache.ParentTasks.Count > 0 || _cache.SubtaskMap.Count > 0)
        {
            foreach (var parent in _cache.ParentTasks)
            {
                TodoItems.Add(new TodoItem { Kind = TodoItemKind.Task, Text = parent.Name });
                if (_cache.SubtaskMap.TryGetValue(parent.Id, out var subtasks))
                {
                    foreach (var subtask in subtasks)
                    {
                        TodoItems.Add(new TodoItem { Kind = TodoItemKind.Subtask, Text = subtask.Name });
                    }
                }
            }
        }
        else
        {
            TodoItems.Add(new TodoItem { Kind = TodoItemKind.Info, Text = "No Tasks" });
        }
    }
}
