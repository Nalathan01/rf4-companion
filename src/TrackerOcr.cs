using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

internal sealed class OcrBox
{
    public int X, Y, W, H;
    public string Text;
}

internal sealed class OcrRead
{
    public List<OcrBox> Boxes = new List<OcrBox>();
    public double F = 1;
    public int SrcW, SrcH;

    public string[] Lines()
    {
        List<string> l = new List<string>();
        foreach (OcrBox b in Boxes) l.Add(b.Text);
        return l.ToArray();
    }
}

internal static class TrackerOcr
{
    public static OcrRead Read(string path, string lang, RectangleF crop, double maxZoom, bool binarize)
    {
        byte[] png;
        OcrRead res = new OcrRead();
        using (Bitmap src = LoadBitmap(path))
        {
            int w = src.Width, h = src.Height;
            res.SrcW = w; res.SrcH = h;
            Rectangle r = crop.IsEmpty ? new Rectangle(0, 0, w, h) : Rect(crop, w, h);
            double max = OcrEngine.MaxImageDimension;
            double f;
            if (!crop.IsEmpty) f = Math.Min(Math.Min(max / r.Width, max / r.Height), maxZoom);
            else if (w > max || h > max || w < 1800) f = Math.Min(Math.Min(max / w, max / h), w < 1800 ? 2.0 : 1.0);
            else f = 1.0;
            res.F = f;
            using (Bitmap bmp = new Bitmap(Math.Max(1, (int)(r.Width * f)), Math.Max(1, (int)(r.Height * f)), PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, new Rectangle(0, 0, bmp.Width, bmp.Height), r, GraphicsUnit.Pixel);
                }
                if (binarize) Binarize(bmp);
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    png = ms.ToArray();
                }
            }
        }
        res.Boxes = Recognize(png, lang);
        return res;
    }

    public static string[] Languages()
    {
        List<string> l = new List<string>();
        foreach (Windows.Globalization.Language g in OcrEngine.AvailableRecognizerLanguages) l.Add(g.LanguageTag);
        return l.ToArray();
    }

    private static Bitmap LoadBitmap(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        using (MemoryStream ms = new MemoryStream(b))
        using (Image img = Image.FromStream(ms))
            return new Bitmap(img);
    }

    private static Rectangle Rect(RectangleF c, int w, int h)
    {
        if (c.Width > 1 || c.Height > 1)
        {
            int x = Math.Max(0, (int)c.X), y = Math.Max(0, (int)c.Y);
            return new Rectangle(x, y, Math.Max(1, Math.Min(w-x, (int)c.Width)), Math.Max(1, Math.Min(h-y, (int)c.Height)));
        }
        return new Rectangle((int)(w * c.X), (int)(h * c.Y), Math.Max(1, (int)(w * c.Width)), Math.Max(1, (int)(h * c.Height)));
    }

    private static void Binarize(Bitmap bmp)
    {
        Rectangle all = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData d = bmp.LockBits(all, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int n = d.Stride * d.Height;
        byte[] px = new byte[n];
        Marshal.Copy(d.Scan0, px, 0, n);
        for (int i = 0; i + 3 < n; i += 4)
        {
            int b = px[i], g = px[i + 1], r = px[i + 2];
            int v = Math.Min(r, Math.Min(g, b)), sat = Math.Max(r, Math.Max(g, b))-v;
            byte o = (byte)(v > 95 && sat < 45 ? 0 : 255);
            px[i] = o; px[i + 1] = o; px[i + 2] = o; px[i + 3] = 255;
        }
        Marshal.Copy(px, 0, d.Scan0, n);
        bmp.UnlockBits(d);
    }

    private static List<OcrBox> Recognize(byte[] png, string lang)
    {
        List<OcrBox> outL = new List<OcrBox>();
        OcrEngine eng = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(lang));
        if (eng == null) eng = OcrEngine.TryCreateFromUserProfileLanguages();
        if (eng == null) return outL;
        InMemoryRandomAccessStream ms = new InMemoryRandomAccessStream();
        DataWriter w = new DataWriter(ms);
        w.WriteBytes(png);
        ImageOcr.W(w.StoreAsync());
        w.DetachStream();
        ms.Seek(0);
        BitmapDecoder dec = ImageOcr.W(BitmapDecoder.CreateAsync(ms));
        SoftwareBitmap sb = ImageOcr.W(dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied));
        OcrResult res = ImageOcr.W(eng.RecognizeAsync(sb));
        foreach (OcrLine ln in res.Lines)
        {
            double x1 = 1e9, y1 = 1e9, x2 = 0, y2 = 0;
            foreach (OcrWord wd in ln.Words)
            {
                Windows.Foundation.Rect b = wd.BoundingRect;
                x1 = Math.Min(x1, b.X); y1 = Math.Min(y1, b.Y);
                x2 = Math.Max(x2, b.X + b.Width); y2 = Math.Max(y2, b.Y + b.Height);
            }
            OcrBox o = new OcrBox();
            o.X = (int)x1; o.Y = (int)y1; o.W = (int)(x2-x1); o.H = (int)(y2-y1); o.Text = ln.Text;
            outL.Add(o);
        }
        return outL;
    }
}
