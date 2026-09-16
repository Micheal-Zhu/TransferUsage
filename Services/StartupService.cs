using Microsoft.Win32;
using System.Reflection;

namespace BalanceDock.Services
{
    public static class StartupService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "BalanceDock";

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null) return;
                if (enabled)
                {
                    string executable = Assembly.GetEntryAssembly().Location;
                    key.SetValue(ValueName, "\"" + executable + "\" --startup", RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }
}
