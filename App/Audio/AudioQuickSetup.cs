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
internal sealed record CablePair(string RenderId, string CaptureId);
internal sealed class QuickAudioState
{
    public string Peer { get; set; } = "";
    public QuickAudioRole Role { get; set; }
    public bool SetDefaultMicrophone { get; set; } = true;
    public bool Pending { get; set; }
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

    internal static AudioOptions BuildPlan(QuickAudioRole role, string peer, string render, string capture,
        CablePair cable, AudioOptions previous)
    {
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
            AudioDevices.ValidateNoFeedback(render, cable.RenderId);
            result.SendSystem = new() { Enabled = true, Peer = peer, DeviceId = render };
            result.ReceiveMicrophone = new() { Enabled = true, Peer = peer, DeviceId = cable.RenderId };
        }
        return result;
    }

    internal static void SetMicrophone(IAudioDefaults defaults, string cableCapture)
    {
        foreach (Role role in Enum.GetValues<Role>())
        {
            defaults.Set(cableCapture, role);
            if (defaults.Get(DataFlow.Capture, role) != cableCapture) throw new IOException("Windows 未接受默认麦克风切换。");
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
                defaults.Set(originals[(int)role], role);
                if (defaults.Get(flow, role) != originals[(int)role]) throw new IOException("Windows 未能还原原来的音频设备。");
            }
        }
    }

    internal static async Task<bool> ApplyAsync(string peer, QuickAudioRole role, bool defaultMicrophone, Action<string> progress, bool resume = false)
    {
        if (!MachineStuff.MachineMatrix.Any(n => string.Equals(n, peer, StringComparison.OrdinalIgnoreCase))
            || string.Equals(peer, Common.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请先完成鼠标配对，并选择另一台电脑。");
        var defaults = new WindowsAudioDefaults();
        if (role == QuickAudioRole.RemoteComputer) WindowsAudioDefaults.CheckSupport();
        var prior = LoadState();
        if (prior != null && (prior.Pending || !string.IsNullOrEmpty(prior.AppliedCaptureId)) && (prior.Role != role || prior.Peer != peer))
            throw new InvalidOperationException("请先点击“还原一键配置”，再更换角色或电脑。");
        var state = prior != null && prior.Peer == peer && prior.Role == role ? prior : new QuickAudioState
        {
            Peer = peer, Role = role, PreviousAudio = AudioService.Instance.Options,
            PreviousRender = Enum.GetValues<Role>().Select(r => defaults.Get(DataFlow.Render, r)).ToArray(),
            PreviousCapture = Enum.GetValues<Role>().Select(r => SafeCaptureDefault(defaults, r)).ToArray(),
        };
        state.SetDefaultMicrophone = defaultMicrophone;
        var cable = FindCable();
        if (role == QuickAudioRole.RemoteComputer && cable == null)
        {
            if (resume) throw new IOException("重启后仍未检测到完整 VB-CABLE 设备，请查看官方安装结果或点击按钮重试。");
            // Do not reinstall over disabled/partial installations: the vendor may offer removal.
            if (HasInactiveCable()) throw new IOException("检测到未就绪或已禁用的 VB-CABLE。请先重启 Windows，或在声音设置中启用设备；不会重复安装或卸载已有驱动。");
            state.Pending = true;
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
        // Drivers can switch the default speaker to CABLE Input. Restore the original
        // playback roles before selecting the system-audio source, avoiding feedback.
        if (role == QuickAudioRole.RemoteComputer && cable != null)
            RestoreCablePlayback(defaults, state.PreviousRender);
        string render = defaults.Get(DataFlow.Render, Role.Console);
        using (var sourceDevice = AudioDevices.Open(render, DataFlow.Render))
            if (sourceDevice.DeviceFriendlyName.Equals("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("默认播放设备仍是虚拟音频线，请先选回实际扬声器或耳机，再使用一键配置。");
        string capture = SafeCaptureDefault(defaults, Role.Console);
        var plan = BuildPlan(role, peer, render, capture, cable, AudioService.Instance.Options);
        plan.Validate(Common.MachineName, Class.Setting.Values.TcpPort);
        state.Pending = true;
        SaveState(state);
        var before = AudioService.Instance.Options;
        try
        {
            if (role == QuickAudioRole.RemoteComputer)
            {
                state.AppliedCaptureId = cable.CaptureId;
                SaveState(state); // Persistent recovery point before changing any default role.
                if (defaultMicrophone) SetMicrophone(defaults, cable.CaptureId);
                else RestoreChangedDefaults(defaults, DataFlow.Capture, state.PreviousCapture, cable.CaptureId);
            }
            progress("正在保存两条音频通道……");
            await AudioService.Instance.ApplyAsync(plan);
            state.Pending = false;
            SaveState(state);
            using var runOnce = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", true);
            runOnce?.DeleteValue(RunOnceName, false);
            progress("本机配置已完成。另一台请选择相反角色，双方鼠标连接后音频会自动连接。\r\n已固定指定麦克风的会议/游戏软件，仍需改为“系统默认”或 CABLE Output；可能需要重开应用。");
            return true;
        }
        catch
        {
            if (role == QuickAudioRole.RemoteComputer && cable != null)
                RestoreChangedDefaults(defaults, DataFlow.Capture, state.PreviousCapture, cable.CaptureId);
            await AudioService.Instance.ApplyAsync(before);
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
    private static bool HasInactiveCable()
    {
        using var devices = new MMDeviceEnumerator();
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
        {
            using (device) if (device.DeviceFriendlyName.Equals("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
