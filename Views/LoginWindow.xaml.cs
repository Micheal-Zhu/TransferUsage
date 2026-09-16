using BalanceDock.Models;
using BalanceDock.Native;
using BalanceDock.Services;
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace BalanceDock.Views
{
    public partial class LoginWindow : Window
    {
        private readonly StationRecord _station;
        private readonly NewApiClient _apiClient;
        private readonly SettingsStore _settingsStore;
        private readonly CoreWebView2Environment _environment;
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();
        private BrowserSessionService _browserSession;

        public BrowserCapture Capture { get; private set; }

        public LoginWindow(StationRecord station, NewApiClient apiClient, SettingsStore settingsStore, CoreWebView2Environment environment)
        {
            InitializeComponent();
            _station = station;
            _apiClient = apiClient;
            _settingsStore = settingsStore;
            _environment = environment;
            TitleText.Text = "连接 " + station.Name;
            Loaded += LoginWindow_Loaded;
            Closed += LoginWindow_Closed;
            SourceInitialized += delegate { DwmBackdrop.Apply(this, false); };
        }

        private async void LoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _browserSession = new BrowserSessionService(LoginBrowser, _apiClient, _settingsStore.WebViewDataFolder);
                await _browserSession.InitializeAsync(_environment);
                StatusText.Text = "等待登录…";
                Capture = await _browserSession.CaptureAsync(_station, TimeSpan.FromMinutes(10), _closing.Token);
                StatusText.Text = "已连接";
                DialogResult = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void LoginWindow_Closed(object sender, EventArgs e)
        {
            _closing.Cancel();
            if (_browserSession != null) _browserSession.Dispose();
        }
    }
}
