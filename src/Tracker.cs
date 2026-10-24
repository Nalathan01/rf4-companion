using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal static class Tracker
{
    private static readonly object gate = new object();
    private static Dictionary<string, object> st;
    private static Thread worker;
    public static volatile int Version;
    public static volatile string PauseReason = "";
    private static string msg = "";
    private static DateTime msgAt = DateTime.MinValue;
    private static volatile string busyFile = "";

    private static string StateFile { get { return AppEnv.DataPath("tracker2.json"); } }

    public static string Dir
    {
        get
        {
            string d = (UserStore.Get("trackerDir") ?? "").ToString();
            return d.Length > 0 ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Russian Fishing 4", "Screenshots");
        }
    }

    public static string LangTag
    {
        get { string l = (UserStore.Get("trackerLang") ?? "").ToString(); return l.Length > 0 ? l : "de-DE"; }
    }

    public static bool On
    {
        get { object v = UserStore.Get("trackerOn"); return v is bool && (bool)v && PauseReason.Length == 0; }
    }

    private static string Now() { return DateTime.Now.ToString("o", CultureInfo.InvariantCulture); }

    private static DateTime Time(object v)
    {
        DateTime t;
        return DateTime.TryParse((v ?? "").ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t) ? t : DateTime.MinValue;
    }

    private static List<Dictionary<string, object>> L(Dictionary<string, object> d, string key)
    {
        object v;
        if (d.TryGetValue(key, out v) && v is List<Dictionary<string, object>>) return (List<Dictionary<string, object>>)v;
        List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
        IEnumerable e = v as IEnumerable;
        if (e != null && !(v is string)) foreach (object o in e) { Dictionary<string, object> x = o as Dictionary<string, object>; if (x != null) l.Add(x); }
        d[key] = l;
        return l;
    }

    private static List<string> Strs(object v)
    {
        List<string> l = new List<string>();
        IEnumerable e = v as IEnumerable;
        if (e != null && !(v is string)) foreach (object o in e) if (o != null && o.ToString().Length > 0) l.Add(o.ToString());
        return l;
    }

    private static string S(Dictionary<string, object> d, string k) { return AppEnv.GetString(d, k); }

    private static void Load()
    {
        if (st != null) return;
        try { st = AppEnv.ReadJsonFile(StateFile) as Dictionary<string, object>; } catch (Exception ex) { AppEnv.Log("Tracker", ex.Message); }
        if (st == null) st = new Dictionary<string, object>();
        L(st, "sessions"); L(st, "pending");
        if (!st.ContainsKey("seen")) st["seen"] = new List<object>();
    }

    private static void Save()
    {
        Version++;
        try { AppEnv.WriteJsonFile(StateFile, st); } catch (Exception ex) { AppEnv.Log("Tracker", ex.Message); }
    }

    private static void Say(string text)
    {
        msg = text;
        msgAt = DateTime.Now;
        Version++;
    }

    private static string T(string key, string fb) { return GameData.Ui(UserStore.Lang, key, fb); }

    private static Dictionary<string, object> Cur(bool create)
    {
        List<Dictionary<string, object>> ss = L(st, "sessions");
        Dictionary<string, object> s = ss.FirstOrDefault(x => S(x, "id") == S(st, "cur"));
        if (s != null && DateTime.Now.Subtract(Time(s["end"])).TotalMinutes > 360)
        {
            if (!create) return s;
            s = NewSession(s, false);
            Say(T("trkNewAfterPause", "Über 6 Stunden Pause, neue Session gestartet."));
        }
        if (s == null && create) s = NewSession(ss.OrderBy(x => S(x, "start")).LastOrDefault(), false);
        if (s != null && create) s["end"] = Now();
        return s;
    }

    private static Dictionary<string, object> NewSession(Dictionary<string, object> prev, bool keepSpot)
    {
        Dictionary<string, object> s = AppEnv.Obj("id", Guid.NewGuid().ToString("N").Substring(0, 8), "start", Now(), "end", Now(),
            "lake", prev != null ? S(prev, "lake") : "", "x", keepSpot && prev != null ? prev["x"] : null, "y", keepSpot && prev != null ? prev["y"] : null,
            "clip", keepSpot && prev != null ? S(prev, "clip") : "", "temp", "", "keepnet", new List<Dictionary<string, object>>(), "keepnetTime", "", "hits", new List<Dictionary<string, object>>());
        s["rods"] = prev != null ? L(prev, "rods").Select(r => { Dictionary<string, object> c = new Dictionary<string, object>(r); c["inh"] = true; return c; }).ToList() : new List<Dictionary<string, object>>();
        L(st, "sessions").Add(s);
        List<Dictionary<string, object>> ss = L(st, "sessions");
        if (ss.Count > 40) ss.RemoveRange(0, ss.Count-40);
        st["cur"] = s["id"];
        st.Remove("replace");
        return s;
    }

    public static void Start()
    {
        lock (gate) Load();
        worker = new Thread(Loop);
        worker.IsBackground = true;
        worker.Start();
        Thread hk = new Thread(HotkeyLoop);
        hk.IsBackground = true;
        hk.SetApartmentState(ApartmentState.STA);
        hk.Start();
    }

    private static void Loop()
    {
        while (true)
        {
            Thread.Sleep(1500);
            try { Tick(); } catch (Exception ex) { AppEnv.Log("Tracker", ex.ToString()); busyFile = ""; }
        }
    }

    private static void Tick()
    {
        if (!On) return;
        string dir = Dir;
        if (!Directory.Exists(dir)) return;
        DateTime since, cutoff = DateTime.Now.AddSeconds(-1);
        HashSet<string> seen;
        lock (gate)
        {
            since = Time(st.ContainsKey("since") ? st["since"] : null);
            if (since == DateTime.MinValue) { since = DateTime.Now; st["since"] = Now(); Save(); }
            seen = new HashSet<string>(Strs(st["seen"]), StringComparer.OrdinalIgnoreCase);
        }
        FileInfo next = new DirectoryInfo(dir).GetFiles()
            .Where(f => Regex.IsMatch(f.Extension, @"^\.(png|jpg|jpeg|bmp)$", RegexOptions.IgnoreCase) && f.LastWriteTime > since && f.LastWriteTime < cutoff && !seen.Contains(f.FullName))
            .OrderBy(f => f.LastWriteTime).FirstOrDefault();
        if (next == null) return;
        lock (gate)
        {
            List<object> sl = Strs(st["seen"]).Cast<object>().ToList();
            sl.Add(next.FullName);
            if (sl.Count > 400) sl.RemoveRange(0, sl.Count-400);
            st["seen"] = sl;
            Save();
        }
        Process(next.FullName, null);
    }

    private static OcrRead Ocr(string path, string lang, RectangleF crop, double zoom, bool bin)
    {
        return TrackerOcr.Read(path, lang, crop, zoom, bin);
    }

    private static bool RuAvailable() { return ImageOcr.Available("ru"); }

    public static void Process(string path, string rodId)
    {
        busyFile = Path.GetFileName(path);
        Version++;
        try { ProcessInner(path, rodId); }
        catch (Exception ex) { AppEnv.Log("Tracker", path + ": " + ex.Message); Say(T("trkError", "Screenshot konnte nicht gelesen werden") + ": " + ex.Message); }
        finally { busyFile = ""; Version++; }
    }

    private static void ProcessInner(string path, string rodId)
    {
        string tag = LangTag, code = TrackerParse.Code(tag);
        DateTime time = File.GetLastWriteTime(path);
        OcrRead r0 = Ocr(path, tag, RectangleF.Empty, 1, false);
        string[] lines = r0.Lines();
        if (lines.Length == 0) { Say(T("trackerUnknown", "Screenshot ohne Fang oder Setup")); return; }
        TrackRes r = TrackerParse.Parse(lines, r0.Boxes, code);
        if (rodId != null && r.Kind != "setup") { Say(T("trkNoRod", "Auf diesem Screenshot ist keine Rute zu sehen.")); return; }
        switch (r.Kind)
        {
            case "setup":
                {
                    TrackerParse.RepairSetup(r, lines, code);
                    Dictionary<string, object> leader = TrackerParse.Leader(lines, null);
                    if (leader == null)
                    {
                        Rectangle rect = TrackerParse.LeaderRect(r0);
                        if (!rect.IsEmpty)
                        {
                            OcrRead rl = Ocr(path, tag, new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), 4, false);
                            Match m = Regex.Match(string.Join(" ", rl.Lines()), @"(\d+(?:[.,]\d+)?)\s*(cm|m|см|м)\b");
                            if (m.Success)
                            {
                                double v = double.Parse(m.Groups[1].Value.Replace(",", "."), CultureInfo.InvariantCulture);
                                if (m.Groups[2].Value == "m" || m.Groups[2].Value == "м") v = v * 100;
                                leader = TrackerParse.Leader(lines, (int)Math.Round(v));
                            }
                        }
                    }
                    AddRod(r, leader, path, time, rodId);
                    return;
                }
            case "detail":
                TrackerParse.RepairDetail(r, lines, code);
                lock (gate)
                {
                    Dictionary<string, object> s = Cur(true);
                    if (r.Lake.Length > 0) s["lake"] = r.Lake;
                    AddCatch(s, r.Fish, r.Weight, r.Baits, time, path, "detail");
                    Save();
                }
                return;
            case "catch":
                lock (gate) { AddCatch(Cur(true), r.Fish, r.Weight, null, time, path, "catch"); Save(); }
                return;
            case "keepnet":
                Keepnet(path, tag, code, r, time);
                return;
            case "recipe":
                Say(Fangbuch.AddRecipeFromTracker(r.Recipe, Lake()));
                return;
            case "position":
                SetPos(r.X, r.Y);
                if (RuAvailable()) LakeFromMap(path);
                return;
        }
        string all = string.Join(" ", lines);
        bool isHud = Regex.IsMatch(all, "Bedienfeld|Hilfe|Chat|Rute aufnehmen|Schnur straffen|Details|Menu|Меню", RegexOptions.IgnoreCase);
        if (!isHud && Regex.IsMatch(all, "Freilassen|Release|Отпустить|Setzkescher|Keepnet|Садок|Leertaste|Space", RegexOptions.IgnoreCase))
        {
            TrackLists lists = TrackerParse.Lists(code);
            NameHit f = null;
            foreach (string l in lines.Take(3)) if (f == null) f = TrackerParse.MatchFish(l, lists);
            if (f != null)
            {
                int? w = null;
                foreach (string l in Ocr(path, tag, new RectangleF(0.38f, 0.12f, 0.24f, 0.045f), 4, true).Lines())
                {
                    Match m = Regex.Match(l, @"(\d[\d\.,]*)\s*(kg|g|кг|г|lb|lbs)\b", RegexOptions.IgnoreCase);
                    if (m.Success) { w = TrackerParse.Weight(m.Groups[1].Value, m.Groups[2].Value); if (w != null) break; }
                }
                if (w == null) { Say(T("trackerUnknown", "Screenshot ohne Fang oder Setup")); return; }
                lock (gate) { AddCatch(Cur(true), f.En, w, null, time, path, "catch"); Save(); }
                return;
            }
        }
        int[] hp = TrackerParse.FindPos(Ocr(path, tag, new RectangleF(0.925f, 0.87f, 0.055f, 0.075f), 4, false).Lines(), LakeObj());
        if (hp != null)
        {
            string ht = TrackerParse.MatchTemp(Ocr(path, tag, new RectangleF(0.9f, 0.1f, 0.1f, 0.022f), 4, true).Lines());
            if (ht.Length > 0) lock (gate) { Cur(true)["temp"] = ht; Save(); }
            SetPos(hp[0], hp[1]);
            return;
        }
        if (!isHud && lines.Length < 60)
        {
            OcrBox cand = r0.Boxes.FirstOrDefault(b => Regex.IsMatch(b.Text.Trim(), @"^\d{1,3}(\s*[:;.]\s*|\s+)\d{1,3}$") && b.H < 30);
            int grid = lines.Count(l => Regex.IsMatch(l.Trim(), "^([A-J]|10|[1-9])$"));
            if (cand == null && grid >= 6) cand = r0.Boxes.FirstOrDefault(b => Regex.IsMatch(b.Text.Trim(), @"^\d{1,3}(\s*[:;.]\s*\d{1,3})?$") && b.H < 30);
            if (cand != null || grid >= 6)
            {
                if (RuAvailable()) LakeFromMap(path);
                if (cand != null)
                {
                    double f = r0.F;
                    RectangleF rect = new RectangleF((float)((cand.X-cand.H * 3.3) / f), (float)((cand.Y-cand.H) / f), (float)((cand.W + cand.H * 4.2) / f), (float)(cand.H * 2.3 / f));
                    int[] pos = TrackerParse.FindPos(Ocr(path, tag, rect, 4, false).Lines(), LakeObj());
                    if (pos != null) { SetPos(pos[0], pos[1]); return; }
                }
                Say(T("trackerUnknown", "Screenshot ohne Fang oder Setup"));
                return;
            }
        }
        string temp = TrackerParse.MatchTemp(Ocr(path, tag, new RectangleF(0.9f, 0.1f, 0.1f, 0.022f), 4, true).Lines());
        if (temp.Length > 0) lock (gate) { Cur(true)["temp"] = temp; Save(); }
        if (temp.Length > 0) Say(T("temperature", "Wassertemperatur") + ": " + T("temp_" + temp, temp));
        else Say(T("trackerUnknown", "Screenshot ohne Fang oder Setup"));
    }

    private static string Lake()
    {
        lock (gate) { Dictionary<string, object> s = Cur(false); return s != null ? S(s, "lake") : ""; }
    }

    private static Lake LakeObj()
    {
        Lake l;
        return GameData.LakeById.TryGetValue(Lake(), out l) ? l : null;
    }

    private static void LakeFromMap(string path)
    {
        foreach (string l in Ocr(path, "ru", RectangleF.Empty, 1, false).Lines())
        {
            if (l.Length < 4 || l.Length > 40) continue;
            string id = TrackerParse.MatchLake(l, "ru");
            if (id.Length == 0) continue;
            lock (gate)
            {
                Dictionary<string, object> s = Cur(true);
                if (S(s, "lake") != id && S(s, "lake").Length > 0 && s["x"] != null) { s = NewSession(s, false); }
                s["lake"] = id;
                Save();
            }
            Lake lk;
            Say(T("trackerLakeFound", "Gewässer erkannt") + ": " + (GameData.LakeById.TryGetValue(id, out lk) ? lk.Name(UserStore.Lang) : id));
            return;
        }
    }

    private static void SetPos(int x, int y)
    {
        lock (gate)
        {
            Dictionary<string, object> s = Cur(true);
            bool moved = false;
            if (s["x"] != null && s["y"] != null)
            {
                int ox = System.Convert.ToInt32(s["x"]), oy = System.Convert.ToInt32(s["y"]);
                if (Math.Abs(ox-x) > 2 || Math.Abs(oy-y) > 2) { s = NewSession(s, false); moved = true; }
            }
            s["x"] = x; s["y"] = y;
            Save();
            Say(T("trackerPos", "Position erkannt") + ": " + x + ":" + y + (moved ? ". " + T("trkNewSpot", "Neuer Spot, neue Session mit denselben Ruten.") : ""));
        }
    }

    private static string Key(string b) { return Regex.Replace((b ?? "").ToLowerInvariant(), @"[^\p{L}\p{Nd}]", ""); }

    private static void AddRod(TrackRes o, Dictionary<string, object> leader, string path, DateTime time, string rodId)
    {
        lock (gate)
        {
            Dictionary<string, object> s = Cur(true);
            List<Dictionary<string, object>> rods = L(s, "rods");
            string rig = Regex.Split(o.Rig, @"\s*\|\s*")[0];
            string len = leader != null ? leader["cm"].ToString() : "";
            Dictionary<string, object> nr = AppEnv.Obj("id", Guid.NewGuid().ToString("N").Substring(0, 6), "time", time.ToString("o", CultureInfo.InvariantCulture), "rig", rig, "baits", o.Baits.ToArray(),
                "dip", o.Dip, "pva", o.Pva, "tech", o.Tech, "leaderCm", leader != null ? leader["cm"] : null, "material", leader != null ? leader["material"] : "", "kg", leader != null ? leader["kg"] : "", "path", path);
            string target = rodId ?? (st.ContainsKey("replace") ? S(st, "replace") : null);
            int idx = target != null ? rods.FindIndex(x => S(x, "id") == target) : -1;
            if (idx < 0)
            {
                DateTime sStart = Time(s["start"]);
                string sig = rig + "|" + string.Join("+", o.Baits.ToArray()) + "|" + o.Dip + "|" + len;
                for (int i = 0; i < rods.Count; i++)
                {
                    Dictionary<string, object> x = rods[i];
                    string xs = S(x, "rig") + "|" + string.Join("+", Strs(x["baits"]).ToArray()) + "|" + S(x, "dip") + "|" + S(x, "leaderCm");
                    bool inherited = AppEnv.GetBool(x, "inh");
                    bool sameLen = len.Length > 0 && S(x, "leaderCm") == len;
                    bool sameBaits = string.Join("+", Strs(x["baits"]).ToArray()) == string.Join("+", o.Baits.ToArray());
                    if (inherited && (xs == sig || sameLen || sameBaits)) { idx = i; break; }
                }
            }
            if (idx >= 0) { nr["id"] = rods[idx]["id"]; rods[idx] = nr; } else rods.Add(nr);
            st.Remove("replace");
            Save();
            int n = (idx >= 0 ? idx : rods.Count-1) + 1;
            Say(string.Format(T("trkRod", "Rute {0} erfasst: {1}"), n, string.Join(" + ", o.Baits.Select(b => GameData.Translate(b, UserStore.Lang)).ToArray())) + (len.Length > 0 ? ", " + T("leader", "Vorfach") + " " + len + " cm" : ""));
        }
    }

    private static string FindRod(Dictionary<string, object> s, List<string> baits, DateTime time)
    {
        List<Dictionary<string, object>> rods = L(s, "rods");
        if (baits != null && baits.Count > 0)
        {
            List<string> keys = baits.Select(Key).Where(k => k.Length > 0).ToList();
            List<Dictionary<string, object>> hits = rods.Where(r =>
            {
                List<string> rk = Strs(r["baits"]).Select(Key).ToList();
                return keys.All(k => rk.Any(x => x.Contains(k) || k.Contains(x)));
            }).ToList();
            if (hits.Count == 1 || (hits.Count > 1 && hits.Select(h => S(h, "leaderCm")).Distinct().Count() == 1)) return S(hits[0], "id");
        }
        Dictionary<string, object> hit = L(s, "hits").Where(h => Math.Abs(time.Subtract(Time(h["t"])).TotalMinutes) <= 2.5).OrderBy(h => Math.Abs(time.Subtract(Time(h["t"])).TotalMinutes)).FirstOrDefault();
        if (hit != null)
        {
            int ri = System.Convert.ToInt32(hit["rod"])-1;
            if (ri >= 0 && ri < rods.Count) return S(rods[ri], "id");
        }
        return rods.Count == 1 ? S(rods[0], "id") : "";
    }

    private static bool Impossible(string fish, int? w)
    {
        int[] t;
        return w != null && GameData.Trophies.TryGetValue(fish, out t) && t[1] > 0 && w.Value > t[1] * 1.6;
    }

    private static bool AddCatch(Dictionary<string, object> s, string fish, int? weight, List<string> baits, DateTime time, string path, string src)
    {
        if (string.IsNullOrEmpty(fish) || Impossible(fish, weight)) return false;
        List<Dictionary<string, object>> pending = L(st, "pending");
        Dictionary<string, object> same = pending.FirstOrDefault(p => S(p, "fish") == fish && weight != null && S(p, "weight") == weight.Value.ToString(CultureInfo.InvariantCulture));
        if (same != null)
        {
            if (baits != null && baits.Count > 0) { same["baits"] = baits.ToArray(); string rid = FindRod(s, baits, time); if (rid.Length > 0) same["rod"] = rid; }
            Say(T("trackerCatch", "Fang erkannt") + ": " + GameData.Translate(fish, UserStore.Lang));
            return false;
        }
        if (weight != null && Fangbuch.HasCatch(fish, weight.Value, time.ToString("yyyy-MM-dd"))) return false;
        pending.Add(AppEnv.Obj("id", Guid.NewGuid().ToString("N").Substring(0, 8), "time", time.ToString("o", CultureInfo.InvariantCulture), "fish", fish, "weight", weight,
            "session", s["id"], "rod", FindRod(s, baits, time), "baits", baits != null ? baits.ToArray() : new string[0], "src", src, "path", path));
#if PRIVATE
        Ext.RaiseCatch(fish, weight, time, src);
#endif
        Say(T("trackerCatch", "Fang erkannt") + ": " + GameData.Translate(fish, UserStore.Lang) + (weight != null ? " " + GameData.FormatWeight(weight.Value, UserStore.Lang) : ""));
        return true;
    }

    private static void Keepnet(string path, string tag, string code, TrackRes r, DateTime time)
    {
        OcrRead r2 = Ocr(path, tag, new RectangleF(0.14f, 0.11f, 0.845f, 0.86f), 100, false);
        Dictionary<TrackItem, DateTime> times = new Dictionary<TrackItem, DateTime>();
        List<TrackItem> items = TrackerParse.Cards(r2.Boxes, code, time, times);
        if (items.Count == 0) items = TrackerParse.Merge(r.Items, TrackerParse.KeepnetItems(r2.Lines(), r2.Boxes, TrackerParse.Lists(code)));
        items = items.Where(i => !Impossible(i.Fish, i.Weight)).ToList();
        lock (gate)
        {
            Dictionary<string, object> s = Cur(true);
            List<Dictionary<string, object>> kn = L(s, "keepnet");
            if (Math.Abs(time.Subtract(Time(s["keepnetTime"])).TotalMinutes) > 5) kn.Clear();
            int added = 0;
            Dictionary<string, int> known = new Dictionary<string, int>();
            foreach (TrackItem it in items)
            {
                string tt = times.ContainsKey(it) ? times[it].ToString("o", CultureInfo.InvariantCulture) : "";
                Dictionary<string, object> ex = kn.FirstOrDefault(k => S(k, "fish") == it.Fish && it.Weight != null && S(k, "weight") == it.Weight.Value.ToString(CultureInfo.InvariantCulture));
                if (ex != null) { if (tt.Length > 0 && S(ex, "t").Length == 0) ex["t"] = tt; }
                else kn.Add(AppEnv.Obj("fish", it.Fish, "weight", it.Weight, "t", tt));
                if (AddCatch(s, it.Fish, it.Weight, null, times.ContainsKey(it) ? times[it] : time, path, "keepnet")) added++;
            }
            s["keepnetTime"] = time.ToString("o", CultureInfo.InvariantCulture);
            Save();
            Say(r.Total != null ? string.Format(T("trackerKeepnetOf", "Setzkescher: {0} von {1} erkannt, {2} neu"), items.Count, r.Total, added) : T("trackerKeepnet", "Setzkescher erkannt, neue Fänge") + ": " + added);
        }
    }

    private static void HotkeyLoop()
    {
        try
        {
            HotWindow w = new HotWindow();
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 1000;
            t.Tick += delegate { w.Sync(On); };
            t.Start();
            Application.Run();
        }
        catch (Exception ex) { AppEnv.Log("Tracker", "Tastenkürzel: " + ex.Message); }
    }

    private sealed class HotWindow : NativeWindow
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        private bool active;

        public HotWindow() { CreateHandle(new CreateParams()); }

        public void Sync(bool on)
        {
            if (on == active) return;
            active = on;
            int[] ids = { 1, 2, 3, 10 };
            foreach (int id in ids)
            {
                if (!on) { UnregisterHotKey(Handle, id); continue; }
                if (!RegisterHotKey(Handle, id, 0x4003, (uint)(id == 10 ? 0x30 : 0x30 + id))) AppEnv.Log("Tracker", "Tastenkürzel AltGr+" + (id % 10) + " belegt");
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312) { int id = m.WParam.ToInt32(); if (id == 10) UndoHit(); else Hit(id); }
            base.WndProc(ref m);
        }
    }

    public static void SkipBacklog()
    {
        lock (gate) { Load(); st["since"] = Now(); Save(); }
    }

    public static void Hit(int rod)
    {
        lock (gate)
        {
            Dictionary<string, object> s = Cur(true);
            int n = L(s, "rods").Count;
            if (rod > n) { System.Media.SystemSounds.Hand.Play(); Say(string.Format(T("trkHitNoRod", "Rute {0} ist in dieser Session nicht erfasst."), rod)); return; }
            L(s, "hits").Add(AppEnv.Obj("t", Now(), "rod", rod));
#if PRIVATE
            Ext.RaiseHit(rod, DateTime.Now);
#endif
            Save();
            System.Media.SystemSounds.Asterisk.Play();
            Say(string.Format(T("trkHit", "Fang auf Rute {0} gezählt, {1} auf dieser Rute."), rod, L(s, "hits").Count(h => System.Convert.ToInt32(h["rod"]) == rod)));
        }
    }

    public static void UndoHit()
    {
        lock (gate)
        {
            Dictionary<string, object> s = Cur(true);
            List<Dictionary<string, object>> hs = L(s, "hits");
            if (hs.Count == 0) { System.Media.SystemSounds.Hand.Play(); return; }
            Dictionary<string, object> last = hs.OrderBy(h => Time(h["t"])).Last();
            hs.Remove(last);
            Save();
            System.Media.SystemSounds.Exclamation.Play();
            Say(string.Format(T("trkHitUndo", "Letzter Fang auf Rute {0} zurückgenommen."), last["rod"]));
        }
    }

    public static object Command(string cmd, Dictionary<string, object> a)
    {
        string id = AppEnv.GetString(a, "id");
        lock (gate)
        {
            Load();
            Dictionary<string, object> s = Cur(cmd != "status");
            switch (cmd)
            {
                case "on":
                    UserStore.Set("trackerOn", AppEnv.GetBool(a, "on"));
                    if (AppEnv.GetBool(a, "on") && !st.ContainsKey("since")) st["since"] = Now();
                    Save();
                    break;
                case "settings":
                    if (a.ContainsKey("dir")) UserStore.Set("trackerDir", AppEnv.GetString(a, "dir").Trim());
                    if (a.ContainsKey("lang")) UserStore.Set("trackerLang", AppEnv.GetString(a, "lang"));
                    Save();
                    break;
                case "newspot": NewSession(s, false); Save(); Say(T("newSpotStarted", "Neuer Spot, neue Session gestartet. Die Ruten sind übernommen.")); break;
                case "newsession": NewSession(s, true); Save(); Say(T("trkNewSession", "Neue Session am selben Spot mit denselben Ruten.")); break;
                case "clearrods": L(s, "rods").Clear(); Save(); Say(T("trkRodsCleared", "Ruten geleert. Jetzt jede Rute einmal mit F12 erfassen.")); break;
                case "rod/delete": L(s, "rods").RemoveAll(r => S(r, "id") == id); Save(); break;
                case "rod/replace": if (S(st, "replace") == id) st.Remove("replace"); else st["replace"] = id; Save(); break;
                case "rod/reread":
                    {
                        Dictionary<string, object> rod = L(s, "rods").FirstOrDefault(r => S(r, "id") == id);
                        string p = rod != null ? S(rod, "path") : "";
                        if (p.Length == 0 || !File.Exists(p)) throw new AppError("trkNoShot", null);
                        ThreadPool.QueueUserWorkItem(delegate { Process(p, id); });
                        break;
                    }
                case "session":
                    if (a.ContainsKey("lake")) s["lake"] = AppEnv.GetString(a, "lake");
                    if (a.ContainsKey("clip")) s["clip"] = AppEnv.GetString(a, "clip").Trim();
                    int x, y;
                    if (int.TryParse(AppEnv.GetString(a, "x"), out x) && int.TryParse(AppEnv.GetString(a, "y"), out y)) { s["x"] = x; s["y"] = y; }
                    Save();
                    break;
                case "pending/rod":
                    foreach (Dictionary<string, object> p in L(st, "pending").Where(p => S(p, "id") == id)) p["rod"] = AppEnv.GetString(a, "rod");
                    Save();
                    break;
                case "pending/discard":
                    {
                        HashSet<string> ids = new HashSet<string>(Strs(a.ContainsKey("ids") ? a["ids"] : null));
                        L(st, "pending").RemoveAll(p => ids.Contains(S(p, "id")));
                        Save();
                        break;
                    }
                case "pending/accept":
                    {
                        HashSet<string> ids = new HashSet<string>(Strs(a.ContainsKey("ids") ? a["ids"] : null));
                        List<Dictionary<string, object>> take = L(st, "pending").Where(p => ids.Contains(S(p, "id"))).ToList();
                        List<Dictionary<string, object>> outC = new List<Dictionary<string, object>>();
                        foreach (Dictionary<string, object> p in take)
                        {
                            Dictionary<string, object> ses = L(st, "sessions").FirstOrDefault(x2 => S(x2, "id") == S(p, "session")) ?? s;
                            Dictionary<string, object> rod = L(ses, "rods").FirstOrDefault(r => S(r, "id") == S(p, "rod"));
                            List<string> baits = Strs(p["baits"]);
                            if (baits.Count == 0 && rod != null) baits = Strs(rod["baits"]);
                            outC.Add(AppEnv.Obj("date", Time(p["time"]).ToString("yyyy-MM-dd"), "lake", S(ses, "lake"), "fish", S(p, "fish"), "weight", p["weight"],
                                "bait", baits.Count > 0 ? baits[0] : "", "bait2", baits.Count > 1 ? baits[1] : "", "dip", rod != null ? S(rod, "dip") : "", "pva", rod != null ? S(rod, "pva") : "",
                                "rig", rod != null ? S(rod, "rig") : "", "x", ses["x"], "y", ses["y"], "clip", S(ses, "clip"), "depth", "", "spotId", "", "notes", "Tracker",
                                "temp", S(ses, "temp"), "tech", rod != null ? S(rod, "tech") : ""));
                        }
                        Fangbuch.AddCatches(outC);
                        L(st, "pending").RemoveAll(p => ids.Contains(S(p, "id")));
                        Save();
                        Say(string.Format(T("trkAccepted", "{0} Fänge ins Fangbuch übernommen."), outC.Count));
                        break;
                    }
            }
            return Status(s);
        }
    }

    private static object Status(Dictionary<string, object> s)
    {
        string lang = UserStore.Lang;
        Func<IEnumerable<string>, string> tr = b => string.Join(" + ", b.Select(x => GameData.Translate(x, lang)).ToArray());
        List<object> rods = new List<object>();
        if (s != null)
        {
            List<Dictionary<string, object>> rl = L(s, "rods");
            for (int i = 0; i < rl.Count; i++)
            {
                Dictionary<string, object> r = rl[i];
                int n = i + 1;
                rods.Add(AppEnv.Obj("id", S(r, "id"), "n", n, "rig", S(r, "rig"), "baits", tr(Strs(r["baits"])), "dip", GameData.Translate(S(r, "dip"), lang), "pva", S(r, "pva"),
                    "leader", S(r, "leaderCm").Length > 0 ? S(r, "leaderCm") + " cm" : "", "hits", L(s, "hits").Count(h => System.Convert.ToInt32(h["rod"]) == n),
                    "time", Time(r["time"]).ToString("HH:mm"), "hasShot", File.Exists(S(r, "path")), "replace", S(st, "replace") == S(r, "id"),
                    "catches", L(st, "pending").Count(p => S(p, "rod") == S(r, "id"))));
            }
        }
        List<object> pend = L(st, "pending").OrderByDescending(p => S(p, "time")).Select(p => (object)AppEnv.Obj(
            "id", S(p, "id"), "time", Time(p["time"]).ToString("dd.MM. HH:mm"), "fish", GameData.Translate(S(p, "fish"), lang),
            "weight", p["weight"] != null ? GameData.FormatWeight(System.Convert.ToDouble(p["weight"]), lang) : "", "rod", S(p, "rod"),
            "baits", tr(Strs(p["baits"])), "src", S(p, "src"), "other", s == null || S(p, "session") != S(s, "id"))).ToList();
        Lake lk = null;
        if (s != null) GameData.LakeById.TryGetValue(S(s, "lake"), out lk);
        return AppEnv.Obj(
            "on", On || PauseReason.Length > 0, "paused", PauseReason, "dir", Dir, "dirOk", Directory.Exists(Dir), "lang", LangTag, "langs", TrackerOcr.Languages(), "busy", busyFile,
            "msg", msg, "msgAt", msgAt == DateTime.MinValue ? "" : msgAt.ToString("HH:mm:ss"),
            "session", s == null ? null : AppEnv.Obj("id", S(s, "id"), "start", Time(s["start"]).ToString("dd.MM. HH:mm"), "lake", S(s, "lake"), "lakeName", lk != null ? lk.Name(lang) : "",
                "x", s["x"], "y", s["y"], "clip", S(s, "clip"), "temp", S(s, "temp").Length > 0 ? T("temp_" + S(s, "temp"), S(s, "temp")) : "", "keepnet", L(s, "keepnet").Count, "hits", L(s, "hits").Count),
            "rods", rods, "pending", pend);
    }
}
