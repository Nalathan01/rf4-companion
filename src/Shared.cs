using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

internal static class Shared
{
    private const string Raw = "https://raw.githubusercontent.com/Nalathan01/rf4-companion-data/main/shared/admin/";
    private const string Worker = "https://rf4-share.rf4-companion.workers.dev/";
    private static DateTime lastFetch = DateTime.MinValue;
    private static readonly object gate = new object();
    private static string checkedKey;
    private static Dictionary<string, object> checkedMe;

    private static Dictionary<string, int[]> votes = new Dictionary<string, int[]>(StringComparer.Ordinal);
    private static HashSet<string> hidden = new HashSet<string>(StringComparer.Ordinal);

    public static int MetaVersion;

    public static string Rid(string reportId)
    {
        if ((reportId ?? "").StartsWith("adm:", StringComparison.Ordinal)) return reportId;
        string s = System.Text.RegularExpressions.Regex.Replace((reportId ?? "").ToLowerInvariant(), "[^0-9a-z_:\\-]", "-");
        return "r:" + (s.Length > 90 ? s.Substring(0, 90) : s);
    }

    public static bool HiddenReport(string reportId)
    {
        return hidden.Count > 0 && hidden.Contains(Rid(reportId));
    }

    public static int[] Votes(string id)
    {
        int[] v;
        return votes.TryGetValue(id, out v) ? v : new int[] { 0, 0 };
    }

    private static void FetchMeta()
    {
        try
        {
            Dictionary<string, object> d = AppEnv.Json.DeserializeObject(Http.GetString(Worker + "meta?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture), 30000)) as Dictionary<string, object>;
            if (d == null || !AppEnv.GetBool(d, "ok")) return;
            Dictionary<string, int[]> v = new Dictionary<string, int[]>(StringComparer.Ordinal);
            Dictionary<string, object> raw = d.ContainsKey("votes") ? d["votes"] as Dictionary<string, object> : null;
            if (raw != null)
                foreach (KeyValuePair<string, object> kv in raw)
                {
                    object[] a = kv.Value as object[];
                    if (a != null && a.Length == 2) v[kv.Key] = new int[] { Convert.ToInt32(a[0], CultureInfo.InvariantCulture), Convert.ToInt32(a[1], CultureInfo.InvariantCulture) };
                }
            HashSet<string> h = new HashSet<string>(AppEnv.GetList(d, "hidden").Cast<object>().Select(o => (o ?? "").ToString()), StringComparer.Ordinal);
            votes = v;
            if (!h.SetEquals(hidden)) { hidden = h; MetaVersion++; }
        }
        catch (Exception ex) { AppEnv.Log("Geteilt", "Bewertungen: " + ex.Message); }
    }

    public static void Fetch(bool force)
    {
        if (!Features.Log) return;
        if (!force && DateTime.UtcNow.Subtract(lastFetch).TotalMinutes < 15) return;
        lastFetch = DateTime.UtcNow;
        FetchMeta();
        try
        {
            string json = Http.GetString(Raw + "spots.json?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture), 30000);
            Dictionary<string, object> d = AppEnv.Json.DeserializeObject(json) as Dictionary<string, object>;
            if (d == null) return;
            List<Dictionary<string, object>> raws = AppEnv.GetList(d, "spots").Cast<object>().Select(o => o as Dictionary<string, object>).Where(o => o != null && AppEnv.GetString(o, "id").StartsWith("adm:", StringComparison.Ordinal) && !AppEnv.GetBool(o, "hidden")).ToList();
            int n = Community.ReplacePrefix("adm:", raws);
            if (n > 0) AppEnv.Log("Geteilt", raws.Count + " Spots, geaendert: " + n);
        }
        catch (WebException ex)
        {
            HttpWebResponse res = ex.Response as HttpWebResponse;
            if (res != null && res.StatusCode == HttpStatusCode.NotFound) Community.ReplacePrefix("adm:", new List<Dictionary<string, object>>());
            else AppEnv.Log("Geteilt", ex.Message);
        }
        catch (Exception ex) { AppEnv.Log("Geteilt", ex.Message); }
    }

    private static string Key
    {
        get { return (UserStore.Get("shareKey") ?? "").ToString().Trim(); }
    }

    public static bool CanShare
    {
        get
        {
            string key = Key;
            if (!Features.Log || key.Length == 0) return false;
            lock (gate) if (checkedKey == key) return checkedMe != null;
            lock (gate) if (failedKey == key && DateTime.UtcNow.Subtract(failedAt).TotalMinutes < 10) return false;
            try { if (Me(key) != null) return true; } catch (Exception) { }
            lock (gate) { failedKey = key; failedAt = DateTime.UtcNow; }
            return false;
        }
    }

    private static string failedKey;
    private static DateTime failedAt;

    private static string Code(string key)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Z0-9]{4}(-[A-Z0-9]{4}){4}$")) return "";
        using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key)), 0, 16).Replace("-", "").ToLowerInvariant();
    }

    private static object Status(string key, Dictionary<string, object> me)
    {
        string code = Code(key);
        return AppEnv.Obj("key", key, "name", me != null ? AppEnv.GetString(me, "name") : "", "cc", me != null ? AppEnv.GetString(me, "cc") : "", "code", code, "admin", me != null && AppEnv.GetBool(me, "admin"));
    }

    public static object KeyStatus()
    {
        string key = Key;
        if (key.Length == 0) return AppEnv.Obj("key", "");
        return Status(key, Me(key));
    }

    public static object NewKey()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        byte[] b = new byte[20];
        using (System.Security.Cryptography.RandomNumberGenerator rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < b.Length; i++) { if (i > 0 && i % 4 == 0) sb.Append('-'); sb.Append(chars[b[i] % chars.Length]); }
        UserStore.Set("shareKey", sb.ToString());
        lock (gate) { checkedKey = null; checkedMe = null; failedKey = null; }
        return Status(sb.ToString(), null);
    }

    public static object Register(string name, string cc)
    {
        string key = Key;
        if (Code(key).Length == 0) { NewKey(); key = Key; }
        Call("register", key, AppEnv.Obj("name", name ?? "", "cc", cc ?? ""));
        lock (gate) { checkedKey = null; checkedMe = null; failedKey = null; }
        return Status(key, Me(key));
    }

    public static bool IsAdmin
    {
        get
        {
            string key = Key;
            if (key.Length == 0 || !CanShare) return false;
            Dictionary<string, object> me = Me(key);
            return me != null && AppEnv.GetBool(me, "admin");
        }
    }

    public static object Block(string reportId)
    {
        string key = Key;
        if (key.Length == 0) throw new AppError("shareBadKey", null);
        Dictionary<string, object> r = AppEnv.Json.DeserializeObject(Call("block", key, AppEnv.Obj("id", reportId ?? ""))) as Dictionary<string, object>;
        lastFetch = DateTime.MinValue;
        Fetch(true);
        return r;
    }

    public static object Report(string reportId)
    {
        return AppEnv.Json.DeserializeObject(Call("report", Key, AppEnv.Obj("id", reportId ?? "")));
    }

    public static object Rate(string reportId, int v)
    {
        object r = AppEnv.Json.DeserializeObject(Call("rate", Key, AppEnv.Obj("id", reportId ?? "", "v", v)));
        lastFetch = DateTime.MinValue;
        Fetch(true);
        return r;
    }

    public static object Remove(string reportId)
    {
        object r = AppEnv.Json.DeserializeObject(Call("remove", Key, AppEnv.Obj("id", reportId ?? "")));
        lastFetch = DateTime.MinValue;
        Fetch(true);
        return r;
    }

    public static object Restore(string reportId)
    {
        object r = AppEnv.Json.DeserializeObject(Call("restore", Key, AppEnv.Obj("id", reportId ?? "")));
        lastFetch = DateTime.MinValue;
        Fetch(true);
        return r;
    }

    public static object Reports()
    {
        return AppEnv.Json.DeserializeObject(Call("reports", Key, null));
    }

    public static object SetKey(string key)
    {
        key = (key ?? "").Trim().ToUpperInvariant();
        if (key.Length == 0) { UserStore.Set("shareKey", ""); return AppEnv.Obj("key", ""); }
        lock (gate) { checkedKey = null; checkedMe = null; failedKey = null; }
        Dictionary<string, object> me = Me(key);
        if (me == null && Code(key).Length == 0) throw new AppError("shareBadKey", null);
        UserStore.Set("shareKey", key);
        return Status(key, me);
    }

    private static Dictionary<string, object> Me(string key)
    {
        lock (gate) if (checkedKey == key && checkedMe != null) return checkedMe;
        try
        {
            Dictionary<string, object> me = AppEnv.Json.DeserializeObject(Call("me", key, null)) as Dictionary<string, object>;
            if (me == null || !AppEnv.GetBool(me, "ok")) return null;
            lock (gate) { checkedKey = key; checkedMe = me; }
            return me;
        }
        catch (AppError e) { if (e.Code == "shareBadKey") return null; throw; }
    }

    private static string Call(string path, string key, object body)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Worker + path);
        req.Method = body == null ? "GET" : "POST";
        req.UserAgent = "RF4Companion";
        req.Headers["x-key"] = key;
        req.Timeout = 120000;
        req.ReadWriteTimeout = 120000;
        if (body != null)
        {
            byte[] b = Encoding.UTF8.GetBytes(AppEnv.Json.Serialize(body));
            req.ContentType = "application/json";
            req.ContentLength = b.Length;
            using (Stream st = req.GetRequestStream()) st.Write(b, 0, b.Length);
        }
        try
        {
            using (WebResponse res = req.GetResponse())
            using (StreamReader sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
        }
        catch (WebException ex)
        {
            HttpWebResponse res = ex.Response as HttpWebResponse;
            string code = "shareFailed";
            if (res != null)
                try
                {
                    using (StreamReader sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                        code = AppEnv.GetString(AppEnv.Json.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>, "error");
                }
                catch (Exception) { }
            throw new AppError(string.IsNullOrEmpty(code) ? "shareFailed" : code, ex.Message);
        }
    }

    public static object Set(string spotId, bool on, string lang)
    {
        string key = Key;
        if (key.Length == 0) throw new AppError("shareBadKey", null);
        lock (gate)
        {
            Dictionary<string, object> spot = Fangbuch.SpotById(spotId);
            if (spot == null) throw new AppError("shareNoSpot", null);
            if (on) Call("share", key, AppEnv.Obj("id", spotId, "spot", Build(spot), "images", Images(spot)));
            else Call("unshare", key, AppEnv.Obj("id", spotId));
            Fangbuch.SetSpotValue(spotId, "shared", on ? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) : null);
            lastFetch = DateTime.MinValue;
            Fetch(true);
            return AppEnv.Obj("ok", true, "shared", on);
        }
    }

    public static void Refresh(string spotId)
    {
        Dictionary<string, object> spot = Fangbuch.SpotById(spotId);
        if (spot == null || AppEnv.GetString(spot, "shared").Length == 0 || Key.Length == 0) return;
        try { Set(spotId, true, "en"); }
        catch (Exception ex) { AppEnv.Log("Geteilt", "Aktualisieren: " + ex.Message); }
    }

    private static Dictionary<string, object> Build(Dictionary<string, object> s)
    {
        Dictionary<string, object> r = new Dictionary<string, object>();
        r["lake"] = AppEnv.GetString(s, "lake");
        int[] xy = Fangbuch.SpotPosition(s);
        if (xy != null) { r["x"] = xy[0]; r["y"] = xy[1]; }
        r["fish"] = AppEnv.GetString(s, "fish").Split(',').Select(f => GameData.ToKey(f.Trim())).Where(f => f.Length > 0).Distinct().Cast<object>().ToArray();
        string baits = string.Join(" + ", new string[] { "bait", "bait2" }.Select(k => GameData.ToKey(AppEnv.GetString(s, k))).Where(v => v.Length > 0).ToArray());
        if (baits.Length > 0) r["baitDetail"] = baits;
        foreach (string k in new string[] { "dip", "rig" }) { string v = GameData.ToKey(AppEnv.GetString(s, k)); if (v.Length > 0) r[k] = v; }
        Dictionary<string, object> rc = Fangbuch.RecipeByName(AppEnv.GetString(s, "pva"));
        if (rc != null)
        {
            List<string> mix = new List<string>();
            if (AppEnv.GetString(rc, "base").Length > 0) mix.Add(AppEnv.GetString(rc, "base"));
            foreach (object o in AppEnv.GetList(rc, "additives")) if (o != null && o.ToString().Trim().Length > 0) mix.Add(o.ToString().Trim());
            if (AppEnv.GetString(rc, "attractant").Length > 0) mix.Add(AppEnv.GetString(rc, "attractant"));
            if (mix.Count > 0) r["groundbait"] = string.Join(" + ", mix.ToArray());
        }
        List<object> rigs = AppEnv.GetList(s, "rigs").Cast<object>().ToList();
        if (rigs.Count > 0 && baits.Length > 0)
        {
            List<object> setups = new List<object> { baits };
            foreach (object o in rigs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                string b = string.Join(" + ", new string[] { "bait", "bait2" }.Select(k => GameData.ToKey(AppEnv.GetString(m, k))).Where(v => v.Length > 0).ToArray());
                if (b.Length > 0) setups.Add(b);
            }
            if (setups.Count > 1) r["setups"] = setups.ToArray();
        }
        foreach (string k in new string[] { "clip", "depth" }) { string v = AppEnv.GetString(s, k); if (v.Length > 0) r[k] = v; }
        if (AppEnv.GetString(s, "tech").Length > 0) r["method"] = AppEnv.GetString(s, "tech");
        if (AppEnv.GetString(s, "temp").Length > 0) r["ptemp"] = AppEnv.GetString(s, "temp");
        if (AppEnv.GetString(s, "notes").Length > 0) { r["text"] = AppEnv.GetString(s, "notes"); r["textLang"] = "de"; }
        return r;
    }

    private static object[] Images(Dictionary<string, object> s)
    {
        List<object> outL = new List<object>();
        Dictionary<string, object> imgs = s.ContainsKey("images") ? s["images"] as Dictionary<string, object> : null;
        if (imgs == null) return outL.ToArray();
        foreach (string kind in new string[] { "spot", "net", "setup", "feed", "map", "other" })
        {
            object f;
            if (!imgs.TryGetValue(kind, out f) || f == null) continue;
            byte[] data;
            string type;
            if (!Fangbuch.ImageFile(f.ToString(), out data, out type)) continue;
            outL.Add(AppEnv.Obj("kind", kind, "jpg", Convert.ToBase64String(Jpg(data))));
        }
        return outL.ToArray();
    }

    private static byte[] Jpg(byte[] b)
    {
        using (MemoryStream ms = new MemoryStream(b))
        using (Image src = Image.FromStream(ms))
        {
            double s = Math.Min(1.0, 1600.0 / src.Width);
            using (Bitmap dst = new Bitmap((int)(src.Width * s), (int)(src.Height * s)))
            {
                using (Graphics g = Graphics.FromImage(dst))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height));
                }
                ImageCodecInfo jpg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using (EncoderParameters ep = new EncoderParameters(1))
                using (MemoryStream outMs = new MemoryStream())
                {
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
                    dst.Save(outMs, jpg, ep);
                    return outMs.ToArray();
                }
            }
        }
    }
}
