namespace GuardPulse.Agent.Session;

using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

public partial class ToastWindow : Window
{
    private static ToastWindow? _instance;
    private readonly DispatcherTimer _hideTimer = new();

    public ToastWindow()
    {
        InitializeComponent();
        Topmost = true;
        _hideTimer.Interval = TimeSpan.FromSeconds(6);
        _hideTimer.Tick += (_, _) => Dismiss();
    }

    public static void ShowToast(string title, string message, int displaySeconds = 6)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_instance == null)
            {
                _instance = new ToastWindow();
            }

            _instance.Display(title, message, displaySeconds);
        });
    }

    private void Display(string title, string message, int displaySeconds)
    {
        _hideTimer.Stop();
        ToastTitle.Text = title;
        ToastMessage.Text = message;
        _hideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(displaySeconds, 3, 60));

        // Position at bottom-right of primary work area
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - DesiredSize.Width - 16;
        Top = workArea.Bottom - DesiredSize.Height - 16;

        Show();

        // Fade in
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
        BeginAnimation(OpacityProperty, fadeIn);

        _hideTimer.Start();
    }

    private void Dismiss()
    {
        _hideTimer.Stop();
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
        fadeOut.Completed += (_, _) => Hide();
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
