using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

internal static class Gate
{
    private const string StatusUrl = "https://rf4-share.rf4-companion.workers.dev/status";
    private static DateTime last = DateTime.MinValue;
    private static volatile bool running;
    public static volatile bool Blocked;
    public static volatile string Message = "";
    public static volatile string Min = "";

    public static void Check(bool force)
    {
        if (running || (!force && DateTime.UtcNow.Subtract(last).TotalMinutes < 10)) return;
        running = true;
        last = DateTime.UtcNow;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                Dictionary<string, object> d = AppEnv.Json.DeserializeObject(Http.GetString(StatusUrl, 15000)) as Dictionary<string, object>;
                Apply(AppEnv.GetString(d, "min"), AppEnv.GetString(d, "msg"));
            }
            catch (Exception ex) { AppEnv.Log("Daten", "Status: " + ex.Message); }
            finally { running = false; }
        });
    }

    public static void Apply(string min, string msg)
    {
        Min = min ?? "";
        Message = msg ?? "";
        Version need;
        bool b = Min.Length > 0 && Version.TryParse(Min.Split('.').Length == 1 ? Min + ".0" : Min, out need) && Updater.CurrentVersion < Full(need);
        if (b != Blocked) AppEnv.Log("Daten", b ? "Mindestversion " + Min + ", diese Version erhält keine Daten" : "Datenzugang frei");
        Blocked = b;
    }

    public static void Refused()
    {
        if (!Blocked) { Blocked = true; AppEnv.Log("Daten", "Worker verlangt eine neuere Version"); }
        Check(true);
    }

    private static Version Full(Version v)
    {
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }
}
