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
    private static IntPtr win = IntPtr.Zero;
    private static DateTime winAt = DateTime.MinValue;
    private static DateTime lastFull = DateTime.MinValue;
    private static bool cardOpen;
    private static bool cardDone;
    private static volatile bool fast;
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
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);

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
            if (!On) { state = "off"; cardOpen = false; Thread.Sleep(1000); continue; }
            try { Tick(); } catch (Exception ex) { state = "error"; AppEnv.Log("Live", ex.Message); Thread.Sleep(1000); }
            Thread.Sleep(fast ? 50 : 250);
        }
    }

    private static IntPtr GameWindow()
    {
        if (win != IntPtr.Zero && DateTime.Now.Subtract(winAt).TotalSeconds < 5 && IsWindow(win)) return win;
        win = IntPtr.Zero;
        winAt = DateTime.Now;
        foreach (Process p in Process.GetProcessesByName("rf4_x64"))
        {
            try { if (win == IntPtr.Zero && p.MainWindowHandle != IntPtr.Zero) win = p.MainWindowHandle; }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
        return win;
    }

    private static void Tick()
    {
        IntPtr h = GameWindow();
        fast = false;
        if (h == IntPtr.Zero) { state = "noGame"; return; }
        if (IsIconic(h)) { state = "minimized"; return; }
        RECT r;
        if (!GetClientRect(h, out r) || r.Right < 400 || r.Bottom < 300) { state = "minimized"; return; }
        int w = r.Right, hg = r.Bottom;
        bool fg = GetForegroundWindow() == h || Visible(h, w, hg);
        fast = fg;
        if (!fg && DateTime.Now.Subtract(lastFull).TotalMilliseconds < 750) return;
        Rectangle strip = new Rectangle(0, (int)(hg * 0.05), w, (int)(hg * 0.15));
        Bitmap full = null, st = null;
        try
        {
            if (fg)
            {
                st = Grab(h, strip);
                if (Black(st)) { st.Dispose(); st = null; }
            }
            if (st == null)
            {
                full = Full(h, w, hg);
                lastFull = DateTime.Now;
                if (full == null) { state = "noImage"; return; }
                st = full.Clone(strip, PixelFormat.Format32bppArgb);
            }
            state = "watching";
            lastCheck = DateTime.Now;
            bool card, menu;
            Analyze(st, hg, out card, out menu);
            if (card)
            {
                if (!cardOpen) { cardOpen = true; cardDone = false; }
                if (cardDone) return;
                Rectangle crop = new Rectangle((int)(w * 0.28), (int)(hg * 0.02), (int)(w * 0.44), (int)(hg * 0.15));
                string found;
                using (Bitmap c = full != null ? full.Clone(crop, PixelFormat.Format32bppArgb) : Grab(h, crop))
                    found = Find(TrackerOcr.Read(c, Lang, RectangleF.Empty, 2, false).Lines());
                if (found.Length == 0) return;
                string[] p = found.Split('|');
                Add(p[0], int.Parse(p[1], CultureInfo.InvariantCulture), DateTime.Now);
                cardDone = true;
                return;
            }
            cardOpen = false;
            if (!menu || DateTime.Now.Subtract(lastKeepnet).TotalSeconds < 4) return;
            lastKeepnet = DateTime.Now;
            Rectangle title = new Rectangle((int)(w * 0.3), 0, (int)(w * 0.4), (int)(hg * 0.08));
            string[] lines;
            using (Bitmap c = full != null ? full.Clone(title, PixelFormat.Format32bppArgb) : Grab(h, title))
                lines = TrackerOcr.Read(c, Lang, RectangleF.Empty, 2, false).Lines();
            if (Regex.IsMatch(string.Join(" ", lines), "Setzkescher|Keepnet|Садок|Siatka", RegexOptions.IgnoreCase)) Keepnet(h, r);
        }
        finally
        {
            if (st != null) st.Dispose();
            if (full != null) full.Dispose();
        }
    }

    private static bool Visible(IntPtr h, int w, int hg)
    {
        foreach (double fx in new double[] { 0.1, 0.5, 0.9 })
            foreach (double fy in new double[] { 0.04, 0.18 })
            {
                POINT p = new POINT();
                ClientToScreen(h, ref p);
                p.X += (int)(w * fx);
                p.Y += (int)(hg * fy);
                if (GetAncestor(WindowFromPoint(p), 2) != h) return false;
            }
        return true;
    }

    private static Bitmap Grab(IntPtr h, Rectangle part)
    {
        Bitmap b = new Bitmap(part.Width, part.Height, PixelFormat.Format32bppArgb);
        POINT p = new POINT();
        ClientToScreen(h, ref p);
        using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(p.X + part.X, p.Y + part.Y, 0, 0, part.Size);
        return b;
    }

    private static Bitmap Full(IntPtr h, int w, int hg)
    {
        Bitmap b = new Bitmap(w, hg, PixelFormat.Format32bppArgb);
        bool ok;
        using (Graphics g = Graphics.FromImage(b))
        {
            IntPtr dc = g.GetHdc();
            ok = PrintWindow(h, dc, 3);
            g.ReleaseHdc(dc);
        }
        if (ok && !Black(b)) return b;
        b.Dispose();
        return null;
    }

    private static bool Black(Bitmap b)
    {
        for (int i = 1; i < 12; i++)
            for (int j = 1; j < 4; j++)
            {
                Color c = b.GetPixel(b.Width * i / 12, b.Height * j / 4);
                if (c.R + c.G + c.B > 9) return false;
            }
        return true;
    }

    private static void Analyze(Bitmap st, int hg, out bool card, out bool menu)
    {
        card = false;
        menu = false;
        Rectangle all = new Rectangle(0, 0, st.Width, st.Height);
        BitmapData d = st.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] px = new byte[d.Stride * d.Height];
        Marshal.Copy(d.Scan0, px, 0, px.Length);
        st.UnlockBits(d);
        int head = Math.Min(st.Height-1, (int)(hg * 0.01)), gray = 0, n = 0;
        for (int x = 0; x < st.Width; x += 4)
        {
            int i = head * d.Stride + x * 4, b = px[i], g = px[i + 1], r = px[i + 2];
            n++;
            if (r >= 110 && r <= 210 && Math.Abs(r-g) < 14 && Math.Abs(g-b) < 14) gray++;
        }
        menu = gray >= n * 0.85;
        int y0 = (int)(hg * 0.115), y1 = Math.Min(st.Height-1, (int)(hg * 0.145)), k = Math.Max(3, (int)(hg * 0.006));
        for (int y = y0; y <= y1 && !card; y++)
        {
            int[] c = Colors(px, d.Stride, st.Width, y);
            int big = (c[0] >= c[3] * 0.04 ? 1 : 0) + (c[1] >= c[3] * 0.04 ? 1 : 0) + (c[2] >= c[3] * 0.04 ? 1 : 0);
            if (c[0] + c[1] + c[2] < c[3] * 0.4 || big < 2) continue;
            int[] up = Colors(px, d.Stride, st.Width, Math.Max(0, y-k)), dn = Colors(px, d.Stride, st.Width, Math.Min(st.Height-1, y + k));
            card = up[0] + up[1] + up[2] < up[3] * 0.15 && dn[0] + dn[1] + dn[2] < dn[3] * 0.15;
        }
    }

    private static int[] Colors(byte[] px, int stride, int width, int y)
    {
        int cg = 0, co = 0, cb = 0, m = 0;
        for (int x = 0; x < width; x += 2)
        {
            int i = y * stride + x * 4, b = px[i], g = px[i + 1], r = px[i + 2];
            m++;
            if (g > 150 && r < 190 && b < 110 && g-r > 30) cg++;
            else if (r > 200 && g > 120 && g < 200 && b < 80) co++;
            else if (b > 180 && r < 130 && b-r > 80) cb++;
        }
        return new int[] { cg, co, cb, m };
    }

    private static void Keepnet(IntPtr h, RECT r)
    {
        string code = TrackerParse.Code(Lang);
        DateTime now = DateTime.Now;
        Dictionary<TrackItem, DateTime> times = new Dictionary<TrackItem, DateTime>();
        List<TrackItem> items;
        using (Bitmap bmp = Full(h, r.Right, r.Bottom))
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
