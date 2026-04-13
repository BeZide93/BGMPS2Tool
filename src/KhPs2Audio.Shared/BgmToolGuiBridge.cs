using System.Globalization;
using System.Text;

namespace KhPs2Audio.Shared;

public sealed record BgmToolConfig(
    double Volume,
    double Sf2Volume,
    string Sf2BankMode,
    double Sf2PreEqStrength,
    double Sf2PreLowPassHz,
    bool Sf2AutoLowPass,
    string Sf2LoopPolicy,
    bool Sf2LoopMicroCrossfade,
    bool Sf2LoopTailWrapFill,
    bool Sf2LoopStartContentAlign,
    bool Sf2LoopEndContentAlign,
    string MidiProgramCompaction,
    string AdsrMode,
    bool MidiPitchBendWorkaround,
    bool MidiLoop,
    double HoldMinutes,
    double PreEqStrength,
    double PreLowPassHz)
{
    public static BgmToolConfig Default { get; } = new(
        Volume: 1.0,
        Sf2Volume: 1.0,
        Sf2BankMode: "used",
        Sf2PreEqStrength: 0.0,
        Sf2PreLowPassHz: 0.0,
        Sf2AutoLowPass: false,
        Sf2LoopPolicy: "safe",
        Sf2LoopMicroCrossfade: false,
        Sf2LoopTailWrapFill: false,
        Sf2LoopStartContentAlign: true,
        Sf2LoopEndContentAlign: false,
        MidiProgramCompaction: "compact",
        AdsrMode: "authored",
        MidiPitchBendWorkaround: true,
        MidiLoop: false,
        HoldMinutes: 60.0,
        PreEqStrength: 0.0,
        PreLowPassHz: 0.0);
}

public sealed record BgmTemplateMatch(
    string AssetStem,
    string BgmPath,
    string WdPath,
    int SequenceId,
    int BankId);

public sealed record GuiMidiReplacementRequest(
    string MidiPath,
    string? SoundFontPath,
    string? TemplateRootDirectory,
    string ConfigPath,
    BgmToolConfig Config,
    string? OutputDirectory = null);

public sealed record GuiWaveReplacementRequest(
    string WavePath,
    string? TemplateRootDirectory,
    string ConfigPath,
    BgmToolConfig Config,
    string? OutputDirectory = null);

public static class BgmToolGuiBridge
{
    private static readonly string GuiTempRoot = Path.Combine(Path.GetTempPath(), "BGMPS2ToolGui");
    private static readonly string[] PreviewTempPrefixes = ["preview-ps2", "preview-midisf2"];

    public static BgmToolConfig LoadConfig(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullPath))
        {
            return BgmToolConfig.Default;
        }

        var config = BgmToolConfig.Default;
        foreach (var rawLine in File.ReadAllLines(fullPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var valueText = line[(separatorIndex + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "volume" when TryParseDouble(valueText, out var volume):
                    if (volume > 0)
                    {
                        config = config with { Volume = volume };
                    }

                    break;
                case "sf2_volume" when TryParseDouble(valueText, out var sf2Volume):
                    if (sf2Volume > 0)
                    {
                        config = config with { Sf2Volume = sf2Volume };
                    }

                    break;
                case "sf2_bank_mode":
                    config = config with { Sf2BankMode = NormalizeEnum(valueText, "used") };
                    break;
                case "sf2_pre_eq" when TryParseDouble(valueText, out var sf2PreEq):
                    config = config with { Sf2PreEqStrength = Math.Clamp(sf2PreEq, 0.0, 1.0) };
                    break;
                case "sf2_pre_lowpass_hz" when TryParseDouble(valueText, out var sf2PreLowPass):
                    config = config with { Sf2PreLowPassHz = Math.Clamp(sf2PreLowPass, 0.0, 20_000.0) };
                    break;
                case "sf2_auto_lowpass" when TryParseBool(valueText, out var sf2AutoLowPass):
                    config = config with { Sf2AutoLowPass = sf2AutoLowPass };
                    break;
                case "sf2_loop_policy":
                    config = config with { Sf2LoopPolicy = NormalizeLoopPolicy(valueText) };
                    break;
                case "sf2_loop_micro_crossfade" when TryParseBool(valueText, out var sf2LoopMicroCrossfade):
                    config = config with { Sf2LoopMicroCrossfade = sf2LoopMicroCrossfade };
                    break;
                case "sf2_loop_tail_wrap_fill" when TryParseBool(valueText, out var sf2LoopTailWrapFill):
                    config = config with { Sf2LoopTailWrapFill = sf2LoopTailWrapFill };
                    break;
                case "sf2_loop_start_content_align" when TryParseBool(valueText, out var sf2LoopStartContentAlign):
                    config = config with { Sf2LoopStartContentAlign = sf2LoopStartContentAlign };
                    break;
                case "sf2_loop_end_content_align" when TryParseBool(valueText, out var sf2LoopEndContentAlign):
                    config = config with { Sf2LoopEndContentAlign = sf2LoopEndContentAlign };
                    break;
                case "midi_program_compaction":
                    config = config with { MidiProgramCompaction = NormalizeEnum(valueText, "compact") };
                    break;
                case "adsr":
                    config = config with { AdsrMode = NormalizeEnum(valueText, "authored") };
                    break;
                case "midi_pitch_bend_workaround" when TryParseBool(valueText, out var midiPitchWorkaround):
                    config = config with { MidiPitchBendWorkaround = midiPitchWorkaround };
                    break;
                case "midi_loop" when TryParseBool(valueText, out var midiLoop):
                    config = config with { MidiLoop = midiLoop };
                    break;
                case "hold_minutes" when TryParseDouble(valueText, out var holdMinutes):
                    config = config with { HoldMinutes = Math.Clamp(holdMinutes, 0.1, 600.0) };
                    break;
                case "pre_eq" when TryParseDouble(valueText, out var preEq):
                    config = config with { PreEqStrength = Math.Clamp(preEq, 0.0, 1.0) };
                    break;
                case "pre_lowpass_hz" when TryParseDouble(valueText, out var preLowPass):
                    config = config with { PreLowPassHz = Math.Clamp(preLowPass, 0.0, 20_000.0) };
                    break;
            }
        }

        return config;
    }

    public static void SaveConfig(string configPath, BgmToolConfig config)
    {
        var fullPath = Path.GetFullPath(configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, BuildConfigText(config), Encoding.UTF8);
    }

    public static BgmTemplateMatch ResolveTemplatePair(string inputAssetPath, string? templateRootDirectory = null)
    {
        var fullInputPath = Path.GetFullPath(inputAssetPath);
        var searchRoot = string.IsNullOrWhiteSpace(templateRootDirectory)
            ? Path.GetDirectoryName(fullInputPath)!
            : Path.GetFullPath(templateRootDirectory);
        if (!Directory.Exists(searchRoot))
        {
            throw new DirectoryNotFoundException($"Template root not found: {searchRoot}");
        }

        var assetStem = ResolveAssetStem(fullInputPath, searchRoot);
        var bgmPath = FindTemplateBgm(searchRoot, assetStem)
            ?? throw new FileNotFoundException($"Could not resolve template BGM '{assetStem}.bgm' under: {searchRoot}");
        var bgmInfo = BgmParser.Parse(bgmPath);
        var wdPath = WdLocator.FindForBgm(bgmInfo)
            ?? throw new FileNotFoundException($"Resolved template BGM has no matching WD next to it: {bgmPath}");
        return new BgmTemplateMatch(assetStem, bgmPath, wdPath, bgmInfo.SequenceId, bgmInfo.BankId);
    }

    public static string RunMidiReplacement(GuiMidiReplacementRequest request, TextWriter log)
    {
        var midiPath = Path.GetFullPath(request.MidiPath);
        if (!File.Exists(midiPath))
        {
            throw new FileNotFoundException("Input MIDI file was not found.", midiPath);
        }

        SaveConfig(request.ConfigPath, request.Config);
        var template = ResolveTemplatePair(midiPath, request.TemplateRootDirectory);
        var soundFontPath = ResolveGuiSoundFontPath(request.SoundFontPath, Path.GetDirectoryName(midiPath)!, template.BankId);
        if (soundFontPath is null)
        {
            throw new FileNotFoundException($"No SoundFont was found for bank {template.BankId:D4}. Expected an explicit .sf2 or wave{template.BankId:D4}.sf2 next to the MIDI.");
        }

        var outputDirectory = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(Path.GetDirectoryName(midiPath)!, "output"));
        Directory.CreateDirectory(outputDirectory);

        var tempRoot = PrepareTempWorkspace("midisf2");
        try
        {
            var tempMidiPath = Path.Combine(tempRoot, Path.GetFileName(midiPath));
            var tempSf2Path = Path.Combine(tempRoot, Path.GetFileName(soundFontPath));
            var tempBgmPath = Path.Combine(tempRoot, Path.GetFileName(template.BgmPath));
            var tempWdPath = Path.Combine(tempRoot, Path.GetFileName(template.WdPath));

            File.Copy(midiPath, tempMidiPath, overwrite: true);
            File.Copy(soundFontPath, tempSf2Path, overwrite: true);
            File.Copy(template.BgmPath, tempBgmPath, overwrite: true);
            File.Copy(template.WdPath, tempWdPath, overwrite: true);

            log.WriteLine($"GUI template root resolved: {template.BgmPath} + {template.WdPath}");
            log.WriteLine($"GUI source SF2: {soundFontPath}");

            var rebuiltOutputDirectory = BgmMidiSf2Rebuilder.ReplaceFromMidi(tempMidiPath, tempSf2Path, log);
            CopyOutputArtifacts(rebuiltOutputDirectory, outputDirectory);
            log.WriteLine($"GUI copied rebuilt MIDI/SF2 output to: {outputDirectory}");
            return outputDirectory;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public static string RunWaveReplacement(GuiWaveReplacementRequest request, TextWriter log)
    {
        var wavePath = Path.GetFullPath(request.WavePath);
        if (!File.Exists(wavePath))
        {
            throw new FileNotFoundException("Input WAV file was not found.", wavePath);
        }

        SaveConfig(request.ConfigPath, request.Config);
        var template = ResolveTemplatePair(wavePath, request.TemplateRootDirectory);
        var outputDirectory = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(Path.GetDirectoryName(wavePath)!, "output"));
        Directory.CreateDirectory(outputDirectory);

        var tempRoot = PrepareTempWorkspace("wav");
        try
        {
            var tempWavePath = Path.Combine(tempRoot, Path.GetFileName(wavePath));
            var tempBgmPath = Path.Combine(tempRoot, Path.GetFileName(template.BgmPath));
            var tempWdPath = Path.Combine(tempRoot, Path.GetFileName(template.WdPath));

            File.Copy(wavePath, tempWavePath, overwrite: true);
            File.Copy(template.BgmPath, tempBgmPath, overwrite: true);
            File.Copy(template.WdPath, tempWdPath, overwrite: true);

            log.WriteLine($"GUI template root resolved: {template.BgmPath} + {template.WdPath}");

            var rebuiltOutputDirectory = BgmWaveRebuilder.ReplaceFromWave(tempWavePath, log);
            CopyOutputArtifacts(rebuiltOutputDirectory, outputDirectory);
            log.WriteLine($"GUI copied rebuilt WAV output to: {outputDirectory}");
            return outputDirectory;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public static string RenderOutputPreview(string bgmPath, string wdPath, TextWriter log)
    {
        var fullBgmPath = Path.GetFullPath(bgmPath);
        var fullWdPath = Path.GetFullPath(wdPath);
        if (!File.Exists(fullBgmPath))
        {
            throw new FileNotFoundException("Preview BGM not found.", fullBgmPath);
        }

        if (!File.Exists(fullWdPath))
        {
            throw new FileNotFoundException("Preview WD not found.", fullWdPath);
        }

        var tempRoot = PrepareTempWorkspace("preview-ps2");
        var tempBgmPath = Path.Combine(tempRoot, Path.GetFileName(fullBgmPath));
        var tempWdPath = Path.Combine(tempRoot, Path.GetFileName(fullWdPath));
        File.Copy(fullBgmPath, tempBgmPath, overwrite: true);
        File.Copy(fullWdPath, tempWdPath, overwrite: true);
        return BgmNativeRenderer.RenderToWave(tempBgmPath, log);
    }

    public static string RenderMidiSf2Preview(string midiPath, string sf2Path, BgmToolConfig config, TextWriter log)
    {
        var tempRoot = PrepareTempWorkspace("preview-midisf2");
        var outputPath = Path.Combine(tempRoot, $"{Path.GetFileNameWithoutExtension(midiPath)}.sf2-preview.wav");
        return MidiSf2PreviewRenderer.RenderToWave(midiPath, sf2Path, outputPath, config, log);
    }

    public static int ClearPreviewTempFiles(TextWriter? log = null)
    {
        var deletedDirectoryCount = 0;
        foreach (var prefix in PreviewTempPrefixes)
        {
            var root = Path.Combine(GuiTempRoot, prefix);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (TryDeleteDirectory(directory))
                {
                    deletedDirectoryCount++;
                }
            }

            if (!Directory.EnumerateFileSystemEntries(root).Any() && TryDeleteDirectory(root))
            {
                deletedDirectoryCount++;
            }
        }

        log?.WriteLine($"Cleared {deletedDirectoryCount} temp preview director{(deletedDirectoryCount == 1 ? "y" : "ies")} under: {GuiTempRoot}");
        return deletedDirectoryCount;
    }

    private static string? ResolveGuiSoundFontPath(string? explicitPath, string assetDirectory, int bankId)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Input SoundFont file was not found.", fullPath);
            }

            return fullPath;
        }

        var bankCandidate = Path.Combine(assetDirectory, $"wave{bankId:D4}.sf2");
        if (File.Exists(bankCandidate))
        {
            return bankCandidate;
        }

        return null;
    }

    private static string ResolveAssetStem(string inputPath, string searchRoot)
    {
        var fileStem = Path.GetFileNameWithoutExtension(inputPath);
        foreach (var candidate in GetStemCandidates(fileStem))
        {
            if (candidate.Length == 0)
            {
                continue;
            }

            if (FindTemplateBgm(searchRoot, candidate) is not null)
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not infer the target asset name from '{Path.GetFileName(inputPath)}'. " +
            $"Expected a template like musicXXX.bgm under: {searchRoot}");
    }

    private static IEnumerable<string> GetStemCandidates(string fileStem)
    {
        yield return fileStem;

        var current = fileStem;
        foreach (var suffix in new[] { ".ps2", ".native", ".preview", ".custom", ".edited", ".edit", ".converted", ".import" })
        {
            if (current.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                current = current[..^suffix.Length];
                yield return current;
            }
        }

        var digits = new string(fileStem.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        if (digits.Length > 0 && int.TryParse(digits, out var numericId))
        {
            yield return $"music{numericId:D3}";
        }
    }

    private static string? FindTemplateBgm(string searchRoot, string assetStem)
    {
        var directPath = Path.Combine(searchRoot, $"{assetStem}.bgm");
        if (File.Exists(directPath))
        {
            return directPath;
        }

        return Directory.EnumerateFiles(searchRoot, $"{assetStem}.bgm", SearchOption.AllDirectories)
            .OrderBy(static path => path.Length)
            .FirstOrDefault();
    }

    private static string PrepareTempWorkspace(string prefix)
    {
        var tempRoot = Path.Combine(GuiTempRoot, prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static void CopyOutputArtifacts(string sourceOutputDirectory, string destinationOutputDirectory)
    {
        Directory.CreateDirectory(destinationOutputDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceOutputDirectory))
        {
            var destinationPath = Path.Combine(destinationOutputDirectory, Path.GetFileName(file));
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
        }
        catch
        {
            // Ignore temp cleanup failures.
        }

        return false;
    }

    private static bool TryParseDouble(string valueText, out double value)
    {
        return double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
               double.TryParse(valueText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseBool(string valueText, out bool value)
    {
        if (valueText == "1")
        {
            value = true;
            return true;
        }

        if (valueText == "0")
        {
            value = false;
            return true;
        }

        return bool.TryParse(valueText, out value);
    }

    private static string NormalizeEnum(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizeLoopPolicy(string value)
    {
        var normalized = NormalizeEnum(value, "safe").Replace('_', '-').Replace(' ', '-');
        return normalized switch
        {
            "advanced" or "scored" or "adpcm-scored" or "live" => "advanced",
            "auto" or "auto-loop" or "autoloop" => "auto-loop",
            "advanced-auto" or "advanced-auto-loop" or "advanced-autoloop" or "closest-auto-loop" or "original-auto-loop" => "advanced-auto-loop",
            _ => "safe",
        };
    }

    private static string BuildConfigText(BgmToolConfig config)
    {
        var builder = new StringBuilder();
        builder.AppendLine("; BGMPS2Tool configuration");
        builder.AppendLine("; This file is still used by both the batch workflow and the GUI.");
        builder.AppendLine("; The GUI writes the same keys that the CLI expects.");
        builder.AppendLine();
        builder.AppendLine($"volume={config.Volume.ToString("0.###", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"sf2_volume={config.Sf2Volume.ToString("0.###", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"sf2_bank_mode={NormalizeEnum(config.Sf2BankMode, "used")}");
        builder.AppendLine($"sf2_pre_eq={config.Sf2PreEqStrength.ToString("0.###", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"sf2_pre_lowpass_hz={config.Sf2PreLowPassHz.ToString("0.###", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"sf2_auto_lowpass={(config.Sf2AutoLowPass ? "1" : "0")}");
        builder.AppendLine("; sf2_loop_policy: safe = patched v0.9.2 loop path (default, quietest), advanced = decoded-ADPCM loop scoring, auto-loop = ignore SF2 loop points and search new 28-sample-aligned loop points, advanced-auto-loop = search 28-sample loop points near the original SF2 loop window.");
        builder.AppendLine($"sf2_loop_policy={NormalizeLoopPolicy(config.Sf2LoopPolicy)}");
        builder.AppendLine($"sf2_loop_micro_crossfade={(config.Sf2LoopMicroCrossfade ? "1" : "0")}");
        builder.AppendLine("; sf2_loop_tail_wrap_fill: fills the last partial PSX-ADPCM loop frame from the loop start instead of leaving the encoder to zero-pad that partial frame.");
        builder.AppendLine($"sf2_loop_tail_wrap_fill={(config.Sf2LoopTailWrapFill ? "1" : "0")}");
        builder.AppendLine("; sf2_loop_start_content_align: safe-policy only; moves the actual SF2 loop body onto the WD 28-sample loop-start block instead of looping earlier pre-loop material.");
        builder.AppendLine($"sf2_loop_start_content_align={(config.Sf2LoopStartContentAlign ? "1" : "0")}");
        builder.AppendLine("; sf2_loop_end_content_align: safe-policy test path; prepends up to 27 silent samples so the original SF2 loop end lands on a 28-sample WD block. If enabled, start-content alignment is skipped for that sample.");
        builder.AppendLine($"sf2_loop_end_content_align={(config.Sf2LoopEndContentAlign ? "1" : "0")}");
        builder.AppendLine($"midi_program_compaction={NormalizeEnum(config.MidiProgramCompaction, "compact")}");
        builder.AppendLine($"adsr={NormalizeEnum(config.AdsrMode, "authored")}");
        builder.AppendLine($"midi_pitch_bend_workaround={(config.MidiPitchBendWorkaround ? "1" : "0")}");
        builder.AppendLine($"midi_loop={(config.MidiLoop ? "1" : "0")}");
        builder.AppendLine($"hold_minutes={config.HoldMinutes.ToString("0.###", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"pre_eq={config.PreEqStrength.ToString("0.###", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"pre_lowpass_hz={config.PreLowPassHz.ToString("0.###", CultureInfo.InvariantCulture)}");
        return builder.ToString();
    }
}
