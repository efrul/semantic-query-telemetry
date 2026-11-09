using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// Otzaria's searches and grades, as Otzaria itself keeps them (otzaria/otzaria, 0.9.98 and later):
//
// History: user_state.db (SQLite, WAL), table lists, the row box='history' key='history': a JSON list of up to 200
// bookmarks, newest first. An entry with isSearch:true is a search: its words in book.title, Otzaria's title for it with
// its options in ref, its scope in searchScopeFacets (null: the whole library) and searchFilterFacets. Otzaria keeps no
// time and no count for a search (updated_at is the whole list's), so each is sent once, with TimeUnknown and the time
// this tool first found it, and a search made again, which only moves it to the top, is not seen. Open tabs, workspaces
// and backups repeat queries but are the window's state, not searches: they are not read.
// user_state.db is in <data>\databases, or beside the library's folder (<parent of library_path.txt>\databases).
//
// Grades: only Otzaria's smart (semantic) search has them, on the desktop, after the user agreed in Otzaria. Events go to
// <data>\search_feedback\*.jsonl (a ClientContext line, then events with eventId, type, clientTime, searchSessionId);
// Otzaria sends them to its own server within minutes and deletes them, so few are here at any time. A vote (like,
// dislike, or cleared to take it back) is read only with the search event of its session, never with made-up words.
// Segments of debug builds ("localOnly") and the unverified folder are skipped; installation_key.json and the queue's
// other files are never opened.
//
// <data> is %APPDATA%\otzaria (Otzaria 0.9.92 and later; a portable copy keeps it in otzaria_data beside its exe, which
// the user adds as a folder). Older Otzaria kept binary Hive files (history.hive, or %APPDATA%\com.example\otzaria),
// which this tool does not read: it says so.
public sealed class OtzariaReader : IReader
{
    readonly List<string> dataRoots = new List<string>();
    readonly List<string> extraDatabaseDirs = new List<string>();
    readonly Func<long> now;

    public OtzariaReader(IEnumerable<string> dataRoots, IEnumerable<string> databaseDirs, Func<long> now)
    {
        foreach (var r in dataRoots) if (!string.IsNullOrEmpty(r)) this.dataRoots.Add(r);
        foreach (var d in databaseDirs) if (!string.IsNullOrEmpty(d)) extraDatabaseDirs.Add(d);
        this.now = now;
    }

    public static OtzariaReader ForThisComputer(IEnumerable<string> extraFolders, Func<long> now)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new List<string> { Path.Combine(appData, "otzaria") };
        var dbDirs = new List<string>();
        foreach (var f in extraFolders)
        {
            roots.Add(f);
            roots.Add(Path.Combine(f, "otzaria_data"));
            dbDirs.Add(f);
        }
        return new OtzariaReader(roots, dbDirs, now);
    }

    public SourceData Read()
    {
        var data = new SourceData { Source = Source.Otzaria };
        var byKey = new Dictionary<string, SearchRow>(StringComparer.Ordinal);
        var sessions = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        var votes = new List<Dictionary<string, object>>();
        var seenDbs = new List<string>();
        bool historyRead = false, oldHive = false;
        long found = now();

        foreach (var root in dataRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in DatabaseDirs(root))
            {
                var db = Path.Combine(dir, "user_state.db");
                if (!File.Exists(db) || seenDbs.Exists(x => string.Equals(x, db, StringComparison.OrdinalIgnoreCase))) continue;
                seenDbs.Add(db);
                data.Found = true;
                data.AddPlace(dir);
                historyRead |= ReadHistory(db, found, byKey, data);
            }
            var feedback = Path.Combine(root, "search_feedback");
            if (Directory.Exists(feedback))
            {
                data.Found = true;
                data.AddPlace(root);
                ReadFeedback(feedback, sessions, votes);
            }
            if (File.Exists(Path.Combine(root, "history.hive"))) { data.Found = true; data.AddPlace(root); oldHive = true; }
            if (data.Library == null) data.Library = LibraryVersion(root);
        }
        foreach (var dir in extraDatabaseDirs)
        {
            var db = Path.Combine(dir, "user_state.db");
            if (!File.Exists(db) || seenDbs.Exists(x => string.Equals(x, db, StringComparison.OrdinalIgnoreCase))) continue;
            seenDbs.Add(db);
            data.Found = true;
            data.AddPlace(dir);
            historyRead |= ReadHistory(db, found, byKey, data);
        }
        if (oldHive && !historyRead)
            data.Problems.Add("ההיסטוריה של אוצריא שמורה כאן בפורמט ישן (history.hive) שהכלי לא קורא. אחרי עדכון אוצריא לגרסה 0.9.98 ומעלה היא תיקרא.");
        var oldData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.example", "otzaria");
        if (Directory.Exists(oldData) && !data.Found)
            data.Problems.Add("נמצאה אוצריא בגרסה ישנה (לפני מאי 2026), שנתוניה בפורמט שהכלי לא קורא. אחרי עדכון אוצריא הם ייקראו.");

        // The smart search's searches, from their events: exact times, counted by event
        foreach (var s in sessions.Values) AddSemanticSearch(s, byKey);
        data.Searches.AddRange(byKey.Values);
        data.Grades.AddRange(Grades(votes, sessions));
        return data;
    }

    // Where Otzaria's user_state.db may be for data folder [root]: <root>\databases, and beside its library.
    static List<string> DatabaseDirs(string root)
    {
        var dirs = new List<string> { Path.Combine(root, "databases"), root };
        var library = ReadPointer(Path.Combine(root, "library_path.txt"));
        if (library != null)
        {
            var parent = Path.GetDirectoryName(library.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(parent)) dirs.Add(Path.Combine(parent, "databases"));
        }
        return dirs;
    }

    // A path in one of Otzaria's pointer files (UTF-8 with a byte order mark), or null.
    static string ReadPointer(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var p = File.ReadAllText(file, Encoding.UTF8).Trim('﻿', ' ', '\t', '\r', '\n', '"');
            return p.Length > 0 && p.IndexOfAny(Path.GetInvalidPathChars()) < 0 ? p : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ---- History ----

    static bool ReadHistory(string path, long found, Dictionary<string, SearchRow> byKey, SourceData data)
    {
        string error;
        using (var db = Sqlite.Open(path, out error))
        {
            if (db == null) { data.Problems.Add("לא הצלחתי לקרוא את " + path + ": " + error); return false; }
            if (!Sqlite.Columns(db, "lists").Contains("payload_json")) return false;
            var rows = Sqlite.Query(db, "SELECT payload_json FROM lists WHERE box = 'history' AND key = 'history'", out error);
            if (rows == null) { data.Problems.Add("לא הצלחתי לקרוא את היסטוריית החיפושים של אוצריא: " + error); return false; }
            foreach (var r in rows)
            {
                var list = Json.TryParse(r["payload_json"] as string) as List<object>;
                if (list == null) continue;
                foreach (var item in list)
                {
                    var row = HistorySearch(item as Dictionary<string, object>, found);
                    if (row != null && !byKey.ContainsKey(row.Key)) byKey[row.Key] = row;
                }
            }
            return true;
        }
    }

    internal static SearchRow HistorySearch(Dictionary<string, object> entry, long found)
    {
        if (entry == null || Json.Bool(entry, "isSearch") != true) return null;
        var words = Clean(Json.Str(Json.Obj(entry, "book"), "title"));
        if (words == null) return null;
        var title = Clean(Json.Str(entry, "ref"));
        var scope = Facets(Json.Arr(entry, "searchScopeFacets"));
        var filter = Facets(Json.Arr(entry, "searchFilterFacets"));
        if (filter != null && filter != scope) scope = scope == null ? "סינון: " + filter : scope + "; סינון: " + filter;
        return new SearchRow
        {
            Source = Source.Otzaria,
            Query = words,
            Ref = title != null && title != words ? title : null,
            Scope = scope,
            Count = 1,
            LastAt = found,
            TimeUnknown = true
        };
    }

    // Facets as Otzaria writes them ("/תלמוד בבלי/סדר נזיקין"), as readable text; null for the whole library.
    static string Facets(List<object> facets)
    {
        if (facets == null) return null;
        var parts = new List<string>();
        foreach (var f in facets)
        {
            var s = f as string;
            if (s == null) continue;
            s = s.Trim().Trim('/').Replace("/", " › ");
            if (s.Length > 0 && !parts.Contains(s)) parts.Add(s);
        }
        if (parts.Count == 0) return null;
        var text = string.Join(", ", parts.ToArray());
        return text.Length > Limits.MaxScope ? text.Substring(0, Limits.MaxScope) : text;
    }

    // ---- The smart search's events ----

    static void ReadFeedback(string folder, Dictionary<string, Dictionary<string, object>> sessions, List<Dictionary<string, object>> votes)
    {
        string[] files;
        try { files = Directory.GetFiles(folder, "*.jsonl", SearchOption.TopDirectoryOnly); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        var seenEvents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            List<string> lines;
            try
            {
                // Otzaria may be writing, sending or deleting it right now
                lines = new List<string>();
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null) lines.Add(line);
                }
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (lines.Count == 0) continue;
            var context = Json.TryParseObject(lines[0]);
            if (context == null || Json.Str(context, "app") != "otzaria" || Json.Bool(context, "localOnly") == true) continue;
            for (int i = 1; i < lines.Count; i++)
            {
                // A last line still being written does not parse, and is skipped
                var e = Json.TryParseObject(lines[i]);
                if (e == null) continue;
                var id = Json.Str(e, "eventId");
                if (string.IsNullOrEmpty(id) || !seenEvents.Add(id)) continue;
                var type = Json.Str(e, "type");
                var session = Json.Str(e, "searchSessionId");
                if (string.IsNullOrEmpty(session)) continue;
                if (type == "search") sessions[session] = e;
                else if (type == "vote") votes.Add(e);
            }
        }
    }

    static void AddSemanticSearch(Dictionary<string, object> e, Dictionary<string, SearchRow> byKey)
    {
        var words = Clean(Json.Str(e, "query"));
        long at = Time(Json.Str(e, "clientTime"));
        if (words == null || at <= 0) return;
        var row = new SearchRow
        {
            Source = Source.Otzaria,
            Query = words,
            Scope = Facets(Json.Arr(Json.Obj(Json.Obj(e, "params"), "scope"), "facets")),
            Ref = "חיפוש חכם",
            Count = 1,
            LastAt = at
        };
        SearchRow prev;
        if (byKey.TryGetValue(row.Key, out prev))
        {
            prev.Count++;
            if (at > prev.LastAt) prev.LastAt = at;
        }
        else byKey[row.Key] = row;
    }

    static List<GradeRow> Grades(List<Dictionary<string, object>> votes, Dictionary<string, Dictionary<string, object>> sessions)
    {
        // The last vote on a result stands; a vote cleared takes it back
        var last = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        votes.Sort((a, b) => Time(Json.Str(a, "clientTime")).CompareTo(Time(Json.Str(b, "clientTime"))));
        foreach (var v in votes)
        {
            var result = Json.Obj(v, "result");
            var key = Json.Str(v, "searchSessionId") + "\u001f" + Json.Str(result, "title") + "\u001f" + Json.Str(result, "reference") + "\u001f" +
                (Json.Long(result, "segment") ?? -1);
            last[key] = v;
        }
        var grades = new List<GradeRow>();
        foreach (var v in last.Values)
        {
            int grade;
            var vote = Json.Str(v, "vote");
            if (vote == "like") grade = GradeRow.Relevant;
            else if (vote == "dislike") grade = GradeRow.NotRelevant;
            else continue;
            Dictionary<string, object> search;
            var session = Json.Str(v, "searchSessionId");
            // Without its search the vote's words are unknown: it is not sent
            if (!sessions.TryGetValue(session, out search)) continue;
            var words = Clean(Json.Str(search, "query"));
            long at = Time(Json.Str(v, "clientTime"));
            var result = Json.Obj(v, "result");
            long rank = Json.Long(result, "rank") ?? 0;
            if (words == null || at <= 0) continue;
            var segment = Json.Long(result, "segment");
            grades.Add(new GradeRow
            {
                Source = Source.Otzaria,
                Query = words,
                Search = "otzaria:smart:" + session,
                LineId = 0,
                BookId = 0,
                Grade = grade,
                Rank = Math.Max(0, rank - 1),
                Model = "otzaria-smart-search",
                Scope = Facets(Json.Arr(Json.Obj(Json.Obj(search, "params"), "scope"), "facets")),
                BookTitle = Short(Json.Str(result, "title")),
                HeRef = Short(Json.Str(result, "reference")),
                DocId = segment.HasValue ? segment.Value.ToString(CultureInfo.InvariantCulture) : null,
                GradedAt = at
            });
        }
        return grades;
    }

    // ---- Small things ----

    // The library's version: seforim.db's schema_meta db_version, the database's folder in library_database_path.txt.
    static string LibraryVersion(string root)
    {
        var dir = ReadPointer(Path.Combine(root, "library_database_path.txt"));
        if (dir == null) return null;
        var db = Path.Combine(dir, "seforim.db");
        if (!File.Exists(db)) return null;
        string error;
        using (var c = Sqlite.OpenImmutable(db, out error))
        {
            if (c == null) return null;
            var rows = Sqlite.Query(c, "SELECT value FROM schema_meta WHERE key = 'db_version'", out error);
            if (rows == null || rows.Count == 0) return null;
            var v = rows[0]["value"];
            var s = v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
            return s != null && s.Length > 0 && s.Length <= Limits.MaxVersion ? s : null;
        }
    }

    static long Time(string iso)
    {
        DateTimeOffset t;
        if (iso != null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t))
            return t.ToUnixTimeMilliseconds();
        return 0;
    }

    static string Clean(string s)
    {
        if (s == null) return null;
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    static string Short(string s)
    {
        s = Clean(s);
        return s == null ? null : s.Length > Limits.MaxTitle ? s.Substring(0, Limits.MaxTitle) : s;
    }
}
