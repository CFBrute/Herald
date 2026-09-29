using System;
using System.IO;
using System.Numerics;
using NAudio.Wave;

namespace Herald.Services.Engines.Kokoro;

/// <summary>What happens to Kokoro's audio around the model: trimming, speeding up, saving.</summary>
public static class KokoroAudio
{
    public const int SampleRate = 24000;

    /// <summary>
    /// Leading and trailing silence cut off: librosa's trim (as bundled in kokoro-onnx) with
    /// its defaults - frames of 2048 samples every 512, and anything 60 dB below the loudest
    /// frame counts as silence.
    /// </summary>
    public static float[] Trim(float[] audio)
    {
        const int frameLength = 2048, hop = 512;
        const double minPower = 1e-10, topDb = 60;

        // Frames centred on every hop, the signal padded with zeros at both ends.
        var frames = 1 + audio.Length / hop;
        var power = new double[frames];
        var loudest = 0.0;
        for (var f = 0; f < frames; f++)
        {
            var sum = 0.0;
            var from = f * hop - frameLength / 2;
            for (var i = Math.Max(0, from); i < Math.Min(audio.Length, from + frameLength); i++) sum += (double)audio[i] * audio[i];
            power[f] = sum / frameLength;
            loudest = Math.Max(loudest, power[f]);
        }

        var reference = 10 * Math.Log10(Math.Max(minPower, loudest));
        int first = -1, last = -1;
        for (var f = 0; f < frames; f++)
        {
            if (10 * Math.Log10(Math.Max(minPower, power[f])) - reference <= -topDb) continue;
            if (first < 0) first = f;
            last = f;
        }
        if (first < 0) return [];

        var start = first * hop;
        var end = Math.Min(audio.Length, (last + 1) * hop);
        return audio[start..end];
    }

    /// <summary>
    /// Speech played <paramref name="rate"/> times faster without raising its pitch (WSOLA):
    /// overlapping slices are taken further apart than they're laid down, each nudged to
    /// where it lines up best with the previous one, so the joins don't crackle.
    /// </summary>
    public static float[] SpeedUp(float[] samples, double rate)
    {
        const int window = (int)(0.03 * SampleRate), hopOut = window / 2, tolerance = (int)(0.008 * SampleRate);
        var hopIn = hopOut * rate;

        var fade = new float[window];
        for (var i = 0; i < window; i++) fade[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (window - 1)));

        var outLength = (int)(samples.Length / rate);
        var source = new float[tolerance + samples.Length + window + tolerance + (int)hopIn + 1];
        samples.CopyTo(source, tolerance);
        var output = new float[outLength + window];
        var weight = new float[outLength + window];

        int outPos = 0, previous = -1;
        var inPos = 0.0;
        while (outPos < outLength)
        {
            var start = (int)inPos + tolerance;
            if (previous >= 0) start += BestOffset(source, start - tolerance, previous + hopOut, window, 2 * tolerance) - tolerance;

            for (var i = 0; i < window; i++)
            {
                output[outPos + i] += source[start + i] * fade[i];
                weight[outPos + i] += fade[i];
            }
            previous = start;
            outPos += hopOut;
            inPos += hopIn;
        }

        var result = new float[outLength];
        for (var i = 0; i < outLength; i++) result[i] = output[i] / (weight[i] < 1e-3f ? 1 : weight[i]);
        return result;
    }

    /// <summary>
    /// Of the shifts 0..<paramref name="shifts"/>, the one where the slice at
    /// <paramref name="searchFrom"/> + shift best matches the slice at <paramref name="target"/>
    /// (numpy's correlate "valid" and argmax: the first highest).
    /// </summary>
    private static int BestOffset(float[] source, int searchFrom, int target, int window, int shifts)
    {
        var best = 0;
        var bestScore = Single.NegativeInfinity;
        var wanted = source.AsSpan(target, window);
        for (var shift = 0; shift <= shifts; shift++)
        {
            var score = Dot(source.AsSpan(searchFrom + shift, window), wanted);
            if (score > bestScore)
            {
                bestScore = score;
                best = shift;
            }
        }
        return best;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count) sum += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        var total = Vector.Sum(sum);
        for (; i < a.Length; i++) total += a[i] * b[i];
        return total;
    }

    /// <summary>Saved as 16-bit PCM, as Python's soundfile did.</summary>
    public static void WriteWav(string path, float[] samples)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 1));
        var buffer = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * Int16.MaxValue, MidpointRounding.ToEven);
            buffer[2 * i] = (byte)value;
            buffer[2 * i + 1] = (byte)(value >> 8);
        }
        writer.Write(buffer, 0, buffer.Length);
    }
}
