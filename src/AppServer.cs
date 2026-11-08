using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class AppServer
{
    private readonly string token = Guid.NewGuid().ToString("N");
    private DateTime lastPing = DateTime.Now;
    private bool firstPingSeen;
    private readonly Dictionary<string, DateTime> pages = new Dictionary<string, DateTime>(StringComparer.Ordinal);
    private DateTime byeAt = DateTime.MinValue;
    private bool running = true;
    private Process window;
    private DateTime windowStart;
    private bool restart;
    private string pendingSpot;
    private string cookieName = "rf4t";
    private Api api;

    public void Run(int port, bool noBrowser, string urlFile, string openSpot)
    {
        pendingSpot = openSpot;
        string instFile = AppEnv.DataPath("instance.json");
        string existing = ExistingInstance(instFile);
        if (existing != null)
        {
            if (!noBrowser) OpenWindow(existing + (openSpot != null ? "&spot=" + Uri.EscapeDataString(openSpot) : ""));
            return;
        }
        Updater.CleanUp();
        Updater.CheckInBackground();
        api = new Api(this);
        api.StartBackground();
        if (port == 0) port = FreePort();
        string prefix = "http://localhost:" + port.ToString(CultureInfo.InvariantCulture) + "/";
        cookieName = "rf4t" + port.ToString(CultureInfo.InvariantCulture);
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Prefixes.Add("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/");
        listener.Start();
        string url = prefix + "?t=" + token;
        try { AppEnv.WriteJsonFile(instFile, AppEnv.Obj("url", url, "pid", Process.GetCurrentProcess().Id)); } catch (Exception) { }
        AppEnv.Log("App", "Gestartet " + Updater.CurrentText + " auf " + prefix);

        if (!noBrowser) OpenWindow(url + (openSpot != null ? "&spot=" + Uri.EscapeDataString(openSpot) : ""));
        else if (!string.IsNullOrEmpty(urlFile)) File.WriteAllText(urlFile, url, new UTF8Encoding(false));

        while (running)
        {
            Task<HttpListenerContext> task = listener.GetContextAsync();
            while (!task.Wait(1000))
            {
                if (window != null && window.HasExited)
                {
                    bool handedOff = DateTime.Now.Subtract(windowStart).TotalSeconds < 15;
                    window = FindAppWindow(prefix);
                    if (window == null && !handedOff) { running = false; break; }
                }
                double idle = DateTime.Now.Subtract(lastPing).TotalSeconds;
                if ((firstPingSeen && idle > 90) || (!firstPingSeen && idle > 180)) { running = false; break; }
                if (AllPagesGone()) { running = false; break; }
            }
            if (!running) break;
            HttpListenerContext ctx = task.Result;
            ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
        }
        listener.Stop();
        listener.Close();
        api.Shutdown();
        try { File.Delete(instFile); } catch (Exception) { }
        AppEnv.Log("App", "Beendet");
        if (restart) CloseAppWindow(prefix);
        if (restart) Process.Start(Assembly.GetExecutingAssembly().Location, RestartArgs());
    }

    private static string RestartArgs()
    {
        string[] a = Environment.GetCommandLineArgs();
        List<string> keep = new List<string>();
        for (int i = 1; i < a.Length; i++)
        {
            if ((a[i].Equals("-OpenSpot", StringComparison.OrdinalIgnoreCase) || a[i].Equals("-Export", StringComparison.OrdinalIgnoreCase)) && i + 1 < a.Length) { i++; continue; }
            keep.Add("\"" + a[i].Replace("\"", "") + "\"");
        }
        return string.Join(" ", keep.ToArray());
    }

    private bool AllPagesGone()
    {
        lock (pages)
        {
            if (byeAt == DateTime.MinValue || DateTime.Now.Subtract(byeAt).TotalSeconds < 4) return false;
            foreach (string k in pages.Where(kv => DateTime.Now.Subtract(kv.Value).TotalSeconds > 90).Select(kv => kv.Key).ToList()) pages.Remove(k);
            if (pages.Count > 0) { byeAt = DateTime.MinValue; return false; }
            return true;
        }
    }

    private void Seen(string page, bool bye)
    {
        if (string.IsNullOrEmpty(page)) return;
        lock (pages)
        {
            if (bye) { pages.Remove(page); byeAt = DateTime.Now; }
            else pages[page] = DateTime.Now;
        }
    }

    public void Quit(bool restartAfter)
    {
        restart = restartAfter;
        running = false;
    }

    private static string ExistingInstance(string file)
    {
        try
        {
            Dictionary<string, object> d = AppEnv.ReadJsonFile(file) as Dictionary<string, object>;
            if (d == null) return null;
            int pid = (int)AppEnv.GetDouble(d, "pid", 0);
            string url = AppEnv.GetString(d, "url");
            if (pid <= 0 || url.Length == 0 || pid == Process.GetCurrentProcess().Id) return null;
            Process p = Process.GetProcessById(pid);
            if (p.HasExited || !p.ProcessName.StartsWith("RF4 Companion", StringComparison.OrdinalIgnoreCase)) return null;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url.Replace("/?t=", "/api/alive?t="));
            req.Timeout = 1500;
            using (WebResponse res = req.GetResponse()) { }
            return url;
        }
        catch (Exception) { return null; }
    }

    private static int FreePort()
    {
        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    internal static string EdgePath()
    {
        foreach (string root in new string[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            string candidate = Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private void OpenWindow(string url)
    {
        windowStart = DateTime.Now;
        window = Browser.Open(url);
    }

    private static void CloseAppWindow(string appUrl)
    {
        try
        {
            Process p = FindAppWindow(appUrl);
            if (p == null) return;
            p.CloseMainWindow();
            if (!p.WaitForExit(3000)) p.Kill();
        }
        catch (Exception) { }
    }

    private static Process FindAppWindow(string appUrl)
    {
        try
        {
            using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='" + (Browser.WindowExe ?? "msedge.exe").Replace("'", "") + "'"))
                foreach (ManagementObject o in s.Get())
                {
                    string cl = (o["CommandLine"] as string) ?? "";
                    if (cl.IndexOf("--app=" + appUrl, StringComparison.OrdinalIgnoreCase) < 0 || cl.Contains("--type=")) continue;
                    Process p = Process.GetProcessById(Convert.ToInt32(o["ProcessId"], CultureInfo.InvariantCulture));
                    if (!p.HasExited) return p;
                }
        }
        catch (Exception) { }
        return null;
    }

    public string TakePendingSpot()
    {
        string s = pendingSpot;
        pendingSpot = null;
        return s;
    }

    private void Handle(HttpListenerContext ctx)
    {
        HttpListenerRequest req = ctx.Request;
        string path = req.Url.AbsolutePath;
        try
        {
            if ((path == "/" || path == "/index.html") && req.QueryString["t"] == token)
            {
                ctx.Response.Headers.Add("Set-Cookie", cookieName + "=" + token + "; Path=/; HttpOnly; SameSite=Strict");
                ctx.Response.Redirect("/");
                ctx.Response.Close();
                return;
            }
            if (path == "/" || path == "/index.html")
            {
                Send(ctx, 200, AppEnv.Resource("ui/index.html"), "text/html; charset=utf-8", false);
                return;
            }
            if (path.StartsWith("/maps/"))
            {
                string name = Path.GetFileName(path.Substring(6));
                byte[] img = AppEnv.Resource("maps/" + name);
                if (img == null) { Send(ctx, 404, new byte[0], "text/plain", false); return; }
                Send(ctx, 200, img, "image/jpeg", true);
                return;
            }
            string given = req.Headers["X-Token"];
            if (string.IsNullOrEmpty(given)) given = req.QueryString["t"];
            if (string.IsNullOrEmpty(given) && req.Cookies[cookieName] != null) given = req.Cookies[cookieName].Value;
            if (given != token) { SendJson(ctx, 403, AppEnv.Obj("error", "forbidden")); return; }
            if (path == "/api/alive") { SendJson(ctx, 200, AppEnv.Obj("ok", true)); return; }
            if (path == "/api/bye") { Seen(req.QueryString["pg"], true); SendJson(ctx, 200, AppEnv.Obj("ok", true)); return; }
            Seen(req.QueryString["pg"], false);
            lastPing = DateTime.Now;
            if (path == "/api/ping") firstPingSeen = true;
            if (path.StartsWith("/img/"))
            {
                byte[] b; string type;
                string iname = path == "/img/tg" ? "tg" + (req.QueryString["i"] ?? "0") + "|" + (req.QueryString["p"] ?? "") : path.Substring(5);
                if (api.Image(iname, out b, out type)) Send(ctx, 200, b, type, !iname.StartsWith("fb/"));
                else Send(ctx, 404, new byte[0], "text/plain", false);
                return;
            }
            if (path.StartsWith("/ext/") && path.EndsWith(".js"))
            {
                string f = Path.Combine(Extensions.Dir, Path.GetFileName(path.Substring(5)));
                if (!Extensions.Scripts.Contains(Path.GetFileName(f)) || !File.Exists(f)) { Send(ctx, 404, new byte[0], "text/plain", false); return; }
                Send(ctx, 200, File.ReadAllBytes(f), "text/javascript; charset=utf-8", false);
                return;
            }
            if (path == "/api/backup/import" && req.HasEntityBody)
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    req.InputStream.CopyTo(ms);
                    SendJson(ctx, 200, Backup.Import(ms.ToArray()));
                }
                return;
            }
            string body = "";
            if (req.HasEntityBody)
            {
                using (StreamReader r = new StreamReader(req.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
            }
            object result = api.Call(path, req.QueryString, body);
            SendJson(ctx, 200, result);
        }
        catch (AppError e)
        {
            SendJson(ctx, 400, AppEnv.Obj("error", e.Code, "arg", e.Arg));
        }
        catch (Exception e)
        {
            AppEnv.Log("Http", path + ": " + e);
            try { SendJson(ctx, 500, AppEnv.Obj("error", "internal", "arg", e.Message)); } catch (Exception) { }
        }
    }

    private static void Send(HttpListenerContext ctx, int status, byte[] data, string type, bool cache)
    {
        HttpListenerResponse res = ctx.Response;
        res.StatusCode = status;
        res.ContentType = type;
        res.Headers["Cache-Control"] = cache ? "max-age=86400" : "no-store";
        res.ContentLength64 = data.Length;
        try { res.OutputStream.Write(data, 0, data.Length); } catch (Exception) { }
        try { res.OutputStream.Close(); } catch (Exception) { }
    }

    private static void SendJson(HttpListenerContext ctx, int status, object value)
    {
        Send(ctx, status, Encoding.UTF8.GetBytes(AppEnv.Json.Serialize(value)), "application/json; charset=utf-8", false);
    }
}
