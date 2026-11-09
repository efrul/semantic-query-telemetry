using System;
using System.Collections.Generic;
using System.IO;

// Zayit's searches, as Zayit itself keeps them (kdroidFilter/Zayit, v0.6.0 and later):
//   %APPDATA%\io.github.kdroidfilter.seforimapp\databases\settings\user_settings.db, table visit_history
//   (SeforimApp/src/commonMain/sqldelight/.../VisitHistory.sq; FileKit's filesDir is %APPDATA%\<appId>).
// A row of kind 'search' is one search: its words (searchQuery), its scope (in its key: search:<scope>:<query>, the
// scope g, b<book>, t<toc> or a<author>, with x when extended), how many times it was made (visitCount, one row a
// search, counted up) and when last (visitedAt, epoch ms). Zayit keeps no grades and no daf marks: those are Pardes's,
// which sends its own. A Store install keeps the same folders under LocalCache\Roaming of its package.
// The library's version is release_info.txt beside seforim.db.
public sealed class ZayitReader : IReader
{
    public const string AppId = "io.github.kdroidfilter.seforimapp";
    const string PardesId = "io.github.efrul.pardes";

    readonly List<string> databases = new List<string>();
    readonly List<string> libraryFolders = new List<string>();
    readonly List<string> problems = new List<string>();

    // [databases]: user_settings.db files to read; [libraryFolders]: where seforim.db may be, for the version.
    public ZayitReader(IEnumerable<string> databases, IEnumerable<string> libraryFolders)
    {
        foreach (var d in databases) AddDatabase(d);
        foreach (var l in libraryFolders) if (!string.IsNullOrEmpty(l)) this.libraryFolders.Add(l);
    }

    // Zayit's places on this computer, and the folders the user added. [databasePath] is Zayit's database_path
    // preference (its library's seforim.db), when the app found one.
    public static ZayitReader ForThisComputer(IEnumerable<string> extraFolders, string databasePath)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbs = new List<string>();
        var libs = new List<string>();
        var home = Path.Combine(appData, AppId);
        dbs.Add(Path.Combine(home, "databases", "settings", "user_settings.db"));
        libs.Add(Path.Combine(home, "databases"));
        try
        {
            var packages = Path.Combine(local, "Packages");
            if (Directory.Exists(packages))
                foreach (var p in Directory.GetDirectories(packages, "KdroidFilter.Zayit_*"))
                {
                    var roaming = Path.Combine(p, "LocalCache", "Roaming", AppId);
                    dbs.Add(Path.Combine(roaming, "databases", "settings", "user_settings.db"));
                    libs.Add(Path.Combine(roaming, "databases"));
                }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        foreach (var lib in new[] { Environment.GetEnvironmentVariable("SEFORIMAPP_DATABASE_PATH"), databasePath })
            if (!string.IsNullOrEmpty(lib)) libs.Add(lib.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(lib) : lib);
        foreach (var f in extraFolders)
        {
            foreach (var rel in new[] { "user_settings.db", Path.Combine("settings", "user_settings.db"),
                Path.Combine("databases", "settings", "user_settings.db"), Path.Combine(AppId, "databases", "settings", "user_settings.db") })
                dbs.Add(Path.Combine(f, rel));
            libs.Add(f);
            libs.Add(Path.Combine(f, "databases"));
        }
        return new ZayitReader(dbs, libs);
    }

    void AddDatabase(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        // Pardes sends its own, with the user's consent there; its folder is never read here
        if (path.IndexOf(PardesId, StringComparison.OrdinalIgnoreCase) >= 0) return;
        foreach (var d in databases) if (string.Equals(d, path, StringComparison.OrdinalIgnoreCase)) return;
        databases.Add(path);
    }

    public SourceData Read()
    {
        var data = new SourceData { Source = Source.Zayit };
        data.Problems.AddRange(problems);
        var byKey = new Dictionary<string, SearchRow>(StringComparer.Ordinal);
        foreach (var db in databases)
        {
            if (!File.Exists(db)) continue;
            data.Found = true;
            data.AddPlace(Path.GetDirectoryName(db));
            ReadSearches(db, byKey, data);
        }
        data.Searches.AddRange(byKey.Values);
        foreach (var lib in libraryFolders)
        {
            var v = LibraryVersion(lib);
            if (v != null) { data.Library = v; break; }
        }
        return data;
    }

    static void ReadSearches(string path, Dictionary<string, SearchRow> byKey, SourceData data)
    {
        string error;
        using (var db = Sqlite.Open(path, out error))
        {
            if (db == null) { data.Problems.Add("לא הצלחתי לקרוא את " + path + ": " + error); return; }
            var cols = Sqlite.Columns(db, "visit_history");
            if (!cols.Contains("searchQuery") || !cols.Contains("visitedAt")) return; // before v0.6.0: no history
            var sql = "SELECT key, searchQuery, visitedAt, " +
                (cols.Contains("visitCount") ? "visitCount" : "1 AS visitCount") + ", " +
                (cols.Contains("searchCategoryId") ? "searchCategoryId" : "NULL AS searchCategoryId") + ", " +
                (cols.Contains("searchBookId") ? "searchBookId" : "NULL AS searchBookId") + ", " +
                (cols.Contains("searchTocId") ? "searchTocId" : "NULL AS searchTocId") + ", " +
                (cols.Contains("searchExtended") ? "searchExtended" : "0 AS searchExtended") +
                " FROM visit_history WHERE kind = 'search' AND searchQuery IS NOT NULL";
            var rows = Sqlite.Query(db, sql, out error);
            if (rows == null) { data.Problems.Add("לא הצלחתי לקרוא את היסטוריית החיפושים של זית: " + error); return; }
            foreach (var r in rows)
            {
                var row = ToSearch(r);
                if (row == null) continue;
                SearchRow prev;
                // The same search in two copies of Zayit's data (an installed and a Store copy): the latest stands
                if (!byKey.TryGetValue(row.Key, out prev) || row.LastAt > prev.LastAt) byKey[row.Key] = row;
            }
        }
    }

    internal static SearchRow ToSearch(Dictionary<string, object> r)
    {
        var query = Clean(r["searchQuery"] as string);
        var at = Long(r["visitedAt"]);
        if (query == null || at == null || at.Value <= 0) return null;
        var row = new SearchRow
        {
            Source = Source.Zayit,
            Query = query,
            CategoryId = Long(r["searchCategoryId"]),
            BookId = Long(r["searchBookId"]),
            TocId = Long(r["searchTocId"]),
            Extended = (Long(r["searchExtended"]) ?? 0) != 0,
            Count = Math.Max(1, Long(r["visitCount"]) ?? 1),
            LastAt = at.Value
        };
        // An author's scope lives only in the key (search:a<id>[x]:<query>)
        var key = r.ContainsKey("key") ? r["key"] as string : null;
        if (key != null && key.StartsWith("search:a", StringComparison.Ordinal))
        {
            var scope = key.Substring("search:".Length);
            int colon = scope.IndexOf(':');
            if (colon > 1) row.Scope = "מחבר " + scope.Substring(1, colon - 1).TrimEnd('x');
        }
        return row;
    }

    static long? Long(object v)
    {
        if (v is long) return (long)v;
        if (v is double) return (long)(double)v;
        return null;
    }

    // The words as the app trimmed them; spaces inside are kept, since they are part of the search
    static string Clean(string s)
    {
        if (s == null) return null;
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    // The library's version: the first line of release_info.txt (a yyyyMMddHHmmss stamp) beside seforim.db.
    static string LibraryVersion(string folder)
    {
        try
        {
            var f = Path.Combine(folder, "release_info.txt");
            if (!File.Exists(f)) return null;
            var v = File.ReadAllText(f).Trim();
            int nl = v.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) v = v.Substring(0, nl).Trim();
            return v.Length > 0 && v.Length <= Limits.MaxVersion ? v : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
