using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;

internal static class Updater
{
    private const string Repo = "Nalathan01/rf4-companion";
    private const string AssetName = "RF4.Companion.exe";
    private const string Agent = "RF4Companion";

    private static string latest;
    private static string assetUrl;
    private static string assetSha;
    private static string notes;
    private static string currentNotes;
    private static System.Threading.Timer timer;
    public static volatile int Progress = -1;
    public static volatile string InstallError = "";
    private static DateTime lastCheck = DateTime.MinValue;

    public static string Available
    {
        get { return assetUrl != null && assetSha != null ? latest : null; }
    }

    public static string Notes
    {
        get { return notes ?? ""; }
    }

    public static string CurrentNotes
    {
        get { return currentNotes ?? ""; }
    }

    public static string CurrentText
    {
        get { return Short(Assembly.GetExecutingAssembly().GetName().Version).ToString(); }
    }

    private static string ExePath
    {
        get { return Assembly.GetExecutingAssembly().Location; }
    }

    private static Version Short(Version v)
    {
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static void CleanUp()
    {
        string dir = Path.GetDirectoryName(ExePath), name = Path.GetFileName(ExePath);
        try
        {
            foreach (string f in Directory.GetFiles(dir, name + "*.old"))
            {
                try { File.Delete(f); } catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    private static void MoveAside(string exe, string old)
    {
        if (File.Exists(old))
        {
            try { File.Delete(old); }
            catch (Exception) { File.Move(old, exe + "." + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".old"); }
        }
        File.Move(exe, old);
    }

    public static void CheckIfStale()
    {
        if (DateTime.UtcNow.Subtract(lastCheck).TotalMinutes >= 10) CheckInBackground();
    }

    public static void CheckInBackground()
    {
#if PRIVATE || FANGBUCH
        return;
#endif
        if (timer == null) timer = new System.Threading.Timer(delegate { CheckInBackground(); }, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        lastCheck = DateTime.UtcNow;
        Task.Factory.StartNew(() =>
        {
            try { Check(); }
            catch (Exception ex) { AppEnv.Log("Update", "Pruefung fehlgeschlagen: " + ex.Message); }
        });
    }

    private static void Check()
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
        req.UserAgent = Agent;
        req.Accept = "application/vnd.github+json";
        req.Timeout = 15000;
        string text;
        using (WebResponse res = req.GetResponse())
        using (StreamReader reader = new StreamReader(res.GetResponseStream()))
        {
            text = reader.ReadToEnd();
        }
        Dictionary<string, object> rel = AppEnv.Json.DeserializeObject(text) as Dictionary<string, object>;
        if (rel == null || AppEnv.GetBool(rel, "draft") || AppEnv.GetBool(rel, "prerelease")) return;
        string tag = AppEnv.GetString(rel, "tag_name");
        Version v;
        if (string.IsNullOrEmpty(tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out v)) return;
        v = Short(v);
        if (v == Short(Assembly.GetExecutingAssembly().GetName().Version)) currentNotes = AppEnv.GetString(rel, "body");
        if (v <= Short(Assembly.GetExecutingAssembly().GetName().Version)) return;
        foreach (object o in AppEnv.GetList(rel, "assets"))
        {
            Dictionary<string, object> a = o as Dictionary<string, object>;
            if (a == null || !string.Equals(AppEnv.GetString(a, "name"), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            string digest = AppEnv.GetString(a, "digest");
            if (string.IsNullOrEmpty(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return;
            assetSha = digest.Substring(7).ToLowerInvariant();
            assetUrl = AppEnv.GetString(a, "browser_download_url");
            notes = AppEnv.GetString(rel, "body");
            latest = v.ToString();
            AppEnv.Log("Update", "Version " + latest + " verfuegbar");
            return;
        }
    }

    public static void InstallAsync(Action done)
    {
        if (Progress >= 0) return;
        Progress = 0;
        InstallError = "";
        Task.Factory.StartNew(() =>
        {
            try { Install(); Progress = 100; done(); }
            catch (Exception e) { InstallError = e.Message; Progress = -1; AppEnv.Log("Update", "Fehlgeschlagen in " + Path.GetDirectoryName(ExePath) + ": " + e.Message); }
        });
    }

    public static void Install()
    {
        if (Available == null) throw new AppError("updateNone", null);
        string exe = ExePath;
        string tmp = exe + ".new";
        string old = exe + ".old";
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(assetUrl);
            req.UserAgent = Agent;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            using (WebResponse res = req.GetResponse())
            using (Stream src = res.GetResponseStream())
            using (FileStream dst = File.Create(tmp))
            {
                long total = res.ContentLength, got = 0;
                byte[] buf = new byte[81920];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    got += n;
                    if (total > 0) Progress = (int)Math.Min(99, got * 100 / total);
                }
            }
            string hash;
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(tmp))
            {
                hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            if (hash != assetSha) throw new AppError("updateHash", null);
            MoveAside(exe, old);
            try { File.Move(tmp, exe); }
            catch (Exception)
            {
                File.Move(old, exe);
                throw;
            }
            AppEnv.Log("Update", CurrentText + " auf " + latest);
        }
        catch (AppError)
        {
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch (Exception) { }
            throw new AppError("updateFailed", ex.Message);
        }
    }
}
