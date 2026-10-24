using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace MouseWithoutBorders.Core;

// The input hook only enqueues immutable packets. One worker owns each encrypted stream.
// Merge only consecutive absolute moves; keys, buttons, relative moves and clipboard framing keep their order.
internal sealed class OrderedPacketWriter : IDisposable
{
    private sealed record Packet(byte[] Bytes, bool AbsoluteMove, ID Source, ID Destination);
    private readonly object gate = new();
    private readonly LinkedList<Packet> pending = new();
    private readonly Action<byte[]> write;
    private readonly Action<Exception> failed;
    private readonly int limit;
    private int bytes;
    private bool draining, closed;
    internal OrderedPacketWriter(Action<byte[]> write, Action<Exception> failed, int limit = 2 * 1024 * 1024)
    { this.write = write; this.failed = failed; this.limit = limit; }
    internal int PendingBytes { get { lock (gate) return bytes; } }

    internal void Enqueue(DATA data, byte[] serialized)
    {
        bool absoluteMove = data.Type == PackageType.Mouse && data.Md.dwFlags == WM.WM_MOUSEMOVE
            && data.Md.X >= 0 && data.Md.X <= 65535 && data.Md.Y >= 0 && data.Md.Y <= 65535;
        Exception error = null;
        lock (gate)
        {
            if (closed) throw new IOException("输入发送连接已关闭。");
            var packet = new Packet(serialized, absoluteMove, data.Src, data.Des);
            var last = pending.Last;
            if (absoluteMove && last?.Value.AbsoluteMove == true && last.Value.Source == data.Src && last.Value.Destination == data.Des)
            {
                bytes -= last.Value.Bytes.Length; last.Value = packet; bytes += serialized.Length;
            }
            else if (bytes + serialized.Length > limit)
            {
                closed = true; pending.Clear(); bytes = 0;
                error = new IOException("输入发送队列持续堵塞，正在重建连接。");
            }
            else { pending.AddLast(packet); bytes += serialized.Length; }
            if (error == null && !draining) { draining = true; _ = Task.Run(Drain); }
        }
        if (error != null) { failed(error); throw error; }
    }

    private void Drain()
    {
        try
        {
            while (true)
            {
                Packet packet;
                lock (gate)
                {
                    if (closed || pending.Count == 0) { draining = false; return; }
                    packet = pending.First.Value; pending.RemoveFirst(); bytes -= packet.Bytes.Length;
                }
                write(packet.Bytes);
            }
        }
        catch (Exception ex)
        {
            bool report;
            lock (gate) { report = !closed; closed = true; draining = false; pending.Clear(); bytes = 0; }
            if (report) failed(ex);
        }
    }
    public void Dispose() { lock (gate) { closed = true; pending.Clear(); bytes = 0; } }
}
