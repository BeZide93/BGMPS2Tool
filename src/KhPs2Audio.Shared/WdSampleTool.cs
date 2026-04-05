using System.Text;
using System.Text.Json;

namespace KhPs2Audio.Shared;

public static class WdSampleTool
{
    private const int RawSampleRate = 44_100;
    private const int PreviewReferenceMidiKey = 60;

    public static string ExportForBgm(string bgmPath, TextWriter log)
    {
        var bgmInfo = BgmParser.Parse(bgmPath);
        var wdPath = WdLocator.FindForBgm(bgmInfo)
            ?? throw new FileNotFoundException("No matching .wd file found for the requested .bgm.", bgmInfo.FilePath);
        return ExportRawSamples(bgmInfo.FilePath, wdPath, log);
    }

    public static string ExportPreviewForBgm(string bgmPath, TextWriter log)
    {
        var bgmInfo = BgmParser.Parse(bgmPath);
        var wdPath = WdLocator.FindForBgm(bgmInfo)
            ?? throw new FileNotFoundException("No matching .wd file found for the requested .bgm.", bgmInfo.FilePath);
        return ExportPreviewSamples(bgmInfo.FilePath, wdPath, log);
    }

    public static string ExportForSeb(string sebPath, TextWriter log)
    {
        var fullPath = Path.GetFullPath(sebPath);
        var wdPath = WdLocator.FindForSeb(fullPath)
            ?? throw new FileNotFoundException("No matching .wd file found for the requested .seb.", fullPath);
        return ExportRawSamples(fullPath, wdPath, log);
    }

    public static string ExportPreviewForSeb(string sebPath, TextWriter log)
    {
        var fullPath = Path.GetFullPath(sebPath);
        var wdPath = WdLocator.FindForSeb(fullPath)
            ?? throw new FileNotFoundException("No matching .wd file found for the requested .seb.", fullPath);
        return ExportPreviewSamples(fullPath, wdPath, log);
    }

    public static string InjectForBgm(string bgmPath, string sampleDirectory, TextWriter log)
    {
        var bgmInfo = BgmParser.Parse(bgmPath);
        var wdPath = WdLocator.FindForBgm(bgmInfo)
            ?? throw new FileNotFoundException("No matching .wd file found for the requested .bgm.", bgmInfo.FilePath);
        return InjectSamples(bgmInfo.FilePath, wdPath, sampleDirectory, "ps2-rebuild", log);
    }

    public static string InjectForSeb(string sebPath, string sampleDirectory, TextWriter log)
    {
        var fullPath = Path.GetFullPath(sebPath);
        var wdPath = WdLocator.FindForSeb(fullPath)
            ?? throw new FileNotFoundException("No matching .wd file found for the requested .seb.", fullPath);
        return InjectSamples(fullPath, wdPath, sampleDirectory, "ps2-rebuild", log);
    }

    public static string InjectForSeb(string sebPath, string sampleDirectory, string outputSubdirectory, TextWriter log)
    {
        var fullPath = Path.GetFullPath(sebPath);
        var wdPath = WdLocator.FindForSeb(fullPath)
            ?? throw new FileNotFoundException("No matching .wd file found for the requested .seb.", fullPath);
        return InjectSamples(fullPath, wdPath, sampleDirectory, outputSubdirectory, log);
    }

    private static string ExportRawSamples(string assetPath, string wdPath, TextWriter log)
    {
        var bank = WdBankFile.Load(wdPath);
        var outputDirectory = Path.Combine(Path.GetDirectoryName(assetPath)!, Path.GetFileNameWithoutExtension(assetPath), "wd-samples");
        Directory.CreateDirectory(outputDirectory);

        foreach (var sample in bank.Samples)
        {
            var outputPath = Path.Combine(outputDirectory, $"{sample.Index:D3}.wav");
            WaveWriter.WriteMonoPcm16(outputPath, sample.Pcm, RawSampleRate);
        }

        var manifest = CreateManifest(assetPath, wdPath, bank, previewMode: false);
        var manifestPath = Path.Combine(outputDirectory, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        log.WriteLine($"Exported {bank.Samples.Count} raw WD sample WAVs to: {outputDirectory}");
        log.WriteLine($"Manifest: {manifestPath}");
        return outputDirectory;
    }

    private static string ExportPreviewSamples(string assetPath, string wdPath, TextWriter log)
    {
        var bank = WdBankFile.Load(wdPath);
        var outputDirectory = Path.Combine(Path.GetDirectoryName(assetPath)!, Path.GetFileNameWithoutExtension(assetPath), "ps2-preview");
        Directory.CreateDirectory(outputDirectory);

        foreach (var sample in bank.Samples)
        {
            var outputPath = Path.Combine(outputDirectory, $"{sample.Index:D3}.wav");
            var previewRegion = sample.GetPrimaryRegion();
            var previewRate = GetPreviewSampleRate(previewRegion);
            var previewVolume = previewRegion?.Volume ?? 1f;
            var previewPan = previewRegion?.Pan ?? 0f;

            if (Math.Abs(previewPan) < 0.001f)
            {
                WaveWriter.WriteMonoPcm16(outputPath, Scale(sample.Pcm, previewVolume), previewRate);
            }
            else
            {
                CreateStereoPreview(sample.Pcm, previewVolume, previewPan, out var left, out var right);
                WaveWriter.WriteStereoPcm16(outputPath, left, right, previewRate);
            }
        }

        var manifest = CreateManifest(assetPath, wdPath, bank, previewMode: true);
        var manifestPath = Path.Combine(outputDirectory, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        log.WriteLine($"Exported {bank.Samples.Count} PS2 preview WAVs to: {outputDirectory}");
        log.WriteLine("Preview WAVs apply WD unity-key/fine-tune/volume/pan for more natural standalone playback.");
        log.WriteLine("Use wd-samples, not ps2-preview, as the editable source for roundtrip reinjection.");
        log.WriteLine($"Manifest: {manifestPath}");
        return outputDirectory;
    }

    private static string InjectSamples(string assetPath, string wdPath, string sampleDirectory, string outputSubdirectory, TextWriter log)
    {
        var bank = WdBankFile.Load(wdPath);
        var replacementDirectory = Path.GetFullPath(sampleDirectory);
        if (!Directory.Exists(replacementDirectory))
        {
            throw new DirectoryNotFoundException($"Replacement directory not found: {replacementDirectory}");
        }

        var replacementCount = 0;
        foreach (var sample in bank.Samples)
        {
            var replacementPath = Path.Combine(replacementDirectory, $"{sample.Index:D3}.wav");
            if (!File.Exists(replacementPath))
            {
                continue;
            }

            var pcm = WaveReader.ReadMonoPcm16(replacementPath);
            var loopStartBytes = sample.GetSuggestedLoopStartBytes();
            var looping = sample.Regions.Any(region => region.LoopStartBytes > 0);
            sample.SetReplacement(PsxAdpcmEncoder.Encode(pcm, looping, loopStartBytes));
            replacementCount++;
            log.WriteLine($"Queued replacement: {Path.GetFileName(replacementPath)} -> sample {sample.Index:D3}");
        }

        var rebuiltWd = bank.BuildPatchedFile(log);
        var outputDirectory = Path.Combine(Path.GetDirectoryName(assetPath)!, Path.GetFileNameWithoutExtension(assetPath), outputSubdirectory);
        Directory.CreateDirectory(outputDirectory);

        var assetOutputPath = Path.Combine(outputDirectory, Path.GetFileName(assetPath));
        var wdOutputPath = Path.Combine(outputDirectory, Path.GetFileName(wdPath));
        File.Copy(assetPath, assetOutputPath, overwrite: true);
        File.WriteAllBytes(wdOutputPath, rebuiltWd);

        log.WriteLine($"Applied {replacementCount} replacement sample(s).");
        log.WriteLine($"Wrote rebuilt pair to: {outputDirectory}");
        return outputDirectory;
    }

    private static WdSampleManifest CreateManifest(string assetPath, string wdPath, WdBankFile bank, bool previewMode)
    {
        return new WdSampleManifest(
            Path.GetFileName(assetPath),
            Path.GetFileName(wdPath),
            bank.BankId,
            previewMode ? "preview" : "raw",
            bank.Samples.Select(sample =>
            {
                var previewRegion = sample.GetPrimaryRegion();
                return new WdSampleManifestEntry(
                    sample.Index,
                    $"{sample.Index:D3}.wav",
                    sample.RelativeOffset,
                    sample.RawBytes.Length,
                    sample.Looping,
                    RawSampleRate,
                    GetPreviewSampleRate(previewRegion),
                    previewRegion is null ? null : Math.Round(previewRegion.UnityKey + (previewRegion.FineTuneCents / 100.0), 2),
                    sample.Regions.Select(region => new WdSampleManifestRegion(
                        region.InstrumentIndex,
                        region.RegionIndex,
                        region.KeyLow,
                        region.KeyHigh,
                        region.VelocityLow,
                        region.VelocityHigh,
                        region.LoopStartBytes,
                        region.Stereo,
                        region.UnityKey,
                        region.FineTuneCents,
                        Math.Round(region.Volume, 4),
                        Math.Round(region.Pan, 4))).ToList());
            }).ToList());
    }

    internal static int GetPreviewSampleRate(WdRegionEntry? region)
    {
        if (region is null)
        {
            return RawSampleRate;
        }

        var rootNote = region.UnityKey + (region.FineTuneCents / 100.0);
        var rate = RawSampleRate * Math.Pow(2.0, (PreviewReferenceMidiKey - rootNote) / 12.0);
        return Math.Clamp((int)Math.Round(rate, MidpointRounding.AwayFromZero), 4_000, 96_000);
    }

    private static float[] Scale(IReadOnlyList<float> source, float gain)
    {
        if (Math.Abs(gain - 1f) < 0.0001f)
        {
            return source as float[] ?? source.ToArray();
        }

        var output = new float[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            output[i] = source[i] * gain;
        }

        return output;
    }

    private static void CreateStereoPreview(IReadOnlyList<float> source, float volume, float pan, out float[] left, out float[] right)
    {
        left = new float[source.Count];
        right = new float[source.Count];

        var clampedPan = Math.Clamp(pan, -1f, 1f);
        var leftGain = volume * (clampedPan <= 0f ? 1f : 1f - clampedPan);
        var rightGain = volume * (clampedPan >= 0f ? 1f : 1f + clampedPan);

        for (var i = 0; i < source.Count; i++)
        {
            left[i] = source[i] * leftGain;
            right[i] = source[i] * rightGain;
        }
    }

    internal static int ConvertWdFineTune(byte rawFineTune)
    {
        return (int)Math.Round((rawFineTune / 255.0 * 100.0) - 50.0, MidpointRounding.AwayFromZero);
    }

    internal static float ConvertWdPan(byte rawPan)
    {
        if (rawPan == 255)
        {
            return 1f;
        }

        if (rawPan == 128)
        {
            return 0f;
        }

        if (rawPan == 192)
        {
            return 0.5f;
        }

        if (rawPan > 127)
        {
            return (rawPan - 128) / 127f;
        }

        return 0.5f;
    }
}

public sealed record WdSampleManifest(
    string AssetFile,
    string WdFile,
    int BankId,
    string ExportMode,
    List<WdSampleManifestEntry> Samples);

public sealed record WdSampleManifestEntry(
    int Index,
    string FileName,
    int RelativeOffset,
    int LengthBytes,
    bool Looping,
    int RawSampleRate,
    int SuggestedPreviewSampleRate,
    double? SuggestedRootNote,
    List<WdSampleManifestRegion> Regions);

public sealed record WdSampleManifestRegion(
    int InstrumentIndex,
    int RegionIndex,
    int KeyLow,
    int KeyHigh,
    int VelocityLow,
    int VelocityHigh,
    int LoopStartBytes,
    bool Stereo,
    int UnityKey,
    int FineTuneCents,
    double Volume,
    double Pan);

internal sealed class WdBankFile
{
    private WdBankFile(string filePath, byte[] originalBytes, int bankId, int sampleCollectionOffset, List<WdSampleEntry> samples, List<WdRegionEntry> regions)
    {
        FilePath = filePath;
        OriginalBytes = originalBytes;
        BankId = bankId;
        SampleCollectionOffset = sampleCollectionOffset;
        Samples = samples;
        Regions = regions;
    }

    public string FilePath { get; }
    public byte[] OriginalBytes { get; }
    public int BankId { get; }
    public int SampleCollectionOffset { get; }
    public List<WdSampleEntry> Samples { get; }
    public List<WdRegionEntry> Regions { get; }

    public static WdBankFile Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var data = File.ReadAllBytes(fullPath);
        if (!BinaryHelpers.ReadAscii(data, 0, 2).Equals("WD", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unexpected WD magic.");
        }

        var bankId = BinaryHelpers.ReadUInt16LE(data, 0x2);
        var instrumentCount = checked((int)BinaryHelpers.ReadUInt32LE(data, 0x8));
        var totalRegions = checked((int)BinaryHelpers.ReadUInt32LE(data, 0xC));
        var sampleCollectionOffset = checked((int)(BinaryHelpers.ReadUInt32LE(data, 0x20) + (uint)(totalRegions * 0x20)));

        var regions = new List<WdRegionEntry>();
        for (var instrumentIndex = 0; instrumentIndex < instrumentCount; instrumentIndex++)
        {
            var instrumentStart = checked((int)BinaryHelpers.ReadUInt32LE(data, 0x20 + (instrumentIndex * 4)));
            var instrumentEnd = instrumentIndex < instrumentCount - 1
                ? checked((int)BinaryHelpers.ReadUInt32LE(data, 0x20 + ((instrumentIndex + 1) * 4)))
                : sampleCollectionOffset;

            var instrumentRegions = new List<WdRegionEntry>();
            var regionIndex = 0;
            for (var offset = instrumentStart; offset < instrumentEnd; offset += 0x20)
            {
                var stereo = (data[offset] & 0x1) != 0;
                var flags = data[offset + 1];
                var first = (flags & 0x1) != 0;
                var last = (flags & 0x2) != 0;
                var sampleOffset = checked((int)(BinaryHelpers.ReadUInt32LE(data, offset + 0x4) & 0xFFFFFFF0));
                var loopStartBytes = checked((int)BinaryHelpers.ReadUInt32LE(data, offset + 0x8));
                var unityKey = 0x3A - BinaryHelpers.ReadSByte(data, offset + 0x13);
                var keyHigh = data[offset + 0x14];
                var velocityHigh = data[offset + 0x15];
                var fineTuneCents = WdSampleTool.ConvertWdFineTune(data[offset + 0x12]);
                var volume = data[offset + 0x16] / 127f;
                var pan = WdSampleTool.ConvertWdPan(data[offset + 0x17]);
                var adsr1 = BinaryHelpers.ReadUInt16LE(data, offset + 0x0E);
                var adsr2 = BinaryHelpers.ReadUInt16LE(data, offset + 0x10);

                instrumentRegions.Add(new WdRegionEntry(offset, instrumentIndex, regionIndex, sampleOffset, loopStartBytes, stereo, first, last, 0, keyHigh, 0, velocityHigh, unityKey, fineTuneCents, volume, pan, adsr1, adsr2));
                regionIndex++;
            }

            for (var i = 0; i < instrumentRegions.Count; i++)
            {
                var region = instrumentRegions[i];
                var previous = i > 0 ? instrumentRegions[i - 1] : null;
                var keyLow = region.First
                    ? 0
                    : previous is null
                        ? 0
                        : region.KeyHigh == previous.KeyHigh
                            ? previous.KeyLow
                            : previous.KeyHigh + 1;
                var keyHigh = region.Last ? 0x7F : region.KeyHigh;
                var velocityLow = region.First
                    ? 0
                    : previous is null
                        ? 0
                        : region.KeyHigh == previous.KeyHigh
                            ? previous.VelocityHigh + 1
                            : 0;
                velocityLow = Math.Clamp(velocityLow, 0, 127);
                var velocityHigh = Math.Clamp(region.VelocityHigh, velocityLow, 127);
                instrumentRegions[i] = region with { KeyLow = keyLow, KeyHigh = keyHigh, VelocityLow = velocityLow, VelocityHigh = velocityHigh };
            }

            regions.AddRange(instrumentRegions);
        }

        var samples = new List<WdSampleEntry>();
        var sampleMap = new Dictionary<int, WdSampleEntry>();
        var sampleOffsets = regions.Select(region => region.SampleOffset).Distinct().OrderBy(offset => offset).ToList();
        for (var index = 0; index < sampleOffsets.Count; index++)
        {
            var relativeOffset = sampleOffsets[index];
            var absoluteOffset = checked(sampleCollectionOffset + relativeOffset);
            var sampleLength = MeasureSampleLength(data, absoluteOffset);
            var rawBytes = new byte[sampleLength];
            Buffer.BlockCopy(data, absoluteOffset, rawBytes, 0, sampleLength);
            var decoded = PsxAdpcmDecoder.Decode(rawBytes);
            var sample = new WdSampleEntry(index, relativeOffset, rawBytes, decoded.Pcm);
            samples.Add(sample);
            sampleMap.Add(relativeOffset, sample);
        }

        foreach (var region in regions)
        {
            sampleMap[region.SampleOffset].Regions.Add(region);
        }

        return new WdBankFile(fullPath, data, bankId, sampleCollectionOffset, samples, regions);
    }

    public byte[] BuildPatchedFile(TextWriter log)
    {
        var outputSampleData = new List<byte>();
        for (var sampleIndex = 0; sampleIndex < Samples.Count; sampleIndex++)
        {
            var sample = Samples[sampleIndex];
            var storedSampleBytes = sample.ReplacementBytes is not null
                ? WdLayoutHelpers.CreateStoredSampleChunk(sample.ReplacementBytes)
                : sample.GetOutputBytes();
            sample.NewRelativeOffset = outputSampleData.Count;
            outputSampleData.AddRange(storedSampleBytes);
        }

        var output = new byte[SampleCollectionOffset + outputSampleData.Count];
        Buffer.BlockCopy(OriginalBytes, 0, output, 0, SampleCollectionOffset);
        outputSampleData.ToArray().CopyTo(output, SampleCollectionOffset);
        BinaryHelpers.WriteUInt32LE(output, 0x4, (uint)outputSampleData.Count);

        foreach (var sample in Samples)
        {
            foreach (var region in sample.Regions)
            {
                BinaryHelpers.WriteUInt32LE(output, region.FileOffset + 0x4, (uint)sample.NewRelativeOffset);
                var loopStartBytes = region.LoopStartBytes;
                if (sample.ReplacementBytes is not null)
                {
                    if (sample.ReplacementLoopStartBytes.HasValue)
                    {
                        loopStartBytes = sample.ReplacementLoopStartBytes.Value;
                    }

                    var maxLoopStart = Math.Max(0, sample.ReplacementBytes.Length - 0x10);
                    loopStartBytes = Math.Min(loopStartBytes & ~0xF, maxLoopStart);
                    loopStartBytes = WdLayoutHelpers.OffsetLoopStartForStoredChunk(loopStartBytes > 0, loopStartBytes);
                }

                BinaryHelpers.WriteUInt32LE(output, region.FileOffset + 0x8, (uint)loopStartBytes);
            }
        }

        log.WriteLine($"Rebuilt WD sample section: {outputSampleData.Count} bytes using KH2-style 16-byte zero lead-ins for each stored sample");
        return output;
    }

    private static int MeasureSampleLength(byte[] data, int offset)
    {
        var cursor = offset;
        while (cursor + 0x10 <= data.Length)
        {
            var flag = data[cursor + 1];
            cursor += 0x10;
            if ((flag & 0x1) != 0)
            {
                return cursor - offset;
            }
        }

        throw new InvalidDataException($"Could not determine PSX sample length at 0x{offset:X}.");
    }
}

internal static class WdLayoutHelpers
{
    public const int InterSamplePaddingBytes = 0x10;
    public static readonly byte[] InterSampleZeroPadding = new byte[InterSamplePaddingBytes];

    public static int CalculateSampleSectionLength(IReadOnlyList<byte[]> sampleChunks)
    {
        return sampleChunks.Sum(static chunk => chunk.Length);
    }

    public static byte[] CreateStoredSampleChunk(byte[] encodedSampleBytes)
    {
        var output = new byte[InterSamplePaddingBytes + encodedSampleBytes.Length];
        Buffer.BlockCopy(encodedSampleBytes, 0, output, InterSamplePaddingBytes, encodedSampleBytes.Length);
        return output;
    }

    public static int OffsetLoopStartForStoredChunk(bool looping, int loopStartBytes)
    {
        if (!looping)
        {
            return 0;
        }

        return Math.Max(InterSamplePaddingBytes, loopStartBytes + InterSamplePaddingBytes);
    }
}

internal sealed class WdSampleEntry
{
    public WdSampleEntry(int index, int relativeOffset, byte[] rawBytes, float[] pcm)
    {
        Index = index;
        RelativeOffset = relativeOffset;
        RawBytes = rawBytes;
        Pcm = pcm;
    }

    public int Index { get; }
    public int RelativeOffset { get; }
    public byte[] RawBytes { get; }
    public float[] Pcm { get; }
    public bool Looping => Regions.Any(region => region.LoopStartBytes > 0) || ReplacementLoopStartBytes.GetValueOrDefault() > 0;
    public List<WdRegionEntry> Regions { get; } = new();
    public byte[]? ReplacementBytes { get; private set; }
    public int? ReplacementLoopStartBytes { get; private set; }
    public int NewRelativeOffset { get; set; }

    public void SetReplacement(byte[] replacementBytes, int? replacementLoopStartBytes = null)
    {
        ReplacementBytes = replacementBytes;
        ReplacementLoopStartBytes = replacementLoopStartBytes;
    }

    public byte[] GetOutputBytes()
    {
        return ReplacementBytes ?? RawBytes;
    }

    public int GetSuggestedLoopStartBytes()
    {
        return Regions.Where(region => region.LoopStartBytes > 0).Select(region => region.LoopStartBytes).DefaultIfEmpty(0).Min();
    }

    public WdRegionEntry? GetPrimaryRegion()
    {
        return Regions
            .OrderByDescending(region => region.KeyHigh - region.KeyLow)
            .ThenBy(region => region.RegionIndex)
            .FirstOrDefault();
    }
}

internal sealed record WdRegionEntry(
    int FileOffset,
    int InstrumentIndex,
    int RegionIndex,
    int SampleOffset,
    int LoopStartBytes,
    bool Stereo,
    bool First,
    bool Last,
    int KeyLow,
    int KeyHigh,
    int VelocityLow,
    int VelocityHigh,
    int UnityKey,
    int FineTuneCents,
    float Volume,
    float Pan,
    ushort Adsr1,
    ushort Adsr2);

internal static class PsxAdpcmDecoder
{
    private static readonly int[] Coefficients0 = [0, 60, 115, 98, 122];
    private static readonly int[] Coefficients1 = [0, 0, -52, -55, -60];

    public static DecodedPsxSample Decode(byte[] rawBytes)
    {
        var pcm = new float[(rawBytes.Length / 0x10) * 28];
        var previous1 = 0;
        var previous2 = 0;

        for (var blockIndex = 0; blockIndex < rawBytes.Length / 0x10; blockIndex++)
        {
            var blockOffset = blockIndex * 0x10;
            var predictorShift = rawBytes[blockOffset];
            var flag = rawBytes[blockOffset + 1];
            DecodeBlock(rawBytes.AsSpan(blockOffset + 2, 14), predictorShift, ref previous1, ref previous2, pcm.AsSpan(blockIndex * 28, 28));
        }

        return new DecodedPsxSample(pcm);
    }

    private static void DecodeBlock(ReadOnlySpan<byte> source, byte predictorShift, ref int previous1, ref int previous2, Span<float> destination)
    {
        var shift = predictorShift & 0x0F;
        var filter = Math.Min((predictorShift >> 4) & 0x0F, 4);
        var coef0 = Coefficients0[filter];
        var coef1 = Coefficients1[filter];
        var sample1 = previous1;
        var sample2 = previous2;

        for (var index = 0; index < 28; index++)
        {
            var packed = source[index >> 1];
            var nibble = (index & 1) == 0 ? packed & 0x0F : packed >> 4;
            var signedNibble = (sbyte)((nibble << 4) & 0xF0) >> 4;
            var sample = (signedNibble << 12) >> shift;
            sample += ((coef0 * sample1) + (coef1 * sample2)) >> 6;
            sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
            destination[index] = sample / 32768f;
            sample2 = sample1;
            sample1 = sample;
        }

        previous1 = sample1;
        previous2 = sample2;
    }
}

internal sealed record DecodedPsxSample(float[] Pcm);

internal static class PsxAdpcmEncoder
{
    private static readonly int[] Coefficients0 = [0, 60, 115, 98, 122];
    private static readonly int[] Coefficients1 = [0, 0, -52, -55, -60];
    private const int LookaheadCandidateCount = 4;
    private const double LookaheadWeight = 0.6;

    public static byte[] Encode(short[] pcmSamples, bool looping, int loopStartBytes)
    {
        var blockCount = Math.Max(1, (pcmSamples.Length + 27) / 28);
        var output = new byte[blockCount * 0x10];
        var previous1 = 0;
        var previous2 = 0;

        Span<int> source = stackalloc int[28];
        Span<int> nextSource = stackalloc int[28];
        for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
        {
            LoadBlock(pcmSamples, blockIndex, source);

            var hasNextBlock = blockIndex + 1 < blockCount;
            if (hasNextBlock)
            {
                LoadBlock(pcmSamples, blockIndex + 1, nextSource);
            }

            var isSilentBlock = IsEffectivelySilentBlock(source);
            var block = isSilentBlock
                ? CreateSilentBlock()
                : EncodeBlock(source, hasNextBlock ? nextSource : [], previous1, previous2);
            previous1 = block.LastSample1;
            previous2 = block.LastSample2;

            var outputOffset = blockIndex * 0x10;
            output[outputOffset] = (byte)((block.Filter << 4) | block.Shift);
            var flag = 0x2;
            if (blockIndex == blockCount - 1)
            {
                flag |= 0x1;
            }

            output[outputOffset + 1] = (byte)flag;
            for (var i = 0; i < 14; i++)
            {
                output[outputOffset + 2 + i] = block.Payload[i];
            }
        }

        return output;
    }

    private static bool IsEffectivelySilentBlock(ReadOnlySpan<int> source)
    {
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void LoadBlock(short[] pcmSamples, int blockIndex, Span<int> destination)
    {
        for (var i = 0; i < 28; i++)
        {
            var sampleIndex = (blockIndex * 28) + i;
            destination[i] = sampleIndex < pcmSamples.Length ? pcmSamples[sampleIndex] : 0;
        }
    }

    private static EncodedPsxBlock EncodeBlock(ReadOnlySpan<int> source, ReadOnlySpan<int> nextSource, int previous1, int previous2)
    {
        var candidates = GetBestLocalCandidates(source, previous1, previous2);
        var primaryCandidate = candidates[0] ?? throw new InvalidOperationException("Failed to encode PSX ADPCM block.");
        if (nextSource.Length == 0)
        {
            return primaryCandidate;
        }

        EncodedPsxBlock? best = null;
        double bestScore = double.MaxValue;
        foreach (var candidate in candidates)
        {
            if (candidate is null)
            {
                continue;
            }

            var score = candidate.Error + (FindBestNextError(nextSource, candidate.LastSample1, candidate.LastSample2) * LookaheadWeight);
            if (best is null || score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best ?? throw new InvalidOperationException("Failed to encode PSX ADPCM block.");
    }

    private static EncodedPsxBlock?[] GetBestLocalCandidates(ReadOnlySpan<int> source, int previous1, int previous2)
    {
        var bestCandidates = new EncodedPsxBlock?[LookaheadCandidateCount];
        for (var filter = 0; filter <= 4; filter++)
        {
            for (var shift = 0; shift <= 12; shift++)
            {
                var candidate = TryEncodeBlock(source, previous1, previous2, filter, shift);
                InsertCandidate(bestCandidates, candidate);
            }
        }

        return bestCandidates;
    }

    private static long FindBestNextError(ReadOnlySpan<int> source, int previous1, int previous2)
    {
        long bestError = long.MaxValue;
        for (var filter = 0; filter <= 4; filter++)
        {
            for (var shift = 0; shift <= 12; shift++)
            {
                var candidate = TryEncodeBlock(source, previous1, previous2, filter, shift);
                if (candidate.Error < bestError)
                {
                    bestError = candidate.Error;
                }
            }
        }

        return bestError;
    }

    private static void InsertCandidate(EncodedPsxBlock?[] candidates, EncodedPsxBlock candidate)
    {
        for (var index = 0; index < candidates.Length; index++)
        {
            var existing = candidates[index];
            if (existing is null || candidate.Error < existing.Error)
            {
                for (var shiftIndex = candidates.Length - 1; shiftIndex > index; shiftIndex--)
                {
                    candidates[shiftIndex] = candidates[shiftIndex - 1];
                }

                candidates[index] = candidate;
                return;
            }
        }
    }

    private static EncodedPsxBlock TryEncodeBlock(ReadOnlySpan<int> source, int previous1, int previous2, int filter, int shift)
    {
        var coef0 = Coefficients0[filter];
        var coef1 = Coefficients1[filter];
        var sample1 = previous1;
        var sample2 = previous2;
        var payload = new byte[14];
        long error = 0;
        double feedbackError = 0;

        for (var index = 0; index < 28; index++)
        {
            var prediction = ((coef0 * sample1) + (coef1 * sample2)) >> 6;
            var target = source[index] + (feedbackError * 0.75);
            var scaled = (target - prediction) * (1 << shift) / 4096.0;
            var nibble = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
            nibble = Math.Clamp(nibble, -8, 7);

            var reconstructed = ((nibble << 12) >> shift) + prediction;
            reconstructed = Math.Clamp(reconstructed, short.MinValue, short.MaxValue);
            var delta = source[index] - reconstructed;
            feedbackError = delta;
            error += (long)delta * delta;

            var packedNibble = nibble & 0x0F;
            if ((index & 1) == 0)
            {
                payload[index >> 1] = (byte)packedNibble;
            }
            else
            {
                payload[index >> 1] |= (byte)(packedNibble << 4);
            }

            sample2 = sample1;
            sample1 = reconstructed;
        }

        return new EncodedPsxBlock(filter, shift, payload, sample1, sample2, error);
    }

    private static EncodedPsxBlock CreateSilentBlock()
    {
        return new EncodedPsxBlock(0, 12, new byte[14], 0, 0, 0);
    }
}

internal sealed record EncodedPsxBlock(int Filter, int Shift, byte[] Payload, int LastSample1, int LastSample2, long Error);

internal sealed record WavePcmData(
    short[] Left,
    short[] Right,
    int SampleRate,
    bool IsStereo,
    int? LoopStartSample = null,
    int? LoopEndSample = null)
{
    public bool HasLoop => LoopStartSample.HasValue && LoopEndSample.HasValue && LoopEndSample.Value > LoopStartSample.Value;
}

internal sealed record WaveLoopPoints(int StartSample, int EndSample);

internal static class WaveReader
{
    private const int TargetSampleRate = 44_100;

    public static short[] ReadMonoPcm16(string path)
    {
        var pcm = ReadStereoPcm16(path);
        if (!pcm.IsStereo)
        {
            return pcm.Left;
        }

        return AudioDsp.MixToMono(pcm.Left, pcm.Right, pcm.SampleRate);
    }

    public static WavePcmData ReadStereoPcm16(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var stream = File.OpenRead(fullPath);
        using var reader = new BinaryReader(stream);

        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException($"{fullPath} is not a RIFF/WAV file.");
        }

        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException($"{fullPath} is not a WAVE file.");
        }

        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? dataChunk = null;
        WaveLoopPoints? loopPoints = null;

        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();
            var nextChunk = reader.BaseStream.Position + chunkSize;

            if (chunkId == "fmt ")
            {
                var formatTag = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();
                if (formatTag != 1)
                {
                    throw new InvalidDataException($"{fullPath} must be PCM WAV (format 1).");
                }
            }
            else if (chunkId == "data")
            {
                dataChunk = reader.ReadBytes(chunkSize);
            }
            else if (chunkId == "smpl")
            {
                var smplChunk = reader.ReadBytes(chunkSize);
                loopPoints ??= TryParseSmplLoop(smplChunk);
            }
            else if (chunkId == "id3 ")
            {
                var id3Chunk = reader.ReadBytes(chunkSize);
                loopPoints ??= TryParseId3Loop(id3Chunk);
            }

            reader.BaseStream.Position = nextChunk + (chunkSize % 2);
        }

        if (dataChunk is null || channels < 1 || bitsPerSample != 16)
        {
            throw new InvalidDataException($"{fullPath} must be a 16-bit PCM WAV with audio data.");
        }

        var bytesPerFrame = channels * 2;
        var frameCount = dataChunk.Length / bytesPerFrame;
        var left = new short[frameCount];
        var right = new short[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = frame * bytesPerFrame;
            if (channels == 1)
            {
                var sample = BitConverter.ToInt16(dataChunk, offset);
                left[frame] = sample;
                right[frame] = sample;
            }
            else
            {
                left[frame] = BitConverter.ToInt16(dataChunk, offset);
                right[frame] = BitConverter.ToInt16(dataChunk, offset + 2);
            }
        }

        loopPoints = NormalizeLoopPoints(loopPoints, frameCount, sampleRate);

        if (sampleRate == TargetSampleRate)
        {
            return new WavePcmData(
                left,
                right,
                TargetSampleRate,
                channels > 1,
                loopPoints?.StartSample,
                loopPoints?.EndSample);
        }

        var resampledLeft = AudioDsp.ResampleMono(left, sampleRate, TargetSampleRate);
        var resampledRight = AudioDsp.ResampleMono(right, sampleRate, TargetSampleRate);
        WaveLoopPoints? resampledLoop = null;
        if (loopPoints is not null)
        {
            var start = loopPoints.StartSample;
            var end = loopPoints.EndSample;
            if (!LoopPointsAlreadyUseTargetRate(loopPoints, frameCount, sampleRate))
            {
                start = (int)Math.Round(loopPoints.StartSample * (TargetSampleRate / (double)sampleRate), MidpointRounding.AwayFromZero);
                end = (int)Math.Round(loopPoints.EndSample * (TargetSampleRate / (double)sampleRate), MidpointRounding.AwayFromZero);
            }

            if (end > start && end <= resampledLeft.Length)
            {
                resampledLoop = new WaveLoopPoints(start, end);
            }
        }

        return new WavePcmData(
            resampledLeft,
            resampledRight,
            TargetSampleRate,
            channels > 1,
            resampledLoop?.StartSample,
            resampledLoop?.EndSample);
    }

    private static WaveLoopPoints? NormalizeLoopPoints(WaveLoopPoints? loopPoints, int frameCount, int sampleRate)
    {
        if (loopPoints is null)
        {
            return null;
        }

        var start = Math.Max(0, loopPoints.StartSample);
        var end = Math.Max(start + 1, loopPoints.EndSample);
        if (LoopPointsAlreadyUseTargetRate(new WaveLoopPoints(start, end), frameCount, sampleRate))
        {
            return new WaveLoopPoints(start, end);
        }

        if (start >= frameCount)
        {
            start = Math.Max(0, frameCount - 1);
        }

        if (end > frameCount)
        {
            end = frameCount;
        }

        return end > start ? new WaveLoopPoints(start, end) : null;
    }

    private static bool LoopPointsAlreadyUseTargetRate(WaveLoopPoints loopPoints, int frameCount, int sampleRate)
    {
        if (sampleRate == TargetSampleRate)
        {
            return true;
        }

        var fileDurationSeconds = frameCount / (double)sampleRate;
        var endAsInputSeconds = loopPoints.EndSample / (double)sampleRate;
        var endAsTargetSeconds = loopPoints.EndSample / (double)TargetSampleRate;

        return Math.Abs(endAsTargetSeconds - fileDurationSeconds) <= 0.05 &&
               Math.Abs(endAsInputSeconds - fileDurationSeconds) >= 0.5;
    }

    private static WaveLoopPoints? TryParseSmplLoop(byte[] chunk)
    {
        if (chunk.Length < 0x24)
        {
            return null;
        }

        var loopCount = BitConverter.ToInt32(chunk, 0x1C);
        if (loopCount <= 0 || chunk.Length < 0x24 + 24)
        {
            return null;
        }

        var start = BitConverter.ToInt32(chunk, 0x24 + 8);
        var endInclusive = BitConverter.ToInt32(chunk, 0x24 + 12);
        var endExclusive = endInclusive + 1;
        return endExclusive > start && start >= 0
            ? new WaveLoopPoints(start, endExclusive)
            : null;
    }

    private static WaveLoopPoints? TryParseId3Loop(byte[] chunk)
    {
        if (chunk.Length < 10 || chunk[0] != (byte)'I' || chunk[1] != (byte)'D' || chunk[2] != (byte)'3')
        {
            return null;
        }

        var versionMajor = chunk[3];
        var tagSize = ReadSyncSafeInt32(chunk, 6);
        var end = Math.Min(chunk.Length, 10 + tagSize);
        var offset = 10;
        string? loopStart = null;
        string? loopEnd = null;

        while (offset + 10 <= end)
        {
            if (chunk[offset] == 0)
            {
                break;
            }

            var frameId = Encoding.ASCII.GetString(chunk, offset, 4);
            var frameSize = versionMajor switch
            {
                4 => ReadSyncSafeInt32(chunk, offset + 4),
                _ => ReadBigEndianInt32(chunk, offset + 4),
            };
            if (frameSize <= 0 || offset + 10 + frameSize > end)
            {
                break;
            }

            if (frameId == "TXXX")
            {
                ParseTxxxFrame(chunk.AsSpan(offset + 10, frameSize), out var description, out var value);
                if (description.Equals("LoopStart", StringComparison.OrdinalIgnoreCase))
                {
                    loopStart = value;
                }
                else if (description.Equals("LoopEnd", StringComparison.OrdinalIgnoreCase))
                {
                    loopEnd = value;
                }
            }

            offset += 10 + frameSize;
        }

        return int.TryParse(loopStart, out var startSample) &&
               int.TryParse(loopEnd, out var endSample) &&
               endSample > startSample &&
               startSample >= 0
            ? new WaveLoopPoints(startSample, endSample)
            : null;
    }

    private static void ParseTxxxFrame(ReadOnlySpan<byte> frameData, out string description, out string value)
    {
        description = string.Empty;
        value = string.Empty;
        if (frameData.Length == 0)
        {
            return;
        }

        var encoding = frameData[0];
        if (encoding != 0)
        {
            return;
        }

        var payload = frameData[1..];
        var separatorIndex = payload.IndexOf((byte)0);
        if (separatorIndex < 0)
        {
            return;
        }

        description = Encoding.ASCII.GetString(payload[..separatorIndex]);
        value = Encoding.ASCII.GetString(payload[(separatorIndex + 1)..]).TrimEnd('\0');
    }

    private static int ReadSyncSafeInt32(byte[] data, int offset)
    {
        return
            ((data[offset] & 0x7F) << 21) |
            ((data[offset + 1] & 0x7F) << 14) |
            ((data[offset + 2] & 0x7F) << 7) |
            (data[offset + 3] & 0x7F);
    }

    private static int ReadBigEndianInt32(byte[] data, int offset)
    {
        return
            (data[offset] << 24) |
            (data[offset + 1] << 16) |
            (data[offset + 2] << 8) |
            data[offset + 3];
    }
}
