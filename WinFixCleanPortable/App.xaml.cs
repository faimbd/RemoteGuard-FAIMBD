using System.Windows;
using System.Windows.Threading;
using WinFixCleanPortable.Helpers;

namespace WinFixCleanPortable;

public partial class App : System.Windows.Application
{
    /// <summary>Hidden diagnostic mode: "--selftest" renders every page/popup, logs results and exits.</summary>
    public static bool IsSelfTest { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        IsSelfTest = e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));

        // Attach global crash handlers BEFORE anything renders
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppLogger.LogInfo("============================================================");
        AppLogger.LogInfo($"FaimFixer (WinFix & Clean Portable) v1.5.0 Starting...{(IsSelfTest ? " [SELFTEST MODE]" : "")}");
        AppLogger.LogInfo($"OS: {FormatHelper.GetWindowsFriendlyName()} | 64-bit OS: {Environment.Is64BitOperatingSystem}");
        AppLogger.LogInfo($".NET Runtime: {Environment.Version} | Admin Mode: {FormatHelper.IsRunningAsAdministrator()}");
        AppLogger.LogInfo($"Executable: {Environment.ProcessPath}");
        AppLogger.LogInfo($"Log Path: {AppLogger.LogFilePath}");

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.LogInfo($"Application exiting normally (ExitCode={e.ApplicationExitCode}).");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.LogError("UI Unhandled Exception", e.Exception);
        e.Handled = true;
        if (IsSelfTest)
        {
            AppLogger.LogInfo("SELFTEST RESULT: FAIL (unhandled UI exception)");
            Shutdown(2);
            return;
        }
        System.Windows.MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\n" +
            $"Full crash details have been saved to:\n{AppLogger.LogFilePath}",
            "FaimFixer — Error Log Saved",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            AppLogger.LogError("Fatal Domain Unhandled Exception", ex);
            if (IsSelfTest) return;
            System.Windows.MessageBox.Show(
                $"A fatal error occurred during startup or execution:\n\n{ex.Message}\n\n" +
                $"Full diagnostic log saved to:\n{AppLogger.LogFilePath}",
                "FaimFixer — Crash Report",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.LogError("Background Task Exception", e.Exception);
        e.SetObserved();
    }
}
