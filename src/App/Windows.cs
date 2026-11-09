using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

// The tool's settings in the registry: HKCU\Software\SearchDataSender, its own key only.
sealed class RegistryStore : IStore
{
    readonly string key;
    public RegistryStore(string key) { this.key = key; }

    public string Get(string name)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(key)) return k == null ? null : k.GetValue(name) as string;
    }

    public void Set(string name, string value)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(key)) k.SetValue(name, value ?? "", RegistryValueKind.String);
    }
}

// Where the tool keeps things on this computer, and the names it registers with Windows.
static class Places
{
    public const string SettingsKey = @"Software\SearchDataSender";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "SearchDataSender";
    public const string ExeName = "SearchDataSender.exe";

    public static string Data
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SearchDataSender"); }
    }

    public static string Ledger { get { return Path.Combine(Data, "ledger.txt"); } }

    public static string InstallDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SearchDataSender"); }
    }

    public static string InstalledExe { get { return Path.Combine(InstallDir, ExeName); } }

    public static string RunningExe { get { return Assembly.GetExecutingAssembly().Location; } }

    // Signals between this tool's processes: stop the background sender; show the open window
    public const string ExitEvent = @"Local\SearchDataSender.Exit";
    public const string ShowEvent = @"Local\SearchDataSender.Show";
    public const string WindowMutex = @"Local\SearchDataSender.Window";
    public const string BackgroundMutex = @"Local\SearchDataSender.Background";
}

// Sending in the background, only when the user turned it on: a copy of the tool in its own folder, started with
// Windows from HKCU\...\Run (nothing else: no scheduled task, no service), which sends when the network comes and every
// hour. Turning it off removes the entry and stops the running copy.
static class Startup
{
    public static bool IsOn
    {
        get
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Places.RunKey)) return k != null && k.GetValue(Places.RunValue) != null;
        }
    }

    // Returns null when done, else what went wrong, in Hebrew.
    public static string TurnOn()
    {
        try
        {
            Install();
            using (var k = Registry.CurrentUser.CreateSubKey(Places.RunKey))
                k.SetValue(Places.RunValue, "\"" + Places.InstalledExe + "\" --background", RegistryValueKind.String);
            Process.Start(new ProcessStartInfo(Places.InstalledExe, "--background") { UseShellExecute = false, CreateNoWindow = true });
            return null;
        }
        catch (Exception e)
        {
            if (e is IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception || e is System.Security.SecurityException)
                return "לא הצלחתי להפעיל את השליחה ברקע: " + e.Message;
            throw;
        }
    }

    public static void TurnOff()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Places.RunKey, true))
                if (k != null) k.DeleteValue(Places.RunValue, false);
        }
        catch (UnauthorizedAccessException) { }
        StopBackground();
    }

    // Copies this exe to the tool's folder, unless that copy is this one or the same file; stops the background copy first.
    static void Install()
    {
        var running = Path.GetFullPath(Places.RunningExe);
        var installed = Path.GetFullPath(Places.InstalledExe);
        if (string.Equals(running, installed, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(installed) && SameFile(running, installed)) return;
        Directory.CreateDirectory(Places.InstallDir);
        StopBackground();
        for (int attempt = 0; ; attempt++)
        {
            try { File.Copy(running, installed, true); return; }
            catch (IOException) { if (attempt >= 20) throw; Thread.Sleep(250); }
        }
    }

    static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        return Ledger.Digest(Convert.ToBase64String(File.ReadAllBytes(a))) == Ledger.Digest(Convert.ToBase64String(File.ReadAllBytes(b)));
    }

    // Asks the background copy, if one runs, to stop, and waits for it a little.
    public static void StopBackground()
    {
        EventWaitHandle exit;
        if (!EventWaitHandle.TryOpenExisting(Places.ExitEvent, out exit)) return;
        using (exit) exit.Set();
        for (int i = 0; i < 40; i++)
        {
            Mutex m;
            if (!Mutex.TryOpenExisting(Places.BackgroundMutex, out m)) return;
            m.Dispose();
            Thread.Sleep(250);
        }
    }
}

// The background sender (SearchDataSender.exe --background): one per user session, sending what is new when the
// network comes back and every hour, while the user's consent and choice to send in the background stand.
static class Background
{
    static readonly TimeSpan Hourly = TimeSpan.FromHours(1);
    static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);
    const long MinGapMs = 10 * 60 * 1000; // a burst of network events sends once

    public static int Run()
    {
        var settings = new Settings(new RegistryStore(Places.SettingsKey));
        if (!settings.HasConsent || !settings.AutoSend)
        {
            // Left over from before the user turned it off: take it out of Windows' startup
            Startup.TurnOff();
            return 0;
        }
        bool first;
        using (var single = new Mutex(true, Places.BackgroundMutex, out first))
        {
            if (!first) return 0;
            using (var exit = new EventWaitHandle(false, EventResetMode.AutoReset, Places.ExitEvent))
            using (var wake = new AutoResetEvent(false))
            {
                System.Net.NetworkInformation.NetworkAddressChangedEventHandler onAddress = (s, e) => wake.Set();
                System.Net.NetworkInformation.NetworkAvailabilityChangedEventHandler onAvailable = (s, e) => { if (e.IsAvailable) wake.Set(); };
                PowerModeChangedEventHandler onPower = (s, e) => { if (e.Mode == PowerModes.Resume) wake.Set(); };
                System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += onAddress;
                System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += onAvailable;
                SystemEvents.PowerModeChanged += onPower;
                try
                {
                    // Right after logging in the network is often not up yet
                    if (WaitHandle.WaitAny(new WaitHandle[] { exit }, TimeSpan.FromMinutes(1)) == 0) return 0;
                    while (true)
                    {
                        if (!settings.HasConsent || !settings.AutoSend) return 0;
                        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (now - settings.LastAttemptAt >= MinGapMs) SendOnce(settings, now);
                        int which = WaitHandle.WaitAny(new WaitHandle[] { exit, wake }, Hourly);
                        if (which == 0) return 0;
                        if (which == 1)
                        {
                            // Let the connection settle, and take the burst of events that came with it as one
                            if (exit.WaitOne(Settle)) return 0;
                            while (wake.WaitOne(0)) { }
                        }
                    }
                }
                finally
                {
                    System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= onAddress;
                    System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= onAvailable;
                    SystemEvents.PowerModeChanged -= onPower;
                }
            }
        }
    }

    static void SendOnce(Settings settings, long now)
    {
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return;
        using (var lk = SendLock.TryTake(0))
        {
            // The window is sending: it will take care of it
            if (lk == null) return;
            settings.LastAttemptAt = now;
            var ledger = new Ledger(Places.Ledger);
            var data = Engine.ReadAll(Readers.For(settings, ZayitPrefs.Read("database_path"), ZayitPrefs.ReadPardes("database_path"),
                () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            var sel = Engine.Select(data, settings, ledger);
            if (sel.IsEmpty) return;
            Engine.Send(sel, settings, ledger, new HttpService(settings.ServiceUrl),
                () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5, 65000, null);
        }
    }
}
