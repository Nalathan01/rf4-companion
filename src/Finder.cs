using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;

internal static class Finder
{
    public const string KoiGroup = "grp:koi";
    private static readonly string[] KoiNames = { "Kohaku", "Hi Utsuri", "Orenji Ogon", "Narumi Asagi", "Mameshibori Goshiki", "Yotsushiro", "Midori goi", "Showa Sanshoku", "Taisho Sanke", "Tancho", "Shiro Utsuri", "Ki Utsuri", "Asagi", "Shusui", "Ogon", "Kujaku", "Chagoi", "Soragoi", "Karashigoi", "Benigoi", "Kumonryu", "Ochiba" };

    private sealed class Agg
    {
        public int N, N48;
        public DateTime Last = DateTime.MinValue, First = DateTime.MaxValue;
        public HashSet<string> Fish = new HashSet<string>();

        public void Add(RecordHit h, DateTime now)
        {
            N++;
            DateTime t = Seen(h);
            if (now.Subtract(t).TotalHours <= 48) N48++;
            if (t > Last) Last = t;
            if (t < First) First = t;
            Fish.Add(h.Fish);
        }
    }

    private static DateTime Seen(RecordHit h)
    {
        return h.Seen != DateTime.MinValue ? h.Seen : DateTime.SpecifyKind(h.Day.AddHours(12), DateTimeKind.Utc);
    }

    public static string BaitLabel(string bait, string lang)
    {
        List<string> parts = new List<string>();
        foreach (string p in bait.Split(';'))
        {
            string x = p.Trim();
            if (x.Length > 0) parts.Add(GameData.Translate(x, lang));
        }
        return string.Join(" + ", parts.ToArray());
    }

    private static readonly Dictionary<string, string> keyMemo = new Dictionary<string, string>(StringComparer.Ordinal);

    public static string BaitKey(string bait)
    {
        lock (keyMemo)
        {
            string k;
            if (keyMemo.TryGetValue(bait, out k)) return k;
            List<string> parts = bait.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            parts.Sort(StringComparer.Ordinal);
            k = string.Join("; ", parts.ToArray());
            keyMemo[bait] = k;
            return k;
        }
    }

    private static string PrevWeek(string week)
    {
        DateTime d;
        if (!DateTime.TryParseExact(week, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return "";
        return d.AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static double Hours(DateTime now, DateTime t)
    {
        return t == DateTime.MinValue ? -1 : Math.Round(now.Subtract(t).TotalHours, 1);
    }

    public static object Find(NameValueCollection q, string lang, Dictionary<string, object> strings)
    {
        string fish = q["fish"] ?? "";
        string lakeSel = q["lake"] ?? "";
        string region = q["region"] ?? "";
        string table = q["table"] ?? "";
        string win = q["win"] ?? "week";
        string search = (q["q"] ?? "").Trim();
        DateTime now = DateTime.UtcNow;
        List<RecordHit> all = Records.All();
        string week = Records.CurrentWeek();
        string prev = PrevWeek(week);
        DateTime reset = Records.ResetUtc(week);
        bool resetWindow = Records.InResetWindow(now) || (reset != DateTime.MinValue && now.Subtract(reset).TotalHours < 8);

        Func<RecordHit, bool> baseFilter = h => (region.Length == 0 || h.Region == region) && (table.Length == 0 || h.Table == table);
        Func<RecordHit, bool> inWin = h =>
        {
            if (h.Week != week) return false;
            if (win == "reset24") return reset != DateTime.MinValue && Seen(h) < reset.AddHours(24);
            if (win == "48h") return now.Subtract(Seen(h)).TotalHours <= 48;
            return true;
        };
        List<RecordHit> cur = all.Where(h => baseFilter(h) && inWin(h)).ToList();
        if (region.Length == 0)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            cur = cur.Where(h => seen.Add(h.Fish + "|" + h.Lake + "|" + h.Grams + "|" + h.Date + "|" + h.Table + "|" + BaitKey(h.Bait))).ToList();
        }

        Dictionary<string, int> fishCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (RecordHit h in cur) { int n; fishCount.TryGetValue(h.Fish, out n); fishCount[h.Fish] = n + 1; }
        List<object> fishList = new List<object>();
        int koiN = fishCount.Where(kv => KoiNames.Contains(kv.Key)).Sum(kv => kv.Value);
        if (koiN > 0) fishList.Add(AppEnv.Obj("key", KoiGroup, "name", GameData.Ui(lang, "tgtKoiGroup", "Alle Koi"), "n", koiN));
        foreach (KeyValuePair<string, int> kv in fishCount.OrderByDescending(kv => kv.Value)) fishList.Add(AppEnv.Obj("key", kv.Key, "name", GameData.Translate(kv.Key, lang), "n", kv.Value));

        HashSet<string> regions = new HashSet<string>(all.Where(h => h.Week == week).Select(h => h.Region));
        HashSet<string> tables = new HashSet<string>(all.Where(h => h.Week == week).Select(h => h.Table));
        Dictionary<string, object> res = AppEnv.Obj(
            "week", week, "total", cur.Count, "fishList", fishList,
            "cloud", AppEnv.Obj("last", Cloud.Last == DateTime.MinValue ? "" : Cloud.Last.ToString("o", CultureInfo.InvariantCulture), "error", Cloud.Error, "loaded", Records.Loaded),
            "regions", regions.Where(r => r.Length > 0).OrderBy(r => r).ToArray(),
            "tables", tables.Where(t => t.Length > 0).OrderBy(t => "nlbum".IndexOf(t) < 0 ? 9 : "nlbum".IndexOf(t)).Select(t => (object)AppEnv.Obj("key", t, "name", GameData.Ui(lang, "table_" + t, t))).ToArray(),
            "reset", AppEnv.Obj("window", resetWindow, "at", reset == DateTime.MinValue ? "" : reset.ToString("o", CultureInfo.InvariantCulture), "hoursSince", Hours(now, reset)));

        if (search.Length >= 2)
        {
            Dictionary<string, Agg> hits = new Dictionary<string, Agg>(StringComparer.Ordinal);
            foreach (RecordHit h in cur)
            {
                string label = BaitLabel(h.Bait, lang);
                if (label.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 && h.Bait.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Agg a;
                if (!hits.TryGetValue(BaitKey(h.Bait), out a)) { a = new Agg(); hits[BaitKey(h.Bait)] = a; }
                a.Add(h, now);
            }
            res["search"] = hits.OrderByDescending(kv => kv.Value.N).Take(40).Select(kv => (object)AppEnv.Obj(
                "name", BaitLabel(kv.Key, lang), "n", kv.Value.N, "n48", kv.Value.N48, "lastH", Hours(now, kv.Value.Last),
                "fish", kv.Value.Fish.Select(f => GameData.Translate(f, lang)).OrderBy(f => f).ToArray())).ToArray();
        }

        if (fish.Length == 0) return res;
        HashSet<string> set = fish == KoiGroup ? new HashSet<string>(KoiNames) : new HashSet<string> { fish };
        List<RecordHit> fr = cur.Where(h => set.Contains(h.Fish)).ToList();

        Dictionary<string, Agg> lakes = new Dictionary<string, Agg>(StringComparer.Ordinal);
        foreach (RecordHit h in fr)
        {
            Agg a;
            if (!lakes.TryGetValue(h.Lake, out a)) { a = new Agg(); lakes[h.Lake] = a; }
            a.Add(h, now);
        }
        List<KeyValuePair<string, Agg>> lakeOrder = lakes.OrderByDescending(kv => kv.Value.N48).ThenByDescending(kv => kv.Value.N).ToList();
        string lake = lakeSel.Length > 0 ? lakeSel : (lakeOrder.Count > 0 ? lakeOrder[0].Key : "");
        if (lake.Length == 0)
        {
            foreach (Lake lk in GameData.Lakes) if (lk.Fish.Any(set.Contains)) { lake = lk.Id; break; }
        }
        Lake lakeObj;
        GameData.LakeById.TryGetValue(lake, out lakeObj);

        Dictionary<string, Agg> baits = new Dictionary<string, Agg>(StringComparer.Ordinal);
        List<RecordHit> lr = fr.Where(h => h.Lake == lake).ToList();
        foreach (RecordHit h in lr)
        {
            Agg a;
            if (!baits.TryGetValue(BaitKey(h.Bait), out a)) { a = new Agg(); baits[BaitKey(h.Bait)] = a; }
            a.Add(h, now);
        }

        List<object> prevBaits = new List<object>();
        if (lr.Count == 0)
        {
            Dictionary<string, int> pc = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (RecordHit h in all) if (h.Week == prev && h.Lake == lake && set.Contains(h.Fish) && baseFilter(h)) { string bk = BaitKey(h.Bait); int n; pc.TryGetValue(bk, out n); pc[bk] = n + 1; }
            foreach (KeyValuePair<string, int> kv in pc.OrderByDescending(kv => kv.Value).Take(5)) prevBaits.Add(AppEnv.Obj("name", BaitLabel(kv.Key, lang), "n", kv.Value));
        }

        List<object> spots = new List<object>();
        DateTime since = now.AddHours(-72);
        foreach (Spot s in Community.Spots(false))
        {
            if (s.Lake != lake || s.Last < since) continue;
            List<SpotEvent> ev = s.Events.Where(e => e.Last >= since && e.Reports.Any(r => r.Fish.Any(set.Contains))).ToList();
            if (ev.Count == 0) continue;
            Dictionary<string, int> sb = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (SpotEvent e in ev) foreach (Report r in e.Reports) foreach (string b in Api.ReportBaits(r)) { int n; sb.TryGetValue(b, out n); sb[b] = n + 1; }
            DateTime last = ev.Max(e => e.Last);
            spots.Add(AppEnv.Obj("key", s.Key, "name", s.Mark != null ? SeaMarks.Label(s.Mark, strings) : s.X.ToString(CultureInfo.InvariantCulture) + ":" + s.Y.ToString(CultureInfo.InvariantCulture),
                "hoursAgo", Hours(now, last), "catches", ev.Count,
                "baits", sb.OrderByDescending(kv => kv.Value).Take(3).Select(kv => GameData.Translate(kv.Key, lang)).ToArray()));
        }
        spots.Sort((a, b) => ((double)((Dictionary<string, object>)a)["hoursAgo"]).CompareTo((double)((Dictionary<string, object>)b)["hoursAgo"]));

        List<object> recs = lr.OrderByDescending(h => h.Grams).Take(60).Select(h => (object)AppEnv.Obj(
            "fish", GameData.Translate(h.Fish, lang), "weight", GameData.FormatWeight(h.Grams, lang), "bait", BaitLabel(h.Bait, lang),
            "date", h.Date, "table", GameData.Ui(lang, "table_" + h.Table, h.Table), "region", h.Region)).ToList();

        res["fish"] = fish;
        res["fishName"] = fish == KoiGroup ? GameData.Ui(lang, "tgtKoiGroup", "Alle Koi") : GameData.Translate(fish, lang);
        res["isKoi"] = set.Overlaps(KoiNames);
        res["n"] = fr.Count;
        res["n48"] = fr.Count(h => now.Subtract(Seen(h)).TotalHours <= 48);
        res["lake"] = lake;
        res["lakeName"] = lakeObj != null ? lakeObj.Name(lang) : lake;
        res["lakes"] = lakeOrder.Select(kv => (object)AppEnv.Obj("id", kv.Key, "name", GameData.LakeById.ContainsKey(kv.Key) ? GameData.LakeById[kv.Key].Name(lang) : kv.Key,
            "n", kv.Value.N, "n48", kv.Value.N48, "lastH", Hours(now, kv.Value.Last))).ToArray();
        res["baits"] = baits.OrderByDescending(kv => kv.Value.N48).ThenByDescending(kv => kv.Value.N).Take(20).Select(kv => (object)AppEnv.Obj(
            "name", BaitLabel(kv.Key, lang), "n", kv.Value.N, "n48", kv.Value.N48, "lastH", Hours(now, kv.Value.Last),
            "isNew", !kv.Value.Fish.Any(f => Records.SeenLastWeek(f, lake, kv.Key)),
            "firstH", reset == DateTime.MinValue ? -1 : Math.Round(Math.Max(0, kv.Value.First.Subtract(reset).TotalHours), 0),
            "fish", kv.Value.Fish.Select(f => GameData.Translate(f, lang)).OrderBy(f => f).ToArray())).ToArray();
        res["prevBaits"] = prevBaits;
        res["spots"] = spots;
        res["records"] = recs;
        return res;
    }
}
