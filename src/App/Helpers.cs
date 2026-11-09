using System;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

// Zayit's own preferences, read only, for its look (theme_mode, accent_color) and its library's place (database_path).
// Zayit keeps them in the root Java preferences node, HKCU\Software\JavaSoft\Prefs, escaped by the JDK (JavaPrefs).
// Nothing else is read there, and nothing is ever written.
static class ZayitPrefs
{
    const string Node = @"Software\JavaSoft\Prefs";

    public static string Read(string name)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Node))
            {
                var raw = k == null ? null : k.GetValue(name) as string;
                return raw == null ? null : JavaPrefs.Decode(raw);
            }
        }
        catch (System.Security.SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

static class Fmt
{
    static readonly CultureInfo He = CultureInfo.GetCultureInfo("he-IL");

    public static string Num(long n) { return n.ToString("N0", He); }

    public static string When(long epochMs)
    {
        if (epochMs <= 0) return "—";
        try { return DateTimeOffset.FromUnixTimeMilliseconds(epochMs).LocalDateTime.ToString("dd/MM/yyyy HH:mm", He); }
        catch (ArgumentOutOfRangeException) { return "—"; }
    }

    public static string ShortPath(string p, int max)
    {
        if (string.IsNullOrEmpty(p) || p.Length <= max) return p ?? "";
        var root = Path.GetPathRoot(p) ?? "";
        var tail = p.Substring(p.Length - Math.Max(10, max - root.Length - 4));
        var slash = tail.IndexOfAny(new[] { '\\', '/' });
        if (slash > 0) tail = tail.Substring(slash);
        return root.TrimEnd('\\') + @"\…" + tail;
    }

    // "3 חיפושים", "חיפוש אחד"
    public static string Count(long n, string one, string many)
    {
        return n == 1 ? one : Num(n) + " " + many;
    }
}
