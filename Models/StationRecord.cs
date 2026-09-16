using System;

namespace BalanceDock.Models
{
    public sealed class StationRecord
    {
        public string Id { get; set; }
        public string Origin { get; set; }
        public string WalletUrl { get; set; }
        public string Name { get; set; }
        public string LogoUrl { get; set; }
        public string Provider { get; set; }
        public CurrencySettings Currency { get; set; }
        public long ProgressBaselineQuota { get; set; }
        public long LastObservedQuota { get; set; }

        public StationRecord()
        {
            Id = Guid.NewGuid().ToString("N");
            Currency = new CurrencySettings();
        }
    }
}
