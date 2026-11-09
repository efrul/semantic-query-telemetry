using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

// The search data service (Pardes's feedback service): POST /v1/packages takes a package, DELETE /v1/senders/<id> deletes
// all a sender sent. Its answers, sorted into what the tool does next.
public enum Outcome
{
    Ok,
    Offline,        // no connection, or no answer: try again later
    Busy,           // 20 requests a minute from this address: wait a minute and go on
    DailyLimit,     // 200 packages today from this sender: try again tomorrow
    RowRefused,     // one row is not one the service takes: leave it out and send the rest
    Refused,        // the service refused the package as a whole
    ServerError
}

public sealed class Answer
{
    public Outcome Outcome;
    public int Status;
    public string Reason = "";      // the service's own words, for a refused package
    public string RowKind;          // "grade", "search" or "daf review", for a refused row
    public int RowIndex = -1;       // from 0

    public bool Ok { get { return Outcome == Outcome.Ok; } }

    public string Message()
    {
        switch (Outcome)
        {
            case Outcome.Ok: return null;
            case Outcome.Offline: return "אין חיבור לשירות. אפשר לנסות שוב כשיש רשת, או לשמור לקובץ.";
            case Outcome.Busy: return "השירות מקבל עד 20 בקשות בדקה מכל כתובת. אפשר לנסות שוב בעוד דקה.";
            case Outcome.DailyLimit: return "מהמחשב הזה כבר נשלחו היום 200 חבילות, המכסה היומית. השאר יישלח מחר.";
            case Outcome.RowRefused:
            case Outcome.Refused: return "השירות לא קיבל את החבילה: " + Reason;
            default: return "השירות ענה בשגיאה (" + Status + "). אפשר לנסות שוב מאוחר יותר.";
        }
    }
}

public interface IService
{
    Answer Post(string json);
    Answer DeleteSender(string sender);
}

public sealed class HttpService : IService
{
    readonly string baseUrl;

    static HttpService()
    {
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (NotSupportedException) { }
    }

    public HttpService(string baseUrl) { this.baseUrl = baseUrl.TrimEnd('/'); }

    public Answer Post(string json)
    {
        return Send("POST", baseUrl + "/v1/packages", Encoding.UTF8.GetBytes(json));
    }

    public Answer DeleteSender(string sender)
    {
        return Send("DELETE", baseUrl + "/v1/senders/" + Uri.EscapeDataString(sender), null);
    }

    static Answer Send(string method, string url, byte[] body)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = AppInfo.UserAgent;
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            if (body != null)
            {
                req.ContentType = "application/json; charset=utf-8";
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            }
            using (var resp = (HttpWebResponse)req.GetResponse())
                return Read((int)resp.StatusCode, ReadBody(resp));
        }
        catch (WebException e)
        {
            var resp = e.Response as HttpWebResponse;
            if (resp == null) return new Answer { Outcome = Outcome.Offline };
            using (resp) return Read((int)resp.StatusCode, ReadBody(resp));
        }
        catch (IOException) { return new Answer { Outcome = Outcome.Offline }; }
    }

    static string ReadBody(HttpWebResponse resp)
    {
        try
        {
            using (var s = resp.GetResponseStream())
            {
                if (s == null) return "";
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }
        catch (IOException) { return ""; }
        catch (WebException) { return ""; }
    }

    // "search 3 is not one (query: 20001 characters, at most 20000)": the row the service refused
    static readonly Regex RowProblem = new Regex(@"^(grade|search|daf review) (\d+) is not one", RegexOptions.CultureInvariant);

    public static Answer Read(int status, string body)
    {
        var a = new Answer { Status = status };
        var reason = Json.Str(Json.TryParseObject(body), "error") ?? "";
        a.Reason = reason;
        if (status >= 200 && status < 300) { a.Outcome = Outcome.Ok; return a; }
        if (status == 429)
        {
            a.Outcome = reason.StartsWith("too many packages today", StringComparison.Ordinal) ? Outcome.DailyLimit : Outcome.Busy;
            return a;
        }
        if (status == 400 || status == 413)
        {
            var m = RowProblem.Match(reason);
            if (m.Success)
            {
                a.Outcome = Outcome.RowRefused;
                a.RowKind = m.Groups[1].Value;
                a.RowIndex = int.Parse(m.Groups[2].Value) - 1;
            }
            else a.Outcome = Outcome.Refused;
            return a;
        }
        a.Outcome = status >= 500 ? Outcome.ServerError : Outcome.Refused;
        return a;
    }
}
