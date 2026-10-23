using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

internal sealed class DirResult {
    public double Deg = -1;
    public string Kind = "";
    public double Score;
    public bool Hud;
    public bool Ok;
    public bool MapShot;
    public double Ui;
    public double UiBottom;
    public double Paper;
    public double Grey;
    public double Cx;
    public double Cy;
    public double Size;
    public int Width;
    public int Height;
}

internal static class DirDetect {
    class Comp { public double X, Y; public int N, X0, Y0, X1, Y1; public List<int> Px; }

    public static Task<DirResult> DetectAsync(byte[] data) { return Task.Run(() => Detect(data)); }

    public static DirResult DetectFile(string path) { return Detect(File.ReadAllBytes(path)); }

    public static DirResult Detect(byte[] data) {
        var res = new DirResult();
        try {
            using (var ms = new MemoryStream(data))
            using (var src = new Bitmap(ms)) {
                int w = src.Width, h = src.Height;
                double f = w > 1280 ? 1280.0 / w : 1.0;
                w = Math.Max(1, (int)(w * f)); h = Math.Max(1, (int)(h * f));
                res.Width = w; res.Height = h;
                using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb)) {
                    using (var g = Graphics.FromImage(bmp)) {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                        g.DrawImage(src, 0, 0, w, h);
                    }
                    var px = ReadPixels(bmp);
                    res.Ok = true;
                    Run(px, w, h, res);
                }
            }
        } catch { }
        return res;
    }

    static int[] ReadPixels(Bitmap bmp) {
        int w = bmp.Width, h = bmp.Height;
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var raw = new byte[bd.Stride * h];
        Marshal.Copy(bd.Scan0, raw, 0, raw.Length);
        bmp.UnlockBits(bd);
        var px = new int[w * h];
        for (int y = 0; y < h; y++) {
            int o = y * bd.Stride;
            for (int x = 0; x < w; x++) {
                int b = raw[o + x * 3], g = raw[o + x * 3 + 1], r = raw[o + x * 3 + 2];
                px[y * w + x] = (r << 16) | (g << 8) | b;
            }
        }
        return px;
    }

    static int R(int p) { return (p >> 16) & 255; }
    static int G(int p) { return (p >> 8) & 255; }
    static int B(int p) { return p & 255; }

    static bool IsBlue(int p) { int r = R(p), g = G(p), b = B(p); return b > 105 && b-r > 70 && g > 30 && g < b-25; }
    static bool IsRed(int p) { int r = R(p), g = G(p), b = B(p); return r > 100 && r-g > 55 && r-b > 60 && g < 110; }
    static bool IsArrowGreen(int p) { int r = R(p), g = G(p), b = B(p); return g > 150 && g-r > 70 && g-b > 70; }

    static List<Comp> Comps(bool[] mask, int w, int h, int minPx, bool keepPx) {
        var seen = new bool[w * h];
        var list = new List<Comp>();
        var stack = new Stack<int>();
        for (int i = 0; i < w * h; i++) {
            if (!mask[i] || seen[i]) continue;
            var c = new Comp { X0 = int.MaxValue, Y0 = int.MaxValue, X1 = -1, Y1 = -1 };
            if (keepPx) c.Px = new List<int>();
            double sx = 0, sy = 0;
            seen[i] = true; stack.Push(i);
            while (stack.Count > 0) {
                int j = stack.Pop();
                int x = j % w, y = j / w;
                sx += x; sy += y; c.N++;
                if (keepPx) c.Px.Add(j);
                if (x < c.X0) c.X0 = x;
                if (x > c.X1) c.X1 = x;
                if (y < c.Y0) c.Y0 = y;
                if (y > c.Y1) c.Y1 = y;
                if (x > 0 && mask[j-1] && !seen[j-1]) { seen[j-1] = true; stack.Push(j-1); }
                if (x < w-1 && mask[j+1] && !seen[j+1]) { seen[j+1] = true; stack.Push(j+1); }
                if (y > 0 && mask[j-w] && !seen[j-w]) { seen[j-w] = true; stack.Push(j-w); }
                if (y < h-1 && mask[j+w] && !seen[j+w]) { seen[j+w] = true; stack.Push(j+w); }
            }
            if (c.N >= minPx) { c.X = sx / c.N; c.Y = sy / c.N; list.Add(c); }
        }
        return list;
    }

    static List<Comp> MergeNear(List<Comp> cs, int w) {
        bool merged = true;
        while (merged) {
            merged = false;
            for (int i = 0; i < cs.Count && !merged; i++) for (int k = i + 1; k < cs.Count && !merged; k++) {
                var a = cs[i]; var b = cs[k];
                int sa = Math.Max(a.X1-a.X0, a.Y1-a.Y0), sb = Math.Max(b.X1-b.X0, b.Y1-b.Y0);
                if (sa > w / 6 || sb > w / 6) continue;
                int gx = Math.Max(0, Math.Max(a.X0, b.X0)-Math.Min(a.X1, b.X1)), gy = Math.Max(0, Math.Max(a.Y0, b.Y0)-Math.Min(a.Y1, b.Y1));
                double gap = Math.Sqrt(gx * gx + gy * gy);
                if (gap > Math.Max(6, 1.0 * Math.Min(Math.Max(sa, sb), 40))) continue;
                if (Math.Min(sa, sb) < 5) continue;
                var m = new Comp { Px = new List<int>(a.Px), X0 = Math.Min(a.X0, b.X0), Y0 = Math.Min(a.Y0, b.Y0), X1 = Math.Max(a.X1, b.X1), Y1 = Math.Max(a.Y1, b.Y1) };
                m.Px.AddRange(b.Px); m.N = m.Px.Count;
                m.X = (a.X * a.N + b.X * b.N) / m.N; m.Y = (a.Y * a.N + b.Y * b.N) / m.N;
                cs[i] = m; cs.RemoveAt(k); merged = true;
            }
        }
        return cs;
    }

    static List<Comp> DilComps(bool[] mask, int w, int h, int minPx, int rad) {
        var dil = new bool[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
            if (!mask[y * w + x]) continue;
            for (int dy = -rad; dy <= rad; dy++) for (int dx = -rad; dx <= rad; dx++) {
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && yy >= 0 && xx < w && yy < h) dil[yy * w + xx] = true;
            }
        }
        var cs = Comps(dil, w, h, 1, true);
        var outList = new List<Comp>();
        foreach (var c in cs) {
            var keep = new List<int>();
            double sx = 0, sy = 0;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            foreach (int j in c.Px) if (mask[j]) {
                keep.Add(j); int x = j % w, y = j / w; sx += x; sy += y;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
            if (keep.Count < minPx) continue;
            c.Px = keep; c.N = keep.Count; c.X = sx / c.N; c.Y = sy / c.N; c.X0 = x0; c.Y0 = y0; c.X1 = x1; c.Y1 = y1;
            outList.Add(c);
        }
        return outList;
    }

    static double RingScore(int[] px, int w, int h, double cx, double cy, double rad, double axis) {
        var lum = new List<double>();
        int grey = 0, tot = 0;
        for (int a = 0; a < 360; a += 5) {
            double ad = ((a-axis) % 180 + 180) % 180;
            if (ad < 20 || ad > 160) continue;
            double t = a * Math.PI / 180;
            int x = (int)Math.Round(cx + rad * Math.Sin(t)), y = (int)Math.Round(cy-rad * Math.Cos(t));
            if (x < 0 || y < 0 || x >= w || y >= h) return 0;
            tot++;
            int p = px[y * w + x];
            int r = R(p), g = G(p), b = B(p);
            int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            if (mx > 25 && mx < 200 && mx-mn < 90) { grey++; lum.Add(r); lum.Add(g); lum.Add(b); }
        }
        if (tot == 0 || lum.Count < 24) return 0;
        double mr = 0, mg = 0, mb = 0; int k = lum.Count / 3;
        for (int i = 0; i < k; i++) { mr += lum[i * 3]; mg += lum[i * 3 + 1]; mb += lum[i * 3 + 2]; }
        mr /= k; mg /= k; mb /= k;
        int near = 0;
        for (int i = 0; i < k; i++) { double dr = lum[i * 3]-mr, dg = lum[i * 3 + 1]-mg, db = lum[i * 3 + 2]-mb; if (Math.Sqrt(dr * dr + dg * dg + db * db) < 34) near++; }
        return ((double)grey / tot) * ((double)near / k);
    }

    static bool MinimapInside(int[] px, int w, int h, double cx, double cy, double rad) {
        int white = 0;
        int wr = (int)Math.Max(3, rad * 0.2);
        for (int y = (int)cy-wr; y <= (int)cy + wr; y++) for (int x = (int)cx-wr; x <= (int)cx + wr; x++) {
            if (x < 0 || y < 0 || x >= w || y >= h) continue;
            int p = px[y * w + x];
            if (R(p) > 195 && G(p) > 195 && B(p) > 195) white++;
        }
        if (white < 3) return false;
        int paper = 0, tot = 0;
        for (int a = 0; a < 360; a += 10) {
            double t = a * Math.PI / 180;
            foreach (double f in new double[] { 0.5, 0.65 }) {
                int x = (int)Math.Round(cx + rad * f * Math.Sin(t)), y = (int)Math.Round(cy-rad * f * Math.Cos(t));
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                tot++;
                int p = px[y * w + x];
                if (IsPaper(R(p), G(p), B(p))) paper++;
            }
        }
        return tot > 0 && paper >= tot * 0.5;
    }

    static bool BadgeShape(Comp c) {
        int bw = c.X1-c.X0+1, bh = c.Y1-c.Y0+1;
        if (bw < 4 || bh < 4) return false;
        if (Math.Max(bw, bh) > 2.0 * Math.Min(bw, bh)) return false;
        double fill = (double)c.N / (bw * bh);
        return fill > 0.3;
    }

    static double Norm(double d) { d %= 360; if (d < 0) d += 360; return d; }

    static void Run(int[] px, int w, int h, DirResult res) {
        int n = w * h;
        int ui = 0, ut = 0;
        for (int i = 0; i < n; i += 7) {
            ut++;
            int p = px[i]; int r0 = R(p), g0 = G(p), b0 = B(p);
            int mx0 = Math.Max(r0, Math.Max(g0, b0)), mn0 = Math.Min(r0, Math.Min(g0, b0));
            if (mx0-mn0 < 16 && mx0 > 20 && mx0 < 100) ui++;
        }
        res.Ui = ut > 0 ? (double)ui / ut : 0;
        int ub = 0, ubt = 0, pp = 0, pt = 0, gr = 0;
        for (int y = 0; y < h; y += 3) for (int x = 0; x < w; x += 3) {
            int p = px[y * w + x]; int r0 = R(p), g0 = G(p), b0 = B(p);
            int mx0 = Math.Max(r0, Math.Max(g0, b0)), mn0 = Math.Min(r0, Math.Min(g0, b0));
            pt++;
            if (mx0-mn0 < 14 && mx0 > 20 && mx0 < 225) gr++;
            if ((r0 > 175 && r0 < 235 && g0 > 160 && g0 < 215 && b0 > 120 && b0 < 185 && r0 > b0 + 25) || (r0 > 85 && r0 < 150 && g0 > 110 && g0 < 165 && b0 > 100 && b0 < 150 && g0 > r0 + 8 && Math.Abs(g0-b0) < 30)) pp++;
            if (x >= w * 3 / 10 && x < w * 7 / 10 && y >= h * 7 / 10 && y < h * 92 / 100) { ubt++; if (mx0-mn0 < 16 && mx0 > 20 && mx0 < 100) ub++; }
        }
        res.UiBottom = ubt > 0 ? (double)ub / ubt : 0;
        res.Paper = pt > 0 ? (double)pp / pt : 0;
        res.Grey = pt > 0 ? (double)gr / pt : 0;
        int minPx = Math.Max(5, n / 400000);
        var blueMask = new bool[n]; var redMask = new bool[n]; var greenMask = new bool[n];
        for (int i = 0; i < n; i++) { int p = px[i]; blueMask[i] = IsBlue(p); redMask[i] = IsRed(p); greenMask[i] = IsArrowGreen(p); }
        var blues = DilComps(blueMask, w, h, minPx, 1);
        var reds = DilComps(redMask, w, h, minPx, 1);
        double bestScore = 0;
        foreach (var b in blues) {
            int bw = b.X1-b.X0+1, bh = b.Y1-b.Y0+1;
            if (!BadgeShape(b) || Math.Max(bw, bh) > w / 20) continue;
            foreach (var r in reds) {
                int rw = r.X1-r.X0+1, rh = r.Y1-r.Y0+1;
                if (!BadgeShape(r) || Math.Max(rw, rh) > w / 20) continue;
                double ratio = (double)b.N / r.N;
                if (ratio < 0.5 || ratio > 2.0) continue;
                double sb = (bw + bh) / 2.0, sr = (rw + rh) / 2.0;
                if (Math.Max(sb, sr) > 1.5 * Math.Min(sb, sr)) continue;
                double d = Math.Sqrt((b.X-r.X) * (b.X-r.X) + (b.Y-r.Y) * (b.Y-r.Y));
                double size = (sb + sr) / 2.0;
                if (d / size < 4.5 || d / size > 16) continue;
                double cx = (b.X + r.X) / 2, cy = (b.Y + r.Y) / 2;
                double axis = Math.Atan2(b.X-r.X, r.Y-b.Y) * 180 / Math.PI;
                double rs = Math.Max(RingScore(px, w, h, cx, cy, d / 2, axis), Math.Max(RingScore(px, w, h, cx, cy, d / 2 * 0.95, axis), RingScore(px, w, h, cx, cy, d / 2 * 1.05, axis)));
                if (rs < 0.6 || rs <= bestScore) continue;
                if (!MinimapInside(px, w, h, cx, cy, d / 2)) continue;
                bestScore = rs;
                double ang = Math.Atan2(b.X-r.X, r.Y-b.Y) * 180 / Math.PI;
                res.Deg = Norm(360-ang); res.Kind = "minimap"; res.Score = rs; res.Hud = true; res.Cx = cx / w; res.Cy = cy / h; res.Size = d / w;
            }
        }
        if (res.Deg >= 0) return;
        var darkMask = new bool[n];
        for (int i = 0; i < n; i++) { int p = px[i]; int r = R(p), g = G(p), b = B(p); int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)); darkMask[i] = r + g + b < 200 && mx-mn < 45; }
        var labels = new List<Comp>();
        foreach (var c in DilComps(darkMask, w, h, 30, 1)) {
            int lw = c.X1-c.X0+1, lh = c.Y1-c.Y0+1;
            if (lw < w * 0.025 || lw > w * 0.16 || lh < w * 0.01 || lh > w * 0.05) continue;
            double asp = (double)lw / lh;
            if (asp < 1.6 || asp > 6.5) continue;
            if ((double)c.N / (lw * lh) < 0.45) continue;
            labels.Add(c);
        }
        var greens = MergeNear(DilComps(greenMask, w, h, 8, 3), w);
        foreach (var lb in labels) {
            var touch = new List<Comp>();
            foreach (var c in greens) {
                if (Math.Max(c.X1-c.X0, c.Y1-c.Y0) > w / 6) continue;
                if (c.X1 >= lb.X0-5 && c.X0 <= lb.X1 + 5 && c.Y1 >= lb.Y0-5 && c.Y0 <= lb.Y1 + 5) touch.Add(c);
            }
            if (touch.Count < 2) continue;
            var m = new Comp { Px = new List<int>(), X0 = int.MaxValue, Y0 = int.MaxValue, X1 = -1, Y1 = -1 };
            double sx = 0, sy = 0;
            foreach (var c in touch) {
                m.Px.AddRange(c.Px); sx += c.X * c.N; sy += c.Y * c.N;
                m.X0 = Math.Min(m.X0, c.X0); m.Y0 = Math.Min(m.Y0, c.Y0); m.X1 = Math.Max(m.X1, c.X1); m.Y1 = Math.Max(m.Y1, c.Y1);
                greens.Remove(c);
            }
            m.N = m.Px.Count; m.X = sx / m.N; m.Y = sy / m.N;
            greens.Add(m);
        }
        greens.RemoveAll(c => c.N < Math.Max(minPx, 20));

        Comp best = null; double bestDeg = -1, bestLen = 0;
        string bestHow = "";
        foreach (var c in greens) {
            int cw = c.X1-c.X0+1, ch = c.Y1-c.Y0+1;
            if (Math.Max(cw, ch) < 12 || Math.Max(cw, ch) > w / 5) continue;
            double fillR = (double)c.N / (cw * ch), ml = MapLike(px, w, h, c);
            double deg = -1, len = 0; string how = "";
            double ex1, ey1, ex2, ey2, L;
            if (!Ends(w, c, out ex1, out ey1, out ex2, out ey2, out L)) continue;
            double d1 = 1e9, d2 = 1e9;
            foreach (var lb in labels) {
                double axp = lb.X0-0.2 * (lb.Y1-lb.Y0), ayp = lb.Y1;
                double a1 = Math.Sqrt((ex1-axp) * (ex1-axp) + (ey1-ayp) * (ey1-ayp)), a2 = Math.Sqrt((ex2-axp) * (ex2-axp) + (ey2-ayp) * (ey2-ayp));
                if (Math.Min(a1, a2) > L * 1.1) continue;
                if (Math.Min(a1, a2) < Math.Min(d1, d2)) { d1 = a1; d2 = a2; }
            }
            double hx, hy, hr;
            bool hole = FindHole(w, h, c, out hx, out hy, out hr) && hr >= 1.5;
            if (d1 < 1e8 && Math.Min(d1, d2) < 0.65 * Math.Max(d1, d2)) {
                if (d1 < d2) deg = Norm(Math.Atan2(ex2-ex1, -(ey2-ey1)) * 180 / Math.PI);
                else deg = Norm(Math.Atan2(ex1-ex2, -(ey1-ey2)) * 180 / Math.PI);
                len = L; how = "label";
            }
            if (fillR > 0.6 || ml < 0.5) { deg = -1; continue; }
            if (deg < 0 && hole) {
                double far = 0; int tip = -1;
                foreach (int j in c.Px) { double dx = j % w-hx, dy = j / w-hy; double dd = dx * dx + dy * dy; if (dd > far) { far = dd; tip = j; } }
                far = Math.Sqrt(far);
                if (far >= hr * 2.6) { deg = Norm(Math.Atan2(tip % w-hx, -(tip / w-hy)) * 180 / Math.PI); len = far; how = "hole"; }
            }
            if (deg < 0) continue;
            if (best == null || c.N > best.N) { best = c; bestDeg = deg; bestLen = len; bestHow = how; }
        }
        if (best != null) { res.Deg = bestDeg; res.Kind = "map" + bestHow; res.Score = bestLen; res.MapShot = true; res.Cx = best.X / w; res.Cy = best.Y / h; res.Size = Math.Max(best.X1-best.X0, best.Y1-best.Y0) / (double)w; }
    }

    static bool Ends(int w, Comp c, out double ex1, out double ey1, out double ex2, out double ey2, out double L) {
        double mx = c.X, my = c.Y, sxx = 0, syy = 0, sxy = 0;
        foreach (int j in c.Px) { double dx = j % w-mx, dy = j / w-my; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
        double th = 0.5 * Math.Atan2(2 * sxy, sxx-syy);
        double ax = Math.Cos(th), ay = Math.Sin(th);
        double tmin = double.MaxValue, tmax = double.MinValue;
        foreach (int j in c.Px) { double t = (j % w-mx) * ax + (j / w-my) * ay; if (t < tmin) tmin = t; if (t > tmax) tmax = t; }
        L = tmax-tmin;
        ex1 = mx + ax * tmin; ey1 = my + ay * tmin; ex2 = mx + ax * tmax; ey2 = my + ay * tmax;
        return L >= 12;
    }

    static bool IsPaper(int r, int g, int b) {
        int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        if (mx < 30 || mx > 245 || mx-mn > 120) return false;
        return g-b >= 10 || r-b >= 18;
    }

    static double MapLike(int[] px, int w, int h, Comp c) {
        int cw = c.X1-c.X0+1, ch = c.Y1-c.Y0+1;
        int m = Math.Max(cw, ch);
        int x0 = c.X0-m / 2, x1 = c.X1 + m / 2, y0 = c.Y0-m / 2, y1 = c.Y1 + m / 2;
        int good = 0, tot = 0;
        for (int y = y0; y <= y1; y += 2) for (int x = x0; x <= x1; x += 2) {
            if (x >= c.X0 && x <= c.X1 && y >= c.Y0 && y <= c.Y1) continue;
            if (x < 0 || y < 0 || x >= w || y >= h) continue;
            tot++;
            int p = px[y * w + x];
            int r = R(p), g = G(p), b = B(p);
            if (IsPaper(r, g, b)) good++;
        }
        return tot > 0 ? (double)good / tot : 0;
    }

    static bool FindHole(int w, int h, Comp c, out double hx, out double hy, out double hr) {
        hx = hy = hr = 0;
        int x0 = Math.Max(0, c.X0-1), y0 = Math.Max(0, c.Y0-1), x1 = Math.Min(w-1, c.X1+1), y1 = Math.Min(h-1, c.Y1+1);
        int bw = x1-x0+1, bh = y1-y0+1;
        var mine = new bool[bw * bh];
        foreach (int j in c.Px) mine[(j / w-y0) * bw + (j % w-x0)] = true;
        var outside = new bool[bw * bh];
        var st = new Stack<int>();
        for (int x = 0; x < bw; x++) { st.Push(x); st.Push((bh-1) * bw + x); }
        for (int y = 0; y < bh; y++) { st.Push(y * bw); st.Push(y * bw + bw-1); }
        while (st.Count > 0) {
            int k = st.Pop();
            if (outside[k] || mine[k]) continue;
            outside[k] = true;
            int x = k % bw, y = k / bw;
            if (x > 0) st.Push(k-1);
            if (x < bw-1) st.Push(k+1);
            if (y > 0) st.Push(k-bw);
            if (y < bh-1) st.Push(k+bw);
        }
        double sx = 0, sy = 0; int cnt = 0;
        for (int k = 0; k < bw * bh; k++) if (!mine[k] && !outside[k]) { sx += k % bw; sy += k / bw; cnt++; }
        if (cnt < 4 || cnt < c.N * 0.03) return false;
        hx = x0 + sx / cnt; hy = y0 + sy / cnt; hr = Math.Sqrt(cnt / Math.PI);
        return true;
    }
}
