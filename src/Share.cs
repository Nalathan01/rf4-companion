using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal static class Share
{
    private const string Repo = "Nalathan01/rf4-companion-data";
    private static readonly object gate = new object();
    private static Timer debounce;
    private static volatile bool busy;
    public static volatile string LastError = "";
    public static DateTime LastUpload = DateTime.MinValue;
    private static DateTime lastFetch = DateTime.MinValue;

    private static string IdFile { get { return AppEnv.DataPath("share_id.txt"); } }
    private static string TokenFile { get { return AppEnv.DataPath("share_token.dat"); } }
    private static string StateFile { get { return AppEnv.DataPath("share_state.json"); } }

    public static string ShareId()
    {
        lock (gate)
        {
            if (File.Exists(IdFile)) { string v = File.ReadAllText(IdFile).Trim(); if (v.Length > 0) return v; }
            string id = Guid.NewGuid().ToString("N").Substring(0, 12);
            File.WriteAllText(IdFile, id);
            return id;
        }
    }

    public static string Token()
    {
        try
        {
            if (!File.Exists(TokenFile)) return "";
            byte[] raw = ProtectedData.Unprotect(System.Convert.FromBase64String(File.ReadAllText(TokenFile).Trim()), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch (Exception) { return ""; }
    }

    public static void SetToken(string tok)
    {
        tok = (tok ?? "").Trim();
        if (tok.Length == 0) { if (File.Exists(TokenFile)) File.Delete(TokenFile); return; }
        byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(tok), null, DataProtectionScope.CurrentUser);
        File.WriteAllText(TokenFile, System.Convert.ToBase64String(enc), new UTF8Encoding(false));
        LastError = "";
        Queue(true);
    }

    private static Dictionary<string, object> State()
    {
        try { return AppEnv.ReadJsonFile(StateFile) as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        catch (Exception) { return new Dictionary<string, object>(); }
    }

    public static object Status()
    {
        Dictionary<string, object> st = State();
        IEnumerable sp = UserStore.Get("spots") as IEnumerable;
        int n = 0;
        if (sp != null) foreach (object o in sp) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d != null && AppEnv.GetBool(d, "share")) n++; }
        return AppEnv.Obj("hasToken", Token().Length > 0, "count", n, "last", AppEnv.GetString(st, "last"), "error", LastError, "busy", busy);
    }

    private static string SpotId(string sid, string spotId)
    {
        using (SHA1 sha = SHA1.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sid + "|" + spotId))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
    }

    private static string Composition(List<Dictionary<string, object>> recipes, string name)
    {
        if (name.Length == 0) return "";
        Dictionary<string, object> r = recipes.FirstOrDefault(x => AppEnv.GetString(x, "name") == name);
        if (r == null) return "";
        List<string> parts = new List<string>();
        string b = AppEnv.GetString(r, "base");
        if (b.Length > 0) parts.Add(b);
        foreach (object o in AppEnv.GetList(r, "additives")) if (o != null && o.ToString().Length > 0) parts.Add(o.ToString());
        string a = AppEnv.GetString(r, "attractant");
        if (a.Length > 0) parts.Add(a);
        return string.Join(", ", parts.ToArray());
    }

    private static List<Dictionary<string, object>> Items(string key)
    {
        List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
        IEnumerable e = UserStore.Get(key) as IEnumerable;
        if (e != null) foreach (object o in e) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d != null) l.Add(d); }
        return l;
    }

    private static void Bump(Dictionary<string, int> d, string k)
    {
        if (k.Length == 0) return;
        int n;
        d.TryGetValue(k, out n);
        d[k] = n + 1;
    }

    private static string Payload()
    {
        string sid = ShareId();
        List<Dictionary<string, object>> spots = Items("spots"), catches = Items("catches"), recipes = Items("recipes");
        List<object> outList = new List<object>();
        foreach (Dictionary<string, object> sp in spots)
        {
            if (!AppEnv.GetBool(sp, "share")) continue;
            Lake lake;
            if (!GameData.LakeById.TryGetValue(AppEnv.GetString(sp, "lake"), out lake) || !lake.HasBounds) continue;
            double nx = AppEnv.GetDouble(sp, "nx", -1), ny = AppEnv.GetDouble(sp, "ny", -1);
            if (nx < 0 || ny < 0) continue;
            string spId = AppEnv.GetString(sp, "id");
            List<Dictionary<string, object>> cs = catches.Where(c => AppEnv.GetString(c, "spotId") == spId).ToList();
            Dictionary<string, int[]> fish = new Dictionary<string, int[]>(StringComparer.Ordinal);
            Dictionary<string, int> baits = new Dictionary<string, int>(StringComparer.Ordinal), dips = new Dictionary<string, int>(StringComparer.Ordinal), pvas = new Dictionary<string, int>(StringComparer.Ordinal);
            string last = "", temp = AppEnv.GetString(sp, "temp");
            foreach (Dictionary<string, object> c in cs)
            {
                string f = AppEnv.GetString(c, "fish");
                int[] fv;
                if (!fish.TryGetValue(f, out fv)) { fv = new int[2]; fish[f] = fv; }
                fv[0]++;
                fv[1] = Math.Max(fv[1], (int)AppEnv.GetDouble(c, "weight", 0));
                Bump(baits, string.Join(" + ", new string[] { AppEnv.GetString(c, "bait"), AppEnv.GetString(c, "bait2") }.Where(x => x.Length > 0).ToArray()));
                Bump(dips, AppEnv.GetString(c, "dip"));
                Bump(pvas, Composition(recipes, AppEnv.GetString(c, "pva")));
                if (string.CompareOrdinal(AppEnv.GetString(c, "date"), last) > 0) last = AppEnv.GetString(c, "date");
                if (temp.Length == 0) temp = AppEnv.GetString(c, "temp");
            }
            if (fish.Count == 0 && AppEnv.GetString(sp, "fish").Length > 0) fish[AppEnv.GetString(sp, "fish")] = new int[2];
            if (baits.Count == 0) Bump(baits, string.Join(" + ", new string[] { AppEnv.GetString(sp, "bait"), AppEnv.GetString(sp, "bait2") }.Where(x => x.Length > 0).ToArray()));
            if (dips.Count == 0) Bump(dips, AppEnv.GetString(sp, "dip"));
            if (pvas.Count == 0) Bump(pvas, Composition(recipes, AppEnv.GetString(sp, "pva")));
            Match cm = Regex.Match(AppEnv.GetString(sp, "dist"), @"\d+(?:[.,]\d+)?");
            object clip = cm.Success ? (object)double.Parse(cm.Value.Replace(",", "."), CultureInfo.InvariantCulture) : null;
            Dictionary<string, object> o = new Dictionary<string, object>();
            o["id"] = SpotId(sid, spId);
            o["lake"] = lake.Id;
            o["x"] = (int)Math.Round(lake.XMin + nx * (lake.XMax-lake.XMin));
            o["y"] = (int)Math.Round(lake.YNorth + ny * (lake.YSouth-lake.YNorth));
            o["clip"] = clip;
            o["dir"] = AppEnv.GetString(sp, "dir").Trim();
            o["depth"] = AppEnv.GetString(sp, "depth").Trim();
            o["tech"] = AppEnv.GetString(sp, "tech");
            o["temp"] = temp;
            o["fish"] = fish.Select(kv => (object)AppEnv.Obj("f", kv.Key, "n", kv.Value[0], "max", kv.Value[1])).ToArray();
            o["baits"] = baits.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToArray();
            o["dip"] = dips.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? "";
            o["pva"] = pvas.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? "";
            o["groundbait"] = Composition(recipes, AppEnv.GetString(sp, "groundbait"));
            o["last"] = last;
            o["catches"] = cs.Count;
            outList.Add(o);
        }
        outList.Sort((a, b) => string.CompareOrdinal((string)((Dictionary<string, object>)a)["id"], (string)((Dictionary<string, object>)b)["id"]));
        return AppEnv.Json.Serialize(AppEnv.Obj("v", 1, "source", "companion", "spots", outList));
    }

    private static string Hash(string t)
    {
        using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(t))).Replace("-", "");
    }

    public static void Queue(bool force)
    {
        lock (gate)
        {
            if (debounce != null) debounce.Dispose();
            debounce = new Timer(delegate { try { Upload(force); } catch (Exception ex) { LastError = ex.Message; AppEnv.Log("Teilen", ex.Message); } }, null, 4000, Timeout.Infinite);
        }
    }

    private static HttpWebRequest Gh(string method, string url, string tok)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = method;
        req.UserAgent = "RF4Companion/2.0";
        req.Accept = "application/vnd.github+json";
        req.Headers["X-GitHub-Api-Version"] = "2022-11-28";
        if (tok.Length > 0) req.Headers["Authorization"] = "Bearer " + tok;
        req.Timeout = 20000;
        return req;
    }

    private static string ErrorText(WebException ex)
    {
        HttpWebResponse r = ex.Response as HttpWebResponse;
        if (r == null) return ex.Message;
        try
        {
            using (StreamReader sr = new StreamReader(r.GetResponseStream()))
            {
                Dictionary<string, object> d = AppEnv.Json.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
                return "HTTP " + (int)r.StatusCode + " " + AppEnv.GetString(d, "message");
            }
        }
        catch (Exception) { return "HTTP " + (int)r.StatusCode; }
    }

    private static void Upload(bool force)
    {
        string tok = Token();
        if (tok.Length == 0 || busy) return;
        string payload = Payload();
        string h = Hash(payload);
        Dictionary<string, object> st = State();
        if (!force && AppEnv.GetString(st, "hash") == h) return;
        busy = true;
        try
        {
            string url = "https://api.github.com/repos/" + Repo + "/contents/shared/spots_" + ShareId() + ".json";
            string sha = "";
            try
            {
                using (WebResponse res = Gh("GET", url, tok).GetResponse())
                using (StreamReader sr = new StreamReader(res.GetResponseStream()))
                    sha = AppEnv.GetString(AppEnv.Json.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>, "sha");
            }
            catch (WebException ex)
            {
                HttpWebResponse r = ex.Response as HttpWebResponse;
                if (r == null || r.StatusCode != HttpStatusCode.NotFound) throw new Exception(ErrorText(ex));
            }
            Dictionary<string, object> body = AppEnv.Obj("message", "Companion spots", "content", System.Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)));
            if (sha.Length > 0) body["sha"] = sha;
            byte[] data = Encoding.UTF8.GetBytes(AppEnv.Json.Serialize(body));
            HttpWebRequest put = Gh("PUT", url, tok);
            put.ContentType = "application/json";
            put.ContentLength = data.Length;
            using (Stream s = put.GetRequestStream()) s.Write(data, 0, data.Length);
            try { using (put.GetResponse()) { } }
            catch (WebException ex) { throw new Exception(ErrorText(ex)); }
            st["hash"] = h;
            st["last"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            if (!st.ContainsKey("imgs")) st["imgs"] = new Dictionary<string, object>();
            AppEnv.WriteJsonFile(StateFile, st);
            LastError = "";
        }
        catch (Exception ex) { LastError = ex.Message; AppEnv.Log("Teilen", ex.Message); }
        finally { busy = false; }
    }

    public static void Fetch(bool force)
    {
        if (!force && DateTime.UtcNow.Subtract(lastFetch).TotalMinutes < 30) return;
        lastFetch = DateTime.UtcNow;
        List<Dictionary<string, object>> raws = new List<Dictionary<string, object>>();
        string own = "spots_" + ShareId() + ".json";
        IList files;
        try { files = AppEnv.Json.DeserializeObject(Http.GetString("https://api.github.com/repos/" + Repo + "/contents/shared", 20000)) as IList; }
        catch (Exception ex) { AppEnv.Log("Teilen", "Liste: " + ex.Message); return; }
        if (files == null) return;
        Dictionary<string, string> methods = new Dictionary<string, string> { { "bottom", "Bottom Fishing" }, { "float", "Float Fishing" }, { "spin", "Spinning" }, { "marine", "Marine Fishing" }, { "trolling", "Trolling" } };
        foreach (object fo in files)
        {
            Dictionary<string, object> f = fo as Dictionary<string, object>;
            string name = AppEnv.GetString(f, "name");
            if (!Regex.IsMatch(name, "^spots_[0-9a-f]+\\.json$") || name == own) continue;
            Dictionary<string, object> d;
            try { d = AppEnv.Json.DeserializeObject(Http.GetString("https://raw.githubusercontent.com/" + Repo + "/main/shared/" + name, 20000)) as Dictionary<string, object>; }
            catch (Exception ex) { AppEnv.Log("Teilen", name + ": " + ex.Message); continue; }
            foreach (object so in AppEnv.GetList(d, "spots"))
            {
                Dictionary<string, object> sp = so as Dictionary<string, object>;
                if (sp == null) continue;
                IList bl = AppEnv.GetList(sp, "baits");
                string last = AppEnv.GetString(sp, "last");
                string method;
                methods.TryGetValue(AppEnv.GetString(sp, "tech"), out method);
                raws.Add(AppEnv.Obj(
                    "id", "cp-" + AppEnv.GetString(sp, "id"), "lake", AppEnv.GetString(sp, "lake"),
                    "x", AppEnv.GetDouble(sp, "x", -1), "y", AppEnv.GetDouble(sp, "y", -1),
                    "fish", AppEnv.GetList(sp, "fish").Cast<object>().Select(x => AppEnv.GetString(x as IDictionary, "f")).Where(x => x.Length > 0).ToArray(),
                    "clip", sp.ContainsKey("clip") ? sp["clip"] : null, "depth", AppEnv.GetString(sp, "depth"), "method", method ?? "",
                    "baitDetail", bl.Count > 0 && bl[0] != null ? bl[0].ToString() : "", "dip", AppEnv.GetString(sp, "dip"),
                    "pva", AppEnv.GetString(sp, "pva"), "groundbait", AppEnv.GetString(sp, "groundbait"),
                    "posted", last.Length > 0 ? last + "T12:00:00Z" : "", "url", "", "src", "companion", "dir", AppEnv.GetString(sp, "dir"),
                    "catches", AppEnv.GetDouble(sp, "catches", 0)));
            }
        }
        Community.SetShared(raws);
        AppEnv.Log("Teilen", raws.Count + " geteilte Spots geladen");
    }
}
