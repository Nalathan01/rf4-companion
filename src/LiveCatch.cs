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
    private static string candidate = "";
    private static string lastKey = "";
    private static DateTime lastAt = DateTime.MinValue;
    private static volatile string state = "off";
    private static DateTime lastCheck = DateTime.MinValue;

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
        if (h == IntPtr.Zero) { state = "noGame"; candidate = ""; return; }
        if (IsIconic(h)) { state = "minimized"; return; }
        RECT r;
        if (!GetClientRect(h, out r) || r.Right < 400 || r.Bottom < 300) { state = "minimized"; return; }
        Rectangle crop = new Rectangle((int)(r.Right * 0.28), (int)(r.Bottom * 0.02), (int)(r.Right * 0.44), (int)(r.Bottom * 0.18));
        using (Bitmap bmp = Capture(h, r.Right, r.Bottom, crop))
        {
            if (bmp == null) { state = "noImage"; return; }
            state = "watching";
            lastCheck = DateTime.Now;
            Detect(TrackerOcr.Read(bmp, Lang, RectangleF.Empty, 2, false).Lines());
        }
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

    private static void Detect(string[] lines)
    {
        string code = TrackerParse.Code(Lang);
        TrackLists lists = TrackerParse.Lists(code);
        string found = "";
        for (int i = 0; i < lines.Length && found.Length == 0; i++)
        {
            Match m = Regex.Match(lines[i], @"(\d[\d\.,]*)\s*(kg|g|кг|г|lb|lbs|公斤|克)(?![\p{L}])", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            int? w = TrackerParse.Weight(m.Groups[1].Value, m.Groups[2].Value);
            if (w == null || w.Value <= 0) continue;
            NameHit f = null;
            string before = lines[i].Substring(0, m.Index).Trim();
            if (before.Length >= 3) f = TrackerParse.MatchFish(before, lists);
            if (f == null && i > 0) f = TrackerParse.MatchFish(lines[i-1], lists);
            if (f == null) continue;
            int[] t;
            if (GameData.Trophies.TryGetValue(f.En, out t) && t[1] > 0 && w.Value > t[1] * 1.6) continue;
            found = f.En + "|" + w.Value.ToString(CultureInfo.InvariantCulture);
        }
        if (found.Length == 0) { candidate = ""; return; }
        if (found != candidate) { candidate = found; return; }
        if (found == lastKey && DateTime.Now.Subtract(lastAt).TotalMinutes < 3) { lastAt = DateTime.Now; return; }
        lastKey = found;
        lastAt = DateTime.Now;
        string[] p = found.Split('|');
        lock (gate)
        {
            Load();
            list.Add(AppEnv.Obj("fish", p[0], "weight", int.Parse(p[1], CultureInfo.InvariantCulture), "time", DateTime.Now.ToString("o", CultureInfo.InvariantCulture)));
            if (list.Count > 100) list.RemoveRange(0, list.Count-100);
            Save();
        }
        AppEnv.Log("Live", "Fang " + found);
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
