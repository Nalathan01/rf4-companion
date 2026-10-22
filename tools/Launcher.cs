using System;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("RF4 Companion")]
[assembly: AssemblyProduct("RF4 Companion")]
[assembly: AssemblyCompany("Nalathan")]
[assembly: AssemblyCopyright("Nalathan")]

static class Launcher {
    const string Script = "RF4Companion.ps1";

    [STAThread]
    static int Main(string[] args) {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string script = Path.Combine(dir, Script);
        try {
            if (!File.Exists(script)) throw new FileNotFoundException(script);
            string dll = Path.Combine(dir, "RF4Comp.dll");
            if (File.Exists(dll) && File.GetLastWriteTimeUtc(dll) >= File.GetLastWriteTimeUtc(script)) Assembly.LoadFrom(dll);
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using (var rs = RunspaceFactory.CreateRunspace(iss)) {
                rs.ApartmentState = System.Threading.ApartmentState.STA;
                rs.ThreadOptions = PSThreadOptions.UseCurrentThread;
                rs.Open();
                using (var ps = PowerShell.Create()) {
                    ps.Runspace = rs;
                    ps.AddCommand(script);
                    for (int i = 0; i < args.Length; i++) {
                        string a = args[i];
                        if (a.Length > 1 && a[0] == '-') {
                            string name = a.Substring(1);
                            if (i + 1 < args.Length && !(args[i + 1].Length > 1 && args[i + 1][0] == '-')) { ps.AddParameter(name, args[i + 1]); i++; }
                            else ps.AddParameter(name, true);
                        } else ps.AddArgument(a);
                    }
                    ps.Invoke();
                    if (ps.Streams.Error.Count > 0 && ps.InvocationStateInfo.State == PSInvocationState.Failed) {
                        var sb = new StringBuilder();
                        foreach (var e in ps.Streams.Error) sb.AppendLine(e.ToString());
                        Fail(sb.ToString());
                        return 1;
                    }
                }
            }
            return 0;
        } catch (Exception ex) {
            Fail(ex.ToString());
            return 1;
        }
    }

    static void Fail(string text) {
        try {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RF4Companion");
            Directory.CreateDirectory(d);
            File.AppendAllText(Path.Combine(d, "error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " launcher: " + text + Environment.NewLine);
        } catch { }
        MessageBox.Show(text.Length > 1500 ? text.Substring(0, 1500) : text, "RF4 Companion", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
