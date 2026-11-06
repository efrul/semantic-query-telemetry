using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

static class Service
{
    static Service()
    {
        try
        {
            // Ensure TLS 1.2 (and TLS 1.3 where supported on Windows 11) is enabled on .NET Framework 4.8
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
        }
        catch
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }
    }

    public const string DEFAULT_URL = "https://pardes-feedback.pardes.workers.dev";

    public static string ResolveUrl()
    {
        var env = Environment.GetEnvironmentVariable("PARDES_FEEDBACK_URL");
        if (!string.IsNullOrEmpty(env) && IsHttpUrl(env)) return env.Trim().TrimEnd('/');

        var pref = Prefs.Get("feedback_url", "");
        if (!string.IsNullOrEmpty(pref) && IsHttpUrl(pref)) return pref.Trim().TrimEnd('/');

        try
        {
            var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(exeDir))
            {
                var localFile = Path.Combine(exeDir, "feedback.url");
                if (File.Exists(localFile))
                {
                    var u = File.ReadAllText(localFile, Encoding.UTF8).Trim();
                    if (IsHttpUrl(u)) return u.TrimEnd('/');
                }
            }
        }
        catch { }

        var embedded = Theme.Resource("feedback-url.txt");
        if (embedded != null && embedded.Length > 0)
        {
            var u = Encoding.UTF8.GetString(embedded).Trim('\uFEFF', ' ', '\t', '\r', '\n');
            if (IsHttpUrl(u)) return u.TrimEnd('/');
        }

        // If Pardes for desktop is installed on this PC, extract its compiled FEEDBACK_URL from its binary.
        var fromPardes = DiscoverFromInstalledPardes();
        if (!string.IsNullOrEmpty(fromPardes))
        {
            Prefs.Set("feedback_url", fromPardes);
            return fromPardes;
        }

        // Built-in default feedback service endpoint
        return DEFAULT_URL;
    }

    public static bool IsConfigured
    {
        get { return !string.IsNullOrEmpty(ResolveUrl()); }
    }

    static bool IsHttpUrl(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        return s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    static string DiscoverFromInstalledPardes()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, "Programs", "פרדס", "פרדס.exe"),
                Path.Combine(local, "פרדס", "פרדס.exe"),
                Path.Combine(local, "Programs", "Pardes", "pardes.exe")
            };
            var rx = new Regex(@"https://pardes-feedback\.[a-z0-9\-]+\.workers\.dev", RegexOptions.IgnoreCase);
            foreach (var exe in candidates)
            {
                if (!File.Exists(exe)) continue;
                using (var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buf = new byte[1024 * 1024];
                    int r;
                    string carry = "";
                    while ((r = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        var chunk = carry + Encoding.ASCII.GetString(buf, 0, r);
                        var m = rx.Match(chunk);
                        if (m.Success) return m.Value;
                        carry = chunk.Length > 128 ? chunk.Substring(chunk.Length - 128) : chunk;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    // Sends each package in [packages] to the feedback service. Returns null when all succeed, or a Hebrew error message.
    public static string Send(List<FeedbackPackage> packages, string overrideUrl = null)
    {
        string baseUrl = (overrideUrl ?? ResolveUrl()).Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            return "כתובת שירות המשוב לא הוגדרה.";

        foreach (var pkg in packages)
        {
            if (pkg.IsEmpty) continue;
            string err = PostOne(baseUrl + "/v1/packages", pkg.ToCompactJson());
            if (err != null) return err;
        }
        return null;
    }

    static string PostOne(string url, string jsonBody)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(jsonBody);
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.UserAgent = "ZayitTelemetry/1.0";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            req.ContentLength = bytes.Length;
            using (var s = req.GetRequestStream())
                s.Write(bytes, 0, bytes.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                int code = (int)resp.StatusCode;
                if (code >= 200 && code <= 299) return null;
                return StatusError(code, ReadBody(resp));
            }
        }
        catch (WebException we)
        {
            var resp = we.Response as HttpWebResponse;
            if (resp != null)
            {
                using (resp)
                    return StatusError((int)resp.StatusCode, ReadBody(resp));
            }
            return "לא הצלחתי להתחבר לשירות. בדוק את החיבור לרשת או שמור לקובץ.";
        }
        catch
        {
            return "לא הצלחתי להתחבר לשירות. בדוק את החיבור לרשת או שמור לקובץ.";
        }
    }

    // Deletes every package sent with [sender] from the feedback service (DELETE /v1/senders/<id>).
    public static string DeleteSent(string sender, string overrideUrl = null)
    {
        string baseUrl = (overrideUrl ?? ResolveUrl()).Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            return "כתובת שירות המשוב לא הוגדרה.";
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/v1/senders/" + sender);
            req.Method = "DELETE";
            req.UserAgent = "ZayitTelemetry/1.0";
            req.Timeout = 30000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                int code = (int)resp.StatusCode;
                if (code >= 200 && code <= 299) return null;
                return StatusError(code, ReadBody(resp));
            }
        }
        catch (WebException we)
        {
            var resp = we.Response as HttpWebResponse;
            if (resp != null)
            {
                using (resp)
                    return StatusError((int)resp.StatusCode, ReadBody(resp));
            }
            return "לא הצלחתי להתחבר לשירות. נסה שוב כשיש חיבור לרשת.";
        }
        catch
        {
            return "לא הצלחתי להתחבר לשירות. נסה שוב כשיש חיבור לרשת.";
        }
    }

    static string ReadBody(HttpWebResponse resp)
    {
        try
        {
            using (var s = resp.GetResponseStream())
            {
                if (s == null) return "";
                using (var r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
            }
        }
        catch { return ""; }
    }

    static string StatusError(int status, string body)
    {
        if (status == 413) return "חבילת המשוב גדולה מדי לשליחה אחת.";
        if (status == 429) return "נשלחו יותר מדי חבילות היום. נסה שוב מחר.";
        if (status == 400)
        {
            var obj = Json.ParseObj(body);
            var detail = Json.Str(obj, "error", null);
            return !string.IsNullOrEmpty(detail)
                ? "השירות דחה את החבילה: " + detail + "."
                : "השירות דחה את החבילה (400).";
        }
        return "השירות ענה בשגיאה (" + status + "). אפשר לשמור לקובץ ולנסות שוב אחר כך.";
    }

    // Runs silently when launched with --auto on Windows startup or when network connects.
    public static bool RunAutoOnce(string overrideUrl = null)
    {
        if (!Prefs.IsAutoSendEnabled()) return false;
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) return false;
        }
        catch { }

        string url = overrideUrl ?? ResolveUrl();
        if (string.IsNullOrEmpty(url)) return false;

        bool zayitOn = Prefs.IsSourceEnabled("zayit", true);
        bool otzariaOn = Prefs.IsSourceEnabled("otzaria", true);
        bool searchesOn = Prefs.IsKindEnabled("searches", true);
        bool gradesOn = Prefs.IsKindEnabled("grades", true);
        bool dafOn = Prefs.IsKindEnabled("daf", true);

        var scan = Sources.Scan(false);
        var pkg = Sources.BuildPackage(scan, zayitOn, otzariaOn, searchesOn, gradesOn, dafOn);
        var parts = pkg.Parts();
        if (parts.Count == 0) return false;

        string err = Send(parts, url);
        if (err == null)
        {
            Sources.RecordSent(scan, zayitOn, otzariaOn, searchesOn, gradesOn, dafOn, pkg.Searches.Count, pkg.Grades.Count, pkg.DafMarkCount);
            return true;
        }
        return false;
    }
}
