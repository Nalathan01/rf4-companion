using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

internal static class TgDirect
{
    private static readonly string[] Channels = { "klevalkarr4" };
    private static readonly HashSet<string> Covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rr4pepper", "rr4peppr", "rf4map", "rf4places", "rf4crazyft", "russianfishrr4", "pp4farmtrof" };
    private static readonly Regex Block = new Regex(@"data-post=""([A-Za-z0-9_]+)/(\d+)""(.*?)(?=data-post=""|$)", RegexOptions.Singleline);
    private static readonly Regex TextDiv = new Regex(@"<div class=""tgme_widget_message_text[^""]*""[^>]*>(.*?)</div>", RegexOptions.Singleline);
    private static readonly Regex Time = new Regex(@"<time datetime=""([^""]+)""");
    private static readonly Regex Tag = new Regex(@"#([а-яёА-ЯЁ_]+)(?:@([A-Za-z0-9_]+))?");
    private static readonly Regex Coord = new Regex(@"(?<!\d)(\d{1,3})\s*:\s*(\d{1,3})(?!\d)");
    private static readonly Regex Clip = new Regex(@"(?i)клипс\w*\D{0,4}(\d{1,3}(?:[.,]\d)?)");
    private static DateTime last = DateTime.MinValue;
    private static volatile bool running;

    public static void Sync(bool force)
    {
        if (running || (!force && DateTime.UtcNow.Subtract(last).TotalMinutes < 30)) return;
        running = true;
        last = DateTime.UtcNow;
        try
        {
            List<Dictionary<string, object>> raws = new List<Dictionary<string, object>>();
            foreach (string ch in Channels) raws.AddRange(Read(ch));
            int added = Community.AddDirect(raws);
            AppEnv.Log("Telegram", raws.Count + " Meldungen gelesen, neu: " + added);
        }
        catch (Exception ex) { AppEnv.Log("Telegram", ex.Message); }
        finally { running = false; }
    }

    private static List<Dictionary<string, object>> Read(string ch)
    {
        List<Dictionary<string, object>> outL = new List<Dictionary<string, object>>();
        DateTime cutoff = DateTime.UtcNow.AddDays(-8);
        long before = 0;
        for (int page = 0; page < 15; page++)
        {
            string html = Http.GetString("https://t.me/s/" + ch + (before > 0 ? "?before=" + before.ToString(CultureInfo.InvariantCulture) : ""), 20000);
            long minId = long.MaxValue;
            bool old = false, known = false;
            foreach (Match m in Block.Matches(html))
            {
                long id = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                minId = Math.Min(minId, id);
                string body = m.Groups[3].Value;
                Match tm = Time.Match(body);
                DateTimeOffset when;
                if (!tm.Success || !DateTimeOffset.TryParse(tm.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out when)) continue;
                if (when.UtcDateTime < cutoff) { old = true; continue; }
                Match td = TextDiv.Match(body);
                if (!td.Success) continue;
                string text = WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(td.Groups[1].Value, @"<br\s*/?>", "\n"), "<[^>]+>", "")).Trim();
                Dictionary<string, object> r = Parse(m.Groups[1].Value, id, when.UtcDateTime, text);
                if (r == null) continue;
                if (Community.Has((string)r["id"])) known = true;
                outL.Add(r);
            }
            if (old || known || minId == long.MaxValue || minId <= 1) break;
            before = minId;
            System.Threading.Thread.Sleep(1000);
        }
        return outL;
    }

    private static Dictionary<string, object> Parse(string ch, long id, DateTime posted, string text)
    {
        Match tag = Tag.Match(text);
        if (!tag.Success) return null;
        if (tag.Groups[2].Success && Covered.Contains(tag.Groups[2].Value)) return null;
        string lake = LakeFromTag(tag.Groups[1].Value.ToLowerInvariant());
        if (lake == null) return null;
        Match c = Coord.Match(text);
        if (!c.Success) return null;
        Dictionary<string, object> r = new Dictionary<string, object>();
        r["id"] = "tgd:" + ch + "/" + id.ToString(CultureInfo.InvariantCulture);
        r["lake"] = lake;
        r["x"] = int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture);
        r["y"] = int.Parse(c.Groups[2].Value, CultureInfo.InvariantCulture);
        r["posted"] = posted.ToString("o", CultureInfo.InvariantCulture);
        r["url"] = "https://t.me/" + ch + "/" + id.ToString(CultureInfo.InvariantCulture);
        r["fish"] = new object[0];
        r["bait"] = new object[0];
        Match cl = Clip.Match(text);
        r["clip"] = cl.Success ? (object)double.Parse(cl.Groups[1].Value.Replace(",", "."), CultureInfo.InvariantCulture) : null;
        r["type"] = "spot";
        r["src"] = "telegram";
        return r;
    }

    private static string LakeFromTag(string tag)
    {
        if (tag.StartsWith("острог")) return "old_burg_lake";
        if (tag.StartsWith("ладог")) return "ladoga_lake";
        string best = null;
        double bestScore = 0;
        foreach (Lake l in GameData.Lakes)
            foreach (string w in Regex.Split(l.Name("ru").ToLowerInvariant(), @"[\s.]+").Where(x => x.Length >= 3 && x != "оз" && x != "озеро" && x != "река"))
            {
                if (w == tag) return l.Id;
                double sc;
                if (Fuzzy.Best(tag, new string[] { Fuzzy.Norm(w) }, out sc) == 0 && sc >= 0.8 && sc > bestScore) { bestScore = sc; best = l.Id; }
            }
        return best;
    }
}
