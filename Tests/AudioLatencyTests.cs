using System.Buffers.Binary;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Audio;

namespace MouseWithoutBorders.UnitTests;

[TestClass]
public sealed class AudioLatencyTests
{
    private static byte[] Pcm(int channels, int milliseconds, float value)
    {
        var bytes = new byte[48 * channels * 4 * milliseconds];
        for (int i = 0; i < bytes.Length; i += 4) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i), value);
        return bytes;
    }

    [TestMethod]
    public void StableModeRetainsExistingPlaybackAndBufferBehavior()
    {
        var buffer = AudioLatency.CreateBuffer(2, false, 160);
        var input = Pcm(2, 10, .4f); var output = new byte[input.Length];
        buffer.Add(input); buffer.Read(output, 0, output.Length);
        CollectionAssert.AreEqual(input, output);
        Assert.AreEqual(0, buffer.TargetMilliseconds);
        Assert.AreEqual(100, AudioLatency.PlaybackMilliseconds(false, 160));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void AdaptiveBufferAbsorbsBunchedWifiFramesAndReprimesAfterAnUnderrun(int channels)
    {
        var buffer = AudioLatency.CreateBuffer(channels, true, 160);
        var frame = Pcm(channels, 10, .4f); var output = new byte[frame.Length];
        buffer.Add(frame); buffer.Read(output, 0, output.Length);
        Assert.IsTrue(output.All(b => b == 0)); Assert.AreEqual(frame.Length, buffer.BufferedBytes);
        for (int i = 0; i < 3; i++) buffer.Add(frame);
        buffer.Read(output, 0, output.Length);
        Assert.IsTrue(output.Any(b => b != 0));
        // Three packets arrive together every 30 ms: steady audio after initial priming.
        for (int cycle = 0; cycle < 100; cycle++)
        {
            for (int i = 0; i < 3; i++) buffer.Read(output, 0, output.Length);
            for (int i = 0; i < 3; i++) buffer.Add(frame);
        }
        Assert.AreEqual(0L, buffer.Underruns); Assert.AreEqual(0L, buffer.DroppedBytes);
        for (int i = 0; i < 20; i++) buffer.Read(output, 0, output.Length);
        Assert.AreEqual(1L, buffer.Underruns); Assert.AreEqual(60, buffer.TargetMilliseconds);
        for (int i = 0; i < 5; i++) { buffer.Add(frame); buffer.Read(output, 0, output.Length); Assert.IsTrue(output.All(b => b == 0)); }
        buffer.Add(frame); buffer.Read(output, 0, output.Length); Assert.IsTrue(output.Any(b => b != 0));
        for (int cycle = 0; cycle < 10; cycle++)
        {
            for (int i = 0; i < 50; i++) buffer.Read(output, 0, output.Length);
            buffer.Add(Pcm(channels, 150, .5f));
            buffer.Read(output, 0, output.Length);
        }
        Assert.AreEqual(100, buffer.TargetMilliseconds);
        Assert.IsTrue(buffer.BufferedBytes <= 48 * channels * 4 * 160);
    }

    [TestMethod]
    public void PrebufferWaitDoesNotCountAsUnderrunsAndOverflowRetainsAdaptiveCushion()
    {
        var buffer = AudioLatency.CreateBuffer(2, true, 160); var output = new byte[48 * 2 * 4 * 20];
        for (int i = 0; i < 100; i++) buffer.Read(output, 0, output.Length);
        Assert.AreEqual(0L, buffer.Underruns);
        buffer.Add(Pcm(2, 300, .7f));
        Assert.AreEqual(48 * 2 * 4 * 60, buffer.BufferedBytes);
        buffer.Read(output, 0, output.Length);
        Assert.AreEqual(0L, buffer.Underruns);
        Assert.AreEqual(40, AudioLatency.PlaybackMilliseconds(true, 160));
    }

    [TestMethod]
    public void OldSettingsEnableLowLatencyAndQuickSetupPreservesExplicitOptOut()
    {
        var options = JsonSerializer.Deserialize<AudioOptions>("{\"BufferMilliseconds\":240}")!;
        Assert.IsTrue(options.LowLatency);
        options.LowLatency = false;
        var plan = AudioQuickSetup.BuildPlan(QuickAudioRole.HeadsetComputer, "REMOTE", "speaker", "mic", null, options);
        Assert.IsFalse(plan.Clone().LowLatency);
        Assert.AreEqual(240, plan.BufferMilliseconds);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task CaptureBurstKeepsNewestSamplesAndVolume(int channels)
    {
        long now = 1000;
        var queue = new AudioFrameQueue(channels, true, () => now);
        var input = Enumerable.Range(1, 10).SelectMany(i => Pcm(channels, 10, i / 10f)).ToArray();
        queue.Add(input, input.Length, 50);
        queue.Complete();
        for (int i = 1; i <= 10; i++)
        {
            var frame = await queue.ReadAsync(CancellationToken.None);
            Assert.AreEqual(48 * channels * 4 * 10, frame.Length);
            Assert.AreEqual(i / 20f, BinaryPrimitives.ReadSingleLittleEndian(frame), .00001f);
        }
        await Assert.ThrowsExceptionAsync<ChannelClosedException>(async () => await queue.ReadAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task NetworkStallDiscardsExpiredSpeechAndWaitsForFreshAudio()
    {
        long now = 1000;
        var queue = new AudioFrameQueue(1, true, () => now);
        var old = Pcm(1, 10, .1f);
        queue.Add(old, old.Length, 100);
        now += 250;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var pending = queue.ReadAsync(timeout.Token).AsTask();
        Assert.IsFalse(pending.IsCompleted);
        var fresh = Pcm(1, 10, .7f);
        queue.Add(fresh, fresh.Length, 100);
        CollectionAssert.AreEqual(fresh, await pending);
    }

    [TestMethod]
    public async Task EmptyCaptureQueueHonorsCancellationAndRecordingFailure()
    {
        var queue = new AudioFrameQueue(2, true);
        using var cancel = new CancellationTokenSource();
        var pending = queue.ReadAsync(cancel.Token).AsTask();
        cancel.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await pending);
        queue.Complete(new IOException("capture stopped"));
        var error = await Assert.ThrowsExceptionAsync<ChannelClosedException>(async () => await queue.ReadAsync(CancellationToken.None));
        Assert.IsInstanceOfType<IOException>(error.InnerException);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void SteadyAudioIsBitExactWithoutDropsOrAddedPrebuffer(int channels)
    {
        var buffer = new LiveAudioBuffer(channels, 60, 20);
        var frame = Pcm(channels, 10, .4f);
        var read = new byte[frame.Length];
        for (int i = 0; i < 1000; i++)
        {
            buffer.Add(frame);
            Assert.AreEqual(read.Length, buffer.Read(read, 0, read.Length));
            CollectionAssert.AreEqual(frame, read);
        }
        Assert.AreEqual(0L, buffer.DroppedBytes);
        buffer.Read(read, 0, read.Length);
        Assert.IsTrue(read.All(b => b == 0), "Underruns must play silence, not repeat old samples.");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void BacklogTrimsOldestAndKeepsTheNewestTwentyMilliseconds(int channels)
    {
        var buffer = new LiveAudioBuffer(channels, 60, 20);
        for (int i = 1; i <= 7; i++) buffer.Add(Pcm(channels, 10, i / 10f));
        Assert.AreEqual(48 * channels * 4 * 20, buffer.BufferedBytes);
        Assert.AreEqual((long)48 * channels * 4 * 50, buffer.DroppedBytes);
        var latest = new byte[buffer.BufferedBytes];
        buffer.Read(latest, 0, latest.Length);
        CollectionAssert.AreEqual(Pcm(channels, 10, .6f).Concat(Pcm(channels, 10, .7f)).ToArray(), latest);
        buffer.Add(Pcm(channels, 200, .8f));
        Assert.AreEqual(48 * channels * 4 * 20, buffer.BufferedBytes);
    }

    [TestMethod]
    public async Task PlaybackAndNetworkThreadsCanTrimReadAndWriteConcurrently()
    {
        var buffer = new LiveAudioBuffer(2, 60, 20);
        var frame = Pcm(2, 10, .2f);
        await Task.WhenAll(Task.Run(() => { for (int i = 0; i < 10000; i++) buffer.Add(frame); }),
            Task.Run(() => { var read = new byte[frame.Length]; for (int i = 0; i < 10000; i++) buffer.Read(read, 0, read.Length); }));
        Assert.IsTrue(buffer.BufferedBytes <= 48 * 2 * 4 * 60);
    }

    [TestMethod]
    public async Task PacketHeaderAndPcmRemainCompatibleWhenCombinedIntoOneWrite()
    {
        using var stream = new MemoryStream();
        var frame = Pcm(2, 10, .3f);
        await AudioWire.WritePacket(stream, frame, CancellationToken.None);
        await AudioWire.WritePacket(stream, Array.Empty<byte>(), CancellationToken.None);
        stream.Position = 0;
        CollectionAssert.AreEqual(frame, await AudioWire.ReadPacket(stream, AudioWire.MaxFrame, CancellationToken.None));
        Assert.AreEqual(0, (await AudioWire.ReadPacket(stream, AudioWire.MaxFrame, CancellationToken.None)).Length);
        Assert.AreEqual(stream.Length, stream.Position);
    }
}
