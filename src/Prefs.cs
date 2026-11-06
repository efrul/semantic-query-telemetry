using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

// Stores telemetry tool settings under HKCU\Software\ZayitTelemetry.
// Reads Zayit's theme, accent, and database_path from HKCU\Software\JavaSoft\Prefs.
static class Prefs
{
    public const string ToolKey = @"Software\ZayitTelemetry";
    public const string ZayitKey = @"Software\JavaSoft\Prefs";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "ZayitTelemetry";

    // Overridden in SelfTest so tests never touch the user's real registry settings.
    public static string TestRoot = null;

    static string RootKey()
    {
        if (TestRoot != null) return TestRoot;
        return ToolKey;
    }

    public static string Get(string name, string def = "")
    {
        // 1. Tool-specific registry key (plain string)
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RootKey()))
            {
                if (k != null)
                {
                    var val = k.GetValue(name) as string;
                    if (val != null) return val;
                }
            }
        }
        catch { }

        // 2. Fallback to JavaSoft\Prefs (for Zayit's own theme_mode, accent_color, database_path, or migrated sender)
        if (TestRoot == null)
        {
            try
            {
                using (var kz = Registry.CurrentUser.OpenSubKey(ZayitKey))
                {
                    if (kz != null)
                    {
                        var raw = kz.GetValue(name) as string;
                        if (raw != null) return FromJava(raw);
                    }
                }
            }
            catch { }
        }

        return def;
    }

    public static string GetFromNode(bool pardes, string name, string def = "")
    {
        // Never read from Pardes. Zayit settings come from Get().
        if (pardes) return def;
        return Get(name, def);
    }

    public static bool Set(string name, string value, bool alsoPardes = false)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RootKey()))
            {
                if (k == null) return false;
                k.SetValue(name, value ?? "", RegistryValueKind.String);
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
        Set(name, val ? "true" : "false");
    }

    public static long GetLong(string name, long def = 0L)
    {
        long v;
        return long.TryParse(Get(name, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
    }

    public static void SetLong(string name, long val)
    {
        Set(name, val.ToString(CultureInfo.InvariantCulture));
    }

    // The random UUID v4 sender id for this computer.
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
        Set("feedback_sender", s);
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

    // Per-source sent-at timestamps (epoch milliseconds).
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

    // Cumulative telemetry statistics across time
    public static void RecordSentStats(int searches, int grades, int dafMarks)
    {
        long totalSearches = GetLong("feedback_total_sent_searches", 0L) + searches;
        long totalGrades = GetLong("feedback_total_sent_grades", 0L) + grades;
        long totalDaf = GetLong("feedback_total_sent_daf", 0L) + dafMarks;
        long totalBatches = GetLong("feedback_total_sent_batches", 0L) + 1L;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        SetLong("feedback_total_sent_searches", totalSearches);
        SetLong("feedback_total_sent_grades", totalGrades);
        SetLong("feedback_total_sent_daf", totalDaf);
        SetLong("feedback_total_sent_batches", totalBatches);
        SetLong("feedback_last_sent_at", now);
        if (GetLong("feedback_first_sent_at", 0L) <= 0L)
            SetLong("feedback_first_sent_at", now);
    }

    public static long GetTotalSentSearches() { return GetLong("feedback_total_sent_searches", 0L); }
    public static long GetTotalSentGrades() { return GetLong("feedback_total_sent_grades", 0L); }
    public static long GetTotalSentDaf() { return GetLong("feedback_total_sent_daf", 0L); }
    public static long GetTotalSentBatches() { return GetLong("feedback_total_sent_batches", 0L); }
    public static long GetFirstSentAt() { return GetLong("feedback_first_sent_at", 0L); }
    public static long GetLastSentAt() { return GetLong("feedback_last_sent_at", 0L); }

    public static void ResetSentStats()
    {
        SetLong("feedback_total_sent_searches", 0L);
        SetLong("feedback_total_sent_grades", 0L);
        SetLong("feedback_total_sent_daf", 0L);
        SetLong("feedback_total_sent_batches", 0L);
        SetLong("feedback_first_sent_at", 0L);
        SetLong("feedback_last_sent_at", 0L);
    }

    // Auto-send is enabled by default to send search data automatically.
    public static bool IsAutoSendEnabled()
    {
        return GetBool("feedback_auto_send", true);
    }

    public static void SetAutoSendEnabled(bool enabled)
    {
        SetBool("feedback_auto_send", enabled);
        if (TestRoot != null) return;
        string targetExe = GetInstalledOrRunningExe();

        // 1. Windows Startup Registry Key (HKCU\Software\Microsoft\Windows\CurrentVersion\Run)
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k != null)
                {
                    if (enabled)
                    {
                        if (!string.IsNullOrEmpty(targetExe) && File.Exists(targetExe))
                            k.SetValue(RunValueName, "\"" + targetExe + "\" --auto", RegistryValueKind.String);
                    }
                    else
                    {
                        k.DeleteValue(RunValueName, false);
                    }
                }
            }
        }
        catch { }

        // 2. Windows Task Scheduler Event Trigger on Network Connection (Event 10000 in NetworkProfile/Operational)
        try
        {
            UpdateScheduledTask(targetExe, enabled);
        }
        catch { }
    }

    static void UpdateScheduledTask(string targetExe, bool enabled)
    {
        const string taskName = "ZayitTelemetryNetworkSync";
        try
        {
            if (!enabled)
            {
                var psiDel = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/delete /tn \"" + taskName + "\" /f",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psiDel))
                {
                    if (p != null) p.WaitForExit(5000);
                }
                return;
            }

            if (string.IsNullOrEmpty(targetExe) || !File.Exists(targetExe)) return;

            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <Triggers>\r\n" +
                "    <EventTrigger>\r\n" +
                "      <Enabled>true</Enabled>\r\n" +
                "      <Subscription>&lt;QueryList&gt;&lt;Query Id=\"0\" Path=\"Microsoft-Windows-NetworkProfile/Operational\"&gt;&lt;Select Path=\"Microsoft-Windows-NetworkProfile/Operational\"&gt;*[System[(EventID=10000)]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>\r\n" +
                "    </EventTrigger>\r\n" +
                "  </Triggers>\r\n" +
                "  <Principals>\r\n" +
                "    <Principal>\r\n" +
                "      <LogonType>InteractiveToken</LogonType>\r\n" +
                "      <RunLevel>LeastPrivilege</RunLevel>\r\n" +
                "    </Principal>\r\n" +
                "  </Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\">\r\n" +
                "    <Exec>\r\n" +
                "      <Command>\"" + targetExe.Replace("\"", "") + "\"</Command>\r\n" +
                "      <Arguments>--auto</Arguments>\r\n" +
                "    </Exec>\r\n" +
                "  </Actions>\r\n" +
                "</Task>";

            string tempXml = Path.Combine(Path.GetTempPath(), "zt_task_" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(tempXml, xml, Encoding.Unicode);
            try
            {
                var psiAdd = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/create /tn \"" + taskName + "\" /xml \"" + tempXml + "\" /f",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psiAdd))
                {
                    if (p != null) p.WaitForExit(5000);
                }
            }
            finally
            {
                try { if (File.Exists(tempXml)) File.Delete(tempXml); } catch { }
            }
        }
        catch { }
    }

    public static string GetInstalledOrRunningExe()
    {
        try
        {
            var running = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(running) || !File.Exists(running)) return running;

            // Safe permanent location in %LOCALAPPDATA%\Programs\ZayitTelemetry\ZayitTelemetry.exe
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var permDir = Path.Combine(localApp, "Programs", "ZayitTelemetry");
            var permExe = Path.Combine(permDir, "ZayitTelemetry.exe");

            if (string.Equals(Path.GetFullPath(running), Path.GetFullPath(permExe), StringComparison.OrdinalIgnoreCase))
                return permExe;

            Directory.CreateDirectory(permDir);
            File.Copy(running, permExe, true);
            return permExe;
        }
        catch
        {
            return Assembly.GetExecutingAssembly().Location;
        }
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
