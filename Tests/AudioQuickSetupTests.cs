using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Audio;
using NAudio.CoreAudioApi;

namespace MouseWithoutBorders.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class AudioQuickSetupTests
{
    [TestMethod]
    public void MissingHistoricalDeviceDoesNotBlockCableInstallationDetection()
    {
        bool readMissingProperty = false;
        Assert.IsFalse(AudioQuickSetup.IsExistingCable(DeviceState.NotPresent, () =>
        {
            readMissingProperty = true;
            throw new System.Runtime.InteropServices.COMException("0xE000020B", unchecked((int)0xE000020B));
        }));
        Assert.IsFalse(readMissingProperty);
        Assert.IsTrue(AudioQuickSetup.IsExistingCable(DeviceState.Disabled, () => "VB-Audio Virtual Cable"));
        Assert.IsTrue(AudioQuickSetup.IsExistingCable(DeviceState.Unplugged, () => "VB-Audio Virtual Cable"));
        Assert.IsTrue(AudioQuickSetup.IsExistingCable(DeviceState.Active, () => "VB-Audio Virtual Cable"));
        Assert.IsFalse(AudioQuickSetup.IsExistingCable(DeviceState.Active, () => "Realtek Audio"));
    }

    [TestMethod]
    public void AudioFailureReportsStageAndOriginalErrorCode()
    {
        var error = new System.Runtime.InteropServices.COMException("0xE000020B", unchecked((int)0xE000020B));
        string message = AudioQuickSetup.DescribeFailure(error, "检测未就绪设备");
        StringAssert.Contains(message, "检测未就绪设备");
        StringAssert.Contains(message, "0xE000020B");
        StringAssert.Contains(message, "音频设备实例");
    }
    [TestMethod]
    public void WindowsDefaultEndpointInterfaceCanBeAcquiredWithoutChangingDevices()
    {
        WindowsAudioDefaults.CheckSupport();
    }

    [TestMethod]
    public void PendingSetupAndOriginalDefaultsSurviveRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-Quick-Test-" + Guid.NewGuid());
        string? previous = Environment.GetEnvironmentVariable("MWB_STANDALONE_DATA");
        try
        {
            Environment.SetEnvironmentVariable("MWB_STANDALONE_DATA", root);
            var state = new QuickAudioState
            {
                Peer = "PC-A", Role = QuickAudioRole.RemoteComputer, Pending = true,
                PreviousCapture = new[] { "mic1", "mic2", "mic3" },
                PreviousRender = new[] { "out1", "out2", "out3" },
                AppliedCaptureId = "cable", PreviousAudio = new AudioOptions { Port = 15400 },
            };
            AudioQuickSetup.SaveState(state);
            var loaded = AudioQuickSetup.LoadState();
            Assert.IsNotNull(loaded);
            Assert.IsTrue(loaded!.Pending);
            CollectionAssert.AreEqual(state.PreviousCapture, loaded.PreviousCapture);
            CollectionAssert.AreEqual(state.PreviousRender, loaded.PreviousRender);
            Assert.AreEqual(15400, loaded.PreviousAudio.Port);
            Assert.AreEqual("PC-A", loaded.Peer);
            loaded.Pending = false;
            AudioQuickSetup.SaveState(loaded);
            Assert.IsFalse(AudioQuickSetup.LoadState().Pending);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MWB_STANDALONE_DATA", previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [TestMethod]
    public void QuickSetupBuildsComplementaryRoutesWithoutChangingAdvancedPort()
    {
        var previous = new AudioOptions { Port = 15222, BufferMilliseconds = 240 };
        var cable = new CablePair("cable-render", "cable-capture");
        var main = AudioQuickSetup.BuildPlan(QuickAudioRole.HeadsetComputer, "PC-B", "headphones", "microphone", null!, previous);
        var remote = AudioQuickSetup.BuildPlan(QuickAudioRole.RemoteComputer, "PC-A", "speakers", "", cable, previous);
        main.Validate("PC-A"); remote.Validate("PC-B");
        Assert.IsTrue(main.SendMicrophone.Enabled && main.ReceiveSystem.Enabled);
        Assert.IsFalse(main.SendSystem.Enabled || main.ReceiveMicrophone.Enabled);
        Assert.AreEqual("microphone", main.SendMicrophone.DeviceId);
        Assert.IsTrue(remote.SendSystem.Enabled && remote.ReceiveMicrophone.Enabled);
        Assert.IsFalse(remote.SendMicrophone.Enabled || remote.ReceiveSystem.Enabled);
        Assert.AreEqual("cable-render", remote.ReceiveMicrophone.DeviceId);
        Assert.AreEqual(15222, main.Port); Assert.AreEqual(240, remote.BufferMilliseconds);
        Assert.IsFalse(previous.Receives || previous.SendMicrophone.Enabled);
        Assert.ThrowsException<ArgumentException>(() => AudioQuickSetup.BuildPlan(QuickAudioRole.RemoteComputer, "PC-A", "speakers", "", null!, previous));
        Assert.ThrowsException<InvalidOperationException>(() => AudioQuickSetup.BuildPlan(QuickAudioRole.RemoteComputer, "PC-A", "cable-render", "", cable, previous));
    }

    [TestMethod]
    public void CableDetectionRejectsPartialAndAmbiguousDevicesAndOtherProducts()
    {
        AudioDeviceChoice input = new("input", "CABLE Input (VB-Audio Virtual Cable)");
        AudioDeviceChoice output = new("output", "CABLE Output (VB-Audio Virtual Cable)");
        var result = AudioQuickSetup.FindCable(new[] { input, new AudioDeviceChoice("16ch", "CABLE In 16ch (VB-Audio Virtual Cable)") }, new[] { output });
        Assert.AreEqual(new CablePair("input", "output"), result);
        Assert.IsNull(AudioQuickSetup.FindCable(new[] { input }, Array.Empty<AudioDeviceChoice>()));
        Assert.IsNull(AudioQuickSetup.FindCable(new[] { new AudioDeviceChoice("A", "CABLE-A Input (VB-Audio Cable A)") }, new[] { output }));
        Assert.ThrowsException<InvalidOperationException>(() => AudioQuickSetup.FindCable(new[] { input, input with { Id = "duplicate" } }, new[] { output }));
    }

    private sealed class FakeDefaults : IAudioDefaults
    {
        internal string[] Capture = { "mic-console", "mic-media", "mic-call" };
        public string Get(DataFlow flow, Role role) => Capture[(int)role];
        public void Set(string id, Role role, DataFlow flow = DataFlow.Capture) => Capture[(int)role] = id;
    }

    [TestMethod]
    public void DefaultMicChangeCoversEveryRoleAndRestorePreservesLaterUserChoice()
    {
        var defaults = new FakeDefaults();
        string[] original = defaults.Capture.ToArray();
        AudioQuickSetup.SetMicrophone(defaults, "cable");
        CollectionAssert.AreEqual(new[] { "cable", "cable", "cable" }, defaults.Capture);
        defaults.Capture[(int)Role.Communications] = "user-chosen-new-mic";
        AudioQuickSetup.RestoreChangedDefaults(defaults, DataFlow.Capture, original, "cable");
        CollectionAssert.AreEqual(new[] { "mic-console", "mic-media", "user-chosen-new-mic" }, defaults.Capture);
    }

    private sealed class RejectDefaults : IAudioDefaults
    {
        public string Get(DataFlow flow, Role role) => "unchanged";
        public void Set(string id, Role role, DataFlow flow = DataFlow.Capture) { }
    }
    [TestMethod]
    public void SilentDefaultDeviceFailureIsReported()
    {
        Assert.ThrowsException<IOException>(() => AudioQuickSetup.SetMicrophone(new RejectDefaults(), "cable"));
    }

    [TestMethod]
    public void DriverPackageIsPinnedAndPreparedWithoutExecutingOrElevating()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Drivers", "VBCABLE_Driver_Pack45.zip"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        string root = Path.Combine(Path.GetTempPath(), "MWB-Driver-Test-" + Guid.NewGuid());
        try
        {
            string exe = CableInstaller.Prepare(Path.Combine(directory!.FullName, "Drivers", "VBCABLE_Driver_Pack45.zip"), root);
            var info = CableInstaller.StartInfo(exe);
            Assert.AreEqual("runas", info.Verb);
            Assert.AreEqual("-i", info.Arguments);
            Assert.IsTrue(info.UseShellExecute);
            Assert.AreEqual(root, info.WorkingDirectory);
            Assert.IsTrue(File.Exists(Path.Combine(root, "vbMmeCable64_win10.inf")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "readme.txt")));
            File.AppendAllText(exe, "tampered");
            Assert.ThrowsException<IOException>(() => CableInstaller.Verify(exe, CableInstaller.SetupHash));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void QuickSetupUiRendersWithoutInstallingDriversOrChangingDefaults()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                using var form = new AudioQuickSetupForm(new[] { "另一台电脑" }, new() { Peer = "另一台电脑", Role = QuickAudioRole.RemoteComputer }, preview: true);
                form.Opacity = 0; form.ShowInTaskbar = false; form.Show();
                form.PerformLayout();
                using var image = new System.Drawing.Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                Directory.CreateDirectory("test-results");
                image.Save("test-results/audio-quick-setup.png", System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
