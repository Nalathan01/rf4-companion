using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class AppError : Exception
{
    public readonly string Code;
    public readonly string Arg;

    public AppError(string code, string arg) : base(code + (arg == null ? "" : ": " + arg))
    {
        Code = code;
        Arg = arg;
    }
}

internal static class AppEnv
{
    public static string DataDir;
    public static string DevRoot;
    private static readonly object logLock = new object();

    public static readonly JavaScriptSerializer Json = CreateSerializer();

    private static JavaScriptSerializer CreateSerializer()
    {
        JavaScriptSerializer s = new JavaScriptSerializer();
        s.MaxJsonLength = int.MaxValue;
        s.RecursionLimit = 256;
        return s;
    }

    public static void Init(string dev, string dataDir)
    {
        DataDir = !string.IsNullOrEmpty(dataDir) ? dataDir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RF4Companion");
        Directory.CreateDirectory(DataDir);
        if (!string.IsNullOrEmpty(dev) && Directory.Exists(dev)) DevRoot = dev;
    }

    public static string DataPath(string name)
    {
        return Path.Combine(DataDir, name);
    }

    public static void Log(string area, string text)
    {
        try
        {
            lock (logLock)
            {
                string dir = DataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RF4Companion");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "app.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + area + ": " + text + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception) { }
    }

    public static byte[] Resource(string name)
    {
        if (DevRoot != null)
        {
            string p = DevPath(name);
            if (p != null && File.Exists(p)) return File.ReadAllBytes(p);
        }
        Assembly asm = Assembly.GetExecutingAssembly();
        Stream s = asm.GetManifestResourceStream(name);
        bool gz = false;
        if (s == null) { s = asm.GetManifestResourceStream(name + ".gz"); gz = s != null; }
        if (s == null) return null;
        using (s)
        using (MemoryStream ms = new MemoryStream())
        {
            if (gz) { using (GZipStream g = new GZipStream(s, CompressionMode.Decompress)) g.CopyTo(ms); }
            else s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    private static string DevPath(string name)
    {
        if (name.StartsWith("ui/")) return Path.Combine(DevRoot, "ui", name.Substring(3));
        if (name == "data/lang.json") return Path.Combine(DevRoot, "lang.json");
        if (name.StartsWith("data/")) return Path.Combine(DevRoot, "data", name.Substring(5));
        if (name.StartsWith("maps/")) return Path.Combine(DevRoot, "data", "maps", name.Substring(5));
        return null;
    }

    public static string ResourceText(string name)
    {
        byte[] b = Resource(name);
        if (b == null) return null;
        return Encoding.UTF8.GetString(b).TrimStart('﻿');
    }

    public static object ReadJsonFile(string path)
    {
        if (!File.Exists(path)) return null;
        string t = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
        if (t.Trim().Length == 0) return null;
        return Json.DeserializeObject(t);
    }

    public static object ReadGzJsonFile(string path)
    {
        if (!File.Exists(path)) return null;
        using (FileStream fs = File.OpenRead(path))
        using (GZipStream g = new GZipStream(fs, CompressionMode.Decompress))
        using (StreamReader r = new StreamReader(g, Encoding.UTF8))
        {
            return Json.DeserializeObject(r.ReadToEnd());
        }
    }

    public static void WriteJsonFile(string path, object value)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, Json.Serialize(value), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    public static string GetString(IDictionary d, string key)
    {
        if (d == null || !d.Contains(key) || d[key] == null) return "";
        return Convert.ToString(d[key], CultureInfo.InvariantCulture);
    }

    public static bool GetBool(IDictionary d, string key)
    {
        if (d == null || !d.Contains(key) || d[key] == null) return false;
        object v = d[key];
        if (v is bool) return (bool)v;
        bool b;
        return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out b) && b;
    }

    public static double GetDouble(IDictionary d, string key, double fallback)
    {
        if (d == null || !d.Contains(key) || d[key] == null) return fallback;
        double v;
        if (double.TryParse(Convert.ToString(d[key], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
        return fallback;
    }

    public static IList GetList(IDictionary d, string key)
    {
        if (d == null || !d.Contains(key)) return new object[0];
        IList l = d[key] as IList;
        return l ?? new object[0];
    }

    public static Dictionary<string, object> Obj(params object[] kv)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        return d;
    }
}
