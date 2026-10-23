using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

internal static class Fangbuch
{
    private static readonly object gate = new object();
    private static readonly Random rnd = new Random();

    private static List<Dictionary<string, object>> List(string key)
    {
        List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
        IEnumerable e = UserStore.Get(key) as IEnumerable;
        if (e != null) foreach (object o in e) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d != null) l.Add(d); }
        return l;
    }

    private static string NewId()
    {
        lock (rnd) return rnd.Next().ToString("x8") + rnd.Next(256).ToString("x2");
    }

    private static string S(Dictionary<string, object> d, string k)
    {
        return AppEnv.GetString(d, k).Trim();
    }

    private static string Tr(string v, string lang)
    {
        return v.Length == 0 ? "" : GameData.Translate(v, lang);
    }

    private static string LakeName(string id, string lang)
    {
        Lake l;
        return GameData.LakeById.TryGetValue(id, out l) ? l.Name(lang) : id;
    }

    private static int[] SpotXY(Dictionary<string, object> s)
    {
        Lake l;
        if (!GameData.LakeById.TryGetValue(S(s, "lake"), out l) || !l.HasBounds) return null;
        double nx = AppEnv.GetDouble(s, "nx", -1), ny = AppEnv.GetDouble(s, "ny", -1);
        if (nx < 0 || ny < 0) return null;
        return new int[] { (int)Math.Round(l.XMin + nx * (l.XMax-l.XMin)), (int)Math.Round(l.YNorth + ny * (l.YSouth-l.YNorth)) };
    }

    private static string BaitCombo(Dictionary<string, object> c, string lang)
    {
        List<string> p = new List<string>();
        foreach (string k in new string[] { "bait", "bait2" }) { string v = S(c, k); if (v.Length > 0) p.Add(Tr(v, lang)); }
        return string.Join(" + ", p.ToArray());
    }

    public static object Log(string lang)
    {
        List<Dictionary<string, object>> catches = List("catches"), spots = List("spots");
        Dictionary<string, Dictionary<string, object>> spotById = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (Dictionary<string, object> s in spots) spotById[S(s, "id")] = s;

        List<object> outCatches = new List<object>();
        foreach (Dictionary<string, object> c in catches)
        {
            Dictionary<string, object> sp;
            spotById.TryGetValue(S(c, "spotId"), out sp);
            string x = S(c, "x"), y = S(c, "y");
            outCatches.Add(AppEnv.Obj(
                "id", S(c, "id"), "date", S(c, "date"), "lake", S(c, "lake"), "lakeName", LakeName(S(c, "lake"), lang),
                "fishKey", S(c, "fish"), "fish", Tr(S(c, "fish"), lang),
                "grams", (int)AppEnv.GetDouble(c, "weight", 0), "weight", AppEnv.GetDouble(c, "weight", 0) > 0 ? GameData.FormatWeight(AppEnv.GetDouble(c, "weight", 0), lang) : "",
                "bait", Tr(S(c, "bait"), lang), "bait2", Tr(S(c, "bait2"), lang), "baits", BaitCombo(c, lang),
                "dip", Tr(S(c, "dip"), lang), "pva", S(c, "pva"), "rig", Tr(S(c, "rig"), lang),
                "x", x, "y", y, "clip", S(c, "clip"), "depth", S(c, "depth"),
                "spotId", S(c, "spotId"), "spotName", sp != null ? S(sp, "name") : "", "notes", S(c, "notes") == "Tracker" ? "" : S(c, "notes"),
                "tracker", S(c, "notes") == "Tracker"));
        }

        List<object> stats = new List<object>();
        foreach (IGrouping<string, Dictionary<string, object>> g in catches.GroupBy(c => S(c, "fish")).OrderByDescending(g => g.Count()))
        {
            if (g.Key.Length == 0) continue;
            double max = g.Max(c => AppEnv.GetDouble(c, "weight", 0));
            string top = g.GroupBy(c => BaitCombo(c, lang)).Where(b => b.Key.Length > 0).OrderByDescending(b => b.Count()).Select(b => b.Key).FirstOrDefault() ?? "";
            stats.Add(AppEnv.Obj("fish", Tr(g.Key, lang), "n", g.Count(), "max", max > 0 ? GameData.FormatWeight(max, lang) : "", "bait", top));
        }

        List<object> outSpots = new List<object>();
        foreach (Dictionary<string, object> s in spots)
        {
            int[] xy = SpotXY(s);
            string id = S(s, "id");
            outSpots.Add(AppEnv.Obj(
                "id", id, "lake", S(s, "lake"), "lakeName", LakeName(S(s, "lake"), lang), "name", S(s, "name"),
                "x", xy != null ? xy[0].ToString(CultureInfo.InvariantCulture) : "", "y", xy != null ? xy[1].ToString(CultureInfo.InvariantCulture) : "",
                "fish", Tr(S(s, "fish"), lang), "bait", Tr(S(s, "bait"), lang), "bait2", Tr(S(s, "bait2"), lang), "dip", Tr(S(s, "dip"), lang),
                "groundbait", S(s, "groundbait"), "pva", S(s, "pva"), "tech", S(s, "tech"), "dist", S(s, "dist"), "depth", S(s, "depth"), "notes", S(s, "notes"),
                "catches", catches.Count(c => S(c, "spotId") == id)));
        }

        return AppEnv.Obj("catches", outCatches, "stats", stats, "spots", outSpots,
            "days", catches.Select(c => S(c, "date")).Distinct().Count());
    }

    public static object Lists(string lakeId, string lang)
    {
        Lake l;
        IEnumerable<string> fish = GameData.LakeById.TryGetValue(lakeId, out l) && l.Fish.Count > 0 ? (IEnumerable<string>)l.Fish : GameData.Lakes.SelectMany(x => x.Fish).Distinct();
        List<Dictionary<string, object>> catches = List("catches");
        return AppEnv.Obj(
            "fish", fish.Select(f => GameData.Translate(f, lang)).Distinct().OrderBy(f => f).ToArray(),
            "baits", GameData.BaitKeys().Select(b => GameData.Translate(b, lang)).Distinct().OrderBy(b => b).ToArray(),
            "dips", GameData.DipKeys.Select(b => GameData.Translate(b, lang)).Distinct().OrderBy(b => b).ToArray(),
            "pva", List("recipes").Select(r => S(r, "name")).Where(n => n.Length > 0).Distinct().OrderBy(n => n).ToArray(),
            "rigs", catches.Select(c => Tr(S(c, "rig"), lang)).Where(r => r.Length > 0).Distinct().OrderBy(r => r).ToArray());
    }

    private static void Put(Dictionary<string, object> target, Dictionary<string, object> src, string key, bool translate, string lang)
    {
        if (src == null || !src.ContainsKey(key)) return;
        string v = AppEnv.GetString(src, key).Trim();
        target[key] = translate ? GameData.Untranslate(v, lang) : v;
    }

    public static object SaveCatch(Dictionary<string, object> a, string lang)
    {
        lock (gate)
        {
            List<Dictionary<string, object>> catches = List("catches");
            string id = AppEnv.GetString(a, "id");
            Dictionary<string, object> c = catches.FirstOrDefault(x => S(x, "id") == id && id.Length > 0);
            if (c == null) { c = new Dictionary<string, object>(); c["id"] = NewId(); catches.Add(c); }
            foreach (string k in new string[] { "date", "lake", "x", "y", "clip", "depth", "spotId", "pva" }) Put(c, a, k, false, lang);
            foreach (string k in new string[] { "fish", "bait", "bait2", "dip", "rig" }) Put(c, a, k, true, lang);
            if (a.ContainsKey("notes") && !(S(c, "notes") == "Tracker" && AppEnv.GetString(a, "notes").Length == 0)) Put(c, a, "notes", false, lang);
            if (a.ContainsKey("weight"))
            {
                double kg;
                string w = AppEnv.GetString(a, "weight").Replace(",", ".").Trim();
                c["weight"] = double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out kg) ? (int)Math.Round(kg * 1000) : 0;
            }
            if (S(c, "fish").Length == 0) throw new AppError("fishMissing", null);
            UserStore.Set("catches", catches);
            return AppEnv.Obj("id", c["id"]);
        }
    }

    public static object Delete(string key, IEnumerable ids)
    {
        HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
        if (ids != null) foreach (object o in ids) if (o != null) set.Add(o.ToString());
        lock (gate)
        {
            List<Dictionary<string, object>> l = List(key);
            int before = l.Count;
            l.RemoveAll(x => set.Contains(S(x, "id")));
            UserStore.Set(key, l);
            return AppEnv.Obj("removed", before-l.Count);
        }
    }

    public static object SaveSpot(Dictionary<string, object> a, string lang)
    {
        lock (gate)
        {
            List<Dictionary<string, object>> spots = List("spots");
            string id = AppEnv.GetString(a, "id");
            Dictionary<string, object> s = spots.FirstOrDefault(x => S(x, "id") == id && id.Length > 0);
            if (s == null) { s = new Dictionary<string, object>(); s["id"] = NewId(); spots.Add(s); }
            foreach (string k in new string[] { "lake", "name", "groundbait", "pva", "tech", "dist", "depth", "notes" }) Put(s, a, k, false, lang);
            foreach (string k in new string[] { "fish", "bait", "bait2", "dip" }) Put(s, a, k, true, lang);
            Lake l;
            int px, py;
            if (GameData.LakeById.TryGetValue(S(s, "lake"), out l) && l.HasBounds && int.TryParse(AppEnv.GetString(a, "x"), out px) && int.TryParse(AppEnv.GetString(a, "y"), out py))
            {
                s["nx"] = (px-l.XMin) / (l.XMax-l.XMin);
                s["ny"] = (py-l.YNorth) / (l.YSouth-l.YNorth);
            }
            UserStore.Set("spots", spots);
            return AppEnv.Obj("id", s["id"]);
        }
    }
}
