using BalanceDock.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BalanceDock.Services
{
    public sealed class BrowserCapture
    {
        public BalanceSnapshot Snapshot { get; set; }
        public AuthContext Auth { get; set; }
    }

    public sealed class BrowserSessionService : IDisposable
    {
        private readonly WebView2 _webView;
        private readonly NewApiClient _apiClient;
        private readonly string _userDataFolder;
        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
        private TaskCompletionSource<BrowserCapture> _pending;
        private TaskCompletionSource<UsageStatsSnapshot> _pendingUsage;
        private StationRecord _pendingStation;
        private string _expectedOrigin;
        private string _usageExpectedOrigin;
        private bool _initialized;

        public CoreWebView2Environment Environment { get; private set; }

        public BrowserSessionService(WebView2 webView, NewApiClient apiClient, string userDataFolder)
        {
            _webView = webView;
            _apiClient = apiClient;
            _userDataFolder = userDataFolder;
        }

        public async Task InitializeAsync(CoreWebView2Environment sharedEnvironment)
        {
            if (_initialized) return;
            Environment = sharedEnvironment ?? await CoreWebView2Environment.CreateAsync(null, _userDataFolder, null);
            await _webView.EnsureCoreWebView2Async(Environment);
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.WebResourceResponseReceived += OnWebResourceResponseReceived;
            _initialized = true;
        }

        public async Task<BrowserCapture> CaptureAsync(StationRecord station, TimeSpan timeout, CancellationToken token)
        {
            if (!_initialized) throw new InvalidOperationException("Browser session is not initialized.");
            await _operationGate.WaitAsync(token);
            try
            {
                _expectedOrigin = station.Origin.TrimEnd('/');
                _pendingStation = station;
                _pending = new TaskCompletionSource<BrowserCapture>();
                string separator = station.WalletUrl.Contains("?") ? "&" : "?";
                _webView.CoreWebView2.Navigate(station.WalletUrl + separator + "balancedock=" + DateTime.UtcNow.Ticks);

                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    Task delay = Task.Delay(timeout, timeoutCts.Token);
                    Task finished = await Task.WhenAny(_pending.Task, delay);
                    if (finished == _pending.Task)
                    {
                        timeoutCts.Cancel();
                        return await _pending.Task;
                    }
                }

                token.ThrowIfCancellationRequested();
                throw new TimeoutException("未检测到登录后的余额响应");
            }
            finally
            {
                _pending = null;
                _pendingStation = null;
                _operationGate.Release();
            }
        }

        public async Task<UsageStatsSnapshot> CaptureUsageStatsAsync(StationRecord station, TimeSpan timeout, CancellationToken token)
        {
            if (!_initialized) throw new InvalidOperationException("Browser session is not initialized.");
            await _operationGate.WaitAsync(token);
            try
            {
                _usageExpectedOrigin = station.Origin.TrimEnd('/');
                _pendingUsage = new TaskCompletionSource<UsageStatsSnapshot>();
                _webView.CoreWebView2.Navigate(BuildUsagePageUrl(station, DateTime.UtcNow.Ticks));

                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    Task delay = Task.Delay(timeout, timeoutCts.Token);
                    Task finished = await Task.WhenAny(_pendingUsage.Task, delay);
                    if (finished == _pendingUsage.Task)
                    {
                        timeoutCts.Cancel();
                        return await _pendingUsage.Task;
                    }
                }

                token.ThrowIfCancellationRequested();
                throw new TimeoutException("未检测到使用日志响应");
            }
            finally
            {
                _pendingUsage = null;
                _usageExpectedOrigin = null;
                _operationGate.Release();
            }
        }

        public static string BuildUsagePageUrl(StationRecord station, long cacheBuster)
        {
            var origin = new Uri(station.Origin.TrimEnd('/') + "/");
            return new Uri(origin, "usage-logs/common?pageSize=100&balancedock=" + cacheBuster).AbsoluteUri;
        }

        private async void OnWebResourceResponseReceived(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            TaskCompletionSource<BrowserCapture> pending = _pending;
            TaskCompletionSource<UsageStatsSnapshot> pendingUsage = _pendingUsage;
            if (pending == null && pendingUsage == null) return;

            Uri requestUri;
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out requestUri)) return;
            string origin = requestUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            StationRecord station = _pendingStation;

            try
            {
                if (pending != null
                    && station != null
                    && string.Equals(origin, _expectedOrigin, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(requestUri.AbsolutePath.TrimEnd('/'), NewApiClient.GetBalancePath(station), StringComparison.OrdinalIgnoreCase))
                {
                    // Keep listening after an unauthenticated response. The interactive
                    // login page will issue the same request again after sign-in.
                    if (e.Response.StatusCode == 401 || e.Response.StatusCode == 403) return;

                    string body = await ReadResponseBodyAsync(e);
                    BalanceSnapshot snapshot;
                    if (!_apiClient.TryParseSnapshot(station, body, out snapshot)) return;

                    var auth = new AuthContext
                    {
                        Authorization = TryGetHeader(e.Request.Headers, "Authorization"),
                        UserId = TryGetHeader(e.Request.Headers, "New-Api-User"),
                        CookieHeader = await ReadCookieHeaderAsync(origin),
                        AdditionalHeaders = ReadAdditionalHeaders(e.Request.Headers)
                    };
                    pending.TrySetResult(new BrowserCapture { Snapshot = snapshot, Auth = auth });
                    return;
                }

                if (pendingUsage != null
                    && string.Equals(origin, _usageExpectedOrigin, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(requestUri.AbsolutePath.TrimEnd('/'), "/api/log/self", StringComparison.OrdinalIgnoreCase))
                {
                    if (e.Response.StatusCode == 401 || e.Response.StatusCode == 403) return;
                    if (e.Response.StatusCode < 200 || e.Response.StatusCode >= 300)
                    {
                        pendingUsage.TrySetException(new NewApiException("使用日志请求失败", (HttpStatusCode)e.Response.StatusCode));
                        return;
                    }

                    string body = await ReadResponseBodyAsync(e);
                    UsageStatsSnapshot usage;
                    if (_apiClient.TryParseUsageStats(body, out usage)) pendingUsage.TrySetResult(usage);
                    else pendingUsage.TrySetException(new NewApiException("使用日志格式不兼容", (HttpStatusCode)e.Response.StatusCode));
                }
            }
            catch (Exception ex)
            {
                if (pending != null) pending.TrySetException(ex);
                if (pendingUsage != null) pendingUsage.TrySetException(ex);
            }
        }

        private static async Task<string> ReadResponseBodyAsync(CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            using (Stream content = await e.Response.GetContentAsync())
            using (var reader = new StreamReader(content, Encoding.UTF8))
                return await reader.ReadToEndAsync();
        }

        private async Task<string> ReadCookieHeaderAsync(string origin)
        {
            IReadOnlyList<CoreWebView2Cookie> cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync(origin);
            return string.Join("; ", cookies.Select(c => c.Name + "=" + c.Value).ToArray());
        }

        private static string TryGetHeader(CoreWebView2HttpRequestHeaders headers, string name)
        {
            try { return headers.GetHeader(name); }
            catch { return null; }
        }

        private static IDictionary<string, string> ReadAdditionalHeaders(CoreWebView2HttpRequestHeaders headers)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> header in headers)
                result[header.Key] = header.Value;
            return result;
        }

        public void Dispose()
        {
            if (_initialized && _webView.CoreWebView2 != null)
                _webView.CoreWebView2.WebResourceResponseReceived -= OnWebResourceResponseReceived;
        }
    }
}
