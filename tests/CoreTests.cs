using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

// The core's tests: run on Linux with Mono (tools/test-core.sh) and on Windows by SearchDataSender.exe --selftest.
// Each check prints a line; Run returns how many failed.
public static partial class CoreTests
{
    static int failed, passed;
    static Action<string> log;

    static void Check(bool ok, string name, string detail = "")
    {
        if (ok) passed++; else failed++;
        log((ok ? "PASS  " : "FAIL  ") + name + (ok || detail == "" ? "" : "  [" + detail + "]"));
    }

    public static int Run(string workDir, Action<string> output)
    {
        log = output;
        failed = 0;
        passed = 0;
        Directory.CreateDirectory(workDir);
        var tests = new List<KeyValuePair<string, Action<string>>>
        {
            new KeyValuePair<string, Action<string>>("json", d => TestJson()),
            new KeyValuePair<string, Action<string>>("model", d => TestModel()),
            new KeyValuePair<string, Action<string>>("ledger", TestLedger),
            new KeyValuePair<string, Action<string>>("settings", d => TestSettings()),
            new KeyValuePair<string, Action<string>>("answers", d => TestAnswers()),
            new KeyValuePair<string, Action<string>>("select", TestSelect),
            new KeyValuePair<string, Action<string>>("send", TestSend),
            new KeyValuePair<string, Action<string>>("http", d => TestHttp()),
            new KeyValuePair<string, Action<string>>("sqlite", TestSqlite),
        };
        tests.AddRange(ReaderTests());
        foreach (var t in tests)
        {
            var dir = Path.Combine(workDir, t.Key);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            try { t.Value(dir); }
            catch (Exception e) { Check(false, t.Key + " threw", e.GetType().Name + ": " + e.Message + " " + e.StackTrace); }
        }
        log(string.Format("{0} passed, {1} failed", passed, failed));
        return failed;
    }

    // ---- JSON ----

    static void TestJson()
    {
        var o = (Dictionary<string, object>)Json.Parse("\uFEFF{\"a\": [1, -2.5, \"x\\u05d0\\n\", true, null], \"b\": {\"c\": 9007199254740993}}");
        var a = (List<object>)o["a"];
        Check(a.Count == 5 && (long)a[0] == 1 && (double)a[1] == -2.5 && (string)a[2] == "xא\n" && (bool)a[3] && a[4] == null, "json: parses values");
        Check((long)((Dictionary<string, object>)o["b"])["c"] == 9007199254740993L, "json: keeps a long whole");
        foreach (var bad in new[] { "{", "{\"a\":1,}", "[1 2]", "01", "1.", "\"\\x\"", "{\"a\":1} x", "nul", "\"a\nb\"" })
            Check(Json.TryParse(bad) == null && !(bad == "nul" && false), "json: refuses " + bad.Replace("\n", "\\n"));
        var map = new JsonMap().Add("z", 1L).Add("a", "\u05d0\"\\").Add("none", null).Add("list", new List<object> { 1L, "\ud800" });
        var text = Json.Write(map, false);
        Check(text == "{\"z\":1,\"a\":\"\u05d0\\\"\\\\\",\"list\":[1,\"\\ud800\"]}", "json: writes in order, escapes, leaves out null", text);
        var back = (Dictionary<string, object>)Json.Parse(Json.Write(map, true));
        Check((string)back["a"] == "\u05d0\"\\", "json: indented output reads back");
        Check(Json.Long(Json.TryParseObject("{\"n\": 5.0, \"s\": \"7\"}"), "n") == 5 && Json.Long(Json.TryParseObject("{\"s\": \"7\"}"), "s") == 7, "json: typed long reading");
    }

    // ---- Rows and packages ----

    static SearchRow Search(string q, long at, long count = 1)
    {
        return new SearchRow { Source = Source.Zayit, Query = q, LastAt = at, Count = count };
    }

    static GradeRow Grade(string q, long line, int grade, long at)
    {
        return new GradeRow { Source = Source.Zayit, Query = q, Search = "search:g:" + q, LineId = line, BookId = 7, Grade = grade, Rank = 1, GradedAt = at };
    }

    static void TestModel()
    {
        var s = Search("חזקת הבתים", 1000);
        var again = Search("חזקת הבתים", 2000, 2);
        Check(s.Key == again.Key && s.Fingerprint != again.Fingerprint, "model: a search made again keeps its key and changes its fingerprint");
        Check(Search("x", 1).Key != new SearchRow { Source = Source.Otzaria, Query = "x", LastAt = 1 }.Key, "model: the app is part of a search's key");
        Check(!Search("", 1).Valid() && !Search(new string('א', Limits.MaxQuery + 1), 1).Valid() && Search("א", 1).Valid(), "model: a search's limits");
        Check(!Grade("q", 1, 1, 1).Valid() && Grade("q", 1, 3, 1).Valid() && !Grade("q", -1, 2, 1).Valid(), "model: a grade's limits");
        var g1 = Grade("q", 5, 2, 10);
        var g2 = Grade("q", 5, 0, 20);
        Check(g1.Key == g2.Key && g1.Fingerprint != g2.Fingerprint, "model: a grade changed keeps its key");

        var pkg = new Package { Sender = "aaaaaaaa-1111-4111-8111-111111111111", SentAt = "2026-11-09T12:00:00Z", Library = "v3" };
        pkg.Searches.Add(s);
        pkg.Grades.Add(g1);
        pkg.Marks.Add(new DafMark { Tractate = "Berakhot", TractateHe = "ברכות", Amud = "2a", C = "g", I = 1, T = "שורה", Ok = true, At = "2026-11-01T09:00:00Z" });
        pkg.Marks.Add(new DafMark { Tractate = "Berakhot", TractateHe = "ברכות", Amud = "2a", C = "g", I = 2, T = "", Ok = false });
        var json = (Dictionary<string, object>)Json.Parse(pkg.ToCompactJson());
        Check(Json.Long(json, "format") == 1 && Json.Str(json, "app") == "Pardes" && Json.Bool(json, "publish") == true &&
            Json.Str(json, "appVersion") == AppInfo.AppVersion && Json.Str(json, "sender") == pkg.Sender, "model: a package says format 1, publish, its version and sender");
        var daf = Json.Arr(json, "daf");
        Check(daf.Count == 1 && Json.Arr((Dictionary<string, object>)daf[0], "marks").Count == 2, "model: marks of a tractate are one review");
        var search = (Dictionary<string, object>)Json.Arr(json, "searches")[0];
        Check(Json.Str(search, "source") == "zayit" && !search.ContainsKey("scope") && !search.ContainsKey("bookId"), "model: a search leaves out what it lacks");

        var big = new Package { Sender = pkg.Sender, SentAt = pkg.SentAt };
        for (int i = 0; i < 1300; i++) big.Searches.Add(Search(new string('ק', 900) + i, i));
        for (int i = 0; i < 30; i++) big.Grades.Add(Grade("q" + i, i, 2, i));
        var parts = big.Parts(Limits.PartBytes);
        int rows = 0;
        bool fits = true;
        foreach (var p in parts) { rows += p.Rows; fits &= p.Bytes <= Limits.PartBytes; }
        Check(parts.Count >= 2 && fits && rows == big.Rows, "model: a big package splits into parts that fit, keeping every row", parts.Count + " parts");
        Check(new Package().Parts(Limits.PartBytes).Count == 0 && pkg.Parts(Limits.PartBytes).Count == 1, "model: no part for nothing, one for a small package");
    }

    // ---- The ledger ----

    static void TestLedger(string dir)
    {
        var path = Path.Combine(dir, "ledger.txt");
        var l = new Ledger(path);
        var s = Search("q", 1);
        l.MarkSent(new[] { s.Fingerprint });
        l.SetExcluded("key-x", true);
        l.MarkRefused("fp-r");
        var again = new Ledger(path);
        Check(again.WasSent(s.Fingerprint) && !again.WasSent(Search("q", 2).Fingerprint), "ledger: what was sent is kept, a change is new");
        Check(again.IsExcluded("key-x") && again.WasRefused("fp-r"), "ledger: exclusions and refusals are kept");
        again.SetExcluded("key-x", false);
        Check(!new Ledger(path).IsExcluded("key-x"), "ledger: an exclusion can be undone");
        Check(!File.ReadAllText(path).Contains("q"), "ledger: keeps digests, never a search's words");
        var many = new List<string>();
        for (int i = 0; i < 3000; i++) many.Add("fp" + i);
        var l2 = new Ledger(path);
        for (int i = 0; i < 1200; i++) { l2.SetExcluded("k" + i, true); l2.SetExcluded("k" + i, false); }
        l2.MarkSent(many);
        var before = File.ReadAllLines(path).Length;
        var l3 = new Ledger(path);
        Check(File.ReadAllLines(path).Length == before, "ledger: reading never rewrites the file");
        l3.Tidy();
        Check(new Ledger(path).WasSent("fp2999") && File.ReadAllLines(path).Length < before && !new Ledger(path).IsExcluded("k5"),
            "ledger: tidying drops lines that later ones cancelled", before + " lines before");
        // Two writers at once (the window and the background sender) both get their lines in
        var w1 = new Ledger(path);
        var w2 = new Ledger(path);
        var t1 = new Thread(() => { for (int i = 0; i < 200; i++) w1.MarkSent(new[] { "a" + i }); });
        var t2 = new Thread(() => { for (int i = 0; i < 200; i++) w2.MarkSent(new[] { "b" + i }); });
        t1.Start(); t2.Start(); t1.Join(); t2.Join();
        var both = new Ledger(path);
        Check(both.WasSent("a199") && both.WasSent("b199") && both.WasSent("a0") && both.WasSent("b0"), "ledger: two writers at once lose nothing");
    }

    // ---- Settings ----

    static void TestSettings()
    {
        var st = new Settings(new MemoryStore());
        Check(!st.HasConsent && !st.AutoSend, "settings: no consent and no background sending until the user agrees");
        Check(st.SearchesOn && st.GradesOn && st.DafOn && st.IsSourceOn(Source.Zayit) && st.IsSourceOn(Source.Otzaria), "settings: every kind and app on by default");
        var id = st.Sender;
        Guid g;
        Check(Guid.TryParseExact(id, "D", out g) && st.Sender == id, "settings: a random sender, kept");
        Check(st.NewSender() != id, "settings: a new sender on asking");
        st.GiveConsent(5);
        Check(st.HasConsent, "settings: consent given");
        st.AutoSend = true;
        st.WithdrawConsent();
        Check(!st.HasConsent && !st.AutoSend, "settings: withdrawing consent stops background sending");
        Check(st.ServiceUrl == AppInfo.DefaultServiceUrl || Environment.GetEnvironmentVariable("SEARCH_DATA_SERVICE_URL") != null, "settings: the service's address by default");
    }

    // ---- The service's answers ----

    static void TestAnswers()
    {
        Check(HttpService.Read(201, "{\"id\":4}").Ok, "answers: 201 is taken");
        Check(HttpService.Read(429, "{\"error\":\"too many packages today\"}").Outcome == Outcome.DailyLimit, "answers: the daily limit");
        Check(HttpService.Read(429, "{\"error\":\"too many requests, try again later\"}").Outcome == Outcome.Busy, "answers: the minute's limit");
        var row = HttpService.Read(400, "{\"error\":\"search 3 is not one (query: 20001 characters, at most 20000)\"}");
        Check(row.Outcome == Outcome.RowRefused && row.RowKind == "search" && row.RowIndex == 2, "answers: a refused row, from 0");
        Check(HttpService.Read(400, "{\"error\":\"daf review 1 is not one (mark 2: i)\"}").RowKind == "daf review", "answers: a refused daf review");
        Check(HttpService.Read(400, "{\"error\":\"no sender id\"}").Outcome == Outcome.Refused, "answers: a refused package");
        Check(HttpService.Read(503, "").Outcome == Outcome.ServerError && HttpService.Read(400, "<html>").Outcome == Outcome.Refused, "answers: errors without JSON");
    }

    // ---- Selecting and sending ----

    static SourceData Data(Source src, params SearchRow[] searches)
    {
        var d = new SourceData { Source = src, Found = true };
        foreach (var s in searches) { s.Source = src; d.Searches.Add(s); }
        return d;
    }

    static void TestSelect(string dir)
    {
        var st = new Settings(new MemoryStore());
        var l = new Ledger(Path.Combine(dir, "l.txt"));
        var z = Data(Source.Zayit, Search("a", 1), Search("b", 2), Search("c", 3), Search("", 4));
        z.Grades.Add(Grade("a", 1, 2, 5));
        var o = Data(Source.Otzaria, Search("d", 6));
        l.MarkSent(new[] { z.Searches[0].Fingerprint });
        l.SetExcluded(z.Searches[1].Key, true);
        var sel = Engine.Select(new[] { z, o }, st, l);
        Check(sel.Searches.Count == 2 && sel.Searches[0].Query == "c" && sel.Searches[1].Query == "d", "select: leaves out what was sent, excluded or invalid");
        Check(sel.ExcludedSearches.Count == 1 && sel.ExcludedSearches[0].Query == "b" && sel.Grades.Count == 1, "select: the excluded apart");
        st.SetSourceOn(Source.Otzaria, false);
        st.GradesOn = false;
        sel = Engine.Select(new[] { z, o }, st, l);
        Check(sel.Searches.Count == 1 && sel.Grades.Count == 0, "select: apps and kinds turned off");
        z.Searches[0].Count = 2;
        z.Searches[0].LastAt = 10;
        Check(Engine.Select(new[] { z }, st, l).Searches.Exists(x => x.Query == "a"), "select: a search made again is new");
    }

    sealed class FakeService : IService
    {
        public readonly List<string> Posted = new List<string>();
        public Func<int, string, Answer> Respond = (n, json) => new Answer { Outcome = Outcome.Ok, Status = 201 };
        public string Deleted;
        public Answer Post(string json) { Posted.Add(json); return Respond(Posted.Count, json); }
        public Answer DeleteSender(string sender) { Deleted = sender; return new Answer { Outcome = Outcome.Ok, Status = 200 }; }
    }

    static void TestSend(string dir)
    {
        var st = new Settings(new MemoryStore());
        var l = new Ledger(Path.Combine(dir, "l.txt"));
        var d = new SourceData { Source = Source.Zayit, Found = true };
        for (int i = 0; i < 1300; i++) d.Searches.Add(Search(new string('ש', 900) + i, i));
        var svc = new FakeService();
        // The second part fails: the first stays sent, and only the rest goes the next time
        svc.Respond = (n, json) => n == 2 ? new Answer { Outcome = Outcome.Offline } : new Answer { Outcome = Outcome.Ok };
        Func<long> now = () => 1793800000000L;
        var r = Engine.Send(Engine.Select(new[] { d }, st, l), st, l, svc, now, 0, 0, null);
        int firstPart = r.Searches;
        Check(!r.Complete && r.StoppedBy.Outcome == Outcome.Offline && r.Parts == 1 && firstPart > 0, "send: stops at a failed part", r.Parts + " parts");
        Check(l.SentCount == firstPart && st.SentSearches == firstPart && st.SentPackages == 1, "send: the parts that went are kept at once");
        svc.Respond = (n, json) => new Answer { Outcome = Outcome.Ok };
        var left = Engine.Select(new[] { d }, st, l);
        Check(left.Searches.Count == 1300 - firstPart, "send: what went is not selected again");
        r = Engine.Send(left, st, l, svc, now, 0, 0, null);
        Check(r.Complete && l.SentCount == 1300 && Engine.Select(new[] { d }, st, l).IsEmpty, "send: the rest goes, and nothing is left");
        var body = Json.TryParseObject(svc.Posted[0]);
        Check(Json.Bool(body, "publish") == true && Json.Str(body, "sender") == st.Sender, "send: each part says publish and the sender");

        // A row the service refuses is left out and kept as refused; the rest of its part goes
        var st2 = new Settings(new MemoryStore());
        var l2 = new Ledger(Path.Combine(dir, "l2.txt"));
        var d2 = Data(Source.Zayit, Search("good", 1), Search("bad", 2), Search("also good", 3));
        var svc2 = new FakeService();
        svc2.Respond = (n, json) => json.Contains("\"bad\"")
            ? HttpService.Read(400, "{\"error\":\"search 2 is not one (query)\"}") : new Answer { Outcome = Outcome.Ok };
        r = Engine.Send(Engine.Select(new[] { d2 }, st2, l2), st2, l2, svc2, now, 0, 0, null);
        Check(r.Complete && r.RefusedRows == 1 && r.Searches == 2 && l2.WasRefused(d2.Searches[1].Fingerprint), "send: a refused row is left out, the rest goes");
        Check(Engine.Select(new[] { d2 }, st2, l2).IsEmpty, "send: a refused row is not sent again as it stands");

        // The minute's limit: wait and go on; the day's limit: stop
        var svc3 = new FakeService();
        svc3.Respond = (n, json) => n == 1 ? new Answer { Outcome = Outcome.Busy, Status = 429 } : new Answer { Outcome = Outcome.Ok };
        var st3 = new Settings(new MemoryStore());
        r = Engine.Send(Engine.Select(new[] { Data(Source.Zayit, Search("x", 1)) }, st3, new Ledger(null)), st3, new Ledger(null), svc3, now, 3, 0, null);
        Check(r.Complete && svc3.Posted.Count == 2, "send: waits out the minute's limit and goes on");
        svc3.Respond = (n, json) => new Answer { Outcome = Outcome.DailyLimit, Status = 429 };
        r = Engine.Send(Engine.Select(new[] { Data(Source.Zayit, Search("y", 1)) }, st3, new Ledger(null)), st3, new Ledger(null), svc3, now, 3, 0, null);
        Check(!r.Complete && r.StoppedBy.Outcome == Outcome.DailyLimit, "send: stops at the day's limit");

        using (var first = SendLock.TryTake(0))
        {
            Check(first != null, "send: takes the lock");
            var t = new Thread(() =>
            {
                using (var second = SendLock.TryTake(0)) Check(second == null, "send: a second sender at the same time does not get it");
            });
            t.Start();
            t.Join();
        }
    }

    static void TestHttp()
    {
        var listener = new HttpListener();
        int port = 18000 + new Random().Next(2000);
        listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        try { listener.Start(); }
        catch (HttpListenerException e) { Check(false, "http: local server", e.Message); return; }
        string method = null, path = null, agent = null, body = null;
        var t = new Thread(() =>
        {
            for (int i = 0; i < 2; i++)
            {
                var ctx = listener.GetContext();
                method = ctx.Request.HttpMethod;
                path = ctx.Request.Url.AbsolutePath;
                agent = ctx.Request.UserAgent;
                using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                var answer = Encoding.UTF8.GetBytes(method == "POST" ? "{\"id\":1}" : "{\"deleted\":3}");
                ctx.Response.StatusCode = method == "POST" ? 201 : 200;
                ctx.Response.OutputStream.Write(answer, 0, answer.Length);
                ctx.Response.Close();
            }
        });
        t.Start();
        var svc = new HttpService("http://127.0.0.1:" + port);
        var a = svc.Post("{\"x\":\"א\"}");
        Check(a.Ok && method == "POST" && path == "/v1/packages" && body == "{\"x\":\"א\"}" && agent == AppInfo.UserAgent, "http: posts a package", method + " " + path + " " + agent);
        var del = svc.DeleteSender("aaaaaaaa-1111-4111-8111-111111111111");
        Check(del.Ok && method == "DELETE" && path == "/v1/senders/aaaaaaaa-1111-4111-8111-111111111111", "http: deletes a sender's packages");
        t.Join(5000);
        listener.Stop();
        Check(new HttpService("http://127.0.0.1:1").Post("{}").Outcome == Outcome.Offline, "http: no server is offline");
    }

    static void TestSqlite(string dir)
    {
        var db = Path.Combine(dir, "t.db");
        var err = Sqlite.Exec(db, "CREATE TABLE t (id INTEGER, name TEXT, score REAL, empty_col TEXT);\nINSERT INTO t VALUES (1, 'חזקה', 2.5, NULL);\nINSERT INTO t VALUES (2, 'b', 3, NULL)");
        Check(err == null, "sqlite: makes a test database", err ?? "");
        string error;
        using (var c = Sqlite.Open(db, out error))
        {
            Check(c != null, "sqlite: opens a copy", error ?? "");
            if (c == null) return;
            var rows = Sqlite.Query(c, "SELECT * FROM t ORDER BY id", out error);
            Check(rows != null && rows.Count == 2 && (long)rows[0]["id"] == 1 && (string)rows[0]["NAME"] == "חזקה" &&
                (double)rows[0]["score"] == 2.5 && rows[0]["empty_col"] == null, "sqlite: reads rows and types");
            Check(Sqlite.Columns(c, "t").Contains("score") && Sqlite.Columns(c, "none").Count == 0, "sqlite: a table's columns");
            Check(Sqlite.Query(c, "SELECT * FROM none", out error) == null && error != null, "sqlite: a missing table is an error, not a crash");
        }
        Check(Sqlite.Open(Path.Combine(dir, "missing.db"), out error) == null, "sqlite: a missing file is not opened");
        File.WriteAllText(Path.Combine(dir, "junk.db"), "not a database at all, just text that is long enough to be read");
        using (var junk = Sqlite.Open(Path.Combine(dir, "junk.db"), out error))
            Check(junk == null || Sqlite.Query(junk, "SELECT 1 FROM sqlite_master", out error) == null, "sqlite: a file that is not a database is not read");
        Check(Directory.GetDirectories(Path.GetTempPath(), "search-data-sender-*").Length == 0, "sqlite: its copies are removed");
    }
}
