using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

internal static class ImageOcr
{
    private static readonly object gate = new object();
    private static Dictionary<string, object> cache;
    private static readonly Queue<string> queue = new Queue<string>();
    private static readonly HashSet<string> queued = new HashSet<string>(StringComparer.Ordinal);
    private static Thread worker;
    private static MatchList candidates;
    private static bool dirty;
    public static int Version;
    private static readonly Regex Header = new Regex(@"^(Монтаж|Оснастка|Angelmontage|Rig|Montaż|Mocowanie|Montage|Montaje|Montaggio|Montagem|Montaj|Takım|Комбинация наживок|Köder\s*Kombination|Bait\s*combination)\b", RegexOptions.IgnoreCase);
    private static string latin;

    private static string LatinLang()
    {
        if (latin != null) return latin;
        string pick = "";
        try
        {
            foreach (string want in new string[] { "en-US", "en-GB", "de-DE", "fr-FR", "es-ES", "it-IT", "pl-PL", "pt-BR", "nl-NL" })
                if (OcrEngine.IsLanguageSupported(new Windows.Globalization.Language(want))) { pick = want; break; }
            if (pick.Length == 0)
                foreach (Windows.Globalization.Language l in OcrEngine.AvailableRecognizerLanguages)
                    if (l.LanguageTag.StartsWith("en") || l.LanguageTag.StartsWith("de") || l.LanguageTag.StartsWith("fr") || l.LanguageTag.StartsWith("es") || l.LanguageTag.StartsWith("pl")) { pick = l.LanguageTag; break; }
        }
        catch (Exception) { }
        latin = pick;
        return pick;
    }
    private static readonly Regex ItemEnd = new Regex(@"\s[OoО0©ф@®]\s*$");

    public static bool AnyAvailable()
    {
        return Available("ru") || LatinLang().Length > 0;
    }

    public static bool Available(string lang)
    {
        try { return OcrEngine.IsLanguageSupported(new Windows.Globalization.Language(lang)); }
        catch (Exception) { return false; }
    }

    private static void EnsureCache()
    {
        if (cache != null) return;
        Dictionary<string, object> d = null;
        try { d = AppEnv.ReadJsonFile(AppEnv.DataPath("ocr_cache.json")) as Dictionary<string, object>; } catch (Exception) { }
        cache = d ?? new Dictionary<string, object>();
    }

    public static Dictionary<string, object> Result(string post)
    {
        lock (gate)
        {
            EnsureCache();
            object v;
            Dictionary<string, object> r = cache.TryGetValue(post, out v) ? v as Dictionary<string, object> : null;
            if (r != null && AppEnv.GetBool(r, "error"))
            {
                DateTime at;
                if (!DateTime.TryParse(AppEnv.GetString(r, "at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at) || DateTime.UtcNow.Subtract(at).TotalHours > 6)
                {
                    cache.Remove(post);
                    queued.Remove(post);
                    return null;
                }
            }
            return r;
        }
    }

    public static void Request(string post)
    {
        if (string.IsNullOrEmpty(post)) return;
        lock (gate)
        {
            EnsureCache();
            if (cache.ContainsKey(post) || !queued.Add(post)) return;
            queue.Enqueue(post);
            if (worker == null || !worker.IsAlive)
            {
                worker = new Thread(Work);
                worker.IsBackground = true;
                worker.Start();
            }
        }
    }

    private static void Work()
    {
        while (true)
        {
            string post;
            lock (gate)
            {
                if (queue.Count == 0)
                {
                    if (dirty) { try { AppEnv.WriteJsonFile(AppEnv.DataPath("ocr_cache.json"), cache); } catch (Exception) { } dirty = false; }
                    worker = null;
                    return;
                }
                post = queue.Dequeue();
            }
            Dictionary<string, object> res = AppEnv.Obj("v", 1);
            try { Scan(post, res); }
            catch (Exception ex) { AppEnv.Log("OCR", post + ": " + ex.Message); res["error"] = true; res["at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
            lock (gate)
            {
                cache[post] = res;
                dirty = true;
                Version++;
                if (queue.Count % 10 == 0) { try { AppEnv.WriteJsonFile(AppEnv.DataPath("ocr_cache.json"), cache); dirty = false; } catch (Exception) { } }
            }
        }
    }

    private static void Scan(string post, Dictionary<string, object> res)
    {
        bool ru = Available("ru");
        string lat = LatinLang();
        if (!ru && lat.Length == 0) { res["noLang"] = true; return; }
        string[] imgs = Scans.FetchImages(post);
        if (imgs == null) return;
        for (int i = 0; i < imgs.Length && i < 10; i++)
        {
            byte[] b = Scans.ImageAt(post, i);
            if (b == null) continue;
            List<string> baits;
            string used;
            if (ReadSetup(b, ru, lat, out baits, out used))
            {
                res["img"] = i;
                res["baits"] = baits.ToArray();
                res["ocr"] = used;
                return;
            }
        }
    }

    public static bool ReadSetup(byte[] b, bool ru, string lat, out List<string> baits, out string used)
    {
        foreach (double crop in new double[] { 0.5, 0.0 })
        {
            if (ru && ParseSetup(Recognize(b, "ru", 3, crop), out baits)) { used = "ru"; return true; }
            if (lat.Length > 0 && ParseSetup(Recognize(b, lat, crop > 0 ? 3u : 2u, crop), out baits)) { used = lat; return true; }
        }
        baits = new List<string>();
        used = "";
        return false;
    }

    public static bool ReadSetup(byte[] b, out List<string> baits, out string used)
    {
        return ReadSetup(b, Available("ru"), LatinLang(), out baits, out used);
    }

    private static T W<T>(IAsyncOperation<T> op)
    {
        while (op.Status == AsyncStatus.Started) Thread.Sleep(3);
        if (op.Status != AsyncStatus.Completed) throw new InvalidOperationException("OCR " + op.Status);
        return op.GetResults();
    }

    public static string[] Recognize(byte[] data, string lang, uint factor, double cropLeft)
    {
        InMemoryRandomAccessStream ms = new InMemoryRandomAccessStream();
        DataWriter w = new DataWriter(ms);
        w.WriteBytes(data);
        W(w.StoreAsync());
        w.DetachStream();
        ms.Seek(0);
        BitmapDecoder dec = W(BitmapDecoder.CreateAsync(ms));
        uint x0 = (uint)(dec.PixelWidth * cropLeft);
        uint f = factor;
        while (f > 1 && (dec.PixelWidth * f > OcrEngine.MaxImageDimension || dec.PixelHeight * f > OcrEngine.MaxImageDimension)) f--;
        BitmapTransform t = new BitmapTransform();
        t.ScaledWidth = dec.PixelWidth * f;
        t.ScaledHeight = dec.PixelHeight * f;
        t.InterpolationMode = BitmapInterpolationMode.Fant;
        BitmapBounds bb = new BitmapBounds();
        bb.X = x0 * f; bb.Y = 0; bb.Width = (dec.PixelWidth-x0) * f; bb.Height = dec.PixelHeight * f;
        t.Bounds = bb;
        SoftwareBitmap sb = W(dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, t, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage));
        OcrEngine eng = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(lang));
        if (eng == null) return new string[0];
        OcrResult r = W(eng.RecognizeAsync(sb));
        List<string> outL = new List<string>();
        foreach (OcrLine ln in r.Lines) outL.Add(ln.Text);
        return outL.ToArray();
    }

    private static MatchList Candidates()
    {
        if (candidates != null) return candidates;
        List<string> loc = new List<string>(), en = new List<string>();
        HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string b in GameData.Baits) keys.Add(b);
        foreach (string d in GameData.DipKeys) keys.Add(d);
        foreach (string k in keys)
        {
            foreach (string lg in GameData.Languages())
            {
                string n = GameData.Translate(k, lg);
                if (n.Length == 0 || (lg != "en" && n == k)) continue;
                loc.Add(n); en.Add(k);
            }
        }
        MatchList m = new MatchList();
        m.Loc = loc.ToArray(); m.En = en.ToArray(); m.LocNorm = new string[m.Loc.Length];
        for (int i = 0; i < m.Loc.Length; i++) m.LocNorm[i] = Fuzzy.Norm(m.Loc[i]);
        candidates = m;
        return m;
    }

    public static bool ParseSetup(string[] lines, out List<string> baits)
    {
        baits = new List<string>();
        int start = -1;
        for (int i = 0; i < lines.Length; i++) if (Header.IsMatch(lines[i].Trim())) { start = i; break; }
        if (start < 0) return false;
        MatchList c = Candidates();
        for (int i = start + 1; i < lines.Length && baits.Count < 4; i++)
        {
            string l = lines[i].Trim();
            if (!ItemEnd.IsMatch(l)) continue;
            string name = ItemEnd.Replace(l, "").Trim();
            name = Regex.Replace(name, @"(?<=\d)[sS]|[sS](?=\d)", "5");
            name = Regex.Replace(name, @"(?<=\d)[oOоО]", "0");
            name = Regex.Replace(name, @"(?<=\d)[lI|]|[lI|](?=\d)", "1");
            if (name.Length < 5) continue;
            double sc;
            int idx = Fuzzy.Best(name, c.LocNorm, out sc);
            List<string> variants = new List<string>();
            string bare = Regex.Replace(name, @"^[A-Za-z0-9'&.\s]+\s(?=[^\u0000-\u007F])", "").Trim();
            if (bare != name && bare.Length >= 5) variants.Add(bare);
            string[] words = name.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int k = 1; k <= 3 && words.Length-k >= 2; k++) variants.Add(string.Join(" ", words, k, words.Length-k));
            if (sc < 0.8)
            {
                foreach (string v in variants)
                {
                    double sc2;
                    int idx2 = Fuzzy.Best(v, c.LocNorm, out sc2);
                    if (idx2 >= 0 && sc2 > sc + 0.05) { idx = idx2; sc = sc2; }
                }
            }
            if (idx < 0 || sc < 0.72) continue;
            if (!baits.Contains(c.En[idx])) baits.Add(c.En[idx]);
        }
        return true;
    }
}
