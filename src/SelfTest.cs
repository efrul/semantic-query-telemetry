using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Microsoft.Win32;

static class SelfTest
{
    static readonly List<string> log = new List<string>();
    static int failed = 0;

    static void Check(bool cond, string name, string detail = "")
    {
        if (cond) log.Add("PASS  " + name + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
        else { failed++; log.Add("FAIL  " + name + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")")); }
    }

    public static int Run(string workDir)
    {
        Directory.CreateDirectory(workDir);
        string regPath = @"Software\JavaSoft\Prefs\zayit_telemetry_selftest_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        Prefs.TestRoot = regPath;

        try
        {
            TestPrefs();
            TestSourcesAndSqlite(workDir);
            TestSplittingAndFileWrite(workDir);
            TestHttpService(workDir);
        }
        catch (Exception ex)
        {
            failed++;
            log.Add("FAIL  unhandled exception: " + ex);
        }
        finally
        {
            Prefs.TestRoot = null;
            Sources.TestZayitRoots = null;
            Sources.TestOtzariaRoots = null;
            Sources.ForceManagedSqlite = false;
            try { Registry.CurrentUser.DeleteSubKeyTree(regPath, false); } catch { }
        }

        log.Add(failed == 0 ? "ALL PASS" : (failed + " FAILED"));
        File.WriteAllLines(Path.Combine(workDir, "selftest-report.txt"), log.ToArray(), Encoding.UTF8);
        return failed == 0 ? 0 : 1;
    }

    static void TestPrefs()
    {
        string sample = @"D:\ספרייה\Zayit library/test_ABC";
        string enc = Prefs.ToJava(sample);
        string dec = Prefs.FromJava(enc);
        Check(dec == sample, "Prefs.ToJava/FromJava round-trip", dec);

        string s1 = Prefs.GetSender();
        Guid g;
        Check(s1.Length == 36 && Guid.TryParseExact(s1, "D", out g), "Prefs.GetSender generates UUID v4", s1);
        string s2 = Prefs.RenewSender();
        Check(s2 != s1 && Prefs.GetSender() == s2, "Prefs.RenewSender updates sender");
    }

    static void TestSourcesAndSqlite(string workDir)
    {
        string zayitDir = Path.Combine(workDir, "zayit");
        string zayitDbDir = Path.Combine(zayitDir, "databases");
        string zayitSettingsDb = Path.Combine(zayitDbDir, "settings", "user_settings.db");
        Directory.CreateDirectory(Path.GetDirectoryName(zayitSettingsDb));
        File.WriteAllText(Path.Combine(zayitDbDir, "release_info.txt"), "version=v24\n", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(zayitDbDir, "seforim.db"), new byte[16]);

        bool dbCreated = Sqlite.ExecNative(zayitSettingsDb,
            "CREATE TABLE visit_history (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, bookId INTEGER NOT NULL, lineIndex INTEGER NOT NULL, bookTitle TEXT NOT NULL, lineRef TEXT NOT NULL, tocText TEXT, snippet TEXT NOT NULL, visitedAt INTEGER NOT NULL, visitCount INTEGER NOT NULL DEFAULT 1, searchQuery TEXT, searchCategoryId INTEGER, searchBookId INTEGER, searchTocId INTEGER, searchExtended INTEGER NOT NULL DEFAULT 0)",
            "INSERT INTO visit_history (kind, bookId, lineIndex, bookTitle, lineRef, snippet, visitedAt, visitCount, searchQuery, searchCategoryId, searchExtended) VALUES ('search', 0, 0, '', '', '', 1793800001000, 3, 'חזקת הבתים', 2, 0)",
            "INSERT INTO visit_history (kind, bookId, lineIndex, bookTitle, lineRef, snippet, visitedAt, visitCount, searchQuery, searchBookId, searchExtended) VALUES ('search', 0, 0, '', '', '', 1793800002000, 1, 'שנים אוחזין בטלית', 45, 1)",
            "CREATE TABLE search_feedback (searchKey TEXT NOT NULL, query TEXT NOT NULL, lineId INTEGER NOT NULL, bookId INTEGER NOT NULL, grade INTEGER NOT NULL, rank INTEGER NOT NULL, searchCategoryId INTEGER, searchBookId INTEGER, searchTocId INTEGER, searchExtended INTEGER NOT NULL DEFAULT 0, model TEXT, gradedAt INTEGER NOT NULL, PRIMARY KEY (searchKey, lineId))",
            "INSERT INTO search_feedback (searchKey, query, lineId, bookId, grade, rank, searchExtended, model, gradedAt) VALUES ('search:g:חזקת הבתים', 'חזקת הבתים', 26579, 72, 3, 0, 0, 'student/v1', 1793800003000)");
        Check(dbCreated, "Create Zayit user_settings.db");

        // Daf feedback JSON
        string dafDir = Path.Combine(zayitDbDir, "daf-layouts", "feedback");
        Directory.CreateDirectory(dafDir);
        File.WriteAllText(Path.Combine(dafDir, "Berakhot.json"),
            "{\"format\":1,\"tractate\":\"Berakhot\",\"tractateHe\":\"ברכות\",\"library\":\"v24\",\"marks\":[{\"amud\":\"2a\",\"c\":\"g\",\"i\":1,\"t\":\"מאימתי קורין\",\"ok\":true,\"cut\":0,\"at\":\"2026-11-05T10:00:00Z\"}]}",
            Encoding.UTF8);

        // Otzaria user_state.db + search_feedback/active.jsonl
        string otzDir = Path.Combine(workDir, "otzaria");
        string otzDbDir = Path.Combine(otzDir, "databases");
        string otzStateDb = Path.Combine(otzDbDir, "user_state.db");
        Directory.CreateDirectory(otzDbDir);
        string historyPayload = "[{\"ref\":\"חיפוש: אבידתו ואבידת אביו\",\"bookTitle\":\"חיפוש\",\"index\":0,\"isSearch\":true,\"createdAt\":\"2026-11-05T11:00:00Z\",\"searchScopeFacets\":[\"/תלמוד בבלי/סדר נזיקין\"],\"book\":{\"title\":\"אבידתו ואבידת אביו\"}}]";
        bool otzCreated = Sqlite.ExecNative(otzStateDb,
            "CREATE TABLE lists (box TEXT NOT NULL, key TEXT NOT NULL, payload_json TEXT NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY (box, key))",
            "INSERT INTO lists (box, key, payload_json, updated_at) VALUES ('history', 'history', '" + historyPayload.Replace("'", "''") + "', 1793800004000)",
            "CREATE TABLE window_sessions (slot INTEGER PRIMARY KEY, tabs_json TEXT NOT NULL, current_tab INTEGER NOT NULL, workspace_id INTEGER NOT NULL, workspace_name TEXT NOT NULL, updated_at INTEGER NOT NULL)",
            "INSERT INTO window_sessions (slot, tabs_json, current_tab, workspace_id, workspace_name, updated_at) VALUES (0, '[{\"type\":\"SearchingTabWindow\",\"searchText\":\"מודה במקצת\"}]', 0, 0, 'default', 1793800005000)");
        Check(otzCreated, "Create Otzaria user_state.db");

        string otzFbDir = Path.Combine(otzDir, "search_feedback");
        Directory.CreateDirectory(otzFbDir);
        File.WriteAllLines(Path.Combine(otzFbDir, "active.jsonl"), new[]
        {
            "{\"app\":\"otzaria\",\"appVersion\":\"1.9.0\",\"libraryVersion\":\"otz-2026\"}",
            "{\"type\":\"search\",\"searchSessionId\":\"s1\",\"query\":\"בור ברשות הרבים\",\"clientTime\":\"2026-11-05T12:00:00Z\",\"params\":{\"scope\":{\"facets\":[\"/תלמוד בבלי\"]}}}",
            "{\"type\":\"vote\",\"searchSessionId\":\"s1\",\"vote\":\"like\",\"clientTime\":\"2026-11-05T12:01:00Z\",\"result\":{\"rank\":0,\"docId\":991,\"title\":\"בבא קמא\",\"reference\":\"בבא קמא דף כז עמוד ב\"}}"
        }, Encoding.UTF8);

        Sources.TestZayitRoots = new List<string> { zayitDir };
        Sources.TestOtzariaRoots = new List<string> { otzDir };

        // Test 1: Native winsqlite3 reader
        Sources.ForceManagedSqlite = false;
        var scan1 = Sources.Scan(true);
        Check(scan1.Zayit.Found && scan1.Zayit.Searches.Count == 2 && scan1.Zayit.Grades.Count == 1 && scan1.Zayit.DafMarkCount == 1,
            "Zayit scan via winsqlite3.dll",
            string.Format("s={0}, g={1}, daf={2}", scan1.Zayit.Searches.Count, scan1.Zayit.Grades.Count, scan1.Zayit.DafMarkCount));
        Check(scan1.Otzaria.Found && scan1.Otzaria.Searches.Count == 3 && scan1.Otzaria.Grades.Count == 1,
            "Otzaria scan via winsqlite3.dll + JSONL",
            string.Format("s={0}, g={1}", scan1.Otzaria.Searches.Count, scan1.Otzaria.Grades.Count));

        // Test 2: Pure-C# Managed B-tree SQLite reader fallback
        Sources.ForceManagedSqlite = true;
        var scan2 = Sources.Scan(true);
        Check(scan2.Zayit.Searches.Count == 2 && scan2.Zayit.Grades.Count == 1 && scan2.Otzaria.Searches.Count == 3,
            "Pure-C# Managed B-tree SQLite reader matches winsqlite3",
            string.Format("zayit={0}, otzaria={1}", scan2.Zayit.Searches.Count, scan2.Otzaria.Searches.Count));
        Sources.ForceManagedSqlite = false;

        // Test 3: Query exclusion filter
        var excluded = new HashSet<string>(StringComparer.Ordinal) { scan1.Zayit.Searches[0].Key };
        var filteredPkg = Sources.BuildPackage(scan1, true, true, true, true, true, excluded);
        Check(filteredPkg.Searches.Count == 4 && filteredPkg.Grades.Count == 2 && filteredPkg.DafMarkCount == 1,
            "BuildPackage respects per-query exclusion",
            "searches=" + filteredPkg.Searches.Count);

        // Test 4: Incremental since timestamps
        Sources.RecordSent(scan1, true, true, true, true, true);
        var scanIncremental = Sources.Scan(false);
        Check(scanIncremental.Zayit.Searches.Count == 0 && scanIncremental.Zayit.Grades.Count == 0 &&
              scanIncremental.Zayit.DafMarkCount == 0 && scanIncremental.Otzaria.Searches.Count == 0 &&
              scanIncremental.Otzaria.Grades.Count == 0,
            "Incremental scan after RecordSent returns 0 already-sent items");

        var scanAllAgain = Sources.Scan(true);
        Check(scanAllAgain.Zayit.Searches.Count == 2 && scanAllAgain.Otzaria.Searches.Count == 3,
            "Scan(allFromStart: true) returns all items regardless of sentAt");
    }

    static void TestSplittingAndFileWrite(string workDir)
    {
        var big = new FeedbackPackage
        {
            Sender = Prefs.GetSender(),
            SentAt = "2026-11-05T12:00:00Z",
            Library = "v24"
        };
        string longQuery = new string('א', 1500);
        for (int i = 0; i < 400; i++)
        {
            big.Searches.Add(new SearchRow
            {
                Query = longQuery + " " + i,
                Source = i % 2 == 0 ? "zayit" : "otzaria",
                Count = 1,
                LastAt = 1793800000000L + i
            });
        }
        var parts = big.Parts();
        bool allUnderLimit = true;
        foreach (var p in parts)
        {
            if (Encoding.UTF8.GetByteCount(p.ToCompactJson()) > FeedbackPackage.PART_BYTES)
                allUnderLimit = false;
        }
        Check(parts.Count >= 2 && allUnderLimit, "Package.Parts() splits >900 KB package into valid parts", "parts=" + parts.Count);

        string outDir = Path.Combine(workDir, "saved");
        string savedPath = parts[0].WriteTo(outDir, 0, parts.Count);
        Check(File.Exists(savedPath) && new FileInfo(savedPath).Length > 100, "Package.WriteTo() writes JSON file atomically", Path.GetFileName(savedPath));
    }

    static void TestHttpService(string workDir)
    {
        int port = 49321;
        string url = "http://127.0.0.1:" + port;
        var listener = new HttpListener();
        listener.Prefixes.Add(url + "/");
        listener.Start();

        int postsReceived = 0;
        string deletedSender = null;
        string lastPostJson = null;

        var serverThread = new Thread(() =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = listener.GetContext();
                    var req = ctx.Request;
                    var resp = ctx.Response;
                    if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/v1/packages")
                    {
                        using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                            lastPostJson = sr.ReadToEnd();
                        postsReceived++;
                        var b = Encoding.UTF8.GetBytes("{\"id\":" + postsReceived + "}");
                        resp.StatusCode = 201;
                        resp.ContentType = "application/json";
                        resp.OutputStream.Write(b, 0, b.Length);
                        resp.Close();
                    }
                    else if (req.HttpMethod == "DELETE" && req.Url.AbsolutePath.StartsWith("/v1/senders/"))
                    {
                        deletedSender = req.Url.AbsolutePath.Substring("/v1/senders/".Length);
                        var b = Encoding.UTF8.GetBytes("{\"deleted\":1}");
                        resp.StatusCode = 200;
                        resp.ContentType = "application/json";
                        resp.OutputStream.Write(b, 0, b.Length);
                        resp.Close();
                    }
                    else
                    {
                        resp.StatusCode = 404;
                        resp.Close();
                    }
                }
            }
            catch { }
        });
        serverThread.IsBackground = true;
        serverThread.Start();

        try
        {
            foreach (var src in new[] { "zayit", "otzaria" })
                foreach (var k in new[] { "searches", "grades", "daf" })
                    Prefs.SetSentAt(src, k, 0L);

            var scan = Sources.Scan(false);
            var pkg = Sources.BuildPackage(scan, true, true, true, true, true);
            string sendErr = Service.Send(pkg.Parts(), url);
            var parsedSent = Json.ParseObj(lastPostJson);
            Check(sendErr == null && postsReceived == 1 && Json.Str(parsedSent, "app", "") == "Pardes" && Json.Long(parsedSent, "format", 0) == 1,
                "Service.Send POSTs valid format 1 package to /v1/packages");

            string sender = Prefs.GetSender();
            string delErr = Service.DeleteSent(sender, url);
            Check(delErr == null && deletedSender == sender, "Service.DeleteSent sends DELETE /v1/senders/<id>", deletedSender);
        }
        finally
        {
            try { listener.Stop(); listener.Close(); } catch { }
        }
    }
}
