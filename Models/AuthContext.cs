namespace BalanceDock.Models
{
    public sealed class AuthContext
    {
        public string Authorization { get; set; }
        public string UserId { get; set; }
        public string CookieHeader { get; set; }
        public System.Collections.Generic.IDictionary<string, string> AdditionalHeaders { get; set; }

        public AuthContext()
        {
            AdditionalHeaders = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        }

        public bool HasCredentials
        {
            get
            {
                return !string.IsNullOrWhiteSpace(Authorization)
                    || !string.IsNullOrWhiteSpace(CookieHeader)
                    || (AdditionalHeaders != null && AdditionalHeaders.Count > 0);
            }
        }
    }
}
