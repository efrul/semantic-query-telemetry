using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

class SearchRow
{
    public string Query;
    public long? CategoryId;
    public long? BookId;
    public long? TocId;
    public bool Extended;
    public string Scope;  // Otzaria facet/book path when numeric IDs are not used
    public string Source; // "zayit", "pardes", or "otzaria"
    public long Count = 1;
    public long LastAt;

    public string Key
    {
        get
        {
            return (Source ?? "") + "\u0000" + (Query ?? "") + "\u0000" +
                   (CategoryId.HasValue ? CategoryId.Value.ToString() : "") + "\u0000" +
                   (BookId.HasValue ? BookId.Value.ToString() : "") + "\u0000" +
                   (TocId.HasValue ? TocId.Value.ToString() : "") + "\u0000" +
                   (Scope ?? "") + "\u0000" + (Extended ? "1" : "0");
        }
    }

    public string ScopeLabel()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(Scope)) parts.Add(Scope.TrimStart('/'));
        else if (TocId.HasValue) parts.Add("פרק " + TocId.Value);
        else if (BookId.HasValue) parts.Add("ספר " + BookId.Value);
        else if (CategoryId.HasValue) parts.Add("קטגוריה " + CategoryId.Value);
        else parts.Add("כל הספרייה");
        if (Extended) parts.Add("מורחב");
        return string.Join(" · ", parts.ToArray());
    }

    public Dictionary<string, object> ToJson()
    {
        var d = new Dictionary<string, object>();
        d["query"] = Query;
        if (CategoryId.HasValue) d["categoryId"] = CategoryId.Value;
        if (BookId.HasValue) d["bookId"] = BookId.Value;
        if (TocId.HasValue) d["tocId"] = TocId.Value;
        d["extended"] = Extended;
        if (!string.IsNullOrEmpty(Scope)) d["scope"] = Scope;
        if (!string.IsNullOrEmpty(Source)) d["source"] = Source;
        d["count"] = Count;
        d["lastAt"] = LastAt;
        return d;
    }
}

class GradeRow
{
    public string Query;
    public string Search;
    public long LineId;
    public long BookId;
    public int Grade; // 3 = exact, 2 = relevant, 0 = not relevant
    public long Rank;
    public long? CategoryId;
    public long? ScopeBookId;
    public long? TocId;
    public bool Extended;
    public string Model;
    public string Source;    // "zayit", "pardes", or "otzaria"
    public string Scope;     // Otzaria facet path
    public string BookTitle; // Otzaria book title
    public string HeRef;     // Otzaria reference
    public long GradedAt;

    public Dictionary<string, object> ToJson()
    {
        var d = new Dictionary<string, object>();
        d["query"] = Query;
        d["search"] = Search;
        d["lineId"] = LineId;
        d["bookId"] = BookId;
        d["grade"] = (long)Grade;
        d["rank"] = Rank;
        if (CategoryId.HasValue) d["categoryId"] = CategoryId.Value;
        if (ScopeBookId.HasValue) d["scopeBookId"] = ScopeBookId.Value;
        if (TocId.HasValue) d["tocId"] = TocId.Value;
        d["extended"] = Extended;
        if (!string.IsNullOrEmpty(Model)) d["model"] = Model;
        if (!string.IsNullOrEmpty(Source)) d["source"] = Source;
        if (!string.IsNullOrEmpty(Scope)) d["scope"] = Scope;
        if (!string.IsNullOrEmpty(BookTitle)) d["bookTitle"] = BookTitle;
        if (!string.IsNullOrEmpty(HeRef)) d["heRef"] = HeRef;
        d["gradedAt"] = GradedAt;
        return d;
    }
}

class DafMark
{
    public string Amud;
    public string C;
    public long I;
    public string T;
    public bool? Ok;
    public long Cut;
    public string At;
    public long AtMs;

    public Dictionary<string, object> ToJson()
    {
        var d = new Dictionary<string, object>();
        d["amud"] = Amud;
        d["c"] = C;
        d["i"] = I;
        d["t"] = T ?? "";
        if (Ok.HasValue) d["ok"] = Ok.Value;
        d["cut"] = Cut;
        if (!string.IsNullOrEmpty(At)) d["at"] = At;
        return d;
    }
}

class DafReview
{
    public int Format = 1;
    public string Tractate;
    public string TractateHe;
    public string Library;
    public List<DafMark> Marks = new List<DafMark>();

    public Dictionary<string, object> ToJson()
    {
        var m = new List<object>(Marks.Count);
        foreach (var mark in Marks) m.Add(mark.ToJson());
        var d = new Dictionary<string, object>();
        d["format"] = (long)Format;
        d["tractate"] = Tractate;
        if (!string.IsNullOrEmpty(TractateHe)) d["tractateHe"] = TractateHe;
        if (!string.IsNullOrEmpty(Library)) d["library"] = Library;
        d["marks"] = m;
        return d;
    }
}

class FeedbackPackage
{
    public const int FORMAT = 1;
    public const string APP = "Pardes";
    public const int MAX_QUERY = 20000;
    public const int PART_BYTES = 900000; // <= 900 KB per HTTP POST (service limit is 1 MB)

    public string AppVersion = "zayit-telemetry/1.0.0";
    public string Sender;
    public string SentAt;
    public string Library;
    public string Source;
    public List<GradeRow> Grades = new List<GradeRow>();
    public List<SearchRow> Searches = new List<SearchRow>();
    public List<DafReview> Daf = new List<DafReview>();

    public bool IsEmpty
    {
        get { return Grades.Count == 0 && Searches.Count == 0 && Daf.Count == 0; }
    }

    public int DafMarkCount
    {
        get
        {
            int n = 0;
            foreach (var r in Daf) n += r.Marks.Count;
            return n;
        }
    }

    public Dictionary<string, object> ToJsonObject(bool includeDefaults = false)
    {
        var d = new Dictionary<string, object>();
        d["format"] = (long)FORMAT;
        d["app"] = APP;
        if (!string.IsNullOrEmpty(AppVersion)) d["appVersion"] = AppVersion;
        d["sender"] = Sender;
        d["sentAt"] = SentAt;
        if (!string.IsNullOrEmpty(Library)) d["library"] = Library;
        if (!string.IsNullOrEmpty(Source)) d["source"] = Source;
        if (includeDefaults || Grades.Count > 0)
        {
            var g = new List<object>(Grades.Count);
            foreach (var r in Grades) g.Add(r.ToJson());
            d["grades"] = g;
        }
        if (includeDefaults || Searches.Count > 0)
        {
            var s = new List<object>(Searches.Count);
            foreach (var r in Searches) s.Add(r.ToJson());
            d["searches"] = s;
        }
        if (includeDefaults || Daf.Count > 0)
        {
            var df = new List<object>(Daf.Count);
            foreach (var r in Daf) df.Add(r.ToJson());
            d["daf"] = df;
        }
        return d;
    }

    public string ToCompactJson()
    {
        return Json.Stringify(ToJsonObject(false), false);
    }

    public string ToPrettyJson()
    {
        return Json.Stringify(ToJsonObject(true), true) + "\n";
    }

    FeedbackPackage CopyWith(List<GradeRow> grades, List<SearchRow> searches, List<DafReview> daf)
    {
        return new FeedbackPackage
        {
            AppVersion = AppVersion,
            Sender = Sender,
            SentAt = SentAt,
            Library = Library,
            Source = Source,
            Grades = grades,
            Searches = searches,
            Daf = daf
        };
    }

    // Splits this package into one or more packages whose compact UTF-8 JSON is at most PART_BYTES (900 KB),
    // matching FeedbackPackage.parts() in Pardes.
    public List<FeedbackPackage> Parts()
    {
        var result = new List<FeedbackPackage>();
        if (IsEmpty) return result;
        if (Encoding.UTF8.GetByteCount(ToCompactJson()) <= PART_BYTES)
        {
            result.Add(this);
            return result;
        }
        result.AddRange(SplitList(Grades, g => CopyWith(g, new List<SearchRow>(), new List<DafReview>())));
        result.AddRange(SplitList(Searches, s => CopyWith(new List<GradeRow>(), s, new List<DafReview>())));
        foreach (var review in Daf)
        {
            result.AddRange(SplitList(review.Marks, marks =>
            {
                var r = new DafReview
                {
                    Format = review.Format,
                    Tractate = review.Tractate,
                    TractateHe = review.TractateHe,
                    Library = review.Library,
                    Marks = marks
                };
                return CopyWith(new List<GradeRow>(), new List<SearchRow>(), new List<DafReview> { r });
            }));
        }
        return result;
    }

    static List<FeedbackPackage> SplitList<T>(List<T> rows, Func<List<T>, FeedbackPackage> make)
    {
        if (rows == null || rows.Count == 0) return new List<FeedbackPackage>();
        var one = make(rows);
        if (rows.Count == 1 || Encoding.UTF8.GetByteCount(one.ToCompactJson()) <= PART_BYTES)
            return new List<FeedbackPackage> { one };
        int mid = rows.Count / 2;
        var left = SplitList(rows.GetRange(0, mid), make);
        var right = SplitList(rows.GetRange(mid, rows.Count - mid), make);
        left.AddRange(right);
        return left;
    }

    // Writes the package to folder as pardes-feedback-<sender8>-<time>.json atomically via a .part file.
    public string WriteTo(string folder, int index = 0, int count = 1)
    {
        Directory.CreateDirectory(folder);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var shortSender = (Sender ?? "unknown").Length >= 8 ? Sender.Substring(0, 8) : (Sender ?? "unknown");
        var partSuffix = count > 1 ? "-" + (index + 1) : "";
        var name = string.Format("pardes-feedback-{0}-{1}{2}.json", shortSender, stamp, partSuffix);
        var file = Path.Combine(folder, name);
        var tmp = Path.Combine(folder, "." + name + ".part");
        var bytes = Encoding.UTF8.GetBytes(ToPrettyJson());
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(file)) File.Delete(file);
        File.Move(tmp, file);
        return file;
    }
}
