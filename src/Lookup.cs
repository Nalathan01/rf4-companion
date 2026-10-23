using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

internal static class Lookup
{
    private static readonly string[] OcrTags = { "de-DE", "en-US", "ru-RU", "pl-PL", "fr-FR", "es-ES", "it-IT", "pt-PT", "nl-NL", "tr-TR", "ro-RO", "ja-JP", "ko-KR", "zh-CN", "zh-TW" };

    private static Dictionary<string, int> OwnBest()
    {
        Dictionary<string, int> best = new Dictionary<string, int>(StringComparer.Ordinal);
        IEnumerable e = UserStore.Get("catches") as IEnumerable;
        if (e != null)
            foreach (object o in e)
            {
                Dictionary<string, object> c = o as Dictionary<string, object>;
                if (c == null) continue;
                string f = AppEnv.GetString(c, "fish");
                int w = (int)AppEnv.GetDouble(c, "weight", 0);
                int b;
                if (f.Length > 0 && w > 0 && (!best.TryGetValue(f, out b) || w > b)) best[f] = w;
            }
        return best;
    }

    public static object Data(string lang)
    {
        Dictionary<string, int> own = OwnBest();
        Func<string, object> fish = f =>
        {
            int[] t;
            GameData.Trophies.TryGetValue(f, out t);
            int ob;
            own.TryGetValue(f, out ob);
            return AppEnv.Obj("key", f, "name", GameData.Translate(f, lang),
                "trophy", t != null && t[0] > 0 ? GameData.FormatWeight(t[0], lang) : "", "rare", t != null && t[1] > 0 ? GameData.FormatWeight(t[1], lang) : "",
                "own", ob > 0 ? GameData.FormatWeight(ob, lang) : "", "ownMark", t == null || ob <= 0 ? 0 : (t[1] > 0 && ob >= t[1] ? 2 : (t[0] > 0 && ob >= t[0] ? 1 : 0)));
        };
        List<object> lakes = new List<object>();
        foreach (Lake l in GameData.Lakes)
        {
            object d;
            string desc = l.Desc != null && (l.Desc.TryGetValue(lang, out d) || l.Desc.TryGetValue("en", out d)) && d != null ? d.ToString() : "";
            lakes.Add(AppEnv.Obj("id", l.Id, "name", l.Name(lang), "desc", desc,
                "fish", l.Fish.OrderBy(f => GameData.Translate(f, lang)).Select(fish).ToArray()));
        }
        List<object> trophies = GameData.Trophies.Keys.OrderBy(f => GameData.Translate(f, lang)).Select(f =>
        {
            Dictionary<string, object> o = (Dictionary<string, object>)fish(f);
            o["lakes"] = GameData.Lakes.Where(l => l.Fish.Contains(f)).Select(l => l.Name(lang)).ToArray();
            return (object)o;
        }).ToList();
        return AppEnv.Obj("lakes", lakes, "trophies", trophies);
    }

    public static object OcrLangs()
    {
        HashSet<string> have = new HashSet<string>(TrackerOcr.Languages().Select(x => x.Split('-')[0].ToLowerInvariant()), StringComparer.Ordinal);
        HashSet<string> haveFull = new HashSet<string>(TrackerOcr.Languages(), StringComparer.OrdinalIgnoreCase);
        return OcrTags.Select(t => (object)AppEnv.Obj("tag", t, "name", new System.Globalization.CultureInfo(t).NativeName,
            "installed", t.StartsWith("zh") ? haveFull.Any(h => h.StartsWith(t == "zh-TW" ? "zh-Hant" : "zh-Hans", StringComparison.OrdinalIgnoreCase)) : have.Contains(t.Split('-')[0].ToLowerInvariant()))).ToArray();
    }

    public static object InstallOcr(string tag)
    {
        if (!OcrTags.Contains(tag) || !Regex.IsMatch(tag, "^[a-z]{2}-[A-Z]{2}$")) throw new AppError("unknown", tag);
        ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"Add-WindowsCapability -Online -Name 'Language.OCR~~~" + tag + "~0.0.1.0'\"");
        psi.Verb = "runas";
        psi.UseShellExecute = true;
        psi.WindowStyle = ProcessWindowStyle.Normal;
        try { Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { throw new AppError("ocrCancelled", null); }
        return AppEnv.Obj("ok", true);
    }
}
