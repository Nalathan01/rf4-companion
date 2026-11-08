using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

internal static class Overlay
{
    public const int DefaultPort = 47820;
    private static readonly object gate = new object();
    private static HttpListener listener;
    private static volatile string error = "";

    public static int Port
    {
        get
        {
            int p;
            object v = UserStore.Get("overlayPort");
            return v != null && int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out p) && p >= 1024 && p <= 65535 ? p : DefaultPort;
        }
    }

    public static void Start()
    {
        lock (gate)
        {
            Stop();
            int port = Port;
            try
            {
                HttpListener l = new HttpListener();
                l.Prefixes.Add("http://localhost:" + port.ToString(CultureInfo.InvariantCulture) + "/");
                l.Prefixes.Add("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/");
                l.Start();
                listener = l;
                error = "";
                Thread t = new Thread(() => Loop(l));
                t.IsBackground = true;
                t.Start();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppEnv.Log("Overlay", "Port " + port + ": " + ex.Message);
            }
        }
    }

    public static void Stop()
    {
        lock (gate)
        {
            if (listener == null) return;
            try { listener.Stop(); listener.Close(); } catch (Exception) { }
            listener = null;
        }
    }

    public static object Status()
    {
        return AppEnv.Obj("port", Port, "running", listener != null, "error", error, "opts", UserStore.Get("overlayOpts") ?? new Dictionary<string, object>(), "live", LiveCatch.Status());
    }

    public static object Save(Dictionary<string, object> a)
    {
        int p;
        if (!int.TryParse(AppEnv.GetString(a, "port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out p) || p < 1024 || p > 65535) throw new AppError("ovBadPort", null);
        Dictionary<string, object> o = a.ContainsKey("opts") ? a["opts"] as Dictionary<string, object> : null;
        if (o != null) UserStore.Set("overlayOpts", o);
        if (a.ContainsKey("live")) UserStore.Set("liveOn", AppEnv.GetBool(a, "live"));
        if (AppEnv.GetString(a, "lang").Length > 0) UserStore.Set("trackerLang", AppEnv.GetString(a, "lang"));
        if (p != Port || listener == null)
        {
            UserStore.Set("overlayPort", p);
            Start();
        }
        return Status();
    }

    private static void Loop(HttpListener l)
    {
        while (l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = l.GetContext(); } catch (Exception) { return; }
            ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
        }
    }

    private static void Handle(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
        try
        {
            if (path == "/overlay") { Send(ctx, 200, AppEnv.Resource("ui/overlay.html"), "text/html; charset=utf-8"); return; }
            if (path == "/overlay/data") { Send(ctx, 200, Encoding.UTF8.GetBytes(AppEnv.Json.Serialize(Data(ctx.Request.QueryString))), "application/json; charset=utf-8"); return; }
            Send(ctx, 404, new byte[0], "text/plain");
        }
        catch (Exception ex)
        {
            AppEnv.Log("Overlay", path + ": " + ex.Message);
            try { Send(ctx, 500, new byte[0], "text/plain"); } catch (Exception) { }
        }
    }

    private static object Data(NameValueCollection q)
    {
        Dictionary<string, object> opts = UserStore.Get("overlayOpts") as Dictionary<string, object> ?? new Dictionary<string, object>();
        Func<string, string> opt = k => q[k] ?? (opts.ContainsKey(k) && opts[k] != null ? Convert.ToString(opts[k], CultureInfo.InvariantCulture) : "");
        int n;
        if (!int.TryParse(opt("n"), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) n = 5;
        n = Math.Max(1, Math.Min(20, n));
        string lang = opt("lang");
        if (Array.IndexOf(GameData.Languages(), lang) < 0) lang = UserStore.Lang;
        bool onlyTrophy = opt("trophy") == "1" || opt("trophy") == "True";
        List<object> items = LiveCatch.Recent().Where(c => ((string)c[0]).Length > 0)
            .Select(c => new { fish = (string)c[0], grams = (int)c[1], time = (DateTime)c[2], level = Level((string)c[0], (int)c[1]) })
            .Where(c => !onlyTrophy || c.level.Length > 0)
            .OrderByDescending(c => c.time).Take(n)
            .Select(c => (object)AppEnv.Obj("fish", GameData.Translate(c.fish, lang), "weight", c.grams > 0 ? GameData.FormatWeight(c.grams, lang) : "", "level", c.level,
                "time", c.time.ToString("HH:mm", CultureInfo.InvariantCulture), "id", c.fish + c.grams + c.time.Ticks))
            .ToList();
        return AppEnv.Obj("opts", opts, "items", items, "labels", AppEnv.Obj("trophy", GameData.Ui(lang, "trophy", "Trophy"), "rare", GameData.Ui(lang, "rare", "Rare trophy"),
            "title", GameData.Ui(lang, "ovTitle", "Last catches"), "empty", GameData.Ui(lang, "ovEmpty", "No catches yet")));
    }

    private static string Level(string fish, int grams)
    {
        int[] t;
        if (grams <= 0 || !GameData.Trophies.TryGetValue(fish, out t)) return "";
        return t[1] > 0 && grams >= t[1] ? "rare" : t[0] > 0 && grams >= t[0] ? "trophy" : "";
    }

    private static void Send(HttpListenerContext ctx, int status, byte[] data, string type)
    {
        HttpListenerResponse res = ctx.Response;
        res.StatusCode = status;
        res.ContentType = type;
        res.Headers["Cache-Control"] = "no-store";
        if (data == null) data = new byte[0];
        res.ContentLength64 = data.Length;
        try { res.OutputStream.Write(data, 0, data.Length); } catch (Exception) { }
        try { res.OutputStream.Close(); } catch (Exception) { }
    }
}
