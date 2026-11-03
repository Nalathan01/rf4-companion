using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

internal static class DcFeed
{
    private const int Version = 2;
    public const string ImgBase = "https://raw.githubusercontent.com/Nalathan01/rf4-companion-data/dcimg/";

    public static int Load(string dir)
    {
        string f = string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, "dc_feed.json");
        if (f.Length == 0 || !File.Exists(f)) return 0;
        Dictionary<string, object> d = AppEnv.ReadJsonFile(f) as Dictionary<string, object>;
        List<Dictionary<string, object>> raws = new List<Dictionary<string, object>>();
        int read = 0;
        foreach (object o in AppEnv.GetList(d, "reports"))
        {
            Dictionary<string, object> r = o as Dictionary<string, object>;
            if (r == null) continue;
            Report old = Community.Get(AppEnv.GetString(r, "id"));
            if (old != null && AppEnv.GetDouble(old.Raw, "dv", 0) >= Version) continue;
            r["dv"] = Version;
            string[] imgs = AppEnv.GetList(r, "images").Cast<object>().Where(x => x != null).Select(x => x.ToString()).ToArray();
            r.Remove("images");
            if (ReadImages(r, imgs)) read++;
            raws.Add(r);
        }
        int added = Community.ApplyMirror(raws, DateTime.MinValue);
        AppEnv.Log("Discord", raws.Count + " Meldungen, neu: " + added + ", Köder aus Bild: " + read);
        return added;
    }

    public static bool ReadImages(Dictionary<string, object> r, string[] imgs)
    {
        string imgDir = Environment.GetEnvironmentVariable("DC_IMG_DIR");
        string id = AppEnv.GetString(r, "id");
        string name = id.Substring(id.LastIndexOf('/') + 1);
        bool baits = AppEnv.GetString(r, "baitDetail").Length > 0, gotBaits = false;
        Dictionary<string, int> count = new Dictionary<string, int>(StringComparer.Ordinal);
        int maxW = 0;
        double deg = -1;
        List<object> urls = new List<object>();
        for (int i = 0; i < imgs.Length && i < 6; i++)
        {
            byte[] b;
            try { b = Http.GetBytes(imgs[i], 30000); }
            catch (Exception ex) { AppEnv.Log("Discord", "Bild: " + ex.Message); continue; }
            if (b == null) continue;
            try
            {
                List<string> found;
                string used;
                if (!baits && ImageOcr.ReadSetup(b, out found, out used))
                {
                    string[] hook = found.Where(x => !GameData.DipKeys.Contains(x)).ToArray();
                    string[] dip = found.Where(GameData.DipKeys.Contains).ToArray();
                    if (hook.Length > 0)
                    {
                        r["baitDetail"] = string.Join(" + ", hook);
                        if (dip.Length > 0) r["dip"] = string.Join(" + ", dip);
                        r["mimg"] = true;
                        r["ms"] = true;
                        baits = gotBaits = true;
                        continue;
                    }
                }
                foreach (TrackItem it in Keepnet(b))
                {
                    int n;
                    count.TryGetValue(it.Fish, out n);
                    count[it.Fish] = n + 1;
                    if (it.Weight != null && it.Weight.Value > maxW) maxW = it.Weight.Value;
                }
                if (deg < 0)
                {
                    DirResult dr = DirDetect.Detect(b);
                    if (dr.Ok && dr.Deg >= 0) deg = dr.Deg;
                }
            }
            catch (Exception ex) { AppEnv.Log("Discord", "Auswertung: " + ex.Message); }
            if (!string.IsNullOrEmpty(imgDir))
            {
                string file = name + "_" + i + ".jpg";
                try { SaveJpg(b, Path.Combine(imgDir, file)); urls.Add(ImgBase + file); }
                catch (Exception ex) { AppEnv.Log("Discord", "Bild speichern: " + ex.Message); }
            }
        }
        if (count.Count > 0)
        {
            List<object> fish = count.OrderByDescending(kv => kv.Value).Select(kv => (object)kv.Key).ToList();
            foreach (object t in AppEnv.GetList(r, "fish")) if (t != null && !fish.Contains(t.ToString())) fish.Add(t.ToString());
            r["fish"] = fish.ToArray();
        }
        if (maxW > 0) r["weight"] = maxW / 1000.0;
        if (deg >= 0) r["mdeg"] = deg;
        if (urls.Count > 0) r["imgs"] = urls.ToArray();
        return gotBaits;
    }

    private static List<TrackItem> Keepnet(byte[] b)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "rf4dc_" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(tmp, b);
        try
        {
            foreach (string tag in new string[] { "de-DE", "en-US", "en-GB", "ru" })
            {
                if (!ImageOcr.Available(tag)) continue;
                string code = TrackerParse.Code(tag);
                OcrRead r0 = TrackerOcr.Read(tmp, tag, RectangleF.Empty, 1, false);
                string[] lines = r0.Lines();
                if (lines.Length == 0) continue;
                TrackRes res = TrackerParse.Parse(lines, r0.Boxes, code);
                if (res.Kind != "keepnet") continue;
                OcrRead r2 = TrackerOcr.Read(tmp, tag, new RectangleF(0.14f, 0.11f, 0.845f, 0.86f), 100, false);
                List<TrackItem> items = TrackerParse.Cards(r2.Boxes, code, DateTime.Now, new Dictionary<TrackItem, DateTime>());
                if (items.Count == 0) items = TrackerParse.Merge(res.Items, TrackerParse.KeepnetItems(r2.Lines(), r2.Boxes, TrackerParse.Lists(code)));
                return items.Where(i => i.Fish != null && !Impossible(i.Fish, i.Weight)).ToList();
            }
        }
        finally { try { File.Delete(tmp); } catch (Exception) { } }
        return new List<TrackItem>();
    }

    private static bool Impossible(string fish, int? w)
    {
        int[] t;
        return w != null && GameData.Trophies.TryGetValue(fish, out t) && t[1] > 0 && w.Value > t[1] * 1.6;
    }

    private static void SaveJpg(byte[] b, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (MemoryStream ms = new MemoryStream(b))
        using (Image src = Image.FromStream(ms))
        {
            int x0 = (int)(src.Width * 0.14);
            int w = src.Width-x0, h = src.Height;
            double s = Math.Min(1.0, 1280.0 / w);
            using (Bitmap dst = new Bitmap((int)(w * s), (int)(h * s)))
            {
                using (Graphics g = Graphics.FromImage(dst))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height), new Rectangle(x0, 0, w, h), GraphicsUnit.Pixel);
                }
                ImageCodecInfo jpg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using (EncoderParameters ep = new EncoderParameters(1))
                {
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
                    dst.Save(path, jpg, ep);
                }
            }
        }
    }
}
