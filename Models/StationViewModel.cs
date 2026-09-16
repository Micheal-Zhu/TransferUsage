using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace BalanceDock.Models
{
    public sealed class StationViewModel : INotifyPropertyChanged
    {
        private long _quota;
        private long _usedQuota;
        private long _requestCount;
        private DateTime _updatedAt;
        private string _statusText;
        private bool _isRefreshing;
        private bool _needsLogin;
        private long _usagePromptTokens;
        private long _usageCachedTokens;
        private int _usageLogCount;
        private bool _hasUsageStats;
        private bool _isUsageFocused;

        public StationRecord Record { get; private set; }
        public AuthContext Auth { get; set; }

        public string Id { get { return Record.Id; } }
        public string Name { get { return Record.Name; } }
        public string Origin { get { return Record.Origin; } }
        public string Initial { get { return string.IsNullOrWhiteSpace(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant(); } }

        public long Quota
        {
            get { return _quota; }
            private set { SetField(ref _quota, value); RaiseCalculated(); }
        }

        public long UsedQuota
        {
            get { return _usedQuota; }
            private set { SetField(ref _usedQuota, value); RaiseCalculated(); }
        }

        public long RequestCount
        {
            get { return _requestCount; }
            private set { SetField(ref _requestCount, value); }
        }

        public DateTime UpdatedAt
        {
            get { return _updatedAt; }
            private set
            {
                if (SetField(ref _updatedAt, value))
                    OnPropertyChanged("UpdatedLabel");
            }
        }

        public string StatusText
        {
            get { return _statusText; }
            set
            {
                if (SetField(ref _statusText, value))
                    OnPropertyChanged("UpdatedLabel");
            }
        }

        public bool IsRefreshing
        {
            get { return _isRefreshing; }
            set { SetField(ref _isRefreshing, value); }
        }

        public bool NeedsLogin
        {
            get { return _needsLogin; }
            set { SetField(ref _needsLogin, value); }
        }

        public double Progress
        {
            get
            {
                double baseline = Math.Max(0d, Record.ProgressBaselineQuota);
                if (baseline <= 0d) return 0d;
                return Math.Max(0d, Math.Min(100d, Quota * 100d / baseline));
            }
        }

        public string BalanceDisplay
        {
            get { return FormatQuota(Quota, Record.Currency); }
        }

        public string UsageDisplay
        {
            get { return "已用 " + FormatQuota(UsedQuota, Record.Currency); }
        }

        public double CacheHitRate
        {
            get
            {
                if (!_hasUsageStats || _usagePromptTokens <= 0L) return 0d;
                return Math.Max(0d, Math.Min(100d, _usageCachedTokens * 100d / _usagePromptTokens));
            }
        }

        public string CacheHitDisplay
        {
            get { return _isUsageFocused && _hasUsageStats && _usagePromptTokens > 0L ? "缓存 " + CacheHitRate.ToString("0.#", CultureInfo.CurrentCulture) + "% · " : string.Empty; }
        }

        public string CacheHitToolTip
        {
            get
            {
                if (!_isUsageFocused || !_hasUsageStats || _usagePromptTokens <= 0L) return null;
                return "最近 " + _usageLogCount + " 条请求\n缓存 "
                    + _usageCachedTokens.ToString("N0", CultureInfo.CurrentCulture) + " / 输入 "
                    + _usagePromptTokens.ToString("N0", CultureInfo.CurrentCulture) + " Token";
            }
        }

        public bool IsUsageFocused
        {
            get { return _isUsageFocused; }
            set
            {
                if (!SetField(ref _isUsageFocused, value)) return;
                OnPropertyChanged("CacheHitDisplay");
                OnPropertyChanged("CacheHitToolTip");
            }
        }

        public string UpdatedLabel
        {
            get
            {
                if (UpdatedAt == default(DateTime)) return StatusText ?? "等待同步";
                TimeSpan age = DateTime.Now - UpdatedAt;
                if (age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(1d)) return "刚刚更新";
                if (UpdatedAt.Date == DateTime.Today)
                    return UpdatedAt.ToString("HH:mm", CultureInfo.CurrentCulture) + " 更新";
                return UpdatedAt.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture) + " 更新";
            }
        }

        public string StatusGlyph { get { return Initial; } }
        public string StatusColor { get { return "#087F5B"; } }
        public string StatusBackground { get { return "#170F9F78"; } }

        public string ProgressColor
        {
            get
            {
                if (Progress < 15d) return "#D94B4B";
                if (Progress <= 40d) return "#D88A18";
                return "#0F9F78";
            }
        }

        public string ProgressToolTip
        {
            get
            {
                string percentage = Progress.ToString("0.#", CultureInfo.CurrentCulture);
                return "本周期剩余 " + percentage + "%\n充值基准 " + FormatQuota(Record.ProgressBaselineQuota, Record.Currency);
            }
        }

        public StationViewModel(StationRecord record)
        {
            Record = record;
            Auth = new AuthContext();
            _statusText = "等待同步";
        }

        public bool Apply(BalanceSnapshot snapshot)
        {
            long currentQuota = Math.Max(0L, snapshot.Quota);
            long previousBaseline = Record.ProgressBaselineQuota;
            long previousQuota = Math.Max(0L, Record.LastObservedQuota);
            long increase = Math.Max(0L, currentQuota - previousQuota);

            if (previousBaseline <= 0L || GetDisplayAmount(increase, Record.Currency) > 1d)
                Record.ProgressBaselineQuota = currentQuota;
            Record.LastObservedQuota = currentQuota;

            Quota = currentQuota;
            UsedQuota = snapshot.UsedQuota;
            RequestCount = snapshot.RequestCount;
            UpdatedAt = snapshot.UpdatedAt;
            NeedsLogin = false;
            StatusText = null;
            OnPropertyChanged("UpdatedLabel");
            return Record.ProgressBaselineQuota != previousBaseline;
        }

        public void ApplyUsage(UsageStatsSnapshot snapshot)
        {
            if (snapshot == null) return;
            _usagePromptTokens = Math.Max(0L, snapshot.PromptTokens);
            _usageCachedTokens = Math.Max(0L, Math.Min(_usagePromptTokens, snapshot.CachedTokens));
            _usageLogCount = Math.Max(0, snapshot.LogCount);
            _hasUsageStats = true;
            OnPropertyChanged("CacheHitRate");
            OnPropertyChanged("CacheHitDisplay");
            OnPropertyChanged("CacheHitToolTip");
        }

        public static string FormatQuota(long rawQuota, CurrencySettings currency)
        {
            if (currency == null) currency = new CurrencySettings();
            if (!currency.DisplayInCurrency || string.Equals(currency.DisplayType, "TOKENS", StringComparison.OrdinalIgnoreCase))
                return rawQuota.ToString("N0", CultureInfo.CurrentCulture) + " 额度";

            double divisor = currency.QuotaPerUnit <= 0d ? 500000d : currency.QuotaPerUnit;
            double amount = rawQuota / divisor;
            string symbol;
            if (string.Equals(currency.DisplayType, "CNY", StringComparison.OrdinalIgnoreCase))
            {
                amount *= currency.UsdExchangeRate <= 0d ? 1d : currency.UsdExchangeRate;
                symbol = "¥";
            }
            else if (string.Equals(currency.DisplayType, "CUSTOM", StringComparison.OrdinalIgnoreCase))
            {
                amount *= currency.CustomExchangeRate <= 0d ? 1d : currency.CustomExchangeRate;
                symbol = string.IsNullOrWhiteSpace(currency.CustomSymbol) ? "¤" : currency.CustomSymbol;
            }
            else
            {
                symbol = "$";
            }
            return symbol + amount.ToString("0.00", CultureInfo.CurrentCulture);
        }

        private static double GetDisplayAmount(long rawQuota, CurrencySettings currency)
        {
            if (currency == null) currency = new CurrencySettings();
            double divisor = currency.QuotaPerUnit <= 0d ? 500000d : currency.QuotaPerUnit;
            double amount = rawQuota / divisor;
            if (string.Equals(currency.DisplayType, "CNY", StringComparison.OrdinalIgnoreCase))
                return amount * (currency.UsdExchangeRate <= 0d ? 1d : currency.UsdExchangeRate);
            if (string.Equals(currency.DisplayType, "CUSTOM", StringComparison.OrdinalIgnoreCase))
                return amount * (currency.CustomExchangeRate <= 0d ? 1d : currency.CustomExchangeRate);
            return amount;
        }

        private void RaiseCalculated()
        {
            OnPropertyChanged("Progress");
            OnPropertyChanged("ProgressColor");
            OnPropertyChanged("ProgressToolTip");
            OnPropertyChanged("BalanceDisplay");
            OnPropertyChanged("UsageDisplay");
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (object.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
