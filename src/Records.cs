using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

internal sealed class RecordHit
{
    public string Fish, Lake, Bait, Table, Region, Date;
    public int Grams;
    public DateTime Day;
}

internal static class Records
{
    private static readonly object gate = new object();
    private static Dictionary<string, List<RecordHit>> index = new Dictionary<string, List<RecordHit>>(StringComparer.Ordinal);
    private static string signature = "";
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
            foreach (Dictionary<string, object> d in docs)
            {
                if (d == null) continue;
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
                    DateTime day;
                    if (!DateTime.TryParseExact(h.Date.Trim(), Fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) continue;
                    h.Day = day;
                    if (day < cutoff || h.Fish.Length == 0 || h.Lake.Length == 0 || h.Grams <= 0 || h.Bait.Length == 0) continue;
                    Add(idx, h);
                    rows.Add(new object[] { h.Fish, h.Lake, h.Grams, h.Bait, h.Table, h.Region, h.Date, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
                }
            }
            lock (gate) { index = idx; signature = sig; Version++; }
            try { AppEnv.WriteJsonFile(CachePath, AppEnv.Obj("sig", sig, "items", rows)); } catch (Exception ex) { AppEnv.Log("Rekorde", "Zwischenspeicher: " + ex.Message); }
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
        if (d == null || sig.Length == 0) return null;
        Dictionary<string, List<RecordHit>> idx = new Dictionary<string, List<RecordHit>>(StringComparer.Ordinal);
        foreach (object o in AppEnv.GetList(d, "items"))
        {
            IList a = o as IList;
            if (a == null || a.Count < 8) continue;
            RecordHit h = new RecordHit();
            h.Fish = a[0] + ""; h.Lake = a[1] + ""; h.Grams = Convert.ToInt32(a[2], CultureInfo.InvariantCulture);
            h.Bait = a[3] + ""; h.Table = a[4] + ""; h.Region = a[5] + ""; h.Date = a[6] + "";
            DateTime day;
            if (!DateTime.TryParseExact(a[7] + "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) continue;
            h.Day = day;
            Add(idx, h);
        }
        lock (gate) { index = idx; signature = sig; Version++; }
        AppEnv.Log("Rekorde", "Aus Zwischenspeicher: " + idx.Count + " Einträge");
        return sig;
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
