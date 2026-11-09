using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

// SearchDataSender.exe --selftest <folder>: the core's tests (tests/*.cs), then this computer's: the registry, and the
// windows built from made-up data and drawn to pictures in <folder> (never shown, never touching Windows' startup or
// the real settings). The report is <folder>\selftest-report.txt; the exit code is 0 when everything passed.
static class SelfTest
{
    static int failed;
    static Action<string> log;

    static void Check(bool ok, string name, string detail = "")
    {
        if (!ok) failed++;
        log((ok ? "PASS  " : "FAIL  ") + name + (ok || detail == "" ? "" : "  [" + detail + "]"));
    }

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var report = new StringBuilder();
        log = line => { report.AppendLine(line); Console.WriteLine(line); };
        failed = CoreTests.Run(Path.Combine(dir, "core"), log);
        foreach (var t in new Action<string>[] { TestRegistry, TestWindows })
        {
            try { t(dir); }
            catch (Exception e) { Check(false, t.Method.Name + " threw", e.GetType().Name + ": " + e.Message + " " + e.StackTrace); }
        }
        log(failed == 0 ? "ALL PASSED" : failed + " FAILED");
        File.WriteAllText(Path.Combine(dir, "selftest-report.txt"), report.ToString(), new UTF8Encoding(false));
        return failed == 0 ? 0 : 1;
    }

    static void TestRegistry(string dir)
    {
        const string key = Places.SettingsKey + ".SelfTest";
        try
        {
            var store = new RegistryStore(key);
            store.Set("x", "חזקה");
            Check(store.Get("x") == "חזקה" && store.Get("missing") == null, "registry: the tool's own settings");
            var s = new Settings(store);
            Check(!s.HasConsent && !s.AutoSend, "registry: no consent and no background sending to begin with");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
        Check(ZayitPrefs.Read("search-data-sender-missing-value") == null, "registry: a missing Zayit preference is null");
    }

    sealed class OkService : IService
    {
        public int Posts;
        public Answer Post(string json) { Posts++; return new Answer { Outcome = Outcome.Ok, Status = 201 }; }
        public Answer DeleteSender(string sender) { return new Answer { Outcome = Outcome.Ok, Status = 200 }; }
    }

    sealed class FixedReader : IReader
    {
        readonly SourceData data;
        public FixedReader(SourceData d) { data = d; }
        public SourceData Read() { return data; }
    }

    static List<SourceData> Sample(bool found)
    {
        var z = new SourceData { Source = Source.Zayit, Found = found, Library = "20261012193827" };
        var pa = new SourceData { Source = Source.Pardes, Found = found, Library = "20261012193827" };
        var o = new SourceData { Source = Source.Otzaria, Found = found };
        if (!found) return new List<SourceData> { z, pa, o };
        z.AddPlace(@"C:\Users\user\AppData\Roaming\io.github.kdroidfilter.seforimapp\databases\settings");
        pa.AddPlace(@"C:\Users\user\AppData\Roaming\io.github.efrul.pardes\databases\settings");
        o.AddPlace(@"C:\Users\user\AppData\Roaming\otzaria\databases");
        long t = 1794218400000L;
        z.Searches.Add(new SearchRow { Source = Source.Zayit, Query = "חזקת הבתים", Count = 3, LastAt = t });
        z.Searches.Add(new SearchRow { Source = Source.Zayit, Query = "שלוש שנים", BookId = 72, Extended = true, Count = 1, LastAt = t - 3600000 });
        z.Searches.Add(new SearchRow { Source = Source.Zayit, Query = "המוציא מחבירו עליו הראיה", Count = 2, LastAt = t - 7200000 });
        pa.Searches.Add(new SearchRow { Source = Source.Pardes, Query = "קים ליה בדרבה מיניה", Count = 2, LastAt = t - 120000 });
        pa.Grades.Add(new GradeRow { Source = Source.Pardes, Query = "קים ליה בדרבה מיניה", Search = "search:g:קים ליה בדרבה מיניה", LineId = 26579,
            BookId = 72, Grade = GradeRow.Exact, Rank = 0, GradedAt = t - 90000, Model = "student@695cbe2/pardes-clean-1" });
        pa.Marks.Add(new DafMark { Tractate = "Berakhot", TractateHe = "ברכות", Library = "20261012193827", Amud = "2a", C = "g", I = 1,
            T = "מאימתי", Ok = true, At = "2026-11-01T09:00:00Z", AtMs = 1793523600000L });
        o.Searches.Add(new SearchRow { Source = Source.Otzaria, Query = "ספק ספיקא", Scope = "תלמוד בבלי › סדר נזיקין", LastAt = t, TimeUnknown = true });
        o.Searches.Add(new SearchRow { Source = Source.Otzaria, Query = "אין מבטלין", Ref = "חיפוש חכם", LastAt = t - 60000 });
        o.Grades.Add(new GradeRow { Source = Source.Otzaria, Query = "אין מבטלין", Search = "otzaria:smart:s1", Grade = GradeRow.Relevant, Rank = 1,
            BookTitle = "משנה ברכות", HeRef = "ברכות א, א", DocId = "17", GradedAt = t - 30000, Model = "otzaria-smart-search" });
        o.Problems.Add("ההיסטוריה של אוצריא שמורה כאן בפורמט ישן (history.hive) שהכלי לא קורא. אחרי עדכון אוצריא לגרסה 0.9.98 ומעלה היא תיקרא.");
        return new List<SourceData> { z, pa, o };
    }

    static Deps FakeDeps(string dir, List<SourceData> data, IService service)
    {
        var settings = new Settings(new MemoryStore());
        settings.GiveConsent(1);
        var ledgerPath = Path.Combine(dir, "ui-ledger.txt");
        var readers = new List<IReader>();
        foreach (var d in data) readers.Add(new FixedReader(d));
        return new Deps
        {
            Settings = settings,
            OpenLedger = () => new Ledger(ledgerPath),
            MakeReaders = () => readers,
            MakeService = () => service,
            ManageStartup = false,
            Now = () => 1794218400000L
        };
    }

    static void TestWindows(string dir)
    {
        var consent = new ConsentWindow(new Settings(new MemoryStore()), () => 1L);
        Shot(consent, Path.Combine(dir, "consent.png"), 600, 1020);
        Check(true, "windows: the consent window is built");

        var svc = new OkService();
        var data = Sample(true);
        var deps = FakeDeps(dir, data, svc);
        var w = new MainWindow(deps);
        w.ApplyRead(data);
        Shot(w, Path.Combine(dir, "main.png"), 660, 1600);
        Check(true, "windows: the main window is built with data");
        w.ToggleDetails();
        Shot(w, Path.Combine(dir, "main-details.png"), 660, 2000);

        var ledger = deps.OpenLedger();
        var sel = Engine.Select(data, deps.Settings, ledger);
        var result = Engine.Send(sel, deps.Settings, ledger, svc, deps.Now, 0, 0, null);
        w.ApplySendResult(result);
        Check(result.Complete && svc.Posts == 1 && deps.OpenLedger().SentCount == 9, "windows: sending from the window's data", svc.Posts + " posts");
        Shot(w, Path.Combine(dir, "main-sent.png"), 660, 1500);
        Check(Engine.Select(data, deps.Settings, deps.OpenLedger()).IsEmpty, "windows: after sending, nothing new");

        var empty = Sample(false);
        var none = new MainWindow(FakeDeps(Path.Combine(dir, "none"), empty, svc));
        Directory.CreateDirectory(Path.Combine(dir, "none"));
        none.ApplyRead(empty);
        Shot(none, Path.Combine(dir, "main-nothing.png"), 660, 1100);
        Check(true, "windows: the main window is built with nothing found");
    }

    // Draws [w]'s content, unshown, to a PNG at twice the size.
    static void Shot(Window w, string file, double width, double height)
    {
        var root = (FrameworkElement)w.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)(width * 2), (int)(height * 2), 192, 192, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(Theme.B("CANVAS"), null, new Rect(0, 0, width, height));
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create(file)) enc.Save(f);
    }
}
