using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

internal static class Fuzzy
{
    public static string Norm(string s)
    {
        StringBuilder sb = new StringBuilder();
        foreach (char ch in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) sb.Append(ch == 'ё' ? 'е' : ch);
        return sb.ToString();
    }

    private static int Lev(string a, string b)
    {
        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i-1] == b[j-1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j-1] + 1, prev[j] + 1), prev[j-1] + cost);
            }
            int[] t = prev; prev = cur; cur = t;
        }
        return prev[b.Length];
    }

    public static byte[] Hist(string norm)
    {
        byte[] h = new byte[32];
        foreach (char ch in norm) { int b = ch & 31; if (h[b] < 255) h[b]++; }
        return h;
    }

    public static int Best(string query, string[] normCandidates, out double score)
    {
        return Best(query, normCandidates, null, 0, out score);
    }

    public static int Best(string query, string[] normCandidates, byte[][] hists, double min, out double score)
    {
        string q = Norm(query);
        score = 0;
        if (q.Length < 2) return -1;
        byte[] hq = hists != null ? Hist(q) : null;
        int best = -1;
        for (int i = 0; i < normCandidates.Length; i++)
        {
            string c = normCandidates[i];
            if (c.Length == 0) continue;
            int mx = Math.Max(c.Length, q.Length), diff = Math.Abs(c.Length-q.Length);
            if (diff > mx / 2) continue;
            double ub = 1.0-(double)diff / mx;
            if (ub <= score || ub < min) continue;
            if (hq != null)
            {
                byte[] hc = hists[i];
                int common = 0;
                for (int b = 0; b < 32; b++) common += Math.Min(hq[b], hc[b]);
                ub = (double)common / mx;
                if (ub <= score || ub < min) continue;
            }
            double sc = 1.0-(double)Lev(q, c) / Math.Max(q.Length, c.Length);
            if (sc > score) { score = sc; best = i; }
        }
        return best;
    }
}

internal sealed class MatchList
{
    public string[] Loc, LocNorm, En;
    private byte[][] hists;

    public byte[][] Hists
    {
        get
        {
            if (hists == null)
            {
                byte[][] h = new byte[LocNorm.Length][];
                for (int i = 0; i < h.Length; i++) h[i] = Fuzzy.Hist(LocNorm[i]);
                hists = h;
            }
            return hists;
        }
    }
}

internal static class PostText
{
    private static readonly object gate = new object();
    private static readonly Dictionary<string, MatchList> lists = new Dictionary<string, MatchList>();
    private static MatchList ruNoDigit, ruDips;
    private static HashSet<string> dipKeys;
    private static readonly Dictionary<string, string> memo = new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly Dictionary<string, Dictionary<string, string>> parsed = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> cleanMemo = new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly string Dash = "[:\\" + (char)45 + (char)0x2013 + (char)0x2014 + "]";

    private static MatchList Items(string lang)
    {
        lock (gate)
        {
            MatchList m;
            if (lists.TryGetValue(lang, out m)) return m;
            List<string[]> rows = GameData.ItemRows();
            int idx = Math.Max(0, Array.IndexOf(GameData.ItemLangs(), lang));
            m = new MatchList();
            m.Loc = new string[rows.Count]; m.En = new string[rows.Count]; m.LocNorm = new string[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                m.En[i] = rows[i][0];
                m.Loc[i] = idx < rows[i].Length && rows[i][idx].Length > 0 ? rows[i][idx] : rows[i][0];
                m.LocNorm[i] = Fuzzy.Norm(m.Loc[i]);
            }
            lists[lang] = m;
            return m;
        }
    }

    private static void EnsureRu()
    {
        if (ruNoDigit != null) return;
        MatchList ru = Items("ru");
        List<string> nl = new List<string>(), ne = new List<string>();
        for (int i = 0; i < ru.Loc.Length; i++) if (!Regex.IsMatch(ru.Loc[i], @"\d")) { nl.Add(ru.Loc[i]); ne.Add(ru.En[i]); }
        List<string> dl = new List<string>(), de = new List<string>();
        HashSet<string> dk = new HashSet<string>(StringComparer.Ordinal);
        foreach (string k in GameData.DipKeys)
        {
            dk.Add(k);
            Dictionary<string, object> e;
            if (!GameData.Names.TryGetValue(k, out e)) continue;
            string r = AppEnv.GetString(e, "ru");
            if (r.Length == 0) continue;
            dl.Add(r); de.Add(k);
            string flav = Regex.Replace(r, @"^[A-Za-z'&.\s]+\s(?=[^\x00-\x7F])", "").Trim();
            if (flav != r) { dl.Add(flav); de.Add(k); }
        }
        ruDips = Make(dl, de);
        dipKeys = dk;
        ruNoDigit = Make(nl, ne);
    }

    private static MatchList Make(List<string> loc, List<string> en)
    {
        MatchList m = new MatchList();
        m.Loc = loc.ToArray(); m.En = en.ToArray(); m.LocNorm = new string[m.Loc.Length];
        for (int i = 0; i < m.Loc.Length; i++) m.LocNorm[i] = Fuzzy.Norm(m.Loc[i]);
        return m;
    }

    private static string Match(string text, MatchList l, double min, out string loc)
    {
        double sc;
        int i = Fuzzy.Best(text, l.LocNorm, l.Hists, min, out sc);
        loc = i >= 0 ? l.Loc[i] : null;
        return i >= 0 && sc >= min ? l.En[i] : null;
    }

    private static List<string> TranslateRu(string text, bool dip, out int total)
    {
        EnsureRu();
        List<string> keys = new List<string>();
        total = 0;
        string clean = Regex.Replace(text, "[«»\"“”]", "").Trim().TrimEnd('.', '!', ';', ' ');
        foreach (string raw in Regex.Split(clean, @"\s*\+\s*|\s*,\s*|\s*;\s*"))
        {
            string part = raw.Trim().TrimEnd('.');
            if (part.Length < 3) continue;
            bool crushed = Regex.IsMatch(part, "(?i)дробл|измельч");
            part = Regex.Replace(part, @"(?i)\(?\s*(дробл|измельч)[а-я]*\.?\s*\)?", "").Trim();
            List<string> variants = new List<string> { part };
            Match mm = Regex.Match(part, @"^(.*?\D)\s*(\d+(?:\s*/\s*\d+)+)$");
            if (mm.Success)
            {
                variants.Clear();
                foreach (string n in Regex.Split(mm.Groups[2].Value, @"\s*/\s*")) variants.Add(mm.Groups[1].Value.Trim() + " " + n);
            }
            foreach (string v in variants)
            {
                total++;
                string mk = (dip ? "d|" : "i|") + v;
                string hit;
                lock (gate)
                {
                    if (!memo.TryGetValue(mk, out hit))
                    {
                        string loc = null;
                        hit = null;
                        if (dip) hit = Match(v, ruDips, 0.8, out loc);
                        if (hit == null) hit = Match(v, Items("ru"), 0.82, out loc);
                        else loc = null;
                        if (hit == null && v.Contains("е")) hit = Match(v.Replace("е", "ё"), Items("ru"), 0.82, out loc);
                        if (hit != null && !Regex.IsMatch(v, @"\d") && loc != null && Regex.IsMatch(loc, @"\d"))
                        {
                            hit = Match(v, ruNoDigit, 0.82, out loc);
                            if (hit == null) hit = Match(v, ruDips, 0.85, out loc);
                        }
                        memo[mk] = hit ?? "";
                    }
                }
                if (string.IsNullOrEmpty(hit)) continue;
                keys.Add(crushed ? hit + " (crushed)" : hit);
            }
        }
        return keys;
    }

    private static bool IsDip(string k)
    {
        foreach (string d in dipKeys) if (d == k || d.EndsWith(" " + k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static Dictionary<string, string> Parse(string txt)
    {
        Dictionary<string, string> o = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(txt)) return o;
        lock (gate) { Dictionary<string, string> c; if (parsed.TryGetValue(txt, out c)) return c; }
        string t = txt.Replace("ё", "е").Replace("Ё", "Е");
        Match m = Regex.Match(t, @"(?i)клипс[а-я]*(?:\s*или\s*глубин[а-я]*)?\s*" + Dash + @"?\s*(\d{1,3})(?:[.,]\d)?\s*(см)?");
        if (m.Success && !m.Groups[2].Success) o["clip"] = m.Groups[1].Value;
        m = Regex.Match(t, @"(?i)глубин[а-я]*\s*" + Dash + @"?\s*(\d+(?:[.,]\d+)?)\s*(см|м)?");
        if (m.Success)
        {
            double val = double.Parse(m.Groups[1].Value.Replace(",", "."), CultureInfo.InvariantCulture);
            string unit = m.Groups[2].Value;
            if (unit == "см" || (unit.Length == 0 && val > 15)) val = val / 100;
            o["depth"] = Math.Round(val, 2).ToString(CultureInfo.InvariantCulture);
            string cl;
            if (o.TryGetValue("clip", out cl) && cl == m.Groups[1].Value) o.Remove("clip");
        }
        m = Regex.Match(t, @"(?i)скорост[а-я]*(?:\s*проводки)?\s*" + Dash + @"?\s*(\d{1,3})");
        if (m.Success) o["reelSpeed"] = m.Groups[1].Value;
        m = Regex.Match(t, @"(?i)температур[а-я]*(?:\s*воды)?\s*" + Dash + @"?\s*(холодн|нормальн|тепл)");
        if (m.Success) { string v = m.Groups[1].Value.ToLowerInvariant(); o["temp"] = v == "холодн" ? "cold" : (v == "тепл" ? "warm" : "normal"); }
        string baitTxt = "", dipTxt = "", gbTxt = "", pvaTxt = "", comment = "";
        foreach (string line in t.Split('\n'))
        {
            string l = Regex.Replace(line, @"^[^\p{L}]+", "").Trim();
            Match lm;
            if ((lm = Regex.Match(l, @"^(?i)(наживк[а-я]*|приманк[а-я]*)\s*" + Dash + @"\s*(.+)$")).Success) { baitTxt = lm.Groups[2].Value; continue; }
            if ((lm = Regex.Match(l, @"^(?i)дип[а-я]*\s*" + Dash + @"\s*(.+)$")).Success) { dipTxt = lm.Groups[1].Value; continue; }
            if ((lm = Regex.Match(l, @"^(?i)прикорм[а-я]*\s*" + Dash + @"\s*(.+)$")).Success) { gbTxt = lm.Groups[1].Value; continue; }
            if ((lm = Regex.Match(l, @"^(?i)(пва|pva)[а-я]*\s*" + Dash + @"\s*(.+)$")).Success) { pvaTxt = lm.Groups[2].Value; continue; }
            if (line.Contains("💬") || Regex.IsMatch(l, "^(?i)комментари")) comment = Regex.Replace(l, @"^(?i)комментари[а-я]*(\s*игрока)?\s*" + Dash + @"?\s*", "");
        }
        if (baitTxt.Length == 0 && gbTxt.Length == 0 && comment.Length > 0)
        {
            string[] sp = Regex.Split(comment, @"(?i)\.?\s*прикорм[а-я]*\s*" + Dash + @"?\s*");
            int total;
            List<string> cand = TranslateRu(sp[0], false, out total);
            if (cand.Count > 0 && cand.Count * 2 >= total) baitTxt = sp[0];
            if (sp.Length > 1) gbTxt = sp[1];
        }
        int tt;
        if (baitTxt.Length > 0)
        {
            List<string> b = new List<string>(), d = new List<string>();
            foreach (string k in TranslateRu(baitTxt, false, out tt)) { if (IsDip(k)) d.Add(k); else b.Add(k); }
            if (b.Count > 0) o["baitDetail"] = string.Join(" + ", b.ToArray());
            if (d.Count > 0) o["dip"] = string.Join(" + ", d.ToArray());
        }
        if (dipTxt.Length > 0)
        {
            List<string> all = TranslateRu(dipTxt, true, out tt);
            if (all.Count > 0) o["dip"] = string.Join(" + ", all.ToArray());
        }
        if (gbTxt.Length > 0) { List<string> g = TranslateRu(gbTxt, false, out tt); if (g.Count > 0) o["groundbait"] = string.Join(" + ", g.ToArray()); }
        if (pvaTxt.Length > 0) { List<string> p = TranslateRu(pvaTxt, false, out tt); if (p.Count > 0) o["pva"] = string.Join(", ", p.ToArray()); }
        lock (gate) parsed[txt] = o;
        return o;
    }

    private static List<KeyValuePair<Regex, string>> fishRu;
    private static readonly Dictionary<string, string[]> fishMemo = new Dictionary<string, string[]>(StringComparer.Ordinal);

    public static string[] Fish(string txt)
    {
        if (string.IsNullOrEmpty(txt)) return new string[0];
        lock (gate)
        {
            string[] c;
            if (fishMemo.TryGetValue(txt, out c)) return c;
            if (fishRu == null)
            {
                List<KeyValuePair<string, string>> names = new List<KeyValuePair<string, string>>();
                foreach (string en in GameData.Trophies.Keys)
                {
                    string ru = GameData.Translate(en, "ru").Replace("ё", "е").Replace("Ё", "Е").Trim();
                    if (ru.Length >= 3 && ru != en) names.Add(new KeyValuePair<string, string>(ru, en));
                }
                names.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
                fishRu = new List<KeyValuePair<Regex, string>>();
                foreach (KeyValuePair<string, string> kv in names) fishRu.Add(new KeyValuePair<Regex, string>(new Regex(@"(?<![\p{L}])" + Regex.Escape(kv.Key) + @"(?![\p{L}])", RegexOptions.IgnoreCase), kv.Value));
            }
            string t = txt.Replace("ё", "е").Replace("Ё", "Е");
            t = Regex.Replace(t, "#[^\\s]+", " ");
            List<string> found = new List<string>();
            foreach (KeyValuePair<Regex, string> kv in fishRu)
            {
                Match m = kv.Key.Match(t);
                if (!m.Success) continue;
                if (!found.Contains(kv.Value)) found.Add(kv.Value);
                t = kv.Key.Replace(t, " ");
            }
            string[] res = found.ToArray();
            fishMemo[txt] = res;
            return res;
        }
    }

    public static bool HasKnownItem(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int k = text.LastIndexOf("<==", StringComparison.Ordinal);
        if (k >= 0) text = text.Substring(k + 3);
        foreach (string part in Regex.Split(text, @"\s\+\s|\s*,\s*|\s*;\s*"))
        {
            string k2 = ResolveAny(part);
            if (k2 != null && GameData.IsBait(k2)) return true;
        }
        return false;
    }

    private static readonly Dictionary<string, string> anyMemo = new Dictionary<string, string>(StringComparer.Ordinal);
    private static Dictionary<string, string> exactAll;

    private static string OtherLangs(string x)
    {
        string mk = (x ?? "").Trim();
        string hit;
        lock (gate)
        {
            if (anyMemo.TryGetValue(mk, out hit)) return hit;
            if (exactAll == null)
            {
                Dictionary<string, string> ex = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string ol in new string[] { "pl", "de", "fr", "es", "it", "ru" })
                {
                    MatchList ml = Items(ol);
                    for (int i = 0; i < ml.LocNorm.Length; i++) if (ml.LocNorm[i].Length > 0 && !ex.ContainsKey(ml.LocNorm[i])) ex[ml.LocNorm[i]] = ml.En[i];
                }
                exactAll = ex;
            }
        }
        hit = null;
        if (!exactAll.TryGetValue(Fuzzy.Norm(mk), out hit) && mk.Length >= 4)
        {
            string loc;
            foreach (string ol in new string[] { "pl", "de", "fr", "es", "it", "ru" })
            {
                hit = Match(mk, Items(ol), 0.9, out loc);
                if (hit != null) break;
            }
        }
        lock (gate) anyMemo[mk] = hit;
        return hit;
    }

    public static string ResolveAny(string x)
    {
        string k = ResolveKey(x);
        if (k != null) return k;
        x = (x ?? "").Trim();
        if (x.Length < 4) return null;
        return OtherLangs(x);
    }

    private static readonly Dictionary<string, string> normMemo = new Dictionary<string, string>(StringComparer.Ordinal);

    public static string NormalizedPost(string txt)
    {
        if (string.IsNullOrEmpty(txt)) return "";
        lock (gate)
        {
            string c;
            if (normMemo.TryGetValue(txt, out c)) return c;
            StringBuilder sb = new StringBuilder();
            foreach (string line in txt.Split('\n'))
            {
                string l = Regex.Replace(line, @"#\S+", " ");
                if (Regex.IsMatch(l, @"(?i)(Предложить пост|Поиск по водо|Барахолка|Мы в |Поделиться|Подписаться|Наш чат|Авторын|Разместить|Список)")) continue;
                foreach (char ch in l.ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            }
            string res = sb.Length >= 12 ? sb.ToString() : "";
            normMemo[txt] = res;
            return res;
        }
    }

    public static bool Mixed(string txt)
    {
        return !string.IsNullOrEmpty(txt) && Regex.IsMatch(txt, @"(?i)разнорыб");
    }

    public static string ResolveKey(string x)
    {
        x = (x ?? "").Trim();
        if (x.EndsWith(" (crushed)")) x = x.Substring(0, x.Length-10);
        if (x.Length == 0) return null;
        if (GameData.Names.ContainsKey(x) || GameData.HasItem(x)) return x;
        string hit;
        lock (gate) if (keyMemo.TryGetValue(x, out hit)) return hit;
        string loc;
        hit = Match(x, Items("en"), 0.88, out loc);
        lock (gate) keyMemo[x] = hit;
        return hit;
    }

    private static readonly Dictionary<string, string> keyMemo = new Dictionary<string, string>(StringComparer.Ordinal);

    public static string CleanItems(string text, string lang)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string ck = lang + "|" + text;
        lock (gate) { string c; if (cleanMemo.TryGetValue(ck, out c)) return c; }
        List<string> parts = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> pieces = new List<string>();
        foreach (string raw0 in Regex.Split(text, @"\s\+\s|\s*,\s*|\s*;\s*"))
        {
            string r0 = raw0.Trim();
            Match mm = Regex.Match(r0, @"^(.*?\D)\s*(\d+(?:\s*/\s*\d+)+)$");
            if (mm.Success) foreach (string n in Regex.Split(mm.Groups[2].Value, @"\s*/\s*")) pieces.Add(mm.Groups[1].Value.Trim() + " " + n);
            else pieces.Add(r0);
        }
        foreach (string raw in pieces)
        {
            string x = raw.Trim();
            bool crushed = x.EndsWith(" (crushed)");
            if (crushed) x = x.Substring(0, x.Length-10);
            if (x.Length == 0) continue;
            string key;
            if (GameData.Names.ContainsKey(x) || GameData.HasItem(x)) key = x;
            else
            {
                key = ResolveKey(x);
                if (key == null) key = OtherLangs(x);
                if (key == null && Regex.IsMatch(x, @"^[A-Za-z0-9 '&./]+$")) continue;
                if (key == null && (x.Contains(":") || Regex.IsMatch(x, @"(?i)(^|\s)(typ|waga|ilo|porcj|szt)"))) continue;
                if (key == null) key = x;
            }
            if (!seen.Add(key)) continue;
            string label = GameData.Translate(key, lang);
            parts.Add(crushed ? label + " (" + GameData.Ui(lang, "crushed", "crushed") + ")" : label);
        }
        string res = string.Join(" + ", parts.ToArray());
        lock (gate) cleanMemo[ck] = res;
        return res;
    }
}
