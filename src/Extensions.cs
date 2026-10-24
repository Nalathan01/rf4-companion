using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

#if PRIVATE
public static class Ext
{
    internal static readonly Dictionary<string, Func<Dictionary<string, object>, object>> Routes = new Dictionary<string, Func<Dictionary<string, object>, object>>(StringComparer.OrdinalIgnoreCase);

    public static string DataDir { get { return AppEnv.DataDir; } }
    public static string Lang { get { return UserStore.Lang; } }

    public static void Route(string name, Func<Dictionary<string, object>, object> handler) { lock (Routes) Routes[name] = handler; }
    public static void Log(string text) { AppEnv.Log("Erweiterung", text); }
    public static void PauseTracker(string reason)
    {
        reason = reason ?? "";
        if (reason == Tracker.PauseReason) return;
        bool resumed = Tracker.PauseReason.Length > 0 && reason.Length == 0;
        Tracker.PauseReason = reason;
        if (resumed) Tracker.SkipBacklog();
        Tracker.Version++;
    }

    public static Dictionary<string, string[]> LakeNames()
    {
        Dictionary<string, string[]> d = new Dictionary<string, string[]>();
        foreach (Lake l in GameData.Lakes) d[l.Id] = l.Names == null ? new string[0] : l.Names.Values.Where(v => v != null).Select(v => v.ToString()).Distinct().ToArray();
        return d;
    }

    public static Dictionary<string, string[]> FishNames()
    {
        Dictionary<string, string[]> d = new Dictionary<string, string[]>();
        foreach (string f in GameData.Trophies.Keys) d[f] = GameData.Languages().Select(l => GameData.Translate(f, l)).Concat(new string[] { f }).Distinct().ToArray();
        return d;
    }

    public static string ResolveItem(string text) { return PostText.ResolveAny(text); }
    public static bool IsDip(string key) { return GameData.DipKeys.Contains(key); }
    public static string Translate(string key) { return GameData.Translate(GameData.ToKey(key), UserStore.Lang); }

    public static void OpenWindow(string url)
    {
        if (!url.StartsWith("https://")) throw new ArgumentException("url");
        string edge = AppServer.EdgePath();
        if (edge == null) { Process.Start(url); return; }
        string profileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"RF4Companion\edge");
        ProcessStartInfo psi = new ProcessStartInfo(edge, "--app=" + url + " --user-data-dir=\"" + profileDir + "\" --no-first-run");
        psi.UseShellExecute = false;
        Process.Start(psi);
    }
}

internal static class Extensions
{
    public static string Dir { get { return AppEnv.DataPath("extensions"); } }
    public static readonly List<string> Scripts = new List<string>();

    public static void Load()
    {
        if (!Directory.Exists(Dir)) return;
        foreach (string js in Directory.GetFiles(Dir, "*.js")) Scripts.Add(Path.GetFileName(js));
        string[] cs = Directory.GetFiles(Dir, "*.cs");
        if (cs.Length == 0) return;
        try
        {
            using (CodeDomProvider p = CodeDomProvider.CreateProvider("CSharp"))
            {
                CompilerParameters cp = new CompilerParameters();
                cp.GenerateInMemory = true;
                foreach (string r in new string[] { "System.dll", "System.Core.dll", "System.Web.Extensions.dll", "System.Windows.Forms.dll", "System.Drawing.dll" }) cp.ReferencedAssemblies.Add(r);
                cp.ReferencedAssemblies.Add(Assembly.GetExecutingAssembly().Location);
                CompilerResults res = p.CompileAssemblyFromFile(cp, cs);
                if (res.Errors.HasErrors)
                {
                    foreach (CompilerError e in res.Errors) if (!e.IsWarning) AppEnv.Log("Erweiterung", Path.GetFileName(e.FileName) + "(" + e.Line + "): " + e.ErrorText);
                    return;
                }
                foreach (Type t in res.CompiledAssembly.GetTypes())
                {
                    MethodInfo init = t.GetMethod("Init", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (init == null) continue;
                    try { init.Invoke(null, null); AppEnv.Log("Erweiterung", t.Name + " geladen"); }
                    catch (Exception ex) { AppEnv.Log("Erweiterung", t.Name + ": " + (ex.InnerException ?? ex).Message); }
                }
            }
        }
        catch (Exception ex) { AppEnv.Log("Erweiterung", ex.Message); }
    }

    public static object Call(string name, Dictionary<string, object> args)
    {
        Func<Dictionary<string, object>, object> h;
        lock (Ext.Routes) if (!Ext.Routes.TryGetValue(name, out h)) throw new AppError("unknown", name);
        return h(args ?? new Dictionary<string, object>());
    }
}
#else
internal static class Extensions
{
    public static string Dir { get { return AppEnv.DataPath("extensions"); } }
    public static readonly List<string> Scripts = new List<string>();
    public static void Load() { }
    public static object Call(string name, Dictionary<string, object> args) { throw new AppError("unknown", name); }
}
#endif
