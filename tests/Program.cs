using BalanceDock.Models;
using BalanceDock.Services;
using System;

namespace BalanceDock.SelfTests
{
    internal static class Program
    {
        private static int _count;

        private static int Main()
        {
            try
            {
                UrlTests();
                FormattingTests();
                UsageLogTests();
                UsageFocusTests();
                ResponseTests();
                Console.WriteLine("PASS " + _count + " checks");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex.Message);
                return 1;
            }
        }

        private static void UrlTests()
        {
            Uri origin;
            Uri wallet;
            string error;
            Check(UrlNormalizer.TryNormalize("api.example.com/wallet", out origin, out wallet, out error), "normalizes URL without scheme");
            Equal("https://api.example.com/", origin.AbsoluteUri, "normalizes origin");
            Equal("https://api.example.com/wallet", wallet.AbsoluteUri, "normalizes wallet route");
            Check(!UrlNormalizer.TryNormalize("http://api.example.com/wallet", out origin, out wallet, out error), "rejects insecure remote URL");
            Check(UrlNormalizer.TryNormalize("http://localhost:3000/console", out origin, out wallet, out error), "allows localhost HTTP");
            Check(UrlNormalizer.TryNormalize("https://platform.deepseek.com/usage", out origin, out wallet, out error), "accepts DeepSeek usage URL");
            Equal("https://platform.deepseek.com/usage", wallet.AbsoluteUri, "keeps DeepSeek usage route");
        }

        private static void FormattingTests()
        {
            var currency = new CurrencySettings
            {
                DisplayInCurrency = true,
                DisplayType = "CUSTOM",
                QuotaPerUnit = 500000,
                CustomExchangeRate = 1,
                CustomSymbol = "⚡"
            };
            Equal("⚡2.21", StationViewModel.FormatQuota(1105000, currency), "formats custom balance");
            currency.CustomSymbol = "¥";
            Equal("¥10.40", StationViewModel.FormatQuota(5200000, currency), "keeps two decimal places for money");
            currency.CustomSymbol = "⚡";

            var station = new StationViewModel(new StationRecord { Name = "Flash", Origin = "https://example.com", WalletUrl = "https://example.com/wallet", Currency = currency });
            Check(station.Apply(new BalanceSnapshot { Quota = 1105000, UsedQuota = 11445000, UpdatedAt = DateTime.Now }), "sets initial progress baseline");
            Near(100, station.Progress, 0.01, "starts initial balance at full progress");
            Check(!station.Apply(new BalanceSnapshot { Quota = 552500, UsedQuota = 11997500, UpdatedAt = DateTime.Now }), "keeps baseline while balance decreases");
            Near(50, station.Progress, 0.01, "decreases progress against initial balance");
            Check(station.Apply(new BalanceSnapshot { Quota = 2000000, UsedQuota = 12000000, UpdatedAt = DateTime.Now }), "detects recharge from increased balance");
            Near(100, station.Progress, 0.01, "resets recharged balance to full progress");
            Check(!station.Apply(new BalanceSnapshot { Quota = 1500000, UsedQuota = 12500000, UpdatedAt = DateTime.Now }), "keeps recharge baseline while spending");
            Near(75, station.Progress, 0.01, "decreases from recharge baseline");

            var increaseThreshold = new StationViewModel(new StationRecord
            {
                Name = "Threshold",
                Origin = "https://threshold.example.com",
                WalletUrl = "https://threshold.example.com/wallet",
                Currency = currency,
                ProgressBaselineQuota = 5000000,
                LastObservedQuota = 2000000
            });
            Check(!increaseThreshold.Apply(new BalanceSnapshot { Quota = 2500000, UpdatedAt = DateTime.Now }), "does not reset for an increase of exactly one currency unit");
            Near(50, increaseThreshold.Progress, 0.01, "keeps the previous baseline at the one-unit threshold");
            Check(increaseThreshold.Apply(new BalanceSnapshot { Quota = 3000001, UpdatedAt = DateTime.Now }), "resets for an increase above one currency unit");
            Near(100, increaseThreshold.Progress, 0.01, "starts a new cycle above the one-unit threshold");

            var restored = new StationViewModel(new StationRecord
            {
                Name = "Restored",
                Origin = "https://restored.example.com",
                WalletUrl = "https://restored.example.com/wallet",
                Currency = currency,
                ProgressBaselineQuota = 2000000,
                LastObservedQuota = 1500000
            });
            restored.Apply(new BalanceSnapshot { Quota = 1000000, UsedQuota = 13000000, UpdatedAt = DateTime.Now });
            Near(50, restored.Progress, 0.01, "restores persisted progress baseline");
            Equal("#0F9F78", restored.ProgressColor, "uses green above forty percent");
            restored.Apply(new BalanceSnapshot { Quota = 800000, UsedQuota = 13200000, UpdatedAt = DateTime.Now });
            Equal("#D88A18", restored.ProgressColor, "uses amber at forty percent");
            restored.Apply(new BalanceSnapshot { Quota = 300000, UsedQuota = 13700000, UpdatedAt = DateTime.Now });
            Equal("#D88A18", restored.ProgressColor, "uses amber at fifteen percent");
            restored.Apply(new BalanceSnapshot { Quota = 299999, UsedQuota = 13700001, UpdatedAt = DateTime.Now });
            Equal("#D94B4B", restored.ProgressColor, "uses red below fifteen percent");
            Check(restored.ProgressToolTip.Contains("充值基准 ⚡4.00"), "shows the recharge baseline in progress tooltip");
            Equal("刚刚更新", restored.UpdatedLabel, "uses a quiet recent update label");
            Equal("R", restored.StatusGlyph, "uses station initial as the normal status mark");
            restored.NeedsLogin = true;
            Equal("R", restored.StatusGlyph, "keeps station initial when login is required");
            restored.NeedsLogin = false;
            restored.StatusText = "网络异常";
            Equal("R", restored.StatusGlyph, "keeps station initial for refresh errors");
        }

        private static void ResponseTests()
        {
            using (var client = new NewApiClient())
            {
                BalanceSnapshot snapshot;
                Check(client.TryParseSnapshot("{\"success\":true,\"data\":{\"quota\":1105000,\"used_quota\":11445000,\"request_count\":1494}}", out snapshot), "parses self response");
                Equal(1105000L, snapshot.Quota, "reads quota");
                Equal(11445000L, snapshot.UsedQuota, "reads used quota");
                Equal(1494L, snapshot.RequestCount, "reads request count");
                Check(!client.TryParseSnapshot("{\"success\":false}", out snapshot), "rejects failed response");

                var deepSeek = new StationRecord
                {
                    Provider = "deepseek",
                    Origin = "https://platform.deepseek.com",
                    WalletUrl = "https://platform.deepseek.com/usage",
                    Name = "DeepSeek"
                };
                string deepSeekJson = "{\"data\":{\"biz_data\":{\"normal_wallets\":[{\"balance\":\"12.34\",\"currency\":\"CNY\"}],\"bonus_wallets\":[],\"total_costs\":[{\"amount\":\"56.78\",\"currency\":\"CNY\"}]}}}";
                Check(client.TryParseSnapshot(deepSeek, deepSeekJson, out snapshot), "parses DeepSeek summary response");
                Equal(1234L, snapshot.Quota, "reads DeepSeek balance in minor units");
                Equal(5678L, snapshot.UsedQuota, "reads DeepSeek total cost in minor units");
                Equal("¥12.34", StationViewModel.FormatQuota(snapshot.Quota, deepSeek.Currency), "formats DeepSeek CNY balance");
                Equal("/api/v0/users/get_user_summary", NewApiClient.GetBalancePath(deepSeek), "uses DeepSeek summary endpoint");
                StationRecord discovered = client.DiscoverAsync(new Uri("https://platform.deepseek.com"),
                    new Uri("https://platform.deepseek.com/usage"), System.Threading.CancellationToken.None).Result;
                Equal("deepseek", discovered.Provider, "discovers DeepSeek provider without New API status endpoint");
                Equal("https://platform.deepseek.com/usage", discovered.WalletUrl, "discovers DeepSeek usage page");
            }
        }

        private static void UsageFocusTests()
        {
            var tracker = new UsageFocusTracker(TimeSpan.FromMinutes(5));
            DateTime started = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

            Check(!tracker.Observe("a", 100, 90, false, started), "ignores a decrease without a previous observation");
            Check(tracker.Observe("a", 100, 90, true, started), "focuses the first station whose balance decreases");
            Equal(1, tracker.Count, "shows one active station");
            Check(!tracker.Observe("a", 90, 80, true, started.AddMinutes(2)), "refreshes activity without duplicating a station");
            Check(tracker.Observe("b", 200, 190, true, started.AddMinutes(3)), "adds a switched station to the focus queue");
            Equal("a", tracker.ActiveStationIds[0], "preserves first-use queue order");
            Equal("b", tracker.ActiveStationIds[1], "appends newly used stations");
            Check(!tracker.RemoveExpired(started.AddMinutes(7)), "keeps a station at exactly five idle minutes");
            Check(tracker.RemoveExpired(started.AddMinutes(7).AddSeconds(1)), "removes stations after five idle minutes");
            Equal(1, tracker.Count, "keeps other recently active stations focused");
            Equal("b", tracker.ActiveStationIds[0], "keeps the remaining active station");
            Check(tracker.RemoveExpired(started.AddMinutes(8).AddSeconds(1)), "returns to the full list when all activity expires");
            Equal(0, tracker.Count, "clears focus mode when no station is active");
        }

        private static void UsageLogTests()
        {
            using (var client = new NewApiClient())
            {
                UsageStatsSnapshot snapshot;
                string currentResponse = "{\"success\":true,\"data\":{\"items\":["
                    + "{\"prompt_tokens\":1000,\"completion_tokens\":100,\"other\":\"{\\\"cache_tokens\\\":600}\"},"
                    + "{\"prompt_tokens\":500,\"completion_tokens\":50,\"other\":\"{\\\"cache_tokens\\\":100}\"}]}}";
                Check(client.TryParseUsageStats(currentResponse, out snapshot), "parses current New API usage log response");
                Equal(1500L, snapshot.PromptTokens, "sums usage-log prompt tokens");
                Equal(700L, snapshot.CachedTokens, "sums nested cache tokens");
                Equal(2, snapshot.LogCount, "counts token-bearing usage logs");

                string legacyResponse = "{\"success\":true,\"data\":[{\"prompt_tokens\":200,\"cache_tokens\":50,\"other\":\"\"}]}";
                Check(client.TryParseUsageStats(legacyResponse, out snapshot), "parses legacy New API usage log response");
                Equal(50L, snapshot.CachedTokens, "reads top-level legacy cache tokens");
                Check(NewApiClient.SupportsUsageLogs(new StationRecord { Provider = "new-api" }), "enables usage logs for identified New API stations");
                Check(NewApiClient.SupportsUsageLogs(new StationRecord()), "enables usage logs for legacy New API station records");
                Check(!NewApiClient.SupportsUsageLogs(new StationRecord { Provider = "deepseek" }), "does not use New API logs for DeepSeek");
                Equal("https://api.flashpocket.cn/usage-logs/common?pageSize=100&balancedock=42",
                    BrowserSessionService.BuildUsagePageUrl(new StationRecord { Origin = "https://api.flashpocket.cn" }, 42),
                    "builds browser usage-log fallback URL");

                var station = new StationViewModel(new StationRecord { Name = "Flash", Origin = "https://api.flashpocket.cn" });
                station.ApplyUsage(new UsageStatsSnapshot { PromptTokens = 1500, CachedTokens = 700, LogCount = 2, UpdatedAt = DateTime.Now });
                Near(46.666d, station.CacheHitRate, 0.01, "calculates cache hit rate from input tokens");
                Equal(string.Empty, station.CacheHitDisplay, "hides cache hit rate outside the active usage queue");
                Check(station.CacheHitToolTip == null, "hides cache tooltip outside the active usage queue");
                station.IsUsageFocused = true;
                Check(station.CacheHitDisplay.Contains("46.7%"), "formats cache hit rate for station display");
                Check(station.CacheHitToolTip.Contains("700") && station.CacheHitToolTip.Contains("1,500"), "shows cache token totals in tooltip");
                station.IsUsageFocused = false;
                Equal(string.Empty, station.CacheHitDisplay, "hides cache hit rate after leaving the active usage queue");
            }
        }

        private static void Check(bool value, string name)
        {
            _count++;
            if (!value) throw new InvalidOperationException(name);
        }

        private static void Equal<T>(T expected, T actual, string name)
        {
            _count++;
            if (!object.Equals(expected, actual)) throw new InvalidOperationException(name + ": expected " + expected + ", got " + actual);
        }

        private static void Near(double expected, double actual, double tolerance, string name)
        {
            _count++;
            if (Math.Abs(expected - actual) > tolerance) throw new InvalidOperationException(name + ": expected " + expected + ", got " + actual);
        }
    }
}
