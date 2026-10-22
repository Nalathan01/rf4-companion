using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

internal static class DiscordInfo
{
    private static readonly object gate = new object();
    private static Dictionary<string, object> cache;
    private static readonly HashSet<string> pending = new HashSet<string>(StringComparer.Ordinal);
    private static readonly Dictionary<string, string[]> Known = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        { "486847731575554061", new string[] { "Russian Fishing 4 DE", "https://discord.gg/auvuHx9tHp" } },
        { "406518430368399371", new string[] { "Russian Fishing 4 EN", "https://discord.com/servers/russian-fishing-4-en-406518430368399371" } },
        { "611104383337365504", new string[] { "Russian Fishing 4 FR", "https://discord.com/servers/russian-fishing-4-fr-611104383337365504" } }
    };

    private static void Ensure()
    {
        if (cache != null) return;
        Dictionary<string, object> d = null;
        try { d = AppEnv.ReadJsonFile(AppEnv.DataPath("discord_servers.json")) as Dictionary<string, object>; } catch (Exception) { }
        cache = d ?? new Dictionary<string, object>();
    }

    public static string[] Lookup(string guild)
    {
        string[] k;
        if (Known.TryGetValue(guild, out k)) return k;
        lock (gate)
        {
            Ensure();
            object v;
            Dictionary<string, object> e = cache.TryGetValue(guild, out v) ? v as Dictionary<string, object> : null;
            DateTime at;
            bool fresh = e != null && DateTime.TryParse(AppEnv.GetString(e, "at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at) && DateTime.UtcNow.Subtract(at).TotalHours < 24;
            if (!fresh && pending.Add(guild)) ThreadPool.QueueUserWorkItem(delegate { Fetch(guild); });
            if (e == null) return null;
            string name = AppEnv.GetString(e, "name");
            return name.Length == 0 ? null : new string[] { name, AppEnv.GetString(e, "invite") };
        }
    }

    private static void Fetch(string guild)
    {
        string name = "", invite = "";
        try
        {
            Dictionary<string, object> w = AppEnv.Json.DeserializeObject(Http.GetString("https://discord.com/api/guilds/" + guild + "/widget.json", 15000)) as Dictionary<string, object>;
            name = AppEnv.GetString(w, "name");
            invite = AppEnv.GetString(w, "instant_invite");
        }
        catch (Exception) { }
        lock (gate)
        {
            Ensure();
            cache[guild] = AppEnv.Obj("name", name, "invite", invite, "at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            pending.Remove(guild);
            try { AppEnv.WriteJsonFile(AppEnv.DataPath("discord_servers.json"), cache); } catch (Exception) { }
        }
    }
}
