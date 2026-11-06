using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// Lightweight, dependency-free JSON reader and writer for feedback packages, daf review files, and Otzaria's JSON/JSONL.
static class Json
{
    public static object Parse(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        int i = 0;
        if (s[0] == '\uFEFF') i = 1; // skip UTF-8 BOM
        return ReadVal(s, ref i);
    }

    public static Dictionary<string, object> ParseObj(string s)
    {
        return Parse(s) as Dictionary<string, object>;
    }

    static void SkipWs(string s, ref int i)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
            else break;
        }
    }

    static object ReadVal(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) return null;
        char c = s[i];
        if (c == '{') return ReadObj(s, ref i);
        if (c == '[') return ReadArr(s, ref i);
        if (c == '"') return ReadStr(s, ref i);
        if (c == 't' && i + 4 <= s.Length && s.Substring(i, 4) == "true") { i += 4; return true; }
        if (c == 'f' && i + 5 <= s.Length && s.Substring(i, 5) == "false") { i += 5; return false; }
        if (c == 'n' && i + 4 <= s.Length && s.Substring(i, 4) == "null") { i += 4; return null; }
        return ReadNum(s, ref i);
    }

    static Dictionary<string, object> ReadObj(string s, ref int i)
    {
        var d = new Dictionary<string, object>(StringComparer.Ordinal);
        i++; // '{'
        for (;;)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) break;
            if (s[i] == '}') { i++; break; }
            if (s[i] == ',') { i++; continue; }
            if (s[i] != '"') break;
            string k = ReadStr(s, ref i);
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ':') i++;
            d[k] = ReadVal(s, ref i);
        }
        return d;
    }

    static List<object> ReadArr(string s, ref int i)
    {
        var l = new List<object>();
        i++; // '['
        for (;;)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) break;
            if (s[i] == ']') { i++; break; }
            if (s[i] == ',') { i++; continue; }
            l.Add(ReadVal(s, ref i));
        }
        return l;
    }

    static string ReadStr(string s, ref int i)
    {
        var b = new StringBuilder();
        i++; // '"'
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '"') return b.ToString();
            if (c != '\\' || i >= s.Length) { b.Append(c); continue; }
            char e = s[i++];
            switch (e)
            {
                case '"': b.Append('"'); break;
                case '\\': b.Append('\\'); break;
                case '/': b.Append('/'); break;
                case 'b': b.Append('\b'); break;
                case 'f': b.Append('\f'); break;
                case 'n': b.Append('\n'); break;
                case 'r': b.Append('\r'); break;
                case 't': b.Append('\t'); break;
                case 'u':
                    if (i + 4 <= s.Length)
                    {
                        int code;
                        if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            b.Append((char)code);
                            i += 4;
                        }
                    }
                    break;
                default: b.Append(e); break;
            }
        }
        return b.ToString();
    }

    static object ReadNum(string s, ref int i)
    {
        int start = i;
        bool isFloat = false;
        while (i < s.Length)
        {
            char c = s[i];
            if ((c >= '0' && c <= '9') || c == '-' || c == '+') i++;
            else if (c == '.' || c == 'e' || c == 'E') { isFloat = true; i++; }
            else break;
        }
        string raw = s.Substring(start, i - start);
        if (!isFloat)
        {
            long l;
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
        }
        double d;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return 0L;
    }

    public static string Stringify(object v, bool indent = false)
    {
        var b = new StringBuilder();
        WriteVal(b, v, indent ? 0 : -1);
        return b.ToString();
    }

    static void WriteVal(StringBuilder b, object v, int depth)
    {
        if (v == null) { b.Append("null"); return; }
        if (v is bool) { b.Append((bool)v ? "true" : "false"); return; }
        if (v is string) { WriteStr(b, (string)v); return; }
        if (v is int || v is long || v is short || v is byte)
        {
            b.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            return;
        }
        if (v is double || v is float || v is decimal)
        {
            b.Append(Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
            return;
        }
        var dict = v as IDictionary<string, object>;
        if (dict != null)
        {
            b.Append('{');
            bool first = true;
            foreach (var kv in dict)
            {
                if (kv.Value == null) continue;
                if (!first) b.Append(',');
                if (depth >= 0) { b.Append('\n').Append(' ', (depth + 1) * 2); }
                WriteStr(b, kv.Key);
                b.Append(depth >= 0 ? ": " : ":");
                WriteVal(b, kv.Value, depth >= 0 ? depth + 1 : -1);
                first = false;
            }
            if (!first && depth >= 0) { b.Append('\n').Append(' ', depth * 2); }
            b.Append('}');
            return;
        }
        var list = v as IEnumerable;
        if (list != null)
        {
            b.Append('[');
            bool first = true;
            foreach (var item in list)
            {
                if (!first) b.Append(',');
                if (depth >= 0) { b.Append('\n').Append(' ', (depth + 1) * 2); }
                WriteVal(b, item, depth >= 0 ? depth + 1 : -1);
                first = false;
            }
            if (!first && depth >= 0) { b.Append('\n').Append(' ', depth * 2); }
            b.Append(']');
            return;
        }
        WriteStr(b, v.ToString());
    }

    public static void WriteStr(StringBuilder b, string s)
    {
        b.Append('"');
        if (s != null)
        {
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\b': b.Append("\\b"); break;
                    case '\f': b.Append("\\f"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20) b.AppendFormat("\\u{0:x4}", (int)c);
                        else b.Append(c);
                        break;
                }
            }
        }
        b.Append('"');
    }

    // ---- Typed helpers for Dictionary<string, object> ----

    public static string Str(Dictionary<string, object> d, string k, string def = null)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
        return v as string ?? v.ToString();
    }

    public static long? LongOrNull(Dictionary<string, object> d, string k)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return null;
        if (v is long) return (long)v;
        if (v is int) return (int)v;
        if (v is double) return (long)(double)v;
        long parsed;
        if (long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
        return null;
    }

    public static long Long(Dictionary<string, object> d, string k, long def = 0L)
    {
        return LongOrNull(d, k) ?? def;
    }

    public static bool? BoolOrNull(Dictionary<string, object> d, string k)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return null;
        if (v is bool) return (bool)v;
        if (v is long) return (long)v != 0;
        var s = v.ToString();
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1") return true;
        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) || s == "0") return false;
        return null;
    }

    public static Dictionary<string, object> Obj(Dictionary<string, object> d, string k)
    {
        object v;
        return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
    }

    public static List<object> Arr(Dictionary<string, object> d, string k)
    {
        object v;
        return d != null && d.TryGetValue(k, out v) ? v as List<object> : null;
    }
}
