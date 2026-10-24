using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.PowerToys.Settings.UI.Library;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MouseWithoutBorders.Audio;

internal sealed class AudioService
{
    internal static AudioService Instance { get; } = new();
    private readonly SemaphoreSlim configureGate = new(1);
    private readonly ConcurrentDictionary<string, string> status = new();
    private readonly int[] receiving = new int[3];
    private AudioOptions options = new();
    private CancellationTokenSource stop;
    private Task running = Task.CompletedTask;
    internal AudioOptions Options => options.Clone();
    internal string Status => string.Join(Environment.NewLine, status.OrderBy(p => p.Key).Select(p => p.Key + "：" + p.Value));

    private void SetStatus(string label, string value)
    {
        if (status.TryGetValue(label, out string previous) && previous == value) return;
        status[label] = value;
        Logger.Log("Audio " + label + "：" + value);
    }

    internal void Start()
    {
        try
        {
            var loaded = AudioOptions.Load(SettingsUtils.DataDirectory);
            loaded.Validate(Common.MachineName, Setting.Values.TcpPort);
            options = loaded;
            StartWorkers();
        }
        catch (Exception ex) { SetStatus("配置", "未启动音频：" + ex.Message); }
    }

    internal async Task ApplyAsync(AudioOptions value)
    {
        value = value.Clone();
        value.Validate(Common.MachineName, Setting.Values.TcpPort);
        await configureGate.WaitAsync();
        try
        {
            value.Save(SettingsUtils.DataDirectory);
            stop?.Cancel();
            await running;
            stop?.Dispose();
            options = value;
            StartWorkers();
        }
        finally { configureGate.Release(); }
    }

    internal void Stop() => stop?.Cancel();

    private void StartWorkers()
    {
        status.Clear();
        stop = new CancellationTokenSource();
        var snapshot = options.Clone();
        var token = stop.Token;
        running = Task.Run(async () =>
        {
            var tasks = new List<Task>();
            foreach (AudioKind kind in Enum.GetValues<AudioKind>())
            {
                SetStatus(Label(true, kind), snapshot.Send(kind).Enabled ? "等待已配对电脑连接" : "关闭");
                SetStatus(Label(false, kind), snapshot.Receive(kind).Enabled ? "等待所选电脑发送" : "关闭");
                if (snapshot.Send(kind).Enabled) tasks.Add(SendLoop(snapshot, kind, token));
            }
            if (snapshot.Receives) tasks.Add(ListenLoop(snapshot, token));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        });
    }

    private static string Label(bool sending, AudioKind kind) => (sending ? "发送" : "接收") + (kind == AudioKind.System ? "系统声音" : "麦克风");

    // Use the actual address of an authenticated input connection, including IP mappings.
    // Never start audio capture merely because a hostname resolves.
    internal static IPAddress ConnectedAddress(string name)
    {
        var sockets = Common.Sk;
        if (sockets == null) return null;
        lock (sockets.TcpSocketsLock)
        {
            foreach (var socket in sockets.TcpSockets ?? Enumerable.Empty<TcpSk>())
            {
                if (socket.Status != SocketStatus.Connected || !string.Equals(socket.MachineName, name, StringComparison.OrdinalIgnoreCase)) continue;
                try { if (socket.BackingSocket?.RemoteEndPoint is IPEndPoint endpoint) return endpoint.Address; }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException) { }
            }
        }
        return null;
    }

    internal static bool ConnectedAddressMatches(string name, IPAddress address)
    {
        if (address == null || Common.Sk == null) return false;
        var sockets = Common.Sk;
        lock (sockets.TcpSocketsLock)
            foreach (var tcp in sockets.TcpSockets ?? Enumerable.Empty<TcpSk>())
            {
                if (tcp.Status != SocketStatus.Connected || !string.Equals(tcp.MachineName, name, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (tcp.BackingSocket?.RemoteEndPoint is IPEndPoint endpoint && address.MapToIPv6().Equals(endpoint.Address.MapToIPv6())) return true;
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException) { }
            }
        return false;
    }

    private static void RequireConnection(string peer, string key)
    {
        if (ConnectedAddress(peer) == null || !string.Equals(key, Encryption.MyKey, StringComparison.Ordinal))
            throw new IOException("鼠标连接或配对码已变化，等待重新配对连接。");
    }

    internal static async Task<TcpClient> ConnectPeerAsync(IPAddress address, int port, CancellationToken token)
    {
        // Input sockets are dual-stack. An IPv4 peer therefore often appears as ::ffff:x.x.x.x.
        // TcpClient(InterNetworkV6) is IPv6-only and Windows rejects that mapped destination
        // with WSAEADDRNOTAVAIL (10049). Use a native IPv4 socket for mapped addresses;
        // leave real IPv6 addresses, including their scope IDs, unchanged.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var client = new TcpClient(address.AddressFamily) { NoDelay = true, SendBufferSize = 32768, ReceiveBufferSize = 65536 };
        try
        {
            await client.ConnectAsync(address, port, token).ConfigureAwait(false);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private async Task SendLoop(AudioOptions settings, AudioKind kind, CancellationToken token)
    {
        var route = settings.Send(kind);
        string label = Label(true, kind);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var address = ConnectedAddress(route.Peer) ?? throw new IOException("等待 " + route.Peer + " 的鼠标连接");
                string key = Encryption.MyKey;
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshake.CancelAfter(TimeSpan.FromSeconds(8));
                using var client = await ConnectPeerAsync(address, settings.Port, handshake.Token).ConfigureAwait(false);
                using var tls = new SslStream(client.GetStream(), false);
                await AudioWire.AuthenticateSender(tls, new AudioHello
                {
                    Sender = Common.MachineName, Receiver = route.Peer, Kind = kind,
                }, key, handshake.Token).ConfigureAwait(false);
                byte[] ready = new byte[1];
                await tls.ReadExactlyAsync(ready, handshake.Token).ConfigureAwait(false);
                if (ready[0] != 1) throw new IOException("接收端尚未就绪。");
                RequireConnection(route.Peer, key);
                await CaptureAndSend(tls, settings, kind, key, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { SetStatus(label, "等待 / 重试：" + FriendlyError(ex)); }
            try { await Task.Delay(ConnectedAddress(route.Peer) == null ? 250 : 1000, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        SetStatus(label, "已停止");
    }

    private async Task CaptureAndSend(SslStream tls, AudioOptions settings, AudioKind kind, string key, CancellationToken token)
    {
        var route = settings.Send(kind);
        var flow = kind == AudioKind.System ? DataFlow.Render : DataFlow.Capture;
        int channels = kind == AudioKind.System ? 2 : 1;
        using var device = AudioDevices.Open(route.DeviceId, flow);
        // Reject a feedback route before opening a capture session, even after device changes.
        if (kind == AudioKind.System)
        {
            foreach (var receive in new[] { settings.ReceiveSystem, settings.ReceiveMicrophone })
            {
                if (!receive.Enabled) continue;
                using var output = AudioDevices.Open(receive.DeviceId, DataFlow.Render);
                AudioDevices.CheckFeedback(settings, output.ID);
            }
        }
        using WasapiCapture capture = AudioLatency.CreateCapture(device, kind, settings.LowLatency);
        capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
        var queue = new AudioFrameQueue(channels, settings.LowLatency);
        capture.DataAvailable += (_, args) => queue.Add(args.Buffer, args.BytesRecorded, route.Volume);
        capture.RecordingStopped += (_, args) => queue.Complete(args.Exception);
        capture.StartRecording();
        string sendingStatus = "正在发送到 " + route.Peer + " · " + device.FriendlyName + (settings.LowLatency ? " · 低延迟" : " · 稳定缓冲");
        SetStatus(Label(true, kind), sendingStatus);
        long nextCheck = 0, nextDiagnostic = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (Environment.TickCount64 >= nextCheck)
                {
                    RequireConnection(route.Peer, key);
                    AudioDevices.EnsureCurrent(route.DeviceId, flow, device);
                    nextCheck = Environment.TickCount64 + 1000;
                }
                byte[] frame;
                using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    readTimeout.CancelAfter(1000);
                    try { frame = await queue.ReadAsync(readTimeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { frame = Array.Empty<byte>(); }
                }
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                writeTimeout.CancelAfter(3000);
                await AudioWire.WritePacket(tls, frame, writeTimeout.Token).ConfigureAwait(false);
                if (Environment.TickCount64 >= nextDiagnostic)
                {
                    SetStatus(Label(true, kind), sendingStatus + $" · 发送队列丢弃 {queue.DroppedFrames} 帧");
                    nextDiagnostic = Environment.TickCount64 + 5000;
                }
            }
        }
        finally { capture.StopRecording(); queue.Complete(); }
    }

    private async Task ListenLoop(AudioOptions settings, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var connections = new List<Task>();
            TcpListener listener = null;
            using var sessions = CancellationTokenSource.CreateLinkedTokenSource(token);
            X509Certificate2 certificate = null;
            try
            {
                certificate = AudioWire.CreateCertificate();
                listener = new TcpListener(IPAddress.IPv6Any, settings.Port);
                listener.Server.DualMode = true;
                listener.Start(8);
                SetStatus("网络", "音频接收端口 " + settings.Port);
                while (!token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    connections.RemoveAll(t => t.IsCompleted);
                    if (connections.Count >= 8) { client.Dispose(); continue; }
                    client.NoDelay = true;
                    client.SendBufferSize = 32768; client.ReceiveBufferSize = 65536;
                    connections.Add(ReceiveConnection(client, certificate, settings, sessions.Token));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { SetStatus("网络", "接收失败 / 重试：" + FriendlyError(ex)); }
            finally
            {
                listener?.Stop();
                sessions.Cancel();
                await Task.WhenAll(connections).ConfigureAwait(false);
                certificate?.Dispose();
            }
            try { await Task.Delay(2500, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveConnection(TcpClient client, X509Certificate2 certificate, AudioOptions settings, CancellationToken token)
    {
        AudioKind? admitted = null;
        using (client)
        using (var tls = new SslStream(client.GetStream(), false))
        {
            try
            {
                string key = Encryption.MyKey;
                var remoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address.MapToIPv6();
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshake.CancelAfter(8000);
                var hello = await AudioWire.AuthenticateReceiver(tls, certificate, key, hello =>
                {
                    var route = settings.Receive(hello.Kind);
                    return route.Enabled && string.Equals(hello.Sender, route.Peer, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(hello.Receiver, Common.MachineName, StringComparison.OrdinalIgnoreCase)
                        && ConnectedAddressMatches(hello.Sender, remoteAddress);
                }, handshake.Token).ConfigureAwait(false);
                if (Interlocked.CompareExchange(ref receiving[(int)hello.Kind], 1, 0) != 0)
                    throw new IOException("此音频通道已有连接。");
                admitted = hello.Kind;
                await PlayReceived(tls, settings, hello, key, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (admitted.HasValue) SetStatus(Label(false, admitted.Value), "等待 / 重试：" + FriendlyError(ex));
                // Untrusted handshakes never overwrite an active channel's status or leak secrets.
            }
            finally
            {
                if (admitted.HasValue) Interlocked.Exchange(ref receiving[(int)admitted.Value], 0);
            }
        }
    }

    private async Task PlayReceived(SslStream tls, AudioOptions settings, AudioHello hello, string key, CancellationToken token)
    {
        var route = settings.Receive(hello.Kind);
        int channels = hello.Kind == AudioKind.System ? 2 : 1;
        using var device = AudioDevices.Open(route.DeviceId, DataFlow.Render);
        AudioDevices.CheckFeedback(settings, device.ID);
        var buffer = AudioLatency.CreateBuffer(channels, settings.LowLatency, settings.BufferMilliseconds);
        int playbackMilliseconds = AudioLatency.PlaybackMilliseconds(settings.LowLatency, settings.BufferMilliseconds);
        using var playback = new WasapiOut(device, AudioClientShareMode.Shared, true, playbackMilliseconds);
        using var playbackLife = CancellationTokenSource.CreateLinkedTokenSource(token);
        Exception playbackFailure = null;
        playback.PlaybackStopped += (_, args) => { playbackFailure = args.Exception; playbackLife.Cancel(); };
        playback.Init(buffer);
        using (var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            readyTimeout.CancelAfter(3000);
            await tls.WriteAsync(new byte[] { 1 }, readyTimeout.Token).ConfigureAwait(false);
        }
        playback.Play();
        string receivingStatus = "正在接收 " + hello.Sender + " · " + device.FriendlyName + (settings.LowLatency ? " · 自适应低延迟" : " · 稳定缓冲");
        SetStatus(Label(false, hello.Kind), receivingStatus);
        long nextCheck = 0, nextDiagnostic = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (Environment.TickCount64 >= nextCheck)
                {
                    RequireConnection(hello.Sender, key);
                    AudioDevices.EnsureCurrent(route.DeviceId, DataFlow.Render, device);
                    AudioDevices.CheckFeedback(settings, device.ID);
                    nextCheck = Environment.TickCount64 + 1000;
                }
                using var frameTimeout = CancellationTokenSource.CreateLinkedTokenSource(playbackLife.Token);
                frameTimeout.CancelAfter(5000);
                byte[] frame = await AudioWire.ReadPacket(tls, AudioWire.MaxFrame, frameTimeout.Token).ConfigureAwait(false);
                AudioWire.ScaleSamples(frame, channels, route.Volume);
                if (frame.Length > 0) buffer.Add(frame);
                if (Environment.TickCount64 >= nextDiagnostic)
                {
                    SetStatus(Label(false, hello.Kind), receivingStatus + $" · 预缓冲 {buffer.TargetMilliseconds}ms · 欠载 {buffer.Underruns} 次 · 追赶丢弃 {buffer.DroppedBytes * 1000 / buffer.WaveFormat.AverageBytesPerSecond}ms");
                    nextDiagnostic = Environment.TickCount64 + 5000;
                }
            }
        }
        catch (OperationCanceledException) when (playbackFailure != null) { throw new IOException("播放设备中断。", playbackFailure); }
        finally { playback.Stop(); }
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        OperationCanceledException => "音频连接超时，请确认对方已启用接收、端口相同且网络可达。",
        System.Security.Authentication.AuthenticationException => "音频认证失败，请确认两台使用相同配对码。",
        ChannelClosedException => "音频采集已停止，等待设备恢复。",
        SocketException socket => $"网络连接失败（{socket.SocketErrorCode}/{socket.NativeErrorCode}）：{socket.Message}",
        EndOfStreamException => "对方已关闭音频连接或接收设备不可用。",
        _ => ex.Message,
    };
}
