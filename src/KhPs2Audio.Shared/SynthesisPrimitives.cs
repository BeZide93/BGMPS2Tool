namespace KhPs2Audio.Shared;

internal enum LoopMeasure
{
    Samples,
    Bytes,
    PsxAdpcmBlocks,
}

internal enum LoopType
{
    Forward = 0,
}

internal sealed record LoopDescriptor(
    bool Looping,
    int Start,
    int Length,
    LoopMeasure StartMeasure,
    LoopMeasure LengthMeasure,
    LoopType Type = LoopType.Forward)
{
    public static LoopDescriptor None { get; } = new(false, 0, 0, LoopMeasure.Samples, LoopMeasure.Samples);

    public static LoopDescriptor FromSamples(bool looping, int loopStartSample, int sampleLength, LoopType type = LoopType.Forward)
    {
        if (!looping)
        {
            return None;
        }

        var safeStart = Math.Max(0, loopStartSample);
        var safeLength = Math.Max(0, sampleLength - safeStart);
        return new LoopDescriptor(true, safeStart, safeLength, LoopMeasure.Samples, LoopMeasure.Samples, type);
    }

    public static LoopDescriptor FromBytes(bool looping, int loopStartBytes, int loopLengthBytes, LoopType type = LoopType.Forward)
    {
        if (!looping)
        {
            return None;
        }

        return new LoopDescriptor(true, Math.Max(0, loopStartBytes), Math.Max(0, loopLengthBytes), LoopMeasure.Bytes, LoopMeasure.Bytes, type);
    }

    public static LoopDescriptor FromPsxAdpcmBytes(bool looping, int loopStartBytes, int loopLengthBytes, LoopType type = LoopType.Forward)
        => FromBytes(looping, loopStartBytes, loopLengthBytes, type) with
        {
            StartMeasure = LoopMeasure.Bytes,
            LengthMeasure = LoopMeasure.Bytes,
        };

    public int ResolveStartSamples(int sampleLength, int bytesPerSample = sizeof(short))
    {
        if (!Looping)
        {
            return 0;
        }

        var resolved = StartMeasure switch
        {
            LoopMeasure.Samples => Start,
            LoopMeasure.Bytes => bytesPerSample <= 0 ? 0 : Start / bytesPerSample,
            LoopMeasure.PsxAdpcmBlocks => Start * 28,
            _ => Start,
        };
        return Math.Clamp(resolved, 0, Math.Max(0, sampleLength - 1));
    }

    public int ResolveLengthSamples(int sampleLength, int bytesPerSample = sizeof(short))
    {
        if (!Looping)
        {
            return 0;
        }

        var resolvedLength = LengthMeasure switch
        {
            LoopMeasure.Samples => Length,
            LoopMeasure.Bytes => bytesPerSample <= 0 ? 0 : Length / bytesPerSample,
            LoopMeasure.PsxAdpcmBlocks => Length * 28,
            _ => Length,
        };

        if (resolvedLength <= 0)
        {
            return Math.Max(0, sampleLength - ResolveStartSamples(sampleLength, bytesPerSample));
        }

        return Math.Min(resolvedLength, Math.Max(0, sampleLength - ResolveStartSamples(sampleLength, bytesPerSample)));
    }

    public int ResolveStartBytesPcm16(int sampleLength)
        => ResolveStartSamples(sampleLength) * sizeof(short);

    public int ResolveStartBytesPsxAdpcm(int sampleLength)
        => PsxAdpcmLoopMath.SamplesToBytes(ResolveStartSamples(sampleLength));

    public LoopDescriptor NormalizeToSamples(int sampleLength, int bytesPerSample = sizeof(short))
        => FromSamples(Looping, ResolveStartSamples(sampleLength, bytesPerSample), sampleLength, Type);

    public LoopDescriptor WithSampleStart(int loopStartSample, int sampleLength)
        => FromSamples(Looping, loopStartSample, sampleLength, Type);

    public LoopDescriptor ScaleSamples(double scale, int scaledSampleLength)
    {
        if (!Looping)
        {
            return None;
        }

        var scaledStart = Math.Clamp((int)Math.Round(ResolveStartSamples(scaledSampleLength) * scale, MidpointRounding.AwayFromZero), 0, Math.Max(0, scaledSampleLength - 1));
        return FromSamples(true, scaledStart, scaledSampleLength, Type);
    }
}

internal static class PsxAdpcmLoopMath
{
    public static int SamplesToBytes(int loopStartSample)
        => Math.Max(0, (loopStartSample / 28) * 0x10);

    public static int BytesToSamples(int loopStartBytes)
        => Math.Max(0, (loopStartBytes / 0x10) * 28);

    public static LoopDescriptor NormalizeToPsxAdpcmBytes(LoopDescriptor loopDescriptor, int sampleLength)
    {
        if (!loopDescriptor.Looping)
        {
            return LoopDescriptor.None;
        }

        var normalized = loopDescriptor.NormalizeToSamples(sampleLength);
        var loopStartBytes = SamplesToBytes(normalized.ResolveStartSamples(sampleLength));
        var loopLengthBytes = SamplesToBytes(normalized.ResolveLengthSamples(sampleLength));
        if (loopLengthBytes <= 0)
        {
            loopLengthBytes = Math.Max(0, SamplesToBytes(sampleLength) - loopStartBytes);
        }

        return LoopDescriptor.FromPsxAdpcmBytes(true, loopStartBytes, loopLengthBytes, normalized.Type);
    }
}

internal sealed record SamplePitchComponents(
    int OriginalPitch,
    int PitchCorrectionCents,
    int StoredSampleRate,
    double SampleRatePitchOffsetSemitones,
    double LoopAlignmentPitchOffsetSemitones)
{
    public double SourceRootNoteSemitones => OriginalPitch + (PitchCorrectionCents / 100.0);

    public double PitchOffsetSemitones => SampleRatePitchOffsetSemitones + LoopAlignmentPitchOffsetSemitones;

    public double EffectiveRootNoteSemitones => SourceRootNoteSemitones + PitchOffsetSemitones;
}

internal sealed record RegionPitchComponents(
    int? OverridingRootKey,
    int CoarseTuneSemitones,
    int FineTuneCents)
{
    public double ResolveOffsetFromSourcePitch(SamplePitchComponents samplePitch)
    {
        var rootOverrideOffset = (OverridingRootKey ?? samplePitch.OriginalPitch) - samplePitch.OriginalPitch;
        return rootOverrideOffset + CoarseTuneSemitones + (FineTuneCents / 100.0);
    }

    public double ResolveEffectiveRootNoteSemitones(SamplePitchComponents samplePitch)
        => samplePitch.EffectiveRootNoteSemitones + ResolveOffsetFromSourcePitch(samplePitch);
}
