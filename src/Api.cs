using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading;

internal sealed class Api
{
    private readonly AppServer server;
    private volatile bool ready;
    private Timer timer;
    private volatile bool stopping;

    public Api(AppServer s)
    {
        server = s;
    }

    public void StartBackground()
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                GameData.Load();
                UserStore.Load();
                Community.Load();
                Scans.Load();
                ready = true;
                timer = new Timer(delegate { Tick(); }, null, 2000, 600000);
            }
            catch (Exception ex) { AppEnv.Log("Start", ex.ToString()); }
        });
    }

    public void Shutdown()
    {
        stopping = true;
        if (timer != null) timer.Dispose();
    }

    private void Tick()
    {
        if (stopping) return;
        DateTime sy = Community.Synced;
        if (!Community.Syncing && (sy == DateTime.MinValue || DateTime.UtcNow.Subtract(sy).TotalMinutes >= 30)) Community.Sync(false);
    }

    private void WaitReady()
    {
        for (int i = 0; i < 300 && !ready; i++) Thread.Sleep(50);
        if (!ready) throw new AppError("notReady", null);
    }

    private static string Lang
    {
        get { return UserStore.Lang; }
    }

    public object Call(string path, NameValueCollection q, string body)
    {
        Dictionary<string, object> args = body.Length > 0 ? AppEnv.Json.DeserializeObject(body) as Dictionary<string, object> : null;
        if (path == "/api/ping")
        {
            return AppEnv.Obj("ok", true, "ready", ready, "syncing", Community.Syncing, "syncState", Community.SyncState, "data", Community.Version,
                "update", Updater.Available, "spot", server.TakePendingSpot());
        }
        WaitReady();
        switch (path)
        {
            case "/api/init": return Init();
            case "/api/lang":
                UserStore.Set("lang", AppEnv.GetString(args, "lang"));
                return Init();
            case "/api/active": return Active(q);
            case "/api/spot": return SpotDetail(q["key"] ?? "");
            case "/api/open":
                {
                    string u = AppEnv.GetString(args, "url");
                    if (!u.StartsWith("https://") && !u.StartsWith("http://")) throw new AppError("badUrl", u);
                    System.Diagnostics.Process.Start(u);
                    return AppEnv.Obj("ok", true);
                }
            case "/api/sync":
                ThreadPool.QueueUserWorkItem(delegate { Community.Sync(AppEnv.GetBool(args, "full")); });
                return AppEnv.Obj("ok", true);
            case "/api/update":
                Updater.Install();
                server.Quit(true);
                return AppEnv.Obj("ok", true);
        }
        throw new AppError("unknown", path);
    }

    public bool Image(string name, out byte[] data, out string type)
    {
        data = null;
        type = null;
        if (!name.StartsWith("tg")) return false;
        int bar = name.IndexOf('|');
        if (bar < 0) return false;
        string post = name.Substring(bar + 1);
        int idx;
        if (!int.TryParse(name.Substring(2, bar-2), out idx)) return false;
        if (Scans.PostKey(post) != post) return false;
        string[] imgs = Scans.FetchImages(post);
        if (imgs == null || idx < 0 || idx >= imgs.Length) return false;
        try
        {
            data = Scans.ImageBytes(imgs[idx]);
            type = "image/jpeg";
            return data != null;
        }
        catch (Exception ex) { AppEnv.Log("Bilder", ex.Message); return false; }
    }

    private static readonly string[] DetailKeys = { "baitDetail", "lure", "rig", "dip", "groundbait", "dryMix", "pva", "reelSpeed" };

    private static string CleanDetail(string t, string lang)
    {
        if (string.IsNullOrEmpty(t)) return "";
        int k = t.LastIndexOf("<==", StringComparison.Ordinal);
        if (k >= 0) t = t.Substring(k + 3);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"^\s*\(?\s*(Bait|Köder|Наживка)\s*\)?\s*:\s*", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"^\s*(By catch date|By weight|By time|Nach Fangzeit)\s*", "");
        t = t.Replace("©", "");
        List<string> parts = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string x0 in System.Text.RegularExpressions.Regex.Split(t, @"\s\+\s"))
        {
            string x = x0.Trim();
            if (x.Length == 0 || !seen.Add(x)) continue;
            parts.Add(GameData.Translate(x, lang));
        }
        return string.Join(" + ", parts.ToArray());
    }

    private object SpotDetail(string key)
    {
        string lang = Lang;
        Dictionary<string, object> strings = GameData.Strings(lang);
        Spot s = Community.Spots().FirstOrDefault(x => x.Key == key);
        if (s == null) throw new AppError("spotGone", key);
        Lake lake;
        GameData.LakeById.TryGetValue(s.Lake, out lake);
        DateTime now = DateTime.UtcNow;
        List<object> events = new List<object>();
        Dictionary<string, int> dirs = new Dictionary<string, int>();
        foreach (SpotEvent e in s.Events.OrderByDescending(x => x.Last).Take(60))
        {
            List<object> src = new List<object>();
            HashSet<string> seenSrc = new HashSet<string>();
            string imgPost = "";
            double deg = -1;
            double weight = 0;
            Dictionary<string, string> detail = new Dictionary<string, string>();
            string clip = "", depth = "", method = "", postText = "";
            foreach (Report r in e.Reports)
            {
                if (seenSrc.Add(r.SourceKey)) src.Add(AppEnv.Obj("label", r.SourceLabel, "url", r.Linkable ? r.Url : "", "members", r.SourceKey.StartsWith("dc:")));
                string pk = Scans.PostKey(r.Url);
                if (pk.Length > 0)
                {
                    if (imgPost.Length == 0) imgPost = pk;
                    if (deg < 0) deg = Scans.Degrees(pk);
                    if (postText.Length == 0) postText = Scans.Text(pk);
                }
                double w = AppEnv.GetDouble(r.Raw, "weight", 0);
                if (w > weight) weight = w;
                if (clip.Length == 0) clip = r.S("clip");
                if (depth.Length == 0) depth = r.S("depth");
                if (method.Length == 0) method = r.S("method");
                foreach (string dk in DetailKeys)
                {
                    if (detail.ContainsKey(dk)) continue;
                    string v = dk == "reelSpeed" ? r.S(dk) : CleanDetail(r.S(dk), lang);
                    if (v.Length > 0) detail[dk] = v;
                }
            }
            List<string> fish = new List<string>();
            foreach (Report r in e.Reports) foreach (string f in r.Fish) { string n = GameData.Translate(f, lang); if (!fish.Contains(n)) fish.Add(n); }
            string dir = deg >= 0 ? Scans.Dir16(deg, lang) : "";
            if (dir.Length > 0) { int n; dirs.TryGetValue(dir, out n); dirs[dir] = n + 1; }
            int imgCount = 0;
            if (imgPost.Length > 0) { string[] im = Scans.Images(imgPost); imgCount = im == null ? -1 : im.Length; }
            events.Add(AppEnv.Obj("time", e.Last.ToString("o", CultureInfo.InvariantCulture), "hoursAgo", Math.Round(now.Subtract(e.Last).TotalHours, 1),
                "fish", fish.ToArray(), "weight", weight > 0 ? GameData.FormatWeight(weight * 1000, lang) : "",
                "clip", clip, "depth", depth, "method", method, "dir", dir, "detail", detail, "sources", src.ToArray(),
                "imgPost", imgPost, "imgCount", imgCount, "reports", e.Reports.Count, "postText", postText));
        }
        return AppEnv.Obj("key", s.Key, "lake", s.Lake, "lakeName", lake != null ? lake.Name(lang) : s.Lake,
            "name", s.Mark != null ? SeaMarks.Label(s.Mark, strings) : s.X.ToString(CultureInfo.InvariantCulture) + ":" + s.Y.ToString(CultureInfo.InvariantCulture),
            "sea", s.Mark != null, "x", s.X, "y", s.Y, "events", events, "totalEvents", s.Events.Count, "totalReports", s.Reports.Count,
            "dirs", dirs.OrderByDescending(kv => kv.Value).Select(kv => (object)AppEnv.Obj("dir", kv.Key, "n", kv.Value)).ToArray());
    }

    private object Init()
    {
        string lang = Lang;
        List<object> lakes = new List<object>();
        foreach (Lake l in GameData.Lakes)
        {
            lakes.Add(AppEnv.Obj("id", l.Id, "name", l.Name(lang), "map", l.HasBounds && l.MapKey.Length > 0 ? "/maps/" + l.MapKey + ".jpg" : "",
                "b", new double[] { l.XMin, l.XMax, l.YSouth, l.YNorth }));
        }
        return AppEnv.Obj("version", Updater.CurrentText, "lang", lang, "langs", GameData.Languages(), "strings", GameData.Strings(lang), "lakes", lakes,
            "reports", Community.Count);
    }

    private object Active(NameValueCollection q)
    {
        string lang = Lang;
        Dictionary<string, object> strings = GameData.Strings(lang);
        double hours = 24;
        double.TryParse(q["hours"] ?? "24", NumberStyles.Float, CultureInfo.InvariantCulture, out hours);
        if (hours <= 0) hours = 24;
        string lakeFilter = q["lake"] ?? "";
        string fishFilter = q["fish"] ?? "";
        DateTime now = DateTime.UtcNow;
        DateTime since = now.AddHours(-hours);
        List<object> rows = new List<object>();
        foreach (Spot s in Community.Spots())
        {
            if (s.Last < since) continue;
            if (lakeFilter.Length > 0 && !string.Equals(s.Lake, lakeFilter, StringComparison.OrdinalIgnoreCase)) continue;
            List<SpotEvent> recent = s.Events.Where(e => e.Last >= since).ToList();
            if (recent.Count == 0) continue;
            List<Report> rr = recent.SelectMany(e => e.Reports).ToList();
            if (fishFilter.Length > 0 && !rr.Any(r => r.Fish.Contains(fishFilter))) continue;
            Dictionary<string, int> fish = new Dictionary<string, int>();
            Dictionary<string, int> baits = new Dictionary<string, int>();
            Dictionary<string, int> clips = new Dictionary<string, int>();
            HashSet<string> sources = new HashSet<string>();
            List<string> sourceLabels = new List<string>();
            foreach (SpotEvent e in recent)
            {
                HashSet<string> ef = new HashSet<string>();
                HashSet<string> eb = new HashSet<string>();
                foreach (Report r in e.Reports)
                {
                    foreach (string f in r.Fish) ef.Add(f);
                    foreach (object b in AppEnv.GetList(r.Raw, "bait")) if (b != null && b.ToString().Length > 0) eb.Add(b.ToString());
                    string c = r.S("clip");
                    if (c.Length > 0) { int n; clips.TryGetValue(c, out n); clips[c] = n + 1; }
                    if (sources.Add(r.SourceKey)) sourceLabels.Add(r.SourceLabel);
                }
                foreach (string f in ef) { int n; fish.TryGetValue(f, out n); fish[f] = n + 1; }
                foreach (string b in eb) { int n; baits.TryGetValue(b, out n); baits[b] = n + 1; }
            }
            Lake lake;
            GameData.LakeById.TryGetValue(s.Lake, out lake);
            double score = 0;
            foreach (SpotEvent e in recent) score += Math.Exp(-Math.Max(0, now.Subtract(e.Last).TotalHours) / 12.0);
            double[] norm = lake != null && lake.HasBounds ? GameData.ToNorm(lake, s.X, s.Y) : new double[] { 0, 0 };
            rows.Add(AppEnv.Obj(
                "key", s.Key, "lake", s.Lake, "lakeName", lake != null ? lake.Name(lang) : s.Lake,
                "name", s.Mark != null ? SeaMarks.Label(s.Mark, strings) : s.X.ToString(CultureInfo.InvariantCulture) + ":" + s.Y.ToString(CultureInfo.InvariantCulture),
                "sea", s.Mark != null, "x", s.X, "y", s.Y, "nx", norm[0], "ny", norm[1],
                "last", s.Last.ToString("o", CultureInfo.InvariantCulture), "hoursAgo", Math.Round(now.Subtract(s.Last).TotalHours, 1),
                "catches", recent.Count, "reports", rr.Count, "score", Math.Round(score, 3),
                "fish", Top(fish, 4, lang), "baits", Top(baits, 3, lang),
                "clip", clips.Count > 0 ? clips.OrderByDescending(kv => kv.Value).First().Key : "",
                "sources", sourceLabels.Distinct().ToArray(), "sourceCount", sources.Count));
        }
        rows.Sort((a, b) => ((double)((Dictionary<string, object>)b)["score"]).CompareTo((double)((Dictionary<string, object>)a)["score"]));
        return AppEnv.Obj("hours", hours, "spots", rows, "syncing", Community.Syncing, "synced", Community.Synced == DateTime.MinValue ? "" : Community.Synced.ToString("o", CultureInfo.InvariantCulture));
    }

    private static object[] Top(Dictionary<string, int> d, int n, string lang)
    {
        return d.OrderByDescending(kv => kv.Value).Take(n).Select(kv => (object)AppEnv.Obj("key", kv.Key, "name", GameData.Translate(kv.Key, lang), "n", kv.Value)).ToArray();
    }
}

internal static class UserStore
{
    private static readonly object gate = new object();
    private static Dictionary<string, object> data = new Dictionary<string, object>();

    public static void Load()
    {
        try
        {
            Dictionary<string, object> d = AppEnv.ReadJsonFile(AppEnv.DataPath("user.json")) as Dictionary<string, object>;
            if (d != null) lock (gate) data = d;
        }
        catch (Exception ex) { AppEnv.Log("Daten", "user.json: " + ex.Message); }
    }

    public static string Lang
    {
        get
        {
            string l;
            lock (gate) l = AppEnv.GetString(data, "lang");
            if (l.Length == 0)
            {
                string ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                l = Array.IndexOf(GameData.Languages(), ui) >= 0 ? ui : "en";
            }
            return l;
        }
    }

    public static object Get(string key)
    {
        lock (gate) { object v; return data.TryGetValue(key, out v) ? v : null; }
    }

    public static void Set(string key, object value)
    {
        lock (gate)
        {
            data[key] = value;
            AppEnv.WriteJsonFile(AppEnv.DataPath("user.json"), data);
        }
    }
}
