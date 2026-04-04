namespace KhPs2Audio.Shared;

public static class WaveWriter
{
    public static void WriteMonoPcm16(string path, IReadOnlyList<float> samples, int sampleRate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var frameCount = samples.Count;
        const short channels = 1;
        const short bitsPerSample = 16;
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        var byteRate = sampleRate * blockAlign;
        var dataSize = frameCount * blockAlign;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);

        for (var i = 0; i < frameCount; i++)
        {
            writer.Write(FloatToPcm16(samples[i]));
        }
    }

    public static void WriteStereoPcm16(string path, IReadOnlyList<float> left, IReadOnlyList<float> right, int sampleRate)
    {
        if (left.Count != right.Count)
        {
            throw new ArgumentException("Left and right channel lengths must match.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var frameCount = left.Count;
        const short channels = 2;
        const short bitsPerSample = 16;
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        var byteRate = sampleRate * blockAlign;
        var dataSize = frameCount * blockAlign;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);

        for (var i = 0; i < frameCount; i++)
        {
            writer.Write(FloatToPcm16(left[i]));
            writer.Write(FloatToPcm16(right[i]));
        }
    }

    private static short FloatToPcm16(float value)
    {
        var clamped = Math.Clamp(value, -1f, 1f);
        return (short)Math.Round(clamped * short.MaxValue, MidpointRounding.AwayFromZero);
    }
}
