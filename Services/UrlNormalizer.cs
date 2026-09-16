using System;

namespace BalanceDock.Services
{
    public static class UrlNormalizer
    {
        public static bool TryNormalize(string input, out Uri origin, out Uri wallet, out string error)
        {
            origin = null;
            wallet = null;
            error = null;

            string candidate = (input ?? string.Empty).Trim();
            if (candidate.Length == 0)
            {
                error = "请输入页面 URL";
                return false;
            }
            if (!candidate.Contains("://")) candidate = "https://" + candidate;

            Uri parsed;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out parsed)
                || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            {
                error = "URL 格式不正确";
                return false;
            }
            if (parsed.Scheme == Uri.UriSchemeHttp
                && !parsed.IsLoopback
                && !string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                error = "为保护登录信息，请使用 HTTPS 地址";
                return false;
            }

            var builder = new UriBuilder(parsed.Scheme, parsed.Host, parsed.IsDefaultPort ? -1 : parsed.Port);
            origin = builder.Uri;
            wallet = string.Equals(parsed.Host, "platform.deepseek.com", StringComparison.OrdinalIgnoreCase)
                ? new Uri(origin, "/usage")
                : new Uri(origin, "/wallet");
            return true;
        }
    }
}
