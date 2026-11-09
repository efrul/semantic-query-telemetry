using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Reads an app's SQLite database through the SQLite that comes with Windows 10 and 11 (winsqlite3.dll; on Linux, for the
// tests, libsqlite3 through Mono's dllmap). It never opens the app's own file: it copies the database and its write-ahead
// log to a folder of its own and reads the copy, so the app, even while open, is neither locked nor changed.
public static class Sqlite
{
    const string Lib = "winsqlite3.dll";
    const int OK = 0, ROW = 100, DONE = 101;
    const int OPEN_READONLY = 0x1, OPEN_READWRITE = 0x2, OPEN_CREATE = 0x4, OPEN_URI = 0x40, OPEN_NOMUTEX = 0x8000;
    const int INTEGER = 1, FLOAT = 2, TEXT = 3, BLOB = 4, NULL = 5;

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_name(IntPtr stmt, int col);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_type(IntPtr stmt, int col);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern double sqlite3_column_double(IntPtr stmt, int col);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_errmsg(IntPtr db);

    public sealed class Copy : IDisposable
    {
        readonly string dir;
        public readonly IntPtr Db;

        internal Copy(string dir, IntPtr db) { this.dir = dir; Db = db; }

        public void Dispose()
        {
            sqlite3_close_v2(Db);
            if (dir == null) return;
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // A copy of the database at [path], open, or null (with [error]) when it is not one.
    public static Copy Open(string path, out string error)
    {
        error = null;
        var dir = Path.Combine(Path.GetTempPath(), "search-data-sender-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, "db.sqlite");
            CopyShared(path, copy);
            if (File.Exists(path + "-wal")) CopyShared(path + "-wal", copy + "-wal");
            IntPtr db;
            if (sqlite3_open_v2(Z(copy), out db, OPEN_READWRITE | OPEN_NOMUTEX, IntPtr.Zero) != OK)
            {
                error = db != IntPtr.Zero ? Message(db) : "not a database";
                if (db != IntPtr.Zero) sqlite3_close_v2(db);
                Directory.Delete(dir, true);
                return null;
            }
            sqlite3_busy_timeout(db, 2000);
            return new Copy(dir, db);
        }
        catch (Exception e)
        {
            if (!(e is IOException || e is UnauthorizedAccessException || e is DllNotFoundException || e is EntryPointNotFoundException)) throw;
            error = e.Message;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return null;
        }
    }

    // Reads [path] even while the app holds it open for writing.
    static void CopyShared(string from, string to)
    {
        using (var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var dst = new FileStream(to, FileMode.CreateNew, FileAccess.Write))
            src.CopyTo(dst);
    }

    // A large database the app does not change while it runs (a library's seforim.db), opened in place, read only and
    // without locking (SQLite's immutable mode): copying it would take long. For one small query only.
    public static Copy OpenImmutable(string path, out string error)
    {
        error = null;
        if (!File.Exists(path)) { error = "no such file"; return null; }
        var full = Path.GetFullPath(path).Replace('\\', '/');
        var uri = "file://" + (full.StartsWith("/", StringComparison.Ordinal) ? "" : "/") + UriPath(full) + "?mode=ro&immutable=1";
        IntPtr db;
        try
        {
            if (sqlite3_open_v2(Z(uri), out db, OPEN_READONLY | OPEN_URI | OPEN_NOMUTEX, IntPtr.Zero) != OK)
            {
                error = db != IntPtr.Zero ? Message(db) : "not a database";
                if (db != IntPtr.Zero) sqlite3_close_v2(db);
                return null;
            }
        }
        catch (DllNotFoundException e) { error = e.Message; return null; }
        return new Copy(null, db);
    }

    // The rows [sql] gives, each a column name to a long, double, string, byte[] or null; null with [error] on failure.
    public static List<Dictionary<string, object>> Query(Copy db, string sql, out string error)
    {
        error = null;
        IntPtr stmt;
        var bytes = Z(sql);
        if (sqlite3_prepare_v2(db.Db, bytes, bytes.Length, out stmt, IntPtr.Zero) != OK)
        {
            error = Message(db.Db);
            return null;
        }
        var rows = new List<Dictionary<string, object>>();
        try
        {
            int cols = sqlite3_column_count(stmt);
            var names = new string[cols];
            for (int c = 0; c < cols; c++) names[c] = Utf8(sqlite3_column_name(stmt, c), -1);
            int rc;
            while ((rc = sqlite3_step(stmt)) == ROW)
            {
                var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < cols; c++) row[names[c]] = Value(stmt, c);
                rows.Add(row);
            }
            if (rc != DONE) { error = Message(db.Db); return null; }
        }
        finally { sqlite3_finalize(stmt); }
        return rows;
    }

    // The columns of [table], lower case; empty when there is no such table.
    public static HashSet<string> Columns(Copy db, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string error;
        var rows = Query(db, "PRAGMA table_info(\"" + table.Replace("\"", "\"\"") + "\")", out error);
        if (rows != null) foreach (var r in rows) { var n = r["name"] as string; if (n != null) set.Add(n); }
        return set;
    }

    // Runs [sql] on the database file at [path] itself, creating it: for the tests' made-up databases only.
    public static string Exec(string path, string sql)
    {
        IntPtr db;
        if (sqlite3_open_v2(Z(path), out db, OPEN_READWRITE | OPEN_CREATE | OPEN_NOMUTEX, IntPtr.Zero) != OK) return "cannot create " + path;
        try
        {
            foreach (var statement in sql.Split(new[] { ";\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (statement.Trim().Length == 0) continue;
                IntPtr stmt;
                var bytes = Z(statement);
                if (sqlite3_prepare_v2(db, bytes, bytes.Length, out stmt, IntPtr.Zero) != OK) return Message(db);
                int rc = sqlite3_step(stmt);
                sqlite3_finalize(stmt);
                if (rc != DONE && rc != ROW) return Message(db);
            }
            return null;
        }
        finally { sqlite3_close_v2(db); }
    }

    static object Value(IntPtr stmt, int c)
    {
        switch (sqlite3_column_type(stmt, c))
        {
            case INTEGER: return sqlite3_column_int64(stmt, c);
            case FLOAT: return sqlite3_column_double(stmt, c);
            case TEXT: return Utf8(sqlite3_column_text(stmt, c), sqlite3_column_bytes(stmt, c));
            case BLOB: return null;
            default: return null;
        }
    }

    static string Message(IntPtr db) { return Utf8(sqlite3_errmsg(db), -1); }

    // A path for a file: URI, every byte but letters, digits and -._~/: percent-encoded (Hebrew folders included)
    static string UriPath(string path)
    {
        var b = new StringBuilder();
        foreach (var x in Encoding.UTF8.GetBytes(path))
        {
            char c = (char)x;
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || "-._~/:".IndexOf(c) >= 0) b.Append(c);
            else b.Append('%').Append(x.ToString("X2"));
        }
        return b.ToString();
    }

    static byte[] Z(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        Array.Resize(ref b, b.Length + 1);
        return b;
    }

    static string Utf8(IntPtr p, int length)
    {
        if (p == IntPtr.Zero) return null;
        if (length < 0) { length = 0; while (Marshal.ReadByte(p, length) != 0) length++; }
        var bytes = new byte[length];
        Marshal.Copy(p, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }
}
