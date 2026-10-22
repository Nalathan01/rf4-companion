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

    public static string Available
    {
        get { return assetUrl != null && assetSha != null ? latest : null; }
    }

    public static string Notes
    {
        get { return notes ?? ""; }
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
        try
        {
            string old = ExePath + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch (Exception) { }
    }

    public static void CheckInBackground()
    {
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

    public static void Install()
    {
        if (Available == null) throw new AppError("updateNone", null);
        string exe = ExePath;
        string tmp = exe + ".new";
        string old = exe + ".old";
        try
        {
            using (WebClient wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = Agent;
                wc.DownloadFile(assetUrl, tmp);
            }
            string hash;
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(tmp))
            {
                hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            if (hash != assetSha) throw new AppError("updateHash", null);
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
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
