using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

class SourceSnapshot
{
    public string Id;    // "zayit" or "otzaria"
    public string Title; // "זית" or "אוצריא"
    public bool Found;
    public string PathFound;
    public string LibraryVersion;
    public List<SearchRow> Searches = new List<SearchRow>();
    public List<GradeRow> Grades = new List<GradeRow>();
    public List<DafReview> Daf = new List<DafReview>();

    public int DafMarkCount
    {
        get
        {
            int n = 0;
            foreach (var r in Daf) n += r.Marks.Count;
            return n;
        }
    }

    public long MaxSearchAt
    {
        get
        {
            long m = 0;
            foreach (var s in Searches) if (s.LastAt > m) m = s.LastAt;
            return m;
        }
    }

    public long MaxGradeAt
    {
        get
        {
            long m = 0;
            foreach (var g in Grades) if (g.GradedAt > m) m = g.GradedAt;
            return m;
        }
    }

    public long MaxDafAt
    {
        get
        {
            long m = 0;
            foreach (var r in Daf)
                foreach (var mk in r.Marks)
                    if (mk.AtMs > m) m = mk.AtMs;
            return m;
        }
    }
}

class ScanResult
{
    public SourceSnapshot Zayit = new SourceSnapshot { Id = "zayit", Title = "זית" };
    public SourceSnapshot Otzaria = new SourceSnapshot { Id = "otzaria", Title = "אוצריא" };
    public bool AllFromStart;

    public long LastSentAt
    {
        get
        {
            long m = 0;
            foreach (var src in new[] { "zayit", "otzaria" })
                foreach (var k in new[] { "searches", "grades", "daf" })
                {
                    long v = Prefs.GetSentAt(src, k);
                    if (v > m) m = v;
                }
            return m;
        }
    }
}

static class Sources
{
    static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

    // Optional overrides for SelfTest or user-selected custom folders.
    public static List<string> TestZayitRoots = null;
    public static List<string> TestOtzariaRoots = null;
    public static readonly List<string> ExtraFolders = new List<string>();
    public static bool ForceManagedSqlite = false;

    public static string CollapseSpaces(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return Whitespace.Replace(s, " ").Trim();
    }

    public static string NormalizeToDir(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            path = path.Trim().Trim('"', '\'');
            if (File.Exists(path) || path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            {
                var d = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            }
            if (Directory.Exists(path)) return Path.GetFullPath(path);
        }
        catch { }
        return null;
    }

    public static long ParseTimeMs(string iso, long fallback = 0L)
    {
        if (string.IsNullOrEmpty(iso)) return fallback;
        DateTimeOffset dto;
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
            return dto.ToUnixTimeMilliseconds();
        long ms;
        if (long.TryParse(iso, NumberStyles.Integer, CultureInfo.InvariantCulture, out ms) && ms > 0)
            return ms;
        return fallback;
    }

    public static ScanResult Scan(bool allFromStart)
    {
        var res = new ScanResult { AllFromStart = allFromStart };
        ScanZayit(res.Zayit, allFromStart);
        ScanOtzaria(res.Otzaria, allFromStart);
        return res;
    }

    // ---- 1. Zayit (זית) ----

    static void ScanZayit(SourceSnapshot snap, bool all)
    {
        long sinceSearches = all ? 0L : Prefs.GetSentAt("zayit", "searches");
        long sinceGrades = all ? 0L : Prefs.GetSentAt("zayit", "grades");
        long sinceDaf = all ? 0L : Prefs.GetSentAt("zayit", "daf");

        var settingsDbs = new List<KeyValuePair<string, string>>(); // (dbPath, sourceTag)
        var libraryDirs = new List<string>();

        if (TestZayitRoots != null)
        {
            foreach (var r in TestZayitRoots) AddZayitCandidate(r, "zayit", settingsDbs, libraryDirs);
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // 1. Standard AppData and LocalAppData folders
            AddZayitCandidate(Path.Combine(appData, "io.github.kdroidfilter.seforimapp"), "zayit", settingsDbs, libraryDirs);
            AddZayitCandidate(Path.Combine(localAppData, "io.github.kdroidfilter.seforimapp"), "zayit", settingsDbs, libraryDirs);
            AddZayitCandidate(Path.Combine(appData, "זית"), "zayit", settingsDbs, libraryDirs);
            AddZayitCandidate(Path.Combine(localAppData, "זית"), "zayit", settingsDbs, libraryDirs);
            AddZayitCandidate(Path.Combine(appData, "Zayit"), "zayit", settingsDbs, libraryDirs);
            AddZayitCandidate(Path.Combine(localAppData, "Zayit"), "zayit", settingsDbs, libraryDirs);

            // 2. Microsoft Store (AppX/MSIX) packages in %LOCALAPPDATA%\Packages
            try
            {
                var pkgDir = Path.Combine(localAppData, "Packages");
                if (Directory.Exists(pkgDir))
                {
                    foreach (var d in Directory.GetDirectories(pkgDir, "*"))
                    {
                        var name = Path.GetFileName(d);
                        if (name.IndexOf("Zayit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("SeforimApp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.StartsWith("KdroidFilter.", StringComparison.OrdinalIgnoreCase))
                        {
                            AddZayitCandidate(Path.Combine(d, "LocalCache", "Roaming", "io.github.kdroidfilter.seforimapp"), "zayit", settingsDbs, libraryDirs);
                            AddZayitCandidate(Path.Combine(d, "LocalCache", "Local", "io.github.kdroidfilter.seforimapp"), "zayit", settingsDbs, libraryDirs);
                            AddZayitCandidate(Path.Combine(d, "LocalState", "io.github.kdroidfilter.seforimapp"), "zayit", settingsDbs, libraryDirs);
                        }
                    }
                }
            }
            catch { }

            // 3. Registry database_path preference
            var prefLib = Prefs.Get("database_path", "");
            if (!string.IsNullOrEmpty(prefLib))
            {
                AddZayitCandidate(prefLib, "zayit", settingsDbs, libraryDirs);
                var norm = NormalizeToDir(prefLib);
                if (!string.IsNullOrEmpty(norm))
                {
                    AddZayitLibraryDir(norm, settingsDbs, libraryDirs);
                    var parent = Path.GetDirectoryName(norm);
                    if (!string.IsNullOrEmpty(parent))
                        AddZayitCandidate(parent, "zayit", settingsDbs, libraryDirs);
                }
            }

            // 4. Environment variable
            var envLib = Environment.GetEnvironmentVariable("SEFORIMAPP_DATABASE_PATH");
            if (!string.IsNullOrEmpty(envLib))
            {
                AddZayitCandidate(envLib, "zayit", settingsDbs, libraryDirs);
                var norm = NormalizeToDir(envLib);
                if (!string.IsNullOrEmpty(norm)) AddZayitLibraryDir(norm, settingsDbs, libraryDirs);
            }

            // 5. Common installation and document locations
            AddZayitCandidate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Zayit"), "zayit", settingsDbs, libraryDirs);
            AddZayitCandidate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ספריית זית"), "zayit", settingsDbs, libraryDirs);
            AddZayitCandidate(Path.Combine(localAppData, "Programs", "Zayit"), "zayit", settingsDbs, libraryDirs);
            AddZayitCandidate(Path.Combine(localAppData, "Programs", "זית"), "zayit", settingsDbs, libraryDirs);

            // 6. User-added extra folders
            foreach (var extra in ExtraFolders)
                AddZayitCandidate(extra, "zayit", settingsDbs, libraryDirs);
        }

        var searchesByKey = new Dictionary<string, SearchRow>(StringComparer.Ordinal);
        var gradesByKey = new Dictionary<string, GradeRow>(StringComparer.Ordinal);

        foreach (var kv in settingsDbs)
        {
            string dbPath = kv.Key;
            string srcTag = kv.Value;
            if (!File.Exists(dbPath)) continue;
            snap.Found = true;
            if (string.IsNullOrEmpty(snap.PathFound))
                snap.PathFound = Path.GetDirectoryName(dbPath);

            // Read visit_history
            var hRows = Sqlite.ReadTable(dbPath, "visit_history",
                "SELECT kind, searchQuery, searchCategoryId, searchBookId, searchTocId, searchExtended, visitCount, visitedAt FROM visit_history WHERE kind = 'search' AND searchQuery IS NOT NULL",
                ForceManagedSqlite);
            foreach (var r in hRows)
            {
                var kind = Json.Str(r, "kind", "search");
                if (!string.Equals(kind, "search", StringComparison.OrdinalIgnoreCase)) continue;
                var q = CollapseSpaces(Json.Str(r, "searchQuery", ""));
                if (string.IsNullOrEmpty(q) || q.Length > FeedbackPackage.MAX_QUERY) continue;
                long visitedAt = Json.Long(r, "visitedAt", 0L);
                if (visitedAt <= sinceSearches) continue;
                var row = new SearchRow
                {
                    Query = q,
                    CategoryId = Json.LongOrNull(r, "searchCategoryId"),
                    BookId = Json.LongOrNull(r, "searchBookId"),
                    TocId = Json.LongOrNull(r, "searchTocId"),
                    Extended = Json.Long(r, "searchExtended", 0L) != 0,
                    Source = srcTag,
                    Count = Math.Max(1L, Json.Long(r, "visitCount", 1L)),
                    LastAt = visitedAt
                };
                SearchRow prev;
                if (!searchesByKey.TryGetValue(row.Key, out prev) || row.LastAt >= prev.LastAt)
                    searchesByKey[row.Key] = row;
            }

            // Read search_feedback
            var gRows = Sqlite.ReadTable(dbPath, "search_feedback",
                "SELECT searchKey, query, lineId, bookId, grade, rank, searchCategoryId, searchBookId, searchTocId, searchExtended, model, gradedAt FROM search_feedback",
                ForceManagedSqlite);
            foreach (var r in gRows)
            {
                var q = CollapseSpaces(Json.Str(r, "query", ""));
                var sk = Json.Str(r, "searchKey", "");
                int grade = (int)Json.Long(r, "grade", -1L);
                long gradedAt = Json.Long(r, "gradedAt", 0L);
                long lineId = Json.Long(r, "lineId", -1L);
                long bookId = Json.Long(r, "bookId", -1L);
                long rank = Json.Long(r, "rank", -1L);
                if (string.IsNullOrEmpty(q) || q.Length > FeedbackPackage.MAX_QUERY || string.IsNullOrEmpty(sk)) continue;
                if ((grade != 0 && grade != 2 && grade != 3) || lineId < 0 || bookId < 0 || rank < 0 || gradedAt <= sinceGrades) continue;
                var g = new GradeRow
                {
                    Query = q,
                    Search = sk,
                    LineId = lineId,
                    BookId = bookId,
                    Grade = grade,
                    Rank = rank,
                    CategoryId = Json.LongOrNull(r, "searchCategoryId"),
                    ScopeBookId = Json.LongOrNull(r, "searchBookId"),
                    TocId = Json.LongOrNull(r, "searchTocId"),
                    Extended = Json.Long(r, "searchExtended", 0L) != 0,
                    Model = Json.Str(r, "model", null),
                    Source = srcTag,
                    GradedAt = gradedAt
                };
                string gk = sk + "\u0000" + lineId;
                GradeRow prev;
                if (!gradesByKey.TryGetValue(gk, out prev) || g.GradedAt >= prev.GradedAt)
                    gradesByKey[gk] = g;
            }
        }

        // Read library version & daf-layouts/feedback/*.json
        var dafByTractate = new Dictionary<string, DafReview>(StringComparer.OrdinalIgnoreCase);
        foreach (var libDir in libraryDirs)
        {
            if (!Directory.Exists(libDir)) continue;
            if (File.Exists(Path.Combine(libDir, "seforim.db")) || Directory.Exists(Path.Combine(libDir, "daf-layouts")))
            {
                snap.Found = true;
                if (string.IsNullOrEmpty(snap.PathFound)) snap.PathFound = libDir;
            }
            if (string.IsNullOrEmpty(snap.LibraryVersion))
                snap.LibraryVersion = ReadLibraryVersion(libDir);

            var fbDir = Path.Combine(libDir, "daf-layouts", "feedback");
            if (!Directory.Exists(fbDir)) continue;
            foreach (var file in Directory.GetFiles(fbDir, "*.json"))
            {
                try
                {
                    long fileMs = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();
                    var obj = Json.ParseObj(File.ReadAllText(file, Encoding.UTF8));
                    if (obj == null) continue;
                    long fmt = Json.Long(obj, "format", 1L);
                    var tractate = Json.Str(obj, "tractate", Path.GetFileNameWithoutExtension(file));
                    if (fmt != 1 || string.IsNullOrEmpty(tractate)) continue;
                    var marksArr = Json.Arr(obj, "marks");
                    if (marksArr == null || marksArr.Count == 0) continue;

                    var kept = new List<DafMark>();
                    foreach (var item in marksArr)
                    {
                        var m = item as Dictionary<string, object>;
                        if (m == null) continue;
                        var amud = Json.Str(m, "amud", "");
                        var c = Json.Str(m, "c", "");
                        long idx = Json.Long(m, "i", -1L);
                        var at = Json.Str(m, "at", null);
                        long atMs = ParseTimeMs(at, fileMs);
                        if (string.IsNullOrEmpty(amud) || string.IsNullOrEmpty(c) || idx < 0 || atMs <= sinceDaf) continue;
                        kept.Add(new DafMark
                        {
                            Amud = amud,
                            C = c,
                            I = idx,
                            T = Json.Str(m, "t", ""),
                            Ok = Json.BoolOrNull(m, "ok"),
                            Cut = Json.Long(m, "cut", 0L),
                            At = at,
                            AtMs = atMs
                        });
                    }
                    if (kept.Count > 0)
                    {
                        dafByTractate[tractate] = new DafReview
                        {
                            Format = 1,
                            Tractate = tractate,
                            TractateHe = Json.Str(obj, "tractateHe", tractate),
                            Library = Json.Str(obj, "library", snap.LibraryVersion),
                            Marks = kept
                        };
                    }
                }
                catch { }
            }
        }

        snap.Searches.AddRange(searchesByKey.Values);
        snap.Searches.Sort((a, b) => a.LastAt.CompareTo(b.LastAt));
        snap.Grades.AddRange(gradesByKey.Values);
        snap.Grades.Sort((a, b) => a.GradedAt.CompareTo(b.GradedAt));
        snap.Daf.AddRange(dafByTractate.Values);
    }

    static void AddZayitCandidate(string path, string srcTag, List<KeyValuePair<string, string>> dbs, List<string> libs)
    {
        if (string.IsNullOrEmpty(path)) return;
        path = path.Trim().Trim('"', '\'');

        // Direct user_settings.db file
        if (path.EndsWith("user_settings.db", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            TryAddDb(path, srcTag, dbs);
            var parentDir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(parentDir)) AddZayitLibraryDir(parentDir, dbs, libs);
            return;
        }

        // Direct seforim.db file
        if (path.EndsWith("seforim.db", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            var parentDir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(parentDir))
            {
                AddZayitLibraryDir(parentDir, dbs, libs);
                AddZayitCandidate(parentDir, srcTag, dbs, libs);
            }
            return;
        }

        string dir = NormalizeToDir(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        // Check user_settings.db in all known directory structures:
        TryAddDb(Path.Combine(dir, "user_settings.db"), srcTag, dbs);
        TryAddDb(Path.Combine(dir, "settings", "user_settings.db"), srcTag, dbs);
        TryAddDb(Path.Combine(dir, "databases", "settings", "user_settings.db"), srcTag, dbs);
        TryAddDb(Path.Combine(dir, "databases", "user_settings.db"), srcTag, dbs);
        TryAddDb(Path.Combine(dir, ".zayit-profile", "io.github.kdroidfilter.seforimapp", "databases", "settings", "user_settings.db"), srcTag, dbs);

        AddZayitLibraryDir(dir, dbs, libs);
        AddZayitLibraryDir(Path.Combine(dir, "databases"), dbs, libs);
    }

    static void AddZayitLibraryDir(string path, List<KeyValuePair<string, string>> dbs, List<string> libs)
    {
        string libDir = NormalizeToDir(path);
        if (string.IsNullOrEmpty(libDir) || !Directory.Exists(libDir)) return;
        string full = Path.GetFullPath(libDir);
        if (!libs.Contains(full)) libs.Add(full);
        TryAddDb(Path.Combine(full, "user_settings.db"), "zayit", dbs);
        TryAddDb(Path.Combine(full, "settings", "user_settings.db"), "zayit", dbs);
        TryAddDb(Path.Combine(full, "databases", "settings", "user_settings.db"), "zayit", dbs);
        TryAddDb(Path.Combine(full, "databases", "user_settings.db"), "zayit", dbs);
        TryAddDb(Path.Combine(full, ".zayit-profile", "io.github.kdroidfilter.seforimapp", "databases", "settings", "user_settings.db"), "zayit", dbs);
    }

    static void TryAddDb(string path, string srcTag, List<KeyValuePair<string, string>> dbs)
    {
        if (!File.Exists(path)) return;
        string full = Path.GetFullPath(path);
        foreach (var kv in dbs)
            if (string.Equals(kv.Key, full, StringComparison.OrdinalIgnoreCase)) return;
        dbs.Add(new KeyValuePair<string, string>(full, srcTag));
    }

    static string ReadLibraryVersion(string dir)
    {
        var f = Path.Combine(dir, "release_info.txt");
        if (!File.Exists(f)) return null;
        try
        {
            foreach (var raw in File.ReadAllLines(f, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                int eq = line.IndexOf('=');
                if (eq > 0 && line.Substring(0, eq).Trim().Equals("version", StringComparison.OrdinalIgnoreCase))
                    return line.Substring(eq + 1).Trim();
                if (eq < 0) return line;
            }
        }
        catch { }
        return null;
    }

    // ---- 2. Otzaria (אוצריא) ----

    static void ScanOtzaria(SourceSnapshot snap, bool all)
    {
        long sinceSearches = all ? 0L : Prefs.GetSentAt("otzaria", "searches");
        long sinceGrades = all ? 0L : Prefs.GetSentAt("otzaria", "grades");

        var dataRoots = new List<string>();
        var dbDirs = new List<string>();

        if (TestOtzariaRoots != null)
        {
            foreach (var r in TestOtzariaRoots) AddOtzariaRoot(r, dataRoots, dbDirs);
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // 1. Standard AppData and LocalAppData roots
            AddOtzariaRoot(Path.Combine(appData, "otzaria"), dataRoots, dbDirs);
            AddOtzariaRoot(Path.Combine(appData, "com.example", "otzaria"), dataRoots, dbDirs);
            AddOtzariaRoot(Path.Combine(localAppData, "otzaria"), dataRoots, dbDirs);

            // 2. Read library_path.txt / library_database_path.txt from %APPDATA%\otzaria
            try
            {
                var recDb = Path.Combine(appData, "otzaria", "library_database_path.txt");
                if (File.Exists(recDb))
                {
                    var p = File.ReadAllText(recDb, Encoding.UTF8).Trim('\uFEFF', ' ', '\t', '\r', '\n', '"');
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) AddOtzariaRoot(p, dataRoots, dbDirs);
                }
                var recLib = Path.Combine(appData, "otzaria", "library_path.txt");
                if (File.Exists(recLib))
                {
                    var p = File.ReadAllText(recLib, Encoding.UTF8).Trim('\uFEFF', ' ', '\t', '\r', '\n', '"');
                    if (!string.IsNullOrEmpty(p))
                    {
                        var norm = NormalizeToDir(p);
                        if (!string.IsNullOrEmpty(norm)) AddOtzariaRoot(norm, dataRoots, dbDirs);
                    }
                }
            }
            catch { }

            // 3. Known installation locations & portable
            AddOtzariaRoot(@"C:\אוצריא", dataRoots, dbDirs);
            AddOtzariaRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "אוצריא"), dataRoots, dbDirs);
            AddOtzariaRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Otzaria"), dataRoots, dbDirs);
            AddOtzariaRoot(Path.Combine(localAppData, "Programs", "Otzaria"), dataRoots, dbDirs);
            AddOtzariaRoot(Path.Combine(localAppData, "Programs", "אוצריא"), dataRoots, dbDirs);

            // 4. User-added extra folders
            foreach (var extra in ExtraFolders)
                AddOtzariaRoot(extra, dataRoots, dbDirs);
        }

        var searchesByKey = new Dictionary<string, SearchRow>(StringComparer.Ordinal);
        var gradesByKey = new Dictionary<string, GradeRow>(StringComparer.Ordinal);

        // 1. Read user_state.db in each databases directory
        foreach (var dbDir in dbDirs)
        {
            var stateDb = Path.Combine(dbDir, "user_state.db");
            if (!File.Exists(stateDb)) continue;
            snap.Found = true;
            if (string.IsNullOrEmpty(snap.PathFound)) snap.PathFound = dbDir;

            var listRows = Sqlite.ReadTable(stateDb, "lists",
                "SELECT box, key, payload_json, updated_at FROM lists",
                ForceManagedSqlite);
            foreach (var r in listRows)
            {
                var box = Json.Str(r, "box", "");
                var key = Json.Str(r, "key", "");
                var payload = Json.Str(r, "payload_json", "");
                long updatedAt = NormalizeEpochMs(Json.Long(r, "updated_at", 0L));
                if (string.IsNullOrEmpty(payload)) continue;

                if (string.Equals(box, "history", StringComparison.OrdinalIgnoreCase))
                {
                    ParseOtzariaBookmarksJson(payload, updatedAt, sinceSearches, searchesByKey);
                }
                else if (string.Equals(box, "workspaces", StringComparison.OrdinalIgnoreCase))
                {
                    ParseOtzariaWorkspacesJson(payload, updatedAt, sinceSearches, searchesByKey);
                }
            }

            var sessionRows = Sqlite.ReadTable(stateDb, "window_sessions",
                "SELECT tabs_json, updated_at FROM window_sessions",
                ForceManagedSqlite);
            foreach (var r in sessionRows)
            {
                var tabsJson = Json.Str(r, "tabs_json", "");
                long updatedAt = NormalizeEpochMs(Json.Long(r, "updated_at", 0L));
                if (!string.IsNullOrEmpty(tabsJson))
                    ParseOtzariaTabsJson(tabsJson, updatedAt, sinceSearches, searchesByKey);
            }
        }

        // 2. Read search_feedback/*.jsonl and legacy history.hive in each Otzaria dataRoot
        foreach (var root in dataRoots)
        {
            if (!Directory.Exists(root)) continue;

            // Check search_feedback queue segments (active.jsonl, seg-*.jsonl, unverified/*.jsonl)
            foreach (var fbSub in new[] { Path.Combine(root, "search_feedback"), Path.Combine(root, "search_feedback", "unverified") })
            {
                if (!Directory.Exists(fbSub)) continue;
                snap.Found = true;
                if (string.IsNullOrEmpty(snap.PathFound)) snap.PathFound = root;
                foreach (var file in Directory.GetFiles(fbSub, "*.jsonl"))
                    ParseOtzariaFeedbackJsonl(file, snap, sinceSearches, sinceGrades, searchesByKey, gradesByKey);
            }

            // Check legacy history.hive if present
            foreach (var hiveFile in new[] { Path.Combine(root, "history.hive"), Path.Combine(root, "bookmarks", "history.hive") })
            {
                if (!File.Exists(hiveFile)) continue;
                snap.Found = true;
                if (string.IsNullOrEmpty(snap.PathFound)) snap.PathFound = root;
                ParseOtzariaHistoryHive(hiveFile, sinceSearches, searchesByKey);
            }
        }

        snap.Searches.AddRange(searchesByKey.Values);
        snap.Searches.Sort((a, b) => a.LastAt.CompareTo(b.LastAt));
        snap.Grades.AddRange(gradesByKey.Values);
        snap.Grades.Sort((a, b) => a.GradedAt.CompareTo(b.GradedAt));
    }

    static void AddOtzariaRoot(string root, List<string> dataRoots, List<string> dbDirs)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        string full = Path.GetFullPath(root);
        if (!dataRoots.Contains(full)) dataRoots.Add(full);
        if (!dbDirs.Contains(full)) dbDirs.Add(full);
        var subDb = Path.Combine(full, "databases");
        if (Directory.Exists(subDb) && !dbDirs.Contains(subDb)) dbDirs.Add(subDb);

        // Read library_path.txt (Otzaria stores the path to "אוצריא" library folder here)
        var libTxt = Path.Combine(full, "library_path.txt");
        if (File.Exists(libTxt))
        {
            try
            {
                var libPath = File.ReadAllText(libTxt, Encoding.UTF8).Trim('\uFEFF', ' ', '\t', '\r', '\n', '"');
                if (!string.IsNullOrEmpty(libPath) && Directory.Exists(libPath))
                {
                    var libDb = Path.Combine(libPath, "databases");
                    if (Directory.Exists(libDb) && !dbDirs.Contains(libDb)) dbDirs.Add(libDb);
                    var parent = Directory.GetParent(libPath);
                    if (parent != null)
                    {
                        var parentDb = Path.Combine(parent.FullName, "databases");
                        if (Directory.Exists(parentDb) && !dbDirs.Contains(parentDb)) dbDirs.Add(parentDb);
                    }
                }
            }
            catch { }
        }
    }

    static long NormalizeEpochMs(long v)
    {
        if (v <= 0) return 0L;
        // If timestamp is in seconds (< 10^11), convert to milliseconds; if in microseconds (> 10^14), divide by 1000.
        if (v < 100000000000L) return v * 1000L;
        if (v > 100000000000000L) return v / 1000L;
        return v;
    }

    static void ParseOtzariaBookmarksJson(string jsonText, long fallbackMs, long sinceSearches, Dictionary<string, SearchRow> searchesByKey)
    {
        var parsed = Json.Parse(jsonText);
        var arr = parsed as List<object>;
        if (arr == null)
        {
            var obj = parsed as Dictionary<string, object>;
            if (obj != null) arr = Json.Arr(obj, "items") ?? Json.Arr(obj, "bookmarks") ?? Json.Arr(obj, "history");
        }
        if (arr == null) return;

        foreach (var item in arr)
        {
            var bm = item as Dictionary<string, object>;
            if (bm == null) continue;
            bool isSearch = Json.BoolOrNull(bm, "isSearch") ?? false;
            var bookObj = Json.Obj(bm, "book");
            var bookTitle = Json.Str(bm, "bookTitle", null) ?? Json.Str(bookObj, "title", null);
            var rawRef = Json.Str(bm, "ref", "");
            if (!isSearch && !string.Equals(bookTitle, "חיפוש", StringComparison.Ordinal) && !rawRef.StartsWith("חיפוש:", StringComparison.Ordinal))
                continue;

            // In Otzaria SearchingTab, book.title holds the raw query text, or ref holds "חיפוש: <query>" / "<query>".
            string query = bookObj != null ? Json.Str(bookObj, "title", null) : null;
            if (string.IsNullOrEmpty(query) || query == "חיפוש")
            {
                query = rawRef;
                if (query.StartsWith("חיפוש:", StringComparison.Ordinal))
                    query = query.Substring("חיפוש:".Length);
            }
            query = CollapseSpaces(query);
            if (string.IsNullOrEmpty(query) || query == "חיפוש" || query.Length > FeedbackPackage.MAX_QUERY)
                continue;

            string scope = FormatFacets(Json.Arr(bm, "searchScopeFacets"));
            long createdAt = ParseTimeMs(Json.Str(bm, "createdAt", null), fallbackMs);
            if (createdAt <= 0) createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (createdAt <= sinceSearches) continue;

            AddOtzariaSearch(searchesByKey, query, scope, createdAt);
        }
    }

    static void ParseOtzariaWorkspacesJson(string jsonText, long fallbackMs, long sinceSearches, Dictionary<string, SearchRow> searchesByKey)
    {
        var arr = Json.Parse(jsonText) as List<object>;
        if (arr == null) return;
        foreach (var item in arr)
        {
            var ws = item as Dictionary<string, object>;
            if (ws == null) continue;
            var tabs = Json.Arr(ws, "tabs");
            if (tabs != null) ParseOtzariaTabList(tabs, fallbackMs, sinceSearches, searchesByKey);
        }
    }

    static void ParseOtzariaTabsJson(string jsonText, long fallbackMs, long sinceSearches, Dictionary<string, SearchRow> searchesByKey)
    {
        var arr = Json.Parse(jsonText) as List<object>;
        if (arr != null) ParseOtzariaTabList(arr, fallbackMs, sinceSearches, searchesByKey);
    }

    static void ParseOtzariaTabList(List<object> tabs, long fallbackMs, long sinceSearches, Dictionary<string, SearchRow> searchesByKey)
    {
        if (fallbackMs <= sinceSearches) return;
        foreach (var item in tabs)
        {
            var tab = item as Dictionary<string, object>;
            if (tab == null) continue;
            var type = Json.Str(tab, "type", "");
            if (type != "SearchingTabWindow" && type != "search") continue;
            var q = CollapseSpaces(Json.Str(tab, "searchText", null) ?? Json.Str(tab, "query", ""));
            if (string.IsNullOrEmpty(q) || q.Length > FeedbackPackage.MAX_QUERY) continue;
            string scope = FormatFacets(Json.Arr(tab, "searchScopeFacets") ?? Json.Arr(tab, "facets"));
            AddOtzariaSearch(searchesByKey, q, scope, fallbackMs > 0 ? fallbackMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    static void ParseOtzariaFeedbackJsonl(string file, SourceSnapshot snap, long sinceSearches, long sinceGrades,
        Dictionary<string, SearchRow> searchesByKey, Dictionary<string, GradeRow> gradesByKey)
    {
        try
        {
            long fileMs = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();
            var sessionQueries = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
            foreach (var rawLine in File.ReadAllLines(file, Encoding.UTF8))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                var obj = Json.ParseObj(line);
                if (obj == null) continue;

                // Line 1 in Otzaria search_feedback segment is ClientContext
                if (obj.ContainsKey("libraryVersion") || string.Equals(Json.Str(obj, "app", ""), "otzaria", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(snap.LibraryVersion))
                        snap.LibraryVersion = Json.Str(obj, "libraryVersion", null) ?? Json.Str(obj, "appVersion", null);
                }

                var type = Json.Str(obj, "type", "");
                var sessionId = Json.Str(obj, "searchSessionId", "");
                long clientMs = ParseTimeMs(Json.Str(obj, "clientTime", null), fileMs);

                if (type == "search")
                {
                    var q = CollapseSpaces(Json.Str(obj, "query", ""));
                    if (string.IsNullOrEmpty(q) || q.Length > FeedbackPackage.MAX_QUERY) continue;
                    var paramsObj = Json.Obj(obj, "params");
                    var scopeObj = Json.Obj(paramsObj, "scope");
                    string scope = FormatFacets(Json.Arr(scopeObj, "facets"));
                    if (!string.IsNullOrEmpty(sessionId))
                        sessionQueries[sessionId] = new KeyValuePair<string, string>(q, scope);
                    if (clientMs > sinceSearches)
                        AddOtzariaSearch(searchesByKey, q, scope, clientMs);
                }
                else if (type == "vote")
                {
                    if (clientMs <= sinceGrades) continue;
                    var vote = Json.Str(obj, "vote", "");
                    int grade = vote == "like" ? 2 : (vote == "dislike" ? 0 : (int)Json.Long(obj, "grade", -1L));
                    if (grade != 0 && grade != 2 && grade != 3) continue;

                    string q = CollapseSpaces(Json.Str(obj, "query", ""));
                    string scope = null;
                    KeyValuePair<string, string> sess;
                    if (!string.IsNullOrEmpty(sessionId) && sessionQueries.TryGetValue(sessionId, out sess))
                    {
                        if (string.IsNullOrEmpty(q)) q = sess.Key;
                        scope = sess.Value;
                    }
                    if (string.IsNullOrEmpty(q) || q.Length > FeedbackPackage.MAX_QUERY) continue;

                    var resObj = Json.Obj(obj, "result");
                    long rank = Math.Max(0L, Json.Long(resObj, "rank", 0L));
                    long docId = Math.Max(0L, Json.Long(resObj, "docId", Json.Long(resObj, "segment", 0L)));
                    var title = Json.Str(resObj, "title", null);
                    var heRef = Json.Str(resObj, "reference", null);
                    string searchKey = "otzaria:" + (scope ?? "") + ":" + q;

                    var g = new GradeRow
                    {
                        Query = q,
                        Search = searchKey,
                        LineId = docId,
                        BookId = 0L,
                        Grade = grade,
                        Rank = rank,
                        Source = "otzaria",
                        Scope = scope,
                        BookTitle = title,
                        HeRef = heRef,
                        GradedAt = clientMs
                    };
                    string gk = searchKey + "\u0000" + docId + "\u0000" + (heRef ?? "");
                    GradeRow prev;
                    if (!gradesByKey.TryGetValue(gk, out prev) || g.GradedAt >= prev.GradedAt)
                        gradesByKey[gk] = g;
                }
            }
        }
        catch { }
    }

    static void ParseOtzariaHistoryHive(string hiveFile, long sinceSearches, Dictionary<string, SearchRow> searchesByKey)
    {
        try
        {
            long fileMs = new DateTimeOffset(File.GetLastWriteTimeUtc(hiveFile)).ToUnixTimeMilliseconds();
            if (fileMs <= sinceSearches) return;
            var bytes = File.ReadAllBytes(hiveFile);
            var text = Encoding.UTF8.GetString(bytes);
            // Extract embedded JSON objects containing "isSearch":true or "חיפוש:"
            int pos = 0;
            while ((pos = text.IndexOf("חיפוש:", pos, StringComparison.Ordinal)) >= 0)
            {
                int start = pos + "חיפוש:".Length;
                int end = start;
                while (end < text.Length && end - start < 300)
                {
                    char c = text[end];
                    if (c == '"' || c == '\0' || c == '\r' || c == '\n' || c < 0x20) break;
                    end++;
                }
                var q = CollapseSpaces(text.Substring(start, end - start));
                if (!string.IsNullOrEmpty(q) && q.Length <= FeedbackPackage.MAX_QUERY)
                    AddOtzariaSearch(searchesByKey, q, null, fileMs);
                pos = end + 1;
            }
        }
        catch { }
    }

    static string FormatFacets(List<object> facets)
    {
        if (facets == null || facets.Count == 0) return null;
        var list = new List<string>();
        foreach (var f in facets)
        {
            var s = f != null ? f.ToString().Trim() : "";
            if (!string.IsNullOrEmpty(s) && s != "/") list.Add(s);
        }
        if (list.Count == 0) return null;
        string joined = string.Join(", ", list.ToArray());
        return joined.Length > 500 ? joined.Substring(0, 500) : joined;
    }

    static void AddOtzariaSearch(Dictionary<string, SearchRow> searchesByKey, string query, string scope, long atMs)
    {
        var row = new SearchRow
        {
            Query = query,
            Scope = scope,
            Source = "otzaria",
            Count = 1L,
            LastAt = atMs
        };
        SearchRow prev;
        if (searchesByKey.TryGetValue(row.Key, out prev))
        {
            prev.Count += 1L;
            if (atMs > prev.LastAt) prev.LastAt = atMs;
        }
        else
        {
            searchesByKey[row.Key] = row;
        }
    }

    // ---- 3. Package Construction & Recording Sent Timestamps ----

    public static FeedbackPackage BuildPackage(
        ScanResult scan,
        bool includeZayit,
        bool includeOtzaria,
        bool includeSearches,
        bool includeGrades,
        bool includeDaf,
        HashSet<string> excludedSearchKeys = null)
    {
        var pkg = new FeedbackPackage
        {
            Sender = Prefs.GetSender(),
            SentAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            Library = scan.Zayit.LibraryVersion ?? scan.Otzaria.LibraryVersion
        };

        if (includeZayit && !includeOtzaria) pkg.Source = "zayit";
        else if (includeOtzaria && !includeZayit) pkg.Source = "otzaria";

        var sources = new List<SourceSnapshot>();
        if (includeZayit && scan.Zayit.Found) sources.Add(scan.Zayit);
        if (includeOtzaria && scan.Otzaria.Found) sources.Add(scan.Otzaria);

        foreach (var src in sources)
        {
            if (includeSearches)
            {
                foreach (var s in src.Searches)
                {
                    if (excludedSearchKeys != null && excludedSearchKeys.Contains(s.Key)) continue;
                    pkg.Searches.Add(s);
                }
            }
            if (includeGrades) pkg.Grades.AddRange(src.Grades);
            if (includeDaf) pkg.Daf.AddRange(src.Daf);
        }
        return pkg;
    }

    public static void RecordSent(ScanResult scan, bool includeZayit, bool includeOtzaria, bool includeSearches, bool includeGrades, bool includeDaf)
    {
        if (includeZayit && scan.Zayit.Found)
        {
            if (includeSearches && scan.Zayit.MaxSearchAt > 0) Prefs.SetSentAt("zayit", "searches", scan.Zayit.MaxSearchAt);
            if (includeGrades && scan.Zayit.MaxGradeAt > 0) Prefs.SetSentAt("zayit", "grades", scan.Zayit.MaxGradeAt);
            if (includeDaf && scan.Zayit.MaxDafAt > 0) Prefs.SetSentAt("zayit", "daf", scan.Zayit.MaxDafAt);
        }
        if (includeOtzaria && scan.Otzaria.Found)
        {
            if (includeSearches && scan.Otzaria.MaxSearchAt > 0) Prefs.SetSentAt("otzaria", "searches", scan.Otzaria.MaxSearchAt);
            if (includeGrades && scan.Otzaria.MaxGradeAt > 0) Prefs.SetSentAt("otzaria", "grades", scan.Otzaria.MaxGradeAt);
        }
    }
}
