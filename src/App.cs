using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

static class App
{
    public const string Name = "שליחת נתוני חיפוש — זית ואוצריא";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--selftest")
            return SelfTest.Run(args[1]);
        if (args.Length >= 1 && args[0] == "--auto")
            return RunAutoBackground();

        // Ensure auto-send registration in Windows Startup if enabled by default
        try
        {
            if (Prefs.IsAutoSendEnabled())
                Prefs.SetAutoSendEnabled(true);
        }
        catch { }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (s, e) =>
        {
            MessageBox.Show(e.Exception.Message, Name, MessageBoxButton.OK, MessageBoxImage.Error,
                MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            e.Handled = true;
        };
        return app.Run(new MainWindow());
    }

    static int RunAutoBackground()
    {
        if (!Prefs.IsAutoSendEnabled()) return 0;

        bool isNew;
        using (var mutex = new Mutex(true, "ZayitTelemetry_AutoBackgroundWatcher_SingleInstance", out isNew))
        {
            if (!isNew)
            {
                // Another instance is already watching in background. Trigger a single send and exit.
                Service.RunAutoOnce();
                return 0;
            }

            // Initial send on startup
            Service.RunAutoOnce();

            // Periodic background check every 30 minutes while session is alive
            while (Prefs.IsAutoSendEnabled())
            {
                Thread.Sleep(TimeSpan.FromMinutes(30));
                if (!Prefs.IsAutoSendEnabled()) break;
                try
                {
                    if (NetworkInterface.GetIsNetworkAvailable())
                    {
                        Service.RunAutoOnce();
                    }
                }
                catch { }
            }
            return 0;
        }
    }
}
