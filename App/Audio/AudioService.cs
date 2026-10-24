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

    internal void Start()
    {
        try
        {
            var loaded = AudioOptions.Load(SettingsUtils.DataDirectory);
            loaded.Validate(Common.MachineName, Setting.Values.TcpPort);
            options = loaded;
            StartWorkers();
        }
        catch (Exception ex) { status["配置"] = "未启动音频：" + ex.Message; }
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
                status[Label(true, kind)] = snapshot.Send(kind).Enabled ? "等待已配对电脑连接" : "关闭";
                status[Label(false, kind)] = snapshot.Receive(kind).Enabled ? "等待所选电脑发送" : "关闭";
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

    private static void RequireConnection(string peer, string key)
    {
        if (ConnectedAddress(peer) == null || !string.Equals(key, Encryption.MyKey, StringComparison.Ordinal))
            throw new IOException("鼠标连接或配对码已变化，等待重新配对连接。");
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
                using var client = new TcpClient(address.AddressFamily) { NoDelay = true };
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshake.CancelAfter(TimeSpan.FromSeconds(8));
                await client.ConnectAsync(address, settings.Port, handshake.Token).ConfigureAwait(false);
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
            catch (Exception ex) { status[label] = "等待 / 重试：" + FriendlyError(ex); }
            try { await Task.Delay(2500, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        status[label] = "已停止";
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
        using WasapiCapture capture = kind == AudioKind.System ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, true, 20);
        capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(10)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest,
        });
        capture.DataAvailable += (_, args) =>
        {
            int frameSize = 960 * channels * 4;
            for (int offset = 0; offset < args.BytesRecorded; offset += frameSize)
            {
                int size = Math.Min(frameSize, args.BytesRecorded - offset);
                if (size % (channels * 4) != 0) continue;
                var frame = new byte[size];
                Buffer.BlockCopy(args.Buffer, offset, frame, 0, size);
                AudioWire.ScaleSamples(frame, channels, route.Volume);
                queue.Writer.TryWrite(frame);
            }
        };
        capture.RecordingStopped += (_, args) => queue.Writer.TryComplete(args.Exception);
        capture.StartRecording();
        status[Label(true, kind)] = "正在发送到 " + route.Peer + " · " + device.FriendlyName;
        long nextCheck = 0;
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
                    try { frame = await queue.Reader.ReadAsync(readTimeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { frame = Array.Empty<byte>(); }
                }
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                writeTimeout.CancelAfter(3000);
                await AudioWire.WritePacket(tls, frame, writeTimeout.Token).ConfigureAwait(false);
            }
        }
        finally { capture.StopRecording(); queue.Writer.TryComplete(); }
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
                status["网络"] = "音频接收端口 " + settings.Port;
                while (!token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    connections.RemoveAll(t => t.IsCompleted);
                    if (connections.Count >= 8) { client.Dispose(); continue; }
                    client.NoDelay = true;
                    connections.Add(ReceiveConnection(client, certificate, settings, sessions.Token));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { status["网络"] = "接收失败 / 重试：" + FriendlyError(ex); }
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
                        && remoteAddress.Equals(ConnectedAddress(hello.Sender)?.MapToIPv6());
                }, handshake.Token).ConfigureAwait(false);
                if (Interlocked.CompareExchange(ref receiving[(int)hello.Kind], 1, 0) != 0)
                    throw new IOException("此音频通道已有连接。");
                admitted = hello.Kind;
                await PlayReceived(tls, settings, hello, key, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (admitted.HasValue) status[Label(false, admitted.Value)] = "等待 / 重试：" + FriendlyError(ex);
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
        var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, channels))
        {
            BufferDuration = TimeSpan.FromMilliseconds(settings.BufferMilliseconds + 100),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        using var playback = new WasapiOut(device, AudioClientShareMode.Shared, true, Math.Min(settings.BufferMilliseconds, 100));
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
        status[Label(false, hello.Kind)] = "正在接收 " + hello.Sender + " · " + device.FriendlyName;
        long nextCheck = 0;
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
                if (buffer.BufferedDuration.TotalMilliseconds > settings.BufferMilliseconds) buffer.ClearBuffer();
                if (frame.Length > 0) buffer.AddSamples(frame, 0, frame.Length);
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
        EndOfStreamException => "对方已关闭音频连接或接收设备不可用。",
        _ => ex.Message,
    };
}
