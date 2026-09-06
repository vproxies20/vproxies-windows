using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace VProxies;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\VProxies.Windows.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("VProxies is already running. Check the taskbar notification area.", "VProxies", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ShowStartupFailure(ex);
            Shutdown(1);
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowStartupFailure(e.Exception);
        e.Handled = true;
        Shutdown(1);
    }

    private static void ShowStartupFailure(Exception exception)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VProxies");
        var logPath = Path.Combine(directory, "startup-crash.log");
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}] VProxies startup failure\r\n{exception}\r\n\r\n");
        }
        catch { }
        System.Windows.MessageBox.Show($"VProxies could not start.\n\n{exception.Message}\n\nDiagnostic log: {logPath}", "VProxies Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
