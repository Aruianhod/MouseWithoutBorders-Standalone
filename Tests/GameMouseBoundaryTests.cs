using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class GameMouseBoundaryTests
{
    [TestMethod]
    public void PointLineAndRectangleLocksWorkAtOriginAndOnNegativeMonitor()
    {
        var desktop = new Rectangle(-1920, -1080, 3840, 2160);
        foreach (var client in new[] { new Rectangle(-1920, -1080, 1920, 1080), new Rectangle(0, 0, 1920, 1080) })
        {
            foreach (var clip in new[] {
                new Rectangle(client.Left, client.Top, 0, 0),
                new Rectangle(client.Left + 100, client.Top + 100, 0, 300),
                new Rectangle(client.Left + 100, client.Top + 100, 300, 0), client })
            {
                Assert.IsTrue(GameCaptureTracker.IsCaptured(true, clip, desktop, client, client), clip.ToString());
                Assert.IsFalse(GameCaptureTracker.IsCaptured(false, clip, desktop, client, client), "A menu that shows its cursor releases relative mode.");
            }
            foreach (var clip in new[] {
                new Rectangle(client.Right, client.Top, 0, 100),
                new Rectangle(client.Left, client.Bottom, 100, 0),
                new Rectangle(client.Left - 1, client.Top, 1, 1),
                new Rectangle(client.Left + 100, client.Top + 100, 0, client.Height),
                new Rectangle(client.Left, client.Top, -1, 1) })
                Assert.IsFalse(GameCaptureTracker.IsCaptured(true, clip, desktop, client, client), clip.ToString());
        }
        Assert.IsFalse(GameCaptureTracker.IsCaptured(true, Rectangle.Empty, Rectangle.Empty, desktop, desktop));
    }

    [TestMethod]
    public void UnclippedHiddenFullscreenUsesItsOwnMonitorNotEntireVirtualDesktop()
    {
        var desktop = new Rectangle(-1920, 0, 4480, 1600);
        var monitor = new Rectangle(-1920, 0, 1920, 1080);
        Assert.IsTrue(GameCaptureTracker.IsCaptured(true, desktop, desktop, monitor, monitor));
        Assert.IsFalse(GameCaptureTracker.IsCaptured(false, desktop, desktop, monitor, monitor));
        Assert.IsFalse(GameCaptureTracker.IsCaptured(true, desktop, desktop, new(-1920, 30, 1920, 1050), monitor));
        Assert.IsFalse(GameCaptureTracker.IsCaptured(true, desktop, desktop, new(0, 0, 1920, 1080), monitor));
    }

    [TestMethod]
    public void FocusAndSettingChangesInvalidateCachedCaptureImmediately()
    {
        Assert.IsTrue(GameMouseMode.CaptureBelongsToForeground((IntPtr)1, (IntPtr)1, true));
        Assert.IsFalse(GameMouseMode.CaptureBelongsToForeground((IntPtr)1, (IntPtr)2, true));
        Assert.IsFalse(GameMouseMode.CaptureBelongsToForeground((IntPtr)1, IntPtr.Zero, true));
        Assert.IsFalse(GameMouseMode.CaptureBelongsToForeground(IntPtr.Zero, IntPtr.Zero, true));
        Assert.IsFalse(GameMouseMode.CaptureBelongsToForeground((IntPtr)1, (IntPtr)1, false));
        var tracker = new GameCaptureTracker();
        tracker.Observe((IntPtr)1, true, 0);
        Assert.IsTrue(tracker.Observe((IntPtr)1, true, 100));
        tracker.Reset();
        Assert.IsFalse(tracker.Observe((IntPtr)1, true, 1000));
        Assert.IsTrue(tracker.Observe((IntPtr)1, true, 1100));
        Assert.IsFalse(tracker.Observe((IntPtr)2, false, 1101));
    }

    [TestMethod]
    public void DuplicateHeartbeatOnLiveDirectionSurvivesOtherDirectionClosing()
    {
        ID id = Common.MachineID == (ID)2 ? (ID)3 : (ID)2;
        using var first = new TcpSk(true, null, SocketStatus.Connected, "first") { MachineId = (uint)id };
        using var second = new TcpSk(true, null, SocketStatus.Connected, "second") { MachineId = (uint)id };
        try
        {
            foreach (int magic in new[] { GameMouseMode.WireMagic, GameMouseMode.FullscreenWireMagic })
            {
                var frame = new DATA { Type = PackageType.SettingsCapability, Src = id, Des = ID.ALL,
                    Md = new() { dwFlags = magic | 1, WheelDelta = 100 } };
                GameMouseMode.Receive(frame, first);
                GameMouseMode.Receive(frame, second); // Identical serial, separate authenticated transport.
            }
            first.Status = SocketStatus.Error;
            Assert.IsTrue(GameMouseMode.RemoteCaptured(id));
            Assert.IsTrue(GameMouseMode.RemoteFullscreenBlocked(id));
            second.Status = SocketStatus.Error;
            Assert.IsFalse(GameMouseMode.RemoteCaptured(id));
            Assert.IsFalse(GameMouseMode.RemoteFullscreenBlocked(id));
        }
        finally { GameMouseMode.Stop(); }
    }

    [TestMethod]
    public void DuplicateAndOutOfOrderFramesCannotRenewExpiredLease()
    {
        var peer = new RemoteGameMouseState();
        peer.Update(true, 20, Point.Empty, 0);
        Assert.IsFalse(peer.Update(true, 20, Point.Empty, 1400));
        Assert.IsFalse(peer.Update(false, 19, Point.Empty, 1499));
        Assert.IsFalse(peer.IsActive(1501, true));
        Assert.AreEqual(new Point(32768, 32768), peer.TakeRestore(1502, true));
        Assert.IsNull(peer.TakeRestore(1503, true));
        Assert.IsTrue(peer.Update(true, 1, Point.Empty, 1600)); // Peer restarted.
        Assert.IsTrue(peer.IsActive(1601, true));
        Assert.IsNull(peer.TakeRestore(1602, true));
    }

    [TestMethod]
    public void QueuedCombinedAbsoluteMoveDoesNotDropButtonReleaseOrWheel()
    {
        int absolute = (int)(NativeMethods.MOUSEEVENTF.MOVE | NativeMethods.MOUSEEVENTF.ABSOLUTE);
        var move = new NativeMethods.INPUT { mi = new() { dx = 50000, dy = 30000, dwFlags = absolute } };
        var desktop = move;
        Assert.IsTrue(InputSimulation.FilterCapturedAbsoluteMove(ref desktop, false));
        Assert.AreEqual(50000, desktop.mi.dx);
        Assert.IsFalse(InputSimulation.FilterCapturedAbsoluteMove(ref move, true));
        foreach (var flag in new[] { NativeMethods.MOUSEEVENTF.LEFTUP, NativeMethods.MOUSEEVENTF.RIGHTUP, NativeMethods.MOUSEEVENTF.WHEEL, NativeMethods.MOUSEEVENTF.XUP })
        {
            var combined = new NativeMethods.INPUT { mi = new() { dx = 50000, dy = 30000, mouseData = 120, dwFlags = absolute | (int)flag } };
            Assert.IsTrue(InputSimulation.FilterCapturedAbsoluteMove(ref combined, true));
            Assert.AreEqual((int)flag, combined.mi.dwFlags);
            Assert.AreEqual(0, combined.mi.dx);
            Assert.AreEqual(120, combined.mi.mouseData);
        }
    }

    [TestMethod]
    public void RelativeInputBackendDoesNotChangeWhenGameShowsMenuCursor()
    {
        var relative = new NativeMethods.INPUT { mi = new() { dwFlags = (int)NativeMethods.MOUSEEVENTF.MOVE } };
        Assert.IsFalse(InputSimulation.UsePointerInjector(relative, true, true));
        Assert.IsFalse(InputSimulation.UsePointerInjector(relative, true, false));
        relative.mi.dwFlags |= (int)NativeMethods.MOUSEEVENTF.ABSOLUTE;
        Assert.IsTrue(InputSimulation.UsePointerInjector(relative, true, false));
        Assert.IsFalse(InputSimulation.UsePointerInjector(relative, true, true));
        Assert.IsFalse(InputSimulation.UsePointerInjector(relative, false, false));
    }

    [TestMethod]
    public void InjectionTimingCannotSwallowPhysicalKeysOrMouseAndOwnEventsDoNotLoop()
    {
        foreach (bool keyboard in new[] { false, true })
        {
            int injected = keyboard ? 0x10 : 1;
            Assert.IsFalse(InputHook.IgnoreInjectedHookEvent(0, IntPtr.Zero, keyboard, true));
            Assert.IsTrue(InputHook.IgnoreInjectedHookEvent(injected, InputHook.InjectedInputMarker, keyboard, false));
            Assert.IsTrue(InputHook.IgnoreInjectedHookEvent(injected, IntPtr.Zero, keyboard, true));
            Assert.IsFalse(InputHook.IgnoreInjectedHookEvent(injected, IntPtr.Zero, keyboard, false));
            Assert.IsFalse(InputHook.IgnoreInjectedHookEvent(0, InputHook.InjectedInputMarker, keyboard, false));
        }
        Assert.AreEqual(IntPtr.Size == 8 ? 32 : 24, Marshal.SizeOf<InputHook.MouseLLHookStruct>());
        Assert.AreEqual(IntPtr.Size == 8 ? 24 : 20, Marshal.SizeOf<InputHook.KeyboardHookStruct>());
        Assert.AreEqual(IntPtr.Size == 8 ? 24 : 20, Marshal.OffsetOf<InputHook.MouseLLHookStruct>("DwExtraInfo").ToInt32());
    }

    [TestMethod]
    public void SoftwareWarpsCannotTurnRemoteGameCameraButPhysicalInputAndButtonsSurvive()
    {
        Assert.IsTrue(InputHook.IgnoreRemoteGameWarp(WM.WM_MOUSEMOVE, 1, true));
        Assert.IsFalse(InputHook.IgnoreRemoteGameWarp(WM.WM_MOUSEMOVE, 0, true));
        Assert.IsFalse(InputHook.IgnoreRemoteGameWarp(WM.WM_MOUSEMOVE, 1, false));
        Assert.IsFalse(InputHook.IgnoreRemoteGameWarp(WM.WM_LBUTTONUP, 1, true));
    }
}
