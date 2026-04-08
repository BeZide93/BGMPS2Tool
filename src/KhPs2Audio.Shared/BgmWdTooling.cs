using System.Text.Json;
using System.Text.RegularExpressions;

namespace KhPs2Audio.Shared;

public sealed record BgmProgramOffsetResult(
    string InputBgmPath,
    string OutputBgmPath,
    int InstrumentOffset,
    int TrackCount,
    int PatchedProgramCount,
    string ManifestPath);

public sealed record WdCombineResult(
    string PrimaryWdPath,
    string SecondaryWdPath,
    string CombinedWdPath,
    string? PrimaryBgmPath,
    string? SecondaryBgmPath,
    string? PrimaryBgmOutputPath,
    string? SecondaryBgmOutputPath,
    int PrimaryInstrumentCount,
    int SecondaryInstrumentCount,
    int CombinedInstrumentCount,
    int PrimaryRegionCount,
    int SecondaryRegionCount,
    int CombinedRegionCount,
    int SecondaryProgramOffset,
    int SecondaryProgramPatchedCount,
    int OutputBankId,
    string ManifestPath);

public static class BgmWdTooling
{
    public static BgmProgramOffsetResult OffsetBgmPrograms(string bgmPath, int instrumentOffset, string outputDirectory, TextWriter log)
    {
        var fullBgmPath = Path.GetFullPath(bgmPath);
        if (!File.Exists(fullBgmPath))
        {
            throw new FileNotFoundException("BGM file was not found.", fullBgmPath);
        }

        var data = File.ReadAllBytes(fullBgmPath);
        ValidateBgm(data, fullBgmPath);
        var patchSummary = PatchProgramMarkers(data, instrumentOffset);

        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);
        var outputBgmPath = Path.Combine(fullOutputDirectory, Path.GetFileName(fullBgmPath));
        File.WriteAllBytes(outputBgmPath, data);

        var result = new BgmProgramOffsetResult(
            fullBgmPath,
            outputBgmPath,
            instrumentOffset,
            patchSummary.TrackCount,
            patchSummary.PatchedProgramCount,
            Path.Combine(fullOutputDirectory, $"{Path.GetFileNameWithoutExtension(fullBgmPath)}.program-offset.json"));
        File.WriteAllText(result.ManifestPath, JsonSerializer.Serialize(result, JsonOptions));

        log.WriteLine($"Patched {result.PatchedProgramCount} BGM program marker(s) across {result.TrackCount} track(s).");
        log.WriteLine($"Instrument offset applied: {instrumentOffset:+#;-#;0}");
        log.WriteLine($"Wrote offset BGM: {outputBgmPath}");
        log.WriteLine($"Offset manifest: {result.ManifestPath}");
        return result;
    }

    public static WdCombineResult CombineBanks(
        string primaryWdPath,
        string secondaryWdPath,
        string outputDirectory,
        TextWriter log,
        string? primaryBgmPath = null,
        string? secondaryBgmPath = null)
    {
        var fullPrimaryWdPath = Path.GetFullPath(primaryWdPath);
        var fullSecondaryWdPath = Path.GetFullPath(secondaryWdPath);
        if (!File.Exists(fullPrimaryWdPath))
        {
            throw new FileNotFoundException("Primary WD file was not found.", fullPrimaryWdPath);
        }

        if (!File.Exists(fullSecondaryWdPath))
        {
            throw new FileNotFoundException("Secondary WD file was not found.", fullSecondaryWdPath);
        }

        var primaryBank = WdBankFile.Load(fullPrimaryWdPath);
        var secondaryBank = WdBankFile.Load(fullSecondaryWdPath);

        var primaryInstrumentRegions = ReadInstrumentRegionBytes(primaryBank);
        var secondaryInstrumentRegions = ReadInstrumentRegionBytes(secondaryBank);
        var primaryInstrumentCount = primaryInstrumentRegions.Count;
        var secondaryInstrumentCount = secondaryInstrumentRegions.Count;
        var combinedInstrumentCount = primaryInstrumentCount + secondaryInstrumentCount;
        if (combinedInstrumentCount > byte.MaxValue + 1)
        {
            throw new InvalidDataException($"Combined WD would contain {combinedInstrumentCount} instruments, which exceeds the BGM program byte range.");
        }

        var primarySampleMap = BuildSampleOffsetMap(primaryBank);
        var secondarySampleMap = BuildSampleOffsetMap(secondaryBank);
        var sampleData = new List<byte>();
        var remappedPrimaryOffsets = AppendSamples(primarySampleMap, sampleData);
        var remappedSecondaryOffsets = AppendSamples(secondarySampleMap, sampleData);

        var primaryRegionCount = primaryInstrumentRegions.Sum(static regions => regions.Count);
        var secondaryRegionCount = secondaryInstrumentRegions.Sum(static regions => regions.Count);
        var combinedRegionCount = primaryRegionCount + secondaryRegionCount;
        var regionTableOffset = Align16(0x20 + (combinedInstrumentCount * 4));
        var sampleCollectionOffset = regionTableOffset + (combinedRegionCount * 0x20);

        var output = new byte[sampleCollectionOffset + sampleData.Count];
        Buffer.BlockCopy(primaryBank.OriginalBytes, 0, output, 0, Math.Min(0x20, primaryBank.OriginalBytes.Length));
        BinaryHelpers.WriteUInt32LE(output, 0x04, (uint)sampleData.Count);
        BinaryHelpers.WriteUInt32LE(output, 0x08, (uint)combinedInstrumentCount);
        BinaryHelpers.WriteUInt32LE(output, 0x0C, (uint)combinedRegionCount);

        var currentRegionOffset = regionTableOffset;
        WriteInstrumentSet(output, primaryInstrumentRegions, remappedPrimaryOffsets, currentRegionOffset, 0);
        currentRegionOffset += primaryRegionCount * 0x20;
        WriteInstrumentSet(output, secondaryInstrumentRegions, remappedSecondaryOffsets, currentRegionOffset, primaryInstrumentCount);
        sampleData.CopyTo(output, sampleCollectionOffset);

        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);
        var combinedWdPath = Path.Combine(fullOutputDirectory, Path.GetFileName(fullPrimaryWdPath));
        File.WriteAllBytes(combinedWdPath, output);

        var resolvedPrimaryBgmPath = ResolveOptionalBgm(primaryBgmPath, fullPrimaryWdPath);
        var resolvedSecondaryBgmPath = ResolveOptionalBgm(secondaryBgmPath, fullSecondaryWdPath);

        string? primaryBgmOutputPath = null;
        if (resolvedPrimaryBgmPath is not null)
        {
            primaryBgmOutputPath = Path.Combine(fullOutputDirectory, Path.GetFileName(resolvedPrimaryBgmPath));
            File.Copy(resolvedPrimaryBgmPath, primaryBgmOutputPath, overwrite: true);
            log.WriteLine($"Copied primary BGM unchanged: {primaryBgmOutputPath}");
        }

        string? secondaryBgmOutputPath = null;
        var secondaryProgramPatchedCount = 0;
        if (resolvedSecondaryBgmPath is not null)
        {
            var secondaryBgmBytes = File.ReadAllBytes(resolvedSecondaryBgmPath);
            ValidateBgm(secondaryBgmBytes, resolvedSecondaryBgmPath);
            var patchSummary = PatchProgramMarkers(secondaryBgmBytes, primaryInstrumentCount);
            secondaryProgramPatchedCount = patchSummary.PatchedProgramCount;
            BinaryHelpers.WriteUInt16LE(secondaryBgmBytes, 0x06, (ushort)primaryBank.BankId);
            secondaryBgmOutputPath = Path.Combine(fullOutputDirectory, Path.GetFileName(resolvedSecondaryBgmPath));
            File.WriteAllBytes(secondaryBgmOutputPath, secondaryBgmBytes);
            log.WriteLine($"Patched secondary BGM programs by +{primaryInstrumentCount} and retargeted bank id to {primaryBank.BankId:D4}: {secondaryBgmOutputPath}");
        }

        var result = new WdCombineResult(
            fullPrimaryWdPath,
            fullSecondaryWdPath,
            combinedWdPath,
            resolvedPrimaryBgmPath,
            resolvedSecondaryBgmPath,
            primaryBgmOutputPath,
            secondaryBgmOutputPath,
            primaryInstrumentCount,
            secondaryInstrumentCount,
            combinedInstrumentCount,
            primaryRegionCount,
            secondaryRegionCount,
            combinedRegionCount,
            primaryInstrumentCount,
            secondaryProgramPatchedCount,
            primaryBank.BankId,
            Path.Combine(fullOutputDirectory, "wd-combiner-manifest.json"));
        File.WriteAllText(result.ManifestPath, JsonSerializer.Serialize(result, JsonOptions));

        log.WriteLine($"Combined WD written: {combinedWdPath}");
        log.WriteLine($"Primary instruments: {primaryInstrumentCount}, secondary instruments: {secondaryInstrumentCount}, combined: {combinedInstrumentCount}");
        log.WriteLine($"Primary regions: {primaryRegionCount}, secondary regions: {secondaryRegionCount}, combined: {combinedRegionCount}");
        log.WriteLine($"WD combiner manifest: {result.ManifestPath}");
        return result;
    }

    public static string? TryResolveMatchingBgmForWd(string wdPath)
    {
        var fullWdPath = Path.GetFullPath(wdPath);
        if (!File.Exists(fullWdPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(fullWdPath)!;
        var bankId = WdBankFile.Load(fullWdPath).BankId;
        var fileStem = Path.GetFileNameWithoutExtension(fullWdPath);
        var numericId = TryExtractNumericId(fileStem);

        var candidateNames = new List<string>();
        if (numericId.HasValue)
        {
            candidateNames.Add($"music{numericId.Value:D3}.bgm");
        }

        if (fileStem.StartsWith("wave", StringComparison.OrdinalIgnoreCase))
        {
            candidateNames.Add($"{Regex.Replace(fileStem, "^wave", "music", RegexOptions.IgnoreCase)}.bgm");
        }

        foreach (var candidateName in candidateNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidatePath = Path.Combine(directory, candidateName);
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        var byBankId = Directory.EnumerateFiles(directory, "*.bgm")
            .Select(path =>
            {
                try
                {
                    return BgmParser.Parse(path);
                }
                catch
                {
                    return null;
                }
            })
            .Where(info => info is not null && info.BankId == bankId)
            .Select(info => info!.FilePath)
            .ToList();
        if (byBankId.Count == 1)
        {
            return byBankId[0];
        }

        var byNumeric = numericId.HasValue
            ? Directory.EnumerateFiles(directory, "*.bgm")
                .Where(path => TryExtractNumericId(Path.GetFileNameWithoutExtension(path)) == numericId.Value)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        return byNumeric.Count == 1 ? byNumeric[0] : null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static void ValidateBgm(byte[] data, string path)
    {
        if (data.Length < 0x20 || !string.Equals(BinaryHelpers.ReadAscii(data, 0, 4), "BGM ", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Not a valid BGM file: {path}");
        }
    }

    private static BgmPatchSummary PatchProgramMarkers(byte[] data, int instrumentOffset)
    {
        var trackCount = data[0x08];
        var patchedProgramCount = 0;
        var cursor = 0x20;
        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            if (cursor + 4 > data.Length)
            {
                throw new InvalidDataException("BGM track table exceeds file length.");
            }

            var trackLength = checked((int)BinaryHelpers.ReadUInt32LE(data, cursor));
            var trackStart = cursor + 4;
            var trackEnd = checked(trackStart + trackLength);
            if (trackEnd > data.Length)
            {
                throw new InvalidDataException($"BGM track {trackIndex} exceeds file length.");
            }

            PatchTrack(data, trackIndex, trackStart, trackEnd, instrumentOffset, ref patchedProgramCount);
            cursor = trackEnd;
        }

        return new BgmPatchSummary(trackCount, patchedProgramCount);
    }

    private static void PatchTrack(byte[] data, int trackIndex, int start, int end, int instrumentOffset, ref int patchedProgramCount)
    {
        var offset = start;
        while (offset < end)
        {
            _ = ReadVarLen(data, ref offset, end);
            if (offset >= end)
            {
                break;
            }

            var status = data[offset++];
            switch (status)
            {
                case 0x00:
                case 0x02:
                case 0x03:
                case 0x04:
                case 0x10:
                case 0x18:
                case 0x60:
                case 0x61:
                case 0x7F:
                    break;
                case 0x08:
                case 0x0A:
                case 0x0D:
                case 0x12:
                case 0x13:
                case 0x1A:
                case 0x22:
                case 0x24:
                case 0x26:
                case 0x28:
                case 0x31:
                case 0x34:
                case 0x35:
                case 0x3C:
                case 0x3E:
                case 0x58:
                case 0x5D:
                    EnsureBytesAvailable(offset, end, 1, trackIndex, status);
                    offset += 1;
                    break;
                case 0x0C:
                case 0x11:
                case 0x19:
                case 0x47:
                case 0x5C:
                    EnsureBytesAvailable(offset, end, 2, trackIndex, status);
                    offset += 2;
                    break;
                case 0x20:
                    EnsureBytesAvailable(offset, end, 1, trackIndex, status);
                    var patchedProgram = data[offset] + instrumentOffset;
                    if (patchedProgram < byte.MinValue || patchedProgram > byte.MaxValue)
                    {
                        throw new InvalidDataException($"Program offset would move track {trackIndex} program {data[offset]} out of byte range.");
                    }

                    data[offset] = (byte)patchedProgram;
                    offset += 1;
                    patchedProgramCount++;
                    break;
                case 0x40:
                case 0x48:
                case 0x50:
                    EnsureBytesAvailable(offset, end, 3, trackIndex, status);
                    offset += 3;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported BGM opcode 0x{status:X2} in track {trackIndex} at offset 0x{offset - 1:X}.");
            }

            if (status == 0x00)
            {
                return;
            }
        }
    }

    private static void EnsureBytesAvailable(int offset, int end, int count, int trackIndex, int status)
    {
        if (offset + count > end)
        {
            throw new InvalidDataException($"Opcode 0x{status:X2} in track {trackIndex} exceeds track bounds.");
        }
    }

    private static int ReadVarLen(byte[] data, ref int offset, int end)
    {
        var value = 0;
        while (offset < end)
        {
            var current = data[offset++];
            value = (value << 7) + (current & 0x7F);
            if ((current & 0x80) == 0)
            {
                break;
            }
        }

        return value;
    }

    private static List<List<byte[]>> ReadInstrumentRegionBytes(WdBankFile bank)
    {
        var instrumentCount = checked((int)BinaryHelpers.ReadUInt32LE(bank.OriginalBytes, 0x08));
        var sampleCollectionOffset = bank.SampleCollectionOffset;
        var result = new List<List<byte[]>>(instrumentCount);
        for (var instrumentIndex = 0; instrumentIndex < instrumentCount; instrumentIndex++)
        {
            var instrumentStart = checked((int)BinaryHelpers.ReadUInt32LE(bank.OriginalBytes, 0x20 + (instrumentIndex * 4)));
            var instrumentEnd = instrumentIndex < instrumentCount - 1
                ? checked((int)BinaryHelpers.ReadUInt32LE(bank.OriginalBytes, 0x20 + ((instrumentIndex + 1) * 4)))
                : sampleCollectionOffset;
            if (instrumentEnd < instrumentStart)
            {
                throw new InvalidDataException($"WD instrument table is corrupt at instrument {instrumentIndex}.");
            }

            var regions = new List<byte[]>();
            for (var regionOffset = instrumentStart; regionOffset < instrumentEnd; regionOffset += 0x20)
            {
                var regionBytes = new byte[0x20];
                Buffer.BlockCopy(bank.OriginalBytes, regionOffset, regionBytes, 0, regionBytes.Length);
                regions.Add(regionBytes);
            }

            result.Add(regions);
        }

        return result;
    }

    private static List<WdSampleEntry> BuildSampleOffsetMap(WdBankFile bank)
    {
        return bank.Samples
            .OrderBy(static sample => sample.RelativeOffset)
            .ToList();
    }

    private static Dictionary<int, int> AppendSamples(IEnumerable<WdSampleEntry> samples, List<byte> output)
    {
        var map = new Dictionary<int, int>();
        foreach (var sample in samples)
        {
            map.Add(sample.RelativeOffset, output.Count);
            output.AddRange(sample.RawBytes);
        }

        return map;
    }

    private static void WriteInstrumentSet(
        byte[] output,
        IReadOnlyList<List<byte[]>> instruments,
        IReadOnlyDictionary<int, int> remappedSampleOffsets,
        int regionOffsetStart,
        int instrumentIndexBase)
    {
        var currentRegionOffset = regionOffsetStart;
        for (var instrumentIndex = 0; instrumentIndex < instruments.Count; instrumentIndex++)
        {
            var tableIndex = instrumentIndexBase + instrumentIndex;
            BinaryHelpers.WriteUInt32LE(output, 0x20 + (tableIndex * 4), (uint)currentRegionOffset);
            foreach (var regionBytes in instruments[instrumentIndex])
            {
                var patchedRegion = new byte[regionBytes.Length];
                Buffer.BlockCopy(regionBytes, 0, patchedRegion, 0, patchedRegion.Length);
                var oldSampleOffset = checked((int)(BinaryHelpers.ReadUInt32LE(patchedRegion, 0x04) & 0xFFFFFFF0));
                BinaryHelpers.WriteUInt32LE(patchedRegion, 0x04, (uint)remappedSampleOffsets[oldSampleOffset]);
                Buffer.BlockCopy(patchedRegion, 0, output, currentRegionOffset, patchedRegion.Length);
                currentRegionOffset += patchedRegion.Length;
            }
        }
    }

    private static string? ResolveOptionalBgm(string? explicitPath, string wdPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("BGM file was not found.", fullPath);
            }

            return fullPath;
        }

        return TryResolveMatchingBgmForWd(wdPath);
    }

    private static int Align16(int value)
    {
        return (value + 0x0F) & ~0x0F;
    }

    private static int? TryExtractNumericId(string fileStem)
    {
        var match = Regex.Match(fileStem, @"(\d{2,4})");
        return match.Success && int.TryParse(match.Groups[1].Value, out var numericId)
            ? numericId
            : null;
    }

    private sealed record BgmPatchSummary(int TrackCount, int PatchedProgramCount);
}
