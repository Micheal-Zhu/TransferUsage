using BalanceDock.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BalanceDock.Services
{
    public sealed class NewApiException : Exception
    {
        public HttpStatusCode? StatusCode { get; private set; }
        public NewApiException(string message, HttpStatusCode? statusCode) : base(message) { StatusCode = statusCode; }
    }

    public sealed class NewApiClient : IDisposable
    {
        private readonly HttpClient _client;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        public NewApiClient()
        {
            // .NET Framework 4.5 defaults to legacy TLS on some Windows installations.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false
            };
            _client = new HttpClient(handler);
            _client.Timeout = TimeSpan.FromSeconds(10);
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("BalanceDock/1.0");
        }

        public async Task<StationRecord> DiscoverAsync(Uri origin, Uri wallet, CancellationToken token)
        {
            if (string.Equals(origin.Host, "platform.deepseek.com", StringComparison.OrdinalIgnoreCase))
            {
                return new StationRecord
                {
                    Provider = "deepseek",
                    Origin = origin.GetLeftPart(UriPartial.Authority),
                    WalletUrl = new Uri(origin, "/usage").AbsoluteUri,
                    Name = "DeepSeek",
                    Currency = CreateDeepSeekCurrency("CNY")
                };
            }

            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "/api/status"));
            var response = await SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new NewApiException("站点状态接口不可用", response.StatusCode);

            IDictionary<string, object> root = ParseObject(body);
            if (!GetBool(root, "success", false)) throw new NewApiException("这不是受支持的 New API 站点", response.StatusCode);
            IDictionary<string, object> data = GetObject(root, "data");
            if (data == null || !data.ContainsKey("quota_per_unit"))
                throw new NewApiException("站点缺少余额配置", response.StatusCode);

            return new StationRecord
            {
                Provider = "new-api",
                Origin = origin.GetLeftPart(UriPartial.Authority),
                WalletUrl = wallet.AbsoluteUri,
                Name = GetString(data, "system_name", origin.Host),
                LogoUrl = GetString(data, "logo", null),
                Currency = ParseCurrency(data)
            };
        }

        public async Task<BalanceSnapshot> GetBalanceAsync(StationRecord station, AuthContext auth, CancellationToken token)
        {
            string endpoint = IsDeepSeek(station) ? "/api/v0/users/get_user_summary" : "/api/user/self";
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(station.Origin), endpoint));
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
            if (IsDeepSeek(station)) request.Headers.UserAgent.Clear();
            ApplyAuthentication(request, auth);

            var response = await SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new NewApiException("登录已过期", response.StatusCode);
            if (!response.IsSuccessStatusCode)
                throw new NewApiException("余额请求失败", response.StatusCode);

            BalanceSnapshot snapshot;
            if (!TryParseSnapshot(station, body, out snapshot))
            {
                if (IsDeepSeek(station)) throw new NewApiException("DeepSeek 登录状态需要恢复", HttpStatusCode.Unauthorized);
                throw new NewApiException("余额响应格式不兼容", response.StatusCode);
            }
            return snapshot;
        }

        public async Task<UsageStatsSnapshot> GetUsageStatsAsync(StationRecord station, AuthContext auth, CancellationToken token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(new Uri(station.Origin), "/api/log/self?p=1&page_size=100"));
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
            ApplyAuthentication(request, auth);

            var response = await SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new NewApiException("日志登录状态已过期", response.StatusCode);
            if (!response.IsSuccessStatusCode)
                throw new NewApiException("使用日志请求失败", response.StatusCode);

            UsageStatsSnapshot snapshot;
            if (!TryParseUsageStats(body, out snapshot))
                throw new NewApiException("使用日志格式不兼容", response.StatusCode);
            return snapshot;
        }

        private static void ApplyAuthentication(HttpRequestMessage request, AuthContext auth)
        {
            if (auth == null) return;
            if (!string.IsNullOrWhiteSpace(auth.Authorization)) request.Headers.TryAddWithoutValidation("Authorization", auth.Authorization);
            if (!string.IsNullOrWhiteSpace(auth.UserId)) request.Headers.TryAddWithoutValidation("New-Api-User", auth.UserId);
            if (!string.IsNullOrWhiteSpace(auth.CookieHeader)) request.Headers.TryAddWithoutValidation("Cookie", auth.CookieHeader);
            if (auth.AdditionalHeaders == null) return;
            foreach (KeyValuePair<string, string> header in auth.AdditionalHeaders)
            {
                if (IsUnsafeReplayHeader(header.Key)) continue;
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        private static bool IsUnsafeReplayHeader(string name)
        {
            return string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Accept-Encoding", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            try
            {
                return await _client.SendAsync(request, token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                if (token.IsCancellationRequested) throw;
                throw new NewApiException("连接站点超时，请稍后重试", null);
            }
            catch (HttpRequestException)
            {
                throw new NewApiException("无法建立 HTTPS 连接，请检查网络或代理设置", null);
            }
        }

        public bool TryParseSnapshot(string body, out BalanceSnapshot snapshot)
        {
            snapshot = null;
            try
            {
                IDictionary<string, object> root = ParseObject(body);
                if (!GetBool(root, "success", false)) return false;
                IDictionary<string, object> data = GetObject(root, "data");
                if (data == null || !data.ContainsKey("quota")) return false;
                snapshot = new BalanceSnapshot
                {
                    Quota = GetLong(data, "quota", 0L),
                    UsedQuota = GetLong(data, "used_quota", 0L),
                    RequestCount = GetLong(data, "request_count", 0L),
                    UpdatedAt = DateTime.Now
                };
                return true;
            }
            catch { return false; }
        }

        public bool TryParseSnapshot(StationRecord station, string body, out BalanceSnapshot snapshot)
        {
            if (!IsDeepSeek(station)) return TryParseSnapshot(body, out snapshot);

            snapshot = null;
            try
            {
                IDictionary<string, object> root = ParseObject(body);
                IDictionary<string, object> data = GetObject(root, "data");
                IDictionary<string, object> business = GetObject(data, "biz_data");
                IList<object> wallets = GetList(business, "normal_wallets");
                if (wallets == null || wallets.Count == 0) return false;

                IDictionary<string, object> wallet = SelectMoneyEntry(wallets, "CNY");
                if (wallet == null) return false;
                string currency = GetString(wallet, "currency", "CNY").ToUpperInvariant();
                decimal balance = GetDecimal(wallet, "balance", 0m);
                decimal totalCost = 0m;
                IList<object> costs = GetList(business, "total_costs");
                IDictionary<string, object> cost = SelectMoneyEntry(costs, currency);
                if (cost != null && string.Equals(GetString(cost, "currency", null), currency, StringComparison.OrdinalIgnoreCase))
                    totalCost = GetDecimal(cost, "amount", 0m);

                station.Currency = CreateDeepSeekCurrency(currency);
                snapshot = new BalanceSnapshot
                {
                    Quota = ToMinorUnits(balance),
                    UsedQuota = ToMinorUnits(totalCost),
                    RequestCount = 0L,
                    UpdatedAt = DateTime.Now
                };
                return true;
            }
            catch { return false; }
        }

        public bool TryParseUsageStats(string body, out UsageStatsSnapshot snapshot)
        {
            snapshot = null;
            try
            {
                IDictionary<string, object> root = ParseObject(body);
                if (!GetBool(root, "success", false)) return false;

                object dataValue;
                if (root == null || !root.TryGetValue("data", out dataValue)) return false;
                IDictionary<string, object> dataObject = dataValue as IDictionary<string, object>;
                IList<object> logs = dataObject == null ? AsList(dataValue) : GetList(dataObject, "items");
                if (logs == null) return false;

                long promptTokens = 0L;
                long cachedTokens = 0L;
                int logCount = 0;
                foreach (object value in logs)
                {
                    IDictionary<string, object> log = value as IDictionary<string, object>;
                    if (log == null) continue;
                    long prompt = Math.Max(0L, GetLong(log, "prompt_tokens", 0L));
                    if (prompt <= 0L) continue;

                    long cached = Math.Max(0L, GetLong(log, "cache_tokens", 0L));
                    object otherValue;
                    if (log.TryGetValue("other", out otherValue) && otherValue != null)
                    {
                        IDictionary<string, object> other = otherValue as IDictionary<string, object>;
                        if (other == null && otherValue is string && !string.IsNullOrWhiteSpace((string)otherValue))
                            other = ParseObject((string)otherValue);
                        if (other != null) cached = Math.Max(cached, Math.Max(0L, GetLong(other, "cache_tokens", 0L)));
                    }

                    promptTokens += prompt;
                    cachedTokens += Math.Min(prompt, cached);
                    logCount++;
                }

                snapshot = new UsageStatsSnapshot
                {
                    PromptTokens = promptTokens,
                    CachedTokens = cachedTokens,
                    LogCount = logCount,
                    UpdatedAt = DateTime.Now
                };
                return true;
            }
            catch { return false; }
        }

        public static string GetBalancePath(StationRecord station)
        {
            return IsDeepSeek(station) ? "/api/v0/users/get_user_summary" : "/api/user/self";
        }

        public static bool SupportsUsageLogs(StationRecord station)
        {
            return station != null
                && !IsDeepSeek(station)
                && (string.IsNullOrWhiteSpace(station.Provider)
                    || string.Equals(station.Provider, "new-api", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsDeepSeek(StationRecord station)
        {
            return station != null && string.Equals(station.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
        }

        private static CurrencySettings CreateDeepSeekCurrency(string currency)
        {
            return new CurrencySettings
            {
                DisplayInCurrency = true,
                DisplayType = "CUSTOM",
                QuotaPerUnit = 100d,
                CustomExchangeRate = 1d,
                CustomSymbol = string.Equals(currency, "CNY", StringComparison.OrdinalIgnoreCase) ? "¥" : "$"
            };
        }

        private static long ToMinorUnits(decimal amount)
        {
            return Convert.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
        }

        private static IDictionary<string, object> SelectMoneyEntry(IList<object> entries, string preferredCurrency)
        {
            if (entries == null) return null;
            IDictionary<string, object> fallback = null;
            foreach (object entry in entries)
            {
                IDictionary<string, object> item = entry as IDictionary<string, object>;
                if (item == null) continue;
                if (fallback == null) fallback = item;
                if (string.Equals(GetString(item, "currency", null), preferredCurrency, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
            return fallback;
        }

        private static IList<object> GetList(IDictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return null;
            return AsList(value);
        }

        private static IList<object> AsList(object value)
        {
            object[] array = value as object[];
            if (array != null) return array;
            var items = new List<object>();
            System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
            if (enumerable == null) return null;
            foreach (object item in enumerable) items.Add(item);
            return items;
        }

        private static decimal GetDecimal(IDictionary<string, object> source, string key, decimal fallback)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return fallback;
            decimal parsed;
            return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any,
                CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private CurrencySettings ParseCurrency(IDictionary<string, object> data)
        {
            return new CurrencySettings
            {
                DisplayInCurrency = GetBool(data, "display_in_currency", true),
                DisplayType = GetString(data, "quota_display_type", "USD"),
                QuotaPerUnit = GetDouble(data, "quota_per_unit", 500000d),
                UsdExchangeRate = GetDouble(data, "usd_exchange_rate", 7.3d),
                CustomSymbol = GetString(data, "custom_currency_symbol", "¤"),
                CustomExchangeRate = GetDouble(data, "custom_currency_exchange_rate", 1d)
            };
        }

        private IDictionary<string, object> ParseObject(string json)
        {
            return _serializer.DeserializeObject(json) as IDictionary<string, object>;
        }

        private static IDictionary<string, object> GetObject(IDictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value as IDictionary<string, object> : null;
        }

        private static string GetString(IDictionary<string, object> source, string key, string fallback)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : fallback;
        }

        private static bool GetBool(IDictionary<string, object> source, string key, bool fallback)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return fallback;
            bool result;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result) ? result : fallback;
        }

        private static double GetDouble(IDictionary<string, object> source, string key, double fallback)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static long GetLong(IDictionary<string, object> source, string key, long fallback)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
