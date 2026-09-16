using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using BalanceDock.Services;

namespace BalanceDock
{
    public partial class App : Application
    {
        private Mutex _singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            bool createdNew;
            string instanceName = Environment.GetEnvironmentVariable("BALANCEDOCK_INSTANCE_NAME");
            if (string.IsNullOrWhiteSpace(instanceName)) instanceName = "BalanceDock.SingleInstance";
            _singleInstance = new Mutex(true, "Local\\" + instanceName, out createdNew);
            if (!createdNew)
            {
                Shutdown();
                return;
            }

            try
            {
                Diagnostics.Write("App.OnStartup before base");
                base.OnStartup(e);
                Diagnostics.Write("App.OnStartup before MainWindow ctor");
                var window = new MainWindow();
                MainWindow = window;
                Diagnostics.Write("App.OnStartup before Show");
                window.Show();
                Diagnostics.Write("App.OnStartup after Show visible=" + window.IsVisible);
            }
            catch (Exception ex)
            {
                Diagnostics.Write("App.OnStartup exception " + ex);
                WriteCrashLog(ex);
                Shutdown(-1);
            }
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteCrashLog(e.Exception);
            e.Handled = true;
            Shutdown(-1);
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            WriteCrashLog(e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject)));
        }

        private static void WriteCrashLog(Exception exception)
        {
            try
            {
                string folder = Environment.GetEnvironmentVariable("BALANCEDOCK_DATA_DIR");
                if (string.IsNullOrWhiteSpace(folder))
                    folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BalanceDock");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "crash.log"), DateTime.Now.ToString("s") + Environment.NewLine + exception + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_singleInstance != null)
            {
                _singleInstance.ReleaseMutex();
                _singleInstance.Dispose();
            }
            base.OnExit(e);
        }
    }
}
