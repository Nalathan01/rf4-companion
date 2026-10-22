using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal sealed class Report
{
    public Dictionary<string, object> Raw;
    public string Id;
    public string Lake;
    public int X, Y;
    public DateTime Posted;
    public string Url;
    public string SourceKey;
    public string SourceLabel;
    public bool Linkable;
    public List<string> Fish = new List<string>();
    public string FishKey;
    public string Guild = "";

    public string S(string key) { return AppEnv.GetString(Raw, key); }
}

internal sealed class SpotEvent
{
    public DateTime First, Last;
    public string FishKey;
    public List<Report> Reports = new List<Report>();
    public bool Confirm;
}

internal sealed class Spot
{
    public string Key;
    public string Lake;
    public int X, Y;
    public string Mark;
    public List<Report> Reports = new List<Report>();
    public List<SpotEvent> Events = new List<SpotEvent>();
    public HashSet<Report> Confirms = new HashSet<Report>();
    public DateTime Last;
}

internal static class Community
{
    private static readonly object gate = new object();
    private static List<Report> reports = new List<Report>();
    private static Dictionary<string, Report> byId = new Dictionary<string, Report>(StringComparer.Ordinal);
    private static List<Dictionary<string, object>> skipped = new List<Dictionary<string, object>>();
    private static DateTime synced = DateTime.MinValue;
    private static List<Spot> spots;
    private static List<Spot> spotsAll;
    private static int ocrSeen = -1;
    private static readonly Regex Tg = new Regex(@"^https?://t\.me/([^/?]+)/(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex Vk = new Regex(@"vk\.com/wall(-?\d+)_", RegexOptions.IgnoreCase);
    private static readonly Regex Dc = new Regex(@"discord\.com/channels/(\d+)/(\d+)", RegexOptions.IgnoreCase);
    public static volatile bool Syncing;
    public static volatile string SyncState = "";
    public static int Version;

    private static readonly string[] Waterbodies = {
        "Mosquito Lake", "Winding Rivulet", "Kuori Lake", "Old Burg Lake", "Volkhov River", "Ladoga Lake", "Bear Lake",
        "Sura River", "Akhtuba River", "Belaya River", "Ladoga archipelago", "Norwegian Sea", "Seversky Donets River",
        "The Amber Lake", "Lower Tunguska River", "Yama River", "Copper Lake", "Elk Lake" };

    public static int Count { get { lock (gate) return reports.Count; } }
    public static DateTime Synced { get { lock (gate) return synced; } }

    public static void Load()
    {
        string path = AppEnv.DataPath("community.json");
        Dictionary<string, object> d = null;
        try { d = AppEnv.ReadJsonFile(path) as Dictionary<string, object>; }
        catch (Exception ex) { AppEnv.Log("Community", "Laden: " + ex.Message); }
        List<Report> list = new List<Report>();
        Dictionary<string, Report> ids = new Dictionary<string, Report>(StringComparer.Ordinal);
        DateTime sy = DateTime.MinValue;
        if (d != null)
        {
            foreach (object o in AppEnv.GetList(d, "reports"))
            {
                Report r = Wrap(o as Dictionary<string, object>);
                if (r == null || ids.ContainsKey(r.Id)) continue;
                ids[r.Id] = r;
                list.Add(r);
            }
            DateTimeOffset dto;
            if (AppEnv.GetDouble(d, "v", 0) >= 2 && DateTimeOffset.TryParse(AppEnv.GetString(d, "synced"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto)) sy = dto.UtcDateTime;
        }
        lock (gate)
        {
            reports = list;
            byId = ids;
            synced = sy;
            spots = null;
            Version++;
        }
    }

    private static Report Wrap(Dictionary<string, object> raw)
    {
        if (raw == null) return null;
        Report r = new Report();
        r.Raw = raw;
        r.Id = AppEnv.GetString(raw, "id");
        if (r.Id.Length == 0) return null;
        r.Lake = AppEnv.GetString(raw, "lake");
        r.X = (int)AppEnv.GetDouble(raw, "x", -1);
        r.Y = (int)AppEnv.GetDouble(raw, "y", -1);
        DateTimeOffset dto;
        if (DateTimeOffset.TryParse(AppEnv.GetString(raw, "posted"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto)) r.Posted = dto.UtcDateTime;
        r.Url = AppEnv.GetString(raw, "url");
        SetSource(r);
        Lake lake;
        GameData.LakeById.TryGetValue(r.Lake, out lake);
        SetFish(r, lake);
        return r;
    }

    private static void SetFish(Report r, Lake lake)
    {
        r.Fish = new List<string>();
        string pk = Scans.PostKey(r.Url);
        if (pk.Length > 0)
        {
            foreach (string f in PostText.Fish(Scans.Text(pk)))
            {
                if (lake != null && lake.Fish.Count > 0 && !lake.Fish.Contains(f)) continue;
                if (!r.Fish.Contains(f)) r.Fish.Add(f);
            }
        }
        if (r.Fish.Count == 0) foreach (object f in AppEnv.GetList(r.Raw, "fish"))
        {
            if (f == null) continue;
            string s = f.ToString();
            if (s.Length == 0 || r.Fish.Contains(s)) continue;
            if (lake != null && lake.Fish.Count > 0 && !lake.Fish.Contains(s)) continue;
            r.Fish.Add(s);
        }
        List<string> sorted = new List<string>(r.Fish);
        sorted.Sort(StringComparer.Ordinal);
        r.FishKey = string.Join("|", sorted.ToArray());
    }

    private static int scansSeen = -1;

    public static void QueuePosts(double hours)
    {
        DateTime since = DateTime.UtcNow.AddHours(-hours);
        List<string> keys = new List<string>();
        lock (gate) foreach (Report r in reports) { if (r.Posted < since) continue; string pk = Scans.PostKey(r.Url); if (pk.Length > 0 && !Scans.Known(pk)) keys.Add(pk); }
        foreach (string k in keys) Scans.Request(k);
    }

    private static void SetSource(Report r)
    {
        string u = r.Url;
        Match m = Tg.Match(u);
        if (m.Success) { r.SourceKey = "tg:" + m.Groups[1].Value.ToLowerInvariant(); r.SourceLabel = "Telegram @" + m.Groups[1].Value; r.Linkable = true; return; }
        m = Vk.Match(u);
        if (m.Success) { r.SourceKey = "vk:" + m.Groups[1].Value; r.SourceLabel = "VK"; r.Linkable = true; return; }
        m = Dc.Match(u);
        if (m.Success) { r.SourceKey = "dc:" + m.Groups[2].Value; r.SourceLabel = "Discord"; r.Linkable = false; r.Guild = m.Groups[1].Value; return; }
        if (u.Length == 0) { r.SourceKey = "companion:" + r.Id; r.SourceLabel = "RF4 Companion"; r.Linkable = false; return; }
        r.SourceKey = "web:" + u; r.SourceLabel = "Web"; r.Linkable = true;
    }

    public static void Sync(bool full)
    {
        if (Syncing) return;
        Syncing = true;
        int added = 0;
        try
        {
            DateTime started = DateTime.UtcNow;
            int maxAge = 336;
            DateTime sy = Synced;
            if (full) maxAge = 0;
            else if (sy > DateTime.MinValue && Count > 0)
            {
                int hours = (int)Math.Ceiling(DateTime.UtcNow.Subtract(sy).TotalHours) + 48;
                if (hours < 336) maxAge = hours;
            }
            string[] lakes = full ? Waterbodies : new string[] { null };
            for (int li = 0; li < lakes.Length; li++)
            {
                int offset = 0;
                while (true)
                {
                    string url = "https://rf4intel.com/api/posts?limit=100&offset=" + offset.ToString(CultureInfo.InvariantCulture);
                    if (lakes[li] != null) url += "&waterbody=" + Uri.EscapeDataString(lakes[li]);
                    if (maxAge > 0) url += "&max_age_hours=" + maxAge.ToString(CultureInfo.InvariantCulture);
                    Dictionary<string, object> page = AppEnv.Json.DeserializeObject(Http.GetString(url, 90000)) as Dictionary<string, object>;
                    if (page == null) break;
                    added += Merge(AppEnv.GetList(page, "posts"));
                    offset += 100;
                    int total = (int)AppEnv.GetDouble(page, "total", 0);
                    SyncState = (lakes[li] != null ? lakes[li] + ": " : "") + Math.Min(offset, total).ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture);
                    if (!AppEnv.GetBool(page, "has_more") || offset > 10000) break;
                }
            }
            lock (gate) { if (started > synced) synced = started; }
            Save();
            AppEnv.Log("Community", "Abgleich fertig, neu: " + added);
            SyncState = "";
        }
        catch (Exception ex)
        {
            AppEnv.Log("Community", "Abgleich: " + ex.Message);
            SyncState = "error";
        }
        finally { Syncing = false; }
    }

    private static int Merge(IList posts)
    {
        int added = 0;
        lock (gate)
        {
            foreach (object o in posts)
            {
                Dictionary<string, object> p = o as Dictionary<string, object>;
                if (p == null) continue;
                Dictionary<string, object> raw = Convert(p);
                if (raw == null) continue;
                Report r = Wrap(raw);
                if (r == null) continue;
                Report old;
                if (byId.TryGetValue(r.Id, out old))
                {
                    int i = reports.IndexOf(old);
                    if (i >= 0) reports[i] = r;
                    byId[r.Id] = r;
                    continue;
                }
                byId[r.Id] = r;
                reports.Add(r);
                added++;
            }
            if (added > 0) { spots = null; spotsAll = null; Version++; }
        }
        return added;
    }

    private static Dictionary<string, object> Convert(Dictionary<string, object> p)
    {
        Match m = Regex.Match(AppEnv.GetString(p, "coordinates"), @"^\s*(\d{1,4})\s*:\s*(\d{1,4})\s*$");
        if (!m.Success) return null;
        string lake = GameData.LakeIdByName(AppEnv.GetString(p, "waterbody"));
        if (lake.Length == 0) lake = GameData.LakeIdByName(AppEnv.GetString(p, "waterbody_ru_name"));
        Lake l;
        if (lake.Length == 0 || !GameData.LakeById.TryGetValue(lake, out l) || !l.HasBounds) return null;
        List<object> fish = new List<object>();
        foreach (object f in AppEnv.GetList(p, "fish")) if (f != null && f.ToString().Length > 0) fish.Add(f.ToString());
        List<object> bait = new List<object>();
        foreach (object b in AppEnv.GetList(p, "bait")) if (b != null && b.ToString().Length > 0) bait.Add(b.ToString());
        Dictionary<string, object> r = new Dictionary<string, object>();
        r["id"] = AppEnv.GetString(p, "id");
        r["lake"] = lake;
        r["x"] = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        r["y"] = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        r["fish"] = fish.ToArray();
        r["bait"] = bait.ToArray();
        r["method"] = AppEnv.GetString(p, "method");
        r["clip"] = p.ContainsKey("clip_m") ? p["clip_m"] : null;
        r["depth"] = p.ContainsKey("depth_m") ? p["depth_m"] : null;
        r["posted"] = AppEnv.GetString(p, "posted_at");
        r["url"] = AppEnv.GetString(p, "source_url");
        r["baitDetail"] = AppEnv.GetString(p, "bait_detail");
        r["lure"] = AppEnv.GetString(p, "lure_detail");
        r["rig"] = AppEnv.GetString(p, "rig");
        r["dip"] = AppEnv.GetString(p, "dip");
        r["groundbait"] = AppEnv.GetString(p, "groundbait");
        r["dryMix"] = AppEnv.GetString(p, "dry_mix");
        r["pva"] = AppEnv.GetString(p, "pva");
        r["reelSpeed"] = AppEnv.GetString(p, "reel_speed");
        r["weight"] = p.ContainsKey("weight_kg") ? p["weight_kg"] : null;
        r["type"] = AppEnv.GetString(p, "post_type");
        return r;
    }

    private static void Save()
    {
        List<object> raws = new List<object>();
        string sy;
        lock (gate)
        {
            foreach (Report r in reports) raws.Add(r.Raw);
            sy = synced > DateTime.MinValue ? synced.ToString("o", CultureInfo.InvariantCulture) : "";
        }
        AppEnv.WriteJsonFile(AppEnv.DataPath("community.json"), AppEnv.Obj("v", 2, "synced", sy, "source", "https://rf4intel.com", "reports", raws));
    }

    private static readonly Regex SetupWords = new Regex("(?i)наживк|приманк|прикорм|насадк|дип|бойл|pop ?up|кукуруз|червь|червя|опарыш|пеллетс|пелетс|воблер|блесн|джиг|твистер|виброхвост|силикон|бутерброд|тесто|мотыл|креветк|кальмар|филе|живец|монтаж|оснастк|пилкер|балансир");

    public static bool HasSetup(Report r)
    {
        foreach (object b in AppEnv.GetList(r.Raw, "bait")) if (b != null && b.ToString().Trim().Length > 0) return true;
        foreach (string k in new string[] { "baitDetail", "lure", "rig", "groundbait", "dip", "pva", "dryMix" }) if (r.S(k).Trim().Length > 0) return true;
        string pk = Scans.PostKey(r.Url);
        if (pk.Length == 0) return false;
        if (SetupWords.IsMatch(Scans.Text(pk))) return true;
        Dictionary<string, object> oc = ImageOcr.Result(pk);
        return oc != null && AppEnv.GetList(oc, "baits").Count > 0;
    }

    public static List<Spot> Spots(bool all)
    {
        lock (gate)
        {
            if (ocrSeen != ImageOcr.Version) { ocrSeen = ImageOcr.Version; spots = null; spotsAll = null; }
            if (scansSeen != Scans.Version)
            {
                scansSeen = Scans.Version;
                spots = null; spotsAll = null;
                foreach (Report r in reports) { Lake lk; GameData.LakeById.TryGetValue(r.Lake, out lk); SetFish(r, lk); }
            }
            if (all)
            {
                if (spotsAll == null) spotsAll = BuildSpots(reports);
                return spotsAll;
            }
            if (spots == null)
            {
                List<Report> setup = new List<Report>(), rest = new List<Report>();
                foreach (Report r in reports) { if (HasSetup(r)) setup.Add(r); else rest.Add(r); }
                spots = BuildSpots(setup);
                AttachConfirms(spots, rest);
            }
            return spots;
        }
    }

    public static void QueueRecentOcr(double hours)
    {
        DateTime since = DateTime.UtcNow.AddHours(-hours);
        List<string> posts = new List<string>();
        lock (gate)
        {
            foreach (Report r in reports)
            {
                if (r.Posted < since) continue;
                string pk = Scans.PostKey(r.Url);
                if (pk.Length == 0 || HasSetup(r)) continue;
                posts.Add(pk);
            }
        }
        foreach (string p in posts) ImageOcr.Request(p);
    }

    private static List<Spot> BuildSpots(List<Report> all)
    {
        Dictionary<string, List<Report>> byLake = new Dictionary<string, List<Report>>(StringComparer.OrdinalIgnoreCase);
        foreach (Report r in all)
        {
            if (r.X < 0 || r.Y < 0 || r.Lake.Length == 0 || r.Posted == DateTime.MinValue) continue;
            List<Report> l;
            if (!byLake.TryGetValue(r.Lake, out l)) { l = new List<Report>(); byLake[r.Lake] = l; }
            l.Add(r);
        }
        List<Spot> result = new List<Spot>();
        foreach (KeyValuePair<string, List<Report>> kv in byLake)
        {
            List<Spot> lakeSpots = new List<Spot>();
            Dictionary<string, Spot> byMark = new Dictionary<string, Spot>(StringComparer.Ordinal);
            Dictionary<string, List<Report>> byCoord = new Dictionary<string, List<Report>>(StringComparer.Ordinal);
            foreach (Report r in kv.Value)
            {
                SeaMark sm = SeaMarks.Nearest(r.Lake, r.X, r.Y);
                if (sm != null)
                {
                    Spot s;
                    if (!byMark.TryGetValue(sm.Key, out s)) { s = new Spot(); s.Lake = r.Lake; s.X = (int)Math.Round(sm.X); s.Y = (int)Math.Round(sm.Y); s.Mark = sm.Key; byMark[sm.Key] = s; lakeSpots.Add(s); }
                    s.Reports.Add(r);
                    continue;
                }
                string ck = r.X.ToString(CultureInfo.InvariantCulture) + ":" + r.Y.ToString(CultureInfo.InvariantCulture);
                List<Report> cl;
                if (!byCoord.TryGetValue(ck, out cl)) { cl = new List<Report>(); byCoord[ck] = cl; }
                cl.Add(r);
            }
            List<List<Report>> groups = new List<List<Report>>(byCoord.Values);
            groups.Sort((a, b) => b.Count.CompareTo(a.Count));
            List<Spot> coordSpots = new List<Spot>();
            foreach (List<Report> g in groups)
            {
                Spot target = null;
                foreach (Spot s in coordSpots) if (Math.Abs(s.X-g[0].X) <= 2 && Math.Abs(s.Y-g[0].Y) <= 2) { target = s; break; }
                if (target == null) { target = new Spot(); target.Lake = kv.Key; target.X = g[0].X; target.Y = g[0].Y; coordSpots.Add(target); }
                target.Reports.AddRange(g);
            }
            lakeSpots.AddRange(coordSpots);
            foreach (Spot s in lakeSpots)
            {
                s.Key = s.Lake + ":" + (s.Mark ?? (s.X.ToString(CultureInfo.InvariantCulture) + ":" + s.Y.ToString(CultureInfo.InvariantCulture)));
                BuildEvents(s);
                result.Add(s);
            }
        }
        return result;
    }

    public const double ConfirmHours = 48;

    private static void AttachConfirms(List<Spot> list, List<Report> rest)
    {
        Dictionary<string, List<Spot>> byLake = new Dictionary<string, List<Spot>>(StringComparer.OrdinalIgnoreCase);
        foreach (Spot s in list)
        {
            List<Spot> l;
            if (!byLake.TryGetValue(s.Lake, out l)) { l = new List<Spot>(); byLake[s.Lake] = l; }
            l.Add(s);
        }
        HashSet<Spot> touched = new HashSet<Spot>();
        foreach (Report r in rest)
        {
            if (r.X < 0 || r.Y < 0 || r.Posted == DateTime.MinValue) continue;
            List<Spot> l;
            if (!byLake.TryGetValue(r.Lake, out l)) continue;
            SeaMark sm = SeaMarks.Nearest(r.Lake, r.X, r.Y);
            foreach (Spot s in l)
            {
                bool here = sm != null ? s.Mark == sm.Key : (s.Mark == null && Math.Abs(s.X-r.X) <= 2 && Math.Abs(s.Y-r.Y) <= 2);
                if (!here) continue;
                bool fits = false;
                foreach (Report sr in s.Reports)
                {
                    if (s.Confirms.Contains(sr)) continue;
                    double h = r.Posted.Subtract(sr.Posted).TotalHours;
                    if (h >= 0 && h <= ConfirmHours) { fits = true; break; }
                }
                if (!fits) continue;
                s.Reports.Add(r);
                s.Confirms.Add(r);
                touched.Add(s);
                break;
            }
        }
        foreach (Spot s in touched) { s.Events.Clear(); BuildEvents(s); }
    }

    private static void BuildEvents(Spot s)
    {
        s.Reports.Sort((a, b) => a.Posted.CompareTo(b.Posted));
        List<SpotEvent> open = new List<SpotEvent>();
        foreach (Report r in s.Reports)
        {
            SpotEvent hit = null;
            foreach (SpotEvent e in open)
            {
                if (r.Posted.Subtract(e.Last).TotalHours > 6) continue;
                bool fishOk = e.FishKey.Length == 0 || r.FishKey.Length == 0 || e.FishKey == r.FishKey;
                if (!fishOk) continue;
                hit = e;
                break;
            }
            if (hit == null) { hit = new SpotEvent(); hit.First = r.Posted; hit.FishKey = r.FishKey; s.Events.Add(hit); open.Add(hit); }
            if (hit.FishKey.Length == 0 && r.FishKey.Length > 0) hit.FishKey = r.FishKey;
            hit.Reports.Add(r);
            hit.Last = r.Posted;
            open.RemoveAll(e => r.Posted.Subtract(e.Last).TotalHours > 6);
        }
        foreach (SpotEvent e in s.Events)
        {
            bool all = e.Reports.Count > 0;
            foreach (Report r in e.Reports) if (!s.Confirms.Contains(r)) { all = false; break; }
            e.Confirm = all;
        }
        s.Last = s.Reports.Count > 0 ? s.Reports[s.Reports.Count-1].Posted : DateTime.MinValue;
    }
}

internal static class Http
{
    public static string GetString(string url, int timeout)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "RF4Companion/2.0";
        req.Timeout = timeout;
        req.ReadWriteTimeout = timeout;
        req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        using (WebResponse res = req.GetResponse())
        using (StreamReader r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
        {
            return r.ReadToEnd();
        }
    }

    public static byte[] GetBytes(string url, int timeout)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "RF4Companion/2.0";
        req.Timeout = timeout;
        req.ReadWriteTimeout = timeout;
        using (WebResponse res = req.GetResponse())
        using (Stream s = res.GetResponseStream())
        using (MemoryStream ms = new MemoryStream())
        {
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
