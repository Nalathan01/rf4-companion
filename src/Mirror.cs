using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

internal static class Mirror
{
    private const string Url = "https://raw.githubusercontent.com/Nalathan01/rf4-companion-data/mirror/spots.json.gz";
    private static DateTime lastFetch = DateTime.MinValue;
    public static DateTime Generated = DateTime.MinValue;

    private static string Source
    {
        get { string s = Environment.GetEnvironmentVariable("RF4_MIRROR"); return string.IsNullOrEmpty(s) ? Url : s; }
    }

    public static bool Fresh
    {
        get { return Generated > DateTime.MinValue && DateTime.UtcNow.Subtract(Generated).TotalHours < 12; }
    }

    public static bool Fetch(bool force)
    {
        if (!force && DateTime.UtcNow.Subtract(lastFetch).TotalMinutes < 15) return Fresh;
        lastFetch = DateTime.UtcNow;
        try
        {
            string src = Source;
            byte[] gz = src.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? Http.GetBytes(src + "?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture), 30000) : File.ReadAllBytes(src);
            string json;
            using (MemoryStream ms = new MemoryStream(gz))
            using (GZipStream z = new GZipStream(ms, CompressionMode.Decompress))
            using (StreamReader sr = new StreamReader(z, Encoding.UTF8)) json = sr.ReadToEnd();
            Dictionary<string, object> d = AppEnv.Json.DeserializeObject(json) as Dictionary<string, object>;
            DateTime gen;
            if (d == null || !DateTime.TryParse(AppEnv.GetString(d, "generated"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out gen)) return false;
            if (DateTime.UtcNow.Subtract(gen).TotalHours >= 12) { AppEnv.Log("Spiegel", "veraltet: " + gen.ToString("o", CultureInfo.InvariantCulture)); return false; }
            if (gen <= Generated) return true;
            List<Dictionary<string, object>> raws = AppEnv.GetList(d, "reports").Cast<object>().Select(o => o as Dictionary<string, object>).Where(o => o != null).ToList();
            int n = Community.ApplyMirror(raws, gen);
            Generated = gen;
            AppEnv.Log("Spiegel", raws.Count + " Meldungen, neu oder geändert: " + n);
            return true;
        }
        catch (Exception ex)
        {
            AppEnv.Log("Spiegel", ex.Message);
            return false;
        }
    }

    public static int Export(string path)
    {
        GameData.Load();
        UserStore.Load();
        Scans.Load();
        Community.Load();
        Cloud.LoadState();
        Cloud.Sync(true);
        Records.Refresh();
        Community.Sync(false);
        TgDirect.Sync(true);
        Rf4Fish.SyncSpots(true);
        Community.QueuePosts(192);
        Wait(() => Scans.Pending, 15);
        Scans.Save();
        if (ImageOcr.AnyAvailable())
        {
            Community.QueueRecentOcr(192);
            Wait(() => ImageOcr.Pending, 20);
        }
        DateTime since = DateTime.UtcNow.AddDays(-8);
        List<object> outL = new List<object>();
        Dictionary<string, object> wxFeed = null;
        try
        {
            string fd = Environment.GetEnvironmentVariable("RF4_FISH_DIR");
            if (!string.IsNullOrEmpty(fd) && File.Exists(Path.Combine(fd, "wx_feed.json"))) wxFeed = AppEnv.ReadJsonFile(Path.Combine(fd, "wx_feed.json")) as Dictionary<string, object>;
        }
        catch (Exception ex) { AppEnv.Log("Spiegel", "Futter aus Bild: " + ex.Message); }
        foreach (Report r in Community.All().Where(x => x.Posted >= since && Community.Plausible(x)))
        {
            Dictionary<string, object> raw = new Dictionary<string, object>(r.Raw);
            raw.Remove("weight");
            raw["fish"] = r.Fish.ToArray();
            string pk = Scans.PostKey(r.Url);
            string text = pk.Length > 0 ? Scans.Text(pk) : "";
            Dictionary<string, string> info = PostText.Parse(text);
            bool hadBait = r.S("baitDetail").Length > 0 || r.S("lure").Length > 0 || info.ContainsKey("baitDetail");
            string[] baits = Api.ReportBaits(r);
            if (baits.Length > 0) raw["baitDetail"] = string.Join(" + ", baits);
            foreach (string k in new string[] { "dip", "groundbait", "pva", "reelSpeed", "clip", "depth" })
            {
                string v;
                if (AppEnv.GetString(raw, k).Length == 0 && info.TryGetValue(k, out v)) raw[k] = v;
            }
            Dictionary<string, object> oc = pk.Length > 0 ? ImageOcr.Result(pk) : null;
            if (oc != null && AppEnv.GetString(raw, "dip").Length == 0)
            {
                string[] dips = AppEnv.GetList(oc, "baits").Cast<object>().Select(o => o.ToString()).Where(GameData.DipKeys.Contains).ToArray();
                if (dips.Length > 0) raw["dip"] = string.Join(" + ", dips);
            }
            string t;
            if (info.TryGetValue("temp", out t)) raw["ptemp"] = t;
            double deg = pk.Length > 0 ? Scans.Degrees(pk) : -1;
            if (deg >= 0) raw["mdeg"] = deg;
            bool fromImg = !hadBait && oc != null && AppEnv.GetList(oc, "baits").Cast<object>().Any(o => !GameData.DipKeys.Contains(o.ToString()));
            raw["mimg"] = fromImg;
            RecordHit rec = !hadBait && !fromImg ? Records.Match(r) : null;
            if (rec != null) raw["mrec"] = AppEnv.Obj("t", rec.Table, "r", rec.Region, "d", rec.Date, "b", rec.Bait);
            raw["mixed"] = PostText.Mixed(text);
            raw["ms"] = Community.HasSetup(r);
            if (r.Id.StartsWith("wx:", StringComparison.Ordinal) && baits.Length == 0 && ImageOcr.ZhLang().Length > 0)
            {
                string[] wb = Rf4Fish.SetupBaits(r);
                if (wb.Length > 0) { raw["baitDetail"] = string.Join(" + ", wb); raw["mimg"] = true; raw["ms"] = true; }
            }
            object wf;
            if (wxFeed != null && r.Id.StartsWith("wx:", StringComparison.Ordinal) && AppEnv.GetString(raw, "groundbait").Length == 0 && wxFeed.TryGetValue(r.Id.Substring(3), out wf))
            {
                string[] parts = ((System.Collections.IList)wf).Cast<object>().Select(o => o.ToString()).ToArray();
                if (parts.Length > 0) raw["groundbait"] = string.Join(" + ", parts);
            }
            outL.Add(raw);
        }
        Rf4Fish.SaveSetupCache();
        string json = AppEnv.Json.Serialize(AppEnv.Obj("v", 1, "generated", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), "reports", outL));
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        using (FileStream fs = File.Create(path))
        using (GZipStream z = new GZipStream(fs, CompressionLevel.Optimal))
        {
            byte[] b = Encoding.UTF8.GetBytes(json);
            z.Write(b, 0, b.Length);
        }
        Community.SaveNow();
        try { Rf4Fish.ExportInfo(Path.Combine(dir, "rf4fish_info.json.gz")); }
        catch (Exception ex) { AppEnv.Log("Spiegel", "Nachschlagedaten: " + ex.Message); }
        AppEnv.Log("Spiegel", "Export: " + outL.Count + " Meldungen");
        return outL.Count;
    }

    private static void Wait(Func<int> pending, int minutes)
    {
        DateTime until = DateTime.UtcNow.AddMinutes(minutes);
        while (pending() > 0 && DateTime.UtcNow < until) Thread.Sleep(500);
    }
}
