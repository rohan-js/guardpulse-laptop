namespace GuardPulse.Agent.Core;

/// <summary>
/// URL → registrable-domain extraction for the browser tab feature's per-domain
/// usage rollup. Only http(s) URLs count; "www." is stripped so github.com and
/// www.github.com roll up together. Null for anything that is not a browsable page.
/// </summary>
public static class BrowserDomains
{
    public static string? Extract(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // Browsers show the active URL scheme-less in the omnibox ("youtube.com/x");
        // restore a scheme so Uri can parse it.
        var candidate = url.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            if (candidate.Contains(' '))
            {
                return null;
            }

            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        return host.Length == 0 ? null : host;
    }
}
