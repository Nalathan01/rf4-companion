using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

internal sealed class BrowserInfo
{
    public string Id;
    public string Name;
    public string Exe;
    public bool AppWindow;
}

internal static class Browser
{
    public const string Auto = "auto";
    public const string Default = "default";

    public static string WindowExe;

    private static string PrefPath
    {
        get { return AppEnv.DataPath("browser.txt"); }
    }

    public static string Choice
    {
        get
        {
            try
            {
                if (File.Exists(PrefPath))
                {
                    string v = File.ReadAllText(PrefPath).Trim();
                    if (v.Length > 0) return v;
                }
            }
            catch (Exception) { }
            return Auto;
        }
        set { File.WriteAllText(PrefPath, string.IsNullOrEmpty(value) ? Auto : value, new UTF8Encoding(false)); }
    }

    public static List<BrowserInfo> Installed()
    {
        List<BrowserInfo> list = new List<BrowserInfo>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RegistryKey root in new RegistryKey[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (string b in new string[] { @"SOFTWARE\Clients\StartMenuInternet", @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet" })
            {
                try
                {
                    using (RegistryKey key = root.OpenSubKey(b))
                    {
                        if (key == null) continue;
                        foreach (string sub in key.GetSubKeyNames())
                        {
                            BrowserInfo info = Read(key, sub);
                            if (info == null || !seen.Add(info.Exe)) continue;
                            list.Add(info);
                        }
                    }
                }
                catch (Exception) { }
            }
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static BrowserInfo Read(RegistryKey parent, string sub)
    {
        using (RegistryKey key = parent.OpenSubKey(sub))
        {
            if (key == null) return null;
            string exe = null;
            using (RegistryKey cmd = key.OpenSubKey(@"shell\open\command"))
            {
                if (cmd != null) exe = ExeFromCommand(cmd.GetValue("") as string);
            }
            if (exe == null || !File.Exists(exe)) return null;
            if (Path.GetFileNameWithoutExtension(exe).Equals("iexplore", StringComparison.OrdinalIgnoreCase)) return null;
            string name = null;
            using (RegistryKey caps = key.OpenSubKey("Capabilities"))
            {
                if (caps != null) name = caps.GetValue("ApplicationName") as string;
            }
            if (string.IsNullOrEmpty(name) || name.StartsWith("@")) name = key.GetValue("") as string;
            if (string.IsNullOrEmpty(name)) name = sub;
            return Create(exe, name.Trim());
        }
    }

    private static BrowserInfo Create(string exe, string name)
    {
        BrowserInfo info = new BrowserInfo();
        info.Exe = exe;
        info.Id = exe;
        info.Name = name;
        return info;
    }

    private static string ExeFromCommand(string command)
    {
        if (string.IsNullOrEmpty(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith("\""))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command.Substring(1, end-1) : null;
        }
        int exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeEnd > 0 ? command.Substring(0, exeEnd + 4) : null;
    }

    public static BrowserInfo DefaultBrowser()
    {
        try
        {
            string progId = null;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice"))
            {
                if (key != null) progId = key.GetValue("ProgId") as string;
            }
            if (string.IsNullOrEmpty(progId)) return null;
            using (RegistryKey cmd = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
            {
                string exe = cmd == null ? null : ExeFromCommand(cmd.GetValue("") as string);
                if (exe == null || !File.Exists(exe)) return null;
                BrowserInfo known = Installed().FirstOrDefault(b => string.Equals(b.Exe, exe, StringComparison.OrdinalIgnoreCase));
                return known ?? Create(exe, Path.GetFileNameWithoutExtension(exe));
            }
        }
        catch (Exception) { return null; }
    }

    public static object List()
    {
        BrowserInfo def = DefaultBrowser();
        return AppEnv.Obj("choice", Choice, "defaultName", def == null ? null : def.Name, "defaultApp", def != null && def.AppWindow,
            "list", Installed().Select(b => (object)AppEnv.Obj("id", b.Id, "name", b.Name, "app", b.AppWindow)).ToArray());
    }

    public static Process Open(string url)
    {
        string choice = Choice;
        BrowserInfo target = null;
        if (choice == Default) target = DefaultBrowser();
        else if (choice != Auto) target = Installed().FirstOrDefault(b => string.Equals(b.Id, choice, StringComparison.OrdinalIgnoreCase));
        if (target == null && choice != Default)
        {
            string edge = AppServer.EdgePath();
            if (edge != null) { target = Create(edge, "Microsoft Edge"); target.AppWindow = true; }
        }
        WindowExe = null;
        try
        {
            if (target == null) { Process.Start(url); return null; }
            ProcessStartInfo psi;
            if (target.AppWindow)
            {
                psi = new ProcessStartInfo(target.Exe, "--app=" + url + " --user-data-dir=\"" + AppEnv.EdgeProfile + "\" --start-maximized --no-first-run --no-default-browser-check");
                WindowExe = Path.GetFileName(target.Exe);
            }
            else
            {
                psi = new ProcessStartInfo(target.Exe, "\"" + url + "\"");
            }
            psi.UseShellExecute = false;
            Process p = Process.Start(psi);
            return WindowExe != null ? p : null;
        }
        catch (Exception ex)
        {
            AppEnv.Log("Browser", "Start fehlgeschlagen: " + ex.Message);
            Process.Start(url);
            return null;
        }
    }
}
