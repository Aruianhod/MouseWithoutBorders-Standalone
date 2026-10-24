using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MouseWithoutBorders.Audio;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Sync;

internal sealed record SyncRevision(long Counter, string Origin) : IComparable<SyncRevision>
{
    public int CompareTo(SyncRevision other) => other == null ? 1 : Counter != other.Counter
        ? Counter.CompareTo(other.Counter) : string.CompareOrdinal(Origin, other.Origin);
    internal void Validate()
    {
        if (Counter < 0 || Counter > 1000000000000000 || !Guid.TryParseExact(Origin, "N", out _))
            throw new InvalidDataException("设置版本无效。");
    }
}
internal sealed class SharedSettings
{
    public bool Clipboard { get; set; }
    public bool Files { get; set; }
    public bool Wrap { get; set; }
    public bool Corners { get; set; }
    public bool Fullscreen { get; set; }
    public int EasyMouse { get; set; }
    public bool RelativeMouse { get; set; }
    public bool DrawMouse { get; set; }
    public bool HideMouse { get; set; }
    public bool OneRow { get; set; }
    public string[] Layout { get; set; } = Array.Empty<string>();
    internal void Validate()
    {
        if (EasyMouse < 0 || EasyMouse > 3 || Layout == null || Layout.Length > 4
            || Layout.Any(n => n == null || n.Length > 256 || n.Contains(','))) throw new InvalidDataException("通用设置无效。");
        var names = Layout.Where(n => n.Length > 0).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new InvalidDataException("电脑排列重复。");
    }
    internal static SharedSettings Capture() => new()
    {
        Clipboard = Setting.Values.ShareClipboard, Files = Setting.Values.TransferFile,
        Wrap = Setting.Values.MatrixCircle, Corners = Setting.Values.BlockMouseAtCorners,
        Fullscreen = Setting.Values.DisableEasyMouseWhenForegroundWindowIsFullscreen, EasyMouse = Setting.Values.EasyMouse,
        RelativeMouse = Setting.Values.MoveMouseRelatively, DrawMouse = Setting.Values.DrawMouse, HideMouse = Setting.Values.HideMouse,
        OneRow = Setting.Values.MatrixOneRow, Layout = Setting.Values.MachineMatrixString.Split(','),
    };
    internal void Apply(string peer)
    {
        Validate();
        var settings = Setting.Values;
        bool previousPause = settings.PauseInstantSaving;
        try
        {
            settings.PauseInstantSaving = true;
            settings.ShareClipboard = Clipboard; settings.TransferFile = Files;
            settings.MatrixCircle = Wrap; settings.BlockMouseAtCorners = Corners;
            settings.DisableEasyMouseWhenForegroundWindowIsFullscreen = Fullscreen; settings.EasyMouse = EasyMouse;
            settings.MoveMouseRelatively = RelativeMouse; settings.DrawMouse = DrawMouse; settings.HideMouse = HideMouse;
            if (Layout.Contains(Common.MachineName, StringComparer.OrdinalIgnoreCase) && Layout.Contains(peer, StringComparer.OrdinalIgnoreCase))
            { settings.MachineMatrixString = string.Join(',', Layout); settings.MatrixOneRow = OneRow; MachineStuff.MachineMatrix = null; }
            settings.SaveSettings();
            settings.Changed = true;
        }
        finally { settings.PauseInstantSaving = previousPause; }
        MachineStuff.SendMachineMatrix();
    }
}
internal sealed class SharedRoute
{
    public AudioKind Kind { get; set; }
    public bool Sending { get; set; }
    public string Peer { get; set; } = "";
    public bool Enabled { get; set; }
}
internal sealed class SharedAudio
{
    public int Port { get; set; }
    public int BufferMilliseconds { get; set; }
    public bool LowLatency { get; set; }
    public SharedRoute[] Routes { get; set; } = Array.Empty<SharedRoute>();
    internal static SharedAudio Capture(AudioOptions options) => new()
    {
        Port = options.Port, BufferMilliseconds = options.BufferMilliseconds, LowLatency = options.LowLatency,
        Routes = Enum.GetValues<AudioKind>().SelectMany(kind => new[] {
            new SharedRoute { Kind = kind, Sending = true, Peer = options.Send(kind).Peer, Enabled = options.Send(kind).Enabled },
            new SharedRoute { Kind = kind, Sending = false, Peer = options.Receive(kind).Peer, Enabled = options.Receive(kind).Enabled },
        }).ToArray(),
    };
    internal void Validate()
    {
        if (Port < 1024 || Port > 65535 || BufferMilliseconds < 60 || BufferMilliseconds > 500
            || Routes == null || Routes.Length != 4 || Routes.Any(r => r == null || !Enum.IsDefined(r.Kind) || r.Peer == null || r.Peer.Length > 256)
            || Routes.Select(r => (r.Kind, r.Sending)).Distinct().Count() != 4) throw new InvalidDataException("音频同步设置无效。");
    }
    internal AudioOptions Mirror(string owner, string local, AudioOptions current, Func<string> microphoneOutput)
    {
        Validate();
        var plan = current.Clone();
        plan.Port = Port; plan.BufferMilliseconds = BufferMilliseconds; plan.LowLatency = LowLatency;
        foreach (var route in Routes)
        {
            if (!string.Equals(route.Peer, local, StringComparison.OrdinalIgnoreCase)) continue;
            var destination = route.Sending ? plan.Receive(route.Kind) : plan.Send(route.Kind);
            if (destination.Enabled && !string.Equals(destination.Peer, owner, StringComparison.OrdinalIgnoreCase))
                throw new IOException("本机音频通道正用于另一台电脑，请先在音频设置中选择要共享的电脑。");
            destination.Peer = owner; destination.Enabled = route.Enabled;
            if (destination.Enabled && route.Sending && route.Kind == AudioKind.Microphone && string.IsNullOrEmpty(destination.DeviceId))
                destination.DeviceId = microphoneOutput() ?? throw new IOException("接收远端麦克风需要在本机先完成一次虚拟声卡安装或选择接收设备。");
        }
        return plan;
    }
}
internal sealed class SyncDocument
{
    public string Protocol { get; set; } = "MWB-SETTINGS-1";
    public string Owner { get; set; }
    public SyncRevision Revision { get; set; }
    public SharedSettings Shared { get; set; }
    public SharedAudio Audio { get; set; }
    internal void Validate(string peer)
    {
        if (Protocol != "MWB-SETTINGS-1" || !string.Equals(Owner, peer, StringComparison.OrdinalIgnoreCase) || Revision == null || Shared == null)
            throw new InvalidDataException("设置来源或版本无效。");
        Revision.Validate(); Shared.Validate(); Audio?.Validate();
    }
}
internal sealed class SyncState
{
    public bool Enabled { get; set; } = true;
    public bool IncludeAudio { get; set; } = true;
    public string Identity { get; set; } = Guid.NewGuid().ToString("N");
    public long Clock { get; set; }
    public SyncRevision Revision { get; set; }
    public string LocalFingerprint { get; set; } = "";
    public SyncDocument PendingAudio { get; set; }
    internal bool Observe(string fingerprint, bool force = false)
    {
        if (!force && fingerprint == LocalFingerprint && Revision != null) return false;
        Clock = Math.Max(Clock, Revision?.Counter ?? 0) + 1;
        Revision = new(Clock, Identity); LocalFingerprint = fingerprint; PendingAudio = null;
        return true;
    }
    internal void Adopt(SyncRevision revision, string fingerprint)
    {
        Clock = Math.Max(Clock, revision.Counter); Revision = revision; LocalFingerprint = fingerprint;
    }
}
