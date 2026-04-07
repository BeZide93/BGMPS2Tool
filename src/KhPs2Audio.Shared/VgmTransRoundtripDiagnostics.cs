using System.Diagnostics;
using System.Text.Json;

namespace KhPs2Audio.Shared;

public static class VgmTransRoundtripDiagnostics
{
    public static string Run(string midiPath, string? sf2Path, TextWriter log)
    {
        var fullMidiPath = Path.GetFullPath(midiPath);
        if (!File.Exists(fullMidiPath))
        {
            throw new FileNotFoundException("The input MIDI file was not found.", fullMidiPath);
        }

        var fullSf2Path = string.IsNullOrWhiteSpace(sf2Path) ? null : Path.GetFullPath(sf2Path);
        if (fullSf2Path is not null && !File.Exists(fullSf2Path))
        {
            throw new FileNotFoundException("The input SoundFont file was not found.", fullSf2Path);
        }

        log.WriteLine("Running MIDI + SF2 rebuild for diagnostics...");
        var outputDirectory = BgmMidiSf2Rebuilder.ReplaceFromMidi(fullMidiPath, fullSf2Path, log);
        var assetStem = Path.GetFileNameWithoutExtension(fullMidiPath);
        var manifestPath = Path.Combine(outputDirectory, $"{assetStem}.mid-sf2-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("The rebuild manifest was not written.", manifestPath);
        }

        var paths = ResolvePaths(fullMidiPath, fullSf2Path, outputDirectory, manifestPath);
        var vgmTransCliPath = ResolveVgmTransCliPath(fullMidiPath);
        if (vgmTransCliPath is null)
        {
            throw new FileNotFoundException(
                "Could not find vgmtrans-cli.exe. Place it next to BGMInfo.exe, inside VGMTransExportBatch, or in a sibling VGMTrans-v1.3 folder.");
        }

        var exportDirectory = Path.Combine(outputDirectory, "vgmtrans-roundtrip", assetStem);
        Directory.CreateDirectory(exportDirectory);
        log.WriteLine($"VGMTrans CLI: {vgmTransCliPath}");
        log.WriteLine($"Roundtrip export: {exportDirectory}");
        RunVgmTrans(vgmTransCliPath, paths.OutputBgmPath, paths.OutputWdPath, exportDirectory);

        var exportedMidiPath = Directory.GetFiles(exportDirectory, "*.mid", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var exportedSf2Path = Directory.GetFiles(exportDirectory, "*.sf2", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (exportedMidiPath is null || exportedSf2Path is null)
        {
            throw new InvalidOperationException("VGMTrans did not export both a MIDI and an SF2 file.");
        }

        var sourceMidi = MidiFileParser.Parse(fullMidiPath);
        var roundtripMidi = MidiFileParser.Parse(exportedMidiPath);
        var sourceMidiSummary = SummarizeMidi(sourceMidi);
        var roundtripMidiSummary = SummarizeMidi(roundtripMidi);
        var midiDifferences = CompareMidi(sourceMidiSummary, roundtripMidiSummary);

        SoundFontSummary? sourceSf2Summary = null;
        SoundFontSummary? roundtripSf2Summary = null;
        List<string> soundFontDifferences = [];
        if (paths.SourceSf2Path is not null && File.Exists(paths.SourceSf2Path))
        {
            var sourceSf2 = SoundFontParser.Parse(paths.SourceSf2Path);
            var roundtripSf2 = SoundFontParser.Parse(exportedSf2Path);
            sourceSf2Summary = SummarizeSoundFont(sourceSf2);
            roundtripSf2Summary = SummarizeSoundFont(roundtripSf2);
            soundFontDifferences = CompareSoundFont(sourceSf2Summary, roundtripSf2Summary);
        }

        var report = new VgmTransRoundtripReport(
            fullMidiPath,
            paths.SourceSf2Path,
            paths.OutputBgmPath,
            paths.OutputWdPath,
            exportedMidiPath,
            exportedSf2Path,
            vgmTransCliPath,
            sourceMidiSummary,
            roundtripMidiSummary,
            midiDifferences,
            sourceSf2Summary,
            roundtripSf2Summary,
            soundFontDifferences);

        var reportPath = Path.Combine(outputDirectory, $"{assetStem}.vgmtrans-roundtrip-report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        LogSummary(log, sourceMidiSummary, roundtripMidiSummary, midiDifferences, sourceSf2Summary, roundtripSf2Summary, soundFontDifferences);
        log.WriteLine($"Roundtrip report: {reportPath}");
        return reportPath;
    }

    private static DiagnosticPaths ResolvePaths(string fullMidiPath, string? fullSf2Path, string outputDirectory, string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = document.RootElement;
        var outputBgmName = root.GetProperty("OutputBgm").GetString();
        var outputWdName = root.GetProperty("OutputWd").GetString();
        var inputSoundFontName = root.TryGetProperty("InputSoundFont", out var inputSoundFontElement)
            ? inputSoundFontElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(outputBgmName) || string.IsNullOrWhiteSpace(outputWdName))
        {
            throw new InvalidDataException("The rebuild manifest is missing output file names.");
        }

        var outputBgmPath = Path.Combine(outputDirectory, outputBgmName);
        var outputWdPath = Path.Combine(outputDirectory, outputWdName);
        var inputDirectory = Path.GetDirectoryName(fullMidiPath) ?? Directory.GetCurrentDirectory();

        var resolvedSf2Path = fullSf2Path;
        if (resolvedSf2Path is null && !string.IsNullOrWhiteSpace(inputSoundFontName))
        {
            var candidatePath = Path.Combine(inputDirectory, inputSoundFontName);
            if (File.Exists(candidatePath) &&
                string.Equals(Path.GetExtension(candidatePath), ".sf2", StringComparison.OrdinalIgnoreCase))
            {
                resolvedSf2Path = candidatePath;
            }
        }

        return new DiagnosticPaths(outputBgmPath, outputWdPath, resolvedSf2Path);
    }

    private static string? ResolveVgmTransCliPath(string midiPath)
    {
        var startDirectory = Path.GetDirectoryName(Path.GetFullPath(midiPath)) ?? Directory.GetCurrentDirectory();
        foreach (var directory in EnumerateSelfAndParents(startDirectory))
        {
            var candidates = new[]
            {
                Path.Combine(directory, "vgmtrans-cli.exe"),
                Path.Combine(directory, "VGMTransExportBatch", "vgmtrans-cli.exe"),
                Path.Combine(directory, "VGMTrans-v1.3", "vgmtrans-cli.exe"),
                Path.Combine(directory, "Github", "VGMTransExportBatch", "vgmtrans-cli.exe")
            };

            var match = candidates.FirstOrDefault(File.Exists);
            if (match is not null)
            {
                return match;
            }
        }

        var appBaseDirectory = AppContext.BaseDirectory;
        var appCandidates = new[]
        {
            Path.Combine(appBaseDirectory, "vgmtrans-cli.exe"),
            Path.Combine(appBaseDirectory, "VGMTransExportBatch", "vgmtrans-cli.exe")
        };

        return appCandidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> EnumerateSelfAndParents(string startDirectory)
    {
        var current = Path.GetFullPath(startDirectory);
        while (!string.IsNullOrWhiteSpace(current))
        {
            yield return current;
            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                yield break;
            }

            current = parent.FullName;
        }
    }

    private static void RunVgmTrans(string cliPath, string bgmPath, string wdPath, string exportDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(bgmPath);
        startInfo.ArgumentList.Add(wdPath);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(exportDirectory);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start vgmtrans-cli.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"vgmtrans-cli failed with exit code {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}".Trim());
        }
    }

    private static MidiSummary SummarizeMidi(MidiFile midi)
    {
        var events = midi.Tracks.SelectMany(static track => track.Events).ToList();
        var channels = events.Where(static evt => evt.Channel >= 0)
            .Select(static evt => evt.Channel)
            .Distinct()
            .OrderBy(static channel => channel)
            .ToList();
        var channelSummaries = channels.Select(channel =>
        {
            var channelEvents = events.Where(evt => evt.Channel == channel).ToList();
            var programs = channelEvents.OfType<MidiProgramChangeEvent>()
                .Select(static evt => evt.Program)
                .Distinct()
                .OrderBy(static program => program)
                .ToList();
            return new MidiChannelSummary(
                channel,
                channelEvents.OfType<MidiNoteOnEvent>().Count(),
                channelEvents.OfType<MidiNoteOffEvent>().Count(),
                channelEvents.OfType<MidiProgramChangeEvent>().Count(),
                channelEvents.OfType<MidiControlChangeEvent>().Count(),
                channelEvents.OfType<MidiPitchBendEvent>().Count(),
                programs);
        }).ToList();

        return new MidiSummary(
            midi.FilePath,
            midi.Format,
            midi.Division,
            midi.Tracks.Count,
            midi.Tracks.Count(track => !string.IsNullOrWhiteSpace(track.Name)),
            midi.Tracks.Sum(static track => track.Events.Count),
            midi.Tracks.Max(static track => track.EndTick),
            events.OfType<MidiTempoEvent>().Count(),
            events.OfType<MidiNoteOnEvent>().Count(),
            events.OfType<MidiNoteOffEvent>().Count(),
            events.OfType<MidiProgramChangeEvent>().Count(),
            events.OfType<MidiControlChangeEvent>().Count(),
            events.OfType<MidiPitchBendEvent>().Count(),
            channelSummaries);
    }

    private static SoundFontSummary SummarizeSoundFont(SoundFontFile soundFont)
    {
        var allRegions = soundFont.Presets.SelectMany(static preset => preset.Regions).ToList();
        var presetSummaries = soundFont.Presets
            .OrderBy(static preset => preset.Bank)
            .ThenBy(static preset => preset.Program)
            .Select(preset =>
            {
                var regions = preset.Regions;
                return new SoundFontPresetSummary(
                    preset.Bank,
                    preset.Program,
                    preset.Name,
                    regions.Count,
                    regions.Count(static region => region.Looping),
                    regions.Count(static region => region.StereoPcm is not null),
                    regions.Select(static region => region.IdentityKey).Distinct(StringComparer.Ordinal).Count(),
                    regions.Min(static region => region.KeyLow),
                    regions.Max(static region => region.KeyHigh),
                    regions.Min(static region => region.VelocityLow),
                    regions.Max(static region => region.VelocityHigh));
            }).ToList();

        return new SoundFontSummary(
            soundFont.FilePath,
            soundFont.Presets.Count,
            allRegions.Count,
            allRegions.Count(static region => region.Looping),
            allRegions.Count(static region => region.StereoPcm is not null),
            allRegions.Select(static region => region.IdentityKey).Distinct(StringComparer.Ordinal).Count(),
            soundFont.Warnings,
            presetSummaries);
    }

    private static List<string> CompareMidi(MidiSummary source, MidiSummary roundtrip)
    {
        List<string> differences = [];
        CompareValue("format", source.Format, roundtrip.Format, differences);
        CompareValue("division", source.Division, roundtrip.Division, differences);
        CompareValue("track count", source.TrackCount, roundtrip.TrackCount, differences);
        CompareValue("named track count", source.NamedTrackCount, roundtrip.NamedTrackCount, differences);
        CompareValue("total event count", source.TotalEventCount, roundtrip.TotalEventCount, differences);
        CompareValue("end tick", source.EndTick, roundtrip.EndTick, differences);
        CompareValue("tempo event count", source.TempoEventCount, roundtrip.TempoEventCount, differences);
        CompareValue("note-on count", source.NoteOnCount, roundtrip.NoteOnCount, differences);
        CompareValue("note-off count", source.NoteOffCount, roundtrip.NoteOffCount, differences);
        CompareValue("program change count", source.ProgramChangeCount, roundtrip.ProgramChangeCount, differences);
        CompareValue("control change count", source.ControlChangeCount, roundtrip.ControlChangeCount, differences);
        CompareValue("pitch-bend count", source.PitchBendCount, roundtrip.PitchBendCount, differences);

        var sourceChannels = source.Channels.ToDictionary(static channel => channel.Channel);
        var roundtripChannels = roundtrip.Channels.ToDictionary(static channel => channel.Channel);
        foreach (var channel in sourceChannels.Keys.Union(roundtripChannels.Keys).OrderBy(static value => value))
        {
            var hasSource = sourceChannels.TryGetValue(channel, out var sourceChannel);
            var hasRoundtrip = roundtripChannels.TryGetValue(channel, out var roundtripChannel);
            if (!hasSource)
            {
                differences.Add($"roundtrip MIDI adds channel {channel} that is absent from the source.");
                continue;
            }

            if (!hasRoundtrip)
            {
                differences.Add($"roundtrip MIDI is missing source channel {channel}.");
                continue;
            }

            if (sourceChannel!.Programs.SequenceEqual(roundtripChannel!.Programs) is false)
            {
                differences.Add(
                    $"channel {channel} program set differs: source [{string.Join(", ", sourceChannel.Programs)}] vs roundtrip [{string.Join(", ", roundtripChannel.Programs)}].");
            }

            CompareValue($"channel {channel} note-on count", sourceChannel.NoteOnCount, roundtripChannel.NoteOnCount, differences);
            CompareValue($"channel {channel} control change count", sourceChannel.ControlChangeCount, roundtripChannel.ControlChangeCount, differences);
            CompareValue($"channel {channel} pitch-bend count", sourceChannel.PitchBendCount, roundtripChannel.PitchBendCount, differences);
        }

        return differences;
    }

    private static List<string> CompareSoundFont(SoundFontSummary source, SoundFontSummary roundtrip)
    {
        List<string> differences = [];
        CompareValue("preset count", source.PresetCount, roundtrip.PresetCount, differences);
        CompareValue("region count", source.TotalRegionCount, roundtrip.TotalRegionCount, differences);
        CompareValue("looping region count", source.LoopingRegionCount, roundtrip.LoopingRegionCount, differences);
        CompareValue("stereo region count", source.StereoRegionCount, roundtrip.StereoRegionCount, differences);
        CompareValue("unique sample identity count", source.UniqueIdentityCount, roundtrip.UniqueIdentityCount, differences);

        if (source.Warnings.Count != roundtrip.Warnings.Count)
        {
            differences.Add($"warning count differs: source {source.Warnings.Count} vs roundtrip {roundtrip.Warnings.Count}.");
        }

        var sourcePresets = source.Presets.ToDictionary(static preset => $"{preset.Bank}/{preset.Program}");
        var roundtripPresets = roundtrip.Presets.ToDictionary(static preset => $"{preset.Bank}/{preset.Program}");
        foreach (var presetKey in sourcePresets.Keys.Union(roundtripPresets.Keys).OrderBy(static key => key, StringComparer.Ordinal))
        {
            var hasSource = sourcePresets.TryGetValue(presetKey, out var sourcePreset);
            var hasRoundtrip = roundtripPresets.TryGetValue(presetKey, out var roundtripPreset);
            if (!hasSource)
            {
                differences.Add($"roundtrip SF2 adds preset {presetKey} ({roundtripPreset!.Name}) that is absent from the source.");
                continue;
            }

            if (!hasRoundtrip)
            {
                differences.Add($"roundtrip SF2 is missing source preset {presetKey} ({sourcePreset!.Name}).");
                continue;
            }

            CompareValue($"preset {presetKey} region count", sourcePreset!.RegionCount, roundtripPreset!.RegionCount, differences);
            CompareValue($"preset {presetKey} looping region count", sourcePreset.LoopingRegionCount, roundtripPreset.LoopingRegionCount, differences);
            CompareValue($"preset {presetKey} stereo region count", sourcePreset.StereoRegionCount, roundtripPreset.StereoRegionCount, differences);
            CompareValue($"preset {presetKey} unique sample count", sourcePreset.UniqueIdentityCount, roundtripPreset.UniqueIdentityCount, differences);
            CompareValue($"preset {presetKey} key low", sourcePreset.KeyLow, roundtripPreset.KeyLow, differences);
            CompareValue($"preset {presetKey} key high", sourcePreset.KeyHigh, roundtripPreset.KeyHigh, differences);
            CompareValue($"preset {presetKey} velocity low", sourcePreset.VelocityLow, roundtripPreset.VelocityLow, differences);
            CompareValue($"preset {presetKey} velocity high", sourcePreset.VelocityHigh, roundtripPreset.VelocityHigh, differences);
        }

        return differences;
    }

    private static void CompareValue<T>(string label, T sourceValue, T roundtripValue, List<string> differences)
        where T : notnull
    {
        if (EqualityComparer<T>.Default.Equals(sourceValue, roundtripValue))
        {
            return;
        }

        differences.Add($"{label} differs: source {sourceValue} vs roundtrip {roundtripValue}.");
    }

    private static void LogSummary(
        TextWriter log,
        MidiSummary sourceMidi,
        MidiSummary roundtripMidi,
        List<string> midiDifferences,
        SoundFontSummary? sourceSf2,
        SoundFontSummary? roundtripSf2,
        List<string> soundFontDifferences)
    {
        log.WriteLine($"MIDI summary: source tracks={sourceMidi.TrackCount}, roundtrip tracks={roundtripMidi.TrackCount}; source notes={sourceMidi.NoteOnCount}, roundtrip notes={roundtripMidi.NoteOnCount}.");
        if (sourceSf2 is not null && roundtripSf2 is not null)
        {
            log.WriteLine($"SF2 summary: source presets={sourceSf2.PresetCount}, roundtrip presets={roundtripSf2.PresetCount}; source regions={sourceSf2.TotalRegionCount}, roundtrip regions={roundtripSf2.TotalRegionCount}; source stereo={sourceSf2.StereoRegionCount}, roundtrip stereo={roundtripSf2.StereoRegionCount}.");
        }

        foreach (var difference in midiDifferences.Take(8))
        {
            log.WriteLine($"MIDI diff: {difference}");
        }

        foreach (var difference in soundFontDifferences.Take(8))
        {
            log.WriteLine($"SF2 diff: {difference}");
        }
    }

    private sealed record DiagnosticPaths(
        string OutputBgmPath,
        string OutputWdPath,
        string? SourceSf2Path);
}

internal sealed record VgmTransRoundtripReport(
    string InputMidi,
    string? InputSoundFont,
    string AuthoredBgm,
    string AuthoredWd,
    string RoundtripMidi,
    string RoundtripSoundFont,
    string VgmTransCli,
    MidiSummary SourceMidi,
    MidiSummary RoundtripMidiSummary,
    List<string> MidiDifferences,
    SoundFontSummary? SourceSoundFont,
    SoundFontSummary? RoundtripSoundFontSummary,
    List<string> SoundFontDifferences);

internal sealed record MidiSummary(
    string FilePath,
    int Format,
    int Division,
    int TrackCount,
    int NamedTrackCount,
    int TotalEventCount,
    long EndTick,
    int TempoEventCount,
    int NoteOnCount,
    int NoteOffCount,
    int ProgramChangeCount,
    int ControlChangeCount,
    int PitchBendCount,
    List<MidiChannelSummary> Channels);

internal sealed record MidiChannelSummary(
    int Channel,
    int NoteOnCount,
    int NoteOffCount,
    int ProgramChangeCount,
    int ControlChangeCount,
    int PitchBendCount,
    List<int> Programs);

internal sealed record SoundFontSummary(
    string FilePath,
    int PresetCount,
    int TotalRegionCount,
    int LoopingRegionCount,
    int StereoRegionCount,
    int UniqueIdentityCount,
    List<string> Warnings,
    List<SoundFontPresetSummary> Presets);

internal sealed record SoundFontPresetSummary(
    int Bank,
    int Program,
    string Name,
    int RegionCount,
    int LoopingRegionCount,
    int StereoRegionCount,
    int UniqueIdentityCount,
    int KeyLow,
    int KeyHigh,
    int VelocityLow,
    int VelocityHigh);
