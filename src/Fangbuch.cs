using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

internal static class Fangbuch
{
    private static readonly object gate = new object();
    private static readonly Random rnd = new Random();
    private static readonly string[] ImgKinds = { "spot", "net", "setup", "feed", "map", "other" };
    private const int NotesMax = 600;

    private static string ImgDir
    {
        get { return AppEnv.DataPath("fangbuch_img"); }
    }

    private static Dictionary<string, object> Images(Dictionary<string, object> c)
    {
        Dictionary<string, object> outD = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> d = c.ContainsKey("images") ? c["images"] as Dictionary<string, object> : null;
        if (d == null) return outD;
        foreach (string k in ImgKinds)
        {
            string n = AppEnv.GetString(d, k);
            if (Regex.IsMatch(n, "^[0-9a-f]{10}\\.(png|jpg|webp)$")) outD[k] = n;
        }
        return outD;
    }

    public static object SaveImage(Dictionary<string, object> a, string lang)
    {
        string data = AppEnv.GetString(a, "data");
        Match m = Regex.Match(data, "^data:image/(png|jpeg|webp);base64,");
        if (!m.Success) throw new AppError("imageInvalid", null);
        byte[] b;
        try { b = Convert.FromBase64String(data.Substring(m.Length)); }
        catch (FormatException) { throw new AppError("imageInvalid", null); }
        Directory.CreateDirectory(ImgDir);
        string name = NewId() + "." + (m.Groups[1].Value == "jpeg" ? "jpg" : m.Groups[1].Value);
        string file = Path.Combine(ImgDir, name);
        File.WriteAllBytes(file, b);
        Dictionary<string, object> analysis = Tracker.Analyze(file, lang) as Dictionary<string, object>;
        Privacy.Clean(file, Tracker.LangTag, analysis);
        return AppEnv.Obj("name", name, "analysis", analysis);
    }

    public static bool ImageFile(string name, out byte[] data, out string type)
    {
        data = null;
        type = null;
        if (!Regex.IsMatch(name, "^[0-9a-f]{10}\\.(png|jpg|webp)$")) return false;
        string f = Path.Combine(ImgDir, name);
        if (!File.Exists(f)) return false;
        data = File.ReadAllBytes(f);
        type = name.EndsWith(".png") ? "image/png" : name.EndsWith(".webp") ? "image/webp" : "image/jpeg";
        return true;
    }

    public static object Analyze(Dictionary<string, object> a, string lang)
    {
        string name = AppEnv.GetString(a, "name");
        if (!Regex.IsMatch(name, "^[0-9a-f]{10}\\.(png|jpg|webp)$")) throw new AppError("imageInvalid", null);
        string f = Path.Combine(ImgDir, name);
        if (!File.Exists(f)) throw new AppError("imageInvalid", null);
        return Tracker.Analyze(f, lang);
    }

    public static object Watch(long since)
    {
        string dir = Tracker.Dir;
        long upTo = DateTime.UtcNow.AddSeconds(-1).Ticks;
        List<object> files = new List<object>();
        if (since > 0 && Directory.Exists(dir))
            foreach (FileInfo f in new DirectoryInfo(dir).GetFiles().Where(f => Regex.IsMatch(f.Extension, @"^\.(png|jpe?g)$", RegexOptions.IgnoreCase) && f.LastWriteTimeUtc.Ticks > since && f.LastWriteTimeUtc.Ticks <= upTo).OrderBy(f => f.LastWriteTimeUtc))
                files.Add(f.FullName);
        return AppEnv.Obj("now", (since > 0 ? Math.Max(upTo, since) : upTo).ToString(CultureInfo.InvariantCulture), "dir", dir, "dirOk", Directory.Exists(dir), "files", files.ToArray());
    }

    public static object Import(Dictionary<string, object> a, string lang)
    {
        string src = Path.GetFullPath(AppEnv.GetString(a, "path"));
        string dir = Path.GetFullPath(Tracker.Dir).TrimEnd('\\') + "\\";
        if (!src.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || !File.Exists(src) || !Regex.IsMatch(src, @"\.(png|jpe?g)$", RegexOptions.IgnoreCase)) throw new AppError("imageInvalid", null);
        Directory.CreateDirectory(ImgDir);
        string name = NewId() + (src.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg");
        File.Copy(src, Path.Combine(ImgDir, name));
        Dictionary<string, object> analysis = Tracker.Analyze(Path.Combine(ImgDir, name), lang) as Dictionary<string, object>;
        Privacy.Clean(Path.Combine(ImgDir, name), Tracker.LangTag, analysis);
        return AppEnv.Obj("name", name, "analysis", analysis);
    }

    public static void CleanExisting(string lang)
    {
        try
        {
            if (!Directory.Exists(ImgDir)) return;
            string mark = Path.Combine(ImgDir, "clean.txt");
            HashSet<string> done = new HashSet<string>(File.Exists(mark) ? File.ReadAllLines(mark) : new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (string f in Directory.GetFiles(ImgDir).Where(f => Regex.IsMatch(Path.GetFileName(f), "^[0-9a-f]{10}\\.(png|jpg|webp)$")))
            {
                string n = Path.GetFileName(f);
                if (done.Contains(n)) continue;
                if (File.GetCreationTimeUtc(f) > Privacy.Since) { File.AppendAllText(mark, n + Environment.NewLine); continue; }
                Privacy.Clean(f, Tracker.LangTag, Tracker.Analyze(f, lang) as Dictionary<string, object>);
                File.AppendAllText(mark, n + Environment.NewLine);
            }
        }
        catch (Exception ex) { AppEnv.Log("Datenschutz", "Nachbearbeitung: " + ex.Message); }
    }

    private static void CleanImages()
    {
        try
        {
            if (!Directory.Exists(ImgDir)) return;
            HashSet<string> used = new HashSet<string>(List("catches").Concat(List("spots")).SelectMany(c => Images(c).Values.Select(v => v.ToString())), StringComparer.OrdinalIgnoreCase);
            foreach (string f in Directory.GetFiles(ImgDir))
                if (!used.Contains(Path.GetFileName(f)) && File.GetCreationTimeUtc(f) < DateTime.UtcNow.AddHours(-6)) File.Delete(f);
        }
        catch (Exception ex) { AppEnv.Log("Fangbuch", "Bilder aufraeumen: " + ex.Message); }
    }

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
        return v.Length == 0 ? "" : GameData.Translate(GameData.ToKey(v), lang);
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
                "dip", Tr(S(c, "dip"), lang), "pva", S(c, "pva"), "feedType", S(c, "feedType"), "rig", Tr(S(c, "rig"), lang),
                "tech", S(c, "tech"), "temp", S(c, "temp"), "images", Images(c),
                "x", x, "y", y, "clip", S(c, "clip"), "depth", S(c, "depth"),
                "spotId", S(c, "spotId"), "spotName", sp != null ? S(sp, "name") : "", "notes", S(c, "notes") == "Tracker" ? "" : S(c, "notes"),
                "tracker", S(c, "notes") == "Tracker"));
        }

        List<object> stats = new List<object>();
        foreach (IGrouping<string, Dictionary<string, object>> g in catches.GroupBy(c => S(c, "fish")).OrderByDescending(g => g.Count()))
        {
            if (g.Key.Length == 0) continue;
            double max = g.Select(c => AppEnv.GetDouble(c, "weight", 0)).Where(w => GameData.Plausible(g.Key, w)).DefaultIfEmpty(0).Max();
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
                "groundbait", S(s, "groundbait"), "pva", S(s, "pva"), "feedType", S(s, "feedType"), "tech", S(s, "tech"), "dist", S(s, "dist"), "depth", S(s, "depth"), "notes", S(s, "notes"),
                "clip", S(s, "clip"), "temp", S(s, "temp"), "rig", Tr(S(s, "rig"), lang), "src", S(s, "src"), "images", Images(s),
                "catches", catches.Count(c => S(c, "spotId") == id),
                "nx", AppEnv.GetDouble(s, "nx", -1), "ny", AppEnv.GetDouble(s, "ny", -1)));
        }

        List<object> outRecipes = new List<object>();
        foreach (Dictionary<string, object> rc in List("recipes"))
        {
            List<string> lakes = new List<string>();
            object lv;
            if (rc.TryGetValue("lakes", out lv) && lv != null)
            {
                IEnumerable le = lv is string ? new object[] { lv } : lv as IEnumerable;
                if (le != null) foreach (object o in le) { string n = (o ?? "").ToString().Trim(); if (n.Length == 0) continue; string id = GameData.LakeById.ContainsKey(n) ? n : GameData.LakeIdByName(n); lakes.Add(id.Length > 0 ? LakeName(id, lang) : n); }
            }
            outRecipes.Add(AppEnv.Obj("id", S(rc, "id"), "name", S(rc, "name"), "type", S(rc, "type"),
                "base", Tr(S(rc, "base"), lang), "attractant", Tr(S(rc, "attractant"), lang),
                "additives", AppEnv.GetList(rc, "additives").Cast<object>().Select(o => Tr((o ?? "").ToString().Trim(), lang)).Where(x => x.Length > 0).ToArray(),
                "lakes", lakes.ToArray(), "notes", S(rc, "notes") == "Tracker" ? "" : S(rc, "notes"),
                "uses", catches.Count(c => S(c, "pva") == S(rc, "name") && S(rc, "name").Length > 0)));
        }

        return AppEnv.Obj("catches", outCatches, "stats", stats, "spots", outSpots, "recipes", outRecipes,
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
            foreach (string k in new string[] { "date", "lake", "x", "y", "clip", "depth", "spotId", "pva", "feedType", "tech", "temp" }) Put(c, a, k, false, lang);
            foreach (string k in new string[] { "fish", "bait", "bait2", "dip", "rig" }) Put(c, a, k, true, lang);
            if (a.ContainsKey("notes") && !(S(c, "notes") == "Tracker" && AppEnv.GetString(a, "notes").Length == 0)) Put(c, a, "notes", false, lang);
            if (S(c, "notes").Length > NotesMax) c["notes"] = S(c, "notes").Substring(0, NotesMax);
            if (a.ContainsKey("images")) c["images"] = Images(a);
            if (a.ContainsKey("weight"))
            {
                double kg;
                string w = AppEnv.GetString(a, "weight").Replace(",", ".").Trim();
                c["weight"] = double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out kg) ? (int)Math.Round(kg * 1000) : 0;
            }
            if (S(c, "fish").Length == 0) throw new AppError("fishMissing", null);
            UserStore.Set("catches", catches);
            CleanImages();
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
            if (key == "catches" || key == "spots") CleanImages();
            return AppEnv.Obj("removed", before-l.Count);
        }
    }

    public static bool HasCatch(string fish, int grams, string date)
    {
        return List("catches").Any(c => S(c, "fish") == fish && (int)AppEnv.GetDouble(c, "weight", -1) == grams && S(c, "date") == date);
    }

    public static List<Dictionary<string, object>> SessionCatches(string session)
    {
        return List("catches").Where(c => S(c, "session") == session && session.Length > 0).ToList();
    }

    public static void AddCatches(List<Dictionary<string, object>> items)
    {
        lock (gate)
        {
            List<Dictionary<string, object>> catches = List("catches");
            foreach (Dictionary<string, object> c in items) { c["id"] = NewId(); catches.Add(c); }
            UserStore.Set("catches", catches);
        }
    }

    public static string AddRecipeFromTracker(Dictionary<string, object> rc, string lakeId)
    {
        lock (gate)
        {
            List<Dictionary<string, object>> recipes = List("recipes");
            Func<Dictionary<string, object>, string> sig = x => S(x, "type") + "|" + S(x, "base") + "|" + string.Join("+", AppEnv.GetList(x, "additives").Cast<object>().Select(o => (o ?? "").ToString()).ToArray()) + "|" + S(x, "attractant");
            string name = S(rc, "name"), mine = sig(rc);
            bool taken = false;
            foreach (Dictionary<string, object> x in recipes)
            {
                string xn = S(x, "name");
                if (xn != name && !xn.StartsWith(name + " (")) continue;
                if (xn == name) taken = true;
                if (sig(x) == mine) return GameData.Ui(UserStore.Lang, "trackerRecipeExists", "Rezept schon vorhanden, nichts geändert") + ": " + xn;
            }
            if (taken) name = name + " (" + DateTime.Now.ToString("dd.MM. HH:mm") + ")";
            Lake l;
            recipes.Add(AppEnv.Obj("id", NewId(), "name", name, "type", S(rc, "type"), "fish", new object[0],
                "lakes", GameData.LakeById.TryGetValue(lakeId ?? "", out l) ? new object[] { l.Name("en") } : new object[0],
                "base", S(rc, "base"), "additives", AppEnv.GetList(rc, "additives"), "attractant", S(rc, "attractant"), "notes", "Tracker"));
            UserStore.Set("recipes", recipes);
            return GameData.Ui(UserStore.Lang, "trackerRecipe", "Neues Rezept angelegt") + ": " + name;
        }
    }

    public static object SaveRecipe(Dictionary<string, object> a, string lang)
    {
        lock (gate)
        {
            List<Dictionary<string, object>> recipes = List("recipes");
            string id = AppEnv.GetString(a, "id");
            Dictionary<string, object> rc = recipes.FirstOrDefault(x => S(x, "id") == id && id.Length > 0);
            if (rc == null) { rc = new Dictionary<string, object>(); rc["id"] = NewId(); rc["fish"] = new object[0]; recipes.Add(rc); }
            Put(rc, a, "name", false, lang);
            Put(rc, a, "type", false, lang);
            Put(rc, a, "base", true, lang);
            Put(rc, a, "attractant", true, lang);
            if (a.ContainsKey("notes") && !(S(rc, "notes") == "Tracker" && AppEnv.GetString(a, "notes").Length == 0)) Put(rc, a, "notes", false, lang);
            rc["additives"] = AppEnv.GetList(a, "additives").Cast<object>().Select(o => GameData.Untranslate((o ?? "").ToString(), lang)).Where(x => x.Length > 0).ToArray();
            string lake = AppEnv.GetString(a, "lake");
            Lake l;
            rc["lakes"] = GameData.LakeById.TryGetValue(lake, out l) ? new object[] { l.Name("en") } : new object[0];
            if (S(rc, "name").Length == 0) throw new AppError("nameMissing", null);
            UserStore.Set("recipes", recipes);
            return AppEnv.Obj("id", rc["id"]);
        }
    }

    public static object ItemNames(string lang)
    {
        return GameData.ItemRows().Select(r => GameData.Translate(r[0], lang)).Distinct().OrderBy(x => x).ToArray();
    }

    public static object SaveSpot(Dictionary<string, object> a, string lang)
    {
        lock (gate)
        {
            List<Dictionary<string, object>> spots = List("spots");
            string id = AppEnv.GetString(a, "id");
            Dictionary<string, object> s = spots.FirstOrDefault(x => S(x, "id") == id && id.Length > 0);
            if (s == null) { s = new Dictionary<string, object>(); s["id"] = NewId(); spots.Add(s); }
            foreach (string k in new string[] { "lake", "name", "groundbait", "pva", "feedType", "tech", "dist", "depth", "notes", "clip", "temp", "src" }) Put(s, a, k, false, lang);
            foreach (string k in new string[] { "fish", "bait", "bait2", "dip", "rig" }) Put(s, a, k, true, lang);
            if (S(s, "notes").Length > NotesMax) s["notes"] = S(s, "notes").Substring(0, NotesMax);
            if (a.ContainsKey("images")) s["images"] = Images(a);
            Lake l;
            int px, py;
            if (GameData.LakeById.TryGetValue(S(s, "lake"), out l) && l.HasBounds && int.TryParse(AppEnv.GetString(a, "x"), out px) && int.TryParse(AppEnv.GetString(a, "y"), out py))
            {
                s["nx"] = (px-l.XMin) / (l.XMax-l.XMin);
                s["ny"] = (py-l.YNorth) / (l.YSouth-l.YNorth);
            }
            UserStore.Set("spots", spots);
            CleanImages();
            return AppEnv.Obj("id", s["id"]);
        }
    }
}
