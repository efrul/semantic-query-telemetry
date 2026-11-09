using System;
using System.Collections.Generic;
using System.Globalization;

// Where the tool keeps its choices: the Windows registry (RegistryStore, HKCU\Software\SearchDataSender) in the app, a
// dictionary in the tests. Only the tool's own key is ever read: never Zayit's, Pardes's or any other app's settings.
public interface IStore
{
    string Get(string name);
    void Set(string name, string value);
}

public sealed class MemoryStore : IStore
{
    readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
    public string Get(string name) { string v; return values.TryGetValue(name, out v) ? v : null; }
    public void Set(string name, string value) { values[name] = value; }
}

public sealed class Settings
{
    // The consent text's version: a new wording asks again
    public const int ConsentVersion = 1;

    readonly IStore store;
    public Settings(IStore store) { this.store = store; }

    string Get(string name, string def) { var v = store.Get(name); return v ?? def; }
    bool GetBool(string name, bool def)
    {
        var v = store.Get(name);
        return v == "1" ? true : v == "0" ? false : def;
    }
    void SetBool(string name, bool value) { store.Set(name, value ? "1" : "0"); }
    long GetLong(string name)
    {
        long v;
        return long.TryParse(store.Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0L;
    }
    void SetLong(string name, long value) { store.Set(name, value.ToString(CultureInfo.InvariantCulture)); }

    // ---- Consent ----

    public bool HasConsent { get { return GetLong("consent") >= ConsentVersion; } }

    public void GiveConsent(long nowMs)
    {
        SetLong("consent", ConsentVersion);
        SetLong("consent_at", nowMs);
    }

    public void WithdrawConsent()
    {
        SetLong("consent", 0);
        AutoSend = false;
    }

    // ---- Choices ----

    public bool AutoSend { get { return GetBool("auto_send", false); } set { SetBool("auto_send", value); } }
    public bool IsSourceOn(Source s) { return GetBool("source_" + Sources.Id(s), true); }
    public void SetSourceOn(Source s, bool on) { SetBool("source_" + Sources.Id(s), on); }
    public bool SearchesOn { get { return GetBool("kind_searches", true); } set { SetBool("kind_searches", value); } }
    public bool GradesOn { get { return GetBool("kind_grades", true); } set { SetBool("kind_grades", value); } }
    public bool DafOn { get { return GetBool("kind_daf", true); } set { SetBool("kind_daf", value); } }

    public string ServiceUrl
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("SEARCH_DATA_SERVICE_URL");
            var v = !string.IsNullOrEmpty(env) ? env : Get("service_url", null);
            v = (v ?? "").Trim().TrimEnd('/');
            return v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? v : AppInfo.DefaultServiceUrl;
        }
    }

    // Folders the user added for a portable or moved install, one per line
    public List<string> ExtraFolders
    {
        get
        {
            var list = new List<string>();
            foreach (var f in Get("extra_folders", "").Split('\n'))
                if (f.Trim().Length > 0) list.Add(f.Trim());
            return list;
        }
        set { store.Set("extra_folders", string.Join("\n", value.ToArray())); }
    }

    // ---- The sender ----

    // This computer's random sender id (a UUID v4), made the first time it is asked for
    public string Sender
    {
        get
        {
            var s = (Get("sender", "") ?? "").Trim().ToLowerInvariant();
            Guid g;
            if (s.Length == 36 && Guid.TryParseExact(s, "D", out g)) return s;
            return NewSender();
        }
    }

    public string NewSender()
    {
        var s = Guid.NewGuid().ToString("D").ToLowerInvariant();
        store.Set("sender", s);
        return s;
    }

    // ---- What was sent ----

    public void AddSent(int searches, int grades, int marks, long nowMs)
    {
        SetLong("sent_searches", GetLong("sent_searches") + searches);
        SetLong("sent_grades", GetLong("sent_grades") + grades);
        SetLong("sent_marks", GetLong("sent_marks") + marks);
        SetLong("sent_packages", GetLong("sent_packages") + 1);
        if (GetLong("first_sent_at") <= 0) SetLong("first_sent_at", nowMs);
        SetLong("last_sent_at", nowMs);
    }

    public long SentSearches { get { return GetLong("sent_searches"); } }
    public long SentGrades { get { return GetLong("sent_grades"); } }
    public long SentMarks { get { return GetLong("sent_marks"); } }
    public long SentPackages { get { return GetLong("sent_packages"); } }
    public long FirstSentAt { get { return GetLong("first_sent_at"); } }
    public long LastSentAt { get { return GetLong("last_sent_at"); } }

    public void ResetSentStats()
    {
        foreach (var n in new[] { "sent_searches", "sent_grades", "sent_marks", "sent_packages", "first_sent_at", "last_sent_at" })
            SetLong(n, 0);
    }

    // When the background sender last tried, so that a burst of network events sends once
    public long LastAttemptAt { get { return GetLong("last_attempt_at"); } set { SetLong("last_attempt_at", value); } }
}
