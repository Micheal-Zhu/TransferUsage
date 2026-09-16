using BalanceDock.Models;
using BalanceDock.Native;
using BalanceDock.Services;
using BalanceDock.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BalanceDock
{
    public partial class MainWindow : Window
    {
        private const double CollapsedWidth = 400d;
        private const double ExpandedWidth = 730d;
        private readonly SettingsStore _settingsStore;
        private readonly NewApiClient _apiClient;
        private readonly AppSettings _appSettings;
        private readonly DispatcherTimer _pollTimer;
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _browserGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();
        private readonly UsageFocusTracker _usageFocus = new UsageFocusTracker(TimeSpan.FromMinutes(5d));
        private readonly Dictionary<string, DateTime> _nextUsageRefreshUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly HashSet<string> _browserUsageStations = new HashSet<string>(StringComparer.Ordinal);
        private TrayIconService _tray;
        private BrowserSessionService _browserSession;
        private bool _expanded;
        private bool _allowExit;
        private bool _darkTheme;
        private bool _browserReady;

        public ObservableCollection<StationViewModel> Stations { get; private set; }
        public ObservableCollection<StationViewModel> DisplayedStations { get; private set; }

        public MainWindow()
        {
            Diagnostics.Write("MainWindow ctor before InitializeComponent");
            InitializeComponent();
            Diagnostics.Write("MainWindow ctor after InitializeComponent");
            _settingsStore = new SettingsStore();
            _apiClient = new NewApiClient();
            _appSettings = _settingsStore.LoadSettings();
            Stations = new ObservableCollection<StationViewModel>(
                _settingsStore.LoadStations().Where(s => s != null && !string.IsNullOrWhiteSpace(s.Origin)).Select(s => new StationViewModel(s)));
            AddDiagnosticStations();
            DisplayedStations = new ObservableCollection<StationViewModel>(Stations);
            DataContext = this;
            if (_usageFocus.Count > 0) SynchronizeDisplayedStations();
            Topmost = _appSettings.Topmost;

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _pollTimer.Tick += async delegate { await RefreshAllAsync(); };

            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            Closed += MainWindow_Closed;
            IsVisibleChanged += delegate { Diagnostics.Write("MainWindow IsVisibleChanged=" + IsVisible); };
            StateChanged += delegate { Diagnostics.Write("MainWindow StateChanged=" + WindowState); };
            Stations.CollectionChanged += delegate { SynchronizeDisplayedStations(); };
            ApplyTheme();
            UpdateCollectionState();
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            DwmBackdrop.Apply(this, _darkTheme);
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Diagnostics.Write("MainWindow Loaded begin visible=" + IsVisible);
            RestorePosition();
            UpdateWindowHeight(false);
            if (string.Equals(Environment.GetEnvironmentVariable("BALANCEDOCK_EXPANDED"), "1", StringComparison.Ordinal))
            {
                int expandDelay;
                if (int.TryParse(Environment.GetEnvironmentVariable("BALANCEDOCK_EXPAND_DELAY_MS"), out expandDelay) && expandDelay > 0)
                {
                    var expandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(expandDelay) };
                    expandTimer.Tick += delegate { expandTimer.Stop(); ToggleAddPanel(true); };
                    expandTimer.Start();
                }
                else ToggleAddPanel(true);
            }
            string diagnosticUrl = Environment.GetEnvironmentVariable("BALANCEDOCK_DEMO_URL");
            if (!string.IsNullOrWhiteSpace(diagnosticUrl)) UrlInput.Text = diagnosticUrl;
            ScheduleDiagnosticScreenshot();
            _tray = new TrayIconService(_appSettings.Topmost, _appSettings.RunAtStartup);
            _tray.ShowRequested += delegate { ShowFromTray(); };
            _tray.ExitRequested += delegate { ExitApplication(); };
            _tray.TopmostChanged += delegate
            {
                Topmost = _tray.TopmostChecked;
                _appSettings.Topmost = Topmost;
                SaveSettings();
            };
            _tray.StartupChanged += delegate
            {
                _appSettings.RunAtStartup = _tray.StartupChecked;
                StartupService.SetEnabled(_appSettings.RunAtStartup);
                SaveSettings();
            };
#if !DEBUG
            StartupService.SetEnabled(_appSettings.RunAtStartup);
#endif

            if (Stations.Count > 0)
            {
                try
                {
                    await InitializeBrowserSessionAsync(true);
                }
                catch (Exception ex)
                {
                    Diagnostics.Write("Browser initialize exception " + ex);
                    HeaderStatus.Text = "浏览器不可用";
                    foreach (StationViewModel station in Stations)
                    {
                        station.NeedsLogin = true;
                        station.StatusText = ex.Message;
                    }
                }
            }
            _pollTimer.Start();
            Diagnostics.Write("MainWindow Loaded end visible=" + IsVisible);
        }

        private async Task InitializeBrowserSessionAsync(bool restoreSessions)
        {
            if (_browserReady || _closing.IsCancellationRequested) return;
            Diagnostics.Write("Browser initialize begin");
            SessionBrowser.Visibility = Visibility.Visible;
            _browserSession = new BrowserSessionService(SessionBrowser, _apiClient, _settingsStore.WebViewDataFolder);
            await _browserSession.InitializeAsync(null);
            _browserReady = true;
            Diagnostics.Write("Browser initialize complete");
            if (restoreSessions) await RestoreSessionsAsync();
        }

        private void ScheduleBrowserInitialization()
        {
            if (_browserReady || _closing.IsCancellationRequested) return;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += async delegate
            {
                timer.Stop();
                try { await InitializeBrowserSessionAsync(false); }
                catch (Exception ex) { Diagnostics.Write("Deferred browser initialize exception " + ex); }
            };
            timer.Start();
        }

        private async Task RestoreSessionsAsync()
        {
            foreach (StationViewModel station in Stations.ToArray())
            {
                if (_closing.IsCancellationRequested) break;
                await RecoverSessionAsync(station, TimeSpan.FromSeconds(12));
            }
        }

        private async Task RecoverSessionAsync(StationViewModel station, TimeSpan timeout)
        {
            if (!_browserReady || _browserSession == null)
            {
                station.NeedsLogin = true;
                station.StatusText = "需要登录";
                return;
            }

            await _browserGate.WaitAsync();
            try
            {
                station.IsRefreshing = true;
                station.StatusText = "正在连接";
                BrowserCapture capture = await _browserSession.CaptureAsync(station.Record, timeout, _closing.Token);
                station.Auth = capture.Auth;
                if (ApplySnapshot(station, capture.Snapshot, true)) SaveStations();
                await RefreshUsageStatsIfDueAsync(station);
            }
            catch (OperationCanceledException) { }
            catch
            {
                station.NeedsLogin = true;
                station.StatusText = "需要登录";
            }
            finally
            {
                station.IsRefreshing = false;
                _browserGate.Release();
            }
        }

        private async Task RefreshAllAsync()
        {
            if (!await _refreshGate.WaitAsync(0)) return;
            try
            {
                foreach (StationViewModel station in Stations.ToArray())
                {
                    if (_closing.IsCancellationRequested) return;
                    if (station.Auth == null || !station.Auth.HasCredentials || station.NeedsLogin) continue;
                    await RefreshStationDirectAsync(station, true);
                }
            }
            finally
            {
                if (_usageFocus.RemoveExpired(DateTime.UtcNow)) SynchronizeDisplayedStations();
                _refreshGate.Release();
            }
        }

        private async Task RefreshStationDirectAsync(StationViewModel station, bool recoverOnUnauthorized)
        {
            bool shouldRecover = false;
            bool balanceRefreshed = false;
            station.IsRefreshing = true;
            try
            {
                BalanceSnapshot snapshot = await _apiClient.GetBalanceAsync(station.Record, station.Auth, _closing.Token);
                if (ApplySnapshot(station, snapshot, true)) SaveStations();
                balanceRefreshed = true;
            }
            catch (OperationCanceledException) { }
            catch (NewApiException ex)
            {
                if (recoverOnUnauthorized && (ex.StatusCode == HttpStatusCode.Unauthorized
                    || ex.StatusCode == HttpStatusCode.Forbidden
                    || (string.Equals(station.Record.Provider, "deepseek", StringComparison.OrdinalIgnoreCase)
                        && ex.StatusCode == (HttpStatusCode)429)))
                    shouldRecover = true;
                else
                    station.StatusText = ex.Message;
            }
            catch
            {
                station.StatusText = "网络异常";
            }
            finally { station.IsRefreshing = false; }
            if (shouldRecover) await RecoverSessionAsync(station, TimeSpan.FromSeconds(12));
            else if (balanceRefreshed) await RefreshUsageStatsIfDueAsync(station);
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            Uri origin;
            Uri wallet;
            string error;
            if (!UrlNormalizer.TryNormalize(UrlInput.Text, out origin, out wallet, out error))
            {
                SetAddStatus(error, true);
                return;
            }
            if (Stations.Any(s => string.Equals(s.Record.Origin.TrimEnd('/'), origin.GetLeftPart(UriPartial.Authority).TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
            {
                SetAddStatus("这个站点已经添加", true);
                return;
            }

            ConnectButton.IsEnabled = false;
            SetAddStatus("正在识别站点…", false);
            try
            {
                StationRecord record = await _apiClient.DiscoverAsync(origin, wallet, _closing.Token);
                SetAddStatus("已识别 " + record.Name, false);
                BrowserCapture capture = await OpenLoginAsync(record);
                if (capture == null)
                {
                    SetAddStatus("尚未完成登录", true);
                    return;
                }

                var station = new StationViewModel(record) { Auth = capture.Auth };
                ApplySnapshot(station, capture.Snapshot, false);
                Stations.Add(station);
                SaveStations();
                await RefreshUsageStatsIfDueAsync(station);
                UrlInput.Clear();
                SetAddStatus(null, false);
                ToggleAddPanel(false);
                ScheduleBrowserInitialization();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetAddStatus(ex.Message, true);
            }
            finally { ConnectButton.IsEnabled = !string.IsNullOrWhiteSpace(UrlInput.Text); }
        }

        private async Task<BrowserCapture> OpenLoginAsync(StationRecord record)
        {
            CoreWebView2Environment environment = _browserSession == null ? null : _browserSession.Environment;
            var login = new LoginWindow(record, _apiClient, _settingsStore, environment)
            {
                Owner = this,
                Topmost = Topmost
            };
            bool? result = login.ShowDialog();
            await Task.Yield();
            return result == true ? login.Capture : null;
        }

        private async Task ReloginAsync(StationViewModel station)
        {
            BrowserCapture capture = await OpenLoginAsync(station.Record);
            if (capture == null) return;
            station.Auth = capture.Auth;
            if (ApplySnapshot(station, capture.Snapshot, true)) SaveStations();
            await RefreshUsageStatsIfDueAsync(station);
        }

        private void SetAddStatus(string text, bool error)
        {
            AddStatus.Text = text ?? string.Empty;
            AddStatus.Foreground = (Brush)FindResource(error ? "ErrorBrush" : "TextSecondaryBrush");
        }

        private void AddToggleButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleAddPanel(!_expanded);
        }

        private void ToggleAddPanel(bool expand)
        {
            if (_expanded == expand) return;
            _expanded = expand;
            double targetWidth = expand ? ExpandedWidth : CollapsedWidth;
            Rect area = SystemParameters.WorkArea;
            double right = Left + ActualWidth;
            double targetLeft = Math.Max(area.Left + 8, right - targetWidth);
            double targetHeight = GetTargetWindowHeight();

            if (expand)
            {
                // A collapse may have left the desktop-colored cover in flight;
                // remove it before the slide-in.
                CollapseCover.Opacity = 0;
                CollapseCover.Visibility = Visibility.Collapsed;
                AddPanel.Visibility = Visibility.Visible;
                // Resize the window once, before the slide-in starts. The MainPanel is
                // right-anchored, so a combined bounds change keeps it pixel-stable; the
                // panel region is still fully faded out, so the jump is not visible.
                if (Math.Abs(ActualWidth - targetWidth) > 0.5d || Math.Abs(ActualHeight - targetHeight) > 0.5d)
                    ApplyWindowBounds(targetLeft, Top, targetWidth, targetHeight);
            }

            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(220));

            AddPanelTransform.X = expand ? 0 : 24;
            AddPanelTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                new DoubleAnimation(expand ? 24 : 0, expand ? 0 : 24, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            var opacity = new DoubleAnimation(expand ? 0 : 1, expand ? 1 : 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            AddPanel.Opacity = expand ? 1 : 0;
            opacity.Completed += delegate
            {
                if (expand)
                {
                    if (_expanded)
                    {
                        Diagnostics.Write("Expand animation complete");
                        UrlInput.Focus();
                    }
                }
                else if (!_expanded)
                {
                    AddPanel.Opacity = 0;
                    AddPanel.Visibility = Visibility.Collapsed;
                    // The panel region is now an empty light surface over the
                    // desktop; removing it abruptly reads as a flash. Fade in a
                    // cover colored like the desktop beside the dock, so the
                    // region matches its surroundings before the bounds jump.
                    CollapseCover.Background = new SolidColorBrush(SampleDesktopColor());
                    CollapseCover.Visibility = Visibility.Visible;
                    CollapseCover.Opacity = 0;
                    var coverFade = new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(80))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                        FillBehavior = FillBehavior.Stop
                    };
                    coverFade.Completed += delegate
                    {
                        if (_expanded) return; // an expand interrupted the collapse
                        Rect workArea = SystemParameters.WorkArea;
                        double collapseLeft = Math.Max(workArea.Left + 8, Left + ActualWidth - CollapsedWidth);
                        ApplyWindowBounds(collapseLeft, Top, CollapsedWidth, GetTargetWindowHeight());
                        CollapseCover.Opacity = 0;
                        CollapseCover.Visibility = Visibility.Collapsed;
                        Diagnostics.Write("Collapse animation complete");
                    };
                    CollapseCover.BeginAnimation(OpacityProperty, coverFade);
                }
            };
            AddPanel.BeginAnimation(OpacityProperty, opacity);

            AddIconRotation.Angle = expand ? 45 : 0;
            AddIconRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new DoubleAnimation(expand ? 0 : 45, expand ? 45 : 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            AddToggleButton.ToolTip = expand ? "收起" : "添加中转站";
        }

        private double GetTargetWindowHeight()
        {
            double listHeight = DisplayedStations.Count == 0 ? 182d : Math.Min(592d, 66d + DisplayedStations.Count * 76d);
            double target = _expanded ? Math.Max(198d, listHeight) : listHeight;
            return Math.Min(592d, Math.Max(142d, target));
        }

        // Reads one desktop pixel beside the dock so the collapse cover matches
        // whatever is behind the panel region.
        private Color SampleDesktopColor()
        {
            try
            {
                Point screen = PointToScreen(new Point(0, 0));
                PresentationSource source = PresentationSource.FromVisual(this);
                if (source != null && source.CompositionTarget != null)
                    screen = source.CompositionTarget.TransformToDevice.Transform(screen);
                int x = Math.Max(0, (int)screen.X - 8);
                int y = Math.Max(0, (int)screen.Y + 100);
                using (var bitmap = new System.Drawing.Bitmap(1, 1))
                using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(1, 1));
                    System.Drawing.Color pixel = bitmap.GetPixel(0, 0);
                    return Color.FromRgb(pixel.R, pixel.G, pixel.B);
                }
            }
            catch
            {
                return Color.FromRgb(18, 18, 18); // quiet dark fallback
            }
        }

        // One atomic bounds change applied to the native window. The MainPanel is
        // right-anchored, so changing width and left together keeps every visible
        // element at the same screen position; no per-frame resizing ever happens.
        private void ApplyWindowBounds(double left, double top, double width, double height)
        {
            DwmBackdrop.TrySetBounds(this, left, top, width, height);
            Left = left;
            Width = width;
            Height = height;
        }

        private void UpdateWindowHeight(bool animate)
        {
            double target = GetTargetWindowHeight();
            if (!animate)
            {
                Height = target;
                return;
            }
            double old = ActualHeight;
            Height = target;
            var animation = new DoubleAnimation(old, target, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            BeginAnimation(HeightProperty, animation);
        }

        private void StationMenuButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var station = button == null ? null : button.Tag as StationViewModel;
            if (station == null) return;

            var menu = new ContextMenu();
            var refresh = new MenuItem { Header = "立即刷新" };
            var relogin = new MenuItem { Header = "重新登录" };
            var open = new MenuItem { Header = "打开钱包" };
            var remove = new MenuItem { Header = "移除站点", Foreground = (Brush)FindResource("ErrorBrush") };
            refresh.Click += async delegate
            {
                if (station.Auth == null || !station.Auth.HasCredentials || station.NeedsLogin) await ReloginAsync(station);
                else await RefreshStationDirectAsync(station, true);
            };
            relogin.Click += async delegate { await ReloginAsync(station); };
            open.Click += delegate { Process.Start(new ProcessStartInfo(station.Record.WalletUrl) { UseShellExecute = true }); };
            remove.Click += delegate { RemoveStation(station); };
            menu.Items.Add(refresh);
            menu.Items.Add(relogin);
            menu.Items.Add(open);
            menu.Items.Add(new Separator());
            menu.Items.Add(remove);
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void RemoveStation(StationViewModel station)
        {
            MessageBoxResult result = MessageBox.Show(this, "移除 “" + station.Name + "”？", "Balance Dock", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (result != MessageBoxResult.OK) return;
            _usageFocus.Remove(station.Id);
            _nextUsageRefreshUtc.Remove(station.Id);
            Stations.Remove(station);
            SaveStations();
        }

        private void UrlInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ConnectButton.IsEnabled) ConnectButton_Click(ConnectButton, new RoutedEventArgs());
        }

        private void UrlInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (UrlPlaceholder != null)
                UrlPlaceholder.Visibility = string.IsNullOrEmpty(UrlInput.Text) ? Visibility.Visible : Visibility.Collapsed;
            if (ConnectButton != null)
                ConnectButton.IsEnabled = !string.IsNullOrWhiteSpace(UrlInput.Text);
        }

        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Diagnostics.Write("Drag preview source=" + (e.OriginalSource == null ? "null" : e.OriginalSource.GetType().Name) +
                " button=" + e.ChangedButton + " state=" + e.LeftButton);
            if (e.ChangedButton != MouseButton.Left || e.LeftButton != MouseButtonState.Pressed) return;

            DependencyObject element = e.OriginalSource as DependencyObject;
            while (element != null)
            {
                if (element is ButtonBase || element is TextBoxBase || element is ScrollBar)
                {
                    Diagnostics.Write("Drag ignored interactive=" + element.GetType().Name);
                    return;
                }
                element = VisualTreeHelper.GetParent(element);
            }

            if (DwmBackdrop.BeginWindowDrag(this))
            {
                Diagnostics.Write("Drag native move completed");
                e.Handled = true;
            }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = _appSettings.Topmost;
        }

        private void ExitApplication()
        {
            _allowExit = true;
            Close();
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            Diagnostics.Write("MainWindow Closing allowExit=" + _allowExit);
            if (!_allowExit && !Application.Current.Dispatcher.HasShutdownStarted)
            {
                e.Cancel = true;
                SaveWindowPosition();
                Hide();
                Diagnostics.Write("MainWindow Closing canceled and hidden");
            }
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _pollTimer.Stop();
            _closing.Cancel();
            SaveStations();
            SaveWindowPosition();
            if (_browserSession != null) _browserSession.Dispose();
            if (_tray != null) _tray.Dispose();
            _apiClient.Dispose();
            Application.Current.Shutdown();
        }

        private void UpdateCollectionState()
        {
            bool empty = Stations == null || Stations.Count == 0;
            if (EmptyState != null) EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (StationList != null) StationList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            if (StationList != null)
                ScrollViewer.SetVerticalScrollBarVisibility(StationList, DisplayedStations.Count > 6 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden);
            if (HeaderStatus != null)
                HeaderStatus.Text = empty ? "余额" : (_usageFocus.Count > 0 ? "正在使用 · " + DisplayedStations.Count + " 个站点" : Stations.Count + " 个站点");
            if (IsLoaded) UpdateWindowHeight(true);
        }

        private bool ApplySnapshot(StationViewModel station, BalanceSnapshot snapshot, bool detectUsage)
        {
            bool hasPreviousObservation = station.UpdatedAt != default(DateTime)
                || station.Record.ProgressBaselineQuota > 0L
                || station.Record.LastObservedQuota > 0L;
            long previousQuota = Math.Max(0L, station.Record.LastObservedQuota);
            bool focusChanged = detectUsage && _usageFocus.Observe(
                station.Id, previousQuota, Math.Max(0L, snapshot.Quota), hasPreviousObservation, DateTime.UtcNow);
            bool baselineChanged = station.Apply(snapshot);
            if (focusChanged) SynchronizeDisplayedStations();
            return baselineChanged;
        }

        private async Task RefreshUsageStatsIfDueAsync(StationViewModel station)
        {
            if (!NewApiClient.SupportsUsageLogs(station.Record) || station.Auth == null || !station.Auth.HasCredentials) return;
            DateTime now = DateTime.UtcNow;
            DateTime nextRefresh;
            if (_nextUsageRefreshUtc.TryGetValue(station.Id, out nextRefresh) && now < nextRefresh) return;
            _nextUsageRefreshUtc[station.Id] = now.AddMinutes(1d);

            try
            {
                UsageStatsSnapshot snapshot = null;
                bool useBrowser = _browserUsageStations.Contains(station.Id);
                if (!useBrowser)
                {
                    try
                    {
                        snapshot = await _apiClient.GetUsageStatsAsync(station.Record, station.Auth, _closing.Token);
                    }
                    catch (NewApiException ex)
                    {
                        if (ex.StatusCode != HttpStatusCode.Unauthorized && ex.StatusCode != HttpStatusCode.Forbidden) throw;
                        _browserUsageStations.Add(station.Id);
                        useBrowser = true;
                    }
                }
                if (useBrowser) snapshot = await CaptureUsageStatsInBrowserAsync(station);
                station.ApplyUsage(snapshot);
                Diagnostics.Write("Usage log refresh succeeded station=" + station.Origin
                    + " source=" + (useBrowser ? "browser" : "http")
                    + " promptTokens=" + snapshot.PromptTokens
                    + " cachedTokens=" + snapshot.CachedTokens
                    + " logs=" + snapshot.LogCount);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Diagnostics.Write("Usage log refresh failed station=" + station.Origin + " error=" + ex.Message);
            }
        }

        private Task<UsageStatsSnapshot> CaptureUsageStatsInBrowserAsync(StationViewModel station)
        {
            if (!_browserReady || _browserSession == null)
                throw new InvalidOperationException("Browser session is not ready.");
            return _browserSession.CaptureUsageStatsAsync(station.Record, TimeSpan.FromSeconds(15), _closing.Token);
        }

        private void SynchronizeDisplayedStations()
        {
            foreach (string stationId in _usageFocus.ActiveStationIds.ToArray())
            {
                if (!Stations.Any(station => string.Equals(station.Id, stationId, StringComparison.Ordinal)))
                    _usageFocus.Remove(stationId);
            }

            var activeIds = new HashSet<string>(_usageFocus.ActiveStationIds, StringComparer.Ordinal);
            foreach (StationViewModel station in Stations)
                station.IsUsageFocused = activeIds.Contains(station.Id);

            StationViewModel[] target = _usageFocus.Count == 0
                ? Stations.ToArray()
                : _usageFocus.ActiveStationIds
                    .Select(stationId => Stations.FirstOrDefault(station => string.Equals(station.Id, stationId, StringComparison.Ordinal)))
                    .Where(station => station != null)
                    .ToArray();

            if (!DisplayedStations.SequenceEqual(target))
            {
                DisplayedStations.Clear();
                foreach (StationViewModel station in target) DisplayedStations.Add(station);
            }
            UpdateCollectionState();
        }

        private void SaveStations()
        {
            _settingsStore.SaveStations(Stations.Select(s => s.Record));
        }

        private void SaveSettings()
        {
            _settingsStore.SaveSettings(_appSettings);
        }

        private void SaveWindowPosition()
        {
            if (WindowState != WindowState.Normal) return;
            _appSettings.Left = Left;
            _appSettings.Top = Top;
            _appSettings.HasWindowPosition = true;
            SaveSettings();
        }

        private void RestorePosition()
        {
            Rect area = SystemParameters.WorkArea;
            if (_appSettings.HasWindowPosition)
            {
                Left = Math.Max(area.Left + 8, Math.Min(_appSettings.Left, area.Right - Width - 8));
                Top = Math.Max(area.Top + 8, Math.Min(_appSettings.Top, area.Bottom - Height - 8));
            }
            else
            {
                Left = area.Right - Width - 24;
                Top = area.Top + 24;
            }
        }

        private void ApplyTheme()
        {
            string requestedTheme = Environment.GetEnvironmentVariable("BALANCEDOCK_THEME");
            if (string.Equals(requestedTheme, "dark", StringComparison.OrdinalIgnoreCase))
                _darkTheme = true;
            else if (string.Equals(requestedTheme, "light", StringComparison.OrdinalIgnoreCase))
                _darkTheme = false;
            else try
            {
                object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                _darkTheme = Convert.ToInt32(value) == 0;
            }
            catch { _darkTheme = false; }

            if (_darkTheme)
            {
                SetBrush("TextPrimaryBrush", "#F2ECECEC");
                SetBrush("TextSecondaryBrush", "#A5A6AA");
                SetBrush("PlaceholderBrush", "#8C8E93");
                SetBrush("HairlineBrush", "#24FFFFFF");
                SetBrush("SurfaceBrush", "#DF151618");
                SetBrush("SurfaceStrongBrush", "#F01C1D20");
                SetBrush("PanelBrush", "#16FFFFFF");
                SetBrush("TrackBrush", "#20FFFFFF");
                SetBrush("HoverBrush", "#12FFFFFF");
                SetBrush("PressedBrush", "#1FFFFFFF");
                SetBrush("CommandBrush", "#F1F1F1");
                SetBrush("CommandForegroundBrush", "#202124");
            }
        }

        private static void SetBrush(string key, string color)
        {
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }

        private void AddDiagnosticStations()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("BALANCEDOCK_DEMO"), "1", StringComparison.Ordinal) || Stations.Count != 0) return;
            var custom = new CurrencySettings { DisplayInCurrency = true, DisplayType = "CUSTOM", QuotaPerUnit = 500000d, CustomExchangeRate = 1d, CustomSymbol = "⚡" };
            var flash = new StationViewModel(new StationRecord { Name = "Flash Code", Origin = "https://api.flashpocket.cn", WalletUrl = "https://api.flashpocket.cn/wallet", Currency = custom });
            flash.Apply(new BalanceSnapshot { Quota = 1105000, UsedQuota = 11445000, RequestCount = 1494, UpdatedAt = DateTime.Now });
            if (string.Equals(Environment.GetEnvironmentVariable("BALANCEDOCK_DEMO_CACHE"), "1", StringComparison.Ordinal))
                flash.ApplyUsage(new UsageStatsSnapshot { PromptTokens = 12000L, CachedTokens = 7200L, LogCount = 100, UpdatedAt = DateTime.Now });
            Stations.Add(flash);
            var moon = new StationViewModel(new StationRecord { Name = "Moon API", Origin = "https://api.example.com", WalletUrl = "https://api.example.com/wallet", Currency = new CurrencySettings() });
            moon.Apply(new BalanceSnapshot { Quota = 9250000, UsedQuota = 3250000, RequestCount = 428, UpdatedAt = DateTime.Now });
            Stations.Add(moon);

            int focusedCount;
            if (int.TryParse(Environment.GetEnvironmentVariable("BALANCEDOCK_DEMO_FOCUS_COUNT"), out focusedCount))
            {
                DateTime observedAt = DateTime.UtcNow;
                if (focusedCount >= 1) _usageFocus.Observe(flash.Id, flash.Quota + 1L, flash.Quota, true, observedAt);
                if (focusedCount >= 2) _usageFocus.Observe(moon.Id, moon.Quota + 1L, moon.Quota, true, observedAt);
            }
        }

        private void ScheduleDiagnosticScreenshot()
        {
            string path = Environment.GetEnvironmentVariable("BALANCEDOCK_SCREENSHOT");
            if (string.IsNullOrWhiteSpace(path)) return;
            int requestedDelay;
            double screenshotDelay = int.TryParse(Environment.GetEnvironmentVariable("BALANCEDOCK_SCREENSHOT_DELAY_MS"), out requestedDelay)
                ? Math.Max(100, requestedDelay) : 550;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(screenshotDelay) };
            timer.Tick += delegate
            {
                timer.Stop();
                try
                {
                    int width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
                    int height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
                    var bitmap = new RenderTargetBitmap(width, height, 96d, 96d, PixelFormats.Pbgra32);
                    bitmap.Render(this);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    string folder = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
                    using (FileStream stream = File.Create(path)) encoder.Save(stream);
                    Diagnostics.Write("Diagnostic screenshot saved " + path);
                    if (string.Equals(Environment.GetEnvironmentVariable("BALANCEDOCK_EXIT_AFTER_SCREENSHOT"), "1", StringComparison.Ordinal))
                        Application.Current.Shutdown();
                }
                catch (Exception ex) { Diagnostics.Write("Diagnostic screenshot failed " + ex); }
            };
            timer.Start();
        }
    }
}
