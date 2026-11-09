using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

// What this computer already sent, what the user chose never to send, and what the service refused, kept as digests of
// the rows' fingerprints and keys (never the searches themselves) in a text file of the tool's own:
//   S <digest>   a row's fingerprint that the service took
//   X <digest>   a row's key the user left out: never sent, whatever it becomes
//   U <digest>   a key put back in (cancels an X before it)
//   R <digest>   a row's fingerprint the service refused (sent again only once it changes)
// Lines are appended as things happen, so a crash between two parts of a send loses nothing; a later line overrides
// an earlier one. Sending again only what changed keeps the service from receiving the same rows twice. The window and
// the background sender are two processes: both open the file shared, and only the one holding the SendLock tidies it.
public sealed class Ledger
{
    const string Header = "search-data-sender ledger 1";

    readonly string path;
    readonly HashSet<string> sent = new HashSet<string>(StringComparer.Ordinal);
    readonly HashSet<string> refused = new HashSet<string>(StringComparer.Ordinal);
    readonly HashSet<string> excluded = new HashSet<string>(StringComparer.Ordinal);
    int lines;

    public Ledger(string path)
    {
        this.path = path;
        Load();
    }

    public static string Digest(string text)
    {
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            var b = new StringBuilder(32);
            for (int i = 0; i < 16; i++) b.Append(hash[i].ToString("x2"));
            return b.ToString();
        }
    }

    void Load()
    {
        if (path == null || !File.Exists(path)) return;
        foreach (var raw in ReadLines())
        {
            if (raw.Length < 3 || raw[1] != ' ') continue;
            var d = raw.Substring(2).Trim();
            lines++;
            switch (raw[0])
            {
                case 'S': sent.Add(d); refused.Remove(d); break;
                case 'R': refused.Add(d); break;
                case 'X': excluded.Add(d); break;
                case 'U': excluded.Remove(d); break;
            }
        }
    }

    List<string> ReadLines()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var all = new List<string>();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null) all.Add(line);
                }
                return all;
            }
            catch (IOException) { if (attempt >= 20) throw; System.Threading.Thread.Sleep(50); }
        }
    }

    // Rewrites the file without the lines later ones cancelled, once there are many of them. Call it holding the SendLock.
    public void Tidy()
    {
        int live = sent.Count + refused.Count + excluded.Count;
        int cancelled = lines - live;
        if (path != null && cancelled > 1000 && cancelled > live / 2) Compact();
    }

    public bool WasSent(string fingerprint) { return sent.Contains(Digest(fingerprint)); }
    public bool WasRefused(string fingerprint) { return refused.Contains(Digest(fingerprint)); }
    public bool IsExcluded(string key) { return excluded.Contains(Digest(key)); }
    public int SentCount { get { return sent.Count; } }

    public void MarkSent(IEnumerable<string> fingerprints)
    {
        var add = new List<string>();
        foreach (var f in fingerprints)
        {
            var d = Digest(f);
            if (sent.Add(d)) add.Add("S " + d);
            refused.Remove(d);
        }
        Append(add);
    }

    public void MarkRefused(string fingerprint)
    {
        var d = Digest(fingerprint);
        if (refused.Add(d)) Append(new List<string> { "R " + d });
    }

    public void SetExcluded(string key, bool exclude)
    {
        var d = Digest(key);
        if (exclude ? excluded.Add(d) : excluded.Remove(d)) Append(new List<string> { (exclude ? "X " : "U ") + d });
    }

    void Append(List<string> add)
    {
        if (add.Count == 0 || path == null) return;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var text = new StringBuilder();
        foreach (var l in add) text.Append(l).Append('\n');
        Locked(() =>
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var bytes = Encoding.UTF8.GetBytes((fs.Length == 0 ? Header + "\n" : "") + text);
                        fs.Write(bytes, 0, bytes.Length);
                    }
                    return;
                }
                catch (IOException) { if (attempt >= 20) throw; System.Threading.Thread.Sleep(50); }
            }
        });
        lines += add.Count;
    }

    // Appending from two processes at once could write over each other's lines: one writer at a time
    static void Locked(Action write)
    {
        using (var m = new System.Threading.Mutex(false, @"Local\SearchDataSender.Ledger"))
        {
            bool got;
            try { got = m.WaitOne(10000); }
            catch (System.Threading.AbandonedMutexException) { got = true; }
            try { write(); }
            finally { if (got) m.ReleaseMutex(); }
        }
    }

    void Compact()
    {
        Locked(CompactNow);
    }

    void CompactNow()
    {
        var all = new List<string> { Header };
        foreach (var d in sent) all.Add("S " + d);
        foreach (var d in refused) all.Add("R " + d);
        foreach (var d in excluded) all.Add("X " + d);
        var tmp = path + ".part";
        File.WriteAllLines(tmp, all.ToArray(), new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
        lines = all.Count - 1;
    }
}
