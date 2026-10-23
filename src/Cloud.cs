using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

internal static class Cloud
{
    private const string Repo = "Nalathan01/rf4-companion-data";
    private static readonly object gate = new object();
    private static volatile bool running;
    public static volatile string Error = "";
    public static DateTime Last = DateTime.MinValue;
    private static DateTime lastTry = DateTime.MinValue;

    private static string StateFile { get { return AppEnv.DataPath("cloud_sync2.json"); } }

    private static Dictionary<string, object> State()
    {
        try { return AppEnv.ReadJsonFile(StateFile) as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        catch (Exception) { return new Dictionary<string, object>(); }
    }

    public static void LoadState()
    {
        DateTime t;
        if (DateTime.TryParse(AppEnv.GetString(State(), "last"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) Last = t.ToUniversalTime();
    }

    public static void Sync(bool force)
    {
        if (running) return;
        if (!force && DateTime.UtcNow.Subtract(Last).TotalMinutes < 30) return;
        if (!force && DateTime.UtcNow.Subtract(lastTry).TotalMinutes < 10) return;
        lock (gate)
        {
            if (running) return;
            running = true;
        }
        lastTry = DateTime.UtcNow;
        try
        {
            Run();
            Error = "";
        }
        catch (Exception ex) { Error = ex.Message; AppEnv.Log("Cloud", ex.Message); }
        finally { running = false; }
    }

    private static void Run()
    {
        IList files = AppEnv.Json.DeserializeObject(Http.GetString("https://api.github.com/repos/" + Repo + "/contents/archive", 30000)) as IList;
        if (files == null) throw new Exception("Archivliste leer");
        Dictionary<string, object> st = State();
        Dictionary<string, object> shas = st.ContainsKey("shas") ? st["shas"] as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
        Dictionary<string, List<Dictionary<string, object>>> byWeek = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.Ordinal);
        HashSet<string> present = new HashSet<string>(StringComparer.Ordinal), changed = new HashSet<string>(StringComparer.Ordinal);
        Regex rx = new Regex(@"^(week|delta)_(\d{4}-\d{2}-\d{2})(_\d{8}T\d{4})?\.json\.gz$");
        foreach (object o in files)
        {
            Dictionary<string, object> f = o as Dictionary<string, object>;
            string n = AppEnv.GetString(f, "name");
            Match m = rx.Match(n);
            if (!m.Success) continue;
            string wid = m.Groups[2].Value;
            present.Add(n);
            if (!byWeek.ContainsKey(wid)) byWeek[wid] = new List<Dictionary<string, object>>();
            byWeek[wid].Add(f);
            if (AppEnv.GetString(shas, n) != AppEnv.GetString(f, "sha")) changed.Add(wid);
        }
        foreach (string k in shas.Keys.ToList()) if (!present.Contains(k)) { Match m = rx.Match(k); if (m.Success) changed.Add(m.Groups[2].Value); shas.Remove(k); }
        string archive = AppEnv.DataPath("archive");
        Directory.CreateDirectory(archive);
        List<string> weeks = byWeek.Keys.OrderByDescending(x => x).Take(5).ToList();
        foreach (string wid in changed.Where(weeks.Contains).OrderBy(x => x))
        {
            Dictionary<string, Dictionary<string, object>> map = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> f in byWeek[wid].OrderBy(x => AppEnv.GetString(x, "name").StartsWith("week_") ? 0 : 1).ThenBy(x => AppEnv.GetString(x, "name")))
            {
                byte[] gz = Http.GetBytes(AppEnv.GetString(f, "download_url"), 60000);
                Merge(map, gz);
                shas[AppEnv.GetString(f, "name")] = AppEnv.GetString(f, "sha");
            }
            WriteWeek(Path.Combine(archive, "week_" + wid + ".json.gz"), wid, map.Values);
            AppEnv.Log("Cloud", "Woche " + wid + ": " + map.Count + " Rekorde");
        }
        Last = DateTime.UtcNow;
        st["shas"] = shas;
        st["last"] = Last.ToString("o", CultureInfo.InvariantCulture);
        AppEnv.WriteJsonFile(StateFile, st);
        if (changed.Count > 0) Records.Refresh();
    }

    private static string Sv(IDictionary d, string k) { return AppEnv.GetString(d, k); }

    private static void Merge(Dictionary<string, Dictionary<string, object>> map, byte[] gz)
    {
        string t;
        using (MemoryStream ms = new MemoryStream(gz))
        using (GZipStream z = new GZipStream(ms, CompressionMode.Decompress))
        using (StreamReader sr = new StreamReader(z, Encoding.UTF8)) t = sr.ReadToEnd();
        Dictionary<string, object> d = AppEnv.Json.DeserializeObject(t) as Dictionary<string, object>;
        foreach (object o in AppEnv.GetList(d, "items"))
        {
            IDictionary it = o as IDictionary;
            if (it == null) continue;
            string k = Sv(it, "k");
            Dictionary<string, object> m;
            if (map.TryGetValue(k, out m))
            {
                string fs = Sv(it, "fs"), ls = Sv(it, "ls");
                if (string.CompareOrdinal(fs, Sv(m, "fs")) < 0) m["fs"] = fs;
                if (string.CompareOrdinal(ls, Sv(m, "ls")) > 0) m["ls"] = ls;
                continue;
            }
            string lake = Sv(it, "l");
            if (lake.Length == 0) lake = GameData.LakeIdByName(Sv(it, "loc"));
            map[k] = AppEnv.Obj("k", k, "t", Sv(it, "t"), "r", Sv(it, "r"), "f", Sv(it, "f"), "w", (int)AppEnv.GetDouble(it, "w", 0), "l", lake,
                "b", Sv(it, "b"), "d", Sv(it, "d"), "fs", Sv(it, "fs"), "ls", Sv(it, "ls"));
        }
    }

    private static void WriteWeek(string path, string wid, IEnumerable<Dictionary<string, object>> items)
    {
        string json = AppEnv.Json.Serialize(AppEnv.Obj("week", wid, "v", 1, "items", items.ToList()));
        string tmp = path + ".tmp";
        using (FileStream fs = File.Create(tmp))
        using (GZipStream z = new GZipStream(fs, CompressionMode.Compress))
        {
            byte[] b = Encoding.UTF8.GetBytes(json);
            z.Write(b, 0, b.Length);
        }
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }
}
