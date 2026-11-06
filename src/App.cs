using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

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
            try
            {
                if (NetworkInterface.GetIsNetworkAvailable())
                    Service.RunAutoOnce();
            }
            catch { }

            // Reactive event loop: wakes up instantly on ANY network connection, address assignment, or PC resume
            using (var wakeEvent = new AutoResetEvent(false))
            {
                NetworkAddressChangedEventHandler onAddr = (s, e) => wakeEvent.Set();
                NetworkAvailabilityChangedEventHandler onAvail = (s, e) => { if (e.IsAvailable) wakeEvent.Set(); };
                PowerModeChangedEventHandler onPower = (s, e) => { if (e.Mode == PowerModes.Resume) wakeEvent.Set(); };

                NetworkChange.NetworkAddressChanged += onAddr;
                NetworkChange.NetworkAvailabilityChanged += onAvail;
                SystemEvents.PowerModeChanged += onPower;

                try
                {
                    while (Prefs.IsAutoSendEnabled())
                    {
                        // Wait up to 30 minutes, or wake up IMMEDIATELY when network connects / changes or PC resumes
                        bool triggered = wakeEvent.WaitOne(TimeSpan.FromMinutes(30));
                        if (!Prefs.IsAutoSendEnabled()) break;

                        if (triggered)
                        {
                            // Network changed or reconnected: wait 3 seconds for DHCP/DNS and routing to stabilize
                            Thread.Sleep(3000);
                            // Drain any queued triggers during the delay
                            while (wakeEvent.WaitOne(0)) { }
                        }

                        try
                        {
                            if (NetworkInterface.GetIsNetworkAvailable())
                            {
                                Service.RunAutoOnce();
                            }
                        }
                        catch { }
                    }
                }
                finally
                {
                    NetworkChange.NetworkAddressChanged -= onAddr;
                    NetworkChange.NetworkAvailabilityChanged -= onAvail;
                    SystemEvents.PowerModeChanged -= onPower;
                }
            }

            return 0;
        }
    }
}
