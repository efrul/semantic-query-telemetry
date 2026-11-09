using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

// What one app on this computer holds: where it was found, and its rows, as its reader read them.
public sealed class SourceData
{
    public Source Source;
    public bool Found;
    public readonly List<string> Places = new List<string>();   // the folders it was found in
    public string Library;                                       // the library's version, when the app records one
    public readonly List<SearchRow> Searches = new List<SearchRow>();
    public readonly List<GradeRow> Grades = new List<GradeRow>();
    public readonly List<DafMark> Marks = new List<DafMark>();
    public readonly List<string> Problems = new List<string>();  // what could not be read, for the window to say

    public void AddPlace(string place)
    {
        foreach (var p in Places) if (string.Equals(p, place, StringComparison.OrdinalIgnoreCase)) return;
        Places.Add(place);
    }
}

public interface IReader
{
    SourceData Read();
}

// What would be sent now: the rows of the apps and kinds that are on, that the service has not taken as they stand,
// and that the user did not leave out; and, apart, the rows the user left out.
public sealed class Selection
{
    public readonly List<SearchRow> Searches = new List<SearchRow>();
    public readonly List<GradeRow> Grades = new List<GradeRow>();
    public readonly List<DafMark> Marks = new List<DafMark>();
    public readonly List<SearchRow> ExcludedSearches = new List<SearchRow>();
    public readonly List<GradeRow> ExcludedGrades = new List<GradeRow>();
    public string Library;

    public bool IsEmpty { get { return Searches.Count == 0 && Grades.Count == 0 && Marks.Count == 0; } }
}

public sealed class SendResult
{
    public int Searches, Grades, Marks, Parts, RefusedRows;
    public Answer StoppedBy;     // null when everything went
    public bool Complete { get { return StoppedBy == null; } }
}

// Only one send at a time on this computer, whichever started it: the window, or the background sender.
public sealed class SendLock : IDisposable
{
    readonly Mutex mutex;
    bool owned;

    SendLock(Mutex m, bool owned) { mutex = m; this.owned = owned; }

    public static SendLock TryTake(int waitMs)
    {
        var m = new Mutex(false, @"Local\SearchDataSender.Send");
        bool got;
        try { got = m.WaitOne(waitMs); }
        catch (AbandonedMutexException) { got = true; }
        if (got) return new SendLock(m, true);
        m.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (owned) { mutex.ReleaseMutex(); owned = false; }
        mutex.Dispose();
    }
}

public static class Engine
{
    public static List<SourceData> ReadAll(IEnumerable<IReader> readers)
    {
        var all = new List<SourceData>();
        foreach (var r in readers) all.Add(r.Read());
        return all;
    }

    public static Selection Select(IEnumerable<SourceData> data, Settings settings, Ledger ledger)
    {
        var sel = new Selection();
        foreach (var d in data)
        {
            if (!d.Found || !settings.IsSourceOn(d.Source)) continue;
            if (sel.Library == null && !string.IsNullOrEmpty(d.Library) && d.Library.Length <= Limits.MaxVersion) sel.Library = d.Library;
            if (settings.SearchesOn)
                foreach (var s in d.Searches)
                {
                    if (!s.Valid()) continue;
                    if (ledger.IsExcluded(s.Key)) { sel.ExcludedSearches.Add(s); continue; }
                    if (!ledger.WasSent(s.Fingerprint) && !ledger.WasRefused(s.Fingerprint)) sel.Searches.Add(s);
                }
            if (settings.GradesOn)
                foreach (var g in d.Grades)
                {
                    if (!g.Valid()) continue;
                    if (ledger.IsExcluded(g.Key)) { sel.ExcludedGrades.Add(g); continue; }
                    if (!ledger.WasSent(g.Fingerprint) && !ledger.WasRefused(g.Fingerprint)) sel.Grades.Add(g);
                }
            if (settings.DafOn)
                foreach (var m in d.Marks)
                    if (m.Valid() && !ledger.WasSent(m.Fingerprint) && !ledger.WasRefused(m.Fingerprint)) sel.Marks.Add(m);
        }
        sel.Searches.Sort((a, b) => a.LastAt.CompareTo(b.LastAt));
        sel.Grades.Sort((a, b) => a.GradedAt.CompareTo(b.GradedAt));
        return sel;
    }

    public static Package Build(Selection sel, Settings settings, long nowMs)
    {
        var pkg = new Package
        {
            Sender = settings.Sender,
            SentAt = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            Library = sel.Library
        };
        pkg.Searches.AddRange(sel.Searches);
        pkg.Grades.AddRange(sel.Grades);
        pkg.Marks.AddRange(sel.Marks);
        return pkg;
    }

    // Sends [sel] part by part. Each part the service takes is written to the ledger at once, so what went is never sent
    // again even if a later part fails. A row the service refuses is left out, kept as refused, and the rest goes on.
    // While the service says this address sent too much this minute, waits [busyWaitMs] and goes on, up to [busyWaits]
    // times. The caller holds the SendLock.
    public static SendResult Send(Selection sel, Settings settings, Ledger ledger, IService service, Func<long> now,
        int busyWaits, int busyWaitMs, Action<string> progress)
    {
        var result = new SendResult();
        ledger.Tidy();
        var parts = new Queue<Package>(Build(sel, settings, now()).Parts(Limits.PartBytes));
        int total = parts.Count, waits = 0;
        while (parts.Count > 0)
        {
            var part = parts.Peek();
            if (part.IsEmpty) { parts.Dequeue(); continue; }
            if (progress != null && total > 1) progress(string.Format("שולח חלק {0} מתוך {1}…", total - parts.Count + 1, total));
            var answer = service.Post(part.ToCompactJson());
            if (answer.Ok)
            {
                var fingerprints = new List<string>();
                foreach (var s in part.Searches) fingerprints.Add(s.Fingerprint);
                foreach (var g in part.Grades) fingerprints.Add(g.Fingerprint);
                foreach (var m in part.Marks) fingerprints.Add(m.Fingerprint);
                ledger.MarkSent(fingerprints);
                settings.AddSent(part.Searches.Count, part.Grades.Count, part.Marks.Count, now());
                result.Searches += part.Searches.Count;
                result.Grades += part.Grades.Count;
                result.Marks += part.Marks.Count;
                result.Parts++;
                parts.Dequeue();
                continue;
            }
            if (answer.Outcome == Outcome.RowRefused && LeaveOut(part, answer, ledger, result)) continue;
            if (answer.Outcome == Outcome.Busy && waits < busyWaits)
            {
                waits++;
                if (progress != null) progress("השירות מבקש להמתין דקה, וממשיך לבד…");
                Thread.Sleep(busyWaitMs);
                continue;
            }
            result.StoppedBy = answer;
            return result;
        }
        return result;
    }

    // Takes the row [answer] names out of [part] and keeps it as refused; false when the answer names no row of it.
    static bool LeaveOut(Package part, Answer answer, Ledger ledger, SendResult result)
    {
        int i = answer.RowIndex;
        if (answer.RowKind == "search" && i >= 0 && i < part.Searches.Count)
        {
            ledger.MarkRefused(part.Searches[i].Fingerprint);
            part.Searches.RemoveAt(i);
        }
        else if (answer.RowKind == "grade" && i >= 0 && i < part.Grades.Count)
        {
            ledger.MarkRefused(part.Grades[i].Fingerprint);
            part.Grades.RemoveAt(i);
        }
        else if (answer.RowKind == "daf review")
        {
            // The reviews are the part's tractates in the order their marks come (Package.ToJson)
            var tractates = new List<string>();
            foreach (var m in part.Marks) if (!tractates.Contains(m.Tractate)) tractates.Add(m.Tractate);
            if (i < 0 || i >= tractates.Count) return false;
            var tractate = tractates[i];
            foreach (var m in part.Marks.FindAll(x => x.Tractate == tractate)) ledger.MarkRefused(m.Fingerprint);
            part.Marks.RemoveAll(x => x.Tractate == tractate);
        }
        else return false;
        result.RefusedRows++;
        return true;
    }
}
