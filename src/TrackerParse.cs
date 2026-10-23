using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

internal sealed class TrackItem
{
    public string Fish;
    public int? Weight;
}

internal sealed class TrackRes
{
    public string Kind = "", Fish = "", Dip = "", Pva = "", Rig = "", Tech = "", Lake = "";
    public int? Weight, Total;
    public int X, Y;
    public List<string> Baits = new List<string>();
    public List<TrackItem> Items = new List<TrackItem>();
    public Dictionary<string, object> Recipe;
}

internal sealed class TrackLists
{
    public MatchList Fish, Items;
}

internal sealed class NameHit
{
    public string En, Loc;
    public double Score;
}

internal static class TrackerParse
{
    private const RegexOptions I = RegexOptions.IgnoreCase;
    private static readonly Dictionary<string, TrackLists> cache = new Dictionary<string, TrackLists>(StringComparer.Ordinal);

    private static bool M(string s, string rx) { return Regex.IsMatch(s ?? "", rx, I); }

    public static string Code(string tag)
    {
        if (Regex.IsMatch(tag ?? "", "^zh-(TW|HK|Hant)", I)) return "zh-TW";
        return (tag ?? "de").Split('-')[0].ToLowerInvariant();
    }

    private static MatchList Make(List<string> loc, List<string> en)
    {
        MatchList m = new MatchList();
        m.Loc = loc.ToArray(); m.En = en.ToArray(); m.LocNorm = m.Loc.Select(Fuzzy.Norm).ToArray();
        return m;
    }

    public static TrackLists Lists(string code)
    {
        lock (cache)
        {
            TrackLists t;
            if (cache.TryGetValue(code, out t)) return t;
            List<string> fl = new List<string>(), fe = new List<string>();
            foreach (string f in GameData.Trophies.Keys) { fl.Add(GameData.Translate(f, code)); fe.Add(f); }
            List<string> il = new List<string>(), ie = new List<string>();
            int idx = Math.Max(0, Array.IndexOf(GameData.ItemLangs(), code));
            foreach (string[] r in GameData.ItemRows()) { il.Add(idx < r.Length && r[idx].Length > 0 ? r[idx] : r[0]); ie.Add(r[0]); }
            t = new TrackLists();
            t.Fish = Make(fl, fe);
            t.Items = Make(il, ie);
            cache[code] = t;
            return t;
        }
    }

    public static NameHit Match(string text, MatchList l, double min)
    {
        double sc;
        int i = Fuzzy.Best(text, l.LocNorm, l.Hists, min, out sc);
        if (i < 0 || sc < min) return null;
        NameHit h = new NameHit();
        h.En = l.En[i]; h.Loc = l.Loc[i]; h.Score = sc;
        return h;
    }

    public static NameHit MatchFish(string text, TrackLists lists)
    {
        NameHit best = null;
        foreach (string c in new string[] { text, Regex.Replace(text, @"^\S{1,2}\s+", "") })
        {
            NameHit h = Match(c, lists.Fish, 0.7);
            if (h != null && (best == null || h.Score > best.Score)) best = h;
        }
        return best;
    }

    public static string MatchLake(string text, string code)
    {
        List<string> loc = new List<string>(), ids = new List<string>();
        foreach (Lake l in GameData.Lakes) { loc.Add(l.Name(code)); ids.Add(l.Id); }
        NameHit h = Match(text, Make(loc, ids), 0.85);
        return h != null ? h.En : "";
    }

    private static double? Num(string s)
    {
        double d;
        return double.TryParse((s ?? "").Trim().Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? (double?)d : null;
    }

    public static int? Weight(string numText, string unit)
    {
        string t = Regex.Replace(numText.Trim(), "^[Zz]", "2");
        t = Regex.Replace(t, "[Oo]", "0");
        t = Regex.Replace(t, "[lI|]", "1");
        t = Regex.Replace(t, @"\s", "");
        if (!Regex.IsMatch(t, @"^\d[\d\.,]*$")) return null;
        double? n = Num(t);
        if (n == null) return null;
        double num = n.Value;
        string u = unit.ToLowerInvariant();
        if (u == "kg" || u == "кг" || u == "公斤")
        {
            if (!Regex.IsMatch(t, "[\\.,]") && num >= 100) num = num / 1000;
            return (int)Math.Round(num * 1000);
        }
        if (u.StartsWith("lb")) return (int)Math.Round(num * 453.592);
        return (int)Math.Round(num);
    }

    private static bool SimilarWeight(int? a, int? b)
    {
        if (a == null || b == null) return false;
        string x = a.Value.ToString(CultureInfo.InvariantCulture), y = b.Value.ToString(CultureInfo.InvariantCulture);
        if (x == y) return true;
        if (x.Length != y.Length) return false;
        int diff = 0;
        for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) diff++;
        return diff <= 1;
    }

    public static List<TrackItem> Merge(List<TrackItem> a, List<TrackItem> b)
    {
        List<TrackItem> res = a.Select(x => new TrackItem { Fish = x.Fish, Weight = x.Weight }).ToList();
        foreach (TrackItem it in b)
        {
            if (it.Weight != null)
            {
                if (res.Any(x => x.Fish == it.Fish && SimilarWeight(x.Weight, it.Weight))) continue;
                TrackItem nul = res.FirstOrDefault(x => x.Fish == it.Fish && x.Weight == null);
                if (nul != null) { nul.Weight = it.Weight; continue; }
            }
            int ca = res.Count(x => x.Fish == it.Fish), cb = b.Count(x => x.Fish == it.Fish);
            if (ca < cb) res.Add(new TrackItem { Fish = it.Fish, Weight = it.Weight });
        }
        return res;
    }

    private static List<TrackItem> CardItems(List<OcrBox> g, TrackLists lists)
    {
        if (g.Count == 0) return null;
        int width = g.Max(e => e.X + e.W);
        double minX = width * 0.2;
        Regex wrx = new Regex(@"^\W{0,2}([Zz0-9OoIl][\d\.,OoIl]*)\s*(kg|k|g|кг|г)\.?$", I);
        List<object[]> weights = new List<object[]>();
        foreach (OcrBox e in g)
        {
            string t = e.Text.Trim();
            Match m = wrx.Match(t);
            if (!m.Success) continue;
            string u = m.Groups[2].Value;
            if (u.ToLowerInvariant() == "k") u = "kg";
            int? w = Regex.IsMatch(t, "^[\\dZz]") ? Weight(m.Groups[1].Value, u) : null;
            weights.Add(new object[] { e, w, false });
        }
        List<TrackItem> items = new List<TrackItem>();
        foreach (OcrBox e in g.OrderBy(x => x.Y).ThenBy(x => x.X))
        {
            if (e.X < minX) continue;
            string l = e.Text.Trim();
            if (l.Length < 3 || l.Length > 40) continue;
            if (M(l, @"\d+\s*min|kg|%|/\s*\d|^S\s*\d|\d[\.,]\d")) continue;
            NameHit f = MatchFish(l, lists);
            OcrBox above = g.FirstOrDefault(o => o != e && Math.Abs(o.X-e.X) < e.H * 2 && (e.Y-o.Y) > 0 && (e.Y-o.Y) < e.H * 1.8 && !Regex.IsMatch(o.Text.Trim(), @"\d"));
            if (above != null)
            {
                NameHit f2 = MatchFish(above.Text.Trim() + " " + l, lists);
                if (f2 != null && f2.Score >= 0.8 && (f == null || f2.Score >= f.Score-0.05)) f = f2;
            }
            if (f == null) continue;
            object[] best = null;
            double bestD = 1e9;
            foreach (object[] wt in weights)
            {
                if ((bool)wt[2]) continue;
                OcrBox we = (OcrBox)wt[0];
                double dx = Math.Abs(we.X-e.X), dy = Math.Abs(we.Y-e.Y);
                if (dx > e.H * 2.5 || dy > e.H * 2.6 || dy < 2) continue;
                double dist = dy + dx * 0.5;
                if (dist < bestD) { bestD = dist; best = wt; }
            }
            int? w = null;
            if (best != null) { best[2] = true; w = (int?)best[1]; }
            items.Add(new TrackItem { Fish = f.En, Weight = w });
        }
        return items;
    }

    private static List<TrackItem> KeepnetSequential(string[] lines, TrackLists lists)
    {
        int start = 0;
        for (int i = 0; i < lines.Length; i++) if (M(lines[i], "Nach Fangzeit|Nach Gewicht|Nach Preis|By catch time|By time|By weight|By price|По времени|По весу|По цене")) start = i + 1;
        List<TrackItem> items = new List<TrackItem>();
        for (int i = start; i < lines.Length; i++)
        {
            string l = lines[i].Trim();
            if (l.Length < 3 || l.Length > 40) continue;
            if (M(l, @"\d+\s*min|kg|%|/\s*\d|^S\s*\d")) continue;
            NameHit f = MatchFish(l, lists);
            int wi = i-1;
            if (i > start && !Regex.IsMatch(lines[i-1].Trim(), @"\d") && lines[i-1].Trim().Length >= 3)
            {
                NameHit f2 = MatchFish(lines[i-1].Trim() + " " + l, lists);
                if (f2 != null && f2.Score >= 0.8 && (f == null || f2.Score >= f.Score-0.05)) { f = f2; wi = i-2; }
            }
            if (f == null) continue;
            int? w = null;
            if (wi >= 0)
            {
                Match m = Regex.Match(lines[wi], @"^\s*(\S*?)\s*(kg|g|кг|г|lb|lbs)\s*$", I);
                if (m.Success) { w = Weight(m.Groups[1].Value, m.Groups[2].Value); if (w != null && w <= 0) w = null; }
            }
            items.Add(new TrackItem { Fish = f.En, Weight = w });
        }
        return items;
    }

    public static List<TrackItem> KeepnetItems(string[] lines, List<OcrBox> geo, TrackLists lists)
    {
        List<TrackItem> seq = KeepnetSequential(lines, lists);
        List<TrackItem> ci = CardItems(geo, lists);
        if (ci == null) return seq;
        List<TrackItem> a = ci, b = seq;
        if (b.Count > a.Count) { a = seq; b = ci; }
        return Merge(a, b);
    }

    private static string ResolveItem(string text, TrackLists lists)
    {
        NameHit h = Match(text, lists.Items, 0.75);
        if (h == null) return text;
        if (h.Score >= 0.9) return h.En;
        string q = Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{Nd}]", ""), c = Regex.Replace(h.Loc.ToLowerInvariant(), @"[^\p{L}\p{Nd}]", "");
        if (q.Length >= 8 && c.EndsWith(q)) return h.En;
        return text;
    }

    private const string LabRx = "^(Basis|Base|Основа|Zusätze|Zusatze|Zusatz|Additives?|Добавк\\w*|Lebensmittel|Food|Продукт\\w*|Aroma|Aromatizer|Flavor|Ароматизатор)$";
    private const string AromaRx = "^(Aroma|Aromatizer|Flavor|Ароматизатор)$";

    private static object GeoComposition(List<OcrBox> g, TrackLists lists, out List<string> comp, out string aroma)
    {
        comp = new List<string>();
        aroma = "";
        List<OcrBox> labels = g.Where(e => M(e.Text.Trim(), LabRx)).OrderBy(e => e.Y).ToList();
        if (labels.Count < 2) return null;
        HashSet<OcrBox> used = new HashSet<OcrBox>();
        foreach (OcrBox lb in labels)
        {
            OcrBox best = null;
            double bestDx = 1e9;
            foreach (OcrBox e in g)
            {
                if (e == lb || used.Contains(e)) continue;
                string t = e.Text.Trim();
                if (M(t, LabRx) || t.Length < 3) continue;
                if (Math.Abs((e.Y + e.H / 2.0)-(lb.Y + lb.H / 2.0)) > lb.H * 0.7) continue;
                double dx = e.X-(lb.X + lb.W);
                if (dx < 0 || dx > lb.H * 30) continue;
                if (dx < bestDx) { bestDx = dx; best = e; }
            }
            if (best == null) return "incomplete";
            used.Add(best);
            string v = ResolveItem(Regex.Replace(best.Text.Trim(), "^[^A-Za-zÄÖÜäöüА-Яа-я0-9]+", ""), lists);
            if (M(lb.Text.Trim(), AromaRx)) aroma = v; else comp.Add(v);
        }
        return comp.Count == 0 ? null : "ok";
    }

    private static Dictionary<string, object> RecipeLines(string[] lines, List<OcrBox> geo, TrackLists lists)
    {
        string name = "";
        foreach (string l in lines) if (Regex.IsMatch(l, @"\*\*.+\*\*") && !M(l, "Anzahl|Amount|Количество") && l.Trim().Length > name.Length) name = l.Trim();
        if (name.Length == 0) for (int i = 1; i < lines.Length; i++) if (M(lines[i], @"^(Typ|Type|Тип)\s*:\s*(Trockenmischung|Futtermischung|PVA|Dry mix|Groundbait|Сухая|Прикормка|ПВА)")) { name = lines[i-1].Trim(); break; }
        if (name.Length == 0) for (int i = 1; i < lines.Length; i++) if (M(lines[i], @"^(Typ|Type|Тип|Anzahl|Amount|Количество)\s*:")) { name = lines[i-1].Trim(); break; }
        if (name.Length == 0) return null;
        string all = string.Join(" ", lines);
        string type = M(all, "Trockenmischung|Futtermischung|Dry mix|Groundbait|Прикормка|Сухая смесь") ? "groundbait" : "pva";
        int startIdx = -1;
        for (int i = 0; i < lines.Length; i++) if (M(lines[i].Trim(), @"^\d+\s*(h|ч|Std)\.?$")) startIdx = i;
        if (startIdx < 0) for (int i = 0; i < lines.Length; i++) if (M(lines[i], "ZUSAMMENSETZUNG|COMPOSITION|СОСТАВ")) startIdx = i;
        string labelRx = "^(ZUSTAND|CONDITION|СОСТОЯНИЕ|Kann verwendet|KATEGORIEN|CATEGORIES|Handwerk|Crafting|Favoriten)";
        int aromaIdx = -1;
        for (int i = startIdx + 1; i < lines.Length; i++) if (M(lines[i].Trim(), AromaRx)) { aromaIdx = i; break; }
        int endIdx = lines.Length;
        if (aromaIdx < 0) for (int i = startIdx + 1; i < lines.Length; i++) if (M(lines[i].Trim(), labelRx)) { endIdx = i; break; }
        List<string> comp = new List<string>();
        int stop = aromaIdx > 0 ? aromaIdx : endIdx;
        for (int i = startIdx + 1; i < stop; i++)
        {
            string l = Regex.Replace(lines[i].Trim(), @"^(Zusätze|Zusatze|Zusatz|Basis|Additives?|Base|Добавк\w*|Основа)\s+", "", I);
            if (l.Length < 3 || M(l, "^(Basis|Zusatz|Zusätze|Base|Additive|Основа|Добавка)$") || M(l, labelRx)) continue;
            l = Regex.Replace(l, "^[^A-Za-zÄÖÜäöüА-Яа-я0-9]+", "", I);
            l = Regex.Replace(l, @"^[A-Z]{1,2}\W+(?=[a-zäöü])", "", I);
            comp.Add(ResolveItem(l, lists));
        }
        string aroma = "";
        if (aromaIdx > 0)
            for (int i = aromaIdx + 1; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (l.Length < 3 || M(l, labelRx)) continue;
                aroma = ResolveItem(l, lists);
                break;
            }
        List<string> gc;
        string ga;
        object gs = GeoComposition(geo, lists, out gc, out ga);
        if ("incomplete".Equals(gs)) return null;
        if (gs != null) { comp = gc; aroma = ga; }
        if (comp.Count == 0 && aroma.Length == 0) return null;
        return AppEnv.Obj("name", name, "type", type, "base", comp.Count > 0 ? comp[0] : "", "additives", comp.Skip(1).ToArray(), "attractant", aroma);
    }

    private static Dictionary<string, Dictionary<string, object>> marineRigs;

    private static string MarineRigKey(string text)
    {
        string t = text.Trim();
        if (t.Length < 4) return "";
        if (marineRigs == null)
        {
            marineRigs = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            Dictionary<string, object> d = AppEnv.Json.DeserializeObject(AppEnv.ResourceText("data/marine_rigs.json") ?? "{}") as Dictionary<string, object>;
            if (d != null) foreach (KeyValuePair<string, object> kv in d) { Dictionary<string, object> v = kv.Value as Dictionary<string, object>; if (v != null) marineRigs[kv.Key] = v; }
        }
        List<string> loc = new List<string>(), keys = new List<string>();
        foreach (KeyValuePair<string, Dictionary<string, object>> kv in marineRigs) foreach (KeyValuePair<string, object> n in kv.Value) { loc.Add((n.Value ?? "").ToString()); keys.Add(kv.Key); }
        NameHit h = Match(t, Make(loc, keys), 0.82);
        return h != null ? h.En : "";
    }

    private static TrackRes MarineSetup(string[] lines, TrackLists lists)
    {
        string rk = "";
        int ri = -1;
        for (int i = 0; i < lines.Length && rk.Length == 0; i++)
        {
            if (!M(lines[i], @"^(Angelmontage|Rig|Монтаж|Zestaw)\b")) continue;
            for (int j = i + 1; j < Math.Min(lines.Length, i + 4); j++)
            {
                string c = lines[j].Trim();
                if (c.Length == 0 || M(c, "Ändern|Change|Изменить")) continue;
                rk = MarineRigKey(c);
                ri = j;
                break;
            }
        }
        if (rk.Length == 0) return null;
        if (rk == "rig_MarineBottomDropShot") rk = "rig_MarinePilker";
        string main = "";
        Regex isItem = new Regex(@"\s[O0o©@®]\s*$", I);
        for (int i = ri + 1; i < lines.Length; i++)
        {
            string l = lines[i].Trim();
            if (!isItem.IsMatch(l)) continue;
            string name = isItem.Replace(l, "").Trim();
            string info = "";
            for (int j = i + 1; j < Math.Min(lines.Length, i + 6); j++) { if (isItem.IsMatch(lines[j].Trim())) break; info += " " + lines[j]; }
            if (M(info, "Bremskraft|Spulenkapazit|Drag|Durchmesser|Diameter|Schock|Shock|Шок|Фрикцион|Диаметр")) continue;
            string typ = "";
            Match tm = Regex.Match(info, @"(Typ|Type|Тип)\s*:\s*(\S+(\s\S+)?)", I);
            if (tm.Success) typ = tm.Groups[2].Value.Trim();
            if (M(typ, "Rassel|Rattle|Tube|Perle|Bead|Lockstoff|Attract|Погрем|Трубк|Бусин|Beifänger|Teaser|Seitenarm|Makk|Мушк|Отвод")) continue;
            if (typ.Length == 0 && M(info, "Größe|Size|Размер")) continue;
            NameHit h = Match(name, lists.Items, 0.8);
            main = h != null ? h.En : name;
            break;
        }
        TrackRes o = new TrackRes();
        o.Kind = "setup";
        if (main.Length > 0) o.Baits.Add(main);
        Dictionary<string, object> rn;
        o.Rig = marineRigs.TryGetValue(rk, out rn) ? AppEnv.GetString(rn, "en") : rk;
        o.Tech = "marine";
        return o;
    }

    public static TrackRes Parse(string[] lines, List<OcrBox> geo, string code)
    {
        TrackLists lists = Lists(code);
        TrackRes outR = new TrackRes();
        string allText = string.Join(" ", lines);
        bool isKeepnetScreen = M(allText, "Setzkescher|Keepnet|Садок|Siatka");
        bool isDetail = isKeepnetScreen && M(allText, "Fangzeit|Gewässer|Catch time|Waterbody|Время поимки|Водоём");
        for (int i = 1; i < lines.Length && isDetail; i++)
        {
            Match m = Regex.Match(lines[i], @"^\s*(Gewicht|Weight|Вес|Waga|Poids|Peso)\s*:\s*([\d\.,]+)\s*(kg|g|кг|г|lb|lbs)", I);
            if (!m.Success) continue;
            int? w = Weight(m.Groups[2].Value, m.Groups[3].Value);
            if (w == null) continue;
            NameHit f = MatchFish(lines[i-1], lists);
            if (f == null) continue;
            outR.Kind = "detail"; outR.Weight = w; outR.Fish = f.En;
            foreach (string l in lines) if (outR.Lake.Length == 0 && l.Length >= 4 && l.Length <= 40) outR.Lake = MatchLake(l, code);
            int bi = -1;
            for (int j = i + 1; j < lines.Length; j++) if (M(lines[j].Trim(), "^(Köder|Bait|Наживка|Przynęta|Appât|Cebo|Esca|Isca)$")) bi = j;
            if (bi >= 0)
                for (int j = bi + 1; j < lines.Length && outR.Baits.Count < 2; j++)
                {
                    string l = lines[j].Trim();
                    if (l.Length == 0 || M(l, @"^\d[\d\.,]*\s*(kg|g|cm|кг|г|см)$") || Regex.IsMatch(l, @"^\d{1,2}:\d{2}$")) break;
                    NameHit h = Match(l, lists.Items, 0.8);
                    outR.Baits.Add(h != null ? h.En : l);
                }
            return outR;
        }
        if (M(allText, "ZUSAMMENSETZUNG|COMPOSITION|СОСТАВ"))
        {
            Dictionary<string, object> rec = RecipeLines(lines, geo, lists);
            if (rec != null) { outR.Kind = "recipe"; outR.Recipe = rec; return outR; }
        }
        bool isKeepnet = isKeepnetScreen && M(allText, "Kapazität|Capacity|Вместимость|Pojemność");
        bool isMarket = M(allText, "Fischmarkt|Fish market|Рыбный рынок|Targ rybny") && M(allText, "Zum Verkauf|For sale|На продажу|Na sprzedaż");
        if (isMarket || isKeepnet)
        {
            List<TrackItem> items = KeepnetItems(lines, geo, lists);
            Match sm = isMarket ? Regex.Match(allText, @"(\d{1,3})\s*(stk|pcs|шт)", I) : Regex.Match(allText, @"(Kapazität|Capacity|Вместимость|Pojemność)\D{0,20}?(\d{1,3})\s*/\s*(\d{2,3})", I);
            if (sm.Success) outR.Total = int.Parse(isMarket ? sm.Groups[1].Value : sm.Groups[2].Value, CultureInfo.InvariantCulture);
            if (items.Count > 0) { outR.Kind = "keepnet"; outR.Items = items; return outR; }
        }
        for (int i = 1; i < lines.Length; i++)
        {
            Match m = Regex.Match(lines[i], @"(\d[\d\.,]*)\s*(kg|g|кг|г|lb|lbs|公斤|克)\s*/\s*\d", I);
            if (!m.Success) continue;
            double? num = Num(m.Groups[1].Value);
            if (num == null) continue;
            string unit = m.Groups[2].Value.ToLowerInvariant();
            double grams = num.Value;
            if (unit == "kg" || unit == "кг" || unit == "公斤") grams = num.Value * 1000;
            if (unit.StartsWith("lb")) grams = num.Value * 453.592;
            NameHit best = MatchFish(lines[i-1], lists);
            if (best == null) continue;
            outR.Kind = "catch"; outR.Weight = (int)Math.Round(grams); outR.Fish = best.En;
            return outR;
        }
        TrackRes mar = MarineSetup(lines, lists);
        if (mar != null) return mar;
        int start = -1;
        for (int i = 0; i < lines.Length; i++) if (M(lines[i], @"Köder\s*Kombination|Bait\s*combination|Комбинация\s*наживок|Kombinacja\s*przyn")) { start = i; break; }
        if (start < 0)
        {
            bool hud = M(allText, "Bedienfeld|Esc|Chat|Hilfe|Rute aufnehmen|Menu|Меню");
            if (!hud && lines.Length <= 14)
                foreach (string l in lines)
                {
                    Match pm = Regex.Match(l, @"^\s*(\d{1,4})\s*[:;]\s*(\d{1,4})\s*$");
                    if (pm.Success) { outR.Kind = "position"; outR.X = int.Parse(pm.Groups[1].Value); outR.Y = int.Parse(pm.Groups[2].Value); return outR; }
                }
            return outR;
        }
        for (int i = 0; i < start; i++)
            if (M(lines[i], @"^(Angelmontage|Rig|Монтаж|Zestaw)\b") && i + 1 < start && !M(lines[i + 1], "Ändern|Change|Изменить")) { outR.Rig = lines[i + 1].Trim(); break; }
        List<string> baits = new List<string>();
        Regex end = new Regex(@"\s[O0o©]\s*$", I);
        for (int i = start + 1; i < lines.Length; i++)
        {
            string l = lines[i];
            if (!end.IsMatch(l)) continue;
            string name = end.Replace(l, "").Trim();
            string next = i + 1 < lines.Length ? lines[i + 1] : "", next2 = i + 2 < lines.Length ? lines[i + 2] : "";
            NameHit h = Match(name, lists.Items, 0.8);
            string val = h != null ? h.En : name;
            if (M(next, @"^(Typ|Type|Тип)\s*:\s*D") || M(next, @"\bDip\b|Дип")) outR.Dip = val;
            else if (M(next + " " + next2, "Qualität|Quality|Качество")) outR.Pva = name;
            else baits.Add(val);
        }
        outR.Baits = baits.Take(2).ToList();
        string head = string.Join(" ", lines.Take(start + 1).ToArray());
        if (M(head, "Spinn|Spinning|Спиннинг|Baitcast|Jerk")) outR.Tech = "spin";
        else if (M(head, "Feeder|Karpfen|Carp|Grund|Bottom|Picker|Фидер|Карпов|Донн")) outR.Tech = "bottom";
        else if (M(head, "Match|Bolo|Pose|Float|Telesk|Поплав|Махов|Матч")) outR.Tech = "float";
        else if (M(head, "Meer|Marine|Sea|Морск")) outR.Tech = "marine";
        else if (outR.Dip.Length > 0 || outR.Pva.Length > 0 || outR.Baits.Count >= 2) outR.Tech = "bottom";
        if (outR.Baits.Count > 0 || outR.Dip.Length > 0 || outR.Pva.Length > 0) outR.Kind = "setup";
        return outR;
    }

    private const string LabelRx = @"^(Gewicht|Typ|Anzahl|Länge|Größe|Material|Farbe|Hersteller|Befestigung|Versch\s?leiß|Tragfähigkeit|Weight|Type|Quantity|Amount|Length|Size|Color|Wear|Вес|Тип|Количество|Длина)\b";

    public static bool LeaderLine(string l)
    {
        return (l.Length <= 40 && M(l, "(vorfäch|vorfach|leader|поводок)")) || M(l, @"^\s*(Typ|Type|Тип)\s*:.*(vorfäch|vorfach|leader|поводок)");
    }

    public static Dictionary<string, object> Leader(string[] lines, int? cmOverride)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (!LeaderLine(lines[i])) continue;
            string mat = "", kg = "";
            int? len = null;
            for (int j = i; j < Math.Min(lines.Length, i + 6); j++)
            {
                string l = lines[j];
                Match m;
                if (mat.Length == 0 && (m = Regex.Match(l, @"(Material|Материал)\s*:\s*(\S+)", I)).Success) mat = m.Groups[2].Value;
                if (len == null && (m = Regex.Match(l, @"(Länge|Length|Длина)\s*:\s*(\d+(?:[.,]\d+)?)\s*(cm|m|см|м)\b", I)).Success)
                {
                    double v = double.Parse(m.Groups[2].Value.Replace(",", "."), CultureInfo.InvariantCulture);
                    if (m.Groups[3].Value == "m" || m.Groups[3].Value == "м") v = v * 100;
                    len = (int)Math.Round(v);
                }
                if (kg.Length == 0 && (m = Regex.Match(l, @"(Tragfähigkeit|Load|Нагрузка)\s*:\s*(\d+(?:[.,]\d+)?)\s*(kg|кг)", I)).Success) kg = m.Groups[2].Value + " kg";
            }
            if (len == null) len = cmOverride;
            if (len == null) return null;
            return AppEnv.Obj("cm", len.Value, "material", mat, "kg", kg);
        }
        return null;
    }

    public static System.Drawing.Rectangle LeaderRect(OcrRead r)
    {
        OcrBox t = r.Boxes.FirstOrDefault(g => LeaderLine(g.Text));
        if (t == null) return System.Drawing.Rectangle.Empty;
        OcrBox len = r.Boxes.FirstOrDefault(g => M(g.Text, @"^(Länge|Length|Длина)\s*:?\s*$") && Math.Abs(g.Y-t.Y) <= 40 && g.Y >= t.Y-4);
        if (len == null) return System.Drawing.Rectangle.Empty;
        double f = r.F;
        return new System.Drawing.Rectangle((int)((len.X-4) / f), (int)((len.Y-8) / f), (int)((len.W + 140) / f), (int)((len.H + 16) / f));
    }

    public static void RepairSetup(TrackRes o, string[] lines, string code)
    {
        int start = -1;
        for (int i = 0; i < lines.Length; i++) if (M(lines[i], @"Köder\s*Kombination|Bait\s*combination|Комбинация\s*наживок|Kombinacja\s*przyn")) { start = i; break; }
        if (start < 0) return;
        TrackLists lists = Lists(code);
        List<string> baits = new List<string>();
        Regex end = new Regex(@"\s[O0o©]\s*$", I);
        for (int i = start + 1; i < lines.Length; i++)
        {
            string l = lines[i];
            if (!end.IsMatch(l)) continue;
            string name = end.Replace(l, "").Trim();
            if (name.Length == 0 || M(name, LabelRx)) continue;
            string next = i + 1 < lines.Length ? lines[i + 1] : "", next2 = i + 2 < lines.Length ? lines[i + 2] : "";
            string both = next + " " + next2;
            if (M(next, @"^(Typ|Type|Тип)\s*:\s*D") || M(next, @"\bDip\b|Дип") || (M(both, @"\bPortion|\bportions?\b|порци") && !M(both, "Qualität|Quality|Качество")))
            {
                if (o.Dip.Length == 0) { NameHit dh = Match(name, lists.Items, 0.8); o.Dip = dh != null ? dh.En : name; }
                continue;
            }
            if (M(both, "Qualität|Quality|Качество")) continue;
            NameHit h = Match(name, lists.Items, 0.8);
            baits.Add(h != null ? h.En : name);
        }
        if (baits.Count > 0) o.Baits = baits.Take(2).ToList();
        if (o.Rig.Length == 0 || M(o.Rig, "Ändern|Change|Изменить"))
            for (int i = 0; i < start; i++)
            {
                if (!M(lines[i], @"^(Angelmontage|Rig|Монтаж|Zestaw)\b")) continue;
                for (int j = i + 1; j < Math.Min(start, i + 4); j++) { string c = lines[j].Trim(); if (c.Length > 0 && !M(c, "Ändern|Change|Изменить")) { o.Rig = c; break; } }
                break;
            }
    }

    public static void RepairDetail(TrackRes o, string[] lines, string code)
    {
        if (o.Baits.Count > 0) return;
        int ki = -1;
        for (int i = 0; i < lines.Length; i++) if (M(lines[i].Trim(), "^(Köder|Bait|Наживка|Przynęta|Appât|Cebo|Esca|Isca)$")) ki = i;
        if (ki < 0) return;
        TrackLists lists = Lists(code);
        for (int j = ki + 1; j < lines.Length && o.Baits.Count < 2; j++)
        {
            string l = lines[j].Trim();
            if (l.Length < 4 || l.Length > 60 || Regex.IsMatch(l, @"^\d") || M(l, @"\d\s*(kg|g|cm|min)\b")) continue;
            if (MatchLake(l, code).Length > 0) continue;
            NameHit h = Match(l, lists.Items, 0.85);
            if (h != null && !o.Baits.Contains(h.En)) o.Baits.Add(h.En);
        }
    }

    public static int? AgeMin(string s)
    {
        if (Regex.IsMatch(s, @"<\s*1\s*min")) return 0;
        Match m = Regex.Match(s, @"(?:(\d{1,2})\s*h\s*)?(\d{1,3})\s*min");
        if (m.Success) return (m.Groups[1].Success ? int.Parse(m.Groups[1].Value) * 60 : 0) + int.Parse(m.Groups[2].Value);
        m = Regex.Match(s, @"(\d{1,2})\s*h\s+(\d{1,2})\b");
        if (m.Success) return int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
        m = Regex.Match(s, @"(\d{1,2})\s*h\b");
        if (m.Success) return int.Parse(m.Groups[1].Value) * 60;
        return null;
    }

    public static List<TrackItem> Cards(List<OcrBox> g, string code, DateTime shot, Dictionary<TrackItem, DateTime> times)
    {
        TrackLists lists = Lists(code);
        Regex wr = new Regex(@"^\s*\d+(?:[.,]\d{1,3})?\s*(kg|g)\s*$", I);
        List<TrackItem> cards = new List<TrackItem>();
        foreach (OcrBox w in g.Where(x => wr.IsMatch(x.Text)))
        {
            Match m = Regex.Match(w.Text, @"(\d+(?:[.,]\d{1,3})?)\s*(kg|g)", I);
            int? grams = Weight(m.Groups[1].Value, m.Groups[2].Value);
            if (grams == null) continue;
            double tol = Math.Max(25, w.H);
            List<OcrBox> names = g.Where(x => x.Y < w.Y && (w.Y-x.Y) <= w.H * 4 && Math.Abs(x.X-w.X) <= tol && !M(x.Text, @"min|%|\d\s*(kg|g)\b|^[\s\d.,e•-]+$")).OrderBy(x => x.Y).ToList();
            string name = string.Join(" ", names.Select(x => x.Text.Trim()).ToArray()).Trim();
            string fish = null;
            if (name.Length > 0) { NameHit h = MatchFish(name, lists); if (h != null) fish = h.En; }
            if (fish == null && names.Count > 1) { NameHit h = MatchFish(names[names.Count-1].Text.Trim(), lists); if (h != null) fish = h.En; }
            if (fish == null) continue;
            int? age = null;
            double bs = 1e9;
            foreach (OcrBox a in g)
            {
                if (a.Y >= w.Y || (w.Y-a.Y) > w.H * 14 || !M(a.Text, @"min|\d\s*h\b")) continue;
                double dx = Math.Abs(a.X-w.X);
                if (dx > Math.Max(60, w.H * 2)) continue;
                int? v = AgeMin(a.Text);
                if (v == null) continue;
                double sc = (w.Y-a.Y) + dx * 2;
                if (sc < bs) { bs = sc; age = v; }
            }
            TrackItem it = new TrackItem { Fish = fish, Weight = grams };
            if (age != null) times[it] = shot.AddMinutes(-age.Value-0.5);
            cards.Add(it);
        }
        return cards;
    }

    public static int[] FindPos(string[] lines, Lake lake)
    {
        foreach (string l in lines)
        {
            string t = Regex.Replace(Regex.Replace(Regex.Replace(Regex.Replace(l, "[Oo]", "0"), "[Il|]", "1"), "G", "5"), "S", "5");
            Match m = Regex.Match(t, @"(\d{1,3})\s*[:;.]\s*(\d{1,3})");
            if (m.Success)
            {
                int x = int.Parse(m.Groups[1].Value), y = int.Parse(m.Groups[2].Value);
                if (x >= 1 && x <= 999 && y >= 1 && y <= 999) return new int[] { x, y };
            }
        }
        foreach (string l in lines)
        {
            string t = Regex.Replace(Regex.Replace(Regex.Replace(l, "[Oo]", "0"), "[Il|]", "1"), @"\s", "");
            foreach (Match m in Regex.Matches(t, @"(?<!\d)\d{4,7}(?!\d)"))
            {
                string tok = m.Value;
                for (int i = 1; i <= tok.Length-2; i++)
                {
                    if (tok[i] != '2') continue;
                    int x = int.Parse(tok.Substring(0, i)), y = int.Parse(tok.Substring(i + 1));
                    if (x < 1 || x > 999 || y < 1 || y > 999) continue;
                    if (lake != null && lake.HasBounds && (x < lake.XMin || x > lake.XMax || y < Math.Min(lake.YSouth, lake.YNorth) || y > Math.Max(lake.YSouth, lake.YNorth))) continue;
                    return new int[] { x, y };
                }
            }
        }
        return null;
    }

    public static string MatchTemp(string[] lines)
    {
        List<string> loc = new List<string>(), en = new List<string>();
        foreach (string k in new string[] { "cold", "normal", "warm" })
        {
            loc.Add(k); en.Add(k);
            if (GameData.Lang != null)
                foreach (KeyValuePair<string, object> lp in GameData.Lang)
                {
                    Dictionary<string, object> d = lp.Value as Dictionary<string, object>;
                    string v = AppEnv.GetString(d, "temp_" + k);
                    if (v.Length > 0) { loc.Add(v); en.Add(k); }
                }
        }
        MatchList ml = Make(loc, en);
        foreach (string l in lines)
            foreach (string wd in Regex.Split(l, @"\s+"))
            {
                string t = wd.Replace("rn", "m").Trim();
                if (t.Length < 3) continue;
                NameHit h = Match(t, ml, 0.7);
                if (h != null) return h.En;
            }
        return "";
    }
}
