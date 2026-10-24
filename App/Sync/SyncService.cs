using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.PowerToys.Settings.UI.Library;
using MouseWithoutBorders.Audio;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;
using NAudio.CoreAudioApi;

namespace MouseWithoutBorders.Sync;

internal static class SyncService
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static readonly ConcurrentDictionary<string, long> capable = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> peerStatus = new(StringComparer.OrdinalIgnoreCase);
    private static SyncState state;
    private static CancellationTokenSource stop;
    private static Control ui;
    internal static string Status => string.Join(Environment.NewLine, peerStatus.OrderBy(p => p.Key).Select(p => p.Key + "：" + p.Value));
    private static string StatePath => Path.Combine(SettingsUtils.DataDirectory, "sync.json");
    private static void Save()
    {
        Directory.CreateDirectory(SettingsUtils.DataDirectory);
        string temp = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(state, SettingsUtils.JsonOptions)); File.Move(temp, StatePath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void Report(string peer, string message)
    {
        if (peerStatus.TryGetValue(peer, out var previous) && previous == message) return;
        peerStatus[peer] = message; Logger.Log("Settings sync " + peer + ": " + message);
    }
    internal static void Start(Control control)
    {
        ui = control;
        try
        {
            state = File.Exists(StatePath) ? JsonSerializer.Deserialize<SyncState>(File.ReadAllText(StatePath)) : new();
            if (state == null || !Guid.TryParseExact(state.Identity, "N", out _) || state.Clock < 0 || state.Clock > 1000000000000000)
                throw new InvalidDataException("设置同步记录无效。");
            state.Revision?.Validate();
            stop = new();
            _ = Task.Run(() => Loop(stop.Token));
        }
        catch (Exception ex) { state = new() { Enabled = false }; Report("本机", ex.Message); }
    }
    internal static void Stop() => stop?.Cancel();
    internal static void OnCapabilities(DATA package, TcpSk tcp)
    {
        if (tcp?.Status == SocketStatus.Connected && package.Src == (ID)tcp.MachineId)
            capable[tcp.MachineName] = Environment.TickCount64;
    }
    private static Task<T> OnUi<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ui == null || ui.IsDisposed) { completion.SetException(new IOException("程序正在关闭。")); return completion.Task; }
        try { ui.BeginInvoke(new Action(async () => { try { completion.TrySetResult(await action()); } catch (Exception ex) { completion.TrySetException(ex); } })); }
        catch (Exception ex) { completion.TrySetException(ex); }
        return completion.Task;
    }
    private static SyncDocument Capture() => new()
    {
        Owner = Common.MachineName, Revision = state.Revision,
        Shared = SharedSettings.Capture(), Audio = state.IncludeAudio ? SharedAudio.Capture(AudioService.Instance.Options) : null,
    };
    private static string Fingerprint(SyncDocument document) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { document.Shared, document.Audio })));
    private static SyncDocument Observe(bool force = false)
    {
        var current = Capture(); string fingerprint = Fingerprint(current);
        if (state.Observe(fingerprint, force)) Save();
        current.Revision = state.Revision; return current;
    }
    internal static async Task<(bool Enabled, bool Audio)> Preferences()
    {
        await gate.WaitAsync(); try { return (state?.Enabled ?? false, state?.IncludeAudio ?? true); } finally { gate.Release(); }
    }
    internal static async Task Configure(bool enabled, bool audio, bool force)
    {
        await gate.WaitAsync();
        try
        {
            state.Enabled = enabled; state.IncludeAudio = audio;
            if (!audio) state.PendingAudio = null;
            await OnUi(() => Task.FromResult(Observe(force))); Save();
            Report("本机", enabled ? "已启用双向同步；连接后自动交换最新设置" : "已暂停设置同步");
        }
        finally { gate.Release(); }
    }
    private static string[] ConnectedPeers()
    {
        var sockets = Common.Sk;
        if (sockets == null) return Array.Empty<string>();
        lock (sockets.TcpSocketsLock) return sockets.TcpSockets.Where(t => t.Status == SocketStatus.Connected && t.BackingSocket != null)
            .Select(t => t.MachineName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static void RequirePeer(string peer, IPAddress address, string key)
    {
        if (!string.Equals(key, Encryption.MyKey, StringComparison.Ordinal)) throw new IOException("配对关系已变化。");
        var sockets = Common.Sk;
        if (sockets != null)
            lock (sockets.TcpSocketsLock)
                foreach (var tcp in sockets.TcpSockets)
                {
                    if (tcp.Status != SocketStatus.Connected || !string.Equals(tcp.MachineName, peer, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (tcp.BackingSocket?.RemoteEndPoint is IPEndPoint endpoint && endpoint.Address.MapToIPv6().Equals(address.MapToIPv6())) return;
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or SocketException) { }
                }
        throw new IOException("设置来源不属于当前已验证连接。");
    }
    private static async Task Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Common.SkSend(new DATA { Type = PackageType.SettingsCapability, Src = Common.MachineID, Des = ID.ALL }, (uint)Common.MachineID, false);
                SyncDocument document = null;
                await gate.WaitAsync(token);
                try { if (state.Enabled) document = await OnUi(() => Task.FromResult(Observe())); }
                finally { gate.Release(); }
                if (document != null)
                    foreach (string peer in ConnectedPeers())
                    {
                        if (!capable.TryGetValue(peer, out long seen) || Environment.TickCount64 - seen > 20000)
                        { Report(peer, "等待对面更新到支持设置同步的版本（.16 或更新）"); continue; }
                        try { var reply = await Send(peer, document, token); Report(peer, reply.Message); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                        catch (Exception ex) { Report(peer, "稍后重试：" + ex.Message); }
                    }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { Report("本机", ex.Message); }
            try { await Task.Delay(3000, token); } catch (OperationCanceledException) { break; }
        }
    }
    private static async Task<SyncReply> Send(string peer, SyncDocument document, CancellationToken token)
    {
        var address = AudioService.ConnectedAddress(peer) ?? throw new IOException("对方已断开。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(8000);
        using var client = await AudioService.ConnectPeerAsync(address, Common.Sk.TcpPort, deadline.Token);
        using var cancel = deadline.Token.Register(() => client.Dispose());
        return await Task.Run(() =>
        {
            string authenticated = peer; bool push = true; var action = ClipboardPostAction.SettingsSync;
            string key = Encryption.MyKey;
            if (!Core.Clipboard.ShakeHand(ref authenticated, client.Client, out var encrypted, out var decrypted, ref push, ref action)
                || !string.Equals(peer, authenticated, StringComparison.OrdinalIgnoreCase)) throw new IOException("设置连接验证失败。");
            client.SendTimeout = client.ReceiveTimeout = 8000;
            string nonce = SyncWire.Read<string>(decrypted);
            if (nonce == null || Convert.FromBase64String(nonce).Length != 32) throw new InvalidDataException("同步挑战无效。");
            SyncWire.Write(encrypted, SyncWire.Seal(document, key, "request", Common.MachineName, peer, nonce));
            return SyncWire.Open<SyncReply>(SyncWire.Read<SyncEnvelope>(decrypted), key, "reply", peer, Common.MachineName, nonce);
        }, deadline.Token);
    }
    internal static void Receive(string peer, Socket socket, Stream encrypted, Stream decrypted)
    {
        using (socket)
        {
            try
            {
                var address = ((IPEndPoint)socket.RemoteEndPoint).Address;
                string key = Encryption.MyKey;
                RequirePeer(peer, address, key);
                socket.SendTimeout = socket.ReceiveTimeout = 8000;
                string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); SyncWire.Write(encrypted, nonce);
                var document = SyncWire.Open<SyncDocument>(SyncWire.Read<SyncEnvelope>(decrypted), key, "request", peer, Common.MachineName, nonce);
                document.Validate(peer);
                var reply = Apply(document, () => RequirePeer(peer, address, key)).GetAwaiter().GetResult();
                SyncWire.Write(encrypted, SyncWire.Seal(reply, key, "reply", Common.MachineName, peer, nonce));
                Report(peer, reply.Message);
            }
            catch (Exception ex) { Report(peer, "接收未完成：" + ex.Message); }
        }
    }
    private static async Task<SyncReply> Apply(SyncDocument document, Action verifyPeer)
    {
        await gate.WaitAsync();
        try
        {
            if (!state.Enabled) return new(false, "对面已暂停设置同步");
            return await OnUi<SyncReply>(async () =>
            {
                verifyPeer();
                Observe(); // Preserve a local edit that has not yet reached the periodic publisher.
                int order = document.Revision.CompareTo(state.Revision);
                state.Clock = Math.Max(state.Clock, document.Revision.Counter);
                if (order < 0) return new(false, "本机有更新的设置，正在同步回对面");
                if (order == 0 && state.PendingAudio == null) return new(true, "设置已同步");
                if (order > 0)
                {
                    document.Shared.Apply(document.Owner);
                    state.Revision = document.Revision;
                    state.PendingAudio = state.IncludeAudio && document.Audio != null ? document : null;
                }
                string result = "设置已同步";
                if (state.PendingAudio != null)
                {
                    try
                    {
                        if (AudioQuickSetup.LoadState()?.Pending == true) throw new IOException("请先完成本机待进行的音频驱动配置");
                        var plan = state.PendingAudio.Audio.Mirror(state.PendingAudio.Owner, Common.MachineName, AudioService.Instance.Options,
                            () => AudioQuickSetup.FindCable()?.RenderId);
                        plan.Validate(Common.MachineName, Setting.Values.TcpPort);
                        if (JsonSerializer.Serialize(plan) != JsonSerializer.Serialize(AudioService.Instance.Options))
                        {
                            foreach (AudioKind kind in Enum.GetValues<AudioKind>())
                            {
                                var send = plan.Send(kind); var receive = plan.Receive(kind);
                                if (send.Enabled) { using var input = AudioDevices.Open(send.DeviceId, kind == AudioKind.System ? DataFlow.Render : DataFlow.Capture); }
                                if (receive.Enabled) { using var output = AudioDevices.Open(receive.DeviceId, DataFlow.Render); AudioDevices.CheckFeedback(plan, output.ID); }
                            }
                            await AudioService.Instance.ApplyAsync(plan);
                        }
                        state.PendingAudio = null;
                    }
                    catch (Exception ex) { result = "通用设置已同步；音频待处理：" + ex.Message; }
                }
                state.Adopt(state.Revision, Fingerprint(Capture())); Save();
                return new(true, result);
            });
        }
        finally { gate.Release(); }
    }
}
