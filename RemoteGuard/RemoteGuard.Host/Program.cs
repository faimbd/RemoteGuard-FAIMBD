using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteGuard.Host.Network;
using RemoteGuard.Host.Power;
using RemoteGuard.Host.Security;
using RemoteGuard.Host.Service;
using RemoteGuard.Host.UI;

namespace RemoteGuard.Host;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => a.Equals("--service", StringComparison.OrdinalIgnoreCase)))
        {
            RunAsWindowsService(args);
            return;
        }

        if (args.Any(a => a.Equals("--agent", StringComparison.OrdinalIgnoreCase)))
        {
            RunAsSessionAgent();
            return;
        }

        ApplicationConfiguration.Initialize();
        var store = new SecurityStore();
        var powerGuard = new PowerGuardService();
        var server = new RemoteWebSocketServer(store);
        var tunnel = new TunnelManager();

        Application.Run(new MainForm(store, powerGuard, server, tunnel));
    }

    private static void RunAsWindowsService(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = RemoteGuardWindowsService.ServiceName;
        });
        builder.Services.AddHostedService<RemoteGuardWindowsService>();
        var host = builder.Build();
        host.Run();
    }

    /// <summary>
    /// Runs inside the active console session (spawned by LocalSystem service)
    /// so it can capture and inject input on both Default desktop AND Windows Lock Screen.
    /// </summary>
    private static void RunAsSessionAgent()
    {
        Capture.DesktopCaptureAndInput.EnablePerMonitorDpiAwareness();
        var store = new SecurityStore();
        using var powerGuard = new PowerGuardService();
        if (store.Config.KeepAwake24x7)
            powerGuard.StartKeepAwake(true);

        using var server = new RemoteWebSocketServer(store);
        server.Start();

        using var exitEvent = new ManualResetEventSlim(false);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => exitEvent.Set();
        exitEvent.Wait();
    }
}
