using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Audio;
using NAudio.CoreAudioApi;

namespace MouseWithoutBorders.UnitTests;

[TestClass]
public sealed class PlaybackModeTests
{
    [TestMethod]
    public void HeadsetOnlySeparatesSystemAudioFromRemoteMicrophoneAndBothRestoresSpeakerSource()
    {
        var microphone = new CablePair("mic-in", "mic-out");
        var playback = new CablePair("hifi-in", "hifi-out");
        var previous = new AudioOptions { Port = 15333, BufferMilliseconds = 200 };
        var isolated = AudioQuickSetup.BuildPlan(QuickAudioRole.RemoteComputer, "MAIN", "speaker", "mic",
            microphone, previous, RemotePlaybackMode.HeadsetOnly, playback);
        Assert.AreEqual("hifi-in", isolated.SendSystem.DeviceId);
        Assert.AreEqual("mic-in", isolated.ReceiveMicrophone.DeviceId);
        Assert.IsFalse(isolated.SendMicrophone.Enabled || isolated.ReceiveSystem.Enabled);
        isolated.Validate("SECONDARY");
        var both = AudioQuickSetup.BuildPlan(QuickAudioRole.RemoteComputer, "MAIN", "speaker", "mic",
            microphone, isolated, RemotePlaybackMode.BothComputers, playback);
        Assert.AreEqual("speaker", both.SendSystem.DeviceId);
        Assert.AreEqual("mic-in", both.ReceiveMicrophone.DeviceId);
        Assert.AreEqual(15333, both.Port);
        Assert.AreEqual(200, both.BufferMilliseconds);
        Assert.ThrowsException<ArgumentException>(() => AudioQuickSetup.BuildPlan(QuickAudioRole.RemoteComputer,
            "MAIN", "speaker", "mic", microphone, previous, RemotePlaybackMode.HeadsetOnly));
        Assert.ThrowsException<InvalidOperationException>(() => AudioQuickSetup.BuildPlan(QuickAudioRole.RemoteComputer,
            "MAIN", "speaker", "mic", microphone, previous, RemotePlaybackMode.HeadsetOnly, microphone));
    }

    [TestMethod]
    public void HiFiDetectionCannotAccidentallyReuseMicrophoneCableOrPartialDevice()
    {
        AudioDeviceChoice input = new("hi-in", "Hi-Fi Cable Input (VB-Audio Hi-Fi Cable)");
        AudioDeviceChoice output = new("hi-out", "Hi-Fi Cable Output (VB-Audio Hi-Fi Cable)");
        Assert.AreEqual(new CablePair("hi-in", "hi-out"), AudioQuickSetup.FindHiFiCable(new[] { input }, new[] { output }));
        Assert.IsNull(AudioQuickSetup.FindHiFiCable(new[] { new AudioDeviceChoice("mic", "CABLE Input (VB-Audio Virtual Cable)") }, new[] { output }));
        Assert.IsNull(AudioQuickSetup.FindHiFiCable(new[] { input }, Array.Empty<AudioDeviceChoice>()));
        Assert.ThrowsException<InvalidOperationException>(() => AudioQuickSetup.FindHiFiCable(new[] { input, input with { Id = "duplicate" } }, new[] { output }));
    }

    [TestMethod]
    public void OldSettingsKeepBothAndNewPendingSetupPersistsModeRecoveryAndDriverStage()
    {
        var old = JsonSerializer.Deserialize<QuickAudioState>("{\"Peer\":\"MAIN\",\"Role\":1,\"AppliedCaptureId\":\"mic\"}")!;
        Assert.AreEqual(RemotePlaybackMode.BothComputers, old.PlaybackMode);
        Assert.AreEqual("", old.AppliedRenderId);
        var state = new QuickAudioState
        {
            Peer = "MAIN", Role = QuickAudioRole.RemoteComputer, Pending = true, PendingDriver = "HiFi",
            PlaybackMode = RemotePlaybackMode.HeadsetOnly, AppliedRenderId = "hifi",
            PlaybackRestore = new[] { "speaker", "media", "call" },
        };
        var loaded = JsonSerializer.Deserialize<QuickAudioState>(JsonSerializer.Serialize(state))!;
        Assert.AreEqual(RemotePlaybackMode.HeadsetOnly, loaded.PlaybackMode);
        Assert.AreEqual("HiFi", loaded.PendingDriver);
        Assert.AreEqual("hifi", loaded.AppliedRenderId);
        CollectionAssert.AreEqual(state.PlaybackRestore, loaded.PlaybackRestore);
    }

    private sealed class FakeDefaults : IAudioDefaults
    {
        internal string[] Render = { "speaker", "media", "call" };
        internal string[] Capture = { "mic", "mic-media", "mic-call" };
        internal bool FailOnSecondRender;
        public string Get(DataFlow flow, Role role) => (flow == DataFlow.Render ? Render : Capture)[(int)role];
        public void Set(string id, Role role, DataFlow flow = DataFlow.Capture)
        {
            (flow == DataFlow.Render ? Render : Capture)[(int)role] = id;
            if (FailOnSecondRender && flow == DataFlow.Render && role == Role.Multimedia)
            {
                FailOnSecondRender = false;
                throw new IOException("simulated partial Windows device switch");
            }
        }
    }

    [TestMethod]
    public void PlaybackRestoresAllRolesAndPreservesManualChangesAndMicrophone()
    {
        var devices = new FakeDefaults();
        var state = new QuickAudioState { AppliedRenderId = "hifi", PlaybackRestore = devices.Render.ToArray() };
        AudioQuickSetup.SetDefaultDevice(devices, DataFlow.Render, "hifi");
        CollectionAssert.AreEqual(new[] { "hifi", "hifi", "hifi" }, devices.Render);
        devices.Render[2] = "user-chosen-headphones";
        AudioQuickSetup.RestorePlayback(devices, state);
        CollectionAssert.AreEqual(new[] { "speaker", "media", "user-chosen-headphones" }, devices.Render);
        CollectionAssert.AreEqual(new[] { "mic", "mic-media", "mic-call" }, devices.Capture);
        Assert.AreEqual("", state.AppliedRenderId);
        Assert.AreEqual(0, state.PlaybackRestore.Length);
    }

    [TestMethod]
    public void FailedPlaybackSwitchRollsBackPartialRolesAndPreviouslyChangedMicrophone()
    {
        var devices = new FakeDefaults { FailOnSecondRender = true };
        var transaction = new AudioDefaultsTransaction(devices);
        AudioQuickSetup.SetMicrophone(transaction, "remote-mic");
        Assert.ThrowsException<IOException>(() => AudioQuickSetup.SetDefaultDevice(transaction, DataFlow.Render, "hifi"));
        transaction.Rollback();
        CollectionAssert.AreEqual(new[] { "speaker", "media", "call" }, devices.Render);
        CollectionAssert.AreEqual(new[] { "mic", "mic-media", "mic-call" }, devices.Capture);
    }

    [TestMethod]
    public void FailedSwitchBackRestoresPriorVirtualRouteButPreservesNewManualChoice()
    {
        var devices = new FakeDefaults { Render = new[] { "hifi", "hifi", "hifi" } };
        var transaction = new AudioDefaultsTransaction(devices);
        AudioQuickSetup.RestoreChangedDefaults(transaction, DataFlow.Render, new[] { "speaker", "media", "call" }, "hifi");
        devices.Render[2] = "user-headphones";
        transaction.Rollback();
        CollectionAssert.AreEqual(new[] { "hifi", "hifi", "user-headphones" }, devices.Render);
    }

    [TestMethod]
    public void HiFiInstallerRejectsUntrustedBytesBeforeExtractingAndKeepsVendorUi()
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-HiFi-Test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string bad = Path.Combine(root, "bad.zip");
            File.WriteAllText(bad, "not the pinned vendor archive");
            string extracted = Path.Combine(root, "setup");
            Assert.ThrowsException<IOException>(() => HiFiCableInstaller.Prepare(bad, extracted));
            Assert.IsFalse(Directory.Exists(extracted));
            var info = HiFiCableInstaller.StartInfo(Path.Combine(root, "HiFiCableAsioBridgeSetup.exe"));
            Assert.AreEqual("runas", info.Verb);
            Assert.AreEqual("", info.Arguments);
            Assert.IsTrue(info.UseShellExecute);
            Assert.AreEqual(System.Diagnostics.ProcessWindowStyle.Normal, info.WindowStyle);
            Assert.AreEqual(root, info.WorkingDirectory);
        }
        finally { Directory.Delete(root, true); }
    }
}
