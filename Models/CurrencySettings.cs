namespace BalanceDock.Models
{
    public sealed class CurrencySettings
    {
        public bool DisplayInCurrency { get; set; }
        public string DisplayType { get; set; }
        public double QuotaPerUnit { get; set; }
        public double UsdExchangeRate { get; set; }
        public string CustomSymbol { get; set; }
        public double CustomExchangeRate { get; set; }

        public CurrencySettings()
        {
            DisplayInCurrency = true;
            DisplayType = "USD";
            QuotaPerUnit = 500000d;
            UsdExchangeRate = 7.3d;
            CustomSymbol = "¤";
            CustomExchangeRate = 1d;
        }
    }
}
