namespace GuardPulse.Agent.Service;

using System.IO;
using System.Text;

public sealed partial class AgentHostedService
{
    /// <summary>
    /// Writes the page a blocked tab is navigated to (file:// URL, carries the original
    /// URL in ?from= so the agent can send the child back automatically on unblock).
    /// Rewritten at every service start so updates ship with the installer.
    /// </summary>
    private void WriteBlockPage()
    {
        try
        {
            var path = Path.Combine(_stateDir, "blocked-site.html");
            File.WriteAllText(path, BlockPageHtml, new UTF8Encoding(false));
            RunIcacls($"\"{path}\" /grant \"{SidUsers}:R\"", "blocked-page-users");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blocked-site page write failed");
        }
    }

    /// <summary>Block-page navigation target for a blocked original URL.</summary>
    private string BlockPageUrl(string? originalUrl)
    {
        var path = Path.Combine(_stateDir, "blocked-site.html");
        var url = "file:///" + path.Replace('\\', '/');
        return originalUrl is null ? url : url + "?from=" + Uri.EscapeDataString(originalUrl);
    }

    private const string BlockPageHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>This page is blocked</title>
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  html, body { height: 100%; }
  body {
    display: flex; align-items: center; justify-content: center;
    background: #fff; color: #202124;
    font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; text-align: center; padding: 24px;
  }
  .icon { width: 132px; height: 132px; margin: 0 auto 40px; display: block; }
  h1 { font-size: 22px; font-weight: 400; letter-spacing: .1px; margin-bottom: 14px; }
  p  { font-size: 15px; color: #5f6368; line-height: 1.55; max-width: 480px; margin: 0 auto; }
  .code {
    display: inline-block; margin-top: 46px; padding: 6px 12px;
    font: 12px/1.4 Consolas, 'Courier New', monospace; color: #5f6368;
    background: #f1f3f4; border-radius: 3px;
  }
</style>
</head>
<body>
<div>
  <svg class="icon" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
    <circle cx="12" cy="12" r="10" stroke="#9aa0a6" stroke-width="1.4"/>
    <line x1="5.5" y1="5.5" x2="18.5" y2="18.5" stroke="#9aa0a6" stroke-width="1.4"/>
    <text x="12" y="16.2" text-anchor="middle" font-family="Segoe UI, Arial" font-size="9.5" fill="#9aa0a6">!</text>
  </svg>
  <h1>This page is blocked</h1>
  <p id="hostline">Your organization doesn&rsquo;t allow you to view this site.</p>
  <span class="code">ERR_BLOCKED_BY_ADMINISTRATOR</span>
</div>
<script>
  try {
    var from = new URLSearchParams(location.search).get("from");
    if (from) {
      var host = new URL(from.replace(/^[a-z]*:\/\//i, "http://")).hostname;
      if (host) { document.getElementById("hostline").textContent =
        host + " is blocked. Your organization doesn\u2019t allow you to view this site."; }
    }
  } catch (e) {}
</script>
</body>
</html>
""";
}
