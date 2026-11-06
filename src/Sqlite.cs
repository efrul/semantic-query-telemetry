using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Read-only SQLite 3 reader with zero external dependencies:
// 1. Uses Windows 10/11's built-in C:\Windows\System32\winsqlite3.dll when available.
// 2. Includes a pure-C# SQLite format 3 B-tree + WAL reader fallback so databases can be read even when locked or without winsqlite3.dll.
static class Sqlite
{
    public const int SQLITE_OK = 0;
    public const int SQLITE_ROW = 100;
    public const int SQLITE_DONE = 101;
    const int SQLITE_OPEN_READONLY = 0x00000001;
    const int SQLITE_OPEN_READWRITE = 0x00000002;
    const int SQLITE_OPEN_CREATE = 0x00000004;
    const int SQLITE_OPEN_NOMUTEX = 0x00008000;

    const int SQLITE_INTEGER = 1;
    const int SQLITE_FLOAT = 2;
    const int SQLITE_TEXT = 3;
    const int SQLITE_BLOB = 4;
    const int SQLITE_NULL = 5;

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_close_v2(IntPtr db);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_column_count(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr sqlite3_column_name(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_column_type(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern double sqlite3_column_double(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr sqlite3_column_blob(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

    static byte[] ZStr(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        var z = new byte[b.Length + 1];
        Buffer.BlockCopy(b, 0, z, 0, b.Length);
        return z;
    }

    static string FromZPtr(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        int len = 0;
        while (Marshal.ReadByte(p, len) != 0) len++;
        if (len == 0) return "";
        var buf = new byte[len];
        Marshal.Copy(p, buf, 0, len);
        return Encoding.UTF8.GetString(buf);
    }

    // Executes a read-only SQL query via winsqlite3.dll, returning null if winsqlite3 is unavailable or fails.
    public static List<Dictionary<string, object>> QueryNative(string dbPath, string sql)
    {
        if (!File.Exists(dbPath)) return null;
        IntPtr db = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(ZStr(dbPath), out db, SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX, IntPtr.Zero) != SQLITE_OK)
                return null;
            sqlite3_busy_timeout(db, 2000);
            IntPtr stmt;
            var sqlBytes = ZStr(sql);
            if (sqlite3_prepare_v2(db, sqlBytes, sqlBytes.Length - 1, out stmt, IntPtr.Zero) != SQLITE_OK)
                return null;
            try
            {
                var rows = new List<Dictionary<string, object>>();
                int cols = sqlite3_column_count(stmt);
                var names = new string[cols];
                for (int i = 0; i < cols; i++) names[i] = FromZPtr(sqlite3_column_name(stmt, i));
                while (sqlite3_step(stmt) == SQLITE_ROW)
                {
                    var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < cols; i++)
                    {
                        int t = sqlite3_column_type(stmt, i);
                        if (t == SQLITE_NULL) row[names[i]] = null;
                        else if (t == SQLITE_INTEGER) row[names[i]] = sqlite3_column_int64(stmt, i);
                        else if (t == SQLITE_FLOAT) row[names[i]] = sqlite3_column_double(stmt, i);
                        else if (t == SQLITE_TEXT)
                        {
                            int n = sqlite3_column_bytes(stmt, i);
                            if (n <= 0) row[names[i]] = "";
                            else
                            {
                                var p = sqlite3_column_blob(stmt, i);
                                var buf = new byte[n];
                                Marshal.Copy(p, buf, 0, n);
                                row[names[i]] = Encoding.UTF8.GetString(buf);
                            }
                        }
                        else if (t == SQLITE_BLOB)
                        {
                            int n = sqlite3_column_bytes(stmt, i);
                            var buf = new byte[Math.Max(0, n)];
                            if (n > 0) Marshal.Copy(sqlite3_column_blob(stmt, i), buf, 0, n);
                            row[names[i]] = buf;
                        }
                    }
                    rows.Add(row);
                }
                return rows;
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (db != IntPtr.Zero) try { sqlite3_close_v2(db); } catch { }
        }
    }

    // Executes SQL statements on a writable database file (used by SelfTest to build realistic test databases).
    public static bool ExecNative(string dbPath, params string[] statements)
    {
        IntPtr db = IntPtr.Zero;
        try
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (sqlite3_open_v2(ZStr(dbPath), out db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_NOMUTEX, IntPtr.Zero) != SQLITE_OK)
                return false;
            foreach (var sql in statements)
            {
                IntPtr stmt;
                var sqlBytes = ZStr(sql);
                if (sqlite3_prepare_v2(db, sqlBytes, sqlBytes.Length - 1, out stmt, IntPtr.Zero) != SQLITE_OK)
                    return false;
                try
                {
                    int rc = sqlite3_step(stmt);
                    if (rc != SQLITE_DONE && rc != SQLITE_ROW) return false;
                }
                finally { sqlite3_finalize(stmt); }
            }
            return true;
        }
        catch { return false; }
        finally
        {
            if (db != IntPtr.Zero) try { sqlite3_close_v2(db); } catch { }
        }
    }

    // Reads all rows of a table (using winsqlite3.dll first, or the pure-C# B-tree reader fallback).
    public static List<Dictionary<string, object>> ReadTable(string dbPath, string tableName, string nativeSql = null, bool forceManaged = false)
    {
        if (!File.Exists(dbPath)) return new List<Dictionary<string, object>>();
        if (!forceManaged)
        {
            var rows = QueryNative(dbPath, nativeSql ?? ("SELECT * FROM " + tableName));
            if (rows != null) return rows;
        }
        return ManagedReader.ReadTable(dbPath, tableName);
    }

    // Pure-C# SQLite format 3 B-tree + WAL reader fallback.
    public static class ManagedReader
    {
        public static List<Dictionary<string, object>> ReadTable(string dbPath, string tableName)
        {
            var result = new List<Dictionary<string, object>>();
            try
            {
                byte[] dbBytes;
                using (var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    dbBytes = new byte[fs.Length];
                    int read = 0;
                    while (read < dbBytes.Length)
                    {
                        int n = fs.Read(dbBytes, read, dbBytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                if (dbBytes.Length < 100 || Encoding.ASCII.GetString(dbBytes, 0, 15) != "SQLite format 3")
                    return result;

                int pageSize = (dbBytes[16] << 8) | dbBytes[17];
                if (pageSize == 1) pageSize = 65536;
                int reserved = dbBytes[20];
                int usableSize = pageSize - reserved;
                var walPages = ReadWalPages(dbPath + "-wal", pageSize);

                Func<int, byte[]> getPage = (pgNo) =>
                {
                    if (pgNo <= 0) return null;
                    byte[] walPage;
                    if (walPages != null && walPages.TryGetValue(pgNo, out walPage)) return walPage;
                    long off = (long)(pgNo - 1) * pageSize;
                    if (off + pageSize > dbBytes.Length) return null;
                    var p = new byte[pageSize];
                    Buffer.BlockCopy(dbBytes, (int)off, p, 0, pageSize);
                    return p;
                };

                // Page 1 is sqlite_master: columns (type, name, tbl_name, rootpage, sql)
                var masterRows = new List<object[]>();
                WalkTableBTree(getPage, 1, usableSize, masterRows);
                int rootPage = -1;
                string createSql = null;
                foreach (var r in masterRows)
                {
                    if (r.Length >= 5 && string.Equals(r[0] as string, "table", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r[1] as string, tableName, StringComparison.OrdinalIgnoreCase))
                    {
                        rootPage = Convert.ToInt32(r[3]);
                        createSql = r[4] as string;
                        break;
                    }
                }
                if (rootPage <= 0 || string.IsNullOrEmpty(createSql)) return result;

                var colNames = ParseColumns(createSql);
                var tableRows = new List<object[]>();
                WalkTableBTree(getPage, rootPage, usableSize, tableRows);
                foreach (var vals in tableRows)
                {
                    var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    int count = Math.Min(colNames.Count, vals.Length);
                    for (int i = 0; i < count; i++) d[colNames[i]] = vals[i];
                    result.Add(d);
                }
            }
            catch { }
            return result;
        }

        static Dictionary<int, byte[]> ReadWalPages(string walPath, int pageSize)
        {
            if (!File.Exists(walPath)) return null;
            try
            {
                byte[] wal;
                using (var fs = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < 32 + 24 + pageSize) return null;
                    wal = new byte[fs.Length];
                    int r = 0;
                    while (r < wal.Length)
                    {
                        int n = fs.Read(wal, r, wal.Length - r);
                        if (n <= 0) break;
                        r += n;
                    }
                }
                uint magic = ReadU32(wal, 0);
                if (magic != 0x377f0682u && magic != 0x377f0683u) return null;
                int walPageSize = (int)ReadU32(wal, 8);
                if (walPageSize != pageSize) return null;
                uint salt1 = ReadU32(wal, 16);
                uint salt2 = ReadU32(wal, 20);
                var committed = new Dictionary<int, byte[]>();
                var pending = new Dictionary<int, byte[]>();
                int pos = 32;
                int frameSize = 24 + pageSize;
                while (pos + frameSize <= wal.Length)
                {
                    int pgNo = (int)ReadU32(wal, pos);
                    uint dbSizeAfterCommit = ReadU32(wal, pos + 4);
                    uint fSalt1 = ReadU32(wal, pos + 8);
                    uint fSalt2 = ReadU32(wal, pos + 12);
                    if (fSalt1 != salt1 || fSalt2 != salt2 || pgNo <= 0) break;
                    var page = new byte[pageSize];
                    Buffer.BlockCopy(wal, pos + 24, page, 0, pageSize);
                    pending[pgNo] = page;
                    if (dbSizeAfterCommit > 0)
                    {
                        foreach (var kv in pending) committed[kv.Key] = kv.Value;
                        pending.Clear();
                    }
                    pos += frameSize;
                }
                return committed.Count > 0 ? committed : null;
            }
            catch { return null; }
        }

        static void WalkTableBTree(Func<int, byte[]> getPage, int pageNo, int usableSize, List<object[]> rows)
        {
            var page = getPage(pageNo);
            if (page == null) return;
            int hdrOff = pageNo == 1 ? 100 : 0;
            byte pageType = page[hdrOff];
            int cellCount = (page[hdrOff + 3] << 8) | page[hdrOff + 4];
            if (pageType == 0x05) // Interior table B-tree page
            {
                int ptrOff = hdrOff + 12;
                for (int i = 0; i < cellCount; i++)
                {
                    int cellOff = (page[ptrOff + i * 2] << 8) | page[ptrOff + i * 2 + 1];
                    int leftChild = (int)ReadU32(page, cellOff);
                    WalkTableBTree(getPage, leftChild, usableSize, rows);
                }
                int rightChild = (int)ReadU32(page, hdrOff + 8);
                WalkTableBTree(getPage, rightChild, usableSize, rows);
            }
            else if (pageType == 0x0D) // Leaf table B-tree page
            {
                int ptrOff = hdrOff + 8;
                for (int i = 0; i < cellCount; i++)
                {
                    int cellOff = (page[ptrOff + i * 2] << 8) | page[ptrOff + i * 2 + 1];
                    int pos = cellOff;
                    long payloadLen = ReadVarint(page, ref pos);
                    long rowId = ReadVarint(page, ref pos);
                    byte[] payload = ReadPayload(getPage, page, pos, (int)payloadLen, usableSize);
                    if (payload != null) rows.Add(ParseRecord(payload, rowId));
                }
            }
        }

        static byte[] ReadPayload(Func<int, byte[]> getPage, byte[] page, int startPos, int P, int U)
        {
            int X = U - 35;
            if (P <= X)
            {
                if (startPos + P > page.Length) return null;
                var buf = new byte[P];
                Buffer.BlockCopy(page, startPos, buf, 0, P);
                return buf;
            }
            int M = ((U - 12) * 32 / 255) - 23;
            int K = M + ((P - M) % (U - 4));
            int localLen = K <= X ? K : M;
            if (startPos + localLen + 4 > page.Length) return null;
            var full = new byte[P];
            Buffer.BlockCopy(page, startPos, full, 0, localLen);
            int copied = localLen;
            int ovfPage = (int)ReadU32(page, startPos + localLen);
            while (ovfPage > 0 && copied < P)
            {
                var op = getPage(ovfPage);
                if (op == null) break;
                ovfPage = (int)ReadU32(op, 0);
                int chunk = Math.Min(P - copied, U - 4);
                Buffer.BlockCopy(op, 4, full, copied, chunk);
                copied += chunk;
            }
            return full;
        }

        static object[] ParseRecord(byte[] payload, long rowId)
        {
            int pos = 0;
            long hdrEnd = ReadVarint(payload, ref pos);
            var types = new List<long>();
            while (pos < hdrEnd && pos < payload.Length)
                types.Add(ReadVarint(payload, ref pos));
            pos = (int)hdrEnd;
            var vals = new object[types.Count];
            for (int i = 0; i < types.Count; i++)
            {
                long t = types[i];
                if (t == 0) vals[i] = i == 0 ? (object)rowId : null;
                else if (t == 1) { vals[i] = (long)(sbyte)payload[pos]; pos += 1; }
                else if (t == 2) { vals[i] = (long)(short)((payload[pos] << 8) | payload[pos + 1]); pos += 2; }
                else if (t == 3)
                {
                    int v = (payload[pos] << 16) | (payload[pos + 1] << 8) | payload[pos + 2];
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    vals[i] = (long)v; pos += 3;
                }
                else if (t == 4) { vals[i] = (long)(int)ReadU32(payload, pos); pos += 4; }
                else if (t == 5)
                {
                    long v = 0;
                    for (int b = 0; b < 6; b++) v = (v << 8) | payload[pos + b];
                    if ((v & 0x800000000000L) != 0) v |= unchecked((long)0xFFFF000000000000UL);
                    vals[i] = v; pos += 6;
                }
                else if (t == 6)
                {
                    long v = 0;
                    for (int b = 0; b < 8; b++) v = (v << 8) | payload[pos + b];
                    vals[i] = v; pos += 8;
                }
                else if (t == 7)
                {
                    var tmp = new byte[8];
                    for (int b = 0; b < 8; b++) tmp[7 - b] = payload[pos + b];
                    vals[i] = BitConverter.ToDouble(tmp, 0); pos += 8;
                }
                else if (t == 8) vals[i] = 0L;
                else if (t == 9) vals[i] = 1L;
                else if (t >= 12 && (t & 1) == 0)
                {
                    int len = (int)((t - 12) / 2);
                    var b = new byte[len];
                    if (pos + len <= payload.Length) Buffer.BlockCopy(payload, pos, b, 0, len);
                    vals[i] = b; pos += len;
                }
                else if (t >= 13 && (t & 1) == 1)
                {
                    int len = (int)((t - 13) / 2);
                    vals[i] = pos + len <= payload.Length ? Encoding.UTF8.GetString(payload, pos, len) : "";
                    pos += len;
                }
            }
            return vals;
        }

        static List<string> ParseColumns(string createSql)
        {
            var cols = new List<string>();
            int open = createSql.IndexOf('(');
            int close = createSql.LastIndexOf(')');
            if (open < 0 || close <= open) return cols;
            string body = createSql.Substring(open + 1, close - open - 1);
            int depth = 0;
            int start = 0;
            for (int i = 0; i <= body.Length; i++)
            {
                char c = i < body.Length ? body[i] : ',';
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    string part = body.Substring(start, i - start).Trim();
                    start = i + 1;
                    if (string.IsNullOrEmpty(part)) continue;
                    string tok = FirstToken(part);
                    if (string.IsNullOrEmpty(tok)) continue;
                    string upper = tok.ToUpperInvariant();
                    if (upper == "PRIMARY" || upper == "UNIQUE" || upper == "CHECK" || upper == "FOREIGN" || upper == "CONSTRAINT")
                        continue;
                    cols.Add(tok.Trim('"', '\'', '`', '[', ']'));
                }
            }
            return cols;
        }

        static string FirstToken(string s)
        {
            s = s.TrimStart();
            if (string.IsNullOrEmpty(s)) return "";
            if (s[0] == '"' || s[0] == '`' || s[0] == '[')
            {
                char end = s[0] == '[' ? ']' : s[0];
                int idx = s.IndexOf(end, 1);
                if (idx > 0) return s.Substring(1, idx - 1);
            }
            int k = 0;
            while (k < s.Length && !char.IsWhiteSpace(s[k])) k++;
            return s.Substring(0, k);
        }

        static uint ReadU32(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }

        static long ReadVarint(byte[] b, ref int p)
        {
            long v = 0;
            for (int i = 0; i < 8 && p < b.Length; i++)
            {
                byte c = b[p++];
                v = (v << 7) | (long)(c & 0x7F);
                if ((c & 0x80) == 0) return v;
            }
            if (p < b.Length) v = (v << 8) | b[p++];
            return v;
        }
    }
}
