using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// JSON as the apps write it and the service reads it. Parsing is strict (a malformed file is refused, not half read);
// objects come back as Dictionary<string, object>, arrays as List<object>, whole numbers as long, others as double.
// Writing keeps a JsonMap's keys in the order they were added, so packages read the same as Pardes's own.
public sealed class JsonMap : IEnumerable<KeyValuePair<string, object>>
{
    readonly List<KeyValuePair<string, object>> items = new List<KeyValuePair<string, object>>();

    // Adds [key] unless [value] is null, so optional fields are left out rather than written as null.
    public JsonMap Add(string key, object value)
    {
        if (value != null) items.Add(new KeyValuePair<string, object>(key, value));
        return this;
    }

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() { return items.GetEnumerator(); }
    IEnumerator IEnumerable.GetEnumerator() { return items.GetEnumerator(); }
}

public static class Json
{
    // ---- Reading ----

    public static object Parse(string text)
    {
        if (text == null) throw new FormatException("no JSON");
        int i = 0;
        // A byte order mark is not JSON, but editors write it
        if (text.Length > 0 && text[0] == '﻿') i = 1;
        SkipSpace(text, ref i);
        var value = ReadValue(text, ref i, 0);
        SkipSpace(text, ref i);
        if (i != text.Length) throw new FormatException("text after the JSON value at " + i);
        return value;
    }

    // The object [text] holds, or null when it is not JSON or not an object.
    public static Dictionary<string, object> TryParseObject(string text)
    {
        try { return Parse(text) as Dictionary<string, object>; }
        catch (FormatException) { return null; }
    }

    public static object TryParse(string text)
    {
        try { return Parse(text); }
        catch (FormatException) { return null; }
    }

    const int MaxDepth = 64;

    static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
    }

    static object ReadValue(string s, ref int i, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("JSON nested too deep");
        if (i >= s.Length) throw new FormatException("JSON ends early");
        char c = s[i];
        if (c == '{') return ReadObject(s, ref i, depth);
        if (c == '[') return ReadArray(s, ref i, depth);
        if (c == '"') return ReadString(s, ref i);
        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber(s, ref i);
        if (Word(s, ref i, "true")) return true;
        if (Word(s, ref i, "false")) return false;
        if (Word(s, ref i, "null")) return null;
        throw new FormatException("unexpected '" + c + "' at " + i);
    }

    static bool Word(string s, ref int i, string word)
    {
        if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
        i += word.Length;
        return true;
    }

    static Dictionary<string, object> ReadObject(string s, ref int i, int depth)
    {
        var obj = new Dictionary<string, object>(StringComparer.Ordinal);
        i++;
        SkipSpace(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return obj; }
        while (true)
        {
            SkipSpace(s, ref i);
            if (i >= s.Length || s[i] != '"') throw new FormatException("expected a key at " + i);
            var key = ReadString(s, ref i);
            SkipSpace(s, ref i);
            if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + i);
            i++;
            SkipSpace(s, ref i);
            obj[key] = ReadValue(s, ref i, depth + 1);
            SkipSpace(s, ref i);
            if (i >= s.Length) throw new FormatException("object not closed");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == '}') { i++; return obj; }
            throw new FormatException("expected ',' or '}' at " + i);
        }
    }

    static List<object> ReadArray(string s, ref int i, int depth)
    {
        var list = new List<object>();
        i++;
        SkipSpace(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return list; }
        while (true)
        {
            SkipSpace(s, ref i);
            list.Add(ReadValue(s, ref i, depth + 1));
            SkipSpace(s, ref i);
            if (i >= s.Length) throw new FormatException("array not closed");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == ']') { i++; return list; }
            throw new FormatException("expected ',' or ']' at " + i);
        }
    }

    static string ReadString(string s, ref int i)
    {
        var b = new StringBuilder();
        i++;
        while (true)
        {
            if (i >= s.Length) throw new FormatException("string not closed");
            char c = s[i++];
            if (c == '"') return b.ToString();
            if (c < 0x20) throw new FormatException("control character in a string at " + (i - 1));
            if (c != '\\') { b.Append(c); continue; }
            if (i >= s.Length) throw new FormatException("string not closed");
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
                    if (i + 4 > s.Length) throw new FormatException("short \\u escape");
                    int code;
                    if (!int.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                        throw new FormatException("bad \\u escape at " + i);
                    b.Append((char)code);
                    i += 4;
                    break;
                default: throw new FormatException("bad escape at " + (i - 1));
            }
        }
    }

    static object ReadNumber(string s, ref int i)
    {
        int start = i;
        if (s[i] == '-') i++;
        if (i >= s.Length || s[i] < '0' || s[i] > '9') throw new FormatException("bad number at " + start);
        if (s[i] == '0') i++;
        else while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
        bool whole = true;
        if (i < s.Length && s[i] == '.')
        {
            whole = false;
            i++;
            int digits = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (i == digits) throw new FormatException("bad number at " + start);
        }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            whole = false;
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            int digits = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (i == digits) throw new FormatException("bad number at " + start);
        }
        var raw = s.Substring(start, i - start);
        long l;
        if (whole && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
        return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // ---- Typed reading ----

    public static string Str(Dictionary<string, object> o, string key)
    {
        object v;
        return o != null && o.TryGetValue(key, out v) ? v as string : null;
    }

    // A whole number at [key], also when it was written as a whole double or a numeric string; null otherwise.
    public static long? Long(Dictionary<string, object> o, string key)
    {
        object v;
        if (o == null || !o.TryGetValue(key, out v) || v == null) return null;
        if (v is long) return (long)v;
        if (v is double)
        {
            var d = (double)v;
            if (d == Math.Floor(d) && Math.Abs(d) < 9e15) return (long)d;
            return null;
        }
        var s = v as string;
        long l;
        if (s != null && long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
        return null;
    }

    public static bool? Bool(Dictionary<string, object> o, string key)
    {
        object v;
        if (o == null || !o.TryGetValue(key, out v) || v == null) return null;
        if (v is bool) return (bool)v;
        if (v is long) return (long)v != 0;
        return null;
    }

    public static Dictionary<string, object> Obj(Dictionary<string, object> o, string key)
    {
        object v;
        return o != null && o.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
    }

    public static List<object> Arr(Dictionary<string, object> o, string key)
    {
        object v;
        return o != null && o.TryGetValue(key, out v) ? v as List<object> : null;
    }

    // ---- Writing ----

    public static string Write(object value, bool indent)
    {
        var b = new StringBuilder();
        WriteValue(b, value, indent, 0);
        return b.ToString();
    }

    static void WriteValue(StringBuilder b, object v, bool indent, int depth)
    {
        if (v == null) { b.Append("null"); return; }
        var s = v as string;
        if (s != null) { WriteString(b, s); return; }
        if (v is bool) { b.Append((bool)v ? "true" : "false"); return; }
        if (v is int || v is long || v is short || v is byte)
        {
            b.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            return;
        }
        if (v is double || v is float)
        {
            var d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d)) { b.Append("null"); return; }
            b.Append(d.ToString("R", CultureInfo.InvariantCulture));
            return;
        }
        var map = v as IEnumerable<KeyValuePair<string, object>>;
        if (map != null) { WriteObject(b, map, indent, depth); return; }
        var list = v as IEnumerable;
        if (list != null) { WriteArray(b, list, indent, depth); return; }
        throw new ArgumentException("cannot write " + v.GetType().Name + " as JSON");
    }

    static void WriteObject(StringBuilder b, IEnumerable<KeyValuePair<string, object>> map, bool indent, int depth)
    {
        b.Append('{');
        bool first = true;
        foreach (var kv in map)
        {
            if (!first) b.Append(',');
            first = false;
            NewLine(b, indent, depth + 1);
            WriteString(b, kv.Key);
            b.Append(indent ? ": " : ":");
            WriteValue(b, kv.Value, indent, depth + 1);
        }
        if (!first) NewLine(b, indent, depth);
        b.Append('}');
    }

    static void WriteArray(StringBuilder b, IEnumerable list, bool indent, int depth)
    {
        b.Append('[');
        bool first = true;
        foreach (var item in list)
        {
            if (!first) b.Append(',');
            first = false;
            NewLine(b, indent, depth + 1);
            WriteValue(b, item, indent, depth + 1);
        }
        if (!first) NewLine(b, indent, depth);
        b.Append(']');
    }

    static void NewLine(StringBuilder b, bool indent, int depth)
    {
        if (!indent) return;
        b.Append('\n');
        b.Append(' ', depth * 2);
    }

    public static void WriteString(StringBuilder b, string s)
    {
        b.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                case '\b': b.Append("\\b"); break;
                case '\f': b.Append("\\f"); break;
                default:
                    // Control characters, and half of a surrogate pair standing alone (UTF-8 cannot carry it), as escapes
                    bool lone = char.IsHighSurrogate(c) ? !(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        : char.IsLowSurrogate(c) && !(i > 0 && char.IsHighSurrogate(s[i - 1]));
                    if (c < 0x20 || lone) b.Append("\\u").Append(((int)c).ToString("x4"));
                    else b.Append(c);
                    break;
            }
        }
        b.Append('"');
    }
}
