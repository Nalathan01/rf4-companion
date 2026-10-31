using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;

internal sealed class SeaMark
{
    public string Key;
    public int Depth;
    public bool Bank;
    public double X, Y;
    public string Grid;
    public bool Unique;
}

internal static class SeaMarks
{
    private const string SeaLake = "norwegian_sea";
    private const double Radius = 60;
    private static List<SeaMark> marks;
    private static Dictionary<string, SeaMark> byKey;

    private static void Ensure()
    {
        if (marks != null) return;
        List<SeaMark> l = new List<SeaMark>();
        Dictionary<string, SeaMark> k = new Dictionary<string, SeaMark>(StringComparer.Ordinal);
        try
        {
            Dictionary<string, object> d = AppEnv.Json.DeserializeObject(AppEnv.ResourceText("data/sea_marks.json")) as Dictionary<string, object>;
            Dictionary<int, int> counts = new Dictionary<int, int>();
            foreach (object o in AppEnv.GetList(d, "marks"))
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m == null) continue;
                SeaMark s = new SeaMark();
                s.Depth = (int)AppEnv.GetDouble(m, "depth", 0);
                s.Bank = AppEnv.GetBool(m, "bank");
                s.X = AppEnv.GetDouble(m, "x", 0);
                s.Y = AppEnv.GetDouble(m, "y", 0);
                s.Grid = AppEnv.GetString(m, "grid");
                l.Add(s);
                int c; counts.TryGetValue(s.Depth, out c); counts[s.Depth] = c + 1;
            }
            foreach (SeaMark s in l)
            {
                s.Unique = counts[s.Depth] == 1;
                s.Key = s.Depth.ToString(CultureInfo.InvariantCulture) + (s.Unique ? "" : "@" + s.Grid);
                k[s.Key] = s;
            }
        }
        catch (Exception ex) { AppEnv.Log("Meer", "sea_marks.json: " + ex.Message); }
        byKey = k;
        marks = l;
    }

    public static SeaMark Nearest(string lake, double x, double y)
    {
        if (!string.Equals(lake, SeaLake, StringComparison.OrdinalIgnoreCase)) return null;
        Ensure();
        SeaMark best = null;
        double bd = Radius;
        foreach (SeaMark m in marks)
        {
            double d = Math.Sqrt((m.X-x) * (m.X-x) + (m.Y-y) * (m.Y-y));
            if (m.Bank) d *= 0.7;
            if (d < bd) { bd = d; best = m; }
        }
        return best;
    }

    public static bool IsSea(string lake)
    {
        return string.Equals(lake, SeaLake, StringComparison.OrdinalIgnoreCase);
    }

    public static SeaMark Pit(int depth)
    {
        Ensure();
        SeaMark bank = null, any = null;
        int banks = 0, all = 0;
        foreach (SeaMark m in marks)
        {
            if (m.Depth != depth) continue;
            all++; any = m;
            if (m.Bank) { banks++; bank = m; }
        }
        if (banks == 1) return bank;
        return all == 1 ? any : null;
    }

    public static bool OnLand(string lake, double x, double y)
    {
        if (!IsSea(lake)) return false;
        Lake l;
        if (!GameData.LakeById.TryGetValue(lake, out l) || !l.HasBounds) return false;
        if (x > 575 && y > 870) return false;
        EnsureLand();
        if (land == null) return false;
        int px = (int)((x-l.XMin) / (l.XMax-l.XMin) * LandSize);
        int py = (int)((l.YNorth-y) / (l.YNorth-l.YSouth) * LandSize);
        if (px < 0 || py < 0 || px >= LandSize || py >= LandSize) return false;
        return land[py * LandSize + px];
    }

    private const int LandSize = 256;
    private static bool[] land;
    private static bool landTried;

    private static void EnsureLand()
    {
        if (landTried) return;
        landTried = true;
        try
        {
            byte[] img = AppEnv.Resource("maps/" + SeaLake + ".jpg");
            if (img == null) return;
            using (MemoryStream ms = new MemoryStream(img))
            using (Image src = Image.FromStream(ms))
            using (Bitmap b = new Bitmap(src, LandSize, LandSize))
            {
                bool[] m = new bool[LandSize * LandSize];
                for (int y = 0; y < LandSize; y++)
                    for (int x = 0; x < LandSize; x++)
                    {
                        Color c = b.GetPixel(x, y);
                        m[y * LandSize + x] = c.R-c.B > 15;
                    }
                land = m;
            }
        }
        catch (Exception ex) { AppEnv.Log("Meer", "Landkarte: " + ex.Message); }
    }

    public static SeaMark Get(string key)
    {
        Ensure();
        SeaMark m;
        return key != null && byKey.TryGetValue(key, out m) ? m : null;
    }

    public static string Label(string key, Dictionary<string, object> strings)
    {
        SeaMark m = Get(key);
        if (m == null) return key ?? "";
        string fmt = m.Bank ? Str(strings, "seaBank", "{0}er Bank") : Str(strings, "seaMark", "{0}er");
        string s = string.Format(fmt, m.Depth);
        if (!m.Unique) s += " (" + m.Grid + ")";
        return s;
    }

    private static string Str(Dictionary<string, object> d, string k, string fb)
    {
        object v;
        return d != null && d.TryGetValue(k, out v) && v != null ? v.ToString() : fb;
    }
}
