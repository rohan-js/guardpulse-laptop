using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using QRCoder;

namespace GuardPulse.Agent.Session;

public partial class SetupWindow : Window
{
    private static readonly TimeSpan DeviceInfoTimeout = TimeSpan.FromSeconds(4);

    private readonly PipeClient _pipe;
    private readonly System.Windows.Threading.DispatcherTimer _refresh =
        new() { Interval = TimeSpan.FromSeconds(2) };

    private string? _deviceId;
    private string? _secret;
    private string? _code;

    public SetupWindow(PipeClient pipe)
    {
        _pipe = pipe;
        InitializeComponent();
        MachineNameText.Text = Environment.MachineName;
        _pipe.MessageReceived += OnPipeMessage;
        _pipe.Connected += LoadDevice;
        _refresh.Tick += (_, _) => LoadDevice();
        _refresh.Start();
        LoadDevice();
        Closed += (_, _) =>
        {
            _refresh.Stop();
            _pipe.MessageReceived -= OnPipeMessage;
            _pipe.SendSetupClosed();
        };
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnPipeMessage(System.Text.Json.JsonElement message)
    {
        var type = message.TryGetProperty("t", out var t) ? t.GetString() : null;
        if (type is "activity" or "pinState")
        {
            Dispatcher.Invoke(() =>
            {
                var overlay = message.TryGetProperty("overlayState", out var o) ? o.GetString() : null;
                StatusText.Text = overlay == "locked"
                    ? "Protection active. A lock is currently on screen."
                    : "Protection active. Reporting activity to the parent app.";
            });
        }
    }

    private void LoadDevice()
    {
        // Connected fires on the pipe's receive thread and LoadDevice touches UI, so
        // hop to the dispatcher first; refresh ticks already arrive on it (no-op hop).
        _ = Dispatcher.InvokeAsync(LoadDeviceOnUiThread);
    }

    private async void LoadDeviceOnUiThread()
    {
        try
        {
            // The QR payload needs the pairing secret, which never leaves the service's
            // secret store onto the world-readable device.json: request it over the pipe.
            if (!_pipe.IsConnected)
            {
                DeviceIdText.Text = "waiting for service...";
                CodeText.Text = "— — —";
                return;
            }

            var reply = await _pipe.SendRequestAsync("deviceInfo", new { }, DeviceInfoTimeout);
            if (reply is not JsonElement info)
            {
                Console.Error.WriteLine("[SetupWindow] deviceInfo request timed out or the pipe dropped; retrying on next tick");
                return;
            }

            var deviceId = info.TryGetProperty("deviceId", out var id) ? id.GetString() : null;
            var secret = info.TryGetProperty("secret", out var s) ? s.GetString() : null;
            var code = info.TryGetProperty("code", out var c) ? c.GetString() : null;
            if (string.IsNullOrEmpty(deviceId))
            {
                Console.Error.WriteLine("[SetupWindow] deviceInfo reply had no deviceId");
                return;
            }

            // Re-render whenever ANY credential changed - the pairing secret
            // rotates every 10 minutes, so comparing deviceId alone would
            // freeze the QR on a stale secret and every pair attempt would
            // be rejected by the service.
            if (deviceId != _deviceId || secret != _secret || code != _code)
            {
                _deviceId = deviceId;
                _secret = secret;
                _code = code;
                DeviceIdText.Text = deviceId;
                CodeText.Text = FormatCode(code);
                RenderQr($"guardpulse://pair?deviceId={deviceId}&secret={secret ?? ""}");
                StatusText.Text = _pipe.IsConnected
                    ? "Waiting for pairing..."
                    : "Connecting to service...";
            }
        }
        catch (Exception ex)
        {
            // service unreachable or reply malformed; next tick retries
            Console.Error.WriteLine($"[SetupWindow] LoadDevice failed: {ex.Message}");
        }
    }

    private static string FormatCode(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length != 6 || !code.All(char.IsDigit))
        {
            return "— — —";
        }

        return code[..3] + "-" + code[3..];
    }

    private void RenderQr(string payload)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            var qr = new PngByteQRCode(data);
            var bytes = qr.GetGraphic(10);
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            QrImage.Source = image;
        }
        catch (Exception)
        {
            QrImage.Source = null;
        }
    }
}
