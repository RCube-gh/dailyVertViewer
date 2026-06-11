using System.Threading;

namespace DailyVertViewer.Wpf;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        const string mutexName = @"Local\dailyVertViewer.Wpf";
        _singleInstanceMutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}
