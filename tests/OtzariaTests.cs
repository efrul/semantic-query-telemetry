using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// The Otzaria reader's tests, on data in Otzaria's own formats (otzaria/otzaria 0.9.98).
public static partial class CoreTests
{
    static List<KeyValuePair<string, Action<string>>> OtzariaTests()
    {
        return new List<KeyValuePair<string, Action<string>>>
        {
            new KeyValuePair<string, Action<string>>("otzaria-history", TestOtzariaHistory),
            new KeyValuePair<string, Action<string>>("otzaria-feedback", TestOtzariaFeedback),
        };
    }

    // user_state.db as user_state_database.dart makes it
    const string OtzariaSchema =
        "CREATE TABLE lists (box TEXT NOT NULL, key TEXT NOT NULL, payload_json TEXT NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY (box, key));\n" +
        "CREATE TABLE window_sessions (slot TEXT PRIMARY KEY, tabs_json TEXT, current_index INTEGER, active_workspace_id TEXT, bounds_json TEXT, updated_at INTEGER)";

    static string Sql(string s) { return "'" + s.Replace("'", "''") + "'"; }

    static void OtzariaDb(string folder, string history)
    {
        Directory.CreateDirectory(folder);
        var err = Sqlite.Exec(Path.Combine(folder, "user_state.db"), OtzariaSchema + ";\n" +
            "INSERT INTO lists VALUES ('history', 'history', " + Sql(history) + ", 1793900000000);\n" +
            "INSERT INTO lists VALUES ('workspaces', 'key-workspaces', " + Sql("[{\"id\":\"1-1\",\"name\":\"x\",\"tabs\":[{\"type\":\"SearchingTabWindow\",\"searchText\":\"מתוך לשונית\"}]}]") + ", 1793900000001);\n" +
            "INSERT INTO window_sessions VALUES ('main', " + Sql("[{\"type\":\"SearchingTabWindow\",\"searchText\":\"לשונית פתוחה\"}]") + ", 0, NULL, NULL, 1793900000002)");
        if (err != null) throw new InvalidOperationException(err);
    }

    const string History = "[" +
        "{\"ref\":\"ספק ספיקא\",\"book\":{\"title\":\"ספק ספיקא\"},\"index\":0,\"isSearch\":true,\"searchScopeFacets\":[\"/תלמוד בבלי/סדר נזיקין\"]}," +
        "{\"ref\":\"בבא בתרא כט, א\",\"book\":{\"title\":\"בבא בתרא\"},\"index\":12,\"isSearch\":false}," +
        "{\"ref\":\"חזקה (מ) או חזקת\",\"book\":{\"title\":\"חזקה\"},\"index\":0,\"isSearch\":true,\"searchScopeFacets\":null," +
        "\"searchFilterFacets\":[\"/הלכה\"],\"alternativeWords\":{\"0\":[\"חזקת\"]}}," +
        "{\"ref\":\"ספק ספיקא\",\"book\":{\"title\":\"ספק ספיקא\"},\"index\":0,\"isSearch\":true,\"searchScopeFacets\":[\"/תלמוד בבלי/סדר נזיקין\"]}," +
        "{\"ref\":\"\",\"book\":{\"title\":\"   \"},\"index\":0,\"isSearch\":true}" +
        "]";

    static void TestOtzariaHistory(string dir)
    {
        var root = Path.Combine(dir, "otzaria");
        OtzariaDb(Path.Combine(root, "databases"), History);
        long clock = 1794000000000L;
        var reader = new OtzariaReader(new[] { root }, new string[0], () => clock);
        var data = reader.Read();
        Check(data.Found && data.Problems.Count == 0, "otzaria: found", string.Join("; ", data.Problems.ToArray()));
        Check(data.Searches.Count == 2, "otzaria: the history's searches once each, no book visits, tabs or workspaces", data.Searches.Count + " rows");
        var first = data.Searches.Find(s => s.Query == "ספק ספיקא");
        Check(first != null && first.Scope == "תלמוד בבלי › סדר נזיקין" && first.Ref == null && first.TimeUnknown && first.LastAt == clock && first.Count == 1,
            "otzaria: a search's words, scope, and its time unknown");
        var withOptions = data.Searches.Find(s => s.Query == "חזקה");
        Check(withOptions != null && withOptions.Ref == "חזקה (מ) או חזקת" && withOptions.Scope == "סינון: הלכה", "otzaria: a search's options and filter");
        var json = (Dictionary<string, object>)Json.Parse(Json.Write(first.ToJson(), false));
        Check(Json.Bool(json, "timeUnknown") == true && Json.Str(json, "source") == "otzaria", "otzaria: a search says its time is unknown");

        // Sent once: found again later, with a later time, it is not new
        var st = new Settings(new MemoryStore());
        var l = new Ledger(Path.Combine(dir, "l.txt"));
        var svc = new FakeService();
        var r = Engine.Send(Engine.Select(new[] { data }, st, l), st, l, svc, () => clock, 0, 0, null);
        clock += 86400000L;
        Check(r.Complete && r.Searches == 2 && Engine.Select(new[] { reader.Read() }, st, l).IsEmpty, "otzaria: a search without a time is sent once");

        // user_state.db beside the library (library_path.txt), and the old Hive history
        var portable = Path.Combine(dir, "portable");
        Directory.CreateDirectory(portable);
        File.WriteAllText(Path.Combine(portable, "library_path.txt"), "﻿" + Path.Combine(dir, "lib", "books") + "\r\n", new UTF8Encoding(false));
        OtzariaDb(Path.Combine(dir, "lib", "databases"), History);
        Check(new OtzariaReader(new[] { portable }, new string[0], () => clock).Read().Searches.Count == 2, "otzaria: user_state.db beside the library");
        var old = Path.Combine(dir, "old");
        Directory.CreateDirectory(old);
        File.WriteAllBytes(Path.Combine(old, "history.hive"), new byte[] { 1, 2, 3 });
        var oldData = new OtzariaReader(new[] { old }, new string[0], () => clock).Read();
        Check(oldData.Found && oldData.Searches.Count == 0 && oldData.Problems.Count == 1, "otzaria: old Hive history is not read, and says so");
        Check(!new OtzariaReader(new[] { Path.Combine(dir, "none") }, new string[0], () => clock).Read().Found, "otzaria: not found when nothing is there");
    }

    static string Line(string json) { return json + "\n"; }

    static void TestOtzariaFeedback(string dir)
    {
        var root = Path.Combine(dir, "otzaria");
        var fb = Path.Combine(root, "search_feedback");
        Directory.CreateDirectory(Path.Combine(fb, "unverified"));
        const string context = "{\"app\":\"otzaria\",\"appVersion\":\"0.9.98\",\"platform\":\"windows\"}";
        File.WriteAllText(Path.Combine(fb, "seg-00000000000000001-00000000000000000001-a-b.jsonl"),
            Line(context) +
            Line("{\"eventId\":\"e1\",\"type\":\"search\",\"clientTime\":\"2026-11-09T10:00:00.000Z\",\"searchSessionId\":\"s1\",\"msSinceSearch\":0," +
                 "\"query\":\"אין מבטלין\",\"params\":{\"scope\":{\"facets\":[\"/משנה\"],\"allLibrary\":false}}}") +
            Line("{\"eventId\":\"e2\",\"type\":\"vote\",\"clientTime\":\"2026-11-09T10:00:05.000Z\",\"searchSessionId\":\"s1\",\"vote\":\"like\"," +
                 "\"result\":{\"rank\":2,\"title\":\"משנה ברכות\",\"reference\":\"ברכות א, א\",\"segment\":17}}") +
            Line("{\"eventId\":\"e3\",\"type\":\"vote\",\"clientTime\":\"2026-11-09T10:00:06.000Z\",\"searchSessionId\":\"s1\",\"vote\":\"dislike\"," +
                 "\"result\":{\"rank\":3,\"title\":\"תוספתא\",\"reference\":\"ב\",\"segment\":4}}") +
            Line("{\"eventId\":\"e4\",\"type\":\"vote\",\"clientTime\":\"2026-11-09T10:00:07.000Z\",\"searchSessionId\":\"s1\",\"vote\":\"cleared\"," +
                 "\"result\":{\"rank\":3,\"title\":\"תוספתא\",\"reference\":\"ב\",\"segment\":4}}") +
            Line("{\"eventId\":\"e5\",\"type\":\"vote\",\"clientTime\":\"2026-11-09T10:00:08.000Z\",\"searchSessionId\":\"gone\",\"vote\":\"like\"," +
                 "\"result\":{\"rank\":1,\"title\":\"x\",\"reference\":\"y\",\"segment\":1}}") +
            Line("{\"eventId\":\"e2\",\"type\":\"vote\",\"clientTime\":\"2026-11-09T10:00:05.000Z\",\"searchSessionId\":\"s1\",\"vote\":\"like\"," +
                 "\"result\":{\"rank\":2,\"title\":\"משנה ברכות\",\"reference\":\"ברכות א, א\",\"segment\":17}}") +
            "{\"eventId\":\"e6\",\"type\":\"vo");
        File.WriteAllText(Path.Combine(fb, "seg-2.jsonl"), Line("{\"app\":\"otzaria\",\"localOnly\":true}") +
            Line("{\"eventId\":\"d1\",\"type\":\"search\",\"clientTime\":\"2026-11-09T11:00:00.000Z\",\"searchSessionId\":\"s9\",\"query\":\"debug\"}"));
        File.WriteAllText(Path.Combine(fb, "unverified", "seg-3.jsonl"), Line(context) +
            Line("{\"eventId\":\"u1\",\"type\":\"search\",\"clientTime\":\"2026-11-09T12:00:00.000Z\",\"searchSessionId\":\"s8\",\"query\":\"unverified\"}"));
        File.WriteAllText(Path.Combine(fb, "installation_key.json"), "{\"privateKey\":\"secret\"}");
        var data = new OtzariaReader(new[] { root }, new string[0], () => 1L).Read();
        Check(data.Found, "otzaria feedback: found");
        Check(data.Grades.Count == 1, "otzaria feedback: one grade: a cleared vote is taken back, a vote without its search is not sent", data.Grades.Count + " grades");
        var g = data.Grades.Count == 1 ? data.Grades[0] : null;
        Check(g != null && g.Query == "אין מבטלין" && g.Grade == GradeRow.Relevant && g.Rank == 1 && g.LineId == 0 && g.BookId == 0 &&
            g.BookTitle == "משנה ברכות" && g.HeRef == "ברכות א, א" && g.DocId == "17" && g.Scope == "משנה" && g.Valid(),
            "otzaria feedback: a vote's words come from its search, its rank from 0, Otzaria's ids apart");
        var semantic = data.Searches.Find(s => s.Query == "אין מבטלין");
        Check(semantic != null && semantic.Ref == "חיפוש חכם" && !semantic.TimeUnknown && semantic.LastAt == 1794218400000L,
            "otzaria feedback: a smart search with its exact time");
        Check(!data.Searches.Exists(s => s.Query == "debug" || s.Query == "unverified"), "otzaria feedback: debug segments and unverified ones are skipped");
    }
}
