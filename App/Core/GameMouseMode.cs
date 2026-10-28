using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Core;

// Runtime overrides only: never write or synchronize the user's desktop preferences.
internal sealed class GameCaptureTracker
{
    private IntPtr foreground;
    private long since;
    private bool candidate;
    internal bool Active { get; private set; }

    internal bool Observe(IntPtr window, bool captured, long now)
    {
        if (window != foreground || !captured)
        {
            Active = false;
            candidate = false;
            foreground = window;
        }
        if (captured && window != IntPtr.Zero)
        {
            if (!candidate) { since = now; candidate = true; }
            Active = now - since >= 100;
        }
        return Active;
    }

    internal static bool IsCaptured(bool hidden, Rectangle clip, Rectangle desktop, Rectangle client)
    {
        if (!hidden || clip.Width <= 0 || clip.Height <= 0 || client.Width <= 0 || client.Height <= 0)
            return false;
        // A fullscreen clip equals the desktop on single-monitor PCs. Windows does not
        // distinguish that from an unclipped cursor, so hidden fullscreen is a heuristic.
        return client.Contains(clip) && (clip != desktop || client == desktop);
    }
}

internal sealed class RemoteGameMouseState
{
    internal const int LeaseMilliseconds = 1500;
    private bool active;
    private bool initialized;
    private uint sequence;
    private long seen;
    private Point? restore;
    internal long Seen => seen;
    internal bool LastReportedActive => active;

    internal bool Update(bool next, uint serial, Point position, long now)
    {
        if (initialized && now - seen <= LeaseMilliseconds && unchecked((int)(serial - sequence)) <= 0)
            return false;
        if (active && !next) restore = position;
        if (next) restore = null;
        active = next; sequence = serial; seen = now; initialized = true;
        return true;
    }

    internal bool IsActive(long now, bool connected)
    {
        if (active && (!connected || now - seen > LeaseMilliseconds))
        {
            active = false;
            // Never reuse the virtual position accumulated while turning in a game.
            restore = new Point(32768, 32768);
        }
        return active;
    }

    internal Point? TakeRestore(long now, bool connected)
    {
        IsActive(now, connected);
        var result = restore; restore = null;
        return result;
    }
}

internal static class GameMouseMode
{
    internal const int WireMagic = 0x474D0100;
    private static readonly object Gate = new();
    private static readonly object ProbeGate = new();
    private static readonly Dictionary<ID, (TcpSk Connection, RemoteGameMouseState State)> Peers = new();
    private static readonly GameCaptureTracker Tracker = new();
    private static System.Windows.Forms.Timer timer;
    private static volatile bool localCaptured;
    private static long lastSent;
    private static bool advertised;
    private static string lastRemoteTransition = "尚未收到对面游戏状态（两端需 .19 或更新）";
    internal static bool LocalCaptured => localCaptured;

    internal static bool IsIntentionallyHidden(bool succeeded, int flags, IntPtr cursor) =>
        succeeded && (flags == 0 || (flags == 1 && cursor == IntPtr.Zero));

    internal static bool SuppressSharedCursor
    {
        get
        {
            if (!Setting.Values.AutomaticGameMouse) return false;
            // A background game's hidden cursor must not hide the desktop fallback.
            if (!ForegroundMousePolicy.IsApplication(NativeMethods.GetForegroundWindow())) return false;
            if (LocalCaptured) return true;
            var info = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
            // Respect intentional hiding even before (or without) a clip rectangle.
            // CURSOR_SUPPRESSED (touch / no physical mouse) keeps the desktop fallback.
            return IsIntentionallyHidden(ReadCursorInfo(ref info), info.flags, info.hCursor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; internal Rectangle Bounds => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll", EntryPoint = "GetCursorInfo")] private static extern bool ReadCursorInfo(ref NativeMethods.CURSORINFO info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    internal static void Start()
    {
        if (timer != null || Common.RunOnLogonDesktop || Common.RunOnScrSaverDesktop) return;
        timer = new() { Interval = 50 };
        timer.Tick += (_, _) => Poll();
        timer.Start();
    }

    internal static void Stop()
    {
        timer?.Stop(); timer?.Dispose(); timer = null; localCaptured = false;
        lock (Gate) Peers.Clear();
    }

    internal static (IntPtr Window, bool Captured) RefreshLocalCapture()
    {
        // Also called before incoming input, so the first event cannot overwrite
        // the game's hidden/locked cursor during the UI timer's 50 ms interval.
        lock (ProbeGate)
        {
            var window = NativeMethods.GetForegroundWindow();
            var info = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
            bool candidate = false;
            Point origin = default;
            GetWindowThreadProcessId(window, out uint process);
            if (Setting.Values.AutomaticGameMouse && ForegroundMousePolicy.IsApplication(window)
                && process != Environment.ProcessId && IsIntentionallyHidden(ReadCursorInfo(ref info), info.flags, info.hCursor)
                && GetClipCursor(out var clip) && GetClientRect(window, out var client) && ClientToScreen(window, ref origin))
            {
                var bounds = client.Bounds; bounds.Offset(origin);
                candidate = GameCaptureTracker.IsCaptured(true, clip.Bounds, SystemInformation.VirtualScreen, bounds)
                    && window == NativeMethods.GetForegroundWindow();
            }
            localCaptured = candidate;
            return (window, candidate);
        }
    }

    private static void Poll()
    {
        try
        {
            long now = Environment.TickCount64;
            var observed = RefreshLocalCapture();
            if (SuppressSharedCursor && Common.MouseCursorForm != null) CustomCursor.ShowFakeMouseCursor(int.MinValue, int.MinValue);
            bool active = Tracker.Observe(observed.Window, observed.Captured, now);
            if (active != advertised || now - lastSent >= 250)
            {
                if (active != advertised) Logger.Log("Automatic game mouse: " + (active ? "relative / captured" : "desktop restored"));
                advertised = active; lastSent = now;
                Point position = default;
                NativeMethods.GetCursorPos(ref position);
                var desktop = MachineStuff.DesktopBounds;
                var normalized = Normalize(position, Rectangle.FromLTRB(desktop.Left, desktop.Top, desktop.Right, desktop.Bottom));
                Common.SkSend(new DATA
                {
                    // Older peers already accept this frame and ignore its optional payload.
                    Type = PackageType.SettingsCapability, Src = Common.MachineID, Des = ID.ALL,
                    Md = new MOUSEDATA { X = normalized.X, Y = normalized.Y, WheelDelta = unchecked((int)(uint)now), dwFlags = WireMagic | (active ? 1 : 0) },
                }, (uint)Common.MachineID, false);
            }
        }
        catch (Exception ex) { localCaptured = false; Logger.Log(ex); }
    }

    internal static Point Normalize(Point point, Rectangle desktop) => new(
        (int)Math.Clamp(((long)point.X - desktop.Left) * 65535 / Math.Max(1, desktop.Width), 0, 65535),
        (int)Math.Clamp(((long)point.Y - desktop.Top) * 65535 / Math.Max(1, desktop.Height), 0, 65535));

    internal static bool ValidPacket(DATA packet, ID local, ID peer, bool connected) =>
        connected && peer != ID.NONE && peer != ID.ALL && peer != local && packet.Src == peer
        && (packet.Des == local || packet.Des == ID.ALL) && packet.Type == PackageType.SettingsCapability
        && (packet.Md.dwFlags == WireMagic || packet.Md.dwFlags == (WireMagic | 1))
        && packet.Md.X >= 0 && packet.Md.X <= 65535 && packet.Md.Y >= 0 && packet.Md.Y <= 65535;

    internal static void Receive(DATA packet, TcpSk tcp)
    {
        if (tcp == null || !ValidPacket(packet, Common.MachineID, (ID)tcp.MachineId, tcp.Status == SocketStatus.Connected)) return;
        lock (Gate)
        {
            bool known = Peers.TryGetValue(packet.Src, out var peer);
            if (!known)
            {
                if (Peers.Count >= 16)
                {
                    ID oldest = ID.NONE; long at = long.MaxValue;
                    foreach (var item in Peers) if (item.Value.State.Seen < at) { oldest = item.Key; at = item.Value.State.Seen; }
                    Peers.Remove(oldest);
                }
                peer = (tcp, new());
            }
            // Both authenticated directions may carry the same state; retain ordering.
            bool wasActive = peer.State.LastReportedActive;
            bool next = (packet.Md.dwFlags & 1) != 0;
            if (peer.State.Update(next, unchecked((uint)packet.Md.WheelDelta), new(packet.Md.X, packet.Md.Y), Environment.TickCount64))
            {
                Peers[packet.Src] = (tcp, peer.State);
                if (!known || wasActive != next)
                {
                    lastRemoteTransition = $"对面最近状态：{(next ? "进入游戏模式" : "恢复桌面模式")}（{DateTime.Now:HH:mm:ss}）";
                    Logger.Log($"Remote game mouse: peer {(uint)packet.Src}, {(next ? "captured / relative" : "desktop")}");
                }
            }
        }
    }

    internal static bool RemoteCaptured(ID target)
    {
        lock (Gate) return Peers.TryGetValue(target, out var peer)
            && peer.State.IsActive(Environment.TickCount64, peer.Connection.Status == SocketStatus.Connected);
    }

    internal static bool UseRelative => Setting.Values.MoveMouseRelatively || (Setting.Values.AutomaticGameMouse && RemoteCaptured(Common.DesMachineID));
    internal static bool BlockEdgeSwitch => Setting.Values.AutomaticGameMouse && (Common.DesMachineID == Common.MachineID ? LocalCaptured : RemoteCaptured(Common.DesMachineID));

    internal static string Status => !Setting.Values.AutomaticGameMouse ? "自动识别已关闭"
        : LocalCaptured ? "本机：检测到隐藏并限制光标；游戏模式"
        : RemoteCaptured(Common.DesMachineID) ? "当前控制的电脑：游戏模式（相对移动，暂停鼠标跨屏）"
        : "桌面模式：等待游戏锁定鼠标";

    internal static string RecentRemoteStatus { get { lock (Gate) return lastRemoteTransition; } }

    internal static Point? TakeRestore(ID target)
    {
        lock (Gate) return Peers.TryGetValue(target, out var peer)
            ? peer.State.TakeRestore(Environment.TickCount64, Setting.Values.AutomaticGameMouse && peer.Connection.Status == SocketStatus.Connected) : null;
    }
}
