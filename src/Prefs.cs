using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

// Reads and writes Zayit's and Pardes's preferences in HKCU\Software\JavaSoft\Prefs, using Java's WindowsPreferences
// encoding: '/' before an uppercase letter, '//' for '/', '/uXXXX' for anything outside ASCII. This way the tool shares
// Zayit's theme, library path, and feedback sender id and timestamps with the desktop apps.
static class Prefs
{
    public const string ZayitKey = @"Software\JavaSoft\Prefs";
    public const string PardesKey = @"Software\JavaSoft\Prefs\io\github\efrul\pardes";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "ZayitTelemetry";

    // Overridden in SelfTest so tests never touch the user's real registry settings.
    public static string TestRoot = null;

    static string RootKey(bool pardes)
    {
        if (TestRoot != null) return pardes ? TestRoot + @"\pardes" : TestRoot;
        return pardes ? PardesKey : ZayitKey;
    }

    public static string Get(string name, string def = "")
    {
        var v = GetFromNode(false, name);
        if (!string.IsNullOrEmpty(v)) return v;
        v = GetFromNode(true, name);
        return !string.IsNullOrEmpty(v) ? v : def;
    }

    public static string GetFromNode(bool pardes, string name, string def = "")
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RootKey(pardes)))
            {
                if (k == null) return def;
                var raw = k.GetValue(name) as string;
                return raw != null ? FromJava(raw) : def;
            }
        }
        catch { return def; }
    }

    public static bool Set(string name, string value, bool alsoPardes = false)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RootKey(false)))
            {
                if (k == null) return false;
                k.SetValue(name, ToJava(value ?? ""), RegistryValueKind.String);
            }
            if (alsoPardes)
            {
                using (var kp = Registry.CurrentUser.OpenSubKey(RootKey(true), true))
                {
                    if (kp != null) kp.SetValue(name, ToJava(value ?? ""), RegistryValueKind.String);
                }
            }
            return true;
        }
        catch { return false; }
    }

    public static bool GetBool(string name, bool def)
    {
        var s = Get(name, "");
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return def;
    }

    public static void SetBool(string name, bool val)
    {
        Set(name, val ? "true" : "false", true);
    }

    public static long GetLong(string name, long def = 0L)
    {
        long v;
        return long.TryParse(Get(name, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
    }

    public static void SetLong(string name, long val)
    {
        Set(name, val.ToString(CultureInfo.InvariantCulture), true);
    }

    // The random UUID v4 sender id, shared with Pardes's feedback_sender when present.
    public static string GetSender()
    {
        var s = Get("feedback_sender", "").Trim().ToLowerInvariant();
        Guid g;
        if (s.Length == 36 && Guid.TryParseExact(s, "D", out g)) return s;
        return RenewSender();
    }

    public static string RenewSender()
    {
        var s = Guid.NewGuid().ToString("D").ToLowerInvariant();
        Set("feedback_sender", s, true);
        return s;
    }

    public static bool IsKindEnabled(string kind, bool def = true)
    {
        return GetBool("feedback_kind_" + kind, def);
    }

    public static void SetKindEnabled(string kind, bool enabled)
    {
        SetBool("feedback_kind_" + kind, enabled);
    }

    public static bool IsSourceEnabled(string source, bool def = true)
    {
        return GetBool("feedback_source_" + source, def);
    }

    public static void SetSourceEnabled(string source, bool enabled)
    {
        SetBool("feedback_source_" + source, enabled);
    }

    // Per-source and unified sent-at timestamps (epoch milliseconds).
    public static long GetSentAt(string source, string kind)
    {
        var specific = GetLong("feedback_sent_" + source + "_" + kind, 0L);
        if (specific > 0) return specific;
        if (source == "zayit") return GetLong("feedback_sent_" + kind, 0L);
        return 0L;
    }

    public static void SetSentAt(string source, string kind, long at)
    {
        SetLong("feedback_sent_" + source + "_" + kind, at);
        if (source == "zayit") SetLong("feedback_sent_" + kind, at);
    }

    public static bool IsAutoSendEnabled()
    {
        return GetBool("feedback_auto_send", false);
    }

    public static void SetAutoSendEnabled(bool enabled)
    {
        SetBool("feedback_auto_send", enabled);
        if (TestRoot != null) return;
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                if (enabled)
                {
                    var exe = Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                        k.SetValue(RunValueName, "\"" + exe + "\" --auto", RegistryValueKind.String);
                }
                else
                {
                    k.DeleteValue(RunValueName, false);
                }
            }
        }
        catch { }
    }

    public static string ToJava(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s)
        {
            if (ch < 0x20 || ch > 0x7f) sb.Append("/u").Append(((int)ch).ToString("x4"));
            else if (ch == '\\') sb.Append("//");
            else if (ch == '/') sb.Append('\\');
            else if (ch >= 'A' && ch <= 'Z') sb.Append('/').Append(ch);
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    public static string FromJava(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '/')
            {
                char next = i + 1 < s.Length ? s[i + 1] : ' ';
                if (next == 'u')
                {
                    if (s.Length < i + 6) break;
                    ch = (char)Convert.ToInt32(s.Substring(i + 2, 4), 16);
                    i += 5;
                }
                else if (next >= 'A' && next <= 'Z') { ch = next; i++; }
                else if (next == '/') { ch = '\\'; i++; }
            }
            else if (ch == '\\') ch = '/';
            sb.Append(ch);
        }
        return sb.ToString();
    }
}

static class Fmt
{
    static readonly CultureInfo He = CultureInfo.GetCultureInfo("he-IL");

    public static string Num(long n) { return n.ToString("N0", He); }

    public static string Size(long b)
    {
        if (b >= 1000000000L) return string.Format(He, "{0:0.0} GB", b / 1073741824.0);
        if (b >= 1000000L) return string.Format(He, "{0:0.0} MB", b / 1048576.0);
        if (b >= 1000L) return string.Format(He, "{0:0} KB", b / 1024.0);
        return b + " B";
    }

    public static string When(long epochMs)
    {
        if (epochMs <= 0) return "—";
        try
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).LocalDateTime;
            return dt.ToString("dd/MM/yyyy HH:mm", He);
        }
        catch { return "—"; }
    }

    public static string ShortPath(string p, int max = 56)
    {
        if (string.IsNullOrEmpty(p) || p.Length <= max) return p ?? "";
        var root = Path.GetPathRoot(p) ?? "";
        var tail = p.Substring(p.Length - (max - root.Length - 4));
        var slash = tail.IndexOfAny(new[] { '\\', '/' });
        if (slash > 0) tail = tail.Substring(slash);
        return root.TrimEnd('\\') + @"\…" + tail;
    }
}
