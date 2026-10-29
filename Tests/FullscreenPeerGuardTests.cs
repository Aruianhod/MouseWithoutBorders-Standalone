using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class FullscreenPeerGuardTests
{
    [TestMethod]
    public void VisibleFullscreenPeerBlocksAbsoluteDesktopCrossingWithoutEnablingRelativeInput()
    {
        bool auto = Setting.Values.AutomaticGameMouse, relative = Setting.Values.MoveMouseRelatively;
        int easy = Setting.Values.EasyMouse;
        ID destination = MachineStuff.desMachineID;
        ID peerId = Common.MachineID == (ID)2 ? (ID)3 : (ID)2;
        using var peer = new TcpSk(true, null, SocketStatus.Connected, "FullscreenPeer") { MachineId = (uint)peerId };
        try
        {
            Setting.Values.AutomaticGameMouse = false;
            Setting.Values.MoveMouseRelatively = false;
            Setting.Values.EasyMouse = (int)EasyMouseOption.Enable;
            MachineStuff.desMachineID = peerId;
            var packet = Packet(peerId, true, 10);
            GameMouseMode.Receive(new DATA(packet.Bytes), peer);
            Assert.IsTrue(GameMouseMode.BlockEdgeSwitch);
            Assert.IsFalse(Event.IsSwitchingByMouseEnabled());
            Assert.IsFalse(GameMouseMode.UseRelative, "A fullscreen RTS with a visible cursor must keep absolute desktop input.");
            MachineStuff.desMachineID = (ID)99;
            Assert.IsFalse(GameMouseMode.BlockEdgeSwitch, "Protection belongs to the currently controlled peer only.");
            MachineStuff.desMachineID = peerId;
            GameMouseMode.Receive(Packet(peerId, false, 9), peer);
            Assert.IsTrue(GameMouseMode.BlockEdgeSwitch, "An older release cannot cancel a newer protection state.");
            GameMouseMode.Receive(Packet(peerId, false, 11), peer);
            Assert.IsFalse(GameMouseMode.BlockEdgeSwitch, "Alt+Tab or Win+D releases remote protection.");
            Assert.IsTrue(Event.IsSwitchingByMouseEnabled());
        }
        finally
        {
            GameMouseMode.Stop(); MachineStuff.desMachineID = destination;
            Setting.Values.AutomaticGameMouse = auto; Setting.Values.MoveMouseRelatively = relative; Setting.Values.EasyMouse = easy;
        }
    }

    [TestMethod]
    public void DesktopGameHeartbeatDoesNotEraseFullscreenProtectionAndDisconnectReleasesIt()
    {
        ID peerId = Common.MachineID == (ID)2 ? (ID)3 : (ID)2;
        using var peer = new TcpSk(true, null, SocketStatus.Connected, "FullscreenPeer") { MachineId = (uint)peerId };
        try
        {
            GameMouseMode.Receive(Packet(peerId, true, 10), peer);
            var desktop = Packet(peerId, false, 11);
            desktop.Md.dwFlags = GameMouseMode.WireMagic;
            GameMouseMode.Receive(desktop, peer);
            Assert.IsTrue(GameMouseMode.RemoteFullscreenBlocked(peerId));
            Assert.IsFalse(GameMouseMode.RemoteCaptured(peerId));
            peer.Status = SocketStatus.Error;
            Assert.IsFalse(GameMouseMode.RemoteFullscreenBlocked(peerId), "A lost peer must not trap input.");
        }
        finally { GameMouseMode.Stop(); }
    }

    [TestMethod]
    public void FullscreenPacketsValidateAuthenticatedSourceAndPayload()
    {
        var packet = Packet((ID)2, true, 1);
        Assert.IsTrue(GameMouseMode.ValidPacket(packet, (ID)1, (ID)2, true));
        Assert.IsFalse(GameMouseMode.ValidPacket(packet, (ID)1, (ID)3, true));
        Assert.IsFalse(GameMouseMode.ValidPacket(packet, (ID)1, (ID)2, false));
        packet.Des = (ID)3;
        Assert.IsFalse(GameMouseMode.ValidPacket(packet, (ID)1, (ID)2, true));
        packet.Des = ID.ALL; packet.Md.X = 12;
        Assert.IsFalse(GameMouseMode.ValidPacket(packet, (ID)1, (ID)2, true));
        packet.Md.X = 0; packet.Md.dwFlags |= 4;
        Assert.IsFalse(GameMouseMode.ValidPacket(packet, (ID)1, (ID)2, true));
    }

    [TestMethod]
    public void FullscreenReleaseCannotCancelRelativeCaptureOrSupplyADesktopRestorePosition()
    {
        ID peerId = Common.MachineID == (ID)2 ? (ID)3 : (ID)2;
        using var peer = new TcpSk(true, null, SocketStatus.Connected, "FullscreenPeer") { MachineId = (uint)peerId };
        try
        {
            var capture = Packet(peerId, true, 10);
            capture.Md.dwFlags = GameMouseMode.WireMagic | 1;
            GameMouseMode.Receive(capture, peer);
            GameMouseMode.Receive(Packet(peerId, true, 10), peer);
            GameMouseMode.Receive(Packet(peerId, false, 11), peer);
            Assert.IsTrue(GameMouseMode.RemoteCaptured(peerId));
            Assert.IsFalse(GameMouseMode.RemoteFullscreenBlocked(peerId));
            Assert.IsNull(GameMouseMode.TakeRestore(peerId));
        }
        finally { GameMouseMode.Stop(); }
    }

    private static DATA Packet(ID peer, bool active, int sequence) => new()
    {
        Type = PackageType.SettingsCapability, Src = peer, Des = ID.ALL,
        Md = new() { WheelDelta = sequence, dwFlags = GameMouseMode.FullscreenWireMagic | (active ? 1 : 0) },
    };
}
