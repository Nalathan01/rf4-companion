using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

internal static class Privacy
{
    public static readonly DateTime Since = DateTime.UtcNow;

    private static readonly Regex PathRx = new Regex(@"[A-Za-z]:\\|\\Users\\|Screenshots|rf4_\d|\.png\b", RegexOptions.IgnoreCase);

    public static void Clean(string path, string lang, Dictionary<string, object> analysis)
    {
        List<Rectangle> rects = new List<Rectangle>();
        int w, h;
        bool hud = analysis != null && AppEnv.GetBool(analysis, "hud");
        using (Bitmap src = Load(path))
        {
            w = src.Width;
            h = src.Height;
            if (!hud) rects.Add(Rectangle.FromLTRB((int)(w * 0.835), (int)(h * 0.905), w, (int)(h * 0.995)));
            Rectangle blue = NoticeBox(src);
            if (!blue.IsEmpty) rects.Add(blue);
        }
        try
        {
            RectangleF crop = new RectangleF(0.6f, 0.78f, 0.4f, 0.22f);
            OcrRead r = TrackerOcr.Read(path, lang, crop, 3, false);
            foreach (OcrBox b in r.Boxes.Where(b => PathRx.IsMatch(b.Text ?? "")))
            {
                int x = (int)(crop.X * w + b.X / r.F), y = (int)(crop.Y * h + b.Y / r.F), bw = (int)(b.W / r.F), bh = (int)(b.H / r.F);
                rects.Add(Rectangle.Inflate(new Rectangle(x, y, bw, bh), bh, bh));
            }
        }
        catch (Exception ex) { AppEnv.Log("Datenschutz", ex.Message); }
        List<Rectangle> areas = new List<Rectangle>();
        if (rects.Count > 0) areas.Add(rects.Aggregate(Rectangle.Union));
        if (hud) areas.Add(Rectangle.FromLTRB(0, (int)(h * 0.4), (int)(w * 0.33), (int)(h * 0.875)));
        areas = areas.Select(a => Rectangle.Intersect(a, new Rectangle(0, 0, w, h))).Where(a => !a.IsEmpty).ToList();
        if (areas.Count == 0) return;
        string tmp = path + ".tmp";
        using (Bitmap bmp = Load(path))
        {
            foreach (Rectangle a in areas) Pixelate(bmp, a, Math.Max(8, h / 90));
            bmp.Save(tmp, path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Png : ImageFormat.Jpeg);
        }
        File.Delete(path);
        File.Move(tmp, path);
    }

    private static Bitmap Load(string path)
    {
        using (FileStream fs = File.OpenRead(path))
        using (Image img = Image.FromStream(fs))
            return new Bitmap(img);
    }

    private static Rectangle NoticeBox(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int x0 = (int)(w * 0.7), y0 = (int)(h * 0.82);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, n = 0;
        for (int y = y0; y < h; y += 2)
            for (int x = x0; x < w; x += 2)
            {
                Color c = bmp.GetPixel(x, y);
                if (c.B < 150 || c.B-c.R < 70 || c.G < 100 || c.G > c.B) continue;
                n++;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        int area = (maxX-minX) * (maxY-minY);
        if (n < 400 || area <= 0 || n * 4 < area * 0.5 || maxX-minX < w * 0.08 || maxY-minY < h * 0.03) return Rectangle.Empty;
        return Rectangle.FromLTRB(minX-4, minY-4, maxX + 6, maxY + 6);
    }

    private static void Pixelate(Bitmap bmp, Rectangle r, int block)
    {
        using (Bitmap small = new Bitmap(Math.Max(1, r.Width / block), Math.Max(1, r.Height / block)))
        {
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(bmp, new Rectangle(0, 0, small.Width, small.Height), r, GraphicsUnit.Pixel);
            }
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(small, r, new Rectangle(0, 0, small.Width, small.Height), GraphicsUnit.Pixel);
            }
        }
    }
}
