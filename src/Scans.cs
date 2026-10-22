using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

internal static class Scans
{
    private static readonly object gate = new object();
    private static Dictionary<string, object> data = new Dictionary<string, object>(StringComparer.Ordinal);
    private static Dictionary<string, object> posts = new Dictionary<string, object>(StringComparer.Ordinal);
    private static readonly Dictionary<string, byte[]> imgCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    private static readonly LinkedList<string> imgOrder = new LinkedList<string>();
    private static readonly Regex TgKey = new Regex(@"^https?://t\.me/([^/?]+)/(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex Photo = new Regex(@"tgme_widget_message_photo_wrap[^>]*?background-image:url\('([^']+)'\)");
    private static readonly Regex TextDiv = new Regex(@"<div class=""tgme_widget_message_text[^""]*""[^>]*>(.*?)</div>", RegexOptions.Singleline);
    private static readonly Queue<string> queue = new Queue<string>();
    private static readonly HashSet<string> queued = new HashSet<string>(StringComparer.Ordinal);
    private static Thread worker;
    private static bool dirty;
    public static int Version;

    public static void Load()
    {
        try
        {
            Dictionary<string, object> d = AppEnv.ReadJsonFile(AppEnv.DataPath("scan_cache.json")) as Dictionary<string, object>;
            if (d != null) lock (gate) data = new Dictionary<string, object>(d, StringComparer.Ordinal);
        }
        catch (Exception ex) { AppEnv.Log("Scans", "Laden: " + ex.Message); }
        try
        {
            Dictionary<string, object> p = AppEnv.ReadJsonFile(AppEnv.DataPath("post_cache.json")) as Dictionary<string, object>;
            if (p != null) lock (gate) posts = new Dictionary<string, object>(p, StringComparer.Ordinal);
        }
        catch (Exception ex) { AppEnv.Log("Scans", "post_cache: " + ex.Message); }
    }

    public static string PostKey(string url)
    {
        Match m = TgKey.Match(url ?? "");
        return m.Success ? "https://t.me/" + m.Groups[1].Value + "/" + m.Groups[2].Value : "";
    }

    private static Dictionary<string, object> Entry(Dictionary<string, object> src, string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (gate)
        {
            object v;
            return src.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }
    }

    public static bool Known(string key)
    {
        return Entry(posts, key) != null || AppEnv.GetString(Entry(data, key), "txt").Length > 0;
    }

    public static double Degrees(string key)
    {
        Dictionary<string, object> e = Entry(data, key);
        return e == null ? -1 : AppEnv.GetDouble(e, "deg", -1);
    }

    public static string Text(string key)
    {
        string t = AppEnv.GetString(Entry(posts, key), "txt");
        return t.Length > 0 ? t : AppEnv.GetString(Entry(data, key), "txt");
    }

    private static string[] List(Dictionary<string, object> e)
    {
        List<string> l = new List<string>();
        if (e != null) foreach (object o in AppEnv.GetList(e, "imgs")) if (o != null) l.Add(o.ToString());
        return l.ToArray();
    }

    public static string[] Images(string key)
    {
        Dictionary<string, object> p = Entry(posts, key);
        if (p != null) return List(p);
        Dictionary<string, object> e = Entry(data, key);
        if (e == null || AppEnv.GetList(e, "imgs").Count == 0) return null;
        return List(e);
    }

    public static string[] FetchImages(string key)
    {
        string[] have = Images(key);
        return have ?? Fetch(key);
    }

    public static string[] Fetch(string key)
    {
        List<string> l = new List<string>();
        string txt = "";
        try
        {
            string html = Http.GetString(key + "?embed=1&mode=tme", 20000);
            foreach (Match m in Photo.Matches(html))
            {
                string u = m.Groups[1].Value;
                if (u.StartsWith("//")) u = "https:" + u;
                if (u.StartsWith("https://") && !l.Contains(u)) l.Add(u);
                if (l.Count >= 10) break;
            }
            Match t = TextDiv.Match(html);
            if (t.Success)
            {
                string s = Regex.Replace(t.Groups[1].Value, @"<br\s*/?>", "\n");
                s = Regex.Replace(s, "<[^>]+>", "");
                txt = WebUtility.HtmlDecode(s).Trim();
            }
        }
        catch (Exception ex)
        {
            AppEnv.Log("Post", key + ": " + ex.Message);
            return Images(key) ?? new string[0];
        }
        lock (gate)
        {
            Dictionary<string, object> old = Entry(posts, key);
            if (txt.Length == 0 && old != null) txt = AppEnv.GetString(old, "txt");
            if (txt.Length == 0) txt = AppEnv.GetString(Entry(data, key), "txt");
            posts[key] = AppEnv.Obj("txt", txt, "imgs", l.ToArray(), "at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            dirty = true;
            Version++;
        }
        return l.ToArray();
    }

    public static byte[] ImageAt(string key, int idx)
    {
        string[] imgs = FetchImages(key);
        if (imgs != null && idx >= 0 && idx < imgs.Length)
        {
            try { return ImageBytes(imgs[idx]); }
            catch (WebException) { }
        }
        imgs = Fetch(key);
        if (imgs == null || idx < 0 || idx >= imgs.Length) return null;
        return ImageBytes(imgs[idx]);
    }

    public static byte[] ImageBytes(string url)
    {
        lock (gate)
        {
            byte[] b;
            if (imgCache.TryGetValue(url, out b)) return b;
        }
        byte[] data2 = Http.GetBytes(url, 20000);
        lock (gate)
        {
            imgCache[url] = data2;
            imgOrder.AddLast(url);
            while (imgOrder.Count > 120) { imgCache.Remove(imgOrder.First.Value); imgOrder.RemoveFirst(); }
        }
        return data2;
    }

    public static void Request(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (gate)
        {
            if (!queued.Add(key)) return;
            queue.Enqueue(key);
            if (worker == null || !worker.IsAlive)
            {
                worker = new Thread(Work);
                worker.IsBackground = true;
                worker.Start();
            }
        }
    }

    private static void Work()
    {
        int n = 0;
        while (true)
        {
            string key;
            lock (gate)
            {
                if (queue.Count == 0) { worker = null; break; }
                key = queue.Dequeue();
            }
            Fetch(key);
            if (++n % 20 == 0) Save();
            Thread.Sleep(250);
        }
        Save();
    }

    public static void Save()
    {
        lock (gate)
        {
            if (!dirty) return;
            try { AppEnv.WriteJsonFile(AppEnv.DataPath("post_cache.json"), posts); dirty = false; }
            catch (Exception ex) { AppEnv.Log("Post", "Speichern: " + ex.Message); }
        }
    }

    public static string Dir16(double deg, string lang)
    {
        if (deg < 0) return "";
        string[] keys = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
        string n = keys[(int)Math.Floor(((deg + 11.25) % 360) / 22.5)];
        if (lang == "de" || lang == "nl") n = n.Replace("E", "O");
        return n;
    }
}
