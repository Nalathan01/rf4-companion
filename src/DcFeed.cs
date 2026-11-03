using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal static class DcFeed
{
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
            if (r == null || Community.Has(AppEnv.GetString(r, "id"))) continue;
            string[] imgs = AppEnv.GetList(r, "images").Cast<object>().Where(x => x != null).Select(x => x.ToString()).ToArray();
            r.Remove("images");
            if (AppEnv.GetString(r, "baitDetail").Length == 0 && ReadImages(r, imgs)) read++;
            raws.Add(r);
        }
        int added = Community.AddDirect(raws);
        AppEnv.Log("Discord", raws.Count + " Meldungen, neu: " + added + ", Köder aus Bild: " + read);
        return added;
    }

    public static bool ReadImages(Dictionary<string, object> r, string[] imgs)
    {
        for (int i = 0; i < imgs.Length && i < 6; i++)
        {
            List<string> baits;
            string used;
            try
            {
                byte[] b = Http.GetBytes(imgs[i], 30000);
                if (b == null || !ImageOcr.ReadSetup(b, out baits, out used)) continue;
            }
            catch (Exception ex) { AppEnv.Log("Discord", "Bild: " + ex.Message); continue; }
            string[] hook = baits.Where(x => !GameData.DipKeys.Contains(x)).ToArray();
            string[] dip = baits.Where(GameData.DipKeys.Contains).ToArray();
            if (hook.Length == 0) continue;
            r["baitDetail"] = string.Join(" + ", hook);
            if (dip.Length > 0) r["dip"] = string.Join(" + ", dip);
            r["mimg"] = true;
            r["ms"] = true;
            return true;
        }
        return false;
    }
}
