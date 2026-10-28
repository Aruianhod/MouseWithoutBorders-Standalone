using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Core;

// Global notification/cursor state is not evidence that a background game owns
// the current desktop. Only a visible, active application may block crossing.
internal static class ForegroundMousePolicy
{
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; }

    internal static bool Eligible(string windowClass, bool visible, bool minimized, bool cloaked) =>
        visible && !minimized && !cloaked && !string.IsNullOrEmpty(windowClass)
        && !windowClass.Equals("Progman", StringComparison.OrdinalIgnoreCase)
        && !windowClass.Equals("WorkerW", StringComparison.OrdinalIgnoreCase)
        && !windowClass.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
        && !windowClass.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase)
        && !windowClass.Equals("MultitaskingViewFrame", StringComparison.OrdinalIgnoreCase)
        && !windowClass.Equals("XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase);

    internal static bool IsApplication(IntPtr window)
    {
        if (window == IntPtr.Zero || window == NativeMethods.GetDesktopWindow() || window == NativeMethods.GetShellWindow()) return false;
        var name = new StringBuilder(256);
        if (NativeMethods.GetClassName(window, name, name.Capacity) == 0) return false;
        bool cloaked = DwmGetWindowAttribute(window, 14, out int hidden, sizeof(int)) == 0 && hidden != 0;
        return Eligible(name.ToString(), IsWindowVisible(window), IsIconic(window), cloaked);
    }

    internal static bool CoversMonitor(Rectangle client, Rectangle monitor) =>
        monitor.Width > 0 && monitor.Height > 0 && client.Width > 0 && client.Height > 0
        && client.Left <= monitor.Left + 1 && client.Top <= monitor.Top + 1
        && client.Right >= monitor.Right - 1 && client.Bottom >= monitor.Bottom - 1;

    internal static bool IsForegroundFullscreen(IntPtr window)
    {
        if (!IsApplication(window) || !GetClientRect(window, out var rect)) return false;
        var origin = new Point(rect.Left, rect.Top);
        if (!ClientToScreen(window, ref origin)) return false;
        var client = new Rectangle(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return CoversMonitor(client, Screen.FromHandle(window).Bounds)
            && window == NativeMethods.GetForegroundWindow();
    }
}
