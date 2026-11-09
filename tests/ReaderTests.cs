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
        var data = new ZayitReader(new[] { db }, new[] { Path.Combine(dir, "nowhere"), lib }).Read();
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
        var both = new ZayitReader(new[] { db, store }, new string[0]).Read();
        var merged = both.Searches.FindAll(s => s.Query == "חזקת הבתים");
        Check(merged.Count == 1 && merged[0].Count == 4 && both.Places.Count == 2, "zayit: two copies, the latest of each search");

        // Pardes's folder is never read; a database before v0.6.0 has no history
        var pardes = Path.Combine(dir, "io.github.efrul.pardes", "user_settings.db");
        Directory.CreateDirectory(Path.GetDirectoryName(pardes));
        File.Copy(db, pardes);
        Check(!new ZayitReader(new[] { pardes }, new string[0]).Read().Found, "zayit: never reads Pardes's folder");
        var old = Path.Combine(dir, "old.db");
        Sqlite.Exec(old, "CREATE TABLE highlight (id INTEGER)");
        var oldData = new ZayitReader(new[] { old }, new string[0]).Read();
        Check(oldData.Found && oldData.Searches.Count == 0 && oldData.Problems.Count == 0, "zayit: an old database without history is found, with nothing to send");
        Check(!new ZayitReader(new[] { Path.Combine(dir, "missing.db") }, new string[0]).Read().Found, "zayit: not found when nothing is there");

        // Zayit's database_path preference, as the JDK writes it in the registry
        Check(JavaPrefs.Decode("/C:///Users//x///Seforim//seforim.db") == "C:\\Users\\x\\Seforim\\seforim.db", "zayit: a Java preference's path decoded",
            JavaPrefs.Decode("/C:///Users//x///Seforim//seforim.db"));
        Check(JavaPrefs.Decode("/u05d6/u05d9/u05ea") == "\u05d6\u05d9\u05ea" && JavaPrefs.Decode("/D/A/R/K") == "DARK" && JavaPrefs.Decode("/!abc") == null,
            "zayit: Hebrew and capitals in a Java preference");

        // The extra folder the user adds may be Zayit's folder itself
        var viaFolder = ZayitReader.ForThisComputer(new[] { Path.Combine(dir, "zayit") }, null).Read();
        Check(viaFolder.Found && viaFolder.Searches.Count >= 4, "zayit: found through a folder the user added");
    }
}
