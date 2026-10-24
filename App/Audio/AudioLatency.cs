using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MouseWithoutBorders.Audio;

internal static class AudioLatency
{
    internal static WasapiCapture CreateCapture(MMDevice device, AudioKind kind, bool lowLatency) =>
        kind == AudioKind.System ? new LoopbackCapture(device, lowLatency) : new WasapiCapture(device, true, 60);

    // Event-driven loopback is supported on Windows 10 1703+, below this app's minimum.
    // The 60 ms capture capacity tolerates scheduling stalls without polling every 30 ms.
    // https://learn.microsoft.com/windows/win32/coreaudio/loopback-recording
    private sealed class LoopbackCapture : WasapiCapture
    {
        internal LoopbackCapture(MMDevice device, bool lowLatency) : base(device, lowLatency, lowLatency ? 60 : 100) { }
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }

    internal static int PlaybackMilliseconds(bool lowLatency, int limit) => lowLatency ? 40 : Math.Min(limit, 100);
    internal static int QueueMilliseconds(bool lowLatency, int limit) => lowLatency ? 160 : limit;
    internal static LiveAudioBuffer CreateBuffer(int channels, bool lowLatency, int limit)
    {
        if (!lowLatency) return new(channels, limit, limit, Math.Min(80, limit), Math.Min(200, limit));
        return new(channels, QueueMilliseconds(true, limit), 60, 40, 100);
    }
}

// Timestamped bounded queue: a network stall must not replay old speech on recovery.
internal sealed class AudioFrameQueue
{
    private readonly Channel<(byte[] Bytes, long Captured, long Sequence)> queue;
    private readonly AudioEdgeSmoother edges;
    private long sequence;
    private long lastSequence;
    private long droppedFrames;
    internal long DroppedFrames => Interlocked.Read(ref droppedFrames);
    private readonly Func<long> clock;
    private readonly int channels;
    private readonly int frameMilliseconds;
    private readonly int maxAge;
    internal AudioFrameQueue(int channels, bool lowLatency, Func<long> clock = null)
    {
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels));
        this.channels = channels;
        edges = new AudioEdgeSmoother(channels, initiallySilent: false);
        this.clock = clock ?? (() => Environment.TickCount64);
        frameMilliseconds = lowLatency ? 10 : 20;
        maxAge = lowLatency ? 160 : 250;
        queue = Channel.CreateBounded<(byte[], long, long)>(new BoundedChannelOptions(lowLatency ? 12 : 10)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest,
        });
    }

    internal void Add(byte[] bytes, int count, int volume)
    {
        int align = channels * 4;
        if (count < 0 || count > bytes.Length || count % align != 0) throw new ArgumentException("音频采集帧未对齐。");
        int frameSize = 48 * frameMilliseconds * align;
        long now = clock();
        for (int offset = 0; offset < count; offset += frameSize)
        {
            int size = Math.Min(frameSize, count - offset);
            long id = ++sequence;
            long captured = now - (count - offset - size) / (48 * align);
            if (now - captured > maxAge) continue;
            var frame = new byte[size];
            Buffer.BlockCopy(bytes, offset, frame, 0, size);
            AudioWire.ScaleSamples(frame, channels, volume);
            queue.Writer.TryWrite((frame, captured, id));
        }
    }

    internal async ValueTask<byte[]> ReadAsync(CancellationToken token)
    {
        while (true)
        {
            var frame = await queue.Reader.ReadAsync(token).ConfigureAwait(false);
            if (clock() - frame.Captured > maxAge) continue;
            long skipped = frame.Sequence - lastSequence - 1;
            if (skipped > 0) Interlocked.Add(ref droppedFrames, skipped);
            edges.Process(frame.Bytes, 0, frame.Bytes.Length, frame.Bytes.Length, skipped > 0 && lastSequence > 0);
            lastSequence = frame.Sequence;
            return frame.Bytes;
        }
    }
    internal void Complete(Exception error = null) => queue.Writer.TryComplete(error);
}

// One lock covers trim/add/read: the playback thread cannot race the catch-up operation.
internal sealed class LiveAudioBuffer : IWaveProvider
{
    private readonly object gate = new();
    private readonly BufferedWaveProvider buffer;
    private readonly byte[] discarded;
    private readonly int maximumBytes;
    private readonly int catchUpBytes;
    private long droppedBytes;
    private long underruns;
    private int targetBytes;
    private readonly int maximumTargetBytes;
    private bool priming;
    private bool splice;
    private readonly AudioEdgeSmoother edges;
    private readonly bool adaptive;
    internal int TargetMilliseconds { get { lock (gate) return targetBytes * 1000 / WaveFormat.AverageBytesPerSecond; } }
    internal long Underruns { get { lock (gate) return underruns; } }
    public WaveFormat WaveFormat => buffer.WaveFormat;
    internal int BufferedBytes { get { lock (gate) return buffer.BufferedBytes; } }
    internal long DroppedBytes { get { lock (gate) return droppedBytes; } }

    internal LiveAudioBuffer(int channels, int maximumMilliseconds, int catchUpMilliseconds, int initialMilliseconds = 0, int maximumTargetMilliseconds = 0)
    {
        if (channels is not (1 or 2) || maximumMilliseconds < 20 || maximumMilliseconds > 500
            || catchUpMilliseconds < 10 || catchUpMilliseconds > maximumMilliseconds) throw new ArgumentOutOfRangeException();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
        if (initialMilliseconds < 0 || maximumTargetMilliseconds < initialMilliseconds || maximumTargetMilliseconds > maximumMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(initialMilliseconds));
        adaptive = initialMilliseconds > 0;
        priming = adaptive;
        edges = new AudioEdgeSmoother(channels);
        targetBytes = format.AverageBytesPerSecond * initialMilliseconds / 1000;
        maximumTargetBytes = format.AverageBytesPerSecond * maximumTargetMilliseconds / 1000;
        maximumBytes = format.AverageBytesPerSecond * maximumMilliseconds / 1000;
        catchUpBytes = format.AverageBytesPerSecond * catchUpMilliseconds / 1000;
        buffer = new BufferedWaveProvider(format) { BufferLength = maximumBytes, ReadFully = true };
        discarded = new byte[maximumBytes];
    }

    internal void Add(byte[] frame)
    {
        if (frame.Length % WaveFormat.BlockAlign != 0) throw new ArgumentException("音频接收帧未对齐。");
        lock (gate)
        {
            int offset = 0;
            if ((long)buffer.BufferedBytes + frame.Length > maximumBytes)
            {
                int keep = adaptive ? Math.Max(catchUpBytes, targetBytes) : catchUpBytes;
                int toDrop = buffer.BufferedBytes + frame.Length - keep;
                splice = true;
                int old = Math.Min(buffer.BufferedBytes, toDrop);
                if (old > 0) buffer.Read(discarded, 0, old);
                offset = toDrop - old;
                droppedBytes += toDrop;
            }
            if (frame.Length > offset) buffer.AddSamples(frame, offset, frame.Length - offset);
        }
    }

    public int Read(byte[] destination, int offset, int count)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (offset < 0 || count < 0 || offset > destination.Length - count) throw new ArgumentOutOfRangeException();
        if (count % WaveFormat.BlockAlign != 0) throw new ArgumentException("播放读取未对齐。");
        if (count == 0) return 0;
        lock (gate)
        {
            if (adaptive && count > 0)
                targetBytes = Math.Min(maximumTargetBytes, Math.Max(targetBytes, count + WaveFormat.AverageBytesPerSecond * 20 / 1000));
            if (adaptive && priming)
            {
                if (buffer.BufferedBytes < Math.Max(targetBytes, count))
                { edges.Process(destination, offset, 0, count); return count; }
                priming = false;
            }
            int available = Math.Min(count, buffer.BufferedBytes);
            int result = buffer.Read(destination, offset, count);
            edges.Process(destination, offset, available, count, splice);
            if (available > 0) splice = false;
            if (adaptive && available < count)
            {
                underruns++;
                targetBytes = Math.Min(maximumTargetBytes, targetBytes + WaveFormat.AverageBytesPerSecond * 20 / 1000);
                priming = true;
            }
            return result;
        }
    }

}
