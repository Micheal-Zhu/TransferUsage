using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace BalanceDockLauncher
{
    internal static class Program
    {
        private const string RuntimeVersion = "1.1.0";
        private const string PayloadResourceName = "payload.zip";

        [STAThread]
        private static int Main()
        {
            try
            {
                string runtimeRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BalanceDock",
                    "Runtime",
                    RuntimeVersion);
                string dataRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BalanceDock-Portable");
                string applicationPath = EnsureRuntime(runtimeRoot);

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = applicationPath,
                    WorkingDirectory = runtimeRoot,
                    UseShellExecute = false
                };
                startInfo.EnvironmentVariables["BALANCEDOCK_DATA_DIR"] = dataRoot;
                Process.Start(startInfo);
                return 0;
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "Balance Dock 启动失败。\r\n\r\n" + exception.Message,
                    "Balance Dock",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
        }

        private static string EnsureRuntime(string runtimeRoot)
        {
            string applicationPath = Path.Combine(runtimeRoot, "BalanceDock.exe");
            string markerPath = Path.Combine(runtimeRoot, ".complete");
            if (File.Exists(applicationPath) && File.Exists(markerPath)) return applicationPath;

            string parent = Path.GetDirectoryName(runtimeRoot);
            Directory.CreateDirectory(parent);
            string temporaryRoot = runtimeRoot + ".tmp-" + Process.GetCurrentProcess().Id + "-" + DateTime.UtcNow.Ticks;
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream payload = assembly.GetManifestResourceStream(PayloadResourceName))
                {
                    if (payload == null) throw new InvalidOperationException("启动器内未找到程序组件。");
                    using (ZipArchive archive = new ZipArchive(payload, ZipArchiveMode.Read))
                    {
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            string destination = Path.GetFullPath(Path.Combine(temporaryRoot, entry.FullName));
                            if (!destination.StartsWith(Path.GetFullPath(temporaryRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("程序组件路径无效。");
                            if (string.IsNullOrEmpty(entry.Name))
                            {
                                Directory.CreateDirectory(destination);
                                continue;
                            }
                            Directory.CreateDirectory(Path.GetDirectoryName(destination));
                            entry.ExtractToFile(destination, true);
                        }
                    }
                }
                File.WriteAllText(Path.Combine(temporaryRoot, ".complete"), RuntimeVersion);
                if (Directory.Exists(runtimeRoot)) Directory.Delete(runtimeRoot, true);
                Directory.Move(temporaryRoot, runtimeRoot);
                return applicationPath;
            }
            finally
            {
                if (Directory.Exists(temporaryRoot))
                {
                    try { Directory.Delete(temporaryRoot, true); } catch { }
                }
            }
        }
    }
}
