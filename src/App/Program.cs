using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
[assembly: System.Reflection.AssemblyTitle("שליחת נתוני חיפוש")]
[assembly: System.Reflection.AssemblyVersion(AppInfo.Version + ".0")]
[assembly: System.Reflection.AssemblyFileVersion(AppInfo.Version + ".0")]

// SearchDataSender.exe: the window; --background: the background sender (only when the user turned it on);
// --uninstall [--quiet]: what the uninstaller runs before it removes the files (Uninstall.cs);
// --selftest <folder>: the tests, with a report and pictures of the windows in <folder>.
static class Program
{
    static Mutex single;

    // Lets go of the window's mutex, which the uninstaller waits for, before the window starts it
    public static void LeaveWindow()
    {
        if (single == null) return;
        try { single.ReleaseMutex(); }
        catch (ApplicationException) { }
        single = null;
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--selftest")
            return SelfTest.Run(args.Length >= 2 ? args[1] : Path.Combine(Path.GetTempPath(), "search-data-sender-selftest"));
        if (args.Length >= 1 && args[0] == "--background")
            return Background.Run();
        if (args.Length >= 1 && args[0] == "--uninstall")
            return Uninstall.Run(Array.IndexOf(args, "--quiet") > 0);

        bool first;
        var mutex = new Mutex(true, Places.WindowMutex, out first);
        using (mutex)
        {
            if (!first)
            {
                // Already open: bring that window forward
                EventWaitHandle show;
                if (EventWaitHandle.TryOpenExisting(Places.ShowEvent, out show)) using (show) show.Set();
                return 0;
            }
            single = mutex;
            var deps = Deps.ForThisComputer();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show(e.Exception.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error,
                    MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
                e.Handled = true;
            };
            if (!deps.Settings.HasConsent)
            {
                var consent = new ConsentWindow(deps.Settings, deps.Now);
                if (consent.ShowDialog() != true) return 0;
                if (consent.AutoSendChosen) deps.SetAutoSend(true);
            }
            var window = new MainWindow(deps);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Places.ShowEvent);
            ThreadPool.RegisterWaitForSingleObject(showEvent, (o, timedOut) => window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            })), null, -1, false);
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            return app.Run(window);
        }
    }
}

// What the windows work with: on this computer, the registry, the ledger file, the apps' folders and the service;
// in the self-test, made-up ones.
sealed class Deps
{
    public Settings Settings;
    public Func<Ledger> OpenLedger;
    public Func<List<IReader>> MakeReaders;
    public Func<IService> MakeService;
    public bool ManageStartup;   // false in the self-test, which never touches Windows' startup
    public Func<long> Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static Deps ForThisComputer()
    {
        var settings = new Settings(new RegistryStore(Places.SettingsKey));
        var d = new Deps { Settings = settings, ManageStartup = true };
        d.OpenLedger = () => new Ledger(Places.Ledger);
        d.MakeReaders = () => Readers.For(settings, ZayitPrefs.Read("database_path"), ZayitPrefs.ReadPardes("database_path"), d.Now);
        d.MakeService = () => new HttpService(settings.ServiceUrl);
        return d;
    }

    // Turns sending in the background on or off; returns null, or what went wrong.
    public string SetAutoSend(bool on)
    {
        Settings.AutoSend = on;
        if (!ManageStartup) return null;
        if (!on) { Startup.TurnOff(); return null; }
        var error = Startup.TurnOn();
        if (error != null) Settings.AutoSend = false;
        return error;
    }
}

// The question asked before anything is read or sent: what the tool does, that what it sends is published to
// everyone, what it never sends, and what the user can do about it. Nothing happens without "agree".
sealed class ConsentWindow : Window
{
    readonly CheckBox auto;
    public bool AutoSendChosen;

    public ConsentWindow(Settings settings, Func<long> now)
    {
        Theme.Dress(this);
        Title = AppInfo.Name;
        Icon = Theme.Image("app.png");
        var area = SystemParameters.WorkArea;
        Width = Math.Min(600, area.Width - 40);
        Height = Math.Min(760, area.Height - 30);
        MinWidth = 460;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 18) };
        var head = Theme.T("לפני שמתחילים", 22, "TEXT", true);
        head.Margin = new Thickness(2, 0, 0, 10);
        body.Children.Add(head);

        body.Children.Add(Theme.Card(
            Theme.Title("מה הכלי עושה"),
            Para("הוא קורא במחשב הזה את החיפושים שעשית בזית, בפרדס ובאוצריא, את הדירוגים שנתת לתוצאות בפרדס ובחיפוש החכם של אוצריא, ואת הסימונים שלך בצורת הדף של פרדס, ושולח אותם למאגר החיפושים הפתוח. המטרה: לשפר את החיפוש בכל תוכנות הספרייה התורנית."),
            Theme.Title("מה מתפרסם"),
            Para("כל מה שנשלח מתפרסם באתר המאגר, לכולם, וזמין להורדה: המילים שחיפשת, היקף החיפוש, כמה פעמים ומתי, הדירוגים שנתת והסימונים בצורת הדף. אם חיפשת משהו שאינך רוצה שיתפרסם, אפשר להוריד אותו מהרשימה לפני השליחה."),
            Theme.Title("מה לא נשלח"),
            Para("שם, חשבון, כתובת IP, פרטי המחשב, הערות, סימניות, והספרים שפתחת. כל השליחות מהמחשב הזה מסומנות במספר אקראי, שלא מוצג באתר."),
            Theme.Title("השליטה שלך"),
            Para("לפני כל שליחה רואים מה יישלח, ואפשר להוריד ממנו פריטים. אפשר למחוק מהשרת את כל מה ששלחת, ולבטל את ההסכמה בכל עת.")));

        auto = new CheckBox { Content = "לשלוח לבד ברקע: בהפעלת המחשב, בכל חיבור לרשת ופעם בשעה", IsChecked = false, Margin = new Thickness(2, 4, 0, 6) };
        body.Children.Add(auto);
        var site = Theme.Btn(this, "לאתר המאגר", "Link", () => OpenSite());
        site.HorizontalAlignment = HorizontalAlignment.Left;
        site.Margin = new Thickness(2, 0, 0, 4);
        body.Children.Add(site);

        var agree = Theme.Btn(this, "מסכים, להמשיך", "Default", () =>
        {
            settings.GiveConsent(now());
            AutoSendChosen = auto.IsChecked == true;
            DialogResult = true;
        });
        var later = Theme.Btn(this, "לא עכשיו", "Outlined", () => { DialogResult = false; });
        body.Children.Add(Theme.Row(agree, later));

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(Theme.TitleBar(this, AppInfo.Name, Theme.Image("app.png")));
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;
    }

    static TextBlock Para(string text)
    {
        var t = Theme.T(text, 13.5, "TEXT");
        t.Margin = new Thickness(0, 0, 0, 12);
        t.LineHeight = 21;
        return t;
    }

    public static void OpenSite()
    {
        try { Process.Start(new ProcessStartInfo(AppInfo.SiteUrl) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

// Native Windows folder picker (SHBrowseForFolderW in shell32.dll).
static class FolderPicker
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHBrowseForFolderW(ref BROWSEINFO bi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool SHGetPathFromIDListW(IntPtr pidl, [Out] char[] pszPath);

    [DllImport("ole32.dll")]
    static extern void CoTaskMemFree(IntPtr pv);

    public static string Pick(Window owner, string title)
    {
        var name = Marshal.AllocHGlobal(520);
        var pidl = IntPtr.Zero;
        try
        {
            var bi = new BROWSEINFO
            {
                hwndOwner = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero,
                pszDisplayName = name,
                lpszTitle = title,
                ulFlags = 0x00000041 // BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE
            };
            pidl = SHBrowseForFolderW(ref bi);
            if (pidl == IntPtr.Zero) return null;
            var buf = new char[260];
            return SHGetPathFromIDListW(pidl, buf) ? new string(buf).TrimEnd('\0') : null;
        }
        finally
        {
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
            Marshal.FreeHGlobal(name);
        }
    }
}
