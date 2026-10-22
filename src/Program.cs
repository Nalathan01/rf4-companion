using System;
using System.Globalization;
using System.Net;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            int port = 0;
            bool noBrowser = false;
            string urlFile = null;
            string dev = null;
            string openSpot = null;
            string dataDir = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.Equals("-NoBrowser", StringComparison.OrdinalIgnoreCase)) noBrowser = true;
                else if (a.Equals("-Port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) port = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a.Equals("-UrlFile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) urlFile = args[++i];
                else if (a.Equals("-Dev", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) dev = args[++i];
                else if (a.Equals("-OpenSpot", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) openSpot = args[++i];
                else if (a.Equals("-DataDir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) dataDir = args[++i];
            }
            ServicePointManager.DefaultConnectionLimit = 16;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            AppEnv.Init(dev, dataDir);
            new AppServer().Run(port, noBrowser, urlFile, openSpot);
            return 0;
        }
        catch (Exception ex)
        {
            Exception inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            AppEnv.Log("Start", inner.ToString());
            MessageBox.Show(inner.Message, "RF4 Companion", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
