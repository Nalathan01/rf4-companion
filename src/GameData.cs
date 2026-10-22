using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

internal sealed class Lake
{
    public string Id;
    public int Order;
    public int Rank;
    public Dictionary<string, object> Names;
    public string MapKey;
    public double XMin, XMax, YSouth, YNorth;
    public bool HasBounds;
    public List<string> Fish = new List<string>();

    public string Name(string lang)
    {
        object v;
        if (Names != null && Names.TryGetValue(lang, out v) && v != null && v.ToString().Length > 0) return v.ToString();
        if (Names != null && Names.TryGetValue("en", out v) && v != null) return v.ToString();
        return Id;
    }
}

internal static class GameData
{
    public static readonly List<Lake> Lakes = new List<Lake>();
    public static readonly Dictionary<string, Lake> LakeById = new Dictionary<string, Lake>(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, string> LakeByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, Dictionary<string, object>> Names = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
    public static readonly Dictionary<string, int[]> Trophies = new Dictionary<string, int[]>(StringComparer.Ordinal);
    public static readonly List<string> Baits = new List<string>();
    public static readonly List<string> DipKeys = new List<string>();
    private static List<string[]> itemList;
    public static Dictionary<string, object> Lang;
    private static Dictionary<string, string[]> itemRows;
    private static string[] itemLangs;
    private static readonly object itemLock = new object();

    public static readonly string[] LevelOrder = { "mosquito_lake", "elk_lake", "winding_rivulet", "old_burg_lake", "belaya_river", "kuori_lake", "bear_lake", "volkhov_river", "seversky_donets_river", "sura_river", "ladoga_lake", "the_amber_lake", "ladoga_archipelago", "akhtuba_river", "copper_lake", "lower_tunguska_river", "yama_river", "norwegian_sea" };

    public static void Load()
    {
        Dictionary<string, object> g = AppEnv.Json.DeserializeObject(AppEnv.ResourceText("data/gamedata.json")) as Dictionary<string, object>;
        foreach (object o in AppEnv.GetList(g, "lakes"))
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) continue;
            Lake l = new Lake();
            l.Id = AppEnv.GetString(d, "id");
            l.Order = (int)AppEnv.GetDouble(d, "order", 0);
            l.Names = d.ContainsKey("name") ? d["name"] as Dictionary<string, object> : null;
            l.MapKey = AppEnv.GetString(d, "mapKey");
            Dictionary<string, object> b = d.ContainsKey("bounds") ? d["bounds"] as Dictionary<string, object> : null;
            if (b != null)
            {
                l.XMin = AppEnv.GetDouble(b, "xMin", 0); l.XMax = AppEnv.GetDouble(b, "xMax", 0);
                l.YSouth = AppEnv.GetDouble(b, "ySouth", 0); l.YNorth = AppEnv.GetDouble(b, "yNorth", 0);
                l.HasBounds = l.XMax > l.XMin && l.YNorth > l.YSouth;
            }
            foreach (object f in AppEnv.GetList(d, "fish")) if (f != null) l.Fish.Add(f.ToString());
            int idx = Array.IndexOf(LevelOrder, l.Id);
            l.Rank = idx >= 0 ? idx : 100 + l.Order;
            Lakes.Add(l);
            LakeById[l.Id] = l;
            if (l.Names != null) foreach (KeyValuePair<string, object> kv in l.Names) if (kv.Value != null) LakeByName[kv.Value.ToString().Trim()] = l.Id;
        }
        Lakes.Sort((a, b) => a.Rank.CompareTo(b.Rank));
        Dictionary<string, object> names = g.ContainsKey("names") ? g["names"] as Dictionary<string, object> : null;
        if (names != null) foreach (KeyValuePair<string, object> kv in names) { Dictionary<string, object> v = kv.Value as Dictionary<string, object>; if (v != null) Names[kv.Key] = v; }
        Dictionary<string, object> dips = AppEnv.Json.DeserializeObject(AppEnv.ResourceText("data/dips.json") ?? "{}") as Dictionary<string, object>;
        if (dips != null) foreach (KeyValuePair<string, object> kv in dips) { Dictionary<string, object> v = kv.Value as Dictionary<string, object>; if (v != null) { Names[kv.Key] = v; DipKeys.Add(kv.Key); } }
        foreach (object o in AppEnv.GetList(g, "trophies"))
        {
            Dictionary<string, object> t = o as Dictionary<string, object>;
            if (t == null) continue;
            Trophies[AppEnv.GetString(t, "fish")] = new int[] { (int)AppEnv.GetDouble(t, "trophy", 0), (int)AppEnv.GetDouble(t, "rare", 0) };
        }
        foreach (object o in AppEnv.GetList(g, "baits")) if (o != null) Baits.Add(o.ToString());
        Lang = AppEnv.Json.DeserializeObject(AppEnv.ResourceText("data/lang.json")) as Dictionary<string, object>;
        LoadLearnedFish();
    }

    private static void LoadLearnedFish()
    {
        try
        {
            Dictionary<string, object> d = AppEnv.ReadJsonFile(AppEnv.DataPath("lakefish.json")) as Dictionary<string, object>;
            if (d == null) return;
            foreach (KeyValuePair<string, object> kv in d)
            {
                Lake l;
                if (!LakeById.TryGetValue(kv.Key, out l)) continue;
                IList list = kv.Value as IList;
                if (list == null) continue;
                foreach (object f in list) if (f != null && !l.Fish.Contains(f.ToString())) l.Fish.Add(f.ToString());
            }
        }
        catch (Exception ex) { AppEnv.Log("Daten", "lakefish.json: " + ex.Message); }
    }

    public static string LakeIdByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string id;
        return LakeByName.TryGetValue(name.Trim(), out id) ? id : "";
    }

    public static string Translate(string en, string lang)
    {
        if (string.IsNullOrEmpty(en)) return "";
        Dictionary<string, object> e;
        if (Names.TryGetValue(en, out e))
        {
            object v;
            if (e.TryGetValue(lang, out v) && v != null && v.ToString().Length > 0) return v.ToString();
            return en;
        }
        if (lang == "en") return en;
        EnsureItems();
        string[] row;
        if (itemRows.TryGetValue(en, out row))
        {
            int idx = Array.IndexOf(itemLangs, lang);
            if (idx >= 0 && idx < row.Length && !string.IsNullOrEmpty(row[idx])) return row[idx];
        }
        return en;
    }

    private static void EnsureItems()
    {
        if (itemRows != null) return;
        lock (itemLock)
        {
            if (itemRows != null) return;
            Dictionary<string, string[]> rows = new Dictionary<string, string[]>(StringComparer.Ordinal);
            Dictionary<string, object> d = AppEnv.Json.DeserializeObject(AppEnv.ResourceText("data/itemnames.json")) as Dictionary<string, object>;
            List<string> langs = new List<string>();
            foreach (object o in AppEnv.GetList(d, "langs")) langs.Add(o.ToString());
            foreach (object o in AppEnv.GetList(d, "rows"))
            {
                IList r = o as IList;
                if (r == null || r.Count == 0) continue;
                string[] a = new string[r.Count];
                for (int i = 0; i < r.Count; i++) a[i] = r[i] == null ? "" : r[i].ToString();
                if (!rows.ContainsKey(a[0])) rows[a[0]] = a;
            }
            itemLangs = langs.ToArray();
            itemList = new List<string[]>(rows.Values);
            itemRows = rows;
        }
    }

    public static List<string[]> ItemRows()
    {
        EnsureItems();
        return itemList;
    }

    public static string[] ItemLangs()
    {
        EnsureItems();
        return itemLangs;
    }

    public static bool HasItem(string en)
    {
        EnsureItems();
        return itemRows.ContainsKey(en);
    }

    public static string Ui(string lang, string key, string fallback)
    {
        object v;
        Dictionary<string, object> cur = Lang != null && Lang.ContainsKey(lang) ? Lang[lang] as Dictionary<string, object> : null;
        if (cur != null && cur.TryGetValue(key, out v) && v != null && v.ToString().Length > 0) return v.ToString();
        Dictionary<string, object> en = Lang != null && Lang.ContainsKey("en") ? Lang["en"] as Dictionary<string, object> : null;
        if (en != null && en.TryGetValue(key, out v) && v != null && v.ToString().Length > 0) return v.ToString();
        return fallback;
    }

    public static Dictionary<string, object> Strings(string lang)
    {
        Dictionary<string, object> outD = new Dictionary<string, object>();
        Dictionary<string, object> en = Lang != null && Lang.ContainsKey("en") ? Lang["en"] as Dictionary<string, object> : null;
        Dictionary<string, object> cur = Lang != null && Lang.ContainsKey(lang) ? Lang[lang] as Dictionary<string, object> : null;
        if (en != null) foreach (KeyValuePair<string, object> kv in en) outD[kv.Key] = kv.Value;
        if (cur != null) foreach (KeyValuePair<string, object> kv in cur) if (kv.Value != null && kv.Value.ToString().Length > 0) outD[kv.Key] = kv.Value;
        return outD;
    }

    public static string[] Languages()
    {
        List<string> l = new List<string>();
        if (Lang != null) foreach (string k in Lang.Keys) l.Add(k);
        return l.ToArray();
    }

    public static double[] ToNorm(Lake l, double x, double y)
    {
        return new double[] { (x-l.XMin) / (l.XMax-l.XMin), (l.YNorth-y) / (l.YNorth-l.YSouth) };
    }

    public static string FormatWeight(double grams, string lang)
    {
        CultureInfo c;
        try { c = CultureInfo.GetCultureInfo(lang == "zh-TW" ? "zh-TW" : lang); } catch (Exception) { c = CultureInfo.InvariantCulture; }
        if (grams >= 1000) return (grams / 1000.0).ToString("0.###", c) + " kg";
        return grams.ToString("N0", c) + " g";
    }
}
