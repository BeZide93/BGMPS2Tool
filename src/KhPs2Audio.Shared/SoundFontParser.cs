namespace KhPs2Audio.Shared;

internal static class SoundFontParser
{
    private const int DefaultReferenceSampleRate = 44_100;
    private const ushort RightSampleType = 2;
    private const ushort LeftSampleType = 4;

    public static SoundFontFile Parse(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var data = File.ReadAllBytes(fullPath);
        if (data.Length < 12)
        {
            throw new InvalidDataException("SoundFont file is too small.");
        }

        if (!string.Equals(BinaryHelpers.ReadAscii(data, 0, 4), "RIFF", StringComparison.Ordinal) ||
            !string.Equals(BinaryHelpers.ReadAscii(data, 8, 4), "sfbk", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unexpected SoundFont header.");
        }

        byte[]? sampleChunk = null;
        byte[]? phdrChunk = null;
        byte[]? pbagChunk = null;
        byte[]? pgenChunk = null;
        byte[]? instChunk = null;
        byte[]? ibagChunk = null;
        byte[]? igenChunk = null;
        byte[]? shdrChunk = null;

        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var chunkId = BinaryHelpers.ReadAscii(data, offset, 4);
            var chunkLength = checked((int)BinaryHelpers.ReadUInt32LE(data, offset + 4));
            var chunkDataOffset = offset + 8;
            if (chunkDataOffset + chunkLength > data.Length)
            {
                throw new InvalidDataException("A SoundFont chunk exceeds the file length.");
            }

            if (string.Equals(chunkId, "LIST", StringComparison.Ordinal) && chunkLength >= 4)
            {
                var listType = BinaryHelpers.ReadAscii(data, chunkDataOffset, 4);
                if (string.Equals(listType, "sdta", StringComparison.Ordinal))
                {
                    sampleChunk = FindChunk(data, chunkDataOffset + 4, chunkLength - 4, "smpl");
                }
                else if (string.Equals(listType, "pdta", StringComparison.Ordinal))
                {
                    phdrChunk = FindChunk(data, chunkDataOffset + 4, chunkLength - 4, "phdr");
                    pbagChunk = FindChunk(data, chunkDataOffset + 4, chunkLength - 4, "pbag");
                    pgenChunk = FindChunk(data, chunkDataOffset + 4, chunkLength - 4, "pgen");
                    instChunk = FindChunk(data, chunkDataOffset + 4, chunkLength - 4, "inst");
                    ibagChunk = FindChunk(data, chunkDataOffset + 4, chunkLength - 4, "ibag");
                    igenChunk = FindChunk(data, chunkDataOffset + 4, chunkLength - 4, "igen");
                    shdrChunk = FindChunk(data, chunkDataOffset + 4, chunkLength - 4, "shdr");
                }
            }

            offset = chunkDataOffset + chunkLength + (chunkLength % 2);
        }

        if (sampleChunk is null || phdrChunk is null || pbagChunk is null || pgenChunk is null ||
            instChunk is null || ibagChunk is null || igenChunk is null || shdrChunk is null)
        {
            throw new InvalidDataException("SoundFont file is missing one or more required chunks.");
        }

        var sampleData = ParseSampleChunk(sampleChunk);
        var presetHeaders = ParsePresetHeaders(phdrChunk);
        var presetBags = ParseBags(pbagChunk);
        var presetGenerators = ParseGenerators(pgenChunk);
        var instrumentHeaders = ParseInstrumentHeaders(instChunk);
        var instrumentBags = ParseBags(ibagChunk);
        var instrumentGenerators = ParseGenerators(igenChunk);
        var sampleHeaders = ParseSampleHeaders(shdrChunk);

        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var referenceSampleRate = DetermineReferenceSampleRate(sampleHeaders);
        var presets = new List<SoundFontPreset>(presetHeaders.Count);
        for (var presetIndex = 0; presetIndex < presetHeaders.Count; presetIndex++)
        {
            var preset = presetHeaders[presetIndex];
            var nextPresetBagIndex = presetIndex < presetHeaders.Count - 1
                ? presetHeaders[presetIndex + 1].BagIndex
                : presetBags.Count - 1;

            var presetZones = ReadPresetZones(presetBags, presetGenerators, preset.BagIndex, nextPresetBagIndex, warnings);
            var presetGlobal = presetZones.FirstOrDefault(zone => zone.InstrumentIndex is null);
            var localPresetZones = presetZones.Where(zone => zone.InstrumentIndex.HasValue).ToList();

            var materializedRegions = new List<SoundFontRegion>();
            foreach (var presetZone in localPresetZones)
            {
                var instrumentIndex = presetZone.InstrumentIndex!.Value;
                if (instrumentIndex < 0 || instrumentIndex >= instrumentHeaders.Count)
                {
                    warnings.Add($"Preset {preset.Name} references missing instrument index {instrumentIndex}.");
                    continue;
                }

                var instrument = instrumentHeaders[instrumentIndex];
                var nextInstrumentBagIndex = instrumentIndex < instrumentHeaders.Count - 1
                    ? instrumentHeaders[instrumentIndex + 1].BagIndex
                    : instrumentBags.Count - 1;
                var instrumentZones = ReadInstrumentZones(instrumentBags, instrumentGenerators, instrument.BagIndex, nextInstrumentBagIndex, warnings);
                var instrumentGlobal = instrumentZones.FirstOrDefault(zone => zone.SampleId is null);

                foreach (var instrumentZone in instrumentZones.Where(zone => zone.SampleId.HasValue))
                {
                    var combined = GeneratorValues.Merge(
                        GeneratorValues.Merge(presetGlobal?.Values, presetZone.Values),
                        GeneratorValues.Merge(instrumentGlobal?.Values, instrumentZone.Values));

                    var materialized = MaterializeRegion(preset, combined, instrumentZone.SampleId, sampleHeaders, sampleData, referenceSampleRate, warnings);
                    if (materialized is not null)
                    {
                        materializedRegions.Add(materialized);
                    }
                }
            }

            presets.Add(new SoundFontPreset(
                preset.Name,
                preset.Bank,
                preset.Program,
                materializedRegions.OrderBy(static region => region.KeyLow).ThenBy(static region => region.KeyHigh).ToList()));
        }

        return new SoundFontFile(fullPath, presets, warnings.OrderBy(static warning => warning).ToList());
    }

    private static byte[]? FindChunk(byte[] file, int start, int length, string targetChunkId)
    {
        var offset = start;
        var end = start + length;
        while (offset + 8 <= end)
        {
            var chunkId = BinaryHelpers.ReadAscii(file, offset, 4);
            var chunkLength = checked((int)BinaryHelpers.ReadUInt32LE(file, offset + 4));
            var chunkDataOffset = offset + 8;
            if (chunkDataOffset + chunkLength > file.Length || chunkDataOffset + chunkLength > end)
            {
                throw new InvalidDataException("A SoundFont subchunk exceeds the containing LIST chunk.");
            }

            if (string.Equals(chunkId, targetChunkId, StringComparison.Ordinal))
            {
                var chunk = new byte[chunkLength];
                Buffer.BlockCopy(file, chunkDataOffset, chunk, 0, chunkLength);
                return chunk;
            }

            offset = chunkDataOffset + chunkLength + (chunkLength % 2);
        }

        return null;
    }

    private static short[] ParseSampleChunk(byte[] chunk)
    {
        if (chunk.Length % 2 != 0)
        {
            throw new InvalidDataException("The SoundFont sample chunk has an invalid length.");
        }

        var samples = new short[chunk.Length / 2];
        Buffer.BlockCopy(chunk, 0, samples, 0, chunk.Length);
        return samples;
    }

    private static List<Sf2PresetHeader> ParsePresetHeaders(byte[] chunk)
    {
        const int recordSize = 38;
        if (chunk.Length < recordSize || chunk.Length % recordSize != 0)
        {
            throw new InvalidDataException("Invalid SoundFont phdr chunk length.");
        }

        var count = (chunk.Length / recordSize) - 1;
        var headers = new List<Sf2PresetHeader>(count);
        for (var index = 0; index < count; index++)
        {
            var entryOffset = index * recordSize;
            headers.Add(new Sf2PresetHeader(
                ReadNullTerminatedAscii(chunk, entryOffset, 20),
                BinaryHelpers.ReadUInt16LE(chunk, entryOffset + 20),
                BinaryHelpers.ReadUInt16LE(chunk, entryOffset + 22),
                BinaryHelpers.ReadUInt16LE(chunk, entryOffset + 24)));
        }

        return headers;
    }

    private static List<Sf2InstrumentHeader> ParseInstrumentHeaders(byte[] chunk)
    {
        const int recordSize = 22;
        if (chunk.Length < recordSize || chunk.Length % recordSize != 0)
        {
            throw new InvalidDataException("Invalid SoundFont inst chunk length.");
        }

        var count = (chunk.Length / recordSize) - 1;
        var headers = new List<Sf2InstrumentHeader>(count);
        for (var index = 0; index < count; index++)
        {
            var entryOffset = index * recordSize;
            headers.Add(new Sf2InstrumentHeader(
                ReadNullTerminatedAscii(chunk, entryOffset, 20),
                BinaryHelpers.ReadUInt16LE(chunk, entryOffset + 20)));
        }

        return headers;
    }

    private static List<Sf2Bag> ParseBags(byte[] chunk)
    {
        const int recordSize = 4;
        if (chunk.Length < recordSize || chunk.Length % recordSize != 0)
        {
            throw new InvalidDataException("Invalid SoundFont bag chunk length.");
        }

        var count = chunk.Length / recordSize;
        var bags = new List<Sf2Bag>(count);
        for (var index = 0; index < count; index++)
        {
            var entryOffset = index * recordSize;
            bags.Add(new Sf2Bag(
                BinaryHelpers.ReadUInt16LE(chunk, entryOffset),
                BinaryHelpers.ReadUInt16LE(chunk, entryOffset + 2)));
        }

        return bags;
    }

    private static List<Sf2Generator> ParseGenerators(byte[] chunk)
    {
        const int recordSize = 4;
        if (chunk.Length < recordSize || chunk.Length % recordSize != 0)
        {
            throw new InvalidDataException("Invalid SoundFont generator chunk length.");
        }

        var count = chunk.Length / recordSize;
        var generators = new List<Sf2Generator>(count);
        for (var index = 0; index < count; index++)
        {
            var entryOffset = index * recordSize;
            generators.Add(new Sf2Generator(
                BinaryHelpers.ReadUInt16LE(chunk, entryOffset),
                BinaryHelpers.ReadUInt16LE(chunk, entryOffset + 2)));
        }

        return generators;
    }

    private static List<Sf2SampleHeader> ParseSampleHeaders(byte[] chunk)
    {
        const int recordSize = 46;
        if (chunk.Length < recordSize || chunk.Length % recordSize != 0)
        {
            throw new InvalidDataException("Invalid SoundFont shdr chunk length.");
        }

        var count = (chunk.Length / recordSize) - 1;
        var headers = new List<Sf2SampleHeader>(count);
        for (var index = 0; index < count; index++)
        {
            var entryOffset = index * recordSize;
            headers.Add(new Sf2SampleHeader(
                index,
                ReadNullTerminatedAscii(chunk, entryOffset, 20),
                checked((int)BinaryHelpers.ReadUInt32LE(chunk, entryOffset + 20)),
                checked((int)BinaryHelpers.ReadUInt32LE(chunk, entryOffset + 24)),
                checked((int)BinaryHelpers.ReadUInt32LE(chunk, entryOffset + 28)),
                checked((int)BinaryHelpers.ReadUInt32LE(chunk, entryOffset + 32)),
                checked((int)BinaryHelpers.ReadUInt32LE(chunk, entryOffset + 36)),
                chunk[entryOffset + 40],
                unchecked((sbyte)chunk[entryOffset + 41]),
                BinaryHelpers.ReadUInt16LE(chunk, entryOffset + 42),
                BinaryHelpers.ReadUInt16LE(chunk, entryOffset + 44)));
        }

        return headers;
    }

    private static List<Sf2PresetZone> ReadPresetZones(
        List<Sf2Bag> bags,
        List<Sf2Generator> generators,
        int firstBagIndex,
        int nextBagIndex,
        HashSet<string> warnings)
    {
        var zones = new List<Sf2PresetZone>();
        for (var bagIndex = firstBagIndex; bagIndex < nextBagIndex; bagIndex++)
        {
            var generatorStart = bags[bagIndex].GeneratorIndex;
            var generatorEnd = bagIndex < bags.Count - 1
                ? bags[bagIndex + 1].GeneratorIndex
                : generators.Count;

            var values = new GeneratorValues();
            int? instrumentIndex = null;
            for (var generatorIndex = generatorStart; generatorIndex < generatorEnd; generatorIndex++)
            {
                var generator = generators[generatorIndex];
                if (generator.Operator == 41)
                {
                    instrumentIndex = generator.Amount;
                }
                else
                {
                    values.Apply(generator, warnings);
                }
            }

            zones.Add(new Sf2PresetZone(instrumentIndex, values));
        }

        return zones;
    }

    private static List<Sf2InstrumentZone> ReadInstrumentZones(
        List<Sf2Bag> bags,
        List<Sf2Generator> generators,
        int firstBagIndex,
        int nextBagIndex,
        HashSet<string> warnings)
    {
        var zones = new List<Sf2InstrumentZone>();
        for (var bagIndex = firstBagIndex; bagIndex < nextBagIndex; bagIndex++)
        {
            var generatorStart = bags[bagIndex].GeneratorIndex;
            var generatorEnd = bagIndex < bags.Count - 1
                ? bags[bagIndex + 1].GeneratorIndex
                : generators.Count;

            var values = new GeneratorValues();
            int? sampleId = null;
            for (var generatorIndex = generatorStart; generatorIndex < generatorEnd; generatorIndex++)
            {
                var generator = generators[generatorIndex];
                if (generator.Operator == 53)
                {
                    sampleId = generator.Amount;
                }
                else
                {
                    values.Apply(generator, warnings);
                }
            }

            zones.Add(new Sf2InstrumentZone(sampleId, values));
        }

        return zones;
    }

    private static SoundFontRegion? MaterializeRegion(
        Sf2PresetHeader preset,
        GeneratorValues values,
        int? sampleId,
        List<Sf2SampleHeader> sampleHeaders,
        short[] sampleData,
        int referenceSampleRate,
        HashSet<string> warnings)
    {
        if (sampleId is null || sampleId.Value < 0 || sampleId.Value >= sampleHeaders.Count)
        {
            warnings.Add($"Preset {preset.Name} references missing sample id {sampleId?.ToString() ?? "<none>"}.");
            return null;
        }

        var sample = sampleHeaders[sampleId.Value];
        var sampleType = (ushort)(sample.SampleType & 0x7FFF);
        if (sampleType == RightSampleType && sample.SampleLink < sampleHeaders.Count)
        {
            return null;
        }

        var monoSlice = ExtractSampleSlice(sample, values, sampleHeaders, sampleData, warnings);
        if (monoSlice is null || monoSlice.Pcm.Length == 0)
        {
            return null;
        }

        var rootNote = values.OverridingRootKey ?? sample.OriginalPitch;
        var fineTuneCents = sample.PitchCorrection + values.FineTuneCents + (values.CoarseTuneSemitones * 100);
        if (sample.SampleRate > 0 && referenceSampleRate > 0 && sample.SampleRate != referenceSampleRate)
        {
            var sampleRateSemitones = 12.0 * Math.Log(referenceSampleRate / (double)sample.SampleRate, 2.0);
            var coarseRateSemitones = (int)Math.Floor(sampleRateSemitones);
            rootNote += coarseRateSemitones;
            fineTuneCents += (int)Math.Round((sampleRateSemitones - coarseRateSemitones) * 100.0, MidpointRounding.AwayFromZero);
        }

        while (fineTuneCents > 50)
        {
            rootNote++;
            fineTuneCents -= 100;
        }

        while (fineTuneCents < -50)
        {
            rootNote--;
            fineTuneCents += 100;
        }

        rootNote = Math.Clamp(rootNote, 0, 127);
        var attenuationCentibels = Math.Max(0, values.InitialAttenuationCentibels);
        var volume = Math.Clamp((float)Math.Pow(10.0, -attenuationCentibels / 200.0), 0f, 1f);
        var reverbSend = Math.Clamp(values.ReverbEffectsSendTenthsPercent / 1000.0f, 0f, 1f);
        volume *= ComputeDryMixFromReverbSend(reverbSend);
        var pan = Math.Clamp(values.PanTenthsPercent / 500.0f, -1f, 1f);
        var attackSeconds = TimecentsToSeconds(values.AttackVolEnvTimecents);
        var holdSeconds = TimecentsToSeconds(values.HoldVolEnvTimecents);
        var decaySeconds = TimecentsToSeconds(values.DecayVolEnvTimecents);
        var sustainLevel = Math.Clamp((float)Math.Pow(10.0, -Math.Max(0, values.SustainVolEnvCentibels) / 200.0), 0f, 1f);
        var releaseSeconds = TimecentsToSeconds(values.ReleaseVolEnvTimecents);

        return new SoundFontRegion(
            preset.Bank,
            preset.Program,
            preset.Name,
            monoSlice.IdentityKey,
            monoSlice.SourceSampleName,
            monoSlice.SecondaryIdentityKey,
            monoSlice.SecondarySourceSampleName,
            values.KeyLow,
            values.KeyHigh,
            values.VelocityLow,
            values.VelocityHigh,
            rootNote,
            fineTuneCents,
            volume,
            pan,
            reverbSend,
            attackSeconds,
            holdSeconds,
            decaySeconds,
            sustainLevel,
            releaseSeconds,
            monoSlice.Looping,
            monoSlice.LoopStartSample,
            monoSlice.Pcm,
            monoSlice.SecondaryPcm);
    }

    private static int DetermineReferenceSampleRate(IReadOnlyList<Sf2SampleHeader> sampleHeaders)
    {
        var usableSamples = sampleHeaders
            .Where(static sample => sample.SampleRate > 0 && !string.Equals(sample.Name, "EOS", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (usableSamples.Length == 0)
        {
            return DefaultReferenceSampleRate;
        }

        var usableRates = usableSamples
            .Select(static sample => sample.SampleRate)
            .OrderBy(static rate => rate)
            .ToArray();
        var dominantRate = usableSamples
            .GroupBy(static sample => sample.SampleRate)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => Math.Abs(group.Key - 32_000))
            .Select(static group => group.Key)
            .First();

        // Tiny waveform-style banks often really are authored around their own sample-rate basis
        // (for example a clean 32000 Hz). Larger multi-region banks are more stable on the older
        // conservative 44.1 kHz path, even if their raw samples cluster around ~32768 Hz.
        if (usableSamples.Length <= 24)
        {
            var dominantCoverage = usableSamples.Count(sample => Math.Abs(sample.SampleRate - dominantRate) <= 64) / (double)usableSamples.Length;
            if (dominantCoverage >= 0.75)
            {
                var smallBankTargets = new[] { 32_000, 32_768 };
                var snappedSmallBankRate = smallBankTargets
                    .OrderBy(rate => Math.Abs(rate - dominantRate))
                    .First();
                if (Math.Abs(snappedSmallBankRate - dominantRate) / (double)dominantRate <= 0.03)
                {
                    return snappedSmallBankRate;
                }
            }
        }

        var median = usableRates[usableRates.Length / 2];
        var commonRates = new[] { 44_100, 48_000 };
        var snapped = commonRates
            .OrderBy(rate => Math.Abs(rate - median))
            .First();

        var relativeError = Math.Abs(snapped - median) / (double)median;
        return relativeError <= 0.03 ? snapped : DefaultReferenceSampleRate;
    }

    private static double TimecentsToSeconds(int timecents)
    {
        if (timecents <= -32768)
        {
            return 0;
        }

        return Math.Pow(2.0, timecents / 1200.0);
    }

    private static float ComputeDryMixFromReverbSend(float reverbSend)
    {
        var clamped = Math.Clamp(reverbSend, 0f, 1f);
        var dryDecibels = -6.0f * clamped;
        return MathF.Pow(10f, dryDecibels / 20f);
    }

    private static ExtractedSampleSlice? ExtractSampleSlice(
        Sf2SampleHeader sample,
        GeneratorValues values,
        List<Sf2SampleHeader> sampleHeaders,
        short[] sampleData,
        HashSet<string> warnings)
    {
        var sampleType = (ushort)(sample.SampleType & 0x7FFF);
        var start = sample.Start + values.StartAddrsOffset + (values.StartAddrsCoarseOffset * 32768);
        var end = sample.End + values.EndAddrsOffset + (values.EndAddrsCoarseOffset * 32768);
        var loopStart = sample.StartLoop + values.StartLoopAddrsOffset + (values.StartLoopAddrsCoarseOffset * 32768);
        var loopEnd = sample.EndLoop + values.EndLoopAddrsOffset + (values.EndLoopAddrsCoarseOffset * 32768);

        start = Math.Clamp(start, 0, sampleData.Length);
        end = Math.Clamp(end, start, sampleData.Length);
        loopStart = Math.Clamp(loopStart, start, end);
        loopEnd = Math.Clamp(loopEnd, loopStart, end);

        var looping = (values.SampleModes & 0x1) != 0 && loopEnd > loopStart;
        var sliceEnd = looping ? loopEnd : end;
        if (sliceEnd <= start)
        {
            warnings.Add($"Sample {sample.Name} resolved to an empty slice after generator offsets.");
            return null;
        }

        var pcm = CopySlice(sampleData, start, sliceEnd);
        var loopStartSample = looping ? loopStart - start : 0;
        var identityKey = $"{sample.Index}:{start}:{sliceEnd}:{loopStart}:{loopEnd}:{looping}";
        var sourceName = sample.Name;

        if (sampleType == LeftSampleType && sample.SampleLink < sampleHeaders.Count)
        {
            var linked = sampleHeaders[checked((int)sample.SampleLink)];
            var linkedStart = linked.Start + values.StartAddrsOffset + (values.StartAddrsCoarseOffset * 32768);
            var linkedEnd = linked.End + values.EndAddrsOffset + (values.EndAddrsCoarseOffset * 32768);
            var linkedLoopStart = linked.StartLoop + values.StartLoopAddrsOffset + (values.StartLoopAddrsCoarseOffset * 32768);
            var linkedLoopEnd = linked.EndLoop + values.EndLoopAddrsOffset + (values.EndLoopAddrsCoarseOffset * 32768);

            linkedStart = Math.Clamp(linkedStart, 0, sampleData.Length);
            linkedEnd = Math.Clamp(linkedEnd, linkedStart, sampleData.Length);
            linkedLoopStart = Math.Clamp(linkedLoopStart, linkedStart, linkedEnd);
            linkedLoopEnd = Math.Clamp(linkedLoopEnd, linkedLoopStart, linkedEnd);

            var linkedSliceEnd = looping ? linkedLoopEnd : linkedEnd;
            if (linkedSliceEnd > linkedStart)
            {
                var linkedPcm = CopySlice(sampleData, linkedStart, linkedSliceEnd);
                var pairLength = Math.Min(pcm.Length, linkedPcm.Length);
                if (pairLength > 0)
                {
                    pcm = TrimSlice(pcm, pairLength);
                    linkedPcm = TrimSlice(linkedPcm, pairLength);
                    identityKey = $"stereo:L:{Math.Min(sample.Index, linked.Index)}:{Math.Max(sample.Index, linked.Index)}:{start}:{sliceEnd}:{linkedStart}:{linkedSliceEnd}:{looping}";
                    sourceName = sample.Name;
                    return new ExtractedSampleSlice(
                        identityKey,
                        sourceName,
                        pcm,
                        $"stereo:R:{Math.Min(sample.Index, linked.Index)}:{Math.Max(sample.Index, linked.Index)}:{start}:{sliceEnd}:{linkedStart}:{linkedSliceEnd}:{looping}",
                        linked.Name,
                        linkedPcm,
                        looping,
                        Math.Clamp(loopStartSample, 0, Math.Max(0, pairLength - 1)));
                }
            }
        }

        return new ExtractedSampleSlice(
            identityKey,
            sourceName,
            pcm,
            null,
            null,
            null,
            looping,
            Math.Clamp(loopStartSample, 0, Math.Max(0, pcm.Length - 1)));
    }

    private static short[] CopySlice(short[] sampleData, int start, int endExclusive)
    {
        var length = Math.Max(0, endExclusive - start);
        var output = new short[length];
        Array.Copy(sampleData, start, output, 0, length);
        return output;
    }

    private static short[] TrimSlice(short[] input, int length)
    {
        if (input.Length == length)
        {
            return input;
        }

        var output = new short[length];
        Array.Copy(input, output, length);
        return output;
    }

    internal static List<SoundFontRegion> NormalizeRegions(List<SoundFontRegion> regions, HashSet<string> warnings)
    {
        if (regions.Count == 0)
        {
            return [];
        }

        var keyBoundaries = new SortedSet<int> { 0, 128 };
        foreach (var region in regions)
        {
            keyBoundaries.Add(Math.Clamp(region.KeyLow, 0, 127));
            keyBoundaries.Add(Math.Clamp(region.KeyHigh + 1, 1, 128));
        }

        var authored = new List<SoundFontRegion>();
        var orderedKeyBoundaries = keyBoundaries.OrderBy(static value => value).ToArray();
        for (var boundaryIndex = 0; boundaryIndex < orderedKeyBoundaries.Length - 1; boundaryIndex++)
        {
            var keyLow = orderedKeyBoundaries[boundaryIndex];
            var keyHigh = orderedKeyBoundaries[boundaryIndex + 1] - 1;
            if (keyHigh < keyLow)
            {
                continue;
            }

            var keyCandidates = regions
                .Where(region => keyLow >= region.KeyLow && keyHigh <= region.KeyHigh)
                .OrderByDescending(static region => region.Volume)
                .ToList();

            if (keyCandidates.Count == 0)
            {
                var chosen = regions
                    .OrderBy(region => DistanceToRange((keyLow + keyHigh) / 2, region.KeyLow, region.KeyHigh))
                    .ThenByDescending(static region => region.Volume)
                    .FirstOrDefault();

                if (chosen is not null)
                {
                    warnings.Add($"Filled a SoundFont key gap at {keyLow}-{keyHigh} using the nearest available source zone.");
                    keyCandidates = [chosen];
                }
            }

            if (keyCandidates.Count == 0)
            {
                continue;
            }

            var velocityBoundaries = new SortedSet<int> { 0, 128 };
            foreach (var region in keyCandidates)
            {
                velocityBoundaries.Add(Math.Clamp(region.VelocityLow, 0, 127));
                velocityBoundaries.Add(Math.Clamp(region.VelocityHigh + 1, 1, 128));
            }

            var orderedVelocityBoundaries = velocityBoundaries.OrderBy(static value => value).ToArray();
            for (var velocityIndex = 0; velocityIndex < orderedVelocityBoundaries.Length - 1; velocityIndex++)
            {
                var velocityLow = orderedVelocityBoundaries[velocityIndex];
                var velocityHigh = orderedVelocityBoundaries[velocityIndex + 1] - 1;
                if (velocityHigh < velocityLow)
                {
                    continue;
                }

                var velocityCandidates = keyCandidates
                    .Where(region => velocityLow >= region.VelocityLow && velocityHigh <= region.VelocityHigh)
                    .OrderByDescending(static region => region.Volume)
                    .ToList();

                if (velocityCandidates.Count == 0)
                {
                    var chosen = keyCandidates
                        .OrderBy(region => DistanceToRange((velocityLow + velocityHigh) / 2, region.VelocityLow, region.VelocityHigh))
                        .ThenByDescending(static region => region.Volume)
                        .FirstOrDefault();
                    if (chosen is null)
                    {
                        continue;
                    }

                    velocityCandidates = [chosen];
                }

                foreach (var partitioned in velocityCandidates
                    .Select(region => region with { KeyLow = keyLow, KeyHigh = keyHigh, VelocityLow = velocityLow, VelocityHigh = velocityHigh })
                    .OrderBy(static region => region.KeyHigh)
                    .ThenBy(static region => region.VelocityHigh)
                    .ThenBy(static region => region.SourceSampleName, StringComparer.Ordinal)
                    .ThenBy(static region => region.StereoSourceSampleName ?? string.Empty, StringComparer.Ordinal))
                {
                    if (authored.Count > 0 && CanMerge(authored[^1], partitioned))
                    {
                        authored[^1] = authored[^1] with { KeyHigh = keyHigh };
                    }
                    else
                    {
                        authored.Add(partitioned);
                    }
                }
            }
        }

        return authored.Count == 0
            ? regions.OrderBy(static region => region.KeyHigh).ThenBy(static region => region.VelocityHigh).ToList()
            : authored;
    }

    private static bool CanMerge(SoundFontRegion left, SoundFontRegion right)
    {
        return left.KeyHigh + 1 == right.KeyLow &&
               left.VelocityLow == right.VelocityLow &&
               left.VelocityHigh == right.VelocityHigh &&
               left.IdentityKey == right.IdentityKey &&
               left.StereoIdentityKey == right.StereoIdentityKey &&
               left.RootKey == right.RootKey &&
               left.FineTuneCents == right.FineTuneCents &&
               Math.Abs(left.Volume - right.Volume) < 0.0001f &&
               Math.Abs(left.Pan - right.Pan) < 0.0001f &&
               Math.Abs(left.ReverbSend - right.ReverbSend) < 0.0001f &&
               Math.Abs(left.AttackSeconds - right.AttackSeconds) < 0.0001 &&
               Math.Abs(left.HoldSeconds - right.HoldSeconds) < 0.0001 &&
               Math.Abs(left.DecaySeconds - right.DecaySeconds) < 0.0001 &&
               Math.Abs(left.SustainLevel - right.SustainLevel) < 0.0001f &&
               Math.Abs(left.ReleaseSeconds - right.ReleaseSeconds) < 0.0001 &&
               left.Looping == right.Looping &&
               left.LoopStartSample == right.LoopStartSample;
    }

    private static int DistanceToRange(int key, int low, int high)
    {
        if (key < low)
        {
            return low - key;
        }

        if (key > high)
        {
            return key - high;
        }

        return 0;
    }

    private static string ReadNullTerminatedAscii(byte[] data, int offset, int length)
    {
        var text = System.Text.Encoding.ASCII.GetString(data, offset, length);
        var terminator = text.IndexOf('\0');
        return (terminator >= 0 ? text[..terminator] : text).Trim();
    }

    private sealed class GeneratorValues
    {
        public int StartAddrsOffset { get; private set; }
        public int EndAddrsOffset { get; private set; }
        public int StartLoopAddrsOffset { get; private set; }
        public int EndLoopAddrsOffset { get; private set; }
        public int StartAddrsCoarseOffset { get; private set; }
        public int EndAddrsCoarseOffset { get; private set; }
        public int StartLoopAddrsCoarseOffset { get; private set; }
        public int EndLoopAddrsCoarseOffset { get; private set; }
        public int KeyLow { get; private set; } = 0;
        public int KeyHigh { get; private set; } = 127;
        public int VelocityLow { get; private set; } = 0;
        public int VelocityHigh { get; private set; } = 127;
        public int? SampleId { get; private set; }
        public int InitialAttenuationCentibels { get; private set; }
        public int PanTenthsPercent { get; private set; }
        public int ReverbEffectsSendTenthsPercent { get; private set; }
        public int CoarseTuneSemitones { get; private set; }
        public int FineTuneCents { get; private set; }
        public int SampleModes { get; private set; }
        public int? OverridingRootKey { get; private set; }
        public int AttackVolEnvTimecents { get; private set; } = -12000;
        public int HoldVolEnvTimecents { get; private set; } = -12000;
        public int DecayVolEnvTimecents { get; private set; } = -12000;
        public int SustainVolEnvCentibels { get; private set; }
        public int ReleaseVolEnvTimecents { get; private set; } = -12000;

        public void Apply(Sf2Generator generator, HashSet<string> warnings)
        {
            var signedAmount = unchecked((short)generator.Amount);
            switch (generator.Operator)
            {
                case 0:
                    StartAddrsOffset += signedAmount;
                    break;
                case 1:
                    EndAddrsOffset += signedAmount;
                    break;
                case 2:
                    StartLoopAddrsOffset += signedAmount;
                    break;
                case 3:
                    EndLoopAddrsOffset += signedAmount;
                    break;
                case 4:
                    StartAddrsCoarseOffset += signedAmount;
                    break;
                case 12:
                    EndAddrsCoarseOffset += signedAmount;
                    break;
                case 17:
                    PanTenthsPercent += signedAmount;
                    break;
                case 16:
                    ReverbEffectsSendTenthsPercent += signedAmount;
                    break;
                case 43:
                    KeyLow = generator.Amount & 0xFF;
                    KeyHigh = generator.Amount >> 8;
                    break;
                case 44:
                    VelocityLow = generator.Amount & 0xFF;
                    VelocityHigh = generator.Amount >> 8;
                    break;
                case 45:
                    StartLoopAddrsCoarseOffset += signedAmount;
                    break;
                case 46:
                    EndLoopAddrsCoarseOffset += signedAmount;
                    break;
                case 48:
                    InitialAttenuationCentibels += signedAmount;
                    break;
                case 34:
                    AttackVolEnvTimecents += signedAmount;
                    break;
                case 35:
                    HoldVolEnvTimecents += signedAmount;
                    break;
                case 36:
                    DecayVolEnvTimecents += signedAmount;
                    break;
                case 37:
                    SustainVolEnvCentibels += signedAmount;
                    break;
                case 38:
                    ReleaseVolEnvTimecents += signedAmount;
                    break;
                case 51:
                    CoarseTuneSemitones += signedAmount;
                    break;
                case 52:
                    FineTuneCents += signedAmount;
                    break;
                case 54:
                    SampleModes = generator.Amount;
                    break;
                case 58:
                    OverridingRootKey = generator.Amount & 0xFF;
                    break;
                case 13:
                case 56:
                case 57:
                    warnings.Add($"Ignored SoundFont generator {generator.Operator} during conversion.");
                    break;
                default:
                    if (generator.Operator is not 41 and not 53)
                    {
                        warnings.Add($"Ignored SoundFont generator {generator.Operator} during conversion.");
                    }

                    break;
            }
        }

        public static GeneratorValues Merge(GeneratorValues? baseValues, GeneratorValues? overlayValues)
        {
            var merged = new GeneratorValues();
            if (baseValues is not null)
            {
                CopyTo(baseValues, merged);
            }

            if (overlayValues is not null)
            {
                merged.StartAddrsOffset += overlayValues.StartAddrsOffset;
                merged.EndAddrsOffset += overlayValues.EndAddrsOffset;
                merged.StartLoopAddrsOffset += overlayValues.StartLoopAddrsOffset;
                merged.EndLoopAddrsOffset += overlayValues.EndLoopAddrsOffset;
                merged.StartAddrsCoarseOffset += overlayValues.StartAddrsCoarseOffset;
                merged.EndAddrsCoarseOffset += overlayValues.EndAddrsCoarseOffset;
                merged.StartLoopAddrsCoarseOffset += overlayValues.StartLoopAddrsCoarseOffset;
                merged.EndLoopAddrsCoarseOffset += overlayValues.EndLoopAddrsCoarseOffset;
                merged.InitialAttenuationCentibels += overlayValues.InitialAttenuationCentibels;
                merged.PanTenthsPercent += overlayValues.PanTenthsPercent;
                merged.ReverbEffectsSendTenthsPercent += overlayValues.ReverbEffectsSendTenthsPercent;
                merged.CoarseTuneSemitones += overlayValues.CoarseTuneSemitones;
                merged.FineTuneCents += overlayValues.FineTuneCents;
                merged.AttackVolEnvTimecents += overlayValues.AttackVolEnvTimecents;
                merged.HoldVolEnvTimecents += overlayValues.HoldVolEnvTimecents;
                merged.DecayVolEnvTimecents += overlayValues.DecayVolEnvTimecents;
                merged.SustainVolEnvCentibels += overlayValues.SustainVolEnvCentibels;
                merged.ReleaseVolEnvTimecents += overlayValues.ReleaseVolEnvTimecents;
                merged.SampleModes = overlayValues.SampleModes != 0 ? overlayValues.SampleModes : merged.SampleModes;
                merged.SampleId = overlayValues.SampleId ?? merged.SampleId;
                merged.OverridingRootKey = overlayValues.OverridingRootKey ?? merged.OverridingRootKey;
                merged.KeyLow = overlayValues.KeyLow;
                merged.KeyHigh = overlayValues.KeyHigh;
                merged.VelocityLow = overlayValues.VelocityLow;
                merged.VelocityHigh = overlayValues.VelocityHigh;
            }

            return merged;
        }

        private static void CopyTo(GeneratorValues source, GeneratorValues destination)
        {
            destination.StartAddrsOffset = source.StartAddrsOffset;
            destination.EndAddrsOffset = source.EndAddrsOffset;
            destination.StartLoopAddrsOffset = source.StartLoopAddrsOffset;
            destination.EndLoopAddrsOffset = source.EndLoopAddrsOffset;
            destination.StartAddrsCoarseOffset = source.StartAddrsCoarseOffset;
            destination.EndAddrsCoarseOffset = source.EndAddrsCoarseOffset;
            destination.StartLoopAddrsCoarseOffset = source.StartLoopAddrsCoarseOffset;
            destination.EndLoopAddrsCoarseOffset = source.EndLoopAddrsCoarseOffset;
            destination.KeyLow = source.KeyLow;
            destination.KeyHigh = source.KeyHigh;
            destination.VelocityLow = source.VelocityLow;
            destination.VelocityHigh = source.VelocityHigh;
            destination.SampleId = source.SampleId;
            destination.InitialAttenuationCentibels = source.InitialAttenuationCentibels;
            destination.PanTenthsPercent = source.PanTenthsPercent;
            destination.ReverbEffectsSendTenthsPercent = source.ReverbEffectsSendTenthsPercent;
            destination.CoarseTuneSemitones = source.CoarseTuneSemitones;
            destination.FineTuneCents = source.FineTuneCents;
            destination.AttackVolEnvTimecents = source.AttackVolEnvTimecents;
            destination.HoldVolEnvTimecents = source.HoldVolEnvTimecents;
            destination.DecayVolEnvTimecents = source.DecayVolEnvTimecents;
            destination.SustainVolEnvCentibels = source.SustainVolEnvCentibels;
            destination.ReleaseVolEnvTimecents = source.ReleaseVolEnvTimecents;
            destination.SampleModes = source.SampleModes;
            destination.OverridingRootKey = source.OverridingRootKey;
        }
    }

    private sealed record Sf2PresetHeader(string Name, int Program, int Bank, int BagIndex);

    private sealed record Sf2InstrumentHeader(string Name, int BagIndex);

    private sealed record Sf2Bag(int GeneratorIndex, int ModulatorIndex);

    private sealed record Sf2Generator(ushort Operator, ushort Amount);

    private sealed record Sf2PresetZone(int? InstrumentIndex, GeneratorValues Values);

    private sealed record Sf2InstrumentZone(int? SampleId, GeneratorValues Values);

    private sealed record Sf2SampleHeader(
        int Index,
        string Name,
        int Start,
        int End,
        int StartLoop,
        int EndLoop,
        int SampleRate,
        int OriginalPitch,
        int PitchCorrection,
        int SampleLink,
        int SampleType);

    private sealed record ExtractedSampleSlice(
        string IdentityKey,
        string SourceSampleName,
        short[] Pcm,
        string? SecondaryIdentityKey,
        string? SecondarySourceSampleName,
        short[]? SecondaryPcm,
        bool Looping,
        int LoopStartSample);
}

internal sealed record SoundFontFile(
    string FilePath,
    List<SoundFontPreset> Presets,
    List<string> Warnings)
{
    public SoundFontPreset? FindPreset(int bank, int program)
    {
        return Presets.FirstOrDefault(preset => preset.Bank == bank && preset.Program == program)
            ?? Presets.FirstOrDefault(preset => preset.Bank == (bank & ~0x7F) && preset.Program == program)
            ?? Presets.FirstOrDefault(preset => preset.Bank == 0 && preset.Program == program);
    }
}

internal sealed record SoundFontPreset(
    string Name,
    int Bank,
    int Program,
    List<SoundFontRegion> Regions);

internal sealed record SoundFontRegion(
    int PresetBank,
    int PresetProgram,
    string PresetName,
    string IdentityKey,
    string SourceSampleName,
    string? StereoIdentityKey,
    string? StereoSourceSampleName,
    int KeyLow,
    int KeyHigh,
    int VelocityLow,
    int VelocityHigh,
    int RootKey,
    int FineTuneCents,
    float Volume,
    float Pan,
    float ReverbSend,
    double AttackSeconds,
    double HoldSeconds,
    double DecaySeconds,
    float SustainLevel,
    double ReleaseSeconds,
    bool Looping,
    int LoopStartSample,
    short[] Pcm,
    short[]? StereoPcm);
