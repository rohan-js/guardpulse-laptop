namespace GuardPulse.Agent.Service;

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;

/// <summary>
/// Loopback HTTP endpoint the force-installed Site Guard extension talks to:
/// GET /rules serves the current block rules, /updates.xml + /extension.crx serve the
/// extension package for Chromium's force-install update check, /health is a liveness
/// probe. Binds 127.0.0.1 only (never exposed), no ACL reservation needed for a raw
/// socket, read-only — a misbehaving local process can at most read the block list.
/// </summary>
public sealed class BlocklistServer : IDisposable
{
    public const int DefaultPort = 37846;

    private readonly int _port;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private X509Certificate2? _tlsCertificate;
    private string _rulesTemplate = "{\"domains\":[],\"paths\":[],\"blockPageUrl\":\"\"}";
    private string _rulesJson = "{\"domains\":[],\"paths\":[],\"blockPageUrl\":\"\"}";
    private byte[]? _crxBytes;
    private string _updatesXml = "";
    private string _extensionId = "";

    public BlocklistServer(ILogger logger, int port = DefaultPort)
    {
        _port = port;
        _logger = logger;
    }

    /// <summary>Replaces the served rules document (thread-safe; called per control apply).
    /// The "__SITE_GUARD_ORIGIN__" placeholder is substituted with the packed extension's
    /// real chrome-extension:// origin when the CRX is registered.</summary>
    public void UpdateRules(string rulesJson)
    {
        lock (_gate)
        {
            _rulesTemplate = rulesJson;
        }

        RefreshRules();
    }

    private string ExtensionOrigin => string.IsNullOrEmpty(_extensionId) ? "" : "chrome-extension://" + _extensionId;

    private void RefreshRules()
    {
        lock (_gate)
        {
            _rulesJson = _rulesTemplate.Replace("chrome-extension://__SITE_GUARD_ORIGIN__", ExtensionOrigin)
                                       .Replace("__SITE_GUARD_ORIGIN__", ExtensionOrigin);
        }
    }

    /// <summary>Sets the extension package + update manifest + derived extension id.</summary>
    public void SetExtension(byte[] crxBytes, string updatesXml, string extensionId)
    {
        lock (_gate)
        {
            _crxBytes = crxBytes;
            _updatesXml = updatesXml.Replace("__EXTENSION_ID__", extensionId);
            _extensionId = extensionId;
        }

        RefreshRules();
    }

    /// <summary>Enables TLS: all loopback traffic (extension updates + rules) is served
    /// over https://127.0.0.1 with the given server certificate (system-trusted root).</summary>
    public void SetCertificate(X509Certificate2 certificate)
    {
        _tlsCertificate = certificate;
    }

    public void Start()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _logger.LogInformation("Blocklist extension server listening on 127.0.0.1:{Port}", _port);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blocklist server could not bind 127.0.0.1:{Port} — extension updates unavailable", _port);
            _listener = null;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(() => HandleAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5_000;
                client.SendTimeout = 5_000;
                Stream stream = client.GetStream();
                if (_tlsCertificate is not null)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _tlsCertificate,
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                    }, ct).ConfigureAwait(false);
                    stream = ssl;
                }

                var request = await ReadRequestLineAsync(stream, ct).ConfigureAwait(false);
                if (request is null) return;

                var parts = request.Split(' ');
                if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    AppendRequestLog(request + " -> 405");
                    await WriteResponseAsync(stream, 405, "text/plain", "method not allowed", ct).ConfigureAwait(false);
                    return;
                }

                var path = parts[1].Split('?')[0];
                // Raw request trace: force-install failures are otherwise invisible.
                AppendRequestLog(request);
                switch (path.ToLowerInvariant())
                {
                    case "/rules":
                    case "/block-rules.json":
                        string rules;
                        lock (_gate)
                        {
                            rules = _rulesJson;
                        }

                        await WriteResponseAsync(stream, 200, "application/json", rules, ct).ConfigureAwait(false);
                        return;
                    case "/health":
                        await WriteResponseAsync(stream, 200, "text/plain", "ok", ct).ConfigureAwait(false);
                        return;
                    case "/updates.xml":
                    case "/extension/updates.xml":
                        string xml;
                        lock (_gate)
                        {
                            xml = _updatesXml;
                        }

                        if (string.IsNullOrEmpty(xml))
                        {
                            await WriteResponseAsync(stream, 404, "text/plain", "extension not installed", ct).ConfigureAwait(false);
                            return;
                        }

                        await WriteResponseAsync(stream, 200, "text/xml", xml, ct).ConfigureAwait(false);
                        return;
                    case "/extension.crx":
                    case "/extension/guardpulse-block.crx":
                        byte[]? crx;
                        lock (_gate)
                        {
                            crx = _crxBytes;
                        }

                        if (crx is null || crx.Length == 0)
                        {
                            await WriteResponseAsync(stream, 404, "text/plain", "extension not installed", ct).ConfigureAwait(false);
                            return;
                        }

                        await WriteBytesAsync(stream, 200, "application/x-chrome-extension", crx, ct).ConfigureAwait(false);
                        return;
                    default:
                        await WriteResponseAsync(stream, 404, "text/plain", "not found", ct).ConfigureAwait(false);
                        return;
                }
            }
            catch
            {
                // client vanished / malformed request: nothing to salvage
            }
        }
    }

    private static async Task<string?> ReadRequestLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1024];
        var sb = new StringBuilder();
        while (sb.Length < 2048)
        {
            var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) return null;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
            var text = sb.ToString();
            var eol = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (eol >= 0) return text[..eol];
        }

        return null;
    }

    private static async Task WriteResponseAsync(Stream stream, int status, string contentType, string body, CancellationToken ct)
    {
        await WriteBytesAsync(stream, status, contentType, Encoding.UTF8.GetBytes(body), ct).ConfigureAwait(false);
    }

    private static async Task WriteBytesAsync(Stream stream, int status, string contentType, byte[] body, CancellationToken ct)
    {
        var statusText = status == 200 ? "OK" : status == 404 ? "Not Found" : "Method Not Allowed";
        var header =
            $"HTTP/1.1 {status} {statusText}\r\n" +
            "Content-Type: " + contentType + "\r\n" +
            "Content-Length: " + body.Length + "\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        var head = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Users-readable request trace (force-install failures are silent otherwise).</summary>
    private void AppendRequestLog(string line)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(StatePaths.Root, "extension-server.log"),
                $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} {line}\r\n");
        }
        catch
        {
            // diagnostics only
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch
        {
            // best-effort shutdown
        }
    }
}

/// <summary>Extracts the Chromium extension id from a CRX3 package (deterministic,
/// used for the ExtensionInstallForcelist policy and the update manifest).</summary>
public static class SiteBlockCrx
{
    /// <summary>Reads the Crx3 header's crx_id and maps it to the a-p extension id.</summary>
    public static string? FromCrx(byte[] crx)
    {
        if (crx.Length < 12 || crx[0] != (byte)'C' || crx[1] != (byte)'r' || crx[2] != (byte)'2' || crx[3] != (byte)'4')
        {
            return null;
        }

        var version = BitConverter.ToUInt32(crx, 4);
        if (version != 3) return null;
        var headerLen = (int)BitConverter.ToUInt32(crx, 8);
        if (headerLen <= 0 || 12 + headerLen > crx.Length) return null;

        // Proto field 2 (crx_id), wire type 2: tag 0x12, varint length, bytes.
        for (var i = 12; i < 12 + headerLen - 2; i++)
        {
            if (crx[i] == 0x12 && crx[i + 1] == 0x10 && i + 2 + 16 <= 12 + headerLen)
            {
                return ToExtensionId(crx, i + 2, 16);
            }
        }

        return null;
    }

    /// <summary>Chromium maps each hex digit of the (already-truncated) crx_id to a-p.</summary>
    public static string ToExtensionId(byte[] crx, int offset, int count)
    {
        var sb = new StringBuilder(count * 2);
        for (var i = offset; i < offset + count; i++)
        {
            sb.Append((char)('a' + (crx[i] >> 4)));
            sb.Append((char)('a' + (crx[i] & 0xF)));
        }

        return sb.ToString();
    }
}
