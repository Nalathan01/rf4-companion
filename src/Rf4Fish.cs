using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal static class Rf4Fish
{
    public const string Site = "https://rf4.fish";
    private static readonly Dictionary<string, string> LakeAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "Old Burg", "old_burg_lake" }, { "Kouri Lake", "kuori_lake" } };
    private static readonly Regex Coord = new Regex(@"^\s*(\d{1,3})\s*[:：]\s*(\d{1,3})\s*$");
    private static readonly Regex BaitSplit = new Regex(@"[+＋,，、/;；\s\d.]+");
    private static readonly string[] ImageKinds = { "position", "config", "fish" };
    private static DateTime last = DateTime.MinValue;
    private static volatile bool running;

    public static void SyncSpots(bool force)
    {
        if (running || (!force && DateTime.UtcNow.Subtract(last).TotalMinutes < 30)) return;
        running = true;
        last = DateTime.UtcNow;
        try
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-8);
            List<Dictionary<string, object>> raws = new List<Dictionary<string, object>>();
            for (int offset = 0; offset < 3000; offset += 100)
            {
                Dictionary<string, object> page = AppEnv.Json.DeserializeObject(Http.GetString(Site + "/api/wechat/positions?limit=100&offset=" + offset.ToString(CultureInfo.InvariantCulture), 60000)) as Dictionary<string, object>;
                if (page == null) break;
                IList list = AppEnv.GetList(page, "positions");
                bool old = false, known = false;
                foreach (object o in list)
                {
                    Dictionary<string, object> p = o as Dictionary<string, object>;
                    if (p == null) continue;
                    DateTime posted = Posted(p);
                    if (posted < cutoff) { old = true; continue; }
                    Dictionary<string, object> r = Convert(p, posted);
                    if (r == null) continue;
                    Report prev = Community.Get((string)r["id"]);
                    if (prev != null) known = true;
                    if (prev == null || !AppEnv.GetBool(prev.Raw, "mimg")) raws.Add(r);
                }
                if (old || known || list.Count < 100) break;
                Thread.Sleep(1000);
            }
            int added = Community.ApplyMirror(raws, DateTime.MinValue);
            AppEnv.Log("rf4.fish", raws.Count + " WeChat-Meldungen gelesen, neu oder geändert: " + added);
        }
        catch (Exception ex) { AppEnv.Log("rf4.fish", ex.Message); }
        finally { running = false; }
    }

    private static DateTime Posted(Dictionary<string, object> p)
    {
        DateTime t;
        return DateTime.TryParse(AppEnv.GetString(p, "publish_date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t) ? t : DateTime.MinValue;
    }

    private static string LakeId(Dictionary<string, object> p)
    {
        string en = AppEnv.GetString(p, "map_name_en").Trim();
        string id;
        if (LakeAlias.TryGetValue(en, out id)) return id;
        id = GameData.LakeIdByName(en);
        return id.Length > 0 ? id : GameData.LakeIdByName(AppEnv.GetString(p, "map_name"));
    }

    private static Dictionary<string, object> Convert(Dictionary<string, object> p, DateTime posted)
    {
        string post = AppEnv.GetString(p, "post_id");
        Match m = Coord.Match(AppEnv.GetString(p, "coordinate"));
        if (post.Length == 0 || !m.Success) return null;
        string lake = LakeId(p);
        Lake l;
        if (lake.Length == 0 || !GameData.LakeById.TryGetValue(lake, out l) || !l.HasBounds) return null;
        string fish = AppEnv.GetString(p, "fish_name_en").Trim();
        string baitRaw = AppEnv.GetString(p, "bait").Trim();
        List<string> feed;
        string[] baits = Baits(baitRaw, out feed);
        List<object> imgs = new List<object>();
        Dictionary<string, object> iu = p.ContainsKey("image_urls") ? p["image_urls"] as Dictionary<string, object> : null;
        if (iu != null) foreach (string kind in ImageKinds) foreach (object u in AppEnv.GetList(iu, kind)) if (u != null && u.ToString().StartsWith("https://", StringComparison.Ordinal)) imgs.Add(u.ToString());
        bool config = iu != null && AppEnv.GetList(iu, "config").Count > 0;
        string text = (AppEnv.GetString(p, "title").Trim() + "\n" + AppEnv.GetString(p, "content").Trim()).Trim();
        double clip;
        Dictionary<string, object> r = new Dictionary<string, object>();
        r["id"] = "wx:" + post;
        r["lake"] = lake;
        r["x"] = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        r["y"] = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        r["posted"] = posted.ToString("o", CultureInfo.InvariantCulture);
        r["url"] = Site + "/spots/" + Uri.EscapeDataString(post);
        r["fish"] = fish.Length > 0 ? new object[] { fish } : new object[0];
        r["bait"] = new object[0];
        r["baitDetail"] = string.Join(" + ", baits);
        r["groundbait"] = string.Join(" + ", feed);
        r["baitRaw"] = baitRaw;
        r["clip"] = double.TryParse(AppEnv.GetString(p, "meter").Trim().Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out clip) && clip > 0 ? (object)clip : null;
        r["text"] = text;
        r["textLang"] = "zh";
        r["imgs"] = imgs.ToArray();
        r["cfg"] = iu != null ? AppEnv.GetList(iu, "config").Cast<object>().Where(u => u != null && u.ToString().StartsWith("https://", StringComparison.Ordinal)).Select(u => (object)u.ToString()).ToArray() : new object[0];
        r["type"] = "spot";
        r["src"] = "wechat";
        r["ms"] = baits.Length > 0 || baitRaw.Length > 0 || config;
        return r;
    }

    private static Dictionary<string, object> setupCache;

    public static string[] SetupBaits(Report r)
    {
        if (setupCache == null)
        {
            try { setupCache = AppEnv.ReadJsonFile(AppEnv.DataPath("wx_setup.json")) as Dictionary<string, object>; } catch (Exception) { }
            if (setupCache == null) setupCache = new Dictionary<string, object>();
        }
        object hit;
        if (setupCache.TryGetValue(r.Id, out hit)) return ((IList)hit).Cast<object>().Select(o => o.ToString()).ToArray();
        IList urls = AppEnv.GetList(r.Raw, "cfg");
        if (urls.Count == 0) urls = AppEnv.GetList(r.Raw, "imgs");
        List<string> found = new List<string>();
        foreach (object u in urls.Cast<object>().Take(3))
        {
            try
            {
                List<string> b;
                if (ImageOcr.ReadSetupZh(Http.GetBytes(u.ToString(), 30000), out b)) { found = b; break; }
            }
            catch (Exception ex) { AppEnv.Log("rf4.fish", "Setup-Bild: " + ex.Message); return new string[0]; }
        }
        setupCache[r.Id] = found.ToArray();
        return found.ToArray();
    }

    public static void SaveSetupCache()
    {
        if (setupCache == null) return;
        DateTime cutoff = DateTime.UtcNow.AddDays(-9);
        HashSet<string> keep = new HashSet<string>(Community.All().Where(x => x.Posted >= cutoff).Select(x => x.Id), StringComparer.Ordinal);
        foreach (string k in setupCache.Keys.Where(k => !keep.Contains(k)).ToList()) setupCache.Remove(k);
        AppEnv.WriteJsonFile(AppEnv.DataPath("wx_setup.json"), setupCache);
    }

    private static string[] Baits(string raw, out List<string> feed)
    {
        List<string> keys = new List<string>();
        feed = new List<string>();
        foreach (string part in BaitSplit.Split(raw))
        {
            string t = part.Trim();
            if (t.Length < 2) continue;
            string k = GameData.Untranslate(t, "zh");
            if (k == t || keys.Contains(k) || feed.Contains(k)) continue;
            if (GameData.IsBait(k) || GameData.DipKeys.Contains(k)) keys.Add(k); else feed.Add(k);
        }
        return keys.ToArray();
    }

    private static readonly object infoGate = new object();
    private static Dictionary<string, object> info;
    private static volatile bool infoLoading;
    public static volatile string InfoError = "";

    private static string InfoPath { get { return AppEnv.DataPath("rf4fish_info.json"); } }

    public static Dictionary<string, object> Info()
    {
        Dictionary<string, object> cur;
        lock (infoGate)
        {
            if (info == null) { try { info = AppEnv.ReadJsonFile(InfoPath) as Dictionary<string, object>; } catch (Exception) { } }
            cur = info;
        }
        DateTime fetched;
        bool stale = cur == null || !DateTime.TryParse(AppEnv.GetString(cur, "fetched"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out fetched) || DateTime.UtcNow.Subtract(fetched).TotalHours >= 24;
        if (stale && !infoLoading)
        {
            infoLoading = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { RefreshInfo(); InfoError = ""; }
                catch (Exception ex) { InfoError = ex.Message; AppEnv.Log("rf4.fish", "Nachschlagedaten: " + ex.Message); }
                finally { infoLoading = false; }
            });
        }
        return cur;
    }

    private static void RefreshInfo()
    {
        List<object> trophies = new List<object>();
        foreach (object o in (AppEnv.Json.DeserializeObject(Http.GetString(Site + "/api/trophy-weights", 60000)) as IList) ?? new ArrayList())
        {
            Dictionary<string, object> t = o as Dictionary<string, object>;
            if (t == null || AppEnv.GetString(t, "name").Length == 0) continue;
            trophies.Add(AppEnv.Obj("fish", AppEnv.GetString(t, "name"), "rarity", AppEnv.GetString(t, "rarity"), "card", AppEnv.GetDouble(t, "cardValue", 0), "stars", AppEnv.GetDouble(t, "stars", 0)));
        }
        Thread.Sleep(500);
        List<object> rods = Table(Http.GetString(Site + "/RodList.csv", 60000), h =>
            Row(h, "Rod", "Type", "Lvl", "LT", "HT", "Action", "Power", "Length", "Max Load", "Cost", "Bonus", "Bonus 2", "Bonus 3", "Bonus 4", "Bonus 5", "Bonus 6", "Bonus 7", "Bonus 8"),
            v => new object[] { v[0], v[1], v[2], Range(v[3], v[4]), v[5], v[6], v[7], v[8], v[9], string.Join(", ", v.Skip(10).Where(x => x.Length > 0).ToArray()) });
        Thread.Sleep(500);
        List<object> lines = Table(Http.GetString(Site + "/LinesData.csv", 60000), h =>
            Row(h, "Brand", "Name", "Type", "Length", "Diameter", "Load Capacity", "Player Level", "Price"),
            v => new object[] { v[1], v[0], v[2], v[3], v[4], v[5], v[6], v[7] });
        Thread.Sleep(500);
        List<object> lures = Table(Http.GetString(Site + "/LuresData.csv", 60000), h =>
            Row(h, "Brand", "Name", "Type", "Size", "Mass", "Depth", "Min Allowed Hook Size", "Max Allowed Hook Size", "Price"),
            v => new object[] { v[1], v[0], v[2], v[3], v[4], v[5], Range(HookSize(v[6]), HookSize(v[7])), v[8] });
        Thread.Sleep(500);
        List<object> reels = Reels(Http.GetString(Site + "/data/reels.csv", 60000));
        Dictionary<string, object> d = AppEnv.Obj("fetched", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), "source", Site,
            "trophies", trophies, "rods", rods, "lines", lines, "lures", lures, "reels", reels);
        AppEnv.WriteJsonFile(InfoPath, d);
        lock (infoGate) info = d;
        AppEnv.Log("rf4.fish", "Nachschlagedaten: " + trophies.Count + " Fische, " + rods.Count + " Ruten, " + reels.Count + " Rollen, " + lines.Count + " Schnüre, " + lures.Count + " Kunstköder");
    }

    private static string Range(string a, string b)
    {
        a = a.Trim(); b = b.Trim();
        if (a.Length == 0 || a == "-") return b == "-" ? "" : b;
        if (b.Length == 0 || b == "-" || b == a) return a;
        return a + "-" + b;
    }

    private static string HookSize(string s)
    {
        Match m = Regex.Match(s, @"\(([^)]+)\)");
        return m.Success ? m.Groups[1].Value.Trim() : s.Trim();
    }

    private static Func<string[], string[]> Row(string[] header, params string[] cols)
    {
        int[] idx = cols.Select(c => Array.FindIndex(header, h => string.Equals(h.Trim(), c, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (idx[0] < 0 || idx[1] < 0) throw new InvalidOperationException("CSV-Spalten geändert: " + cols[0] + ", " + cols[1]);
        return v => idx.Select(i => i >= 0 && i < v.Length ? v[i].Trim() : "").ToArray();
    }

    private static List<object> Table(string csv, Func<string[], Func<string[], string[]>> pick, Func<string[], object[]> shape)
    {
        List<string[]> rows = Csv(csv);
        List<object> outL = new List<object>();
        if (rows.Count < 2) return outL;
        Func<string[], string[]> get = pick(rows[0]);
        for (int i = 1; i < rows.Count; i++)
        {
            string[] v = get(rows[i]);
            if (v[0].Length == 0 && v[1].Length == 0) continue;
            outL.Add(shape(v));
        }
        return outL;
    }

    private static readonly HashSet<string> ReelTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Spinning", "Conventional", "Classic Baitcasting", "Low-Profile Baitcasting" };

    private static List<object> Reels(string csv)
    {
        List<object> outL = new List<object>();
        foreach (string[] r in Csv(csv))
        {
            if (r.Length < 25 || r[1].Trim().Length == 0 || !ReelTypes.Contains(r[2].Trim())) continue;
            Func<int, string> c = i => { string s = r[i].Trim(); return s == "-" || s == "?" ? "" : s; };
            string drag = c(18).Split(' ')[0];
            outL.Add(new object[] { c(1), c(2), c(3), c(8), c(14), c(11), drag, c(21), c(24) });
        }
        return outL;
    }

    private static List<string[]> Csv(string text)
    {
        List<string[]> rows = new List<string[]>();
        List<string> row = new List<string>();
        StringBuilder cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
                continue;
            }
            if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Length = 0; }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Length = 0;
                rows.Add(row.ToArray()); row.Clear();
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    private static readonly string[][] ItemCols = {
        new string[] { "rods", "itName", "itType", "itLevel", "itTest", "itAction", "itPower", "itLength", "itMaxLoad", "itPrice", "itBonus" },
        new string[] { "reels", "itName", "itType", "itSize", "itGear", "itSpeed", "itCapacity", "itDrag", "itMech", "itPrice" },
        new string[] { "lines", "itName", "itBrand", "itType", "itLength", "itDiameter", "itLoad", "itLevel", "itPrice" },
        new string[] { "lures", "itName", "itBrand", "itType", "itSize", "itMass", "itDepth", "itHooks", "itPrice" }
    };

    public static object Items(string kind, string lang)
    {
        string[] cols = ItemCols.FirstOrDefault(c => c[0] == kind);
        if (cols == null) throw new AppError("unknown", kind);
        Dictionary<string, object> d = Info();
        List<object> rows = new List<object>();
        if (d != null)
            foreach (object o in AppEnv.GetList(d, kind))
            {
                IList a = o as IList;
                if (a == null || a.Count == 0) continue;
                object[] v = new object[a.Count];
                for (int i = 0; i < a.Count; i++) v[i] = a[i] == null ? "" : a[i].ToString();
                v[0] = GameData.Translate((string)v[0], lang);
                rows.Add(v);
            }
        return AppEnv.Obj("kind", kind, "cols", cols.Skip(1).ToArray(), "rows", rows, "loading", d == null && infoLoading, "error", d == null ? InfoError : "",
            "fetched", d == null ? "" : AppEnv.GetString(d, "fetched"), "source", Site);
    }

    public static Dictionary<string, object[]> TrophyInfo()
    {
        Dictionary<string, object[]> m = new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, object> d = Info();
        if (d == null) return m;
        foreach (object o in AppEnv.GetList(d, "trophies"))
        {
            Dictionary<string, object> t = o as Dictionary<string, object>;
            if (t != null) m[AppEnv.GetString(t, "fish")] = new object[] { AppEnv.GetString(t, "rarity"), (int)AppEnv.GetDouble(t, "card", 0), (int)AppEnv.GetDouble(t, "stars", 0) };
        }
        return m;
    }
}
