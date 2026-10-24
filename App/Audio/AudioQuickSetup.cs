using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.Win32;
using MouseWithoutBorders.Core;
using NAudio.CoreAudioApi;

namespace MouseWithoutBorders.Audio;

internal enum QuickAudioRole { HeadsetComputer, RemoteComputer }
internal enum RemotePlaybackMode { BothComputers, HeadsetOnly }
internal sealed record CablePair(string RenderId, string CaptureId);
internal sealed class QuickAudioState
{
    public string Peer { get; set; } = "";
    public QuickAudioRole Role { get; set; }
    public bool SetDefaultMicrophone { get; set; } = true;
    public bool Pending { get; set; }
    // Zero preserves the behavior of existing .12 and older configurations.
    public RemotePlaybackMode PlaybackMode { get; set; }
    public string AppliedRenderId { get; set; } = "";
    public string[] PlaybackRestore { get; set; } = Array.Empty<string>();
    public string PendingDriver { get; set; } = "";
    public string[] DriverPreviousRender { get; set; } = Array.Empty<string>();
    public string[] DriverPreviousCapture { get; set; } = Array.Empty<string>();
    public string AppliedCaptureId { get; set; } = "";
    public string[] PreviousCapture { get; set; } = Array.Empty<string>();
    public string[] PreviousRender { get; set; } = Array.Empty<string>();
    public AudioOptions PreviousAudio { get; set; } = new();
}

internal static class AudioQuickSetup
{
    internal const string RunOnceName = "MouseWithoutBorders.AudioSetup";
    private static string StatePath => Path.Combine(SettingsUtils.DataDirectory, "audio-quick-setup.json");
    internal static QuickAudioState LoadState() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize<QuickAudioState>(File.ReadAllText(StatePath)) ?? throw new IOException("一键配置备份为空。") : null;

    internal static void SaveState(QuickAudioState state)
    {
        Directory.CreateDirectory(SettingsUtils.DataDirectory);
        string temp = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, SettingsUtils.JsonOptions));
            File.Move(temp, StatePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static CablePair FindCable(IEnumerable<AudioDeviceChoice> renders, IEnumerable<AudioDeviceChoice> captures)
    {
        // Match the base cable only, excluding A/B/C/D/16ch pins and Voicemeeter.
        // Reject ambiguous enumeration instead of connecting unrelated devices.
        var input = renders.Where(d => d.Name.StartsWith("CABLE Input (", StringComparison.OrdinalIgnoreCase)
            && d.Name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase)).ToArray();
        var output = captures.Where(d => d.Name.StartsWith("CABLE Output (", StringComparison.OrdinalIgnoreCase)
            && d.Name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (input.Length > 1 || output.Length > 1) throw new InvalidOperationException("发现多个同名 VB-CABLE 设备，请在高级设置中明确选择。");
        return input.Length == 1 && output.Length == 1 ? new(input[0].Id, output[0].Id) : null;
    }

    internal static CablePair FindCable() => FindCable(AudioDevices.List(DataFlow.Render, false), AudioDevices.List(DataFlow.Capture, false));

    internal static CablePair FindHiFiCable(IEnumerable<AudioDeviceChoice> renders, IEnumerable<AudioDeviceChoice> captures)
    {
        var input = renders.Where(d => d.Name.StartsWith("Hi-Fi Cable Input (", StringComparison.OrdinalIgnoreCase)
            && d.Name.Contains("VB-Audio Hi-Fi Cable", StringComparison.OrdinalIgnoreCase)).ToArray();
        var output = captures.Where(d => d.Name.StartsWith("Hi-Fi Cable Output (", StringComparison.OrdinalIgnoreCase)
            && d.Name.Contains("VB-Audio Hi-Fi Cable", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (input.Length > 1 || output.Length > 1) throw new InvalidOperationException("发现多个同名 Hi-Fi Cable 设备，请先确认设备配置。");
        return input.Length == 1 && output.Length == 1 ? new(input[0].Id, output[0].Id) : null;
    }

    private static CablePair FindHiFiCable() => FindHiFiCable(AudioDevices.List(DataFlow.Render, false), AudioDevices.List(DataFlow.Capture, false));

    internal static AudioOptions BuildPlan(QuickAudioRole role, string peer, string render, string capture,
        CablePair cable, AudioOptions previous, RemotePlaybackMode playbackMode = RemotePlaybackMode.BothComputers, CablePair playbackCable = null)
    {
        if (!Enum.IsDefined(playbackMode)) throw new ArgumentOutOfRangeException(nameof(playbackMode));
        if (string.IsNullOrWhiteSpace(peer) || string.IsNullOrWhiteSpace(render)) throw new ArgumentException("请选择另一台已配对电脑，并确认本机有可用的播放设备。");
        var result = new AudioOptions { Port = previous.Port, BufferMilliseconds = previous.BufferMilliseconds };
        if (role == QuickAudioRole.HeadsetComputer)
        {
            if (string.IsNullOrWhiteSpace(capture)) throw new ArgumentException("本机没有可用麦克风。");
            if (cable != null && (render == cable.RenderId || capture == cable.CaptureId))
                throw new ArgumentException("请先将本机默认设备设为实际耳机和麦克风，再使用主电脑一键配置。");
            result.SendMicrophone = new() { Enabled = true, Peer = peer, DeviceId = capture };
            result.ReceiveSystem = new() { Enabled = true, Peer = peer, DeviceId = render };
        }
        else
        {
            if (cable == null) throw new ArgumentException("尚未检测到完整的 VB-CABLE 设备，可能需要重启 Windows。");
            if (playbackMode == RemotePlaybackMode.HeadsetOnly)
            {
                if (playbackCable == null) throw new ArgumentException("仅主电脑播放需要独立的 Hi-Fi Cable 通道，尚未检测到完整设备。");
                render = playbackCable.RenderId;
            }
            AudioDevices.ValidateNoFeedback(render, cable.RenderId);
            result.SendSystem = new() { Enabled = true, Peer = peer, DeviceId = render };
            result.ReceiveMicrophone = new() { Enabled = true, Peer = peer, DeviceId = cable.RenderId };
        }
        return result;
    }

    internal static void SetMicrophone(IAudioDefaults defaults, string cableCapture)
        => SetDefaultDevice(defaults, DataFlow.Capture, cableCapture);

    internal static void SetDefaultDevice(IAudioDefaults defaults, DataFlow flow, string id)
    {
        foreach (Role role in Enum.GetValues<Role>())
        {
            defaults.Set(id, role, flow);
            if (defaults.Get(flow, role) != id) throw new IOException("Windows 未接受默认音频设备切换。");
        }
    }

    internal static void RestoreChangedDefaults(IAudioDefaults defaults, DataFlow flow, string[] originals, string cableId)
    {
        if (originals.Length != 3) return;
        foreach (Role role in Enum.GetValues<Role>())
        {
            // Preserve manual changes made since setup. Never replace a new user choice.
            if (!string.IsNullOrWhiteSpace(originals[(int)role]) && defaults.Get(flow, role) == cableId)
            {
                defaults.Set(originals[(int)role], role, flow);
                if (defaults.Get(flow, role) != originals[(int)role]) throw new IOException("Windows 未能还原原来的音频设备。");
            }
        }
    }

    internal static async Task<bool> ApplyAsync(string peer, QuickAudioRole role, bool defaultMicrophone, Action<string> progress, bool resume = false,
        RemotePlaybackMode playbackMode = RemotePlaybackMode.BothComputers)
    {
        if (role == QuickAudioRole.HeadsetComputer) playbackMode = RemotePlaybackMode.BothComputers;
        progress("检查鼠标配对与音频设置……");
        if (!MachineStuff.MachineMatrix.Any(n => string.Equals(n, peer, StringComparison.OrdinalIgnoreCase))
            || string.Equals(peer, Common.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请先完成鼠标配对，并选择另一台电脑。");
        var defaults = new WindowsAudioDefaults();
        progress("检查 Windows 默认音频设备接口……");
        if (role == QuickAudioRole.RemoteComputer) WindowsAudioDefaults.CheckSupport();
        progress("读取并备份原来的默认播放设备和麦克风……");
        var prior = LoadState();
        string priorJson = prior == null ? null : JsonSerializer.Serialize(prior);
        if (prior != null && (prior.Pending || !string.IsNullOrEmpty(prior.AppliedCaptureId) || !string.IsNullOrEmpty(prior.AppliedRenderId)) && (prior.Role != role || prior.Peer != peer))
            throw new InvalidOperationException("请先点击“还原一键配置”，再更换角色或电脑。");
        var state = prior != null && prior.Peer == peer && prior.Role == role ? prior : new QuickAudioState
        {
            Peer = peer, Role = role, PreviousAudio = AudioService.Instance.Options,
            PreviousRender = Enum.GetValues<Role>().Select(r => defaults.Get(DataFlow.Render, r)).ToArray(),
            PreviousCapture = Enum.GetValues<Role>().Select(r => SafeCaptureDefault(defaults, r)).ToArray(),
        };
        state.SetDefaultMicrophone = defaultMicrophone;
        state.PlaybackMode = role == QuickAudioRole.RemoteComputer ? playbackMode : RemotePlaybackMode.BothComputers;
        if (role == QuickAudioRole.RemoteComputer && playbackMode == RemotePlaybackMode.HeadsetOnly && state.PlaybackRestore.Length != 3)
            state.PlaybackRestore = Enum.GetValues<Role>().Select(r => defaults.Get(DataFlow.Render, r)).ToArray();
        progress("查找可用的 VB-CABLE 播放与录音端点……");
        var cable = FindCable();
        if (role == QuickAudioRole.RemoteComputer && cable == null)
        {
            if (resume && state.PendingDriver != "HiFi") throw new IOException("重启后仍未检测到完整 VB-CABLE 设备，请查看官方安装结果或点击按钮重试。");
            // Do not reinstall over disabled/partial installations: the vendor may offer removal.
            progress("检查未就绪或已禁用的虚拟声卡，跳过已不存在的旧设备……");
            if (HasInactiveCable()) throw new IOException("检测到未就绪或已禁用的 VB-CABLE。请先重启 Windows，或在声音设置中启用设备；不会重复安装或卸载已有驱动。");
            state.Pending = true;
            state.PendingDriver = "VB-CABLE";
            SaveState(state); // Back up playback AND capture before the installer can change them.
            try { await CableInstaller.InstallAsync(progress); }
            catch (OperationCanceledException) { state.Pending = false; SaveState(state); throw; }
            for (int i = 0; i < 10 && cable == null; i++) { await Task.Delay(1000); cable = FindCable(); }
            if (cable == null)
            {
                using var runOnce = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
                runOnce.SetValue(RunOnceName, "\"" + Environment.ProcessPath + "\"");
                progress("驱动端点尚未就绪。请重启 Windows；程序将在登录后打开并继续本次配置。不要移动程序文件夹。若未自动打开，手动运行本程序也会继续。");
                return false;
            }
        }
        CablePair playbackCable = FindHiFiCable();
        if (role == QuickAudioRole.RemoteComputer && playbackMode == RemotePlaybackMode.HeadsetOnly && playbackCable == null)
        {
            if (resume && state.PendingDriver == "HiFi") throw new IOException("重启后仍未检测到完整 Hi-Fi Cable。请确认官方安装结果并重试；若 Windows 不支持此驱动，请保留“两边同时播放”。");
            if (HasInactiveCable("VB-Audio Hi-Fi Cable")) throw new IOException("检测到未就绪或已禁用的 Hi-Fi Cable，请重启或启用设备后重试；不会重复安装或卸载驱动。");
            state.Pending = true;
            SaveState(state);
            state.PendingDriver = "HiFi";
            state.DriverPreviousRender = Enum.GetValues<Role>().Select(r => defaults.Get(DataFlow.Render, r)).ToArray();
            state.DriverPreviousCapture = Enum.GetValues<Role>().Select(r => SafeCaptureDefault(defaults, r)).ToArray();
            SaveState(state);
            try { await HiFiCableInstaller.InstallAsync(progress); }
            catch (OperationCanceledException) { state.Pending = false; SaveState(state); throw; }
            catch { state.Pending = false; SaveState(state); throw; }
            finally { RestoreHiFiInstallerDefaults(defaults, state); }
            for (int i = 0; i < 10 && playbackCable == null; i++) { await Task.Delay(1000); playbackCable = FindHiFiCable(); }
            if (playbackCable == null)
            {
                using var runOnce = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
                runOnce.SetValue(RunOnceName, "\"" + Environment.ProcessPath + "\"");
                progress("Hi-Fi Cable 端点尚未就绪，请重启 Windows；程序将在登录后继续本次配置。当前配置尚未完成。");
                return false;
            }
        }
        if (state.PendingDriver == "HiFi") RestoreHiFiInstallerDefaults(defaults, state);
        // Drivers can switch the default speaker to CABLE Input. Restore the original
        // playback roles before selecting the system-audio source, avoiding feedback.
        if (role == QuickAudioRole.RemoteComputer && cable != null)
        {
            progress("检查并恢复原来的播放设备……");
            RestoreCablePlayback(defaults, state.PreviousRender);
            if (playbackCable != null)
            {
                // Vendor setup may select its own capture endpoint. It is not our microphone route.
                RestoreChangedDefaults(defaults, DataFlow.Capture, state.PreviousCapture, playbackCable.CaptureId);
            }
            if (playbackCable != null && string.IsNullOrEmpty(state.AppliedRenderId))
                RestoreChangedDefaults(defaults, DataFlow.Render,
                    state.PlaybackRestore.Length == 3 ? state.PlaybackRestore : state.PreviousRender, playbackCable.RenderId);
        }
        var before = AudioService.Instance.Options;
        var transaction = new AudioDefaultsTransaction(defaults);
        state.Pending = true;
        SaveState(state);
        try
        {
            if (role == QuickAudioRole.RemoteComputer && playbackMode == RemotePlaybackMode.BothComputers)
                RestorePlayback(transaction, state);
            progress("检查当前播放设备与音频路由……");
            string render = defaults.Get(DataFlow.Render, Role.Console);
            using (var sourceDevice = AudioDevices.Open(render, DataFlow.Render))
                if (sourceDevice.DeviceFriendlyName.Equals("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase)
                    || (playbackMode == RemotePlaybackMode.BothComputers && sourceDevice.DeviceFriendlyName.Equals("VB-Audio Hi-Fi Cable", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("默认播放设备仍是虚拟音频线，请先选回实际扬声器或耳机，再使用一键配置。");
            string capture = SafeCaptureDefault(defaults, Role.Console);
            var plan = BuildPlan(role, peer, render, capture, cable, AudioService.Instance.Options, playbackMode, playbackCable);
            plan.Validate(Common.MachineName, Class.Setting.Values.TcpPort);
            state.Pending = true;
            SaveState(state);
            if (role == QuickAudioRole.RemoteComputer)
            {
                state.AppliedCaptureId = cable.CaptureId;
                SaveState(state); // Persistent recovery point before changing any default role.
                if (defaultMicrophone)
                {
                    progress("将虚拟声卡设置为默认麦克风……");
                    SetMicrophone(transaction, cable.CaptureId);
                }
                else RestoreChangedDefaults(transaction, DataFlow.Capture, state.PreviousCapture, cable.CaptureId);
                if (playbackMode == RemotePlaybackMode.HeadsetOnly)
                {
                    state.AppliedRenderId = playbackCable.RenderId;
                    SaveState(state); // Recovery point before changing any playback role.
                    progress("将副电脑的系统声音切换到独立通道，仅在主电脑播放……");
                    SetDefaultDevice(transaction, DataFlow.Render, playbackCable.RenderId);
                }
            }
            progress("正在保存两条音频通道……");
            await AudioService.Instance.ApplyAsync(plan);
            state.Pending = false;
            state.PendingDriver = "";
            SaveState(state);
            using var runOnce = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", true);
            runOnce?.DeleteValue(RunOnceName, false);
            progress("本机配置已完成。另一台请选择相反角色，双方鼠标连接后音频会自动连接。\r\n已固定指定麦克风的会议/游戏软件，仍需改为“系统默认”或 CABLE Output；可能需要重开应用。");
            if (role == QuickAudioRole.RemoteComputer)
                progress(playbackMode == RemotePlaybackMode.HeadsetOnly
                    ? "播放位置：仅主电脑。已打开的播放器/游戏若仍从扬声器出声，请改为“系统默认”并重开应用。主电脑未连接时不会在本机播放；需要本机声音时切回“两边同时播放”或点击“还原一键配置”。"
                    : "播放位置：两边同时播放。副电脑扬声器保留声音。 ");
            return true;
        }
        catch
        {
            transaction.Rollback();
            await AudioService.Instance.ApplyAsync(before);
            if (priorJson != null) SaveState(JsonSerializer.Deserialize<QuickAudioState>(priorJson));
            else
            {
                state.Pending = false; state.PendingDriver = ""; state.AppliedRenderId = "";
                state.PlaybackMode = RemotePlaybackMode.BothComputers;
                SaveState(state);
            }
            throw;
        }
    }

    internal static async Task RestoreAsync()
    {
        var state = LoadState();
        if (state == null) return;
        AudioService.Instance.Stop();
        var defaults = new WindowsAudioDefaults();
        var cable = FindCable();
        if (!string.IsNullOrEmpty(state.AppliedCaptureId))
            RestoreChangedDefaults(defaults, DataFlow.Capture, state.PreviousCapture, state.AppliedCaptureId);
        else if (cable != null) RestoreChangedDefaults(defaults, DataFlow.Capture, state.PreviousCapture, cable.CaptureId);
        if (cable != null) RestoreCablePlayback(defaults, state.PreviousRender);
        RestorePlayback(defaults, state);
        var playbackCable = FindHiFiCable();
        if (playbackCable != null)
        {
            RestoreChangedDefaults(defaults, DataFlow.Render, state.PreviousRender, playbackCable.RenderId);
            RestoreChangedDefaults(defaults, DataFlow.Capture, state.PreviousCapture, playbackCable.CaptureId);
        }
        await AudioService.Instance.ApplyAsync(state.PreviousAudio);
        using var runOnce = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", true);
        runOnce?.DeleteValue(RunOnceName, false);
        File.Delete(StatePath);
    }

    private static string SafeCaptureDefault(IAudioDefaults defaults, Role role)
    {
        try { return defaults.Get(DataFlow.Capture, role); }
        catch (System.Runtime.InteropServices.COMException) { return ""; }
    }
    internal static void CancelPending()
    {
        var state = LoadState();
        if (state?.Pending == true) { state.Pending = false; SaveState(state); }
        using var runOnce = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", true);
        runOnce?.DeleteValue(RunOnceName, false);
    }

    internal static void RestorePlayback(IAudioDefaults defaults, QuickAudioState state)
    {
        if (!string.IsNullOrEmpty(state.AppliedRenderId))
            RestoreChangedDefaults(defaults, DataFlow.Render,
                state.PlaybackRestore.Length == 3 ? state.PlaybackRestore : state.PreviousRender, state.AppliedRenderId);
        state.AppliedRenderId = "";
        state.PlaybackRestore = Array.Empty<string>();
    }

    internal static void RestorePlaybackOnStop()
    {
        var state = LoadState();
        if (state == null) return;
        var defaults = new WindowsAudioDefaults();
        RestoreHiFiInstallerDefaults(defaults, state);
        RestorePlayback(defaults, state);
        state.PlaybackMode = RemotePlaybackMode.BothComputers;
        SaveState(state);
    }
    private static void RestoreHiFiInstallerDefaults(IAudioDefaults defaults, QuickAudioState state)
    {
        if (state.PendingDriver != "HiFi") return;
        foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            var originals = flow == DataFlow.Render ? state.DriverPreviousRender : state.DriverPreviousCapture;
            if (originals.Length != 3) continue;
            foreach (Role role in Enum.GetValues<Role>())
            {
                string id = flow == DataFlow.Render ? defaults.Get(flow, role) : SafeCaptureDefault(defaults, role);
                if (string.IsNullOrEmpty(id)) continue;
                using var device = AudioDevices.Open(id, flow);
                if (device.DeviceFriendlyName.Equals("VB-Audio Hi-Fi Cable", StringComparison.OrdinalIgnoreCase))
                    RestoreChangedDefaults(defaults, flow, originals, id);
            }
        }
    }

    private static void RestoreCablePlayback(IAudioDefaults defaults, string[] originals)
    {
        if (originals.Length != 3) return;
        foreach (Role role in Enum.GetValues<Role>())
        {
            string current = defaults.Get(DataFlow.Render, role);
            using var device = AudioDevices.Open(current, DataFlow.Render);
            if (device.DeviceFriendlyName.Equals("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase))
                RestoreChangedDefaults(defaults, DataFlow.Render, originals, current);
        }
    }
    internal static bool IsExistingCable(DeviceState state, Func<string> readName)
    {
        // Windows can retain unplugged hardware records whose property store no longer
        // exists (0xE000020B). Never read names from NotPresent endpoints.
        if ((state & DeviceState.NotPresent) != 0) return false;
        return string.Equals(readName(), "VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase);
    }

    internal static string DescribeFailure(Exception error, string stage)
    {
        string detail = unchecked((uint)error.HResult) == 0xE000020B
            ? "Windows 找不到对应的音频设备实例。请刷新设备列表；如果刚装过驱动，请重启 Windows 后重试。"
            : error.Message;
        return "本次配置尚未完成。\r\n失败步骤：" + stage + "\r\n" + detail + $"\r\n错误码：0x{error.HResult:X8}";
    }

    private static bool HasInactiveCable(string name = "VB-Audio Virtual Cable")
    {
        using var devices = new MMDeviceEnumerator();
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
        {
            using (device) if ((device.State & DeviceState.NotPresent) == 0
                && string.Equals(device.DeviceFriendlyName, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
