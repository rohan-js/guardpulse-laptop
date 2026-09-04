namespace GuardPulse.Agent.Service;

using System.IO;
using System.Text;

public sealed partial class AgentHostedService
{
    /// <summary>
    /// Writes the local page a blocked tab is navigated to (file:// URL). Rewritten at
    /// every service start so updates ship with the installer without migration.
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

    private const string BlockPageHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Blocked by GuardPulse</title>
<style>
  :root { color-scheme: dark; }
  * { margin: 0; padding: 0; box-sizing: border-box; }
  html, body { height: 100%; }
  body {
    display: flex; align-items: center; justify-content: center;
    background: #12141c; color: #eef1f8;
    font-family: 'Segoe UI', system-ui, sans-serif; text-align: center; padding: 24px;
  }
  .card { max-width: 460px; }
  .logo { width: 84px; height: 84px; margin-bottom: 28px; }
  h1 { font-size: 26px; font-weight: 600; letter-spacing: .3px; margin-bottom: 10px; }
  p  { font-size: 15px; color: #9aa3b5; line-height: 1.55; }
  .rule { width: 56px; height: 3px; border-radius: 2px; background: #3d5afe; margin: 22px auto 0; }
</style>
</head>
<body>
<div class="card">
  <svg class="logo" viewBox="0 0 64 64" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M32 4 L56 13 V32 C56 47 46 57 32 60 C18 57 8 47 8 32 V13 Z" fill="#1c2740" stroke="#3d5afe" stroke-width="3" stroke-linejoin="round"/>
    <path d="M14 34 H24 L28 24 L34 42 L38 32 H50" stroke="#22d3ee" stroke-width="3.4" stroke-linecap="round" stroke-linejoin="round" fill="none"/>
  </svg>
  <h1>Blocked by GuardPulse</h1>
  <p>This site is not allowed by your parent.</p>
  <div class="rule"></div>
</div>
</body>
</html>
""";
}
