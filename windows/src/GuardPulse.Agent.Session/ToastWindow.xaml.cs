namespace GuardPulse.Agent.Session;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

public partial class ToastWindow : Window
{
    private static ToastWindow? _instance;
    // FIFO toast queue: ShowToast enqueues (cap 3, oldest dropped beyond it) and
    // each toast shows its FULL duration in turn instead of overwriting the
    // singleton text mid-display.
    private static readonly Queue<(string Title, string Message, int Seconds)> _queue = new();
    private static bool _showing;
    private readonly DispatcherTimer _hideTimer = new();
    private string _currentTitle = "";
    private string _currentMessage = "";
    private bool _dismissing;

    public ToastWindow()
    {
        InitializeComponent();
        // Renders above the lock wall: Owner=null top-level window with Topmost,
        // so the compositor keeps it over the full-desktop overlay.
        Owner = null;
        Topmost = true;
        _hideTimer.Tick += (_, _) => Next();
    }

    public static void ShowToast(string title, string message, int displaySeconds = 6)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_instance == null)
            {
                _instance = new ToastWindow();
            }

            var seconds = Math.Clamp(displaySeconds, 3, 60);
            lock (_queue)
            {
                while (_queue.Count >= 3) _queue.Dequeue();
                _queue.Enqueue((title, message, seconds));
            }

            if (!_showing) _instance.Next();
        });
    }

    private void Next()
    {
        _hideTimer.Stop();
        (string Title, string Message, int Seconds) next;
        lock (_queue)
        {
            if (_queue.Count == 0)
            {
                _showing = false;
                if (IsVisible) Dismiss();
                return;
            }

            next = _queue.Dequeue();
        }

        _showing = true;
        Display(next.Title, next.Message, next.Seconds);
    }

    private void Display(string title, string message, int displaySeconds)
    {
        _currentTitle = title;
        _currentMessage = message;
        ToastTitle.Text = title;
        ToastMessage.Text = message;
        _hideTimer.Interval = TimeSpan.FromSeconds(displaySeconds);

        // Position at bottom-right of primary work area
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - DesiredSize.Width - 16;
        Top = workArea.Bottom - DesiredSize.Height - 16;

        if (!IsVisible) Show();

        // Fade in
        Topmost = false;
        Topmost = true;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
        BeginAnimation(OpacityProperty, fadeIn);

        _hideTimer.Start();
    }

    private void Dismiss()
    {
        _hideTimer.Stop();
        if (!IsVisible || _dismissing) return;
        _dismissing = true;
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
        fadeOut.Completed += (_, _) =>
        {
            _dismissing = false;
            Hide();
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
