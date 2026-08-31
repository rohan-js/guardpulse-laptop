using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using QRCoder;

namespace GuardPulse.Agent.Session;

public partial class SetupWindow : Window
{
    private static readonly string DeviceJsonPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GuardPulse", "Laptop", "device.json");

    private readonly PipeClient _pipe;
    private readonly System.Windows.Threading.DispatcherTimer _refresh =
        new() { Interval = TimeSpan.FromSeconds(2) };

    private string? _deviceId;
    private string? _secret;

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
        try
        {
            if (!File.Exists(DeviceJsonPath))
            {
                DeviceIdText.Text = "waiting for service...";
                CodeText.Text = "— — —";
                return;
            }

            var json = File.ReadAllText(DeviceJsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var deviceId = root.TryGetProperty("deviceId", out var id) ? id.GetString() : null;
            var secret = root.TryGetProperty("secret", out var s) ? s.GetString() : null;
            var code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
            if (string.IsNullOrEmpty(deviceId)) return;

            if (deviceId != _deviceId)
            {
                _deviceId = deviceId;
                DeviceIdText.Text = deviceId;
                CodeText.Text = FormatCode(code);
                RenderQr($"guardpulse://pair?deviceId={deviceId}&secret={secret ?? ""}");
                StatusText.Text = _pipe.IsConnected
                    ? "Waiting for pairing..."
                    : "Connecting to service...";
            }
        }
        catch (Exception)
        {
            // file may be mid-write by the service; next tick retries
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
