using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Sync;

namespace MouseWithoutBorders.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class GameMouseModeTests
{
    [TestMethod]
    public void IntentionallyHiddenCursorDoesNotNeedAClipRectangleToBeRespected()
    {
        Assert.IsTrue(GameMouseMode.IsIntentionallyHidden(true, 0, (IntPtr)123));
        Assert.IsTrue(GameMouseMode.IsIntentionallyHidden(true, 1, IntPtr.Zero));
        Assert.IsFalse(GameMouseMode.IsIntentionallyHidden(true, 1, (IntPtr)123));
        Assert.IsFalse(GameMouseMode.IsIntentionallyHidden(true, 2, IntPtr.Zero), "Keep the fallback for Windows cursor suppression when no physical mouse is attached.");
        Assert.IsFalse(GameMouseMode.IsIntentionallyHidden(false, 0, IntPtr.Zero), "A failed Win32 query must not be treated as a game hiding the cursor.");
    }

    [TestMethod]
    public void AuthenticatedPeerOverridesOnlyCurrentTargetWithoutChangingSyncedPreferences()
    {
        var settings = Setting.Values;
        bool relative = settings.MoveMouseRelatively, auto = settings.AutomaticGameMouse, draw = settings.DrawMouse;
        int easy = settings.EasyMouse;
        ID destination = MachineStuff.desMachineID;
        ID peerId = Common.MachineID == (ID)2 ? (ID)3 : (ID)2;
        using var tcp = new TcpSk(true, null, SocketStatus.Connected, "SyntheticGamePeer") { MachineId = (uint)peerId };
        try
        {
            settings.AutomaticGameMouse = true; settings.MoveMouseRelatively = false;
            settings.DrawMouse = true; settings.EasyMouse = (int)EasyMouseOption.Enable;
            MachineStuff.desMachineID = peerId;
            var packet = new DATA { Type = PackageType.SettingsCapability, Src = peerId, Des = ID.ALL,
                Md = new() { X = 12000, Y = 24000, WheelDelta = 1, dwFlags = GameMouseMode.WireMagic | 1 } };
            GameMouseMode.Receive(packet, tcp);
            Assert.IsTrue(GameMouseMode.UseRelative);
            Assert.IsTrue(GameMouseMode.BlockEdgeSwitch);
            Assert.IsFalse(MouseWithoutBorders.Core.Event.IsSwitchingByMouseEnabled());
            Assert.IsFalse(SharedSettings.Capture().RelativeMouse);
            Assert.IsTrue(SharedSettings.Capture().DrawMouse);
            MachineStuff.desMachineID = Common.MachineID;
            Assert.IsFalse(GameMouseMode.UseRelative);
            MachineStuff.desMachineID = peerId;
            packet.Md.dwFlags = GameMouseMode.WireMagic; packet.Md.WheelDelta++;
            GameMouseMode.Receive(packet, tcp);
            Assert.IsFalse(GameMouseMode.UseRelative);
            Assert.IsTrue(MouseWithoutBorders.Core.Event.IsSwitchingByMouseEnabled());
            Assert.AreEqual(new Point(12000, 24000), GameMouseMode.TakeRestore(peerId));
            settings.MoveMouseRelatively = true;
            Assert.IsTrue(GameMouseMode.UseRelative, "Restore the saved manual relative preference, not a hardcoded absolute mode.");
        }
        finally
        {
            GameMouseMode.Stop(); MachineStuff.desMachineID = destination;
            settings.AutomaticGameMouse = auto; settings.MoveMouseRelatively = relative;
            settings.DrawMouse = draw; settings.EasyMouse = easy;
        }
    }

    [TestMethod]
    public void OldCapabilityPayloadCannotResetActiveGameState()
    {
        var old = new DATA { Type = PackageType.SettingsCapability, Src = (ID)2, Des = ID.ALL };
        Assert.IsFalse(GameMouseMode.ValidPacket(old, (ID)1, (ID)2, true));
        Assert.IsNull(System.Text.Json.JsonSerializer.Deserialize<SharedSettings>("{\"RelativeMouse\":true}")!.AutomaticGameMouse);
    }

    [TestMethod]
    public void DetectionRequiresHiddenCursorAndCaptureInsideForegroundClient()
    {
        var desktop = new Rectangle(0, 0, 1920, 1080);
        var client = new Rectangle(200, 200, 800, 600);
        Assert.IsTrue(GameCaptureTracker.IsCaptured(true, new(600, 500, 1, 1), desktop, client));
        Assert.IsTrue(GameCaptureTracker.IsCaptured(true, client, desktop, client));
        Assert.IsFalse(GameCaptureTracker.IsCaptured(false, client, desktop, client));
        Assert.IsFalse(GameCaptureTracker.IsCaptured(true, desktop, desktop, client));
        Assert.IsFalse(GameCaptureTracker.IsCaptured(true, new(0, 0, 1, 1), desktop, client));
        Assert.IsFalse(GameCaptureTracker.IsCaptured(true, Rectangle.Empty, desktop, client));
        Assert.IsTrue(GameCaptureTracker.IsCaptured(true, desktop, desktop, desktop));
    }

    [TestMethod]
    public void ZeroAreaGameLockIsCapturedAndFocusLossStillReleases()
    {
        // Captured on the secondary PC while reproducing the ZZZ Alt+Tab fault.
        var desktop = new Rectangle(0, 0, 2560, 1600);
        var pointLock = Rectangle.FromLTRB(1280, 800, 1280, 800);
        Assert.IsTrue(GameCaptureTracker.IsCaptured(true, pointLock, desktop, desktop));
        Assert.IsFalse(GameCaptureTracker.IsCaptured(false, pointLock, desktop, desktop));
        Assert.IsFalse(GameCaptureTracker.IsCaptured(true, pointLock, desktop, new(0, 0, 800, 600)));
        Assert.IsFalse(GameCaptureTracker.IsCaptured(true, new(2560, 800, 0, 0), desktop, desktop));
        Assert.IsFalse(GameCaptureTracker.IsCaptured(true, new(1280, 800, -1, 0), desktop, desktop));
        var negativeMonitor = new Rectangle(-2560, 0, 2560, 1600);
        Assert.IsTrue(GameCaptureTracker.IsCaptured(true, new(-1280, 800, 0, 0), negativeMonitor, negativeMonitor));
        var tracker = new GameCaptureTracker();
        Assert.IsFalse(tracker.Observe((IntPtr)1, true, 0));
        Assert.IsTrue(tracker.Observe((IntPtr)1, true, 100));
        Assert.IsFalse(tracker.Observe((IntPtr)2, false, 101));
    }

    [TestMethod]
    public void QueuedDesktopMoveGuardDistinguishesRelativeAndButtonInput()
    {
        var input = new NativeMethods.INPUT();
        input.mi.dwFlags = (int)(NativeMethods.MOUSEEVENTF.MOVE | NativeMethods.MOUSEEVENTF.ABSOLUTE);
        Assert.IsTrue(InputSimulation.IsAbsoluteMouseMove(input));
        input.mi.dwFlags = (int)NativeMethods.MOUSEEVENTF.MOVE;
        Assert.IsFalse(InputSimulation.IsAbsoluteMouseMove(input));
        input.mi.dwFlags = (int)NativeMethods.MOUSEEVENTF.LEFTUP;
        Assert.IsFalse(InputSimulation.IsAbsoluteMouseMove(input));
        input.type = 1;
        input.mi.dwFlags = (int)(NativeMethods.MOUSEEVENTF.MOVE | NativeMethods.MOUSEEVENTF.ABSOLUTE);
        Assert.IsFalse(InputSimulation.IsAbsoluteMouseMove(input));
    }

    [TestMethod]
    public void FocusLossMenuAndShortHiddenTransitionsRestoreImmediately()
    {
        var tracker = new GameCaptureTracker();
        Assert.IsFalse(tracker.Observe((IntPtr)1, true, 0));
        Assert.IsFalse(tracker.Observe((IntPtr)1, true, 99));
        Assert.IsFalse(tracker.Observe((IntPtr)1, false, 100));
        Assert.IsFalse(tracker.Observe((IntPtr)1, true, 200));
        Assert.IsTrue(tracker.Observe((IntPtr)1, true, 300));
        Assert.IsFalse(tracker.Observe((IntPtr)2, true, 301));
        Assert.IsTrue(tracker.Observe((IntPtr)2, true, 401));
        Assert.IsFalse(tracker.Observe(IntPtr.Zero, false, 402));
    }

    [TestMethod]
    public void ExitRebasesOnceAtRealRemoteCursorAndRejectsReorderedState()
    {
        var state = new RemoteGameMouseState();
        Assert.IsTrue(state.Update(true, 10, new(0, 0), 0));
        Assert.IsTrue(state.IsActive(100, true));
        Assert.IsFalse(state.Update(false, 9, new(65535, 65535), 101));
        Assert.IsTrue(state.Update(false, 11, new(12345, 23456), 102));
        Assert.IsFalse(state.IsActive(103, true));
        Assert.AreEqual(new Point(12345, 23456), state.TakeRestore(104, true));
        Assert.IsNull(state.TakeRestore(105, true));
        Assert.IsFalse(state.Update(true, 10, new(), 106));
    }

    [TestMethod]
    public void LostHeartbeatOrDisconnectedPeerCannotTrapMouse()
    {
        var state = new RemoteGameMouseState();
        state.Update(true, 20, new(), 0);
        Assert.IsTrue(state.IsActive(1500, true));
        Assert.IsFalse(state.IsActive(1501, true));
        Assert.AreEqual(new Point(32768, 32768), state.TakeRestore(1502, true));
        state.Update(true, 21, new(), 1600);
        Assert.IsFalse(state.IsActive(1601, false));
        Assert.IsNotNull(state.TakeRestore(1602, false));
    }

    [TestMethod]
    public void SequenceWrapAndPeerRestartAfterExpiryAreAccepted()
    {
        var state = new RemoteGameMouseState();
        state.Update(true, uint.MaxValue - 1, new(), 0);
        Assert.IsTrue(state.Update(false, 1, new(10, 10), 10));
        Assert.IsFalse(state.Update(true, uint.MaxValue, new(), 11));
        Assert.IsTrue(state.Update(true, 0, new(), 2000));
        Assert.IsNull(state.TakeRestore(2001, true));
    }

    [TestMethod]
    public void StateWireRoundTripValidatesIdentityDestinationAndPayload()
    {
        var packet = new DATA { Type = PackageType.SettingsCapability, Src = (ID)2, Des = (ID)1,
            Md = new() { X = 32000, Y = 16000, WheelDelta = -123, dwFlags = GameMouseMode.WireMagic | 1 } };
        Assert.AreEqual(32, packet.Bytes.Length);
        packet = new DATA(packet.Bytes);
        Assert.AreEqual(-123, packet.Md.WheelDelta);
        Assert.IsTrue(GameMouseMode.ValidPacket(packet, (ID)1, (ID)2, true));
        Assert.IsFalse(GameMouseMode.ValidPacket(packet, (ID)1, (ID)2, false));
        Assert.IsFalse(GameMouseMode.ValidPacket(packet, (ID)1, (ID)3, true));
        packet.Des = (ID)3;
        Assert.IsFalse(GameMouseMode.ValidPacket(packet, (ID)1, (ID)2, true));
        packet.Des = ID.ALL; packet.Md.X = -1;
        Assert.IsFalse(GameMouseMode.ValidPacket(packet, (ID)1, (ID)2, true));
        packet.Md.X = 1; packet.Md.dwFlags ^= 4;
        Assert.IsFalse(GameMouseMode.ValidPacket(packet, (ID)1, (ID)2, true));
    }

    [TestMethod]
    public void RestoreCoordinatesHandleNegativeMultiMonitorOriginsAndBounds()
    {
        var desktop = new Rectangle(-1920, -1080, 3840, 2160);
        Assert.AreEqual(new Point(32767, 32767), GameMouseMode.Normalize(Point.Empty, desktop));
        Assert.AreEqual(Point.Empty, GameMouseMode.Normalize(new(-4000, -4000), desktop));
        Assert.AreEqual(new Point(65535, 65535), GameMouseMode.Normalize(new(4000, 4000), desktop));
    }
}
