using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

internal sealed class Api
{
    private readonly AppServer server;
    private volatile bool ready;
    private Timer timer;
    private volatile bool stopping;
    private volatile bool started;
    private volatile string step = "bootGame";
    private volatile int stepPct;
    private volatile string stepInfo = "";
    private readonly System.Diagnostics.Stopwatch startClock = System.Diagnostics.Stopwatch.StartNew();

    private void Step(string key, int pct)
    {
        if (step.Length > 0) AppEnv.Log("Start", step + " fertig nach " + startClock.ElapsedMilliseconds + " ms");
        step = key;
        stepPct = pct;
    }

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
                Step("bootGame", 5);
                GameData.Load();
                Step("bootData", 10);
                UserStore.Load();
                Extensions.Load();
                Tracker.Start();
                Cloud.LoadState();
                Scans.Load();
                Community.Load();
                bool freshArchive = !System.IO.Directory.Exists(Records.ArchiveDir) || System.IO.Directory.GetFiles(Records.ArchiveDir, "week_*.json.gz").Length == 0;
                Thread cloud = new Thread(() => { Cloud.Sync(freshArchive); if (freshArchive) { Records.Loaded = false; Records.Refresh(); } });
                cloud.IsBackground = true;
                cloud.Start();
                ThreadPool.QueueUserWorkItem(delegate { try { Records.Refresh(); } catch (Exception ex) { AppEnv.Log("Rekorde", ex.Message); Records.Loaded = true; } });
                ready = true;
                Step("bootSync", 15);
                bool mirrored = Mirror.Fetch(true);
                if (!mirrored && (Community.Count == 0 || DateTime.UtcNow.Subtract(Community.Synced).TotalMinutes >= 30))
                {
                    Step("bootSync", 15);
                    Thread sync = new Thread(() => Community.Sync(false));
                    sync.IsBackground = true;
                    sync.Start();
                    while (sync.IsAlive)
                    {
                        Match sm = Regex.Match(Community.SyncState ?? "", @"(\d+)/(\d+)");
                        if (sm.Success) { int a = int.Parse(sm.Groups[1].Value), b = Math.Max(1, int.Parse(sm.Groups[2].Value)); stepPct = 15 + 25 * Math.Min(a, b) / b; stepInfo = a + " / " + b; }
                        Thread.Sleep(200);
                    }
                    stepInfo = "";
                    TgDirect.Sync(true);
                }
                Step("bootPosts", 40);
                if (!mirrored) Community.QueuePosts(168);
                int total = Math.Max(1, Scans.Pending);
                DateTime until = DateTime.UtcNow.AddMinutes(6);
                while (Scans.Pending > 0 && DateTime.UtcNow < until)
                {
                    int done = total-Scans.Pending;
                    stepPct = 40 + 35 * Math.Max(0, done) / total;
                    stepInfo = Math.Max(0, done) + " / " + total;
                    Thread.Sleep(250);
                }
                stepInfo = "";
                Step("bootRecords", 75);
                if (freshArchive) cloud.Join(TimeSpan.FromMinutes(3));
                for (int i = 0; i < 1200 && !Records.Loaded; i++) Thread.Sleep(50);
                Step("bootSpots", 90);
                try { Active(new NameValueCollection { { "hours", "168" } }); } catch (Exception ex) { AppEnv.Log("Start", "Vorrechnen: " + ex.Message); }
                Step("", 100);
                timer = new Timer(delegate { Tick(); }, null, 2000, 600000);
            }
            catch (Exception ex) { AppEnv.Log("Start", ex.ToString()); }
            finally { started = true; }
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
        bool mirrored = Mirror.Fetch(false);
        if (!mirrored && !Community.Syncing && (sy == DateTime.MinValue || DateTime.UtcNow.Subtract(sy).TotalMinutes >= 30)) Community.Sync(false);
        Cloud.Sync(false);
        if (!mirrored) TgDirect.Sync(false);
        Records.Refresh();
        if (!mirrored) Community.QueuePosts(72);
        if (!mirrored && ImageOcr.AnyAvailable()) Community.QueueRecentOcr(72);
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
            return AppEnv.Obj("ok", true, "ready", ready, "started", started, "step", step, "stepPct", stepPct, "stepInfo", stepInfo, "syncing", Community.Syncing, "syncState", Community.SyncState, "data", Community.Version, "ocr", ImageOcr.Version,
                "update", Updater.Available, "tracker", Tracker.Version, "spot", server.TakePendingSpot());
        }
        WaitReady();
        if (path.StartsWith("/api/ext/", StringComparison.Ordinal)) return Extensions.Call(path.Substring(9), args);
        if (path == "/api/extensions") return Extensions.Scripts.ToArray();
        if (path.StartsWith("/api/tracker/", StringComparison.Ordinal)) return Tracker.Command(path.Substring(13), args ?? new Dictionary<string, object>());
        switch (path)
        {
            case "/api/init": return Init();
            case "/api/lang":
                UserStore.Set("lang", AppEnv.GetString(args, "lang"));
                return Init();
            case "/api/active": return Active(q);
            case "/api/find": return Finder.Find(q, Lang, GameData.Strings(Lang));
            case "/api/log": return Fangbuch.Log(Lang);
            case "/api/loglists": return Fangbuch.Lists(q["lake"] ?? "", Lang);
            case "/api/catch/save": return Fangbuch.SaveCatch(args ?? new Dictionary<string, object>(), Lang);
            case "/api/catch/delete": return Fangbuch.Delete("catches", AppEnv.GetList(args, "ids"));
            case "/api/myspot/save": return Fangbuch.SaveSpot(args ?? new Dictionary<string, object>(), Lang);
            case "/api/myspot/delete": return Fangbuch.Delete("spots", AppEnv.GetList(args, "ids"));
            case "/api/recipe/save": return Fangbuch.SaveRecipe(args ?? new Dictionary<string, object>(), Lang);
            case "/api/recipe/delete": return Fangbuch.Delete("recipes", AppEnv.GetList(args, "ids"));
            case "/api/itemnames": return Fangbuch.ItemNames(Lang);
            case "/api/devlines":
                {
                    if (AppEnv.DevRoot == null) throw new AppError("unknown", path);
                    return TrackerOcr.Read(q["file"], q["lang"] ?? "de-DE", System.Drawing.RectangleF.Empty, 1, false).Lines();
                }
            case "/api/devtrack":
                {
                    if (AppEnv.DevRoot == null) throw new AppError("unknown", path);
                    Tracker.Process(q["file"], null);
                    return Tracker.Command("status", new Dictionary<string, object>());
                }
            case "/api/spot": return SpotDetail(q["key"] ?? "");
            case "/api/devocr":
                {
                    if (AppEnv.DevRoot == null) throw new AppError("unknown", path);
                    byte[] img = System.IO.File.ReadAllBytes(q["file"]);
                    List<string> found;
                    string used;
                    bool ok = ImageOcr.ReadSetup(img, out found, out used);
                    List<object> names = new List<object>();
                    foreach (string k in found) names.Add(k + " = " + GameData.Translate(k, Lang));
                    return AppEnv.Obj("ok", ok, "used", used, "baits", names);
                }
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
            case "/api/lookup": return Lookup.Data(Lang);
            case "/api/ocrlangs": return Lookup.OcrLangs();
            case "/api/ocrinstall": return Lookup.InstallOcr(AppEnv.GetString(args, "tag"));
            case "/api/about":
                if (AppEnv.GetBool(args, "check")) Updater.CheckInBackground();
                return AppEnv.Obj("version", Updater.CurrentText, "update", Updater.Available, "notes", Updater.Notes, "dataDir", AppEnv.DataDir);
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
        try
        {
            data = Scans.ImageAt(post, idx);
            type = "image/jpeg";
            return data != null;
        }
        catch (Exception ex) { AppEnv.Log("Bilder", ex.Message); return false; }
    }

    private static readonly Dictionary<string, string[]> baitMemo = new Dictionary<string, string[]>(StringComparer.Ordinal);

    internal static void ClearBaitMemo()
    {
        lock (baitMemo) baitMemo.Clear();
    }

    internal static string[] ReportBaits(Report r)
    {
        lock (baitMemo)
        {
            string[] m;
            if (baitMemo.TryGetValue(r.Id, out m)) return m;
        }
        List<string> keys = new List<string>();
        foreach (string field in new string[] { "baitDetail", "lure" })
        {
            string t = CleanDetail(r.S(field));
            if (t.Length == 0) continue;
            foreach (string part in System.Text.RegularExpressions.Regex.Split(t, @"\s\+\s|\s*,\s*"))
            {
                string k = PostText.ResolveKey(part);
                if (k != null && !keys.Contains(k)) keys.Add(k);
            }
            if (keys.Count > 0) break;
        }
        string pk = Scans.PostKey(r.Url);
        if (keys.Count == 0 && pk.Length > 0)
        {
            string pv;
            if (PostText.Parse(Scans.Text(pk)).TryGetValue("baitDetail", out pv)) foreach (string k in pv.Split(new string[] { " + " }, StringSplitOptions.RemoveEmptyEntries)) if (!keys.Contains(k)) keys.Add(k);
        }
        if (keys.Count == 0 && pk.Length > 0)
        {
            Dictionary<string, object> oc = ImageOcr.Result(pk);
            if (oc != null) foreach (object o in AppEnv.GetList(oc, "baits")) if (!GameData.DipKeys.Contains(o.ToString()) && !keys.Contains(o.ToString())) keys.Add(o.ToString());
        }
        if (keys.Count == 0)
        {
            RecordHit rh = Records.Match(r);
            if (rh != null) foreach (string b in rh.Bait.Split(';')) if (b.Trim().Length > 0 && !keys.Contains(b.Trim())) keys.Add(b.Trim());
        }
        bool detailed = keys.Count > 0;
        string[] res = keys.ToArray();
        if (detailed || pk.Length == 0 || ImageOcr.Result(pk) != null) lock (baitMemo) baitMemo[r.Id] = res;
        return res;
    }

    private static readonly string[] DetailKeys = { "baitDetail", "lure", "rig", "dip", "groundbait", "dryMix", "pva", "reelSpeed" };

    private static string CleanDetail(string t)
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
            parts.Add(x);
        }
        return string.Join(" + ", parts.ToArray());
    }

    private static readonly System.Text.RegularExpressions.Regex Footer = new System.Text.RegularExpressions.Regex(@"(?i)(Предложить пост|Поиск по водо|Барахолка|Мы в ВК|Мы в ТГ|Мы в MAX|Поделиться точкой|Подписаться|Наш чат)");

    private static string StripFooter(string t)
    {
        if (string.IsNullOrEmpty(t)) return "";
        List<string> keep = new List<string>();
        foreach (string line in t.Replace(((char)13).ToString(), "").Split((char)10)) if (!Footer.IsMatch(line)) keep.Add(line);
        return string.Join(((char)10).ToString(), keep.ToArray()).Trim();
    }

    private object SpotDetail(string key)
    {
        string lang = Lang;
        Dictionary<string, object> strings = GameData.Strings(lang);
        Spot s = Community.Spots(false).FirstOrDefault(x => x.Key == key) ?? Community.Spots(true).FirstOrDefault(x => x.Key == key);
        if (s == null) throw new AppError("spotGone", key);
        Lake lake;
        GameData.LakeById.TryGetValue(s.Lake, out lake);
        DateTime now = DateTime.UtcNow;
        List<object> events = new List<object>();
        List<Dictionary<string, object>> evObjs = new List<Dictionary<string, object>>();
        List<SpotEvent> evList = new List<SpotEvent>();
        int fetched = 0;
        Dictionary<string, int> dirs = new Dictionary<string, int>();
        foreach (SpotEvent e in s.Events.OrderByDescending(x => x.Last).Take(60))
        {
            List<object> src = new List<object>();
            HashSet<string> seenSrc = new HashSet<string>();
            string imgPost = "";
            double deg = -1;
            double weight = 0;
            Dictionary<string, string> detail = new Dictionary<string, string>();
            string clip = "", depth = "", method = "", postText = "", temp = "";
            bool fromImage = false, mixedRaw = false;
            RecordHit rec = null;
            foreach (Report r in e.Reports)
            {
                if (seenSrc.Add(r.SourceKey))
                {
                    string[] dc = r.Guild.Length > 0 ? DiscordInfo.Lookup(r.Guild) : null;
                    src.Add(AppEnv.Obj("label", SourceName(r), "url", r.Linkable || r.Guild.Length > 0 ? r.Url : "", "members", r.SourceKey.StartsWith("dc:"),
                        "invite", dc != null && dc.Length > 1 ? dc[1] : ""));
                }
                string pk = Scans.PostKey(r.Url);
                if (pk.Length > 0)
                {
                    if (imgPost.Length == 0) imgPost = pk;
                    if (deg < 0) deg = Scans.Degrees(pk);
                    if (postText.Length == 0 && !Scans.Known(pk) && fetched < 8) { Scans.Fetch(pk); fetched++; }
                    if (postText.Length == 0) postText = Scans.Text(pk);
                }
                if (deg < 0) deg = AppEnv.GetDouble(r.Raw, "mdeg", -1);
                if (temp.Length == 0 && r.S("ptemp").Length > 0) temp = GameData.Ui(lang, "temp_" + r.S("ptemp"), r.S("ptemp"));
                if (AppEnv.GetBool(r.Raw, "mimg")) fromImage = true;
                if (AppEnv.GetBool(r.Raw, "mixed")) mixedRaw = true;
                if (rec == null && r.Raw.ContainsKey("mrec"))
                {
                    Dictionary<string, object> mr = r.Raw["mrec"] as Dictionary<string, object>;
                    if (mr != null) { rec = new RecordHit(); rec.Table = AppEnv.GetString(mr, "t"); rec.Region = AppEnv.GetString(mr, "r"); rec.Date = AppEnv.GetString(mr, "d"); rec.Bait = AppEnv.GetString(mr, "b"); }
                }
                double w = AppEnv.GetDouble(r.Raw, "weight", 0);
                if (w > weight) weight = w;
                if (clip.Length == 0) clip = r.S("clip");
                if (depth.Length == 0) depth = r.S("depth");
                if (method.Length == 0) method = r.S("method");
                foreach (string dk in DetailKeys)
                {
                    if (detail.ContainsKey(dk)) continue;
                    string v = dk == "reelSpeed" ? r.S(dk) : PostText.CleanItems(CleanDetail(r.S(dk)), lang);
                    if (v.Length > 0) detail[dk] = v;
                }
            }
            if (postText.Length > 0)
            {
                Dictionary<string, string> info = PostText.Parse(postText);
                string pv;
                if (clip.Length == 0 && info.TryGetValue("clip", out pv)) clip = pv;
                if (depth.Length == 0 && info.TryGetValue("depth", out pv)) depth = pv;
                if (info.TryGetValue("temp", out pv)) temp = GameData.Ui(lang, "temp_" + pv, pv);
                foreach (string dk in new string[] { "baitDetail", "dip", "groundbait", "pva", "reelSpeed" })
                {
                    if (detail.ContainsKey(dk) || !info.TryGetValue(dk, out pv)) continue;
                    string v = dk == "reelSpeed" ? pv : PostText.CleanItems(pv, lang);
                    if (v.Length > 0) detail[dk] = v;
                }
            }
            if (!detail.ContainsKey("baitDetail") && imgPost.Length > 0)
            {
                Dictionary<string, object> oc = ImageOcr.Result(imgPost);
                if (oc == null) ImageOcr.Request(imgPost);
                else
                {
                    List<string> bl = new List<string>(), dl = new List<string>();
                    foreach (object o in AppEnv.GetList(oc, "baits"))
                    {
                        string k = o.ToString();
                        string label = GameData.Translate(k, lang);
                        if (GameData.DipKeys.Contains(k)) dl.Add(label); else bl.Add(label);
                    }
                    if (bl.Count > 0) { detail["baitDetail"] = string.Join(" + ", bl.ToArray()); fromImage = true; }
                    if (dl.Count > 0 && !detail.ContainsKey("dip")) { detail["dip"] = string.Join(" + ", dl.ToArray()); fromImage = true; }
                }
            }
            if (!detail.ContainsKey("baitDetail"))
            {
                foreach (Report r in e.Reports) { rec = Records.Match(r); if (rec != null) break; }
                if (rec != null)
                {
                    List<string> bl = new List<string>();
                    foreach (string b in rec.Bait.Split(';')) if (b.Trim().Length > 0) bl.Add(GameData.Translate(b.Trim(), lang));
                    detail["baitDetail"] = string.Join(" + ", bl.ToArray());
                    if (weight <= 0) weight = rec.Grams / 1000.0;
                }
            }
            bool mixed = mixedRaw || PostText.Mixed(postText);
            List<string> fish = new List<string>();
            foreach (Report r in e.Reports) foreach (string f in r.Fish) { string n = GameData.Translate(f, lang); if (!fish.Contains(n)) fish.Add(n); }
            string dir = deg >= 0 ? Scans.Dir16(deg, lang) : "";
            if (dir.Length > 0) { int n; dirs.TryGetValue(dir, out n); dirs[dir] = n + 1; }
            int imgCount = 0;
            if (imgPost.Length > 0) { string[] im = Scans.Images(imgPost); imgCount = im == null ? -1 : im.Length; }
            Dictionary<string, object> eo = AppEnv.Obj("time", e.Last.ToString("o", CultureInfo.InvariantCulture), "hoursAgo", Math.Round(now.Subtract(e.Last).TotalHours, 1),
                "fish", fish.ToArray(), "weight", weight > 0 ? GameData.FormatWeight(weight * 1000, lang) : "",
                "clip", clip, "depth", depth, "method", method, "dir", dir, "detail", detail, "sources", src.ToArray(),
                "imgPost", imgPost, "imgCount", imgCount, "reports", e.Reports.Count, "postText", StripFooter(postText), "temp", temp, "fromImage", fromImage, "mixed", mixed,
                "record", rec == null ? null : AppEnv.Obj("table", GameData.Ui(lang, "table_" + rec.Table, rec.Table), "region", rec.Region, "date", rec.Date));
            events.Add(eo);
            evObjs.Add(eo);
            evList.Add(e);
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
        bool all = q["all"] == "1";
        DateTime now = DateTime.UtcNow;
        DateTime since = now.AddHours(-hours);
        List<object> rows = new List<object>();
        foreach (Spot s in Community.Spots(all))
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
                    foreach (string b in ReportBaits(r)) eb.Add(b);
                    string c = r.S("clip");
                    if (c.Length == 0)
                    {
                        string pk = Scans.PostKey(r.Url), pc;
                        if (pk.Length > 0 && PostText.Parse(Scans.Text(pk)).TryGetValue("clip", out pc)) c = pc;
                    }
                    if (c.Length > 0) { int n; clips.TryGetValue(c, out n); clips[c] = n + 1; }
                    if (sources.Add(r.SourceKey)) sourceLabels.Add(SourceName(r));
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

    private static string SourceName(Report r)
    {
        if (r.Guild.Length == 0) return r.SourceLabel;
        string[] dc = DiscordInfo.Lookup(r.Guild);
        return dc != null ? "Discord · " + dc[0] : "Discord";
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
