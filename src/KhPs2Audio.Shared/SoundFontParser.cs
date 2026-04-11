namespace KhPs2Audio.Shared;

internal static class SoundFontParser
{
    private const int DefaultReferenceSampleRate = 44_100;
    private const ushort RightSampleType = 2;
    private const ushort LeftSampleType = 4;
    private const int DefaultInitialFilterFcCents = 13_500;
    private const int MinInitialFilterFcCents = 1_500;
    private const int MaxInitialFilterFcCents = 13_500;

    public static SoundFontFile Parse(string path, SoundFontImportOptions? importOptions = null)
    {
        var fullPath = Path.GetFullPath(path);
        importOptions ??= SoundFontImportOptions.Default;
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
        byte[]? pmodChunk = null;
        byte[]? pgenChunk = null;
        byte[]? instChunk = null;
        byte[]? ibagChunk = null;
        byte[]? imodChunk = null;
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
                    pmodChunk = FindChunk(data, chunkDataOffset + 4, chunkLength - 4, "pmod");
                    pgenChunk = FindChunk(data, chunkDataOffset + 4, chunkLength - 4, "pgen");
                    instChunk = FindChunk(data, chunkDataOffset + 4, chunkLength - 4, "inst");
                    ibagChunk = FindChunk(data, chunkDataOffset + 4, chunkLength - 4, "ibag");
                    imodChunk = FindChunk(data, chunkDataOffset + 4, chunkLength - 4, "imod");
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
        var presetModulators = pmodChunk is null ? [] : ParseModulators(pmodChunk, "pmod");
        var presetGenerators = ParseGenerators(pgenChunk);
        var instrumentHeaders = ParseInstrumentHeaders(instChunk);
        var instrumentBags = ParseBags(ibagChunk);
        var instrumentModulators = imodChunk is null ? [] : ParseModulators(imodChunk, "imod");
        var instrumentGenerators = ParseGenerators(igenChunk);
        var sampleHeaders = ParseSampleHeaders(shdrChunk);

        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var playbackSampleRate = DetermineReferenceSampleRate(sampleHeaders);
        var presets = new List<SoundFontPreset>(presetHeaders.Count);
        for (var presetIndex = 0; presetIndex < presetHeaders.Count; presetIndex++)
        {
            var preset = presetHeaders[presetIndex];
            var nextPresetBagIndex = presetIndex < presetHeaders.Count - 1
                ? presetHeaders[presetIndex + 1].BagIndex
                : presetBags.Count - 1;

            var presetZones = ReadPresetZones(presetBags, presetGenerators, presetModulators, preset.BagIndex, nextPresetBagIndex, warnings);
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
                var instrumentZones = ReadInstrumentZones(instrumentBags, instrumentGenerators, instrumentModulators, instrument.BagIndex, nextInstrumentBagIndex, warnings);
                var instrumentGlobal = instrumentZones.FirstOrDefault(zone => zone.SampleId is null);

                foreach (var instrumentZone in instrumentZones.Where(zone => zone.SampleId.HasValue))
                {
                    var presetCombined = GeneratorValues.MergeWithinDomain(presetGlobal?.Values, presetZone.Values);
                    var instrumentCombined = GeneratorValues.MergeWithinDomain(instrumentGlobal?.Values, instrumentZone.Values);
                    var combined = GeneratorValues.MergeAcrossDomains(presetCombined, instrumentCombined);
                    var modulators = CombineModulators(presetGlobal?.Modulators, presetZone.Modulators, instrumentGlobal?.Modulators, instrumentZone.Modulators);

                    var materialized = MaterializeRegion(preset, combined, modulators, instrumentZone.SampleId, sampleHeaders, sampleData, playbackSampleRate, importOptions, warnings);
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

    private static List<Sf2Modulator> ParseModulators(byte[] chunk, string chunkName)
    {
        const int recordSize = 10;
        if (chunk.Length < recordSize || chunk.Length % recordSize != 0)
        {
            throw new InvalidDataException($"Invalid SoundFont {chunkName} chunk length.");
        }

        var count = chunk.Length / recordSize;
        var modulators = new List<Sf2Modulator>(count);
        for (var index = 0; index < count; index++)
        {
            var entryOffset = index * recordSize;
            modulators.Add(new Sf2Modulator(
                BinaryHelpers.ReadUInt16LE(chunk, entryOffset),
                BinaryHelpers.ReadUInt16LE(chunk, entryOffset + 2),
                unchecked((short)BinaryHelpers.ReadUInt16LE(chunk, entryOffset + 4)),
                BinaryHelpers.ReadUInt16LE(chunk, entryOffset + 6),
                BinaryHelpers.ReadUInt16LE(chunk, entryOffset + 8)));
        }

        return modulators;
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
        List<Sf2Modulator> modulators,
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
            var modulatorStart = bags[bagIndex].ModulatorIndex;
            var modulatorEnd = bagIndex < bags.Count - 1
                ? bags[bagIndex + 1].ModulatorIndex
                : modulators.Count;

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

            var zoneKind = instrumentIndex is null ? "global" : "local";
            var zoneModulators = ReadZoneModulators(modulators, modulatorStart, modulatorEnd, "preset", zoneKind, warnings);
            zones.Add(new Sf2PresetZone(instrumentIndex, values, zoneModulators));
        }

        return zones;
    }

    private static List<Sf2InstrumentZone> ReadInstrumentZones(
        List<Sf2Bag> bags,
        List<Sf2Generator> generators,
        List<Sf2Modulator> modulators,
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
            var modulatorStart = bags[bagIndex].ModulatorIndex;
            var modulatorEnd = bagIndex < bags.Count - 1
                ? bags[bagIndex + 1].ModulatorIndex
                : modulators.Count;

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

            var zoneKind = sampleId is null ? "global" : "local";
            var zoneModulators = ReadZoneModulators(modulators, modulatorStart, modulatorEnd, "instrument", zoneKind, warnings);
            zones.Add(new Sf2InstrumentZone(sampleId, values, zoneModulators));
        }

        return zones;
    }

    private static IReadOnlyList<SoundFontModulatorDebug> ReadZoneModulators(
        IReadOnlyList<Sf2Modulator> modulators,
        int startIndex,
        int endIndex,
        string domain,
        string zoneKind,
        HashSet<string> warnings)
    {
        if (modulators.Count == 0)
        {
            return [];
        }

        var safeStart = Math.Clamp(startIndex, 0, modulators.Count);
        var safeEnd = Math.Clamp(endIndex, safeStart, modulators.Count);
        var output = new List<SoundFontModulatorDebug>();
        for (var index = safeStart; index < safeEnd; index++)
        {
            var modulator = modulators[index];
            if (modulator.IsTerminal)
            {
                continue;
            }

            output.Add(modulator.ToDebug(domain, zoneKind, index));
        }

        if (output.Count > 0)
        {
            warnings.Add("SoundFont modulators (pmod/imod) are imported for manifest/debug audit only; dynamic modulation is not converted to WD playback.");
        }

        return output;
    }

    private static IReadOnlyList<SoundFontModulatorDebug> CombineModulators(params IReadOnlyList<SoundFontModulatorDebug>?[] modulatorGroups)
    {
        var combined = new List<SoundFontModulatorDebug>();
        foreach (var group in modulatorGroups)
        {
            if (group is null || group.Count == 0)
            {
                continue;
            }

            combined.AddRange(group);
        }

        return combined;
    }

    private static SoundFontRegion? MaterializeRegion(
        Sf2PresetHeader preset,
        GeneratorValues values,
        IReadOnlyList<SoundFontModulatorDebug> modulators,
        int? sampleId,
        List<Sf2SampleHeader> sampleHeaders,
        short[] sampleData,
        int playbackSampleRate,
        SoundFontImportOptions importOptions,
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

        if (values.KeyLow > values.KeyHigh || values.VelocityLow > values.VelocityHigh)
        {
            return null;
        }

        var monoSlice = ExtractSampleSlice(sample, values, sampleHeaders, sampleData, warnings);
        if (monoSlice is null || monoSlice.Pcm.Length == 0)
        {
            return null;
        }

        monoSlice = PrepareSampleSliceForKh2PitchCompensation(monoSlice, sample, sampleHeaders, playbackSampleRate, importOptions, warnings);
        var initialFilter = ResolveInitialFilterBake(values.InitialFilterFcCents, monoSlice.SampleRate);
        monoSlice = ApplyInitialFilterBake(monoSlice, initialFilter, warnings);
        var debug = CreateRegionDebug(sample, values, monoSlice, modulators);

        var samplePitch = new SamplePitchComponents(
            sample.OriginalPitch,
            sample.PitchCorrection,
            monoSlice.SampleRate,
            0.0,
            0.0);
        var regionPitch = new RegionPitchComponents(
            values.OverridingRootKey,
            values.CoarseTuneSemitones,
            values.FineTuneCents,
            values.ScaleTuningCentsPerKey,
            (values.KeyLow + values.KeyHigh) / 2);
        if (values.ScaleTuningCentsPerKey != 100)
        {
            warnings.Add($"SoundFont scaleTuning={values.ScaleTuningCentsPerKey} cents/key is approximated at each region center during WD pitch encoding.");
        }
        var attenuationCentibels = Math.Max(0, values.InitialAttenuationCentibels);
        var volume = Math.Clamp((float)Math.Pow(10.0, -attenuationCentibels / 200.0), 0f, 1f);
        var reverbSend = Math.Clamp(values.ReverbEffectsSendTenthsPercent / 1000.0f, 0f, 1f);
        volume *= ComputeDryMixFromReverbSend(reverbSend);
        var pan = Math.Clamp(values.PanTenthsPercent / 500.0f, -1f, 1f);
        var attackSeconds = TimecentsToSeconds(values.AttackVolEnvTimecents ?? -12000);
        var holdSeconds = TimecentsToSeconds(values.HoldVolEnvTimecents ?? -12000);
        var decaySeconds = TimecentsToSeconds(values.DecayVolEnvTimecents ?? -12000);
        var sustainLevel = Math.Clamp((float)Math.Pow(10.0, -Math.Max(0, values.SustainVolEnvCentibels ?? 0) / 200.0), 0f, 1f);
        var releaseSeconds = TimecentsToSeconds(values.ReleaseVolEnvTimecents ?? -12000);

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
            samplePitch,
            regionPitch,
            volume,
            pan,
            reverbSend,
            attackSeconds,
            holdSeconds,
            decaySeconds,
            sustainLevel,
            releaseSeconds,
            initialFilter.Cents,
            initialFilter.Applies ? initialFilter.CutoffHz : 0.0,
            monoSlice.SampleRate,
            monoSlice.LoopDescriptor,
            monoSlice.Pcm,
            monoSlice.SecondaryPcm,
            debug);
    }

    private static SoundFontRegionDebug CreateRegionDebug(
        Sf2SampleHeader sample,
        GeneratorValues values,
        ExtractedSampleSlice slice,
        IReadOnlyList<SoundFontModulatorDebug> modulators)
        => new(
            sample.Index,
            sample.Name,
            sample.Start,
            sample.End,
            sample.StartLoop,
            sample.EndLoop,
            sample.SampleRate,
            sample.OriginalPitch,
            sample.PitchCorrection,
            sample.SampleLink,
            sample.SampleType,
            slice.ResolvedStart,
            slice.ResolvedEnd,
            slice.ResolvedLoopStart,
            slice.ResolvedLoopEnd,
            slice.LoopDescriptor.Looping,
            values.ToDebugGenerators(),
            modulators);

    private static int DetermineReferenceSampleRate(IReadOnlyList<Sf2SampleHeader> sampleHeaders)
    {
        return DefaultReferenceSampleRate;
    }

    private static ExtractedSampleSlice PrepareSampleSliceForKh2PitchCompensation(
        ExtractedSampleSlice slice,
        Sf2SampleHeader sample,
        IReadOnlyList<Sf2SampleHeader> sampleHeaders,
        int playbackSampleRate,
        SoundFontImportOptions importOptions,
        HashSet<string> warnings)
    {
        if (playbackSampleRate <= 0)
        {
            return slice;
        }

        var primarySampleRate = ResolveStoredSampleRate(slice.SampleRate, playbackSampleRate);
        var loopStartSample = slice.LoopStartSample;
        var primaryPcm = slice.Pcm;
        short[]? secondaryPcm = slice.SecondaryPcm;
        int? secondarySampleRate = slice.SecondarySampleRate;
        if (secondaryPcm is not null)
        {
            var effectiveSecondarySampleRate = ResolveStoredSampleRate(secondarySampleRate ?? sample.SampleRate, primarySampleRate);
            if (effectiveSecondarySampleRate != primarySampleRate)
            {
                secondaryPcm = AudioDsp.ResampleMono(secondaryPcm, effectiveSecondarySampleRate, primarySampleRate);
            }

            secondarySampleRate = primarySampleRate;
        }

        if (importOptions.PreEqStrength > 0.0001 || importOptions.ManualLowPassHz > 20.0 || importOptions.AutoLowPass)
        {
            primaryPcm = ApplyImportConditioning(primaryPcm, primarySampleRate, primarySampleRate, importOptions);

            if (secondaryPcm is not null)
            {
                secondaryPcm = ApplyImportConditioning(secondaryPcm, primarySampleRate, primarySampleRate, importOptions);
            }
        }

        if (primarySampleRate != playbackSampleRate ||
            (secondarySampleRate.HasValue && secondarySampleRate.Value != playbackSampleRate))
        {
            warnings.Add($"SoundFont sample rates are preserved during import; KH2 pitch compensation is applied instead of early {playbackSampleRate} Hz normalization.");
        }

        if (importOptions.PreEqStrength > 0.0001)
        {
            warnings.Add($"SoundFont import EQ is enabled: sf2_pre_eq={importOptions.PreEqStrength:0.###}.");
        }

        if (importOptions.ManualLowPassHz > 20.0)
        {
            warnings.Add($"SoundFont import low-pass is enabled: sf2_pre_lowpass_hz={importOptions.ManualLowPassHz:0.###}.");
        }
        else if (importOptions.AutoLowPass)
        {
            warnings.Add("SoundFont import low-pass auto mode is enabled: when samples are explicitly resampled, they are filtered near their original bandwidth.");
        }

        return slice with
        {
            Pcm = primaryPcm,
            SecondaryPcm = secondaryPcm,
            LoopDescriptor = slice.LoopDescriptor.WithSampleStart(loopStartSample, primaryPcm.Length),
            SampleRate = primarySampleRate,
            SecondarySampleRate = secondaryPcm is null ? null : primarySampleRate,
        };
    }

    private static int ResolveStoredSampleRate(int sourceSampleRate, int fallbackSampleRate)
    {
        if (sourceSampleRate > 0)
        {
            return sourceSampleRate;
        }

        return fallbackSampleRate > 0 ? fallbackSampleRate : DefaultReferenceSampleRate;
    }

    private static short[] ApplyImportConditioning(
        short[] pcm,
        int sourceSampleRate,
        int referenceSampleRate,
        SoundFontImportOptions importOptions)
    {
        if (pcm.Length == 0)
        {
            return [];
        }

        var effectiveLowPassHz = importOptions.ManualLowPassHz > 20.0
            ? importOptions.ManualLowPassHz
            : GetAutoLowPassHz(sourceSampleRate, referenceSampleRate, importOptions.AutoLowPass);

        if (importOptions.PreEqStrength <= 0.0001 && effectiveLowPassHz <= 20.0)
        {
            return pcm;
        }

        return AudioDsp.ApplyPreEncodeConditioning(pcm, referenceSampleRate, importOptions.PreEqStrength, effectiveLowPassHz);
    }

    private static InitialFilterBake ResolveInitialFilterBake(int? initialFilterFcCents, int sampleRate)
    {
        if (!initialFilterFcCents.HasValue || sampleRate <= 80)
        {
            return new InitialFilterBake(initialFilterFcCents, 0.0, false);
        }

        var cents = Math.Clamp(initialFilterFcCents.Value, MinInitialFilterFcCents, MaxInitialFilterFcCents);
        var cutoffHz = 8.176 * Math.Pow(2.0, cents / 1200.0);
        var maxUsefulCutoffHz = (sampleRate * 0.5) - 20.0;
        if (maxUsefulCutoffHz <= 20.0 ||
            cents >= DefaultInitialFilterFcCents ||
            cutoffHz >= maxUsefulCutoffHz)
        {
            return new InitialFilterBake(cents, cutoffHz, false);
        }

        return new InitialFilterBake(cents, Math.Clamp(cutoffHz, 20.0, maxUsefulCutoffHz), true);
    }

    private static ExtractedSampleSlice ApplyInitialFilterBake(
        ExtractedSampleSlice slice,
        InitialFilterBake initialFilter,
        HashSet<string> warnings)
    {
        if (!initialFilter.Applies)
        {
            return slice;
        }

        warnings.Add("SoundFont initialFilterFc is baked into region PCM before PSX ADPCM encoding for closer Polyphone/VLC tone.");

        var suffix = $"|ifc={initialFilter.Cents.GetValueOrDefault()}|lp={(int)Math.Round(initialFilter.CutoffHz, MidpointRounding.AwayFromZero)}";
        return slice with
        {
            IdentityKey = slice.IdentityKey + suffix,
            SecondaryIdentityKey = slice.SecondaryIdentityKey is null ? null : slice.SecondaryIdentityKey + suffix,
            Pcm = AudioDsp.ApplyLowPassFilter(slice.Pcm, slice.SampleRate, initialFilter.CutoffHz),
            SecondaryPcm = slice.SecondaryPcm is null
                ? null
                : AudioDsp.ApplyLowPassFilter(slice.SecondaryPcm, slice.SecondarySampleRate ?? slice.SampleRate, initialFilter.CutoffHz),
        };
    }

    private static double GetAutoLowPassHz(int sourceSampleRate, int referenceSampleRate, bool autoLowPassEnabled)
    {
        if (!autoLowPassEnabled || sourceSampleRate <= 0 || sourceSampleRate >= referenceSampleRate)
        {
            return 0.0;
        }

        return Math.Clamp(sourceSampleRate * 0.45, 1000.0, (referenceSampleRate * 0.5) - 20.0);
    }

    private static (short[] PrimaryPcm, short[]? SecondaryPcm, int TrimmedTailSamples) StabilizeNormalizedLoopEnd(
        short[] primaryPcm,
        short[]? secondaryPcm,
        int loopStartSample)
    {
        if (primaryPcm.Length == 0)
        {
            return (primaryPcm, secondaryPcm, 0);
        }

        var sharedLength = secondaryPcm is null
            ? primaryPcm.Length
            : Math.Min(primaryPcm.Length, secondaryPcm.Length);
        var trimSamples = sharedLength % 28;
        if (trimSamples == 0)
        {
            return (primaryPcm, secondaryPcm, 0);
        }

        var targetLength = sharedLength - trimSamples;
        if (targetLength - loopStartSample < 28)
        {
            return (primaryPcm, secondaryPcm, 0);
        }

        var trimmedPrimary = TrimSlice(primaryPcm, targetLength);
        var trimmedSecondary = secondaryPcm is null
            ? null
            : TrimSlice(secondaryPcm, targetLength);
        return (trimmedPrimary, trimmedSecondary, trimSamples);
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
        var sliceEnd = end;
        if (sliceEnd <= start)
        {
            warnings.Add($"Sample {sample.Name} resolved to an empty slice after generator offsets.");
            return null;
        }

        var pcm = CopySlice(sampleData, start, sliceEnd);
        var loopStartSample = looping ? loopStart - start : 0;
        var loopLengthSamples = looping ? loopEnd - loopStart : 0;
        var identityKey = $"{sample.Index}:{start}:{sliceEnd}:{loopStart}:{loopEnd}:{looping}:sr={sample.SampleRate}";
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

            var linkedSliceEnd = linkedEnd;
            if (linkedSliceEnd > linkedStart)
            {
                var linkedPcm = CopySlice(sampleData, linkedStart, linkedSliceEnd);
                var pairLength = Math.Min(pcm.Length, linkedPcm.Length);
                if (pairLength > 0)
                {
                    var pairedLoopStart = Math.Clamp(loopStartSample, 0, Math.Max(0, pairLength - 1));
                    var pairedLoopLength = Math.Clamp(loopLengthSamples, 0, Math.Max(0, pairLength - pairedLoopStart));
                    pcm = TrimSlice(pcm, pairLength);
                    linkedPcm = TrimSlice(linkedPcm, pairLength);
                    identityKey = $"stereo:L:{Math.Min(sample.Index, linked.Index)}:{Math.Max(sample.Index, linked.Index)}:{start}:{sliceEnd}:{linkedStart}:{linkedSliceEnd}:{looping}:sr={sample.SampleRate}:sr2={linked.SampleRate}";
                    sourceName = sample.Name;
                    return new ExtractedSampleSlice(
                        identityKey,
                        sourceName,
                        pcm,
                        $"stereo:R:{Math.Min(sample.Index, linked.Index)}:{Math.Max(sample.Index, linked.Index)}:{start}:{sliceEnd}:{linkedStart}:{linkedSliceEnd}:{looping}:sr={sample.SampleRate}:sr2={linked.SampleRate}",
                        linked.Name,
                        linkedPcm,
                        sample.SampleRate,
                        linked.SampleRate,
                        LoopDescriptor.FromSampleLength(looping, pairedLoopStart, pairedLoopLength),
                        start,
                        sliceEnd,
                        loopStart,
                        loopEnd);
                }
            }
        }

        var safeLoopStart = Math.Clamp(loopStartSample, 0, Math.Max(0, pcm.Length - 1));
        var safeLoopLength = Math.Clamp(loopLengthSamples, 0, Math.Max(0, pcm.Length - safeLoopStart));
        return new ExtractedSampleSlice(
            identityKey,
            sourceName,
            pcm,
            null,
            null,
            null,
            sample.SampleRate,
            null,
            LoopDescriptor.FromSampleLength(looping, safeLoopStart, safeLoopLength),
            start,
            sliceEnd,
            loopStart,
            loopEnd);
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

                foreach (var partitioned in CollapsePseudoStereoPairs(velocityCandidates
                    .Select(region => region with { KeyLow = keyLow, KeyHigh = keyHigh, VelocityLow = velocityLow, VelocityHigh = velocityHigh })
                    .OrderBy(static region => region.KeyHigh)
                    .ThenBy(static region => region.VelocityHigh)
                    .ThenBy(static region => region.SourceSampleName, StringComparer.Ordinal)
                    .ThenBy(static region => region.StereoSourceSampleName ?? string.Empty, StringComparer.Ordinal)
                    .ToList()))
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

    private static List<SoundFontRegion> CollapsePseudoStereoPairs(List<SoundFontRegion> regions)
    {
        if (regions.Count < 2)
        {
            return regions;
        }

        var result = new List<SoundFontRegion>(regions.Count);
        var used = new bool[regions.Count];
        for (var index = 0; index < regions.Count; index++)
        {
            if (used[index])
            {
                continue;
            }

            var current = regions[index];
            var pairIndex = -1;
            for (var candidateIndex = index + 1; candidateIndex < regions.Count; candidateIndex++)
            {
                if (used[candidateIndex])
                {
                    continue;
                }

                var candidate = regions[candidateIndex];
                if (!CanCollapsePseudoStereoPair(current, candidate))
                {
                    continue;
                }

                pairIndex = candidateIndex;
                break;
            }

            if (pairIndex < 0)
            {
                result.Add(current);
                continue;
            }

            used[index] = true;
            used[pairIndex] = true;
            var left = current.Pan <= regions[pairIndex].Pan ? current : regions[pairIndex];
            var right = ReferenceEquals(left, current) ? regions[pairIndex] : current;
            result.Add(left with
            {
                StereoIdentityKey = right.IdentityKey,
                StereoSourceSampleName = right.SourceSampleName,
                Pan = (left.Pan + right.Pan) / 2f,
                StereoPcm = right.Pcm,
            });
        }

        return result;
    }

    private static bool CanCollapsePseudoStereoPair(SoundFontRegion left, SoundFontRegion right)
    {
        if (left.StereoPcm is not null || right.StereoPcm is not null ||
            left.StereoIdentityKey is not null || right.StereoIdentityKey is not null)
        {
            return false;
        }

        if (left.IdentityKey == right.IdentityKey)
        {
            return false;
        }

        if (left.KeyLow != right.KeyLow ||
            left.KeyHigh != right.KeyHigh ||
            left.VelocityLow != right.VelocityLow ||
            left.VelocityHigh != right.VelocityHigh ||
            left.SamplePitch != right.SamplePitch ||
            left.RegionPitch != right.RegionPitch ||
            left.InitialFilterFcCents != right.InitialFilterFcCents ||
            Math.Abs(left.InitialFilterCutoffHz - right.InitialFilterCutoffHz) >= 0.001 ||
            left.LoopDescriptor != right.LoopDescriptor)
        {
            return false;
        }

        if (Math.Abs(left.Volume - right.Volume) >= 0.0001f ||
            Math.Abs(left.ReverbSend - right.ReverbSend) >= 0.0001f ||
            Math.Abs(left.AttackSeconds - right.AttackSeconds) >= 0.0001 ||
            Math.Abs(left.HoldSeconds - right.HoldSeconds) >= 0.0001 ||
            Math.Abs(left.DecaySeconds - right.DecaySeconds) >= 0.0001 ||
            Math.Abs(left.SustainLevel - right.SustainLevel) >= 0.0001f ||
            Math.Abs(left.ReleaseSeconds - right.ReleaseSeconds) >= 0.0001)
        {
            return false;
        }

        return left.Pan < 0f &&
               right.Pan > 0f &&
               Math.Abs(left.Pan + right.Pan) < 0.0001f;
    }

    private static bool CanMerge(SoundFontRegion left, SoundFontRegion right)
    {
        return left.KeyHigh + 1 == right.KeyLow &&
               left.VelocityLow == right.VelocityLow &&
               left.VelocityHigh == right.VelocityHigh &&
               left.IdentityKey == right.IdentityKey &&
               left.StereoIdentityKey == right.StereoIdentityKey &&
               left.SamplePitch == right.SamplePitch &&
               left.RegionPitch == right.RegionPitch &&
               Math.Abs(left.Volume - right.Volume) < 0.0001f &&
               Math.Abs(left.Pan - right.Pan) < 0.0001f &&
               Math.Abs(left.ReverbSend - right.ReverbSend) < 0.0001f &&
               Math.Abs(left.AttackSeconds - right.AttackSeconds) < 0.0001 &&
               Math.Abs(left.HoldSeconds - right.HoldSeconds) < 0.0001 &&
               Math.Abs(left.DecaySeconds - right.DecaySeconds) < 0.0001 &&
               Math.Abs(left.SustainLevel - right.SustainLevel) < 0.0001f &&
               Math.Abs(left.ReleaseSeconds - right.ReleaseSeconds) < 0.0001 &&
               left.LoopDescriptor == right.LoopDescriptor;
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

    private static string GetGeneratorName(int op)
        => op switch
        {
            0 => "startAddrsOffset",
            1 => "endAddrsOffset",
            2 => "startloopAddrsOffset",
            3 => "endloopAddrsOffset",
            4 => "startAddrsCoarseOffset",
            5 => "modLfoToPitch",
            6 => "vibLfoToPitch",
            7 => "modEnvToPitch",
            8 => "initialFilterFc",
            9 => "initialFilterQ",
            10 => "modLfoToFilterFc",
            11 => "modEnvToFilterFc",
            12 => "endAddrsCoarseOffset",
            13 => "modLfoToVolume",
            15 => "chorusEffectsSend",
            16 => "reverbEffectsSend",
            17 => "pan",
            21 => "delayModLFO",
            22 => "freqModLFO",
            23 => "delayVibLFO",
            24 => "freqVibLFO",
            25 => "delayModEnv",
            26 => "attackModEnv",
            27 => "holdModEnv",
            28 => "decayModEnv",
            29 => "sustainModEnv",
            30 => "releaseModEnv",
            31 => "keynumToModEnvHold",
            32 => "keynumToModEnvDecay",
            33 => "delayVolEnv",
            34 => "attackVolEnv",
            35 => "holdVolEnv",
            36 => "decayVolEnv",
            37 => "sustainVolEnv",
            38 => "releaseVolEnv",
            39 => "keynumToVolEnvHold",
            40 => "keynumToVolEnvDecay",
            41 => "instrument",
            43 => "keyRange",
            44 => "velRange",
            45 => "startloopAddrsCoarseOffset",
            46 => "keynum",
            47 => "velocity",
            48 => "initialAttenuation",
            50 => "endloopAddrsCoarseOffset",
            51 => "coarseTune",
            52 => "fineTune",
            53 => "sampleID",
            54 => "sampleModes",
            56 => "scaleTuning",
            57 => "exclusiveClass",
            58 => "overridingRootKey",
            _ => "unknown",
        };

    private static string FormatModulatorOperator(ushort value)
    {
        var sourceType = (value >> 10) & 0x3F;
        var polarity = (value & 0x0200) != 0 ? "bipolar" : "unipolar";
        var direction = (value & 0x0100) != 0 ? "negative" : "positive";
        var isMidiController = (value & 0x0080) != 0;
        var index = value & 0x007F;
        var source = isMidiController
            ? $"midi_cc_{index}"
            : index switch
            {
                0 => "none",
                2 => "noteOnVelocity",
                3 => "noteOnKeyNumber",
                10 => "polyPressure",
                13 => "channelPressure",
                14 => "pitchWheel",
                16 => "pitchWheelSensitivity",
                _ => $"general_{index}",
            };

        return $"0x{value:X4} {source} {polarity} {direction} type={sourceType}";
    }

    private sealed class GeneratorValues
    {
        private int? _startAddrsOffset;
        private int? _endAddrsOffset;
        private int? _startLoopAddrsOffset;
        private int? _endLoopAddrsOffset;
        private int? _startAddrsCoarseOffset;
        private int? _endAddrsCoarseOffset;
        private int? _startLoopAddrsCoarseOffset;
        private int? _endLoopAddrsCoarseOffset;
        private int? _keynum;
        private int? _fixedVelocity;
        private int? _keyLow;
        private int? _keyHigh;
        private int? _velocityLow;
        private int? _velocityHigh;
        private int? _initialAttenuationCentibels;
        private int? _initialFilterFcCents;
        private int? _panTenthsPercent;
        private int? _reverbEffectsSendTenthsPercent;
        private int? _coarseTuneSemitones;
        private int? _fineTuneCents;
        private int? _sampleModes;
        private int? _scaleTuningCentsPerKey;
        private readonly List<SoundFontGeneratorDebug> _auditGenerators = [];

        public int StartAddrsOffset => _startAddrsOffset ?? 0;
        public int EndAddrsOffset => _endAddrsOffset ?? 0;
        public int StartLoopAddrsOffset => _startLoopAddrsOffset ?? 0;
        public int EndLoopAddrsOffset => _endLoopAddrsOffset ?? 0;
        public int StartAddrsCoarseOffset => _startAddrsCoarseOffset ?? 0;
        public int EndAddrsCoarseOffset => _endAddrsCoarseOffset ?? 0;
        public int StartLoopAddrsCoarseOffset => _startLoopAddrsCoarseOffset ?? 0;
        public int EndLoopAddrsCoarseOffset => _endLoopAddrsCoarseOffset ?? 0;
        public int? Keynum => _keynum;
        public int? FixedVelocity => _fixedVelocity;
        public int KeyLow => _keyLow ?? 0;
        public int KeyHigh => _keyHigh ?? 127;
        public int VelocityLow => _velocityLow ?? 0;
        public int VelocityHigh => _velocityHigh ?? 127;
        public int? SampleId { get; private set; }
        public int InitialAttenuationCentibels => _initialAttenuationCentibels ?? 0;
        public int? InitialFilterFcCents => _initialFilterFcCents;
        public int PanTenthsPercent => _panTenthsPercent ?? 0;
        public int ReverbEffectsSendTenthsPercent => _reverbEffectsSendTenthsPercent ?? 0;
        public int CoarseTuneSemitones => _coarseTuneSemitones ?? 0;
        public int FineTuneCents => _fineTuneCents ?? 0;
        public int SampleModes => _sampleModes ?? 0;
        public int ScaleTuningCentsPerKey => _scaleTuningCentsPerKey ?? 100;
        public int? OverridingRootKey { get; private set; }
        public int? AttackVolEnvTimecents { get; private set; }
        public int? HoldVolEnvTimecents { get; private set; }
        public int? DecayVolEnvTimecents { get; private set; }
        public int? SustainVolEnvCentibels { get; private set; }
        public int? ReleaseVolEnvTimecents { get; private set; }

        public void Apply(Sf2Generator generator, HashSet<string> warnings)
        {
            var signedAmount = unchecked((short)generator.Amount);
            _auditGenerators.Add(new SoundFontGeneratorDebug(
                generator.Operator,
                GetGeneratorName(generator.Operator),
                FormatRawGeneratorValue(generator.Operator, generator.Amount, signedAmount),
                "raw"));
            switch (generator.Operator)
            {
                case 0:
                    Add(ref _startAddrsOffset, signedAmount);
                    break;
                case 1:
                    Add(ref _endAddrsOffset, signedAmount);
                    break;
                case 2:
                    Add(ref _startLoopAddrsOffset, signedAmount);
                    break;
                case 3:
                    Add(ref _endLoopAddrsOffset, signedAmount);
                    break;
                case 4:
                    Add(ref _startAddrsCoarseOffset, signedAmount);
                    break;
                case 8:
                    _initialFilterFcCents = signedAmount;
                    break;
                case 12:
                    Add(ref _endAddrsCoarseOffset, signedAmount);
                    break;
                case 17:
                    Add(ref _panTenthsPercent, signedAmount);
                    break;
                case 16:
                    Add(ref _reverbEffectsSendTenthsPercent, signedAmount);
                    break;
                case 43:
                    _keyLow = generator.Amount & 0xFF;
                    _keyHigh = generator.Amount >> 8;
                    break;
                case 44:
                    _velocityLow = generator.Amount & 0xFF;
                    _velocityHigh = generator.Amount >> 8;
                    break;
                case 45:
                    Add(ref _startLoopAddrsCoarseOffset, signedAmount);
                    break;
                case 46:
                    _keynum = signedAmount;
                    warnings.Add("SoundFont generator 46 (keynum) is imported for manifest audit only; fixed-key playback is not converted to WD playback.");
                    break;
                case 47:
                    _fixedVelocity = signedAmount;
                    warnings.Add("SoundFont generator 47 (velocity) is imported for manifest audit only; fixed-velocity playback is not converted to WD playback.");
                    break;
                case 48:
                    Add(ref _initialAttenuationCentibels, signedAmount);
                    break;
                case 50:
                    Add(ref _endLoopAddrsCoarseOffset, signedAmount);
                    break;
                case 34:
                    AttackVolEnvTimecents = signedAmount;
                    break;
                case 35:
                    HoldVolEnvTimecents = signedAmount;
                    break;
                case 36:
                    DecayVolEnvTimecents = signedAmount;
                    break;
                case 37:
                    SustainVolEnvCentibels = signedAmount;
                    break;
                case 38:
                    ReleaseVolEnvTimecents = signedAmount;
                    break;
                case 51:
                    Add(ref _coarseTuneSemitones, signedAmount);
                    break;
                case 52:
                    Add(ref _fineTuneCents, signedAmount);
                    break;
                case 54:
                    _sampleModes = generator.Amount;
                    break;
                case 56:
                    _scaleTuningCentsPerKey = signedAmount;
                    break;
                case 58:
                    OverridingRootKey = generator.Amount & 0xFF;
                    break;
                case 13:
                case 57:
                    warnings.Add($"Ignored SoundFont generator {generator.Operator} ({GetGeneratorName(generator.Operator)}) during conversion.");
                    break;
                default:
                    if (generator.Operator is not 41 and not 53)
                    {
                        warnings.Add($"Ignored SoundFont generator {generator.Operator} ({GetGeneratorName(generator.Operator)}) during conversion.");
                    }

                    break;
            }
        }

        public static GeneratorValues MergeWithinDomain(GeneratorValues? globalValues, GeneratorValues? localValues)
        {
            var merged = new GeneratorValues();
            if (globalValues is not null)
            {
                CopyTo(globalValues, merged);
            }

            if (localValues is not null)
            {
                OverrideIfSet(ref merged._startAddrsOffset, localValues._startAddrsOffset);
                OverrideIfSet(ref merged._endAddrsOffset, localValues._endAddrsOffset);
                OverrideIfSet(ref merged._startLoopAddrsOffset, localValues._startLoopAddrsOffset);
                OverrideIfSet(ref merged._endLoopAddrsOffset, localValues._endLoopAddrsOffset);
                OverrideIfSet(ref merged._startAddrsCoarseOffset, localValues._startAddrsCoarseOffset);
                OverrideIfSet(ref merged._endAddrsCoarseOffset, localValues._endAddrsCoarseOffset);
                OverrideIfSet(ref merged._startLoopAddrsCoarseOffset, localValues._startLoopAddrsCoarseOffset);
                OverrideIfSet(ref merged._endLoopAddrsCoarseOffset, localValues._endLoopAddrsCoarseOffset);
                OverrideIfSet(ref merged._keynum, localValues._keynum);
                OverrideIfSet(ref merged._fixedVelocity, localValues._fixedVelocity);
                OverrideIfSet(ref merged._initialAttenuationCentibels, localValues._initialAttenuationCentibels);
                OverrideIfSet(ref merged._initialFilterFcCents, localValues._initialFilterFcCents);
                OverrideIfSet(ref merged._panTenthsPercent, localValues._panTenthsPercent);
                OverrideIfSet(ref merged._reverbEffectsSendTenthsPercent, localValues._reverbEffectsSendTenthsPercent);
                OverrideIfSet(ref merged._coarseTuneSemitones, localValues._coarseTuneSemitones);
                OverrideIfSet(ref merged._fineTuneCents, localValues._fineTuneCents);
                OverrideIfSet(ref merged._scaleTuningCentsPerKey, localValues._scaleTuningCentsPerKey);
                merged.AttackVolEnvTimecents = localValues.AttackVolEnvTimecents ?? merged.AttackVolEnvTimecents;
                merged.HoldVolEnvTimecents = localValues.HoldVolEnvTimecents ?? merged.HoldVolEnvTimecents;
                merged.DecayVolEnvTimecents = localValues.DecayVolEnvTimecents ?? merged.DecayVolEnvTimecents;
                merged.SustainVolEnvCentibels = localValues.SustainVolEnvCentibels ?? merged.SustainVolEnvCentibels;
                merged.ReleaseVolEnvTimecents = localValues.ReleaseVolEnvTimecents ?? merged.ReleaseVolEnvTimecents;
                OverrideIfSet(ref merged._sampleModes, localValues._sampleModes);
                merged.SampleId = localValues.SampleId ?? merged.SampleId;
                merged.OverridingRootKey = localValues.OverridingRootKey ?? merged.OverridingRootKey;
                OverrideIfSet(ref merged._keyLow, localValues._keyLow);
                OverrideIfSet(ref merged._keyHigh, localValues._keyHigh);
                OverrideIfSet(ref merged._velocityLow, localValues._velocityLow);
                OverrideIfSet(ref merged._velocityHigh, localValues._velocityHigh);
                merged._auditGenerators.AddRange(localValues._auditGenerators);
            }

            return merged;
        }

        public static GeneratorValues MergeAcrossDomains(GeneratorValues? presetValues, GeneratorValues? instrumentValues)
        {
            var merged = new GeneratorValues();
            if (presetValues is not null)
            {
                CopyTo(presetValues, merged);
            }

            if (instrumentValues is not null)
            {
                merged._startAddrsOffset = (presetValues?.StartAddrsOffset ?? 0) + instrumentValues.StartAddrsOffset;
                merged._endAddrsOffset = (presetValues?.EndAddrsOffset ?? 0) + instrumentValues.EndAddrsOffset;
                merged._startLoopAddrsOffset = (presetValues?.StartLoopAddrsOffset ?? 0) + instrumentValues.StartLoopAddrsOffset;
                merged._endLoopAddrsOffset = (presetValues?.EndLoopAddrsOffset ?? 0) + instrumentValues.EndLoopAddrsOffset;
                merged._startAddrsCoarseOffset = (presetValues?.StartAddrsCoarseOffset ?? 0) + instrumentValues.StartAddrsCoarseOffset;
                merged._endAddrsCoarseOffset = (presetValues?.EndAddrsCoarseOffset ?? 0) + instrumentValues.EndAddrsCoarseOffset;
                merged._startLoopAddrsCoarseOffset = (presetValues?.StartLoopAddrsCoarseOffset ?? 0) + instrumentValues.StartLoopAddrsCoarseOffset;
                merged._endLoopAddrsCoarseOffset = (presetValues?.EndLoopAddrsCoarseOffset ?? 0) + instrumentValues.EndLoopAddrsCoarseOffset;
                merged._keynum = instrumentValues._keynum ?? presetValues?._keynum;
                merged._fixedVelocity = instrumentValues._fixedVelocity ?? presetValues?._fixedVelocity;
                merged._initialAttenuationCentibels = (presetValues?.InitialAttenuationCentibels ?? 0) + instrumentValues.InitialAttenuationCentibels;
                if (instrumentValues._initialFilterFcCents.HasValue || presetValues?._initialFilterFcCents.HasValue == true)
                {
                    merged._initialFilterFcCents = Math.Clamp(
                        (instrumentValues._initialFilterFcCents ?? DefaultInitialFilterFcCents) +
                        (presetValues?._initialFilterFcCents ?? 0),
                        MinInitialFilterFcCents,
                        MaxInitialFilterFcCents);
                }

                merged._panTenthsPercent = (presetValues?.PanTenthsPercent ?? 0) + instrumentValues.PanTenthsPercent;
                merged._reverbEffectsSendTenthsPercent = (presetValues?.ReverbEffectsSendTenthsPercent ?? 0) + instrumentValues.ReverbEffectsSendTenthsPercent;
                merged._coarseTuneSemitones = (presetValues?.CoarseTuneSemitones ?? 0) + instrumentValues.CoarseTuneSemitones;
                merged._fineTuneCents = (presetValues?.FineTuneCents ?? 0) + instrumentValues.FineTuneCents;
                merged._scaleTuningCentsPerKey = Math.Clamp((instrumentValues._scaleTuningCentsPerKey ?? 100) + (presetValues?._scaleTuningCentsPerKey ?? 0), 0, 1200);
                merged._sampleModes = instrumentValues._sampleModes ?? presetValues?._sampleModes ?? 0;
                merged.SampleId = instrumentValues.SampleId ?? merged.SampleId;
                merged.OverridingRootKey = instrumentValues.OverridingRootKey ?? merged.OverridingRootKey;
                merged._keyLow = Math.Max(presetValues?.KeyLow ?? 0, instrumentValues.KeyLow);
                merged._keyHigh = Math.Min(presetValues?.KeyHigh ?? 127, instrumentValues.KeyHigh);
                merged._velocityLow = Math.Max(presetValues?.VelocityLow ?? 0, instrumentValues.VelocityLow);
                merged._velocityHigh = Math.Min(presetValues?.VelocityHigh ?? 127, instrumentValues.VelocityHigh);
                merged._auditGenerators.AddRange(instrumentValues._auditGenerators);
            }
            else
            {
                merged._scaleTuningCentsPerKey = Math.Clamp((presetValues?._scaleTuningCentsPerKey ?? 0) + 100, 0, 1200);
                merged._sampleModes = presetValues?._sampleModes ?? 0;
                merged._keynum = presetValues?._keynum;
                merged._fixedVelocity = presetValues?._fixedVelocity;
                if (presetValues?._initialFilterFcCents.HasValue == true)
                {
                    merged._initialFilterFcCents = Math.Clamp(
                        DefaultInitialFilterFcCents + presetValues._initialFilterFcCents.Value,
                        MinInitialFilterFcCents,
                        MaxInitialFilterFcCents);
                }
            }

            merged.AttackVolEnvTimecents = (instrumentValues?.AttackVolEnvTimecents ?? -12000) + (presetValues?.AttackVolEnvTimecents ?? 0);
            merged.HoldVolEnvTimecents = (instrumentValues?.HoldVolEnvTimecents ?? -12000) + (presetValues?.HoldVolEnvTimecents ?? 0);
            merged.DecayVolEnvTimecents = (instrumentValues?.DecayVolEnvTimecents ?? -12000) + (presetValues?.DecayVolEnvTimecents ?? 0);
            merged.SustainVolEnvCentibels = (instrumentValues?.SustainVolEnvCentibels ?? 0) + (presetValues?.SustainVolEnvCentibels ?? 0);
            merged.ReleaseVolEnvTimecents = (instrumentValues?.ReleaseVolEnvTimecents ?? -12000) + (presetValues?.ReleaseVolEnvTimecents ?? 0);

            return merged;
        }

        private static void CopyTo(GeneratorValues source, GeneratorValues destination)
        {
            destination._startAddrsOffset = source._startAddrsOffset;
            destination._endAddrsOffset = source._endAddrsOffset;
            destination._startLoopAddrsOffset = source._startLoopAddrsOffset;
            destination._endLoopAddrsOffset = source._endLoopAddrsOffset;
            destination._startAddrsCoarseOffset = source._startAddrsCoarseOffset;
            destination._endAddrsCoarseOffset = source._endAddrsCoarseOffset;
            destination._startLoopAddrsCoarseOffset = source._startLoopAddrsCoarseOffset;
            destination._endLoopAddrsCoarseOffset = source._endLoopAddrsCoarseOffset;
            destination._keynum = source._keynum;
            destination._fixedVelocity = source._fixedVelocity;
            destination._keyLow = source._keyLow;
            destination._keyHigh = source._keyHigh;
            destination._velocityLow = source._velocityLow;
            destination._velocityHigh = source._velocityHigh;
            destination.SampleId = source.SampleId;
            destination._initialAttenuationCentibels = source._initialAttenuationCentibels;
            destination._initialFilterFcCents = source._initialFilterFcCents;
            destination._panTenthsPercent = source._panTenthsPercent;
            destination._reverbEffectsSendTenthsPercent = source._reverbEffectsSendTenthsPercent;
            destination._coarseTuneSemitones = source._coarseTuneSemitones;
            destination._fineTuneCents = source._fineTuneCents;
            destination._sampleModes = source._sampleModes;
            destination._scaleTuningCentsPerKey = source._scaleTuningCentsPerKey;
            destination._auditGenerators.Clear();
            destination._auditGenerators.AddRange(source._auditGenerators);
            destination.AttackVolEnvTimecents = source.AttackVolEnvTimecents;
            destination.HoldVolEnvTimecents = source.HoldVolEnvTimecents;
            destination.DecayVolEnvTimecents = source.DecayVolEnvTimecents;
            destination.SustainVolEnvCentibels = source.SustainVolEnvCentibels;
            destination.ReleaseVolEnvTimecents = source.ReleaseVolEnvTimecents;
            destination.OverridingRootKey = source.OverridingRootKey;
        }

        private static void Add(ref int? target, int amount)
        {
            target = (target ?? 0) + amount;
        }

        private static void OverrideIfSet(ref int? target, int? source)
        {
            if (source.HasValue)
            {
                target = source.Value;
            }
        }

        public IReadOnlyList<SoundFontGeneratorDebug> ToDebugGenerators()
        {
            var output = new List<SoundFontGeneratorDebug>(_auditGenerators);
            AddDebug(output, 0, _startAddrsOffset);
            AddDebug(output, 1, _endAddrsOffset);
            AddDebug(output, 2, _startLoopAddrsOffset);
            AddDebug(output, 3, _endLoopAddrsOffset);
            AddDebug(output, 4, _startAddrsCoarseOffset);
            AddDebug(output, 12, _endAddrsCoarseOffset);
            AddDebug(output, 45, _startLoopAddrsCoarseOffset);
            AddDebug(output, 50, _endLoopAddrsCoarseOffset);
            if (_keyLow.HasValue || _keyHigh.HasValue)
            {
                output.Add(new SoundFontGeneratorDebug(43, GetGeneratorName(43), $"{KeyLow}-{KeyHigh}"));
            }

            if (_velocityLow.HasValue || _velocityHigh.HasValue)
            {
                output.Add(new SoundFontGeneratorDebug(44, GetGeneratorName(44), $"{VelocityLow}-{VelocityHigh}"));
            }

            AddDebug(output, 46, _keynum, "audit-only");
            AddDebug(output, 47, _fixedVelocity, "audit-only");
            AddDebug(output, 48, _initialAttenuationCentibels, "centibels");
            AddDebug(output, 8, _initialFilterFcCents, "absolute cents");
            AddDebug(output, 16, _reverbEffectsSendTenthsPercent, "0.1%");
            AddDebug(output, 17, _panTenthsPercent, "0.1%");
            AddDebug(output, 51, _coarseTuneSemitones, "semitones");
            AddDebug(output, 52, _fineTuneCents, "cents");
            AddDebug(output, 54, _sampleModes);
            AddDebug(output, 56, _scaleTuningCentsPerKey, "cents/key");
            AddDebug(output, 58, OverridingRootKey);
            AddDebug(output, 34, AttackVolEnvTimecents, "timecents");
            AddDebug(output, 35, HoldVolEnvTimecents, "timecents");
            AddDebug(output, 36, DecayVolEnvTimecents, "timecents");
            AddDebug(output, 37, SustainVolEnvCentibels, "centibels");
            AddDebug(output, 38, ReleaseVolEnvTimecents, "timecents");
            return output;
        }

        private static void AddDebug(List<SoundFontGeneratorDebug> output, int op, int? value, string unit = "")
        {
            if (!value.HasValue)
            {
                return;
            }

            var valueText = unit.Length == 0
                ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} {unit}";
            output.Add(new SoundFontGeneratorDebug(op, GetGeneratorName(op), valueText));
        }

        private static string FormatRawGeneratorValue(ushort op, ushort unsignedValue, short signedValue)
            => op switch
            {
                43 or 44 => $"{unsignedValue & 0xFF}-{unsignedValue >> 8}",
                _ => $"signed={signedValue.ToString(System.Globalization.CultureInfo.InvariantCulture)} unsigned=0x{unsignedValue:X4}",
            };
    }

    private sealed record Sf2PresetHeader(string Name, int Program, int Bank, int BagIndex);

    private sealed record Sf2InstrumentHeader(string Name, int BagIndex);

    private sealed record Sf2Bag(int GeneratorIndex, int ModulatorIndex);

    private sealed record Sf2Generator(ushort Operator, ushort Amount);

    private sealed record Sf2Modulator(
        ushort SourceOperator,
        ushort DestinationOperator,
        short Amount,
        ushort AmountSourceOperator,
        ushort TransformOperator)
    {
        public bool IsTerminal =>
            SourceOperator == 0 &&
            DestinationOperator == 0 &&
            Amount == 0 &&
            AmountSourceOperator == 0 &&
            TransformOperator == 0;

        public SoundFontModulatorDebug ToDebug(string domain, string zoneKind, int index)
            => new(
                domain,
                zoneKind,
                index,
                SourceOperator,
                FormatModulatorOperator(SourceOperator),
                DestinationOperator,
                GetGeneratorName(DestinationOperator),
                Amount,
                AmountSourceOperator,
                FormatModulatorOperator(AmountSourceOperator),
                TransformOperator);
    }

    private sealed record Sf2PresetZone(int? InstrumentIndex, GeneratorValues Values, IReadOnlyList<SoundFontModulatorDebug> Modulators);

    private sealed record Sf2InstrumentZone(int? SampleId, GeneratorValues Values, IReadOnlyList<SoundFontModulatorDebug> Modulators);

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

    private readonly record struct InitialFilterBake(int? Cents, double CutoffHz, bool Applies);

    private sealed record ExtractedSampleSlice(
        string IdentityKey,
        string SourceSampleName,
        short[] Pcm,
        string? SecondaryIdentityKey,
        string? SecondarySourceSampleName,
        short[]? SecondaryPcm,
        int SampleRate,
        int? SecondarySampleRate,
        LoopDescriptor LoopDescriptor,
        int ResolvedStart,
        int ResolvedEnd,
        int ResolvedLoopStart,
        int ResolvedLoopEnd)
    {
        public bool Looping => LoopDescriptor.Looping;

        public int LoopStartSample => LoopDescriptor.ResolveStartSamples(Pcm.Length);
    }
}

internal sealed record SoundFontFile(
    string FilePath,
    List<SoundFontPreset> Presets,
    List<string> Warnings)
{
    public SoundFontPreset? FindPreset(int bank, int program)
    {
        return Presets.FirstOrDefault(preset => preset.Bank == bank && preset.Program == program);
    }

    public SoundFontPreset? FindPresetExactOrCoarse(int bank, int program)
    {
        var exact = FindPreset(bank, program);
        if (exact is not null)
        {
            return exact;
        }

        var coarseBank = bank & ~0x7F;
        return coarseBank != bank ? FindPreset(coarseBank, program) : null;
    }

    public SoundFontPreset? FindPresetByMidiMsbBank(int bank, int program)
    {
        if (bank <= 0)
        {
            return null;
        }

        var msbBank = bank >> 7;
        if (msbBank <= 0 || msbBank == bank)
        {
            return null;
        }

        return FindPreset(msbBank, program);
    }

    public SoundFontPreset? FindPercussionFallbackPreset(int requestedProgram)
    {
        return FindPreset(128, 0)
            ?? Presets
                .Where(static preset => preset.Bank == 128)
                .OrderBy(preset => Math.Abs(preset.Program - requestedProgram))
                .ThenBy(static preset => preset.Program)
                .FirstOrDefault();
    }
}

internal sealed record SoundFontPreset(
    string Name,
    int Bank,
    int Program,
    List<SoundFontRegion> Regions);

internal sealed record SoundFontImportOptions(double PreEqStrength, double ManualLowPassHz, bool AutoLowPass)
{
    public static SoundFontImportOptions Default { get; } = new(0.0, 0.0, false);
}

internal sealed record SoundFontRegionDebug(
    int SampleIndex,
    string SampleName,
    int ShdrStart,
    int ShdrEnd,
    int ShdrStartLoop,
    int ShdrEndLoop,
    int ShdrSampleRate,
    int ShdrOriginalPitch,
    int ShdrPitchCorrectionCents,
    int ShdrSampleLink,
    int ShdrSampleType,
    int ResolvedStart,
    int ResolvedEnd,
    int ResolvedLoopStart,
    int ResolvedLoopEnd,
    bool ResolvedLooping,
    IReadOnlyList<SoundFontGeneratorDebug> ResolvedGenerators,
    IReadOnlyList<SoundFontModulatorDebug> Modulators);

internal sealed record SoundFontGeneratorDebug(
    int Operator,
    string Name,
    string Value,
    string Kind = "resolved");

internal sealed record SoundFontModulatorDebug(
    string Domain,
    string ZoneKind,
    int Index,
    int SourceOperator,
    string SourceOperatorDescription,
    int DestinationOperator,
    string DestinationOperatorName,
    int Amount,
    int AmountSourceOperator,
    string AmountSourceOperatorDescription,
    int TransformOperator);

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
    SamplePitchComponents SamplePitch,
    RegionPitchComponents RegionPitch,
    float Volume,
    float Pan,
    float ReverbSend,
    double AttackSeconds,
    double HoldSeconds,
    double DecaySeconds,
    float SustainLevel,
    double ReleaseSeconds,
    int? InitialFilterFcCents,
    double InitialFilterCutoffHz,
    int SampleRate,
    LoopDescriptor LoopDescriptor,
    short[] Pcm,
    short[]? StereoPcm,
    SoundFontRegionDebug Debug)
{
    public bool Looping => LoopDescriptor.Looping;

    public int LoopStartSample => LoopDescriptor.ResolveStartSamples(Pcm.Length);

    public double SampleBaseRootNoteSemitones => SamplePitch.SourceRootNoteSemitones;

    public double RegionPitchOffsetSemitones => RegionPitch.ResolveOffsetFromSourcePitch(SamplePitch);

    public int RootKey => ResolveCanonicalPitch().RootKey;

    public int FineTuneCents => ResolveCanonicalPitch().FineTuneCents;

    private (int RootKey, int FineTuneCents) ResolveCanonicalPitch()
    {
        var rootNote = RegionPitch.ResolveEffectiveRootNoteSemitones(SamplePitch);
        var rootKey = (int)Math.Round(rootNote, MidpointRounding.AwayFromZero);
        var fineTuneCents = (int)Math.Round((rootNote - rootKey) * 100.0, MidpointRounding.AwayFromZero);
        while (fineTuneCents > 50)
        {
            rootKey++;
            fineTuneCents -= 100;
        }

        while (fineTuneCents < -50)
        {
            rootKey--;
            fineTuneCents += 100;
        }

        return (Math.Clamp(rootKey, 0, 127), fineTuneCents);
    }
}
