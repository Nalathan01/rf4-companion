using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

internal static class LiveCatch
{
    private static readonly object gate = new object();
    private static List<Dictionary<string, object>> list;
    private static string lastKey = "";
    private static DateTime lastAt = DateTime.MinValue;
    private static volatile string state = "off";
    private static DateTime lastCheck = DateTime.MinValue;
    private static DateTime lastKeepnet = DateTime.MinValue;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    private static string FilePath { get { return AppEnv.DataPath("live_catches.json"); } }

    public static bool On
    {
        get { object v = UserStore.Get("liveOn"); return v is bool && (bool)v; }
    }

    public static string Lang
    {
        get { string l = (UserStore.Get("trackerLang") ?? "").ToString(); return l.Length > 0 ? l : "de-DE"; }
    }

    private static void Load()
    {
        if (list != null) return;
        list = new List<Dictionary<string, object>>();
        try
        {
            System.Collections.IEnumerable e = AppEnv.ReadJsonFile(FilePath) as System.Collections.IEnumerable;
            if (e != null) foreach (object o in e) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d != null) list.Add(d); }
        }
        catch (Exception ex) { AppEnv.Log("Live", ex.Message); }
    }

    private static void Save()
    {
        try { AppEnv.WriteJsonFile(FilePath, list); } catch (Exception ex) { AppEnv.Log("Live", ex.Message); }
    }

    public static void Start()
    {
        lock (gate) Load();
        Thread t = new Thread(Loop);
        t.IsBackground = true;
        t.Priority = ThreadPriority.BelowNormal;
        t.Start();
    }

    private static void Loop()
    {
        while (true)
        {
            Thread.Sleep(1500);
            if (!On) { state = "off"; continue; }
            try { Tick(); } catch (Exception ex) { state = "error"; AppEnv.Log("Live", ex.Message); }
        }
    }

    private static IntPtr GameWindow()
    {
        foreach (Process p in Process.GetProcessesByName("rf4_x64"))
        {
            try { if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle; }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
        return IntPtr.Zero;
    }

    private static void Tick()
    {
        IntPtr h = GameWindow();
        if (h == IntPtr.Zero) { state = "noGame"; return; }
        if (IsIconic(h)) { state = "minimized"; return; }
        RECT r;
        if (!GetClientRect(h, out r) || r.Right < 400 || r.Bottom < 300) { state = "minimized"; return; }
        Rectangle crop = new Rectangle((int)(r.Right * 0.28), (int)(r.Bottom * 0.02), (int)(r.Right * 0.44), (int)(r.Bottom * 0.18));
        string[] lines = ReadLines(h, r, crop);
        if (lines == null) { state = "noImage"; return; }
        state = "watching";
        lastCheck = DateTime.Now;
        if (Regex.IsMatch(string.Join(" ", lines), "Setzkescher|Keepnet|Садок|Siatka", RegexOptions.IgnoreCase))
        {
            if (DateTime.Now.Subtract(lastKeepnet).TotalSeconds >= 8) { lastKeepnet = DateTime.Now; Keepnet(h, r); }
            return;
        }
        string found = Find(lines);
        if (found.Length == 0) return;
        Thread.Sleep(300);
        string[] again = ReadLines(h, r, crop);
        if (again == null || Find(again) != found) return;
        if (found == lastKey && DateTime.Now.Subtract(lastAt).TotalMinutes < 3) { lastAt = DateTime.Now; return; }
        lastKey = found;
        lastAt = DateTime.Now;
        string[] p = found.Split('|');
        Add(p[0], int.Parse(p[1], CultureInfo.InvariantCulture), DateTime.Now);
    }

    private static string[] ReadLines(IntPtr h, RECT r, Rectangle crop)
    {
        using (Bitmap bmp = Capture(h, r.Right, r.Bottom, crop))
            return bmp == null ? null : TrackerOcr.Read(bmp, Lang, RectangleF.Empty, 2, false).Lines();
    }

    private static void Keepnet(IntPtr h, RECT r)
    {
        string code = TrackerParse.Code(Lang);
        DateTime now = DateTime.Now;
        Dictionary<TrackItem, DateTime> times = new Dictionary<TrackItem, DateTime>();
        List<TrackItem> items;
        using (Bitmap bmp = Capture(h, r.Right, r.Bottom, new Rectangle(0, 0, r.Right, r.Bottom)))
        {
            if (bmp == null) return;
            items = TrackerParse.Cards(TrackerOcr.Read(bmp, Lang, new RectangleF(0.14f, 0.11f, 0.845f, 0.86f), 3, false).Boxes, code, now, times);
        }
        foreach (TrackItem it in items)
        {
            if (it.Fish == null || it.Weight == null || it.Weight.Value <= 0 || Impossible(it.Fish, it.Weight.Value) || !times.ContainsKey(it)) continue;
            if (now.Subtract(times[it]).TotalMinutes > 60) continue;
            Add(it.Fish, it.Weight.Value, times[it]);
        }
    }

    private static bool Impossible(string fish, int grams)
    {
        int[] t;
        return GameData.Trophies.TryGetValue(fish, out t) && t[1] > 0 && grams > t[1] * 1.6;
    }

    private static void Add(string fish, int grams, DateTime time)
    {
        lock (gate)
        {
            Load();
            foreach (Dictionary<string, object> d in list)
            {
                DateTime t;
                if (AppEnv.GetString(d, "fish") == fish && (int)AppEnv.GetDouble(d, "weight", 0) == grams
                    && DateTime.TryParse(AppEnv.GetString(d, "time"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t) && Math.Abs(time.Subtract(t).TotalHours) < 3) return;
            }
            list.Add(AppEnv.Obj("fish", fish, "weight", grams, "time", time.ToString("o", CultureInfo.InvariantCulture)));
            list.Sort((x, y) => string.CompareOrdinal(AppEnv.GetString(x, "time"), AppEnv.GetString(y, "time")));
            if (list.Count > 100) list.RemoveRange(0, list.Count-100);
            Save();
        }
        AppEnv.Log("Live", "Fang " + fish + "|" + grams);
    }

    private static Bitmap Capture(IntPtr h, int w, int hgt, Rectangle crop)
    {
        Bitmap part = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(part))
        {
            if (GetForegroundWindow() == h)
            {
                POINT p = new POINT();
                ClientToScreen(h, ref p);
                g.CopyFromScreen(p.X + crop.X, p.Y + crop.Y, 0, 0, crop.Size);
                if (!Dark(part)) return part;
            }
            using (Bitmap full = new Bitmap(w, hgt, PixelFormat.Format32bppArgb))
            {
                using (Graphics fg = Graphics.FromImage(full))
                {
                    IntPtr dc = fg.GetHdc();
                    bool ok = PrintWindow(h, dc, 3);
                    fg.ReleaseHdc(dc);
                    if (!ok) { part.Dispose(); return null; }
                }
                g.DrawImage(full, new Rectangle(0, 0, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
            }
            if (!Dark(part)) return part;
        }
        part.Dispose();
        return null;
    }

    private static bool Dark(Bitmap b)
    {
        int bright = 0;
        for (int i = 1; i < 12; i++)
            for (int j = 1; j < 8; j++)
            {
                Color c = b.GetPixel(b.Width * i / 12, b.Height * j / 8);
                if (c.R + c.G + c.B > 30) bright++;
            }
        return bright < 4;
    }

    private static string Find(string[] lines)
    {
        TrackLists lists = TrackerParse.Lists(TrackerParse.Code(Lang));
        for (int i = 0; i < lines.Length; i++)
        {
            Match m = Regex.Match(lines[i], @"(\d[\d\.,]*)\s*(kg|g|кг|г|lb|lbs|公斤|克)(?![\p{L}])", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            int? w = TrackerParse.Weight(m.Groups[1].Value, m.Groups[2].Value);
            if (w == null || w.Value <= 0) continue;
            NameHit f = null;
            string before = lines[i].Substring(0, m.Index).Trim();
            if (before.Length >= 3) f = TrackerParse.MatchFish(before, lists);
            if (f == null && i > 0) f = TrackerParse.MatchFish(lines[i-1], lists);
            if (f == null || Impossible(f.En, w.Value)) continue;
            return f.En + "|" + w.Value.ToString(CultureInfo.InvariantCulture);
        }
        return "";
    }

    public static List<object[]> Recent()
    {
        lock (gate)
        {
            Load();
            List<object[]> l = new List<object[]>();
            foreach (Dictionary<string, object> d in list)
            {
                DateTime t;
                if (!DateTime.TryParse(AppEnv.GetString(d, "time"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) continue;
                l.Add(new object[] { AppEnv.GetString(d, "fish"), (int)AppEnv.GetDouble(d, "weight", 0), t });
            }
            return l;
        }
    }

    public static void Clear()
    {
        lock (gate) { Load(); list.Clear(); Save(); }
    }

    public static object Status()
    {
        string[] langs = TrackerOcr.Languages();
        int n;
        lock (gate) { Load(); n = list.Count; }
        return AppEnv.Obj("on", On, "state", state, "checked", lastCheck == DateTime.MinValue ? "" : lastCheck.ToString("HH:mm:ss"), "lang", Lang, "langs", langs,
            "langOk", langs.Any(l => l.Equals(Lang, StringComparison.OrdinalIgnoreCase)), "count", n);
    }
}
