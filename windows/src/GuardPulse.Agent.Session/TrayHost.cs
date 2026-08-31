using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using WinForms = System.Windows.Forms;

namespace GuardPulse.Agent.Session;

/// <summary>
/// Discreet tray presence ("Digital Pulse" design): dark shield icon with a blue
/// pulse dot, generic "Device Service" naming, and a glass-style context menu
/// with a header, a Status action with live dot, and an Exit that is refused
/// while a lock is shown.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private static readonly Color OnSurface = Color.FromArgb(0x1B, 0x1B, 0x1D);
    private static readonly Color OnSurfaceVariant = Color.FromArgb(0x41, 0x47, 0x55);
    private static readonly Color Outline = Color.FromArgb(0x71, 0x77, 0x86);
    private static readonly Color Primary = Color.FromArgb(0x00, 0x58, 0xBC);
    private static readonly Color Secondary = Color.FromArgb(0x00, 0x6E, 0x28);
    private static readonly Color Surface = Color.FromArgb(0xFC, 0xF8, 0xFB);
    private static readonly Color SurfaceContainer = Color.FromArgb(0xF0, 0xED, 0xEF);
    private static readonly Color Separator = Color.FromArgb(0x50, 0xC1, 0xC6, 0xD7);

    private readonly NotifyIcon _icon;
    private readonly Func<bool> _isLockVisible;

    public TrayHost(Action onStatus, Action onDashboard, Action onExit)
    {
        _isLockVisible = () => System.Windows.Application.Current.Windows
            .OfType<LockWindow>().Any(w => w.IsVisible);

        var menu = BuildMenu(onStatus, onDashboard, onExit);

        _icon = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "Device Service",
            ContextMenuStrip = menu,
            // Hidden by default: the parent app drives everything once paired. The
            // service broadcasts pairedState (true when paired, false when a new
            // pairing is needed) and SetPaired reveals the icon only when unpaired.
            Visible = false
        };
        _icon.DoubleClick += (_, _) => onStatus();
    }

    /// <summary>
    /// Once the device is paired the tray icon disappears (the parent app drives
    /// everything; setup returns via the openSetup command). It comes back the
    /// moment the device is unpaired and a new pairing code is needed.
    /// </summary>
    public void SetPaired(bool paired)
    {
        var wanted = !paired;
        if (_icon.Visible != wanted)
        {
            _icon.Visible = wanted;
        }
    }

    private ContextMenuStrip BuildMenu(Action onStatus, Action onDashboard, Action onExit)
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            AutoSize = false,
            Size = new Size(256, 182),
            BackColor = Surface,
            ForeColor = OnSurface,
            Font = new Font("Segoe UI", 9.5f)
        };
        menu.Renderer = new GlassRenderer();

        var header = new ToolStripControlHost(new HeaderPanel { Dock = DockStyle.Fill })
        {
            AutoSize = false,
            Height = 52,
            Margin = new Padding(2, 2, 2, 4)
        };
        menu.Items.Add(header);

        menu.Items.Add(new ToolStripSeparator { BackColor = Separator, AutoSize = false, Height = 2 });

        var status = new StatusMenuItem
        {
            Text = "Status",
            ForeColor = OnSurface,
            Font = new Font("Segoe UI", 9.5f),
            AutoSize = false,
            Height = 34
        };
        status.Click += (_, _) => onStatus();
        menu.Items.Add(status);

        var dashboard = new ToolStripMenuItem
        {
            Text = "Open Dashboard",
            ForeColor = OnSurface,
            Font = new Font("Segoe UI", 9.5f),
            AutoSize = false,
            Height = 34
        };
        dashboard.Click += (_, _) => onDashboard();
        menu.Items.Add(dashboard);

        menu.Items.Add(new ToolStripSeparator { BackColor = Separator, AutoSize = false, Height = 2 });

        var exit = new ExitMenuItem(_isLockVisible)
        {
            Text = "Exit",
            AutoSize = false,
            Height = 34
        };
        exit.Click += (_, _) =>
        {
            if (_isLockVisible())
            {
                return; // tray must not become an unlock bypass
            }

            onExit();
        };
        menu.Items.Add(exit);

        return menu;
    }

    /// <summary>Menu header: shield badge + "Device Service" / "GuardPulse Core".</summary>
    private sealed class HeaderPanel : Panel
    {
        public HeaderPanel()
        {
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var badge = new Rectangle(8, 10, 32, 32);
            using (var badgePath = RoundedPath(g, badge, 16))
            using (var fill = new SolidBrush(Color.FromArgb(0x1A, 0xE5, 0xF1, 0xFF)))
            {
                g.FillPath(fill, badgePath);
            }

            DrawShield(g, new RectangleF(badge.X + 7, badge.Y + 7, 18, 18), Primary, filled: true);

            using var title = new Font("Segoe UI Semibold", 9.5f);
            using var subtitle = new Font("Segoe UI", 8f);
            using var titleBrush = new SolidBrush(OnSurface);
            using var subBrush = new SolidBrush(OnSurfaceVariant);
            g.DrawString("Device Service", title, titleBrush, 48, 12);
            g.DrawString("GuardPulse Core", subtitle, subBrush, 48, 29);
        }
    }

    /// <summary>"Status" row with the live green dot on the right.</summary>
    private sealed class StatusMenuItem : ToolStripMenuItem
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(Point.Empty, Size);
            if (Selected)
            {
                using var hover = new SolidBrush(Color.FromArgb(0x80, 0xE4, 0xE2, 0xE4));
                FillRounded(g, rect with { Width = rect.Width - 6, Height = rect.Height - 2 }, 6, hover);
            }

            using var textBrush = new SolidBrush(OnSurface);
            using var font = new Font("Segoe UI", 9.5f);
            g.DrawString("Status", font, textBrush, 12, 7);
            using var dot = new SolidBrush(Secondary);
            g.FillEllipse(dot, rect.Width - 22, rect.Height / 2 - 4, 8, 8);
        }
    }

    /// <summary>"Exit" row: greyed with a lock glyph; hard-disabled while a lock shows.</summary>
    private sealed class ExitMenuItem : ToolStripMenuItem
    {
        private readonly Func<bool> _lockVisible;

        public ExitMenuItem(Func<bool> lockVisible)
        {
            _lockVisible = lockVisible;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var disabled = _lockVisible();
            using var textBrush = new SolidBrush(disabled ? Outline : OnSurfaceVariant);
            using var font = new Font("Segoe UI", 9.5f);
            g.DrawString("Exit", font, textBrush, 12, 7);

            // Small lock glyph
            var px = Size.Width - 26;
            var py = Size.Height / 2 - 7;
            using var pen = new Pen(textBrush, 1.6f);
            g.DrawRectangle(pen, px, py + 5, 12, 9);
            g.DrawArc(pen, px + 2.5f, py, 7, 8, 180, 180);
        }
    }

    private static GraphicsPath RoundedPath(Graphics g, Rectangle r, int radius) => RoundedRect(r, radius);

    private static void FillRounded(Graphics g, Rectangle r, int radius, SolidBrush brush)
    {
        using var path = RoundedRect(r, radius);
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Flat, borderless rendering with a light hover wash (glass look).</summary>
    private sealed class GlassRenderer : ToolStripProfessionalRenderer
    {
        public GlassRenderer()
        {
            RoundedEdges = false;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            // Header/status/exit items paint their own backgrounds.
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var fill = new SolidBrush(Surface);
            e.Graphics.FillRectangle(fill, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(0xFF, 0xC1, 0xC6, 0xD7));
            var r = e.AffectedBounds with { Width = e.AffectedBounds.Width - 1, Height = e.AffectedBounds.Height - 1 };
            e.Graphics.DrawRectangle(pen, r);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Separator);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }
    }

    private static void DrawShield(Graphics g, RectangleF bounds, Color color, bool filled)
    {
        var points = new[]
        {
            new PointF(bounds.X + bounds.Width / 2, bounds.Y),
            new PointF(bounds.Right, bounds.Y + bounds.Height * 0.22f),
            new PointF(bounds.Right - bounds.Width * 0.04f, bounds.Y + bounds.Height * 0.55f),
            new PointF(bounds.X + bounds.Width / 2, bounds.Bottom),
            new PointF(bounds.X + bounds.Width * 0.04f, bounds.Y + bounds.Height * 0.55f),
            new PointF(bounds.X, bounds.Y + bounds.Height * 0.22f)
        };
        using var path = new GraphicsPath();
        path.AddPolygon(points);
        if (filled)
        {
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
        else
        {
            using var pen = new Pen(color, 2f);
            g.DrawPath(pen, path);
        }
    }

    private static Icon BuildIcon()
    {
        // Prefer the branded laptop icon; fall back to procedural shield if missing.
        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "guardpulse-laptop.ico");
        if (!File.Exists(icoPath))
        {
            icoPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "Assets", "guardpulse-laptop.ico");
        }
        if (File.Exists(icoPath))
        {
            try { return new Icon(icoPath); } catch { /* fall through to procedural */ }
        }
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawShield(g, new RectangleF(5, 4, 22, 25), OnSurface, filled: true);
            // Digital-blue pulse dot inside the shield
            using var dot = new SolidBrush(Primary);
            g.FillEllipse(dot, 12.5f, 12f, 7, 7);
        }

        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
