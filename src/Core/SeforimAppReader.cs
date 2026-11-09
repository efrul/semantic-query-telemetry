using System;
using System.Collections.Generic;
using System.IO;

// Zayit's and Pardes's data, as the apps themselves keep it: Pardes is built on Zayit (kdroidFilter/Zayit) and keeps the
// same files, in a data folder of its own, with two kinds Zayit does not have.
//
// Searches, both apps (v0.6.0 and later): %APPDATA%\<app id>\databases\settings\user_settings.db, table visit_history
// (VisitHistory.sq; FileKit's filesDir is %APPDATA%\<app id>). A row of kind 'search' is one search: its words
// (searchQuery), its scope (in its key: search:<scope>:<query>, the scope g, b<book>, t<toc> or a<author>, with x when
// extended), how many times it was made (visitCount, one row a search, counted up) and when last (visitedAt, epoch ms).
// A Store install of Zayit keeps the same folders under LocalCache\Roaming of its package.
//
// Grades, Pardes only: the same database, table search_feedback (SearchFeedback.sq): a row a graded result, keyed by the
// search's key and the result's line, with its grade (3, 2 or 0), rank from 0, scope, model and gradedAt (epoch ms).
//
// Daf marks, Pardes only: <library>\pardes-daf\feedback\<tractate>.json (DafReview.kt): format 1, the tractate, and its
// marks, a line of the daf each (amud, column, index, words, ok, cut, at).
//
// The library (seforim.db's folder, for the daf marks and release_info.txt, its version) is the app's database_path
// preference when set, else <data folder>\databases.
public sealed class SeforimAppReader : IReader
{
    public const string ZayitId = "io.github.kdroidfilter.seforimapp";
    public const string PardesId = "io.github.efrul.pardes";
    static readonly string[] DafFolders = { Path.Combine("pardes-daf", "feedback"), Path.Combine("daf-layouts", "feedback") };

    readonly Source source;
    readonly List<string> databases = new List<string>();
    readonly List<string> libraryFolders = new List<string>();

    // [databases]: user_settings.db files to read; [libraryFolders]: where seforim.db may be, for its daf marks and version.
    public SeforimAppReader(Source source, IEnumerable<string> databases, IEnumerable<string> libraryFolders)
    {
        this.source = source;
        foreach (var d in databases) AddDatabase(d);
        foreach (var l in libraryFolders)
            if (!string.IsNullOrEmpty(l) && !this.libraryFolders.Exists(x => string.Equals(x, l, StringComparison.OrdinalIgnoreCase)))
                this.libraryFolders.Add(l);
    }

    // Zayit's places on this computer, and the folders the user added. [databasePath]: Zayit's database_path preference.
    public static SeforimAppReader ForZayit(IEnumerable<string> extraFolders, string databasePath)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbs = new List<string>();
        var libs = new List<string>();
        AddLibrary(libs, Environment.GetEnvironmentVariable("SEFORIMAPP_DATABASE_PATH"));
        AddLibrary(libs, databasePath);
        AddHome(Path.Combine(appData, ZayitId), dbs, libs);
        try
        {
            var packages = Path.Combine(local, "Packages");
            if (Directory.Exists(packages))
                foreach (var p in Directory.GetDirectories(packages, "KdroidFilter.Zayit_*"))
                    AddHome(Path.Combine(p, "LocalCache", "Roaming", ZayitId), dbs, libs);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        foreach (var f in extraFolders) AddExtra(f, dbs, libs);
        return new SeforimAppReader(Source.Zayit, dbs, libs);
    }

    // Pardes's places on this computer. [databasePath]: Pardes's database_path preference (its own preferences node).
    public static SeforimAppReader ForPardes(IEnumerable<string> extraFolders, string databasePath)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dbs = new List<string>();
        var libs = new List<string>();
        AddLibrary(libs, databasePath);
        AddHome(Path.Combine(appData, PardesId), dbs, libs);
        foreach (var f in extraFolders) AddExtra(f, dbs, libs);
        return new SeforimAppReader(Source.Pardes, dbs, libs);
    }

    static void AddHome(string home, List<string> dbs, List<string> libs)
    {
        dbs.Add(Path.Combine(home, "databases", "settings", "user_settings.db"));
        libs.Add(Path.Combine(home, "databases"));
    }

    static void AddExtra(string folder, List<string> dbs, List<string> libs)
    {
        foreach (var rel in new[] { "user_settings.db", Path.Combine("settings", "user_settings.db"),
            Path.Combine("databases", "settings", "user_settings.db"), Path.Combine(ZayitId, "databases", "settings", "user_settings.db"),
            Path.Combine(PardesId, "databases", "settings", "user_settings.db") })
            dbs.Add(Path.Combine(folder, rel));
        libs.Add(folder);
        libs.Add(Path.Combine(folder, "databases"));
    }

    static void AddLibrary(List<string> libs, string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        libs.Add(path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path) : path);
    }

    // Each app reads only its own data folder: the other's is read by its own reader, under its own name
    void AddDatabase(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        bool pardes = path.IndexOf(PardesId, StringComparison.OrdinalIgnoreCase) >= 0;
        bool zayit = path.IndexOf(ZayitId, StringComparison.OrdinalIgnoreCase) >= 0;
        if (source == Source.Zayit && pardes) return;
        if (source == Source.Pardes && (zayit || !pardes)) return;
        foreach (var d in databases) if (string.Equals(d, path, StringComparison.OrdinalIgnoreCase)) return;
        databases.Add(path);
    }

    public SourceData Read()
    {
        var data = new SourceData { Source = source };
        var searches = new Dictionary<string, SearchRow>(StringComparer.Ordinal);
        var grades = new Dictionary<string, GradeRow>(StringComparer.Ordinal);
        foreach (var db in databases)
        {
            if (!File.Exists(db)) continue;
            data.Found = true;
            data.AddPlace(Path.GetDirectoryName(db));
            ReadDatabase(db, searches, grades, data);
        }
        data.Searches.AddRange(searches.Values);
        data.Grades.AddRange(grades.Values);
        // The daf marks and the version belong to a library this app uses; only read once the app was found
        if (data.Found)
            foreach (var lib in libraryFolders)
            {
                if (data.Library == null) data.Library = LibraryVersion(lib);
                if (source == Source.Pardes) ReadDaf(lib, data);
            }
        return data;
    }

    void ReadDatabase(string path, Dictionary<string, SearchRow> searches, Dictionary<string, GradeRow> grades, SourceData data)
    {
        string error;
        using (var db = Sqlite.Open(path, out error))
        {
            if (db == null) { data.Problems.Add("לא הצלחתי לקרוא את " + path + ": " + error); return; }
            var cols = Sqlite.Columns(db, "visit_history");
            if (cols.Contains("searchQuery") && cols.Contains("visitedAt"))
            {
                var sql = "SELECT key, searchQuery, visitedAt, " +
                    Column(cols, "visitCount", "1") + ", " + Column(cols, "searchCategoryId", "NULL") + ", " +
                    Column(cols, "searchBookId", "NULL") + ", " + Column(cols, "searchTocId", "NULL") + ", " +
                    Column(cols, "searchExtended", "0") +
                    " FROM visit_history WHERE kind = 'search' AND searchQuery IS NOT NULL";
                var rows = Sqlite.Query(db, sql, out error);
                if (rows == null) data.Problems.Add("לא הצלחתי לקרוא את היסטוריית החיפושים של " + Sources.Name(source) + ": " + error);
                else foreach (var r in rows)
                {
                    var row = ToSearch(r, source);
                    SearchRow prev;
                    // The same search in two copies of the data (an installed and a Store copy): the latest stands
                    if (row != null && (!searches.TryGetValue(row.Key, out prev) || row.LastAt > prev.LastAt)) searches[row.Key] = row;
                }
            }
            var g = Sqlite.Columns(db, "search_feedback");
            if (g.Contains("searchKey") && g.Contains("gradedAt"))
            {
                var rows = Sqlite.Query(db, "SELECT * FROM search_feedback", out error);
                if (rows == null) data.Problems.Add("לא הצלחתי לקרוא את הדירוגים של " + Sources.Name(source) + ": " + error);
                else foreach (var r in rows)
                {
                    var row = ToGrade(r, source);
                    GradeRow prev;
                    if (row != null && (!grades.TryGetValue(row.Key, out prev) || row.GradedAt > prev.GradedAt)) grades[row.Key] = row;
                }
            }
        }
    }

    static string Column(HashSet<string> cols, string name, string otherwise)
    {
        return cols.Contains(name) ? name : otherwise + " AS " + name;
    }

    internal static SearchRow ToSearch(Dictionary<string, object> r, Source source)
    {
        var query = Clean(r["searchQuery"] as string);
        var at = Long(r["visitedAt"]);
        if (query == null || at == null || at.Value <= 0) return null;
        var row = new SearchRow
        {
            Source = source,
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

    internal static GradeRow ToGrade(Dictionary<string, object> r, Source source)
    {
        var query = Clean(Get(r, "query") as string);
        var search = Get(r, "searchKey") as string;
        var line = Long(Get(r, "lineId"));
        var book = Long(Get(r, "bookId"));
        var grade = Long(Get(r, "grade"));
        var at = Long(Get(r, "gradedAt"));
        if (query == null || string.IsNullOrEmpty(search) || line == null || book == null || grade == null || at == null) return null;
        return new GradeRow
        {
            Source = source,
            Query = query,
            Search = search,
            LineId = line.Value,
            BookId = book.Value,
            Grade = (int)grade.Value,
            Rank = Long(Get(r, "rank")) ?? 0,
            CategoryId = Long(Get(r, "searchCategoryId")),
            ScopeBookId = Long(Get(r, "searchBookId")),
            TocId = Long(Get(r, "searchTocId")),
            Extended = (Long(Get(r, "searchExtended")) ?? 0) != 0,
            Model = Get(r, "model") as string,
            GradedAt = at.Value
        };
    }

    static object Get(Dictionary<string, object> r, string name)
    {
        object v;
        return r.TryGetValue(name, out v) ? v : null;
    }

    // Pardes's daf review files in library [folder]: each tractate's marks, as the app wrote them.
    static void ReadDaf(string folder, SourceData data)
    {
        foreach (var rel in DafFolders)
        {
            var dir = Path.Combine(folder, rel);
            string[] files;
            try
            {
                if (!Directory.Exists(dir)) continue;
                files = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                Dictionary<string, object> review;
                try { review = Json.TryParseObject(File.ReadAllText(file)); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (review == null || (Json.Long(review, "format") ?? 1) != 1) continue;
                var tractate = Json.Str(review, "tractate");
                var marks = Json.Arr(review, "marks");
                if (string.IsNullOrEmpty(tractate) || marks == null) continue;
                data.AddPlace(dir);
                foreach (var item in marks)
                {
                    var m = item as Dictionary<string, object>;
                    if (m == null) continue;
                    var mark = new DafMark
                    {
                        Tractate = tractate,
                        TractateHe = Json.Str(review, "tractateHe"),
                        Library = Json.Str(review, "library"),
                        Amud = Json.Str(m, "amud"),
                        C = Json.Str(m, "c"),
                        I = Json.Long(m, "i") ?? -1,
                        T = Json.Str(m, "t") ?? "",
                        Ok = Json.Bool(m, "ok"),
                        Cut = Json.Long(m, "cut") ?? 0,
                        At = Json.Str(m, "at")
                    };
                    // A mark taken back (neither right nor wrong, nothing moved) is not one
                    if (mark.Ok == null && mark.Cut == 0) continue;
                    if (!data.Marks.Exists(x => x.Key == mark.Key)) data.Marks.Add(mark);
                }
            }
        }
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
