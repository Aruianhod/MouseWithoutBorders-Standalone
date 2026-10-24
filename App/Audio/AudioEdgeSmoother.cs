using System;
using System.Buffers.Binary;

namespace MouseWithoutBorders.Audio;

// Only repair known stream discontinuities; uninterrupted PCM is not filtered.
// State is per stream and belongs to its reader (or LiveAudioBuffer's gate).
internal sealed class AudioEdgeSmoother
{
    private const int FadeFrames = 240; // 5 ms at the wire's fixed 48 kHz.
    private readonly int channels;
    private readonly float[] last;
    private readonly float[] anchor;
    private bool silent;
    private int blendPosition = FadeFrames;
    private int silencePosition = FadeFrames;

    internal AudioEdgeSmoother(int channels, bool initiallySilent = true)
    {
        this.channels = channels;
        last = new float[channels]; anchor = new float[channels];
        silent = initiallySilent;
    }

    internal void Process(byte[] data, int offset, int available, int count, bool splice = false)
    {
        int align = channels * 4;
        if (available > 0)
        {
            if (silent || splice)
            {
                Array.Copy(last, anchor, channels);
                blendPosition = 0;
            }
            silent = false;
            int blendBytes = Math.Min(available, (FadeFrames - blendPosition) * align);
            for (int position = 0; position < blendBytes; position += align)
            {
                float mix = blendPosition < FadeFrames ? blendPosition / (FadeFrames - 1f) : 1f;
                for (int channel = 0; channel < channels; channel++)
                {
                    var sample = data.AsSpan(offset + position + channel * 4, 4);
                    float value = BinaryPrimitives.ReadSingleLittleEndian(sample);
                    if (blendPosition < FadeFrames)
                    {
                        value = anchor[channel] * (1 - mix) + value * mix;
                        BinaryPrimitives.WriteSingleLittleEndian(sample, value);
                    }
                    last[channel] = value;
                }
                if (blendPosition < FadeFrames) blendPosition++;
            }
            for (int channel = 0; channel < channels; channel++)
                last[channel] = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + available - align + channel * 4, 4));
        }
        if (available == count) return;
        if (!silent)
        {
            Array.Copy(last, anchor, channels);
            silencePosition = 0;
            silent = true;
        }
        // An empty read cannot retrospectively fade the preceding audio block.
        // Decay its final sample here, including across very small output reads.
        for (int position = available; position < count; position += align)
        {
            float mix = silencePosition < FadeFrames ? 1f - (silencePosition + 1f) / FadeFrames : 0f;
            for (int channel = 0; channel < channels; channel++)
            {
                last[channel] = anchor[channel] * mix;
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + position + channel * 4, 4), last[channel]);
            }
            if (silencePosition < FadeFrames) silencePosition++;
        }
    }
}
