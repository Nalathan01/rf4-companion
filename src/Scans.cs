using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;

internal static class Scans
{
    private static readonly object gate = new object();
    private static Dictionary<string, object> data = new Dictionary<string, object>(StringComparer.Ordinal);
    private static readonly Dictionary<string, string[]> embedImgs = new Dictionary<string, string[]>(StringComparer.Ordinal);
    private static readonly Dictionary<string, byte[]> imgCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    private static readonly LinkedList<string> imgOrder = new LinkedList<string>();
    private static readonly Regex TgKey = new Regex(@"^https?://t\.me/([^/?]+)/(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex Photo = new Regex(@"tgme_widget_message_photo_wrap[^>]*?background-image:url\('([^']+)'\)");

    public static void Load()
    {
        try
        {
            Dictionary<string, object> d = AppEnv.ReadJsonFile(AppEnv.DataPath("scan_cache.json")) as Dictionary<string, object>;
            if (d != null) lock (gate) data = new Dictionary<string, object>(d, StringComparer.Ordinal);
        }
        catch (Exception ex) { AppEnv.Log("Scans", "Laden: " + ex.Message); }
    }

    public static string PostKey(string url)
    {
        Match m = TgKey.Match(url ?? "");
        return m.Success ? "https://t.me/" + m.Groups[1].Value + "/" + m.Groups[2].Value : "";
    }

    private static Dictionary<string, object> Entry(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (gate)
        {
            object v;
            return data.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }
    }

    public static double Degrees(string key)
    {
        Dictionary<string, object> e = Entry(key);
        return e == null ? -1 : AppEnv.GetDouble(e, "deg", -1);
    }

    public static string Text(string key)
    {
        return AppEnv.GetString(Entry(key), "txt");
    }

    public static string[] Images(string key)
    {
        Dictionary<string, object> e = Entry(key);
        List<string> l = new List<string>();
        if (e != null) foreach (object o in AppEnv.GetList(e, "imgs")) if (o != null) l.Add(o.ToString());
        if (l.Count > 0) return l.ToArray();
        lock (gate)
        {
            string[] c;
            if (embedImgs.TryGetValue(key, out c)) return c;
        }
        return null;
    }

    public static string[] FetchImages(string key)
    {
        string[] have = Images(key);
        if (have != null) return have;
        List<string> l = new List<string>();
        try
        {
            string html = Http.GetString(key + "?embed=1&mode=tme", 20000);
            foreach (Match m in Photo.Matches(html))
            {
                string u = m.Groups[1].Value;
                if (u.StartsWith("//")) u = "https:" + u;
                if (u.StartsWith("https://") && !l.Contains(u)) l.Add(u);
                if (l.Count >= 8) break;
            }
        }
        catch (Exception ex) { AppEnv.Log("Bilder", key + ": " + ex.Message); }
        lock (gate) embedImgs[key] = l.ToArray();
        return l.ToArray();
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

    public static string Dir16(double deg, string lang)
    {
        if (deg < 0) return "";
        string[] keys = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
        string n = keys[(int)Math.Floor(((deg + 11.25) % 360) / 22.5)];
        if (lang == "de" || lang == "nl") n = n.Replace("E", "O");
        return n;
    }
}
