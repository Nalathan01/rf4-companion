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
        return 0;
    }

    public static int MergeDiscord(string feedDir, string inPath, string outPath)
    {
        return -1;
    }
}
