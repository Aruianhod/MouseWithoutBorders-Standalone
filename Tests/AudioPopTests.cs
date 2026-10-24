using System.Buffers.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Audio;

namespace MouseWithoutBorders.UnitTests;

[TestClass]
public sealed class AudioPopTests
{
    private static byte[] Pcm(int channels, int frames, float value)
    {
        var bytes = new byte[frames * channels * 4];
        for (int i = 0; i < bytes.Length; i += 4)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i), value);
        return bytes;
    }

    private static float Sample(byte[] bytes, int frame, int channels, int channel = 0) =>
        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan((frame * channels + channel) * 4));

    [TestMethod]
    [DataRow(false, 1)] [DataRow(false, 2)]
    [DataRow(true, 1)] [DataRow(true, 2)]
    public void EmptyReadAndResumeDoNotJumpBetweenAudioAndZero(bool low, int channels)
    {
        var buffer = AudioLatency.CreateBuffer(channels, low, 160);
        var block = Pcm(channels, 480, .8f);
        var output = new byte[block.Length];
        buffer.Add(Pcm(channels, 4800, .8f));
        for (int i = 0; i < 10; i++) buffer.Read(output, 0, output.Length);
        float previous = Sample(output, 479, channels);
        Assert.AreEqual(.8f, previous, .00001f);
        buffer.Read(output, 0, output.Length);
        Assert.IsTrue(Math.Abs(Sample(output, 0, channels) - previous) < .01f,
            "An entirely empty read must decay from the last output, not jump to zero.");
        Assert.AreEqual(0f, Sample(output, 479, channels));
        buffer.Add(Pcm(channels, 4800, -.8f));
        buffer.Read(output, 0, output.Length);
        Assert.IsTrue(Math.Abs(Sample(output, 0, channels)) < .01f,
            "Resume must not jump straight from silence to a nonzero sample.");
        Assert.AreEqual(-.8f, Sample(output, 479, channels), .00001f);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public void OverflowSpliceStartsAtLastOutputInsteadOfFadingFromZero(bool low)
    {
        var buffer = AudioLatency.CreateBuffer(2, low, 160);
        var output = new byte[480 * 2 * 4];
        buffer.Add(Pcm(2, 4800, .8f));
        buffer.Read(output, 0, output.Length);
        Assert.AreEqual(.8f, Sample(output, 479, 2));
        buffer.Add(Pcm(2, 9600, -.8f));
        buffer.Read(output, 0, output.Length);
        Assert.IsTrue(Math.Abs(Sample(output, 0, 2) - .8f) < .01f,
            "Dropping backlog must not create a full-scale edge at the next read.");
        Assert.AreEqual(-.8f, Sample(output, 479, 2), .00001f);
        for (int i = 0; i < 480; i++)
        {
            Assert.IsTrue(Math.Abs(Sample(output, i, 2)) <= .80001f);
            Assert.AreEqual(Sample(output, i, 2), Sample(output, i, 2, 1));
        }
    }

    [TestMethod]
    public void ShortReadsRetainFiveMillisecondEnvelopeAcrossCalls()
    {
        var buffer = new LiveAudioBuffer(1, 160, 160);
        var output = new byte[4];
        buffer.Add(Pcm(1, 480, .8f));
        for (int i = 0; i < 480; i++) buffer.Read(output, 0, 4);
        float previous = .8f;
        for (int i = 0; i < 300; i++)
        {
            buffer.Read(output, 0, 4);
            float sample = Sample(output, 0, 1);
            Assert.IsTrue(Math.Abs(sample - previous) < .01f, "Fade length must not collapse to one read.");
            previous = sample;
        }
        Assert.AreEqual(0f, previous);
        buffer.Add(Pcm(1, 480, -.8f));
        for (int i = 0; i < 300; i++)
        {
            buffer.Read(output, 0, 4);
            float sample = Sample(output, 0, 1);
            Assert.IsTrue(Math.Abs(sample - previous) < .01f);
            previous = sample;
        }
        Assert.AreEqual(-.8f, previous, .00001f);
    }

    [TestMethod]
    public async Task SenderQueueDropDoesNotHardSpliceOppositeSamples()
    {
        var queue = new AudioFrameQueue(2, true, () => 1000);
        var first = Pcm(2, 480, .8f);
        queue.Add(first, first.Length, 100);
        await queue.ReadAsync(CancellationToken.None);
        for (int i = 0; i < 20; i++)
        {
            var next = Pcm(2, 480, -.8f);
            queue.Add(next, next.Length, 100);
        }
        var frame = await queue.ReadAsync(CancellationToken.None);
        Assert.IsTrue(Math.Abs(Sample(frame, 0, 2) - .8f) < .01f);
        Assert.AreEqual(-.8f, Sample(frame, 479, 2), .00001f);
        Assert.AreEqual(8L, queue.DroppedFrames);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public void PartialUnderrunFadesOnlyPaddingAndPreservesStereoChannels(bool low)
    {
        var buffer = AudioLatency.CreateBuffer(2, low, 160);
        var input = Pcm(2, 4800, .7f);
        for (int frame = 0; frame < 4800; frame++)
            BinaryPrimitives.WriteSingleLittleEndian(input.AsSpan(frame * 8 + 4), -.4f);
        buffer.Add(input);
        var output = new byte[480 * 8];
        for (int i = 0; i < 9; i++) buffer.Read(output, 0, output.Length);
        var large = Enumerable.Repeat((byte)0x55, 960 * 8 + 16).ToArray();
        buffer.Read(large, 8, 960 * 8);
        for (int frame = 0; frame < 480; frame++)
        {
            Assert.AreEqual(.7f, Sample(large, frame + 1, 2));
            Assert.AreEqual(-.4f, Sample(large, frame + 1, 2, 1));
        }
        Assert.IsTrue(Math.Abs(Sample(large, 481, 2) - .7f) < .01f);
        Assert.AreEqual(0f, Sample(large, 960, 2));
        Assert.IsTrue(large.Take(8).Concat(large.TakeLast(8)).All(b => b == 0x55));
    }

    [TestMethod]
    public void ZeroLengthReadDoesNotChangeEnvelopeAndInvalidReadsDoNotConsumeAudio()
    {
        var buffer = new LiveAudioBuffer(1, 160, 160);
        buffer.Add(Pcm(1, 480, .8f));
        var output = new byte[480 * 4];
        Assert.AreEqual(0, buffer.Read(output, 0, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => buffer.Read(output, 1, output.Length));
        Assert.ThrowsException<ArgumentException>(() => buffer.Read(output, 0, 3));
        Assert.AreEqual(output.Length, buffer.BufferedBytes);
        buffer.Read(output, 0, output.Length);
        Assert.AreEqual(0f, Sample(output, 0, 1));
        Assert.AreEqual(.8f, Sample(output, 479, 1));
    }
}
