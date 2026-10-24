using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MouseWithoutBorders.Audio;

internal static class AudioLatency
{
    internal static WasapiCapture CreateCapture(MMDevice device, AudioKind kind, bool lowLatency) =>
        kind == AudioKind.System ? new LoopbackCapture(device, lowLatency ? 20 : 100) : new WasapiCapture(device, true, 20);

    // NAudio 2.2.1 WasapiLoopbackCapture does not expose its inherited 100 ms buffer.
    // Keep polling for loopback driver compatibility, but poll a 20 ms buffer (about every 10 ms).
    // https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Wasapi/WasapiLoopbackCapture.cs
    private sealed class LoopbackCapture : WasapiCapture
    {
        internal LoopbackCapture(MMDevice device, int milliseconds) : base(device, false, milliseconds) { }
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }

    internal static int PlaybackMilliseconds(bool lowLatency, int limit) => lowLatency ? 20 : Math.Min(limit, 100);
    internal static int QueueMilliseconds(bool lowLatency, int limit) => lowLatency ? Math.Min(limit, 60) : limit;
}

// Timestamped bounded queue: a network stall must not replay old speech on recovery.
internal sealed class AudioFrameQueue
{
    private readonly Channel<(byte[] Bytes, long Captured)> queue;
    private readonly Func<long> clock;
    private readonly int channels;
    private readonly int frameMilliseconds;
    private readonly int maxAge;
    internal AudioFrameQueue(int channels, bool lowLatency, Func<long> clock = null)
    {
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels));
        this.channels = channels;
        this.clock = clock ?? (() => Environment.TickCount64);
        frameMilliseconds = lowLatency ? 10 : 20;
        maxAge = lowLatency ? 60 : 250;
        queue = Channel.CreateBounded<(byte[], long)>(new BoundedChannelOptions(lowLatency ? 4 : 10)
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
            long captured = now - (count - offset - size) / (48 * align);
            if (now - captured > maxAge) continue;
            var frame = new byte[size];
            Buffer.BlockCopy(bytes, offset, frame, 0, size);
            AudioWire.ScaleSamples(frame, channels, volume);
            queue.Writer.TryWrite((frame, captured));
        }
    }

    internal async ValueTask<byte[]> ReadAsync(CancellationToken token)
    {
        while (true)
        {
            var frame = await queue.Reader.ReadAsync(token).ConfigureAwait(false);
            if (clock() - frame.Captured <= maxAge) return frame.Bytes;
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
    public WaveFormat WaveFormat => buffer.WaveFormat;
    internal int BufferedBytes { get { lock (gate) return buffer.BufferedBytes; } }
    internal long DroppedBytes { get { lock (gate) return droppedBytes; } }

    internal LiveAudioBuffer(int channels, int maximumMilliseconds, int catchUpMilliseconds)
    {
        if (channels is not (1 or 2) || maximumMilliseconds < 20 || maximumMilliseconds > 500
            || catchUpMilliseconds < 10 || catchUpMilliseconds > maximumMilliseconds) throw new ArgumentOutOfRangeException();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
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
                int toDrop = buffer.BufferedBytes + frame.Length - catchUpBytes;
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
        if (count % WaveFormat.BlockAlign != 0) throw new ArgumentException("播放读取未对齐。");
        lock (gate) return buffer.Read(destination, offset, count);
    }
}
