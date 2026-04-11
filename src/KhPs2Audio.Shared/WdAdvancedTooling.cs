using System.Text.Json;

namespace KhPs2Audio.Shared;

public enum WdAdvancedLoopMode
{
    Keep = 0,
    ForceLoop = 1,
    ForceOneShot = 2,
}

public sealed record WdAdvancedRegionSummary(
    int RegionIndex,
    int SampleIndex,
    int KeyLow,
    int KeyHigh,
    int VelocityLow,
    int VelocityHigh,
    int UnityKey,
    int FineTuneCents,
    int LoopStartBytes,
    bool Looping,
    bool UsesSharedSample,
    float Volume,
    float Pan,
    int PreviewHz,
    ushort Adsr1,
    ushort Adsr2);

public sealed record WdAdvancedInstrumentSummary(
    int InstrumentIndex,
    int RegionCount,
    int SampleCount,
    bool Empty,
    bool UsesSharedSamples,
    bool Looping,
    int KeyLow,
    int KeyHigh,
    int VelocityLow,
    int VelocityHigh,
    string SummaryText,
    IReadOnlyList<WdAdvancedRegionSummary> Regions);

public sealed record WdAdvancedBankInfo(
    string WdPath,
    int BankId,
    int InstrumentCount,
    int RegionCount,
    IReadOnlyList<WdAdvancedInstrumentSummary> Instruments);

public sealed record WdAdvancedInstrumentAdjustment(
    int InstrumentIndex,
    double PitchOffsetSemitones,
    int FineTuneOffsetCents,
    double HzRetuneFrom,
    double HzRetuneTo,
    int LoopOffsetBytes,
    double VolumeMultiplier,
    int PanShiftPercent,
    WdAdvancedLoopMode LoopMode,
    ushort? Adsr1Override,
    ushort? Adsr2Override);

public sealed record WdAdvancedRegionAdjustment(
    int InstrumentIndex,
    int RegionIndex,
    double PitchOffsetSemitones,
    int FineTuneOffsetCents,
    double HzRetuneFrom,
    double HzRetuneTo,
    int LoopOffsetBytes,
    double VolumeMultiplier,
    int PanShiftPercent,
    WdAdvancedLoopMode LoopMode,
    ushort? Adsr1Override,
    ushort? Adsr2Override);

public sealed record WdAdvancedApplyResult(
    string InputWdPath,
    string OutputWdPath,
    int InstrumentCount,
    int RegionCount,
    int ModifiedRegionCount,
    int ModifiedSampleCount,
    string ManifestPath);

public static class WdAdvancedTooling
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static WdAdvancedBankInfo LoadBankInfo(string wdPath)
    {
        var fullPath = Path.GetFullPath(wdPath);
        var bank = WdBankFile.Load(fullPath);
        var instrumentCount = checked((int)BinaryHelpers.ReadUInt32LE(bank.OriginalBytes, 0x08));
        var sampleByOffset = bank.Samples.ToDictionary(static sample => sample.RelativeOffset);
        var sharedSampleOffsets = bank.Samples
            .Where(static sample => sample.Regions.Select(static region => region.InstrumentIndex).Distinct().Skip(1).Any())
            .Select(static sample => sample.RelativeOffset)
            .ToHashSet();

        var instruments = new List<WdAdvancedInstrumentSummary>(instrumentCount);
        for (var instrumentIndex = 0; instrumentIndex < instrumentCount; instrumentIndex++)
        {
            var regions = bank.Regions
                .Where(region => region.InstrumentIndex == instrumentIndex)
                .OrderBy(static region => region.RegionIndex)
                .ToList();
            var sampleOffsets = regions.Select(static region => region.SampleOffset).Distinct().ToList();
            var usesSharedSamples = sampleOffsets.Any(sharedSampleOffsets.Contains);
            var looping = regions.Any(static region => region.LoopStartBytes > 0) ||
                          sampleOffsets.Any(offset => sampleByOffset[offset].Looping);

            if (regions.Count == 0)
            {
                instruments.Add(new WdAdvancedInstrumentSummary(
                    instrumentIndex,
                    0,
                    0,
                    Empty: true,
                    UsesSharedSamples: false,
                    Looping: false,
                    KeyLow: 0,
                    KeyHigh: 0,
                    VelocityLow: 0,
                    VelocityHigh: 0,
                    SummaryText: $"Instrument {instrumentIndex:D2} is an empty WD slot.",
                    Regions: Array.Empty<WdAdvancedRegionSummary>()));
                continue;
            }

            var summaries = regions.Select(region =>
            {
                var sample = sampleByOffset[region.SampleOffset];
                return new WdAdvancedRegionSummary(
                    region.RegionIndex,
                    sample.Index,
                    region.KeyLow,
                    region.KeyHigh,
                    region.VelocityLow,
                    region.VelocityHigh,
                    region.UnityKey,
                    region.FineTuneCents,
                    region.LoopStartBytes,
                    sample.Looping || region.LoopStartBytes > 0,
                    sharedSampleOffsets.Contains(region.SampleOffset),
                    region.Volume,
                    region.Pan,
                    WdSampleTool.GetPreviewSampleRate(region),
                    region.Adsr1,
                    region.Adsr2);
            }).ToList();

            var keyLow = summaries.Min(static region => region.KeyLow);
            var keyHigh = summaries.Max(static region => region.KeyHigh);
            var velocityLow = summaries.Min(static region => region.VelocityLow);
            var velocityHigh = summaries.Max(static region => region.VelocityHigh);
            var summaryText = $"Instrument {instrumentIndex:D2}: {regions.Count} region(s), {sampleOffsets.Count} sample(s), key {keyLow}-{keyHigh}, velocity {velocityLow}-{velocityHigh}";
            if (usesSharedSamples)
            {
                summaryText += ", shared sample(s)";
            }

            instruments.Add(new WdAdvancedInstrumentSummary(
                instrumentIndex,
                regions.Count,
                sampleOffsets.Count,
                Empty: false,
                UsesSharedSamples: usesSharedSamples,
                Looping: looping,
                KeyLow: keyLow,
                KeyHigh: keyHigh,
                VelocityLow: velocityLow,
                VelocityHigh: velocityHigh,
                SummaryText: summaryText,
                Regions: summaries));
        }

        return new WdAdvancedBankInfo(
            fullPath,
            bank.BankId,
            instrumentCount,
            bank.Regions.Count,
            instruments);
    }

    public static WdAdvancedApplyResult ApplyInstrumentAdjustments(
        string wdPath,
        string outputDirectory,
        IReadOnlyCollection<WdAdvancedInstrumentAdjustment> adjustments,
        TextWriter log)
        => ApplyAdjustments(wdPath, outputDirectory, adjustments, Array.Empty<WdAdvancedRegionAdjustment>(), log);

    public static WdAdvancedApplyResult ApplyAdjustments(
        string wdPath,
        string outputDirectory,
        IReadOnlyCollection<WdAdvancedInstrumentAdjustment> instrumentAdjustments,
        IReadOnlyCollection<WdAdvancedRegionAdjustment> regionAdjustments,
        TextWriter log)
    {
        var fullWdPath = Path.GetFullPath(wdPath);
        if (!File.Exists(fullWdPath))
        {
            throw new FileNotFoundException("WD file was not found.", fullWdPath);
        }

        var bank = WdBankFile.Load(fullWdPath);
        var outputBytes = (byte[])bank.OriginalBytes.Clone();
        var instrumentCount = checked((int)BinaryHelpers.ReadUInt32LE(bank.OriginalBytes, 0x08));
        var sampleByOffset = bank.Samples.ToDictionary(static sample => sample.RelativeOffset);
        var normalizedInstrumentAdjustments = instrumentAdjustments
            .Where(static adjustment => !IsIdentity(adjustment))
            .GroupBy(static adjustment => adjustment.InstrumentIndex)
            .Select(static group => group.Last())
            .ToDictionary(static adjustment => adjustment.InstrumentIndex);
        var normalizedRegionAdjustments = regionAdjustments
            .Where(static adjustment => !IsIdentity(adjustment))
            .GroupBy(static adjustment => (adjustment.InstrumentIndex, adjustment.RegionIndex))
            .Select(static group => group.Last())
            .ToDictionary(static adjustment => (adjustment.InstrumentIndex, adjustment.RegionIndex));

        foreach (var instrumentAdjustment in normalizedInstrumentAdjustments.Values)
        {
            if (instrumentAdjustment.InstrumentIndex < 0 || instrumentAdjustment.InstrumentIndex >= instrumentCount)
            {
                throw new InvalidDataException($"Instrument {instrumentAdjustment.InstrumentIndex} is outside the WD instrument range 0..{instrumentCount - 1}.");
            }
        }

        var modifiedRegionOffsets = new HashSet<int>();
        var desiredSampleLoops = new Dictionary<int, DesiredSampleLoopEdit>();
        foreach (var region in bank.Regions.OrderBy(static region => region.InstrumentIndex).ThenBy(static region => region.RegionIndex))
        {
            normalizedInstrumentAdjustments.TryGetValue(region.InstrumentIndex, out var instrumentAdjustment);
            normalizedRegionAdjustments.TryGetValue((region.InstrumentIndex, region.RegionIndex), out var regionAdjustment);
            if (instrumentAdjustment is null && regionAdjustment is null)
            {
                continue;
            }

            var effective = BuildEffectiveRegionAdjustment(region, instrumentAdjustment, regionAdjustment);
            if (effective.RegionWriteRequested)
            {
                var rootNote = WdSampleTool.ComposeWdRootNote(region.UnityKey, region.FineTuneCents) + effective.PitchOffsetSemitones;
                var encodedPitch = WdSampleTool.EncodeWdRootNote(rootNote);
                var volume = Math.Clamp((float)(region.Volume * effective.VolumeMultiplier), 0f, 1f);
                var pan = Math.Clamp(region.Pan + effective.PanShift, -1f, 1f);
                var adsr1 = effective.Adsr1Override ?? region.Adsr1;
                var adsr2 = effective.Adsr2Override ?? region.Adsr2;

                WriteRegionPitch(outputBytes, region.FileOffset, encodedPitch.RawFineTune, encodedPitch.RawUnityKey);
                WriteRegionVolumePan(outputBytes, region.FileOffset, volume, pan);
                WriteRegionAdsr(outputBytes, region.FileOffset, adsr1, adsr2);
                modifiedRegionOffsets.Add(region.FileOffset);
            }

            if (!effective.LoopWriteRequested)
            {
                continue;
            }

            var sample = sampleByOffset[region.SampleOffset];
            var currentLoopInfo = sample.GetEffectiveLoopInfo();
            var currentLooping = currentLoopInfo.LoopDescriptor.Looping || region.LoopStartBytes > 0;
            var desiredLooping = effective.LoopMode switch
            {
                WdAdvancedLoopMode.ForceLoop => true,
                WdAdvancedLoopMode.ForceOneShot => false,
                _ => currentLooping,
            };
            var baseLoopStart = currentLoopInfo.LoopDescriptor.Looping
                ? currentLoopInfo.LoopDescriptor.Start
                : sample.GetSuggestedLoopStartBytes();
            var desiredLoopStart = desiredLooping
                ? NormalizeLoopStartBytes(baseLoopStart + effective.LoopOffsetBytes, sample.RawBytes.Length)
                : 0;
            var desiredEdit = new DesiredSampleLoopEdit(
                sample.RelativeOffset,
                desiredLooping,
                desiredLoopStart,
                $"instrument {region.InstrumentIndex:D2}/region {region.RegionIndex:D2}");
            if (desiredSampleLoops.TryGetValue(sample.RelativeOffset, out var existing) &&
                (existing.Looping != desiredEdit.Looping || existing.LoopStartBytes != desiredEdit.LoopStartBytes))
            {
                throw new InvalidDataException(
                    $"Advanced WD loop edits conflict on shared sample {sample.Index:D3}. {existing.SourceLabel} and {desiredEdit.SourceLabel} requested different loop settings.");
            }

            if (sample.Regions.Select(static sampleRegion => sampleRegion.InstrumentIndex).Distinct().Skip(1).Any())
            {
                log.WriteLine($"Advanced WD warning: sample {sample.Index:D3} is shared across instruments; loop edits from {desiredEdit.SourceLabel} will affect all linked regions.");
            }

            desiredSampleLoops[sample.RelativeOffset] = desiredEdit;
        }

        foreach (var desiredLoop in desiredSampleLoops.Values)
        {
            var sample = sampleByOffset[desiredLoop.SampleOffset];
            var rewrittenSampleBytes = RewriteLoopFlags(sample.RawBytes, desiredLoop.Looping, desiredLoop.LoopStartBytes);
            var sampleAbsoluteOffset = checked(bank.SampleCollectionOffset + sample.RelativeOffset);
            Buffer.BlockCopy(rewrittenSampleBytes, 0, outputBytes, sampleAbsoluteOffset, rewrittenSampleBytes.Length);

            foreach (var region in sample.Regions)
            {
                WriteRegionLoop(outputBytes, region.FileOffset, desiredLoop.Looping, desiredLoop.LoopStartBytes);
                modifiedRegionOffsets.Add(region.FileOffset);
            }
        }

        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);
        var outputWdPath = Path.Combine(fullOutputDirectory, Path.GetFileName(fullWdPath));
        File.WriteAllBytes(outputWdPath, outputBytes);

        var result = new WdAdvancedApplyResult(
            fullWdPath,
            outputWdPath,
            instrumentCount,
            bank.Regions.Count,
            modifiedRegionOffsets.Count,
            desiredSampleLoops.Count,
            Path.Combine(fullOutputDirectory, $"{Path.GetFileNameWithoutExtension(fullWdPath)}.advanced-edit.json"));
        File.WriteAllText(result.ManifestPath, JsonSerializer.Serialize(new
        {
            result.InputWdPath,
            result.OutputWdPath,
            result.InstrumentCount,
            result.RegionCount,
            result.ModifiedRegionCount,
            result.ModifiedSampleCount,
            InstrumentAdjustments = normalizedInstrumentAdjustments.Values.OrderBy(static adjustment => adjustment.InstrumentIndex).ToList(),
            RegionAdjustments = normalizedRegionAdjustments.Values.OrderBy(static adjustment => adjustment.InstrumentIndex).ThenBy(static adjustment => adjustment.RegionIndex).ToList()
        }, JsonOptions));

        log.WriteLine($"Advanced WD editor wrote: {result.OutputWdPath}");
        log.WriteLine($"Advanced WD modified {result.ModifiedRegionCount} region(s) and {result.ModifiedSampleCount} sample loop block set(s).");
        log.WriteLine($"Advanced WD manifest: {result.ManifestPath}");
        return result;
    }

    private static EffectiveRegionAdjustment BuildEffectiveRegionAdjustment(
        WdRegionEntry region,
        WdAdvancedInstrumentAdjustment? instrumentAdjustment,
        WdAdvancedRegionAdjustment? regionAdjustment)
    {
        var pitchOffsetSemitones = (instrumentAdjustment?.PitchOffsetSemitones ?? 0.0) +
                                   ((instrumentAdjustment?.FineTuneOffsetCents ?? 0) / 100.0) +
                                   ComputeHzRetuneSemitones(instrumentAdjustment?.HzRetuneFrom ?? 0.0, instrumentAdjustment?.HzRetuneTo ?? 0.0) +
                                   (regionAdjustment?.PitchOffsetSemitones ?? 0.0) +
                                   ((regionAdjustment?.FineTuneOffsetCents ?? 0) / 100.0) +
                                   ComputeHzRetuneSemitones(regionAdjustment?.HzRetuneFrom ?? 0.0, regionAdjustment?.HzRetuneTo ?? 0.0);
        var volumeMultiplier = SanitizeMultiplier(instrumentAdjustment?.VolumeMultiplier) * SanitizeMultiplier(regionAdjustment?.VolumeMultiplier);
        var panShift = ((instrumentAdjustment?.PanShiftPercent ?? 0) + (regionAdjustment?.PanShiftPercent ?? 0)) / 100f;
        var adsr1Override = regionAdjustment?.Adsr1Override ?? instrumentAdjustment?.Adsr1Override;
        var adsr2Override = regionAdjustment?.Adsr2Override ?? instrumentAdjustment?.Adsr2Override;
        var loopMode = regionAdjustment?.LoopMode is not null && regionAdjustment.LoopMode != WdAdvancedLoopMode.Keep
            ? regionAdjustment.LoopMode
            : instrumentAdjustment?.LoopMode ?? WdAdvancedLoopMode.Keep;
        var loopOffsetBytes = (instrumentAdjustment?.LoopOffsetBytes ?? 0) + (regionAdjustment?.LoopOffsetBytes ?? 0);
        var regionWriteRequested = Math.Abs(pitchOffsetSemitones) > 0.000001 ||
                                   Math.Abs(volumeMultiplier - 1.0) > 0.000001 ||
                                   Math.Abs(panShift) > 0.000001 ||
                                   adsr1Override is not null ||
                                   adsr2Override is not null;
        var loopWriteRequested = loopMode != WdAdvancedLoopMode.Keep || loopOffsetBytes != 0;
        return new EffectiveRegionAdjustment(
            region.InstrumentIndex,
            region.RegionIndex,
            pitchOffsetSemitones,
            volumeMultiplier,
            panShift,
            loopMode,
            loopOffsetBytes,
            adsr1Override,
            adsr2Override,
            regionWriteRequested,
            loopWriteRequested);
    }

    private static double SanitizeMultiplier(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value) || value.Value <= 0.0)
        {
            return 1.0;
        }

        return value.Value;
    }

    private static bool IsIdentity(WdAdvancedInstrumentAdjustment adjustment)
    {
        return Math.Abs(adjustment.PitchOffsetSemitones) < 0.000001 &&
               adjustment.FineTuneOffsetCents == 0 &&
               Math.Abs(ComputeHzRetuneSemitones(adjustment.HzRetuneFrom, adjustment.HzRetuneTo)) < 0.000001 &&
               adjustment.LoopOffsetBytes == 0 &&
               Math.Abs(SanitizeMultiplier(adjustment.VolumeMultiplier) - 1.0) < 0.000001 &&
               adjustment.PanShiftPercent == 0 &&
               adjustment.LoopMode == WdAdvancedLoopMode.Keep &&
               adjustment.Adsr1Override is null &&
               adjustment.Adsr2Override is null;
    }

    private static bool IsIdentity(WdAdvancedRegionAdjustment adjustment)
    {
        return Math.Abs(adjustment.PitchOffsetSemitones) < 0.000001 &&
               adjustment.FineTuneOffsetCents == 0 &&
               Math.Abs(ComputeHzRetuneSemitones(adjustment.HzRetuneFrom, adjustment.HzRetuneTo)) < 0.000001 &&
               adjustment.LoopOffsetBytes == 0 &&
               Math.Abs(SanitizeMultiplier(adjustment.VolumeMultiplier) - 1.0) < 0.000001 &&
               adjustment.PanShiftPercent == 0 &&
               adjustment.LoopMode == WdAdvancedLoopMode.Keep &&
               adjustment.Adsr1Override is null &&
               adjustment.Adsr2Override is null;
    }

    private static double ComputeHzRetuneSemitones(double hzRetuneFrom, double hzRetuneTo)
    {
        if (hzRetuneFrom <= 0.0 || hzRetuneTo <= 0.0 || !double.IsFinite(hzRetuneFrom) || !double.IsFinite(hzRetuneTo))
        {
            return 0.0;
        }

        if (Math.Abs(hzRetuneFrom - hzRetuneTo) < 0.000001)
        {
            return 0.0;
        }

        return 12.0 * Math.Log(hzRetuneTo / hzRetuneFrom, 2.0);
    }

    private static int NormalizeLoopStartBytes(int loopStartBytes, int sampleByteLength)
    {
        if (sampleByteLength < 0x10)
        {
            return 0;
        }

        var aligned = Math.Max(0, loopStartBytes) & ~0x0F;
        var maxStart = Math.Max(0, sampleByteLength - 0x10) & ~0x0F;
        return Math.Clamp(aligned, 0, maxStart);
    }

    private static void WriteRegionPitch(byte[] outputBytes, int regionOffset, byte rawFineTune, byte rawUnityKey)
    {
        outputBytes[regionOffset + 0x12] = rawFineTune;
        outputBytes[regionOffset + 0x13] = rawUnityKey;
    }

    private static void WriteRegionVolumePan(byte[] outputBytes, int regionOffset, float volume, float pan)
    {
        outputBytes[regionOffset + 0x16] = (byte)Math.Clamp((int)Math.Round(volume * 127.0, MidpointRounding.AwayFromZero), 0, 127);
        outputBytes[regionOffset + 0x17] = WdSampleTool.EncodeWdPan(pan);
    }

    private static void WriteRegionAdsr(byte[] outputBytes, int regionOffset, ushort adsr1, ushort adsr2)
    {
            BinaryHelpers.WriteUInt16LE(outputBytes, regionOffset + 0x0C, adsr1);
            BinaryHelpers.WriteUInt16LE(outputBytes, regionOffset + 0x0E, adsr2);
    }

    private static void WriteRegionLoop(byte[] outputBytes, int regionOffset, bool looping, int loopStartBytes)
    {
        BinaryHelpers.WriteUInt32LE(outputBytes, regionOffset + 0x08, looping ? (uint)loopStartBytes : 0u);
        outputBytes[regionOffset + 0x18] = looping ? (byte)0x02 : (byte)0x00;
    }

    private static byte[] RewriteLoopFlags(byte[] rawBytes, bool looping, int loopStartBytes)
    {
        var output = (byte[])rawBytes.Clone();
        var blockCount = output.Length / 0x10;
        if (blockCount <= 0)
        {
            return output;
        }

        var loopStartBlock = looping
            ? Math.Clamp(NormalizeLoopStartBytes(loopStartBytes, output.Length) / 0x10, 0, blockCount - 1)
            : -1;
        for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
        {
            var flagOffset = (blockIndex * 0x10) + 1;
            var flag = (byte)(output[flagOffset] & 0xF8);
            if (looping && blockIndex == loopStartBlock)
            {
                flag |= 0x04;
            }

            if (blockIndex == blockCount - 1)
            {
                flag |= looping ? (byte)0x03 : (byte)0x01;
            }

            output[flagOffset] = flag;
        }

        return output;
    }

    private sealed record DesiredSampleLoopEdit(
        int SampleOffset,
        bool Looping,
        int LoopStartBytes,
        string SourceLabel);

    private sealed record EffectiveRegionAdjustment(
        int InstrumentIndex,
        int RegionIndex,
        double PitchOffsetSemitones,
        double VolumeMultiplier,
        float PanShift,
        WdAdvancedLoopMode LoopMode,
        int LoopOffsetBytes,
        ushort? Adsr1Override,
        ushort? Adsr2Override,
        bool RegionWriteRequested,
        bool LoopWriteRequested);
}
