using System;
using System.Globalization;
using System.Text;

// The JDK's escaping of Java preference values in the Windows registry (java.util.prefs.WindowsPreferences): '/' before
// each capital letter, "//" for '\', '\' for '/', and /uXXXX for characters outside printable ASCII.
public static class JavaPrefs
{
    // The value [s] stood for; null for one written another way (a "/!" form), which is not read.
    public static string Decode(string s)
    {
        if (s == null || s.StartsWith("/!", StringComparison.Ordinal)) return null;
        var b = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\') { b.Append('/'); continue; }
            if (c != '/' || i + 1 >= s.Length) { b.Append(c); continue; }
            char n = s[i + 1];
            int code;
            if (n == '/') { b.Append('\\'); i++; }
            else if (n >= 'A' && n <= 'Z') { b.Append(n); i++; }
            else if (n == 'u' && i + 6 <= s.Length &&
                int.TryParse(s.Substring(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
            {
                b.Append((char)code);
                i += 5;
            }
            else b.Append(c);
        }
        return b.ToString();
    }
}
