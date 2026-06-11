using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Net.Http;
using System.Windows.Threading;
using DailyVertViewer.Wpf.Services;
using DailyVertViewer.Wpf.ViewModels;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace DailyVertViewer.Wpf;

public partial class MainWindow : Window
{
    private const int HotkeyId = 1;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VirtualKeyC = 0x43;

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _nowLineTimer;
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _allowClose;
    private bool _hotkeyRegistered;
    private bool _hasShownOnce;

    public MainWindow()
    {
        InitializeComponent();

        var jst = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
        var environment = new AppEnvironment();
        var sharedHttpClient = new HttpClient();
        var dashboardDataService = new DashboardDataService(
            new GoogleCalendarService(jst),
            new TogglService(environment, jst),
            new ClickUpService(sharedHttpClient, environment, jst),
            jst);

        _viewModel = new MainViewModel(dashboardDataService, HideWithAnimation);
        DataContext = _viewModel;
        _nowLineTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _nowLineTimer.Tick += (_, _) => _viewModel.UpdateNowLine();
        _notifyIcon = CreateNotifyIcon();

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += OnStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Width = UiConstants.WindowWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Left = SystemParameters.PrimaryScreenWidth;
        Top = UiConstants.YPosition;
        _viewModel.UpdateWindowMetrics(Height);
        _nowLineTimer.Start();
        Hide();
        await Task.CompletedTask;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        _hotkeyRegistered = RegisterHotKey(handle, HotkeyId, ModAlt | ModControl, VirtualKeyC);
        if (!_hotkeyRegistered)
        {
            var message = "Ctrl+Alt+C hotkey registration failed. Another app may already be using it.";
            _viewModel.SetStatusMessage(message);
            _notifyIcon.ShowBalloonTip(5000, "dailyVertViewer", message, Forms.ToolTipIcon.Warning);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _nowLineTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        var handle = new WindowInteropHelper(this).Handle;
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(handle, HotkeyId);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideWithAnimation();
            return;
        }

        base.OnClosing(e);
    }

    private async void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        _viewModel.HandleKey(e.Key, ShowWithAnimationAsync);
        await Task.CompletedTask;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmHotkey = 0x0312;
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await ToggleWindowAsync();
            });
            handled = true;
        }

        return IntPtr.Zero;
    }

    private async Task ShowWithAnimationAsync()
    {
        if (IsVisible && _hasShownOnce)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            return;
        }

        Height = SystemParameters.PrimaryScreenHeight;
        Left = SystemParameters.PrimaryScreenWidth;
        Top = UiConstants.YPosition;
        _viewModel.UpdateWindowMetrics(Height);
        Show();
        WindowState = WindowState.Normal;
        Activate();

        var animation = new DoubleAnimation
        {
            From = SystemParameters.PrimaryScreenWidth,
            To = SystemParameters.PrimaryScreenWidth - UiConstants.WindowWidth,
            Duration = TimeSpan.FromMilliseconds(UiConstants.SlideDurationMs)
        };

        BeginAnimation(LeftProperty, animation);
        _hasShownOnce = true;
        await Task.Delay(UiConstants.SlideDurationMs);
    }

    private async Task RevealWindowAsync()
    {
        await _viewModel.ShowAsync();
        await ShowWithAnimationAsync();
    }

    private async Task ToggleWindowAsync()
    {
        if (IsVisible)
        {
            HideWithAnimation();
            return;
        }

        await RevealWindowAsync();
    }

    private void HideWithAnimation()
    {
        if (!IsVisible)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            From = Left,
            To = SystemParameters.PrimaryScreenWidth,
            Duration = TimeSpan.FromMilliseconds(UiConstants.SlideDurationMs)
        };
        animation.Completed += (_, _) => Hide();
        BeginAnimation(LeftProperty, animation);
    }

    private Forms.NotifyIcon CreateNotifyIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, async (_, _) =>
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                await RevealWindowAsync();
            });
        });
        menu.Items.Add("Hide", null, (_, _) => Dispatcher.Invoke(HideWithAnimation));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        var notifyIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "dailyVertViewer",
            Visible = true,
            ContextMenuStrip = menu
        };
        notifyIcon.DoubleClick += async (_, _) =>
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                if (IsVisible)
                {
                    HideWithAnimation();
                }
                else
                {
                    await RevealWindowAsync();
                }
            });
        };

        return notifyIcon;
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
