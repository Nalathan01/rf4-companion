using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

internal static class Backup
{
    private const string Marker = "rf4companion-backup.txt";
    private static readonly string[] Lists = { "spots", "catches", "recipes" };

    private static string ImgDir { get { return AppEnv.DataPath("fangbuch_img"); } }

    public static object Export()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string file = Path.Combine(dir, "RF4 Companion Sicherung " + DateTime.Now.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture) + ".zip");
        string user = AppEnv.DataPath("user.json");
        int images = 0;
        using (FileStream fs = File.Create(file))
        using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (StreamWriter w = new StreamWriter(zip.CreateEntry(Marker).Open(), new UTF8Encoding(false))) w.Write("RF4 Companion " + Updater.CurrentText);
            if (File.Exists(user)) AddFile(zip, user, "user.json", CompressionLevel.Optimal);
            if (Directory.Exists(ImgDir))
                foreach (string f in Directory.GetFiles(ImgDir).Where(f => Regex.IsMatch(Path.GetFileName(f), @"^[0-9a-f]{10}\.(png|jpg|webp)$")))
                {
                    AddFile(zip, f, "fangbuch_img/" + Path.GetFileName(f), CompressionLevel.NoCompression);
                    images++;
                }
        }
        try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch (Exception) { }
        return AppEnv.Obj("file", file, "images", images);
    }

    public static object Import(byte[] data)
    {
        Dictionary<string, object> incoming;
        List<ZipArchiveEntry> imgs;
        using (MemoryStream ms = new MemoryStream(data))
        using (ZipArchive zip = OpenZip(ms))
        {
            if (zip == null || zip.GetEntry(Marker) == null || zip.GetEntry("user.json") == null) throw new AppError("backupInvalid", null);
            using (StreamReader r = new StreamReader(zip.GetEntry("user.json").Open(), Encoding.UTF8))
                incoming = AppEnv.Json.DeserializeObject(r.ReadToEnd()) as Dictionary<string, object>;
            if (incoming == null) throw new AppError("backupInvalid", null);
            imgs = zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^fangbuch_img/[0-9a-f]{10}\.(png|jpg|webp)$")).ToList();
            Directory.CreateDirectory(ImgDir);
            foreach (ZipArchiveEntry e in imgs)
            {
                string target = Path.Combine(ImgDir, Path.GetFileName(e.FullName));
                if (!File.Exists(target)) using (Stream src = e.Open()) using (FileStream dst = File.Create(target)) src.CopyTo(dst);
            }
        }
        Dictionary<string, int> added = new Dictionary<string, int>();
        foreach (string key in Lists)
        {
            List<object> have = AsList(UserStore.Get(key));
            HashSet<string> ids = new HashSet<string>(have.Select(o => AppEnv.GetString(o as IDictionary, "id")).Where(x => x.Length > 0), StringComparer.Ordinal);
            HashSet<string> names = key == "recipes" ? new HashSet<string>(have.Select(o => AppEnv.GetString(o as IDictionary, "name")), StringComparer.Ordinal) : new HashSet<string>();
            int n = 0;
            foreach (object o in AsList(incoming.ContainsKey(key) ? incoming[key] : null))
            {
                IDictionary d = o as IDictionary;
                if (d == null) continue;
                string id = AppEnv.GetString(d, "id");
                if ((id.Length > 0 && ids.Contains(id)) || (key == "recipes" && names.Contains(AppEnv.GetString(d, "name")))) continue;
                have.Add(o);
                if (id.Length > 0) ids.Add(id);
                n++;
            }
            if (n > 0) UserStore.Set(key, have);
            added[key] = n;
        }
        foreach (KeyValuePair<string, object> kv in incoming)
        {
            if (Lists.Contains(kv.Key) || kv.Value == null) continue;
            object cur = UserStore.Get(kv.Key);
            if (cur == null || cur.ToString().Length == 0) UserStore.Set(kv.Key, kv.Value);
        }
        AppEnv.Log("Sicherung", "Importiert: " + added["spots"] + " Spots, " + added["catches"] + " Faenge, " + added["recipes"] + " Rezepte, " + imgs.Count + " Bilder");
        return AppEnv.Obj("spots", added["spots"], "catches", added["catches"], "recipes", added["recipes"]);
    }

    private static void AddFile(ZipArchive zip, string path, string name, CompressionLevel level)
    {
        using (Stream dst = zip.CreateEntry(name, level).Open()) using (FileStream src = File.OpenRead(path)) src.CopyTo(dst);
    }

    private static ZipArchive OpenZip(Stream s)
    {
        try { return new ZipArchive(s, ZipArchiveMode.Read); } catch (Exception) { return null; }
    }

    private static List<object> AsList(object o)
    {
        List<object> l = new List<object>();
        IEnumerable e = o as IEnumerable;
        if (e != null && !(o is string)) foreach (object x in e) l.Add(x);
        return l;
    }
}
