using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal static class Rf4Fish
{
    public const string Site = "https://rf4.fish";
    private const string MirrorInfo = "https://raw.githubusercontent.com/Nalathan01/rf4-companion-data/mirror/rf4fish_info.json.gz";

    public static void SyncSpots(bool force)
    {
    }

    public static string[] SetupBaits(Report r)
    {
        return new string[0];
    }

    public static void SaveSetupCache()
    {
    }

    public static void ExportInfo(string path)
    {
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
        string json;
        using (MemoryStream ms = new MemoryStream(Http.GetBytes(MirrorInfo + "?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture), 60000)))
        using (GZipStream z = new GZipStream(ms, CompressionMode.Decompress))
        using (StreamReader sr = new StreamReader(z, Encoding.UTF8)) json = sr.ReadToEnd();
        Dictionary<string, object> d = AppEnv.Json.DeserializeObject(json) as Dictionary<string, object>;
        if (d == null) throw new InvalidOperationException("Spiegel ohne Nachschlagedaten");
        AppEnv.WriteJsonFile(InfoPath, d);
        lock (infoGate) info = d;
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
