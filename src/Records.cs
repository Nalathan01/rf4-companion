using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

internal sealed class RecordHit
{
    public string Fish, Lake, Bait, Table, Region, Date, Week;
    public int Grams;
    public DateTime Day, Seen;
}

internal static class Records
{
    private static readonly object gate = new object();
    private static Dictionary<string, List<RecordHit>> index = new Dictionary<string, List<RecordHit>>(StringComparer.Ordinal);
    private static string signature = "";
    private static List<RecordHit> all = new List<RecordHit>();
    private static HashSet<string> prev = new HashSet<string>(StringComparer.Ordinal);

    public static bool SeenLastWeek(string fish, string lake, string baitKey)
    {
        lock (gate) return prev.Contains(fish + "|" + lake + "|" + baitKey);
    }
    private static volatile bool loading;
    public static int Version;
    public static volatile bool Loaded;
    private static readonly string[] Fmts = { "d.M.yy", "d.M.yyyy", "dd.MM.yy", "dd.MM.yyyy", "M/d/yy", "M/d/yyyy", "yyyy-MM-dd", "d/M/yy" };
    private static readonly Regex TextWeight = new Regex(@"(?i)(\d{1,3}[.,]\d{3})\s*(кг|kg)");

    public static string ArchiveDir
    {
        get { return AppEnv.DataPath("archive"); }
    }

    private static string Signature(List<FileInfo> files)
    {
        List<string> parts = new List<string>();
        foreach (FileInfo f in files) parts.Add(f.Name + ":" + f.Length.ToString(CultureInfo.InvariantCulture) + ":" + f.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        return string.Join("|", parts.ToArray());
    }

    private static List<FileInfo> Files()
    {
        List<FileInfo> l = new List<FileInfo>();
        if (!Directory.Exists(ArchiveDir)) return l;
        foreach (string p in Directory.GetFiles(ArchiveDir, "week_*.json.gz")) l.Add(new FileInfo(p));
        l.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name));
        if (l.Count > 4) l.RemoveRange(4, l.Count-4);
        return l;
    }

    public static void Refresh()
    {
        if (loading) return;
        List<FileInfo> files = Files();
        string sig = Signature(files);
        if (sig == signature) { Loaded = true; return; }
        loading = true;
        try
        {
            if (signature.Length == 0)
            {
                string cached = LoadCache();
                if (cached != null)
                {
                    if (cached != sig) ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(3000); try { Refresh(); } catch (Exception ex) { AppEnv.Log("Rekorde", ex.Message); } });
                    return;
                }
            }
            Dictionary<string, List<RecordHit>> idx = new Dictionary<string, List<RecordHit>>(StringComparer.Ordinal);
            List<object> rows = new List<object>();
            DateTime cutoff = DateTime.Today.AddDays(-CacheDays);
            Dictionary<string, object>[] docs = new Dictionary<string, object>[files.Count];
            System.Threading.Tasks.Parallel.For(0, files.Count, i =>
            {
                try { docs[i] = AppEnv.ReadGzJsonFile(files[i].FullName) as Dictionary<string, object>; }
                catch (Exception ex) { AppEnv.Log("Rekorde", files[i].Name + ": " + ex.Message); }
            });
            List<RecordHit> flat = new List<RecordHit>();
            HashSet<string> pv = new HashSet<string>(StringComparer.Ordinal);
            string prevWeek = files.Count > 1 ? Regex.Match(files[1].Name, @"\d{4}-\d{2}-\d{2}").Value : "";
            foreach (Dictionary<string, object> d in docs)
            {
                if (d == null) continue;
                string week = AppEnv.GetString(d, "week");
                foreach (object o in AppEnv.GetList(d, "items"))
                {
                    IDictionary it = o as IDictionary;
                    if (it == null) continue;
                    RecordHit h = new RecordHit();
                    h.Fish = AppEnv.GetString(it, "f");
                    h.Lake = AppEnv.GetString(it, "l");
                    if (h.Lake.Length == 0) h.Lake = GameData.LakeIdByName(AppEnv.GetString(it, "loc"));
                    h.Grams = (int)AppEnv.GetDouble(it, "w", 0);
                    h.Bait = AppEnv.GetString(it, "b");
                    h.Table = AppEnv.GetString(it, "t");
                    h.Region = AppEnv.GetString(it, "r");
                    h.Date = AppEnv.GetString(it, "d");
                    h.Week = week;
                    DateTime seen;
                    if (DateTime.TryParse(AppEnv.GetString(it, "fs"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out seen)) h.Seen = seen;
                    DateTime day;
                    if (!DateTime.TryParseExact(h.Date.Trim(), Fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) continue;
                    h.Day = day;
                    if (week == prevWeek && h.Fish.Length > 0 && h.Bait.Length > 0) pv.Add(h.Fish + "|" + (h.Lake.Length > 0 ? h.Lake : "") + "|" + Finder.BaitKey(h.Bait));
                    if (day < cutoff || h.Fish.Length == 0 || h.Lake.Length == 0 || h.Grams <= 0 || h.Bait.Length == 0) continue;
                    Add(idx, h);
                    flat.Add(h);
                    rows.Add(new object[] { h.Fish, h.Lake, h.Grams, h.Bait, h.Table, h.Region, h.Date, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), h.Week, h.Seen == DateTime.MinValue ? "" : h.Seen.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) });
                }
            }
            lock (gate) { index = idx; all = flat; prev = pv; signature = sig; Version++; }
            try { AppEnv.WriteJsonFile(CachePath, AppEnv.Obj("v", 3, "sig", sig, "items", rows, "prev", pv.ToArray())); } catch (Exception ex) { AppEnv.Log("Rekorde", "Zwischenspeicher: " + ex.Message); }
            AppEnv.Log("Rekorde", "Geladen: " + files.Count + " Wochen, " + idx.Count + " Einträge");
        }
        finally { loading = false; Loaded = true; }
    }

    private const int CacheDays = 10;

    private static string CachePath
    {
        get { return AppEnv.DataPath("records_recent.json"); }
    }

    private static void Add(Dictionary<string, List<RecordHit>> idx, RecordHit h)
    {
        string k = Key(h.Fish, h.Lake, h.Grams);
        List<RecordHit> l;
        if (!idx.TryGetValue(k, out l)) { l = new List<RecordHit>(); idx[k] = l; }
        l.Add(h);
    }

    private static string LoadCache()
    {
        if (!File.Exists(CachePath)) return null;
        Dictionary<string, object> d;
        try { d = AppEnv.ReadJsonFile(CachePath) as Dictionary<string, object>; }
        catch (Exception) { return null; }
        string sig = AppEnv.GetString(d, "sig");
        if (d == null || sig.Length == 0 || AppEnv.GetDouble(d, "v", 0) != 3) return null;
        HashSet<string> pv = new HashSet<string>(StringComparer.Ordinal);
        foreach (object o in AppEnv.GetList(d, "prev")) if (o != null) pv.Add(o.ToString());
        Dictionary<string, List<RecordHit>> idx = new Dictionary<string, List<RecordHit>>(StringComparer.Ordinal);
        List<RecordHit> flat = new List<RecordHit>();
        foreach (object o in AppEnv.GetList(d, "items"))
        {
            IList a = o as IList;
            if (a == null || a.Count < 10) continue;
            RecordHit h = new RecordHit();
            h.Fish = a[0] + ""; h.Lake = a[1] + ""; h.Grams = Convert.ToInt32(a[2], CultureInfo.InvariantCulture);
            h.Bait = a[3] + ""; h.Table = a[4] + ""; h.Region = a[5] + ""; h.Date = a[6] + "";
            DateTime day;
            if (!DateTime.TryParseExact(a[7] + "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) continue;
            h.Day = day;
            h.Week = a[8] + "";
            DateTime seen;
            if (DateTime.TryParse(a[9] + "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out seen)) h.Seen = seen;
            Add(idx, h);
            flat.Add(h);
        }
        lock (gate) { index = idx; all = flat; prev = pv; signature = sig; Version++; }
        AppEnv.Log("Rekorde", "Aus Zwischenspeicher: " + idx.Count + " Einträge");
        return sig;
    }

    public static List<RecordHit> All()
    {
        lock (gate) return all;
    }

    public static string CurrentWeek()
    {
        string w = "";
        foreach (RecordHit h in All()) if (string.CompareOrdinal(h.Week, w) > 0) w = h.Week;
        return w;
    }

    public static DateTime ResetUtc(string week)
    {
        DateTime wd;
        if (!DateTime.TryParseExact(week, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out wd)) return DateTime.MinValue;
        DateTime bound = wd.AddHours(18.5), min = DateTime.MaxValue;
        foreach (RecordHit h in All()) if (h.Week == week && h.Seen >= bound && h.Seen < min) min = h.Seen;
        return min == DateTime.MaxValue ? DateTime.MinValue : min;
    }

    public static bool InResetWindow(DateTime utc)
    {
        if (utc.DayOfWeek == DayOfWeek.Sunday && utc.TimeOfDay >= TimeSpan.FromHours(18.5)) return true;
        return utc.DayOfWeek == DayOfWeek.Monday && utc.TimeOfDay <= TimeSpan.FromMinutes(75);
    }

    private static string Key(string fish, string lake, int grams)
    {
        return fish + "|" + lake + "|" + grams.ToString(CultureInfo.InvariantCulture);
    }

    public static int ReportGrams(Report r)
    {
        string w = r.S("weight");
        double kg;
        if (w.Length > 0 && double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out kg))
        {
            string frac = w.Contains(".") ? w.Substring(w.IndexOf('.') + 1) : "";
            if (frac.Length == 3) return (int)Math.Round(kg * 1000);
        }
        string pk = Scans.PostKey(r.Url);
        if (pk.Length == 0) return 0;
        Match m = TextWeight.Match(Scans.Text(pk));
        if (!m.Success) return 0;
        double v;
        if (!double.TryParse(m.Groups[1].Value.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return 0;
        return (int)Math.Round(v * 1000);
    }

    public static RecordHit Match(Report r)
    {
        int g = ReportGrams(r);
        if (g <= 0 || r.Posted == DateTime.MinValue) return null;
        DateTime day = r.Posted.ToLocalTime().Date;
        lock (gate)
        {
            foreach (string f in r.Fish)
            {
                List<RecordHit> l;
                if (!index.TryGetValue(Key(f, r.Lake, g), out l)) continue;
                foreach (RecordHit h in l) if (Math.Abs(day.Subtract(h.Day).TotalDays) <= 1) return h;
            }
        }
        return null;
    }
}
