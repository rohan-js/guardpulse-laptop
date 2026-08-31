using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GuardPulse.Agent.Session;

/// <summary>
/// Enables the DWM acrylic "blur behind" effect on a borderless window so the
/// desktop (the frozen locked app) shows through as a frosted layer, matching
/// the Digital Pulse "Mica" background. Falls back silently: when the effect
/// cannot be applied the window keeps its semi-transparent surface fill and
/// simply dims what is behind it.
/// </summary>
internal static class DwmBlur
{
    public static void Enable(Window window, Color tint, double tintOpacity = 0.75)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == nint.Zero)
            {
                return;
            }

            var accent = new AccentPolicy
            {
                AccentState = AccentState.EnableAcrylicBlurBehind,
                // ABGR; the alpha byte is the tint's translucency.
                GradientColor = ((uint)(byte)(tintOpacity * 255) << 24)
                                | (uint)tint.B << 16 | (uint)tint.G << 8 | (uint)tint.R
            };

            var accentSize = Marshal.SizeOf<AccentPolicy>();
            var accentPtr = Marshal.AllocHGlobal(accentSize);
            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.AcccentPolicy,
                    Data = accentPtr,
                    DataLength = accentSize
                };
                _ = SetWindowCompositionAttribute(handle, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }
        catch
        {
            // older OS or compositor policy; the translucent fill is the fallback
        }
    }

    private enum AccentState
    {
        EnableAcrylicBlurBehind = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    private enum WindowCompositionAttribute
    {
        AcccentPolicy = 19
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public nint Data;
        public int DataLength;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);
}
