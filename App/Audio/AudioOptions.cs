// Independent audio extension, MIT licensed. Audio is opt-in, separate from input sharing.
using System;
using System.IO;
using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;

namespace MouseWithoutBorders.Audio;

internal enum AudioKind { System = 1, Microphone = 2 }

internal sealed class AudioRoute
{
    public bool Enabled { get; set; }
    public string Peer { get; set; } = "";
    // Empty means follow the default device. Microphone receive requires an explicit endpoint.
    public string DeviceId { get; set; } = "";
    public int Volume { get; set; } = 100;
}

internal sealed class AudioOptions
{
    public AudioRoute SendSystem { get; set; } = new();
    public AudioRoute SendMicrophone { get; set; } = new();
    public AudioRoute ReceiveSystem { get; set; } = new();
    public AudioRoute ReceiveMicrophone { get; set; } = new();
    public int Port { get; set; } = 15102;
    public bool LowLatency { get; set; } = true;
    public int BufferMilliseconds { get; set; } = 160;
    public bool Receives => ReceiveSystem.Enabled || ReceiveMicrophone.Enabled;
    public AudioRoute Send(AudioKind kind) => kind == AudioKind.System ? SendSystem : SendMicrophone;
    public AudioRoute Receive(AudioKind kind) => kind == AudioKind.System ? ReceiveSystem : ReceiveMicrophone;
    public AudioOptions Clone() => JsonSerializer.Deserialize<AudioOptions>(JsonSerializer.Serialize(this));

    internal void Validate(string localName, int controlPort = 15100)
    {
        if (Port < 1024 || Port > 65535 || Port == controlPort || Port == controlPort + 1)
            throw new ArgumentException("音频端口必须为 1024–65535，并且与鼠标、剪贴板端口不同。");
        if (BufferMilliseconds < 60 || BufferMilliseconds > 500)
            throw new ArgumentException("缓冲时间须为 60–500 毫秒。");
        foreach (var route in new[] { SendSystem, SendMicrophone, ReceiveSystem, ReceiveMicrophone })
        {
            if (route == null || route.Peer == null || route.DeviceId == null)
                throw new ArgumentException("音频配置缺少必要字段。");
            route.Peer = route.Peer.Trim();
            if (route.Volume < 0 || route.Volume > 100 || route.Peer.Length > 256 || route.DeviceId.Length > 1024)
                throw new ArgumentException("音量须为 0–100%，设备和电脑名称长度须有效。");
            if (route.Enabled && (string.IsNullOrWhiteSpace(route.Peer) || route.Peer.Equals(localName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("请为每项已启用的共享选择另一台已配对电脑。");
        }
        if (ReceiveMicrophone.Enabled && string.IsNullOrWhiteSpace(ReceiveMicrophone.DeviceId))
            throw new ArgumentException("请为远端麦克风明确选择输出设备：供应用使用时选虚拟音频线的播放端（如 CABLE Input），监听时选耳机。");
    }

    internal static AudioOptions Load(string directory)
    {
        string path = Path.Combine(directory, "audio.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<AudioOptions>(File.ReadAllText(path)) ?? throw new IOException("音频配置为空。");
    }

    internal void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "audio.json");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this, SettingsUtils.JsonOptions));
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
