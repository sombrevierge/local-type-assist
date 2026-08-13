using System.Threading;
using System.Windows;
using LocalTypeAssist.Services;

namespace LocalTypeAssist;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
            AppLog.Error("Unhandled WPF dispatcher exception.", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                AppLog.Error("Unhandled AppDomain exception.", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        _singleInstanceMutex = new Mutex(initiallyOwned: true, "LocalTypeAssist.SingleInstance", out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show("Local Type Assist уже запущен. Проверьте значок в системном трее.",
                "Local Type Assist", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            _host = new AppHost();
            _host.Start(e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Не удалось запустить Local Type Assist",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _host?.PrepareForExit();
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
