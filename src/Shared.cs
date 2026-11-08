using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private const string Repo = "Nalathan01/rf4-companion-data";
    private const string Folder = "shared/admin";
    private const string Raw = "https://raw.githubusercontent.com/" + Repo + "/main/" + Folder + "/";
    private const string Author = "Nalathan (Admin)";
    private const string Country = "DE";
    private static DateTime lastFetch = DateTime.MinValue;
    private static readonly object gate = new object();

    public static void Fetch(bool force)
    {
        if (!Features.Log) return;
        if (!force && DateTime.UtcNow.Subtract(lastFetch).TotalMinutes < 15) return;
        lastFetch = DateTime.UtcNow;
        try
        {
            string json = Http.GetString(Raw + "spots.json?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture), 30000);
            Dictionary<string, object> d = AppEnv.Json.DeserializeObject(json) as Dictionary<string, object>;
            if (d == null) return;
            List<Dictionary<string, object>> raws = AppEnv.GetList(d, "spots").Cast<object>().Select(o => o as Dictionary<string, object>).Where(o => o != null && AppEnv.GetString(o, "id").StartsWith("adm:", StringComparison.Ordinal)).ToList();
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

#if PRIVATE
    public static bool CanShare { get { return true; } }

    public static object Set(string spotId, bool on, string lang)
    {
        lock (gate)
        {
            Dictionary<string, object> spot = Fangbuch.SpotById(spotId);
            if (spot == null) throw new AppError("shareNoSpot", null);
            string token = Token();
            string sha;
            Dictionary<string, object> file = ReadFile(token, Folder + "/spots.json", out sha);
            List<object> list = AppEnv.GetList(file, "spots").Cast<object>().Where(o => AppEnv.GetString(o as Dictionary<string, object>, "id") != "adm:" + spotId).ToList();
            if (on) list.Insert(0, Build(spot, token));
            Dictionary<string, object> outFile = AppEnv.Obj("v", 1, "author", Author, "cc", Country, "updated", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), "spots", list.ToArray());
            Put(token, Folder + "/spots.json", Encoding.UTF8.GetBytes(AppEnv.Json.Serialize(outFile)), sha, (on ? "Share spot " : "Unshare spot ") + spotId);
            Fangbuch.SetSpotValue(spotId, "shared", on ? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) : null);
            lastFetch = DateTime.MinValue;
            Fetch(true);
            return AppEnv.Obj("ok", true, "shared", on);
        }
    }

    public static void Refresh(string spotId)
    {
        Dictionary<string, object> spot = Fangbuch.SpotById(spotId);
        if (spot == null || AppEnv.GetString(spot, "shared").Length == 0) return;
        try { Set(spotId, true, "en"); }
        catch (Exception ex) { AppEnv.Log("Geteilt", "Aktualisieren: " + ex.Message); }
    }

    private static Dictionary<string, object> Build(Dictionary<string, object> s, string token)
    {
        string id = AppEnv.GetString(s, "id");
        Dictionary<string, object> r = new Dictionary<string, object>();
        r["id"] = "adm:" + id;
        r["author"] = Author;
        r["cc"] = Country;
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
        string first = AppEnv.GetString(s, "shared");
        r["posted"] = first.Length > 0 ? first : DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        List<object> urls = new List<object>();
        Dictionary<string, object> imgs = s.ContainsKey("images") ? s["images"] as Dictionary<string, object> : null;
        if (imgs != null)
            foreach (string kind in new string[] { "spot", "net", "setup", "feed", "map", "other" })
            {
                object f;
                if (!imgs.TryGetValue(kind, out f) || f == null) continue;
                byte[] data;
                string type;
                if (!Fangbuch.ImageFile(f.ToString(), out data, out type)) continue;
                string name = id + "_" + kind + ".jpg";
                string isha;
                ReadFile(token, Folder + "/img/" + name, out isha);
                Put(token, Folder + "/img/" + name, Jpg(data), isha, "Spot image " + name);
                urls.Add(Raw + "img/" + name);
            }
        if (urls.Count > 0) r["imgs"] = urls.ToArray();
        return r;
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

    private static string Token()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("gh", "auth token");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                string t = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(15000);
                if (t.Length > 0) return t;
            }
        }
        catch (Exception) { }
        throw new AppError("shareNoToken", null);
    }

    private static HttpWebRequest Gh(string token, string path, string method)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/contents/" + path);
        req.Method = method;
        req.UserAgent = "RF4Companion";
        req.Accept = "application/vnd.github+json";
        req.Headers["Authorization"] = "Bearer " + token;
        req.Timeout = 60000;
        return req;
    }

    private static Dictionary<string, object> ReadFile(string token, string path, out string sha)
    {
        sha = null;
        try
        {
            using (WebResponse res = Gh(token, path + "?ref=main", "GET").GetResponse())
            using (StreamReader sr = new StreamReader(res.GetResponseStream()))
            {
                Dictionary<string, object> meta = AppEnv.Json.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
                sha = AppEnv.GetString(meta, "sha");
                string content = AppEnv.GetString(meta, "content").Replace("\n", "");
                if (content.Length == 0 || !path.EndsWith(".json", StringComparison.Ordinal)) return new Dictionary<string, object>();
                return AppEnv.Json.DeserializeObject(Encoding.UTF8.GetString(Convert.FromBase64String(content))) as Dictionary<string, object> ?? new Dictionary<string, object>();
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse res = ex.Response as HttpWebResponse;
            if (res != null && res.StatusCode == HttpStatusCode.NotFound) return new Dictionary<string, object>();
            throw new AppError("shareFailed", ex.Message);
        }
    }

    private static void Put(string token, string path, byte[] data, string sha, string message)
    {
        Dictionary<string, object> who = AppEnv.Obj("name", "Nalathan", "email", "194643374+Nalathan01@users.noreply.github.com");
        Dictionary<string, object> body = AppEnv.Obj("message", message, "content", Convert.ToBase64String(data), "branch", "main", "committer", who, "author", who);
        if (!string.IsNullOrEmpty(sha)) body["sha"] = sha;
        byte[] b = Encoding.UTF8.GetBytes(AppEnv.Json.Serialize(body));
        HttpWebRequest req = Gh(token, path, "PUT");
        req.ContentType = "application/json";
        req.ContentLength = b.Length;
        try
        {
            using (Stream st = req.GetRequestStream()) st.Write(b, 0, b.Length);
            using (WebResponse res = req.GetResponse()) { }
        }
        catch (WebException ex) { throw new AppError("shareFailed", ex.Message); }
    }
#else
    public static bool CanShare { get { return false; } }

    public static object Set(string spotId, bool on, string lang)
    {
        throw new AppError("shareNoToken", null);
    }

    public static void Refresh(string spotId) { }
#endif
}
