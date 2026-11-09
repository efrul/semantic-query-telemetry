using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// What the tool sends: rows of package format 1 (Pardes's FeedbackPackage.kt, checked by the service's package.js), each
// with a Key (which search, grade or mark it is) and a Fingerprint (that, as it stands now). A row is sent again only
// when its fingerprint changes: a search made again (a new count or time) or a grade changed.

public static class AppInfo
{
    public const string Version = "2.0.0";
    public const string Name = "שליחת נתוני חיפוש";
    public const string AppVersion = "search-data-sender/" + Version;
    public const string UserAgent = "SearchDataSender/" + Version;
    public const string DefaultServiceUrl = "https://pardes-feedback.pardes.workers.dev";
    public const string SiteUrl = "https://pardes-feedback.pardes.workers.dev/";
}

// The service's limits (feedback-service/src/package.js); a row past them would make it refuse the whole package.
public static class Limits
{
    public const int MaxQuery = 20000;
    public const int MaxSearchKey = MaxQuery + 1000;
    public const int MaxModel = 200;
    public const int MaxScope = 500;
    public const int MaxTitle = 300;
    public const int MaxTractate = 100;
    public const int MaxShort = 16;
    public const int MaxMarkText = 2000;
    public const int MaxVersion = 64;
    public const int PartBytes = 900000; // a part at most, as the apps split theirs; the service takes 1,000,000
}

public enum Source { Zayit, Pardes, Otzaria }

public static class Sources
{
    public static string Id(Source s) { return s == Source.Zayit ? "zayit" : s == Source.Pardes ? "pardes" : "otzaria"; }
    public static string Name(Source s) { return s == Source.Zayit ? "זית" : s == Source.Pardes ? "פרדס" : "אוצריא"; }
}

public sealed class SearchRow
{
    public Source Source;
    public string Query;
    public long? CategoryId, BookId, TocId; // Zayit's scope: the library's ids
    public bool Extended;
    public string Scope;                    // Otzaria's scope: its filter, as text
    public string Ref;                      // Otzaria's title for it, with its options, when it differs from the words
    public long Count = 1;
    public long LastAt;                     // epoch ms of the last time it was searched
    // The app keeps no time for it (Otzaria's history): LastAt is when this tool first found it, an upper bound, and
    // finding it again later is not a new search
    public bool TimeUnknown;

    public string Key
    {
        get { return Join("search", Sources.Id(Source), Query, CategoryId, BookId, TocId, Extended, Scope, Ref); }
    }

    public string Fingerprint { get { return TimeUnknown ? Join(Key, Count) : Join(Key, Count, LastAt); } }

    public string ScopeLabel()
    {
        string where;
        if (!string.IsNullOrEmpty(Scope)) where = Scope;
        else if (TocId.HasValue) where = "פרק " + TocId.Value;
        else if (BookId.HasValue) where = "ספר " + BookId.Value;
        else if (CategoryId.HasValue) where = "קטגוריה " + CategoryId.Value;
        else where = "כל הספרייה";
        return Extended ? where + ", מורחב" : where;
    }

    public bool Valid()
    {
        return Text(Query, Limits.MaxQuery) && LastAt >= 0 && Count >= 0 && (Scope == null || Scope.Length <= Limits.MaxScope) &&
            (Ref == null || Ref.Length <= Limits.MaxQuery);
    }

    public JsonMap ToJson()
    {
        return new JsonMap()
            .Add("query", Query)
            .Add("categoryId", CategoryId)
            .Add("bookId", BookId)
            .Add("tocId", TocId)
            .Add("extended", Extended)
            .Add("scope", Scope)
            .Add("ref", Ref)
            .Add("source", Sources.Id(Source))
            .Add("count", Count)
            .Add("lastAt", LastAt)
            .Add("timeUnknown", TimeUnknown ? (object)true : null);
    }

    internal static string Join(params object[] parts)
    {
        var b = new StringBuilder();
        foreach (var p in parts)
        {
            if (b.Length > 0) b.Append('\u001f');
            if (p == null) continue;
            if (p is bool) b.Append((bool)p ? "1" : "0");
            else if (p is long) b.Append(((long)p).ToString(CultureInfo.InvariantCulture));
            else if (p is int) b.Append(((int)p).ToString(CultureInfo.InvariantCulture));
            else b.Append(p);
        }
        return b.ToString();
    }

    internal static bool Text(string s, int max) { return !string.IsNullOrEmpty(s) && s.Length <= max; }
}

public sealed class GradeRow
{
    public const int Exact = 3, Relevant = 2, NotRelevant = 0;

    public Source Source;
    public string Query;      // the words searched
    public string Search;     // the search the result belongs to, as the app keys it
    public long LineId;       // the library's line (Zayit); 0 when the app has no such id
    public long BookId;       // the library's book (Zayit); 0 when the app has no such id
    public int Grade;
    public long Rank;         // the result's place in the list, from 0
    public long? CategoryId, ScopeBookId, TocId;
    public bool Extended;
    public string Model;      // the search model that found the result, when the app says
    public string Scope;      // Otzaria's filter, as text
    public string BookTitle;  // Otzaria's result: its book
    public string HeRef;      // Otzaria's result: its place, as a Hebrew reference
    public string DocId;      // Otzaria's result: its own id, kept apart from Zayit's line ids
    public long GradedAt;     // epoch ms

    public string Key
    {
        get { return SearchRow.Join("grade", Sources.Id(Source), Search, LineId, BookId, HeRef, DocId); }
    }

    public string Fingerprint { get { return SearchRow.Join(Key, Grade, GradedAt); } }

    public string GradeLabel()
    {
        if (Grade == Exact) return "בדיוק זה";
        if (Grade == Relevant) return "רלוונטי";
        return "לא רלוונטי";
    }

    public string ResultLabel()
    {
        if (!string.IsNullOrEmpty(HeRef)) return HeRef;
        if (!string.IsNullOrEmpty(BookTitle)) return BookTitle;
        return "ספר " + BookId + ", שורה " + LineId;
    }

    public bool Valid()
    {
        return SearchRow.Text(Query, Limits.MaxQuery) && SearchRow.Text(Search, Limits.MaxSearchKey) && LineId >= 0 && BookId >= 0 &&
            (Grade == Exact || Grade == Relevant || Grade == NotRelevant) && Rank >= 0 && GradedAt >= 0 &&
            (Model == null || Model.Length <= Limits.MaxModel);
    }

    public JsonMap ToJson()
    {
        return new JsonMap()
            .Add("query", Query)
            .Add("search", Search)
            .Add("lineId", LineId)
            .Add("bookId", BookId)
            .Add("grade", (long)Grade)
            .Add("rank", Rank)
            .Add("categoryId", CategoryId)
            .Add("scopeBookId", ScopeBookId)
            .Add("tocId", TocId)
            .Add("extended", Extended)
            .Add("model", Model)
            .Add("source", Sources.Id(Source))
            .Add("scope", Scope)
            .Add("bookTitle", BookTitle)
            .Add("heRef", HeRef)
            .Add("docId", DocId)
            .Add("gradedAt", GradedAt);
    }
}

// A line of the daf view checked in Pardes's daf review (<library>\pardes-daf\feedback\<tractate>.json).
public sealed class DafMark
{
    public string Tractate, TractateHe, Library;
    public string Amud, C;
    public long I;
    public string T = "";
    public bool? Ok;
    public long Cut;
    public string At;  // as the app wrote it
    public long AtMs;

    public string Key { get { return SearchRow.Join("daf", Tractate, Amud, C, I); } }
    public string Fingerprint { get { return SearchRow.Join(Key, Ok, Cut, At, T); } }

    public bool Valid()
    {
        return SearchRow.Text(Tractate, Limits.MaxTractate) && SearchRow.Text(Amud, Limits.MaxShort) && SearchRow.Text(C, Limits.MaxShort) &&
            I >= 0 && T != null && T.Length <= Limits.MaxMarkText && (At == null || At.Length <= Limits.MaxVersion) &&
            (TractateHe == null || TractateHe.Length <= Limits.MaxTractate) && (Library == null || Library.Length <= Limits.MaxVersion);
    }

    public JsonMap ToJson()
    {
        return new JsonMap().Add("amud", Amud).Add("c", C).Add("i", I).Add("t", T ?? "").Add("ok", Ok).Add("cut", Cut).Add("at", At);
    }
}

// One send: package format 1 of Pardes's feedback service, with "publish": the sender agreed that what it holds is shown
// to everyone on the open search site.
public sealed class Package
{
    public string Sender;
    public string SentAt;
    public string Library;
    public readonly List<SearchRow> Searches = new List<SearchRow>();
    public readonly List<GradeRow> Grades = new List<GradeRow>();
    public readonly List<DafMark> Marks = new List<DafMark>();

    public bool IsEmpty { get { return Searches.Count == 0 && Grades.Count == 0 && Marks.Count == 0; } }
    public int Rows { get { return Searches.Count + Grades.Count + Marks.Count; } }

    public JsonMap ToJson()
    {
        var searches = new List<object>();
        foreach (var s in Searches) searches.Add(s.ToJson());
        var grades = new List<object>();
        foreach (var g in Grades) grades.Add(g.ToJson());
        // The marks, a review a tractate, in the order they came
        var daf = new List<object>();
        var byTractate = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        foreach (var m in Marks)
        {
            List<object> marks;
            if (!byTractate.TryGetValue(m.Tractate, out marks))
            {
                marks = new List<object>();
                byTractate[m.Tractate] = marks;
                daf.Add(new JsonMap().Add("format", 1L).Add("tractate", m.Tractate).Add("tractateHe", m.TractateHe)
                    .Add("library", m.Library).Add("marks", marks));
            }
            marks.Add(m.ToJson());
        }
        return new JsonMap()
            .Add("format", 1L)
            .Add("app", "Pardes")
            .Add("appVersion", AppInfo.AppVersion)
            .Add("sender", Sender)
            .Add("sentAt", SentAt)
            .Add("library", Library)
            .Add("publish", true)
            .Add("grades", grades)
            .Add("searches", searches)
            .Add("daf", daf);
    }

    public string ToCompactJson() { return Json.Write(ToJson(), false); }

    public int Bytes { get { return Encoding.UTF8.GetByteCount(ToCompactJson()); } }

    Package Empty()
    {
        return new Package { Sender = Sender, SentAt = SentAt, Library = Library };
    }

    // This package in parts of at most [maxBytes], as Pardes's FeedbackPackage.parts: whole when it fits, else its
    // grades, searches and marks halved until each part fits. A single row too big alone stays a part of its own.
    public List<Package> Parts(int maxBytes)
    {
        var parts = new List<Package>();
        if (IsEmpty) return parts;
        if (Bytes <= maxBytes) { parts.Add(this); return parts; }
        Split(Grades, maxBytes, parts, (p, rows) => p.Grades.AddRange(rows));
        Split(Searches, maxBytes, parts, (p, rows) => p.Searches.AddRange(rows));
        Split(Marks, maxBytes, parts, (p, rows) => p.Marks.AddRange(rows));
        return parts;
    }

    void Split<T>(List<T> rows, int maxBytes, List<Package> parts, Action<Package, List<T>> fill)
    {
        if (rows.Count == 0) return;
        var one = Empty();
        fill(one, rows);
        if (rows.Count == 1 || one.Bytes <= maxBytes) { parts.Add(one); return; }
        int mid = rows.Count / 2;
        Split(rows.GetRange(0, mid), maxBytes, parts, fill);
        Split(rows.GetRange(mid, rows.Count - mid), maxBytes, parts, fill);
    }

    // Writes this package, indented, to a new file in [folder]: search-data-<sender8>-<time>[-<n>].json, through a
    // temporary file so a crash never leaves half a package. Returns the file's path.
    public string WriteTo(string folder, int index, int count)
    {
        Directory.CreateDirectory(folder);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var who = Sender != null && Sender.Length >= 8 ? Sender.Substring(0, 8) : "sender";
        var name = string.Format(CultureInfo.InvariantCulture, "search-data-{0}-{1}{2}.json", who, stamp,
            count > 1 ? "-" + (index + 1) : "");
        var file = Path.Combine(folder, name);
        var tmp = file + ".part";
        File.WriteAllText(tmp, Json.Write(ToJson(), true) + "\n", new UTF8Encoding(false));
        if (File.Exists(file)) File.Delete(file);
        File.Move(tmp, file);
        return file;
    }
}
