using System;

namespace BalanceDock.Models
{
    public sealed class BalanceSnapshot
    {
        public long Quota { get; set; }
        public long UsedQuota { get; set; }
        public long RequestCount { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
