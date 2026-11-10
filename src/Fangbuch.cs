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
    private static readonly string[] ImgKinds = { "spot", "net", "net2", "net3", "setup", "feed", "map", "other" };
    private static readonly string[] NetKinds = { "net", "net2", "net3" };
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
            string mark = Path.Combine(ImgDir, "clean2.txt");
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

    public static Dictionary<string, object> SpotById(string id)
    {
        lock (gate) return List("spots").FirstOrDefault(x => S(x, "id") == id && id.Length > 0);
    }

    public static Dictionary<string, object> RecipeByName(string name)
    {
        lock (gate) return name.Length == 0 ? null : List("recipes").FirstOrDefault(x => S(x, "name") == name);
    }

    public static void SetSpotValue(string id, string key, object value)
    {
        lock (gate)
        {
            List<Dictionary<string, object>> spots = List("spots");
            Dictionary<string, object> s = spots.FirstOrDefault(x => S(x, "id") == id);
            if (s == null) return;
            if (value == null) s.Remove(key); else s[key] = value;
            UserStore.Set("spots", spots);
        }
    }

    public static int[] SpotPosition(Dictionary<string, object> s)
    {
        return SpotXY(s);
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

    private static bool FillNet(Dictionary<string, object> s)
    {
        Dictionary<string, object> im = Images(s);
        List<string> files = NetKinds.Where(k => im.ContainsKey(k)).Select(k => im[k].ToString()).ToList();
        string net = string.Join(",", files.ToArray());
        if (s.ContainsKey("net") && S(s, "netImg") == net && S(s, "netV") == "2") return false;
        List<TrackItem> all = new List<TrackItem>();
        Dictionary<TrackItem, DateTime> when = new Dictionary<TrackItem, DateTime>();
        int total = 0;
        foreach (string name in files)
        {
            string f = Path.Combine(ImgDir, name);
            if (!File.Exists(f)) continue;
            Dictionary<TrackItem, DateTime> times = new Dictionary<TrackItem, DateTime>();
            List<TrackItem> items;
            int t;
            try { items = Tracker.NetItems(f, times, out t); }
            catch (Exception ex) { AppEnv.Log("Fangbuch", "Kescherbild " + name + ": " + ex.Message); return false; }
            total = Math.Max(total, t);
            HashSet<TrackItem> taken = new HashSet<TrackItem>();
            foreach (TrackItem it in items)
            {
                DateTime ti;
                bool hasT = times.TryGetValue(it, out ti);
                TrackItem same = all.Where(x => !taken.Contains(x) && x.Fish == it.Fish && x.Weight == it.Weight)
                    .OrderBy(x => hasT && when.ContainsKey(x) ? Math.Abs(when[x].Subtract(ti).TotalMinutes) : 1000)
                    .FirstOrDefault(x => !hasT || !when.ContainsKey(x) || Math.Abs(when[x].Subtract(ti).TotalMinutes) <= 3);
                if (same != null) { taken.Add(same); if (hasT && !when.ContainsKey(same)) when[same] = ti; continue; }
                all.Add(it);
                taken.Add(it);
                if (hasT) when[it] = ti;
            }
        }
        List<object> o = new List<object>();
        foreach (IGrouping<string, TrackItem> g in all.GroupBy(i => i.Fish))
            o.Add(AppEnv.Obj("fish", g.Key, "n", g.Count(), "max", g.Select(i => i.Weight ?? 0).Where(w => w > 0 && GameData.Plausible(g.Key, w)).DefaultIfEmpty(0).Max()));
        if (total > AppEnv.GetDouble(s, "count", 0)) s["count"] = total.ToString(CultureInfo.InvariantCulture);
        s["net"] = o.ToArray();
        s["netImg"] = net;
        s["netV"] = "2";
        return true;
    }

    private static List<Dictionary<string, object>> NetList(Dictionary<string, object> s)
    {
        return AppEnv.GetList(s, "net").Cast<object>().OfType<Dictionary<string, object>>().ToList();
    }

    public static object Log(string lang)
    {
        List<Dictionary<string, object>> catches = List("catches"), spots;
        lock (gate)
        {
            spots = List("spots");
            bool changed = false;
            foreach (Dictionary<string, object> s in spots) changed |= FillNet(s);
            if (changed) UserStore.Set("spots", spots);
        }
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
                "clip", S(s, "clip"), "temp", S(s, "temp"), "rig", Tr(S(s, "rig"), lang), "rigs", AppEnv.GetList(s, "rigs").Cast<object>().Select(o => { Dictionary<string, object> m = o as Dictionary<string, object>; return (object)AppEnv.Obj("rig", Tr(S(m, "rig"), lang), "bait", Tr(S(m, "bait"), lang), "bait2", Tr(S(m, "bait2"), lang), "dip", Tr(S(m, "dip"), lang)); }).ToArray(), "src", S(s, "src"), "images", Images(s),
                "count", AppEnv.GetDouble(s, "count", 0), "minutes", AppEnv.GetDouble(s, "minutes", 0), "money", AppEnv.GetDouble(s, "money", 0),
                "catches", catches.Count(c => S(c, "spotId") == id), "shared", S(s, "shared").Length > 0,
                "nx", AppEnv.GetDouble(s, "nx", -1), "ny", AppEnv.GetDouble(s, "ny", -1),
                "net", NetList(s).Select(n => { double mx = AppEnv.GetDouble(n, "max", 0); return (object)AppEnv.Obj("fish", Tr(S(n, "fish"), lang), "n", (int)AppEnv.GetDouble(n, "n", 0), "max", mx, "maxText", mx > 0 ? GameData.FormatWeight(mx, lang) : ""); }).ToArray()));
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
                "uses", S(rc, "name").Length == 0 ? 0 : spots.Where(s => S(s, "pva") == S(rc, "name")).Sum(s => NetList(s).Sum(n => (int)AppEnv.GetDouble(n, "n", 0)))));
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
        string en = translate ? GameData.Untranslate(v, lang) : v;
        target[key] = translate && key.StartsWith("bait", StringComparison.Ordinal) ? GameData.ToKey(en) : en;
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

    private static readonly HashSet<string> BaseItems = new HashSet<string>(new string[] {
        "Corn flour", "Rape Seed", "Ground peas", "Sunflower seed", "Flaxseed", "Rye bran", "Wheat bran", "Ground Crackers", "Oat flakes",
        "Hempseed flour", "Feed", "Semolina", "Pearl barley" }, StringComparer.Ordinal);
    private static readonly HashSet<string> AromaItems = new HashSet<string>(new string[] {
        "Honey", "Hempseed Oil", "Linseed Oil", "Anise Oil", "Fish Oil", "Vanilla", "Coriander", "Cinnamon", "Fennel", "Garlic", "Caramel",
        "Sunflower Oil", "Strawberry", "Salmon", "Caviar smell", "Black pepper", "Crab and mussels", "Bitter almond", "Nut mixture", "Cream",
        "Banana", "Mint", "White chocolate", "Coconut", "Red plum", "Apricot", "Squid", "Freshly cut grass", "Corn liquor", "Bloodworm Extract",
        "Earthworm extract", "Ripe pear", "Tutti Frutti", "Citrus Mix", "Mango", "Pineapple", "Atlantic tuna", "Halibut oil", "Mussel sauce",
        "Red krill", "Smoked sausages", "Blackberry jam", "Mulberry", "Raspberry", "Cranberry & Spices", "Cherry Liquor", "Black currant",
        "Amino acids", "Tiger nut extract", "Coconut cream", "Caramel peach", "Salmon oil", "Strawberry and spice", "Vanilla cream", "Spicy garlic",
        "Allspice", "Amino acid", "Tuna oil", "Spices" }, StringComparer.Ordinal);
    private static readonly HashSet<string> AddItems = new HashSet<string>(new string[] {
        "Casters", "Maggots", "Corn", "Clay", "Peas", "Chopped worm", "Millet Porridge", "Pellets", "Biscuit",
        "Cookies", "Zebra mussels", "Pumpkin seed", "Dried Krill", "Wheat Grains", "Chicken egg", "Rice", "Powdered milk",
        "Condensed milk", "Hemp seeds", "Corn seeds", "Wheat seeds", "Shelled peas" }, StringComparer.Ordinal);

    public static object ItemNames(string lang)
    {
        Dictionary<string, int[]> seen = new Dictionary<string, int[]>(StringComparer.Ordinal);
        Func<string, string> key = t =>
        {
            string k = GameData.ToKey((t ?? "").Trim());
            if (k.EndsWith(GameData.Crushed, StringComparison.Ordinal)) k = k.Substring(0, k.Length-GameData.Crushed.Length);
            return k.Length > 0 && (GameData.HasItem(k) || GameData.Names.ContainsKey(k)) ? k : "";
        };
        Action<string, int, int> count = (k, slot, n) => { if (k.Length == 0) return; int[] c; if (!seen.TryGetValue(k, out c)) seen[k] = c = new int[3]; c[slot] += n; };
        foreach (Report r in Community.All())
        {
            if (!Regex.IsMatch(r.Id, "^(wx|dcd|adm):")) continue;
            string[] parts = r.S("groundbait").Split(new string[] { " + " }, StringSplitOptions.RemoveEmptyEntries).Select(key).ToArray();
            if (parts.Length < 3) continue;
            count(parts[0], 0, 1);
            count(parts[parts.Length-1], 2, 1);
            for (int i = 1; i < parts.Length-1; i++) count(parts[i], 1, 1);
        }
        foreach (Dictionary<string, object> rc in List("recipes"))
        {
            count(key(S(rc, "base")), 0, 3);
            count(key(S(rc, "attractant")), 2, 3);
            foreach (object o in AppEnv.GetList(rc, "additives")) count(key((o ?? "").ToString()), 1, 3);
        }
        List<string>[] lists = { new List<string>(), new List<string>(), new List<string>() };
        HashSet<string> all = new HashSet<string>(seen.Keys, StringComparer.Ordinal);
        foreach (string b in GameData.Baits) if (Regex.IsMatch(b, @"\d+( Pop-Up| Soluble)?$")) all.Add(b);
        foreach (string[] row in GameData.ItemRows()) if (BaseItems.Contains(row[0]) || AromaItems.Contains(row[0]) || AddItems.Contains(row[0]) || row[0].Contains("Feed Pellets")) all.Add(row[0]);
        HashSet<string> dips = new HashSet<string>(GameData.DipKeys, StringComparer.Ordinal);
        HashSet<string> fish = new HashSet<string>(GameData.Lakes.SelectMany(l => l.Fish).Concat(GameData.Trophies.Keys), StringComparer.Ordinal);
        HashSet<string> fishNames = new HashSet<string>(fish.Select(f => GameData.Translate(f, lang)), StringComparer.OrdinalIgnoreCase);
        foreach (string k in all)
        {
            if (dips.Contains(k) || fish.Contains(k) || fishNames.Contains(GameData.Translate(k, lang)) || Regex.IsMatch(k, @"(?i)\breel\b|\bdip\b|^(groundbait|dry mix|ready-made groundbait|bream mix|press cake|breadcrumbs|bread crumbs)$|\(large bag\)")) continue;
            int slot;
            if (BaseItems.Contains(k)) slot = 0;
            else if (AromaItems.Contains(k)) slot = 2;
            else if (AddItems.Contains(k) || k.Contains("Feed Pellets") || Regex.IsMatch(k, @"\d+( Pop-Up| Soluble)?$")) slot = 1;
            else
            {
                int[] c;
                if (!seen.TryGetValue(k, out c) || c.Sum() < 1) continue;
                slot = c[2] > c[1] ? 2 : 1;
            }
            lists[slot].Add(GameData.Translate(k, lang));
        }
        Func<List<string>, string[]> sorted = l => l.Distinct().OrderBy(x => x).ToArray();
        string[] bases = sorted(lists[0]), aromas = sorted(lists[2]).Where(x => !bases.Contains(x)).ToArray();
        return AppEnv.Obj("base", bases, "add", sorted(lists[1]).Where(x => !bases.Contains(x) && !aromas.Contains(x)).ToArray(), "aroma", aromas);
    }

    public static object SaveSpot(Dictionary<string, object> a, string lang)
    {
        lock (gate)
        {
            List<Dictionary<string, object>> spots = List("spots");
            string id = AppEnv.GetString(a, "id");
            Dictionary<string, object> s = spots.FirstOrDefault(x => S(x, "id") == id && id.Length > 0);
            if (s == null) { s = new Dictionary<string, object>(); s["id"] = NewId(); spots.Add(s); }
            foreach (string k in new string[] { "lake", "name", "groundbait", "pva", "feedType", "tech", "dist", "depth", "notes", "clip", "temp", "src", "count", "minutes", "money" }) Put(s, a, k, false, lang);
            foreach (string k in new string[] { "fish", "bait", "bait2", "dip", "rig" }) Put(s, a, k, true, lang);
            if (a.ContainsKey("rigs"))
            {
                List<object> rigs = new List<object>();
                foreach (object o in AppEnv.GetList(a, "rigs"))
                {
                    Dictionary<string, object> src = o as Dictionary<string, object>, t = new Dictionary<string, object>();
                    foreach (string k in new string[] { "rig", "bait", "bait2", "dip" }) Put(t, src, k, true, lang);
                    if (S(t, "bait").Length > 0 || S(t, "rig").Length > 0) rigs.Add(t);
                }
                s["rigs"] = rigs;
            }
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
