using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

internal static class BaitHistory
{
    private const string Repo = "Nalathan01/rf4-companion-data";
    private static readonly Regex FileRx = new Regex(@"^baits_(\d{4}-\d{2})\.json\.gz$");
    private static readonly Regex TopRx = new Regex(@"^top_(\d{4})\.json\.gz$");

    private sealed class Row
    {
        public string Month, Fish, Lake, Bait;
        public int N, Max;
    }

    private sealed class Top
    {
        public string Month, Fish, Lake, Bait, Date, Region, Table;
        public int W;
    }

    private sealed class Agg
    {
        public int N, Max;
        public string Last = "";
        public HashSet<string> Fish = new HashSet<string>(StringComparer.Ordinal);
    }

    private static readonly object gate = new object();
    private static List<Row> rows;
    private static List<Top> tops = new List<Top>();
    private static volatile bool running;
    private static volatile string error = "";
    private static DateTime lastSync = DateTime.MinValue;

    private static string Dir { get { return AppEnv.DataPath("history"); } }

    private static void EnsureLoaded()
    {
        if (running) return;
        if (rows != null && DateTime.UtcNow.Subtract(lastSync).TotalHours < 6) return;
        running = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                if (rows == null) Load();
                Sync();
                Load();
                error = "";
            }
            catch (Exception ex) { error = ex.Message; AppEnv.Log("Historie", ex.Message); if (rows == null) Load(); }
            finally { lastSync = DateTime.UtcNow; running = false; }
        });
    }

    private static void Sync()
    {
        IList files = AppEnv.Json.DeserializeObject(Http.GetString("https://api.github.com/repos/" + Repo + "/contents/history", 30000)) as IList;
        if (files == null) throw new Exception("Historie nicht gefunden");
        Directory.CreateDirectory(Dir);
        string stateFile = Path.Combine(Dir, "state.json");
        Dictionary<string, object> shas;
        try { shas = AppEnv.ReadJsonFile(stateFile) as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        catch (Exception) { shas = new Dictionary<string, object>(); }
        foreach (object o in files)
        {
            Dictionary<string, object> f = o as Dictionary<string, object>;
            string n = AppEnv.GetString(f, "name");
            if (!FileRx.IsMatch(n) && !TopRx.IsMatch(n)) continue;
            string path = Path.Combine(Dir, n);
            if (File.Exists(path) && AppEnv.GetString(shas, n) == AppEnv.GetString(f, "sha")) continue;
            byte[] gz = Http.GetBytes(AppEnv.GetString(f, "download_url"), 60000);
            File.WriteAllBytes(path + ".tmp", gz);
            if (File.Exists(path)) File.Delete(path);
            File.Move(path + ".tmp", path);
            shas[n] = AppEnv.GetString(f, "sha");
        }
        AppEnv.WriteJsonFile(stateFile, shas);
    }

    private static void Load()
    {
        List<Row> list = new List<Row>();
        if (Directory.Exists(Dir))
        {
            foreach (string path in Directory.GetFiles(Dir, "baits_*.json.gz").OrderBy(x => x))
            {
                Match m = FileRx.Match(Path.GetFileName(path));
                if (!m.Success) continue;
                Dictionary<string, object> d;
                try { d = AppEnv.ReadGzJsonFile(path) as Dictionary<string, object>; }
                catch (Exception ex) { AppEnv.Log("Historie", Path.GetFileName(path) + ": " + ex.Message); continue; }
                foreach (object o in AppEnv.GetList(d, "items"))
                {
                    IList r = o as IList;
                    if (r == null || r.Count < 5) continue;
                    string loc = Convert.ToString(r[1], CultureInfo.InvariantCulture);
                    string lake = GameData.LakeIdByName(loc);
                    list.Add(new Row
                    {
                        Month = m.Groups[1].Value,
                        Fish = Convert.ToString(r[0], CultureInfo.InvariantCulture),
                        Lake = lake.Length > 0 ? lake : loc,
                        Bait = Finder.BaitKey(Convert.ToString(r[2], CultureInfo.InvariantCulture)),
                        N = Convert.ToInt32(r[3], CultureInfo.InvariantCulture),
                        Max = Convert.ToInt32(r[4], CultureInfo.InvariantCulture)
                    });
                }
            }
        }
        List<Top> top = new List<Top>();
        if (Directory.Exists(Dir))
        {
            foreach (string path in Directory.GetFiles(Dir, "top_*.json.gz"))
            {
                if (!TopRx.IsMatch(Path.GetFileName(path))) continue;
                Dictionary<string, object> d;
                try { d = AppEnv.ReadGzJsonFile(path) as Dictionary<string, object>; }
                catch (Exception ex) { AppEnv.Log("Historie", Path.GetFileName(path) + ": " + ex.Message); continue; }
                foreach (object o in AppEnv.GetList(d, "items"))
                {
                    IList r = o as IList;
                    if (r == null || r.Count < 7) continue;
                    string date = Convert.ToString(r[4], CultureInfo.InvariantCulture);
                    string loc = Convert.ToString(r[1], CultureInfo.InvariantCulture);
                    string lake = GameData.LakeIdByName(loc);
                    Match dm = Regex.Match(date, @"^(\d{1,2})\.(\d{1,2})\.(\d{2,4})$");
                    if (!dm.Success) continue;
                    string y = dm.Groups[3].Value;
                    top.Add(new Top
                    {
                        Month = (y.Length == 2 ? "20" + y : y) + "-" + dm.Groups[2].Value.PadLeft(2, '0'),
                        Fish = Convert.ToString(r[0], CultureInfo.InvariantCulture),
                        Lake = lake.Length > 0 ? lake : loc,
                        Bait = Finder.BaitKey(Convert.ToString(r[2], CultureInfo.InvariantCulture)),
                        W = Convert.ToInt32(r[3], CultureInfo.InvariantCulture),
                        Date = date,
                        Region = Convert.ToString(r[5], CultureInfo.InvariantCulture),
                        Table = Convert.ToString(r[6], CultureInfo.InvariantCulture)
                    });
                }
            }
        }
        lock (gate) { rows = list; tops = top; }
    }

    private static string LakeName(string id, string lang)
    {
        Lake l;
        return GameData.LakeById.TryGetValue(id, out l) ? l.Name(lang) : id;
    }

    public static object Query(NameValueCollection q, string lang)
    {
        EnsureLoaded();
        List<Row> all;
        List<Top> allTop;
        lock (gate) { all = rows; allTop = tops; }
        if (all == null || (running && all.Count == 0)) return AppEnv.Obj("loading", true, "error", error);
        string fish = q["fish"] ?? "";
        string lakeSel = q["lake"] ?? "";
        int months;
        if (!int.TryParse(q["months"] ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out months)) months = 0;
        string from = months > 0 ? DateTime.UtcNow.AddMonths(1-months).ToString("yyyy-MM", CultureInfo.InvariantCulture) : "";
        List<Row> cur = all.Where(r => string.CompareOrdinal(r.Month, from) >= 0).ToList();

        Dictionary<string, int> fishCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Row r in cur) { int n; fishCount.TryGetValue(r.Fish, out n); fishCount[r.Fish] = n + r.N; }
        List<object> fishList = new List<object>();
        int koiN = fishCount.Where(kv => Finder.KoiNames.Contains(kv.Key)).Sum(kv => kv.Value);
        if (koiN > 0) fishList.Add(AppEnv.Obj("key", Finder.KoiGroup, "name", GameData.Ui(lang, "tgtKoiGroup", "Alle Koi"), "n", koiN));
        foreach (KeyValuePair<string, int> kv in fishCount.OrderByDescending(kv => kv.Value)) fishList.Add(AppEnv.Obj("key", kv.Key, "name", GameData.Translate(kv.Key, lang), "n", kv.Value));

        List<string> monthList = all.Select(r => r.Month).Distinct().OrderBy(x => x).ToList();
        Dictionary<string, object> res = AppEnv.Obj("loading", false, "error", error, "fishList", fishList, "total", cur.Sum(r => r.N),
            "first", monthList.FirstOrDefault() ?? "", "last", monthList.LastOrDefault() ?? "");
        if (fish.Length == 0) return res;

        HashSet<string> set = fish == Finder.KoiGroup ? new HashSet<string>(Finder.KoiNames) : new HashSet<string> { fish };
        List<Row> fr = cur.Where(r => set.Contains(r.Fish)).ToList();
        Dictionary<string, int> lakes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Row r in fr) { int n; lakes.TryGetValue(r.Lake, out n); lakes[r.Lake] = n + r.N; }
        string lake = lakeSel.Length > 0 && lakes.ContainsKey(lakeSel) ? lakeSel : "";

        Dictionary<string, Agg> baits = new Dictionary<string, Agg>(StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, int>> byMonth = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (Row r in fr)
        {
            if (lake.Length > 0 && r.Lake != lake) continue;
            Agg a;
            if (!baits.TryGetValue(r.Bait, out a)) { a = new Agg(); baits[r.Bait] = a; }
            a.N += r.N;
            a.Max = Math.Max(a.Max, r.Max);
            a.Fish.Add(r.Fish);
            if (string.CompareOrdinal(r.Month, a.Last) > 0) a.Last = r.Month;
            Dictionary<string, int> bm;
            if (!byMonth.TryGetValue(r.Month, out bm)) { bm = new Dictionary<string, int>(StringComparer.Ordinal); byMonth[r.Month] = bm; }
            int c; bm.TryGetValue(r.Bait, out c); bm[r.Bait] = c + r.N;
        }
        int total = baits.Values.Sum(a => a.N);

        res["fish"] = fish;
        res["fishName"] = fish == Finder.KoiGroup ? GameData.Ui(lang, "tgtKoiGroup", "Alle Koi") : GameData.Translate(fish, lang);
        res["lake"] = lake;
        res["lakeName"] = lake.Length > 0 ? LakeName(lake, lang) : GameData.Ui(lang, "allLakes", "Alle Gewässer");
        res["n"] = total;
        res["lakes"] = lakes.OrderByDescending(kv => kv.Value).Select(kv => (object)AppEnv.Obj("id", kv.Key, "name", LakeName(kv.Key, lang), "n", kv.Value)).ToArray();
        res["baits"] = baits.OrderByDescending(kv => kv.Value.N).Take(40).Select(kv => (object)AppEnv.Obj(
            "name", Finder.BaitLabel(kv.Key, lang), "n", kv.Value.N, "share", total > 0 ? Math.Round(100.0 * kv.Value.N / total, 1) : 0,
            "max", GameData.FormatWeight(kv.Value.Max, lang), "last", kv.Value.Last,
            "fish", kv.Value.Fish.Select(f => GameData.Translate(f, lang)).OrderBy(f => f).ToArray())).ToArray();
        res["months"] = byMonth.OrderByDescending(kv => kv.Key).Select(kv => (object)AppEnv.Obj(
            "month", kv.Key, "n", kv.Value.Values.Sum(),
            "top", kv.Value.OrderByDescending(x => x.Value).Take(3).Select(x => (object)AppEnv.Obj("name", Finder.BaitLabel(x.Key, lang), "n", x.Value)).ToArray())).ToArray();
        res["top"] = allTop.Where(t => set.Contains(t.Fish) && (lake.Length == 0 || t.Lake == lake) && string.CompareOrdinal(t.Month, from) >= 0)
            .OrderByDescending(t => t.W).Take(15).Select(t => (object)AppEnv.Obj(
            "fish", GameData.Translate(t.Fish, lang), "w", GameData.FormatWeight(t.W, lang), "bait", Finder.BaitLabel(t.Bait, lang), "lake", LakeName(t.Lake, lang),
            "region", t.Region, "date", t.Date, "table", t.Table == "n" ? GameData.Ui(lang, "records", "Rekorde") : GameData.Ui(lang, "table_" + t.Table, t.Table))).ToArray();
        return res;
    }
}
