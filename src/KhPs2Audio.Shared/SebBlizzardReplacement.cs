using System.Text.Json;

namespace KhPs2Audio.Shared;

public static class SebBlizzardReplacement
{
    private const int RawSampleRate = 44_100;
    private const int OneShotTemplateRootKey = 0x50;
    private const byte NeutralFineTune = 0x80;
    private const byte FullKeyRangeHigh = 0x7F;
    private const byte FullVolume = 0x7F;
    private const byte CenterPan = 0xC0;

    private static readonly byte[] OneShotScriptTemplate =
    [
        0x20, 0x03, 0x00,
        0x2C, 0x1E, 0xEE, 0x00,
        0x31, 0x1E, 0x00,
        0x39, 0x0D, 0x00, 0x00,
        0x58, 0x03, 0x00,
        0x2A, 0x00,
        0x12, OneShotTemplateRootKey, 0x32
    ];

    private static readonly SebOffsetPatch[] BlizzardEntryPatches =
    [
        new(4, 0x64),
        new(5, 0x68),
        new(6, 0x6C),
        new(7, 0x70),
        new(8, 0x74),
        new(9, 0x78),
        new(10, 0x7C),
        new(11, 0x80),
        new(12, 0x88),
        new(13, 0x8C),
        new(14, 0x90),
        new(15, 0x94),
        new(16, 0x98),
    ];

    private static readonly IReadOnlyDictionary<int, int> TemplateInstrumentMap = new Dictionary<int, int>
    {
        [4] = 4,
        [5] = 5,
        [6] = 6,
        [7] = 7,
        [8] = 8,
        [9] = 9,
        [10] = 10,
        [11] = 11,
        [12] = 12,
        [13] = 13,
        [14] = 14,
        [15] = 15,
        [16] = 16,
    };

    public static string Replace(string sebPath, string replacementPathOrDirectory, TextWriter log)
    {
        var fullSebPath = Path.GetFullPath(sebPath);
        var fullInputPath = Path.GetFullPath(replacementPathOrDirectory);
        var wdPath = WdLocator.FindForSeb(fullSebPath)
            ?? throw new FileNotFoundException("No matching .wd file found for the requested .seb.", fullSebPath);

        var replacements = ResolveReplacements(fullInputPath);
        var originalSebBytes = File.ReadAllBytes(fullSebPath);
        var bank = WdBankFile.Load(wdPath);
        var authoredReplacements = new List<SebAuthoredReplacement>();
        var manifestEntries = new List<SebBlizzardReplacementEntry>();
        var nextInstrumentIndex = bank.Regions.Max(static region => region.InstrumentIndex) + 1;

        foreach (var replacement in replacements.OrderBy(static entry => entry.Key))
        {
            var templateInstrumentIndex = TemplateInstrumentMap[replacement.Key];
            var templateRegion = bank.Regions.FirstOrDefault(region => region.InstrumentIndex == templateInstrumentIndex)
                ?? throw new InvalidDataException($"Template WD instrument {templateInstrumentIndex:D3} was not found for Blizzard slot {replacement.Key}.");
            var preparedPcm = BuildPreparedSlotPcm(replacement.Value);
            var replacementBytes = PsxAdpcmEncoder.Encode(preparedPcm, looping: false, loopStartBytes: 0);
            var authoredInstrumentIndex = nextInstrumentIndex++;

            authoredReplacements.Add(new SebAuthoredReplacement(
                replacement.Key,
                authoredInstrumentIndex,
                templateRegion,
                replacementBytes,
                Path.GetFileName(replacement.Value)));

            log.WriteLine($"Mapped Blizzard slot {replacement.Key} -> new dedicated WD instrument {authoredInstrumentIndex:D3} using {Path.GetFileName(replacement.Value)} at {RawSampleRate} Hz.");

            manifestEntries.Add(new SebBlizzardReplacementEntry(
                replacement.Key,
                Path.GetFileName(replacement.Value),
                [authoredInstrumentIndex],
                [RawSampleRate]));
        }

        var rebuiltWd = BuildExtendedWd(bank, authoredReplacements, log);
        var rebuiltSeb = BuildOneShotSeb(originalSebBytes, authoredReplacements, log);
        var outputDirectory = Path.Combine(Path.GetDirectoryName(fullSebPath)!, Path.GetFileNameWithoutExtension(fullSebPath), "blizzard-rebuild");
        Directory.CreateDirectory(outputDirectory);

        var assetOutputPath = Path.Combine(outputDirectory, Path.GetFileName(fullSebPath));
        var wdOutputPath = Path.Combine(outputDirectory, Path.GetFileName(wdPath));
        File.WriteAllBytes(assetOutputPath, rebuiltSeb);
        File.WriteAllBytes(wdOutputPath, rebuiltWd);

        var manifest = new SebBlizzardReplacementManifest(
            Path.GetFileName(fullSebPath),
            Path.GetFileName(fullInputPath),
            File.Exists(fullInputPath) ? "single-wav" : "replacement-directory",
            "Blizzard slots 4-16 are reauthored as dedicated one-shot SEB entries that point at newly appended WD instruments, leaving all original se000 instruments and sounds untouched.",
            manifestEntries.OrderBy(entry => entry.PcSlot).ToList());

        var manifestPath = Path.Combine(outputDirectory, "blizzard-replacements.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        log.WriteLine($"Blizzard replacement manifest: {manifestPath}");
        return outputDirectory;
    }

    private static Dictionary<int, string> ResolveReplacements(string inputPath)
    {
        if (File.Exists(inputPath))
        {
            if (!string.Equals(Path.GetExtension(inputPath), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("replaceblizzard currently expects a .wav file or a directory containing .wav files.");
            }

            return TemplateInstrumentMap.Keys.ToDictionary(slot => slot, _ => inputPath);
        }

        if (!Directory.Exists(inputPath))
        {
            throw new FileNotFoundException($"Replacement input was not found: {inputPath}");
        }

        var replacements = new Dictionary<int, string>();
        foreach (var slot in TemplateInstrumentMap.Keys.OrderBy(static slot => slot))
        {
            var directName = Path.Combine(inputPath, $"{slot}.wav");
            var paddedName = Path.Combine(inputPath, $"{slot:D3}.wav");
            if (File.Exists(directName))
            {
                replacements.Add(slot, directName);
            }
            else if (File.Exists(paddedName))
            {
                replacements.Add(slot, paddedName);
            }
        }

        if (replacements.Count == 0)
        {
            throw new FileNotFoundException("No Blizzard replacement WAVs were found. Expected files like 4.wav to 16.wav or 004.wav to 016.wav.");
        }

        return replacements;
    }

    private static short[] BuildPreparedSlotPcm(string replacementWavePath)
    {
        return WaveReader.ReadMonoPcm16(replacementWavePath);
    }

    private static byte[] BuildExtendedWd(WdBankFile bank, IReadOnlyList<SebAuthoredReplacement> authoredReplacements, TextWriter log)
    {
        var originalInstrumentCount = bank.Regions.Max(static region => region.InstrumentIndex) + 1;
        var instrumentCount = originalInstrumentCount + authoredReplacements.Count;
        var totalRegions = instrumentCount;
        var regionTableOffset = Align16(0x20 + (instrumentCount * 4));
        var sampleCollectionOffset = regionTableOffset + (totalRegions * 0x20);

        var originalRegionBytes = bank.Regions
            .OrderBy(static region => region.InstrumentIndex)
            .ToDictionary(
                static region => region.InstrumentIndex,
                region =>
                {
                    var bytes = new byte[0x20];
                    Buffer.BlockCopy(bank.OriginalBytes, region.FileOffset, bytes, 0, bytes.Length);
                    return bytes;
                });

        var originalInstrumentSamples = bank.Regions
            .OrderBy(static region => region.InstrumentIndex)
            .Select(region => bank.Samples.First(sample => sample.RelativeOffset == region.SampleOffset).RawBytes)
            .ToList();

        var sampleChunks = new List<byte[]>(instrumentCount);
        sampleChunks.AddRange(originalInstrumentSamples);
        sampleChunks.AddRange(authoredReplacements
            .OrderBy(static replacement => replacement.InstrumentIndex)
            .Select(static replacement => WdLayoutHelpers.CreateStoredSampleChunk(replacement.ReplacementBytes)));

        var sampleBytesLength = WdLayoutHelpers.CalculateSampleSectionLength(sampleChunks);
        var output = new byte[sampleCollectionOffset + sampleBytesLength];
        Buffer.BlockCopy(bank.OriginalBytes, 0, output, 0, Math.Min(0x20, bank.OriginalBytes.Length));
        BinaryHelpers.WriteUInt32LE(output, 0x4, (uint)sampleBytesLength);
        BinaryHelpers.WriteUInt32LE(output, 0x8, (uint)instrumentCount);
        BinaryHelpers.WriteUInt32LE(output, 0xC, (uint)totalRegions);

        var currentSampleOffset = 0;
        for (var instrumentIndex = 0; instrumentIndex < originalInstrumentCount; instrumentIndex++)
        {
            var regionOffset = regionTableOffset + (instrumentIndex * 0x20);
            BinaryHelpers.WriteUInt32LE(output, 0x20 + (instrumentIndex * 4), (uint)regionOffset);

            var regionBytes = new byte[0x20];
            Buffer.BlockCopy(originalRegionBytes[instrumentIndex], 0, regionBytes, 0, regionBytes.Length);
            BinaryHelpers.WriteUInt32LE(regionBytes, 0x04, (uint)currentSampleOffset);
            Buffer.BlockCopy(regionBytes, 0, output, regionOffset, regionBytes.Length);

            var sampleBytes = sampleChunks[instrumentIndex];
            Buffer.BlockCopy(sampleBytes, 0, output, sampleCollectionOffset + currentSampleOffset, sampleBytes.Length);
            currentSampleOffset += sampleBytes.Length;
        }

        var orderedReplacements = authoredReplacements.OrderBy(static replacement => replacement.InstrumentIndex).ToList();
        for (var replacementIndex = 0; replacementIndex < orderedReplacements.Count; replacementIndex++)
        {
            var replacement = orderedReplacements[replacementIndex];
            var regionOffset = regionTableOffset + (replacement.InstrumentIndex * 0x20);
            BinaryHelpers.WriteUInt32LE(output, 0x20 + (replacement.InstrumentIndex * 4), (uint)regionOffset);

            var regionBytes = new byte[0x20];
            Buffer.BlockCopy(bank.OriginalBytes, replacement.TemplateRegion.FileOffset, regionBytes, 0, regionBytes.Length);
            regionBytes[0x00] = 0x00;
            regionBytes[0x01] = 0x03;
            BinaryHelpers.WriteUInt32LE(regionBytes, 0x04, (uint)currentSampleOffset);
            BinaryHelpers.WriteUInt32LE(regionBytes, 0x08, 0);
            regionBytes[0x12] = NeutralFineTune;
            regionBytes[0x13] = unchecked((byte)(sbyte)(0x3A - OneShotTemplateRootKey));
            regionBytes[0x14] = FullKeyRangeHigh;
            regionBytes[0x16] = FullVolume;
            regionBytes[0x17] = CenterPan;
            Buffer.BlockCopy(regionBytes, 0, output, regionOffset, regionBytes.Length);

            var storedReplacementBytes = sampleChunks[originalInstrumentCount + replacementIndex];
            Buffer.BlockCopy(storedReplacementBytes, 0, output, sampleCollectionOffset + currentSampleOffset, storedReplacementBytes.Length);
            currentSampleOffset += storedReplacementBytes.Length;
        }

        log.WriteLine($"Authored isolated WD extension: {instrumentCount} instruments, {sampleBytesLength} bytes of PSX-ADPCM sample data using KH2-style 16-byte zero lead-ins for each sample chunk.");
        return output;
    }

    private static byte[] BuildOneShotSeb(byte[] originalSebBytes, IReadOnlyList<SebAuthoredReplacement> authoredReplacements, TextWriter log)
    {
        var output = new List<byte>(originalSebBytes.Length + (BlizzardEntryPatches.Length * OneShotScriptTemplate.Length));
        output.AddRange(originalSebBytes);
        var authoredMap = authoredReplacements.ToDictionary(static replacement => replacement.PcSlot);

        var rewrittenOffsets = new Dictionary<int, int>();
        foreach (var patch in BlizzardEntryPatches)
        {
            var scriptOffset = output.Count;
            rewrittenOffsets.Add(patch.Slot, scriptOffset);

            var scriptBytes = new byte[OneShotScriptTemplate.Length];
            Buffer.BlockCopy(OneShotScriptTemplate, 0, scriptBytes, 0, scriptBytes.Length);
            scriptBytes[1] = (byte)authoredMap[patch.Slot].InstrumentIndex;
            output.AddRange(scriptBytes);
        }

        var rebuiltSeb = output.ToArray();
        foreach (var patch in BlizzardEntryPatches)
        {
            BinaryHelpers.WriteUInt32LE(rebuiltSeb, patch.TableOffset, (uint)rewrittenOffsets[patch.Slot]);
            log.WriteLine($"Reauthored Blizzard slot {patch.Slot} SEB entry -> 0x{rewrittenOffsets[patch.Slot]:X}.");
        }

        BinaryHelpers.WriteUInt32LE(rebuiltSeb, 0x0C, (uint)rebuiltSeb.Length);
        log.WriteLine($"Rebuilt SEB script section: {rebuiltSeb.Length - originalSebBytes.Length} bytes appended");
        return rebuiltSeb;
    }

    private static int Align16(int value)
    {
        return (value + 0x0F) & ~0x0F;
    }
}

public sealed record SebBlizzardReplacementManifest(
    string AssetFile,
    string InputName,
    string InputMode,
    string MappingAssumption,
    List<SebBlizzardReplacementEntry> Replacements);

public sealed record SebBlizzardReplacementEntry(
    int PcSlot,
    string ReplacementFile,
    IReadOnlyList<int> WdSampleIndices,
    IReadOnlyList<int> SlotPreviewRates);

internal sealed record SebOffsetPatch(int Slot, int TableOffset);

internal sealed record SebAuthoredReplacement(
    int PcSlot,
    int InstrumentIndex,
    WdRegionEntry TemplateRegion,
    byte[] ReplacementBytes,
    string ReplacementFile);
