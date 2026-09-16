using System;

namespace BalanceDock.Models
{
    public sealed class UsageStatsSnapshot
    {
        public long PromptTokens { get; set; }
        public long CachedTokens { get; set; }
        public int LogCount { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
