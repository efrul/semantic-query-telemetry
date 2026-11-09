using System;
using System.Collections.Generic;
using System.IO;

// The readers' tests, on made-up data in each app's real format.
public static partial class CoreTests
{
    static List<KeyValuePair<string, Action<string>>> ReaderTests()
    {
        var list = new List<KeyValuePair<string, Action<string>>>
        {
            new KeyValuePair<string, Action<string>>("zayit", TestZayit),
            new KeyValuePair<string, Action<string>>("pardes", TestPardes),
        };
        list.AddRange(OtzariaTests());
        return list;
    }

    // Zayit's visit_history as VisitHistory.sq makes it (v0.6.0 and later)
    const string ZayitSchema =
        "CREATE TABLE visit_history (key TEXT NOT NULL PRIMARY KEY, kind TEXT NOT NULL, bookId INTEGER, searchQuery TEXT, title TEXT NOT NULL, " +
        "visitedAt INTEGER NOT NULL, visitCount INTEGER NOT NULL DEFAULT 1, tocEntryId INTEGER, lineId INTEGER, searchCategoryId INTEGER, " +
        "searchBookId INTEGER, searchTocId INTEGER, searchExtended INTEGER NOT NULL DEFAULT 0);\n" +
        "CREATE INDEX visit_history_visitedAt ON visit_history(visitedAt)";

    static string ZayitDb(string dir, string name, string inserts)
    {
        var folder = Path.Combine(dir, name, "databases", "settings");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "user_settings.db");
        var err = Sqlite.Exec(path, ZayitSchema + ";\n" + inserts);
        if (err != null) throw new InvalidOperationException(err);
        return path;
    }

    static void TestZayit(string dir)
    {
        var db = ZayitDb(dir, "zayit",
            "INSERT INTO visit_history VALUES ('search:g:חזקת הבתים', 'search', NULL, 'חזקת הבתים', 'חזקת הבתים', 1793800000000, 3, NULL, NULL, NULL, NULL, NULL, 0);\n" +
            "INSERT INTO visit_history VALUES ('search:b72x:שלוש שנים', 'search', NULL, 'שלוש שנים', 'שלוש שנים — בבא בתרא', 1793800001000, 1, NULL, NULL, NULL, 72, NULL, 1);\n" +
            "INSERT INTO visit_history VALUES ('search:t9:a:b', 'search', NULL, 'a:b', 'a:b', 1793800002000, 2, NULL, NULL, NULL, 72, 9, 0);\n" +
            "INSERT INTO visit_history VALUES ('search:a12x:רש\"י', 'search', NULL, 'רש\"י', 'רש\"י', 1793800003000, 1, NULL, NULL, NULL, NULL, NULL, 1);\n" +
            "INSERT INTO visit_history VALUES ('book:72', 'book', 72, NULL, 'בבא בתרא', 1793800004000, 5, NULL, 100, NULL, NULL, NULL, 0);\n" +
            "INSERT INTO visit_history VALUES ('search:g:   ', 'search', NULL, '   ', 'x', 1793800005000, 1, NULL, NULL, NULL, NULL, NULL, 0)");
        var lib = Path.Combine(dir, "library");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "release_info.txt"), "20261012193827\n");
        var data = new SeforimAppReader(Source.Zayit, new[] { db }, new[] { Path.Combine(dir, "nowhere"), lib }).Read();
        Check(data.Found && data.Problems.Count == 0, "zayit: found", string.Join("; ", data.Problems.ToArray()));
        Check(data.Searches.Count == 4, "zayit: only searches, not book visits or empty words", data.Searches.Count + " rows");
        var first = data.Searches.Find(s => s.Query == "חזקת הבתים");
        Check(first != null && first.Count == 3 && first.LastAt == 1793800000000L && first.BookId == null && !first.Extended && first.Source == Source.Zayit,
            "zayit: a search's words, count, time and scope");
        var inBook = data.Searches.Find(s => s.Query == "שלוש שנים");
        Check(inBook != null && inBook.BookId == 72 && inBook.Extended, "zayit: a search in a book, extended");
        var colon = data.Searches.Find(s => s.Query == "a:b");
        Check(colon != null && colon.TocId == 9, "zayit: words with ':' from searchQuery, not from the key");
        var author = data.Searches.Find(s => s.Query == "רש\"י");
        Check(author != null && author.Scope == "מחבר 12", "zayit: an author's scope, from the key");
        Check(data.Library == "20261012193827", "zayit: the library's version");

        // A second copy (the Store's) with the same search made later: the latest stands
        var store = ZayitDb(dir, "store",
            "INSERT INTO visit_history VALUES ('search:g:חזקת הבתים', 'search', NULL, 'חזקת הבתים', 'חזקת הבתים', 1793900000000, 4, NULL, NULL, NULL, NULL, NULL, 0)");
        var both = new SeforimAppReader(Source.Zayit, new[] { db, store }, new string[0]).Read();
        var merged = both.Searches.FindAll(s => s.Query == "חזקת הבתים");
        Check(merged.Count == 1 && merged[0].Count == 4 && both.Places.Count == 2, "zayit: two copies, the latest of each search");

        // Pardes's folder is never read; a database before v0.6.0 has no history
        var pardes = Path.Combine(dir, "io.github.efrul.pardes", "user_settings.db");
        Directory.CreateDirectory(Path.GetDirectoryName(pardes));
        File.Copy(db, pardes);
        Check(!new SeforimAppReader(Source.Zayit, new[] { pardes }, new string[0]).Read().Found, "zayit: never reads Pardes's folder as Zayit's");
        var old = Path.Combine(dir, "old.db");
        Sqlite.Exec(old, "CREATE TABLE highlight (id INTEGER)");
        var oldData = new SeforimAppReader(Source.Zayit, new[] { old }, new string[0]).Read();
        Check(oldData.Found && oldData.Searches.Count == 0 && oldData.Problems.Count == 0, "zayit: an old database without history is found, with nothing to send");
        Check(!new SeforimAppReader(Source.Zayit, new[] { Path.Combine(dir, "missing.db") }, new string[0]).Read().Found, "zayit: not found when nothing is there");

        // Zayit's database_path preference, as the JDK writes it in the registry
        Check(JavaPrefs.Decode("/C:///Users//x///Seforim//seforim.db") == "C:\\Users\\x\\Seforim\\seforim.db", "zayit: a Java preference's path decoded",
            JavaPrefs.Decode("/C:///Users//x///Seforim//seforim.db"));
        Check(JavaPrefs.Decode("/u05d6/u05d9/u05ea") == "\u05d6\u05d9\u05ea" && JavaPrefs.Decode("/D/A/R/K") == "DARK" && JavaPrefs.Decode("/!abc") == null,
            "zayit: Hebrew and capitals in a Java preference");

        // The extra folder the user adds may be Zayit's folder itself
        var viaFolder = SeforimAppReader.ForZayit(new[] { Path.Combine(dir, "zayit") }, null).Read();
        Check(viaFolder.Found && viaFolder.Searches.Count >= 4, "zayit: found through a folder the user added");
    }

    // Pardes: Zayit's visit_history, and its own search_feedback (SearchFeedback.sq) and daf review files (DafReview.kt)
    const string PardesGrades =
        "CREATE TABLE search_feedback (searchKey TEXT NOT NULL, query TEXT NOT NULL, lineId INTEGER NOT NULL, bookId INTEGER NOT NULL, " +
        "grade INTEGER NOT NULL, rank INTEGER NOT NULL, searchCategoryId INTEGER, searchBookId INTEGER, searchTocId INTEGER, " +
        "searchExtended INTEGER NOT NULL DEFAULT 0, model TEXT, gradedAt INTEGER NOT NULL, PRIMARY KEY (searchKey, lineId))";

    static void TestPardes(string dir)
    {
        var home = Path.Combine(dir, SeforimAppReader.PardesId);
        var db = ZayitDb(home, "", 
            "INSERT INTO visit_history VALUES ('search:g:חזקת הבתים', 'search', NULL, 'חזקת הבתים', 'חזקת הבתים', 1793800000000, 2, NULL, NULL, NULL, NULL, NULL, 0);\n" +
            PardesGrades + ";\n" +
            "INSERT INTO search_feedback VALUES ('search:g:חזקת הבתים', 'חזקת הבתים', 26579, 72, 3, 2, NULL, NULL, NULL, 0, 'student@695cbe2/pardes-clean-1', 1793800000002);\n" +
            "INSERT INTO search_feedback VALUES ('search:b72:שלוש שנים', 'שלוש שנים', 100, 72, 0, 0, NULL, 72, NULL, 1, NULL, 1793800000003);\n" +
            "INSERT INTO search_feedback VALUES ('search:g:x', 'x', 1, 1, 1, 0, NULL, NULL, NULL, 0, NULL, 1793800000004)");
        var lib = Path.Combine(dir, "pardes-library");
        var daf = Path.Combine(lib, "pardes-daf", "feedback");
        Directory.CreateDirectory(daf);
        File.WriteAllText(Path.Combine(daf, "Berakhot.json"), "{\"format\":1,\"tractate\":\"Berakhot\",\"tractateHe\":\"ברכות\",\"library\":\"20261012193827\"," +
            "\"marks\":[{\"amud\":\"2a\",\"c\":\"g\",\"i\":1,\"t\":\"מאימתי\",\"ok\":true,\"cut\":0,\"at\":\"2026-11-01T09:00:00Z\"}," +
            "{\"amud\":\"2a\",\"c\":\"r\",\"i\":3,\"t\":\"עד\",\"cut\":1,\"at\":\"2026-11-01T09:01:00Z\"}," +
            "{\"amud\":\"2a\",\"c\":\"g\",\"i\":2,\"t\":\"x\",\"cut\":0,\"at\":\"2026-11-01T09:02:00Z\"}]}");
        File.WriteAllText(Path.Combine(daf, "Shabbat.unreadable-1793800000000.json"), "{not json");
        File.WriteAllText(Path.Combine(lib, "release_info.txt"), "20261012193827");
        var data = new SeforimAppReader(Source.Pardes, new[] { db }, new[] { lib }).Read();
        Check(data.Found && data.Problems.Count == 0 && data.Searches.Count == 1 && data.Searches[0].Source == Source.Pardes, "pardes: its searches, as Pardes's",
            string.Join("; ", data.Problems.ToArray()));
        Check(data.Grades.Count == 3, "pardes: its grades", data.Grades.Count + " grades");
        var exact = data.Grades.Find(g => g.LineId == 26579);
        Check(exact != null && exact.Grade == GradeRow.Exact && exact.Rank == 2 && exact.BookId == 72 && exact.Search == "search:g:חזקת הבתים" &&
            exact.Model == "student@695cbe2/pardes-clean-1" && exact.GradedAt == 1793800000002L && exact.Valid(), "pardes: a grade as Pardes keeps it");
        var inBook = data.Grades.Find(g => g.LineId == 100);
        Check(inBook != null && inBook.ScopeBookId == 72 && inBook.Extended && inBook.Grade == GradeRow.NotRelevant, "pardes: a grade's scope");
        Check(!data.Grades.Find(g => g.Query == "x").Valid(), "pardes: a grade off the scale is not valid, so not sent");
        Check(data.Marks.Count == 2 && data.Marks.TrueForAll(m => m.Valid() && m.TractateHe == "ברכות"), "pardes: its daf marks, not one taken back or an unreadable file",
            data.Marks.Count + " marks");
        Check(data.Library == "20261012193827", "pardes: the library's version");
        Check(!new SeforimAppReader(Source.Pardes, new[] { Path.Combine(dir, "zayit-like", "user_settings.db") }, new[] { lib }).Read().Found,
            "pardes: reads only Pardes's own folder");
        var st = new Settings(new MemoryStore());
        var sel = Engine.Select(new[] { data }, st, new Ledger(null));
        Check(sel.Grades.Count == 2 && sel.Marks.Count == 2 && sel.Searches.Count == 1, "pardes: what would be sent");
    }
}
