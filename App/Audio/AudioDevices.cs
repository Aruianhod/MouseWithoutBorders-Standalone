using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace MouseWithoutBorders.Audio;

internal sealed record AudioDeviceChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

internal static class AudioDevices
{
    internal static List<AudioDeviceChoice> List(DataFlow flow, bool includeDefault = true)
    {
        var list = new List<AudioDeviceChoice>();
        if (includeDefault) list.Add(new("", "跟随 Windows 默认设备"));
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device) list.Add(new(device.ID, device.FriendlyName));
        }
        return list;
    }

    internal static MMDevice Open(string id, DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(id)
            ? enumerator.GetDefaultAudioEndpoint(flow, Role.Console) : enumerator.GetDevice(id);
        if (device.State != DeviceState.Active || device.DataFlow != flow)
        {
            device.Dispose();
            throw new InvalidOperationException("所选音频设备不可用，请连接设备或重新选择。");
        }
        return device;
    }

    internal static void EnsureCurrent(string selected, DataFlow flow, MMDevice opened)
    {
        if (opened.State != DeviceState.Active) throw new InvalidOperationException("音频设备已断开，等待恢复。");
        if (string.IsNullOrEmpty(selected))
        {
            using var current = Open(selected, flow);
            if (current.ID != opened.ID) throw new InvalidOperationException("默认音频设备已更改，正在重新连接。");
        }
    }

    internal static void CheckFeedback(AudioOptions options, string receiveDeviceId)
    {
        if (!options.SendSystem.Enabled) return;
        using var capture = Open(options.SendSystem.DeviceId, DataFlow.Render);
        ValidateNoFeedback(capture.ID, receiveDeviceId);
    }

    internal static void ValidateNoFeedback(string captureDeviceId, string receiveDeviceId)
    {
        if (string.Equals(captureDeviceId, receiveDeviceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能把接收到的声音播放到正在共享的同一设备，否则会循环回传。请选择另一输出设备或关闭系统声音发送。");
    }
}
