using System;
using System.IO;
using System.Text;

namespace BalanceDock.Services
{
    internal static class Diagnostics
    {
        public static void Write(string message)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("BALANCEDOCK_DIAGNOSTICS"), "1", StringComparison.Ordinal)) return;
            try
            {
                string folder = Environment.GetEnvironmentVariable("BALANCEDOCK_DATA_DIR");
                if (string.IsNullOrWhiteSpace(folder)) return;
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "diagnostics.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}
