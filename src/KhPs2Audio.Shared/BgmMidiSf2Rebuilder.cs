using System.Text.Json;

namespace KhPs2Audio.Shared;

public static class BgmMidiSf2Rebuilder
{
    private const string ConfigFileName = "config.ini";
    private const string Sf2VolumeKey = "sf2_volume";
    private const string MidiLoopKey = "midi_loop";
    private const string Sf2BankModeKey = "sf2_bank_mode";
    private const string Sf2PreEqKey = "sf2_pre_eq";
    private const string Sf2PreLowPassHzKey = "sf2_pre_lowpass_hz";
    private const string Sf2AutoLowPassKey = "sf2_auto_lowpass";
    private const string MidiPitchBendWorkaroundKey = "midi_pitch_bend_workaround";
    private const string MidiProgramCompactionKey = "midi_program_compaction";
    private const string AdsrModeKey = "adsr";
    private const double DefaultSf2Volume = 1.0;
    private const bool DefaultMidiLoop = false;
    private const Sf2BankMode DefaultSf2BankMode = Sf2BankMode.Used;
    private const double DefaultSf2PreEqStrength = 0.0;
    private const double DefaultSf2PreLowPassHz = 0.0;
    private const bool DefaultSf2AutoLowPass = false;
    private const bool DefaultMidiPitchBendWorkaround = true;
    private const MidiProgramCompactionMode DefaultMidiProgramCompaction = MidiProgramCompactionMode.Preserve;
    private const MidiSf2AdsrMode DefaultMidiSf2AdsrMode = MidiSf2AdsrMode.Authored;
    private const ushort DefaultPpqn = 48;
    private const int MaxAuthoredWdBytes = 980 * 1024;
    private const int MaxAuthoredBgmBytes = 48_900;
    private const int MaxExpandedBgmGrowthBytes = 64 * 1024;
    private const double MaxExpandedBgmGrowthFactor = 4.0;
    private const int MaxExpandedOneShotBgmGrowthBytes = 96 * 1024;
    private const double MaxExpandedOneShotBgmGrowthFactor = 6.0;
    private const int DefaultMidiPanCenter = 64;
    private const double FastAttackClampSeconds = 0.125;
    private const int SpuSampleRate = 44100;
    private const int Ps2AdsrSampleRate = 48_000;
    private const int SpuMaxLevel = 0x7FFF;
    private const int PsxEnvelopeMaxLevel = 0x7FFFFFFF;
    private const int MaxEnvelopeSearchSamples = SpuSampleRate * 120;
    private const int GeneralMidiPercussionChannel = 9;
    private const int GeneralMidiPercussionBank = 128;
    private const int ShortLoopAlignmentThresholdSamples = 512;
    private const int PitchVariantStepCents = 25;
    private const int PitchVariantMaxResidualCents = 50;
    private const int PitchRetuneNoOpThresholdCents = 5;
    private const int SustainHoldShift = 31;
    private const int SustainHoldStep = 3;
    private static readonly uint[] PsxRateTable = BuildPsxRateTable();
    private static readonly IReadOnlyList<AttackAdsrProfile> AttackProfiles = BuildAttackProfiles();
    private static readonly IReadOnlyList<DecayAdsrProfile> DecayProfiles = BuildDecayProfiles();
    private static readonly IReadOnlyList<ReleaseAdsrProfile> ReleaseProfiles = BuildReleaseProfiles();

    public static string ReplaceFromMidi(string midiPath, string? soundFontPath, TextWriter log)
    {
        var inputMidiPath = Path.GetFullPath(midiPath);
        if (!File.Exists(inputMidiPath))
        {
            throw new FileNotFoundException("Input MIDI file was not found.", inputMidiPath);
        }

        var assetDirectory = Path.GetDirectoryName(inputMidiPath)
            ?? throw new InvalidOperationException("Could not determine the input directory.");
        var assetStem = ResolveAssetStem(inputMidiPath);
        var bgmPath = Path.Combine(assetDirectory, $"{assetStem}.bgm");
        if (!File.Exists(bgmPath))
        {
            throw new FileNotFoundException($"No matching .bgm was found next to the MIDI. Expected: {bgmPath}", bgmPath);
        }

        var bgmInfo = BgmParser.Parse(bgmPath);
        var wdPath = WdLocator.FindForBgm(bgmInfo)
            ?? throw new FileNotFoundException("No matching .wd file was found for the requested .bgm.", bgmPath);

        var config = LoadMidiSf2Config(log);
        var volume = config.Sf2Volume;
        var midiLoop = config.MidiLoop;
        var sf2BankMode = config.Sf2BankMode;
        var midiPitchBendWorkaround = config.MidiPitchBendWorkaround;
        var midiProgramCompaction = config.MidiProgramCompaction;
        var adsrMode = config.AdsrMode;
        var midi = MidiFileParser.Parse(inputMidiPath);
        var sf2Path = ResolveSoundFontPath(soundFontPath, assetDirectory, bgmInfo.BankId);
        var wdBank = WdBankFile.Load(wdPath);

        ConversionPlan plan;
        byte[] outputWd;
        string programSourceLabel;
        if (!string.IsNullOrWhiteSpace(sf2Path))
        {
            try
            {
                var soundFont = SoundFontParser.Parse(
                    sf2Path,
                    new SoundFontImportOptions(config.Sf2PreEqStrength, config.Sf2PreLowPassHz, config.Sf2AutoLowPass));
                plan = BuildPlan(midi, soundFont, wdBank, volume, sf2BankMode, midiProgramCompaction, adsrMode, midiPitchBendWorkaround, log);
                plan = ConstrainPlanToWdBudget(plan, MaxAuthoredWdBytes, log);
                outputWd = BuildWd(wdPath, bgmInfo.BankId, plan, log);
                programSourceLabel = Path.GetFileName(sf2Path);
            }
            catch (MissingSoundFontPresetException ex)
            {
                log.WriteLine($"SoundFont fallback: {ex.Message}");
                log.WriteLine($"Falling back to original WD instrument mapping from: {wdPath}");
                plan = BuildPlanFromOriginalWd(midi, wdBank, midiPitchBendWorkaround, log);
                outputWd = (byte[])wdBank.OriginalBytes.Clone();
                programSourceLabel = $"original WD fallback ({Path.GetFileName(wdPath)})";
            }
        }
        else
        {
            log.WriteLine($"No matching .sf2 was found next to the MIDI/BGM. Falling back to original WD instrument mapping from: {wdPath}");
            plan = BuildPlanFromOriginalWd(midi, wdBank, midiPitchBendWorkaround, log);
            outputWd = (byte[])wdBank.OriginalBytes.Clone();
            programSourceLabel = $"original WD fallback ({Path.GetFileName(wdPath)})";
        }

        var outputDirectory = Path.Combine(assetDirectory, "output");
        Directory.CreateDirectory(outputDirectory);

        var outputBgmPath = Path.Combine(outputDirectory, Path.GetFileName(bgmPath));
        var outputWdPath = Path.Combine(outputDirectory, Path.GetFileName(wdPath));
        var manifestPath = Path.Combine(outputDirectory, $"{assetStem}.mid-sf2-manifest.json");

        var outputBgm = BuildBgm(bgmPath, bgmInfo.SequenceId, bgmInfo.BankId, midi, plan, midiLoop, log);

        File.WriteAllBytes(outputBgmPath, outputBgm);
        File.WriteAllBytes(outputWdPath, outputWd);
        var instrumentManifests = BuildInstrumentManifests(wdPath, plan);
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new MidiSf2ReplacementManifest(
                    Path.GetFileName(inputMidiPath),
                    programSourceLabel,
                    Path.GetFileName(outputBgmPath),
                    Path.GetFileName(outputWdPath),
                    plan.TrackPlans.Select(static track => new MidiSf2TrackManifest(track.Channel, track.Name, track.EventCount)).ToList(),
                    plan.ProgramMap.Select(entry => new MidiSf2ProgramManifest(entry.Key.Bank, entry.Key.Program, entry.Value.InstrumentIndex, entry.Value.PresetName, entry.Value.RegionCount)).OrderBy(static entry => entry.Bank).ThenBy(static entry => entry.Program).ToList(),
                    instrumentManifests,
                    plan.Warnings),
                new JsonSerializerOptions { WriteIndented = true }));

        log.WriteLine($"Input MIDI: {inputMidiPath}");
        log.WriteLine($"Program source: {programSourceLabel}");
        log.WriteLine($"Matched PS2 pair: {bgmPath} + {wdPath}");
        log.WriteLine($"Wrote rebuilt BGM: {outputBgmPath}");
        log.WriteLine($"Wrote rebuilt WD: {outputWdPath}");
        log.WriteLine($"Manifest: {manifestPath}");
        return outputDirectory;
    }

    private static string? ResolveSoundFontPath(string? explicitPath, string assetDirectory, int bankId)
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

    private static string ResolveAssetStem(string midiPath)
    {
        var fileStem = Path.GetFileNameWithoutExtension(midiPath);
        foreach (var candidate in GetStemCandidates(fileStem))
        {
            if (candidate.Length == 0)
            {
                continue;
            }

            var bgmCandidate = Path.Combine(Path.GetDirectoryName(midiPath)!, $"{candidate}.bgm");
            if (File.Exists(bgmCandidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not infer the target asset name from '{Path.GetFileName(midiPath)}'. " +
            "Place the MIDI next to the matching musicXXX.bgm and name it like musicXXX.mid.");
    }

    private static IEnumerable<string> GetStemCandidates(string fileStem)
    {
        yield return fileStem;

        var current = fileStem;
        foreach (var suffix in new[] { ".edit", ".edited", ".custom", ".converted", ".import" })
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

    private static MidiSf2Config LoadMidiSf2Config(TextWriter log)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        if (!File.Exists(configPath))
        {
            log.WriteLine(
                $"Config: {ConfigFileName} not found next to the tool. Using default {Sf2VolumeKey}={DefaultSf2Volume:0.###}, {MidiLoopKey}=0, {Sf2BankModeKey}={DefaultSf2BankMode.ToString().ToLowerInvariant()}, {Sf2PreEqKey}={DefaultSf2PreEqStrength:0.###}, {Sf2PreLowPassHzKey}={DefaultSf2PreLowPassHz:0.###}, {Sf2AutoLowPassKey}=0, {MidiProgramCompactionKey}={DefaultMidiProgramCompaction.ToString().ToLowerInvariant()}, {AdsrModeKey}={DefaultMidiSf2AdsrMode.ToString().ToLowerInvariant()}, and {MidiPitchBendWorkaroundKey}=1 for MIDI/SF2 conversion.");
            return new MidiSf2Config(DefaultSf2Volume, DefaultMidiLoop, DefaultSf2BankMode, DefaultSf2PreEqStrength, DefaultSf2PreLowPassHz, DefaultSf2AutoLowPass, DefaultMidiPitchBendWorkaround, DefaultMidiProgramCompaction, DefaultMidiSf2AdsrMode);
        }

        var volume = DefaultSf2Volume;
        var midiLoop = DefaultMidiLoop;
        var sf2BankMode = DefaultSf2BankMode;
        var sf2PreEqStrength = DefaultSf2PreEqStrength;
        var sf2PreLowPassHz = DefaultSf2PreLowPassHz;
        var sf2AutoLowPass = DefaultSf2AutoLowPass;
        var midiPitchBendWorkaround = DefaultMidiPitchBendWorkaround;
        var midiProgramCompaction = DefaultMidiProgramCompaction;
        var adsrMode = DefaultMidiSf2AdsrMode;
        var foundExplicitSf2Volume = false;
        var foundExplicitMidiLoop = false;
        var foundExplicitSf2BankMode = false;
        var foundExplicitSf2PreEq = false;
        var foundExplicitSf2PreLowPass = false;
        var foundExplicitSf2AutoLowPass = false;
        var foundExplicitMidiPitchBendWorkaround = false;
        var foundExplicitMidiProgramCompaction = false;
        var foundExplicitAdsrMode = false;
        foreach (var rawLine in File.ReadAllLines(configPath))
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
            if (key.Equals(Sf2VolumeKey, StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(valueText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out volume) &&
                    !double.TryParse(valueText.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out volume))
                {
                    log.WriteLine($"Config warning: could not parse {Sf2VolumeKey}={valueText}. Using the current value.");
                    volume = DefaultSf2Volume;
                }

                foundExplicitSf2Volume = true;
                continue;
            }

            if (key.Equals(MidiLoopKey, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseConfigBool(valueText, out midiLoop))
                {
                    log.WriteLine($"Config warning: could not parse {MidiLoopKey}={valueText}. Use 0/1, true/false, yes/no, or on/off. Using the current value.");
                    midiLoop = DefaultMidiLoop;
                }

                foundExplicitMidiLoop = true;
                continue;
            }

            if (key.Equals(Sf2BankModeKey, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseSf2BankMode(valueText, out sf2BankMode))
                {
                    log.WriteLine($"Config warning: could not parse {Sf2BankModeKey}={valueText}. Use 'used' or 'full'. Using the current value.");
                    sf2BankMode = DefaultSf2BankMode;
                }

                foundExplicitSf2BankMode = true;
                continue;
            }

            if (key.Equals(Sf2PreEqKey, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseConfigDouble(valueText, out sf2PreEqStrength))
                {
                    log.WriteLine($"Config warning: could not parse {Sf2PreEqKey}={valueText}. Using the current value.");
                    sf2PreEqStrength = DefaultSf2PreEqStrength;
                }

                foundExplicitSf2PreEq = true;
                continue;
            }

            if (key.Equals(Sf2PreLowPassHzKey, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseConfigDouble(valueText, out sf2PreLowPassHz))
                {
                    log.WriteLine($"Config warning: could not parse {Sf2PreLowPassHzKey}={valueText}. Using the current value.");
                    sf2PreLowPassHz = DefaultSf2PreLowPassHz;
                }

                foundExplicitSf2PreLowPass = true;
                continue;
            }

            if (key.Equals(Sf2AutoLowPassKey, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseConfigBool(valueText, out sf2AutoLowPass))
                {
                    log.WriteLine($"Config warning: could not parse {Sf2AutoLowPassKey}={valueText}. Use 0/1, true/false, yes/no, or on/off. Using the current value.");
                    sf2AutoLowPass = DefaultSf2AutoLowPass;
                }

                foundExplicitSf2AutoLowPass = true;
                continue;
            }

            if (key.Equals(MidiPitchBendWorkaroundKey, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseConfigBool(valueText, out midiPitchBendWorkaround))
                {
                    log.WriteLine($"Config warning: could not parse {MidiPitchBendWorkaroundKey}={valueText}. Use 0/1, true/false, yes/no, or on/off. Using the current value.");
                    midiPitchBendWorkaround = DefaultMidiPitchBendWorkaround;
                }

                foundExplicitMidiPitchBendWorkaround = true;
                continue;
            }

            if (key.Equals(MidiProgramCompactionKey, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseMidiProgramCompactionMode(valueText, out midiProgramCompaction))
                {
                    log.WriteLine($"Config warning: could not parse {MidiProgramCompactionKey}={valueText}. Use 'auto', 'compact', or 'preserve'. Using the current value.");
                    midiProgramCompaction = DefaultMidiProgramCompaction;
                }

                foundExplicitMidiProgramCompaction = true;
                continue;
            }

            if (key.Equals(AdsrModeKey, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseMidiSf2AdsrMode(valueText, out adsrMode))
                {
                    log.WriteLine($"Config warning: could not parse {AdsrModeKey}={valueText}. Use 'auto', 'authored', or 'template'. Using the current value.");
                    adsrMode = DefaultMidiSf2AdsrMode;
                }

                foundExplicitAdsrMode = true;
            }
        }

        if (volume <= 0)
        {
            log.WriteLine($"Config warning: {Sf2VolumeKey} must be greater than 0. Using the default value.");
            volume = DefaultSf2Volume;
        }

        if (sf2PreEqStrength < 0.0 || sf2PreEqStrength > 1.0)
        {
            log.WriteLine($"Config warning: {Sf2PreEqKey} must be between 0 and 1. Using the default value.");
            sf2PreEqStrength = DefaultSf2PreEqStrength;
        }

        if (sf2PreLowPassHz != 0.0 && sf2PreLowPassHz < 1000.0)
        {
            log.WriteLine($"Config warning: {Sf2PreLowPassHzKey} must be 0 or at least 1000. Using the default value.");
            sf2PreLowPassHz = DefaultSf2PreLowPassHz;
        }
        else if (sf2PreLowPassHz > 20000.0)
        {
            log.WriteLine($"Config warning: {Sf2PreLowPassHzKey} must not exceed 20000. Using the default value.");
            sf2PreLowPassHz = DefaultSf2PreLowPassHz;
        }

        var volumeLabel = foundExplicitSf2Volume
            ? $"{Sf2VolumeKey}={volume:0.###}"
            : $"{Sf2VolumeKey} not set, using neutral {Sf2VolumeKey}={volume:0.###}";
        var loopLabel = foundExplicitMidiLoop
            ? $"{MidiLoopKey}={(midiLoop ? 1 : 0)}"
            : $"{MidiLoopKey} not set, using default {MidiLoopKey}=0";
        var bankModeLabel = foundExplicitSf2BankMode
            ? $"{Sf2BankModeKey}={sf2BankMode.ToString().ToLowerInvariant()}"
            : $"{Sf2BankModeKey} not set, using default {Sf2BankModeKey}={DefaultSf2BankMode.ToString().ToLowerInvariant()}";
        var sf2EqLabel = foundExplicitSf2PreEq
            ? $"{Sf2PreEqKey}={sf2PreEqStrength:0.###}"
            : $"{Sf2PreEqKey} not set, using default {Sf2PreEqKey}={DefaultSf2PreEqStrength:0.###}";
        var sf2LowPassLabel = foundExplicitSf2PreLowPass
            ? $"{Sf2PreLowPassHzKey}={sf2PreLowPassHz:0.###}"
            : $"{Sf2PreLowPassHzKey} not set, using default {Sf2PreLowPassHzKey}={DefaultSf2PreLowPassHz:0.###}";
        var sf2AutoLowPassLabel = foundExplicitSf2AutoLowPass
            ? $"{Sf2AutoLowPassKey}={(sf2AutoLowPass ? 1 : 0)}"
            : $"{Sf2AutoLowPassKey} not set, using default {Sf2AutoLowPassKey}={(DefaultSf2AutoLowPass ? 1 : 0)}";
        var programCompactionLabel = foundExplicitMidiProgramCompaction
            ? $"{MidiProgramCompactionKey}={midiProgramCompaction.ToString().ToLowerInvariant()}"
            : $"{MidiProgramCompactionKey} not set, using default {MidiProgramCompactionKey}={DefaultMidiProgramCompaction.ToString().ToLowerInvariant()}";
        var adsrModeLabel = foundExplicitAdsrMode
            ? $"{AdsrModeKey}={adsrMode.ToString().ToLowerInvariant()}"
            : $"{AdsrModeKey} not set, using default {AdsrModeKey}={DefaultMidiSf2AdsrMode.ToString().ToLowerInvariant()}";
        var pitchWorkaroundLabel = foundExplicitMidiPitchBendWorkaround
            ? $"{MidiPitchBendWorkaroundKey}={(midiPitchBendWorkaround ? 1 : 0)}"
            : $"{MidiPitchBendWorkaroundKey} not set, using default {MidiPitchBendWorkaroundKey}={(DefaultMidiPitchBendWorkaround ? 1 : 0)}";
        log.WriteLine($"Config: loaded {configPath} -> {volumeLabel}; {loopLabel}; {bankModeLabel}; {sf2EqLabel}; {sf2LowPassLabel}; {sf2AutoLowPassLabel}; {programCompactionLabel}; {adsrModeLabel}; {pitchWorkaroundLabel}");

        return new MidiSf2Config(volume, midiLoop, sf2BankMode, sf2PreEqStrength, sf2PreLowPassHz, sf2AutoLowPass, midiPitchBendWorkaround, midiProgramCompaction, adsrMode);
    }

    private static bool TryParseConfigDouble(string valueText, out double value)
    {
        return double.TryParse(valueText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) ||
               double.TryParse(valueText.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseConfigBool(string valueText, out bool value)
    {
        switch (valueText.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                value = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool TryParseSf2BankMode(string valueText, out Sf2BankMode bankMode)
    {
        switch (valueText.Trim().ToLowerInvariant())
        {
            case "used":
            case "midi":
            case "usedonly":
                bankMode = Sf2BankMode.Used;
                return true;
            case "full":
            case "all":
            case "fullbank":
                bankMode = Sf2BankMode.Full;
                return true;
            default:
                bankMode = DefaultSf2BankMode;
                return false;
        }
    }

    private static bool TryParseMidiProgramCompactionMode(string valueText, out MidiProgramCompactionMode mode)
    {
        switch (valueText.Trim().ToLowerInvariant())
        {
            case "auto":
            case "default":
            case "heuristic":
                mode = MidiProgramCompactionMode.Auto;
                return true;
            case "1":
            case "true":
            case "yes":
            case "on":
            case "compact":
            case "dense":
                mode = MidiProgramCompactionMode.Compact;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
            case "preserve":
            case "sparse":
            case "original":
                mode = MidiProgramCompactionMode.Preserve;
                return true;
            default:
                mode = DefaultMidiProgramCompaction;
                return false;
        }
    }

    private static bool TryParseMidiSf2AdsrMode(string valueText, out MidiSf2AdsrMode mode)
    {
        switch (valueText.Trim().ToLowerInvariant())
        {
            case "auto":
            case "default":
            case "hybrid":
                mode = MidiSf2AdsrMode.Auto;
                return true;
            case "authored":
            case "vgmtrans":
            case "sf2":
                mode = MidiSf2AdsrMode.Authored;
                return true;
            case "template":
            case "original":
            case "wd":
                mode = MidiSf2AdsrMode.Template;
                return true;
            default:
                mode = DefaultMidiSf2AdsrMode;
                return false;
        }
    }

    private static ConversionPlan BuildPlan(MidiFile midi, SoundFontFile soundFont, WdBankFile templateBank, double volume, Sf2BankMode sf2BankMode, MidiProgramCompactionMode midiProgramCompaction, MidiSf2AdsrMode adsrMode, bool enablePitchBendWorkaround, TextWriter log)
    {
        var warnings = new HashSet<string>(soundFont.Warnings, StringComparer.Ordinal);
        var usedPresetRefs = GetUsedPresetRefs(midi);
        if (UsesImplicitGeneralMidiPercussionBank(midi))
        {
            warnings.Add("MIDI channel 10 percussion convention detected: channel 10 bank 0/program notes are resolved through SoundFont percussion bank 128 when available.");
        }

        var pitchVariantPresetRefs = enablePitchBendWorkaround ? GetPitchBendPresetRefs(midi) : [];
        var programMap = new Dictionary<PresetRef, ProgramMapping>();
        var authoredSamples = new Dictionary<string, AuthoredSample>(StringComparer.Ordinal);
        var instruments = new List<AuthoredInstrument>();
        var usedInstrumentIndices = new HashSet<int>();
        var availablePresets = soundFont.Presets
            .OrderBy(static preset => preset.Bank)
            .ThenBy(static preset => preset.Program)
            .ToList();
        var availablePresetMap = availablePresets.ToDictionary(
            static preset => new PresetRef(preset.Bank, preset.Program),
            static preset => preset);
        var resolvedPresetMap = new Dictionary<PresetRef, SoundFontPreset>();
        var missingPresetRefs = new HashSet<PresetRef>();

        foreach (var presetRef in usedPresetRefs)
        {
            var resolvedPreset = ResolvePreset(soundFont, presetRef, warnings, missingPresetRefs);
            if (resolvedPreset is not null)
            {
                resolvedPresetMap[presetRef] = resolvedPreset;
            }
        }

        if (missingPresetRefs.Count > 0)
        {
            var missingList = string.Join(", ", missingPresetRefs.OrderBy(static preset => preset.Bank).ThenBy(static preset => preset.Program).Select(static preset => $"{preset.Bank}/{preset.Program}"));
            var availableList = string.Join(", ", availablePresets.Select(static preset => $"{preset.Bank}/{preset.Program}"));
            throw new MissingSoundFontPresetException(missingList, availableList);
        }

        var fallbackResolvedPresetRefs = resolvedPresetMap
            .Where(static pair => pair.Key != new PresetRef(pair.Value.Bank, pair.Value.Program))
            .Select(static pair => pair.Key)
            .OrderBy(static preset => preset.Bank)
            .ThenBy(static preset => preset.Program)
            .ToList();
        var effectivePitchVariantPresetRefs = pitchVariantPresetRefs;
        if (sf2BankMode == Sf2BankMode.Full && effectivePitchVariantPresetRefs.Count > 0)
        {
            effectivePitchVariantPresetRefs = [];
            log.WriteLine("Pitch variants: disabled in sf2_bank_mode=full so the authored WD stays closer to the original SoundFont program layout for existing BGM files.");
        }
        else if (pitchVariantPresetRefs.Count > 0 && fallbackResolvedPresetRefs.Count > 0)
        {
            effectivePitchVariantPresetRefs = [];
            var fallbackList = string.Join(", ", fallbackResolvedPresetRefs.Select(static preset => $"{preset.Bank}/{preset.Program}"));
            log.WriteLine($"Pitch variants: disabled for this build because one or more MIDI presets had to fall back to different SoundFont presets ({fallbackList}). Keeping the authored WD/BGM layout simpler for KH2 compatibility.");
        }

        var compactSparsePrograms = ResolveProgramCompactionMode(midiProgramCompaction, sf2BankMode, usedPresetRefs, effectivePitchVariantPresetRefs);
        if (midiProgramCompaction == MidiProgramCompactionMode.Compact)
        {
            log.WriteLine("Program compaction: forced on via config; using dense PS2 instrument indices and removing sparse WD table gaps.");
        }
        else if (midiProgramCompaction == MidiProgramCompactionMode.Preserve)
        {
            log.WriteLine("Program compaction: forced off via config; preserving sparse/original-style PS2 instrument indices and any WD table gaps.");
        }
        else if (compactSparsePrograms)
        {
            log.WriteLine("Program compaction: using dense PS2 instrument indices for sparse MIDI program numbers.");
        }
        else if (effectivePitchVariantPresetRefs.Count > 0)
        {
            log.WriteLine("Program compaction: disabled because bend-aware instrument variants need stable original-style program indices.");
        }
        else if (sf2BankMode == Sf2BankMode.Full)
        {
            log.WriteLine("Program compaction: disabled because sf2_bank_mode=full should preserve original-style program indices wherever possible.");
        }

        var presetsToAuthor = (sf2BankMode == Sf2BankMode.Full
                ? availablePresets
                : resolvedPresetMap.Values.ToList())
            .DistinctBy(static preset => new PresetRef(preset.Bank, preset.Program))
            .OrderBy(static preset => preset.Bank)
            .ThenBy(static preset => preset.Program)
            .ToList();
        if (sf2BankMode == Sf2BankMode.Full)
        {
            log.WriteLine($"SF2 bank mode: full; authoring all {presetsToAuthor.Count} available SoundFont preset(s), including presets that are not referenced by the current MIDI.");
        }

        var enableShortLoopPitchCompensation = ShouldEnableShortLoopPitchCompensation(presetsToAuthor, warnings);
        if (enableShortLoopPitchCompensation)
        {
            log.WriteLine("Short-loop pitch compensation: enabled for simple waveform-style SF2 content.");
        }

        var templateRegionsByInstrument = templateBank.Regions
            .GroupBy(static region => region.InstrumentIndex)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(region => region.RegionIndex).ToList());
        var sourceLoopingRegionCount = 0;
        var sourceOneShotRegionCount = 0;
        var templateForcedOneShotRegionCount = 0;
        var exactTemplateOneShotMatchCount = 0;
        var templateInstrumentAllOneShotCount = 0;
        var sourceLoopPreservedCount = 0;
        var nextCompactInstrumentIndex = 0;
        foreach (var preset in presetsToAuthor)
        {
            var presetRef = new PresetRef(preset.Bank, preset.Program);
            var instrumentIndex = compactSparsePrograms
                ? AllocateCompactInstrumentIndex(usedInstrumentIndices, ref nextCompactInstrumentIndex)
                : AllocateInstrumentIndex(presetRef, usedInstrumentIndices);
            var normalizedRegions = CollapsePseudoStereoRegions(SoundFontParser.NormalizeRegions(preset.Regions, warnings));
            var preferFastAttackEnvelopeForPreset =
                presetsToAuthor.Count == 1 && normalizedRegions.Count <= 12;
            var authoredRegions = new List<AuthoredRegion>();
            var downmixedPseudoStereoRegions = 0;
            foreach (var sourceRegion in normalizedRegions.OrderBy(static region => region.KeyHigh).ThenBy(static region => region.VelocityHigh))
            {
                var region = sourceRegion;
                var regionWasDownmixed = false;
                if (region.StereoPcm is not null &&
                    !string.IsNullOrWhiteSpace(region.StereoIdentityKey) &&
                    Math.Abs(region.Pan) <= 0.25f)
                {
                    region = region with
                    {
                        IdentityKey = $"{region.IdentityKey}|{region.StereoIdentityKey}",
                        SourceSampleName = region.StereoSourceSampleName is null
                            ? region.SourceSampleName
                            : $"{region.SourceSampleName}+{region.StereoSourceSampleName}",
                        Pan = 0f,
                        Pcm = MixPseudoStereoToMono(region.Pcm, region.StereoPcm),
                        StereoIdentityKey = null,
                        StereoSourceSampleName = null,
                        StereoPcm = null,
                    };
                    downmixedPseudoStereoRegions++;
                    regionWasDownmixed = true;
                }

                var isStereo = region.StereoPcm is not null && !string.IsNullOrWhiteSpace(region.StereoIdentityKey);
                var loopPolicy = ResolveLoopPolicy(
                    templateBank,
                    templateRegionsByInstrument,
                    preset.Program,
                    region.KeyLow,
                    region.KeyHigh,
                    region.VelocityLow,
                    region.VelocityHigh,
                    isStereo,
                    region.LoopDescriptor);
                if (region.Looping)
                {
                    sourceLoopingRegionCount++;
                    if (loopPolicy.Looping)
                    {
                        sourceLoopPreservedCount++;
                    }
                    else
                    {
                        templateForcedOneShotRegionCount++;
                        if (string.Equals(loopPolicy.LoopTemplateMatchKind, "exact_one_shot_match", StringComparison.Ordinal))
                        {
                            exactTemplateOneShotMatchCount++;
                        }
                        else if (string.Equals(loopPolicy.LoopTemplateMatchKind, "instrument_all_one_shot", StringComparison.Ordinal))
                        {
                            templateInstrumentAllOneShotCount++;
                        }
                    }
                }
                else
                {
                    sourceOneShotRegionCount++;
                }

                var authoredIdentityKey = BuildLoopAwareIdentityKey(region.IdentityKey, loopPolicy.LoopDescriptor);
                var authoredSample = GetOrAddAuthoredSample(
                    authoredSamples,
                    authoredIdentityKey,
                    region.SourceSampleName,
                    region.Pcm,
                    region.SamplePitch,
                    loopPolicy.LoopDescriptor,
                    volume,
                    enableShortLoopPitchCompensation);
                var envelope = EncodeAdsr(region);
                var sourceInfo = new AuthoredRegionSourceInfo(
                    region.SourceSampleName,
                    region.RootKey,
                    region.FineTuneCents,
                    region.SampleRate,
                    region.InitialFilterFcCents,
                    region.InitialFilterCutoffHz,
                    region.AttackSeconds,
                    region.HoldSeconds,
                    region.DecaySeconds,
                    region.SustainLevel,
                    region.ReleaseSeconds,
                    region.LoopDescriptor,
                    regionWasDownmixed,
                    isStereo,
                    loopPolicy.LoopDescriptor,
                    region.Debug);
                var provisionalRegion = new AuthoredRegion(
                    authoredSample,
                    region.KeyLow,
                    region.KeyHigh,
                    region.VelocityLow,
                    region.VelocityHigh,
                    region.RegionPitch,
                    Math.Clamp(region.Volume, 0f, 1f),
                    isStereo ? GetStereoLeftPan(region.Pan) : Math.Clamp(region.Pan, -1f, 1f),
                    envelope,
                    isStereo,
                    false,
                    sourceInfo,
                    string.Empty,
                    loopPolicy.LoopPolicyReason,
                    loopPolicy.UsedTemplateLoopPolicy,
                    loopPolicy.LoopTemplateMatchKind);
                WdRegionEntry? envelopeTemplateRegion = null;
                var preferAuthoredEnvelope = false;
                string envelopePolicyReason;
                if (adsrMode == MidiSf2AdsrMode.Authored)
                {
                    preferAuthoredEnvelope = true;
                    envelopePolicyReason = "authored:config=authored";
                }
                else
                {
                    var forceAuthoredEnvelope =
                        adsrMode != MidiSf2AdsrMode.Template &&
                        (sf2BankMode == Sf2BankMode.Full ||
                         regionWasDownmixed ||
                         (preferFastAttackEnvelopeForPreset && region.AttackSeconds <= FastAttackClampSeconds));
                    envelopeTemplateRegion = forceAuthoredEnvelope
                        ? null
                        : SelectEnvelopeTemplateRegion(
                            templateRegionsByInstrument,
                            preset.Program,
                            provisionalRegion,
                            authoredRegions.Count);
                    preferAuthoredEnvelope = envelopeTemplateRegion is null;
                    if (adsrMode == MidiSf2AdsrMode.Template)
                    {
                        envelopePolicyReason = envelopeTemplateRegion is not null
                            ? $"template:config=template ({DescribeEnvelopeTemplateUsage(envelopeTemplateRegion, provisionalRegion, authoredRegions.Count)})"
                            : "authored:config=template_no_match";
                    }
                    else
                    {
                        envelopePolicyReason =
                            sf2BankMode == Sf2BankMode.Full
                                ? "authored:sf2_bank_mode=full"
                                : regionWasDownmixed
                                    ? "authored:downmixed_pseudo_stereo"
                                    : (preferFastAttackEnvelopeForPreset && region.AttackSeconds <= FastAttackClampSeconds)
                                        ? "authored:fast_attack_single_preset"
                                        : envelopeTemplateRegion is not null
                                            ? DescribeEnvelopeTemplateUsage(envelopeTemplateRegion, provisionalRegion, authoredRegions.Count)
                                            : "authored:no_template_match";
                    }
                }

                authoredRegions.Add(provisionalRegion with
                {
                    PreferAuthoredEnvelope = preferAuthoredEnvelope,
                    EnvelopePolicyReason = envelopePolicyReason,
                });

                if (isStereo)
                {
                    var stereoIdentityKey = BuildLoopAwareIdentityKey(region.StereoIdentityKey!, loopPolicy.LoopDescriptor);
                    var stereoSample = GetOrAddAuthoredSample(
                    authoredSamples,
                    stereoIdentityKey,
                    region.StereoSourceSampleName ?? $"{region.SourceSampleName}-R",
                    region.StereoPcm!,
                    region.SamplePitch,
                    loopPolicy.LoopDescriptor,
                    volume,
                    enableShortLoopPitchCompensation);

                    authoredRegions.Add(new AuthoredRegion(
                        stereoSample,
                        region.KeyLow,
                        region.KeyHigh,
                        region.VelocityLow,
                        region.VelocityHigh,
                        region.RegionPitch,
                        Math.Clamp(region.Volume, 0f, 1f),
                        GetStereoRightPan(region.Pan),
                        envelope,
                        true,
                        preferAuthoredEnvelope,
                        sourceInfo with
                        {
                            SourceSampleName = region.StereoSourceSampleName ?? $"{region.SourceSampleName}-R",
                        },
                        envelopePolicyReason,
                        loopPolicy.LoopPolicyReason,
                        loopPolicy.UsedTemplateLoopPolicy,
                        loopPolicy.LoopTemplateMatchKind));
                }
            }

            var instrument = new AuthoredInstrument(instrumentIndex, preset.Name, preset.Program, authoredRegions);
            instruments.Add(instrument);
            programMap[presetRef] = new ProgramMapping((byte)instrumentIndex, preset.Name, instrument.Regions.Count);
            var usageLabel = usedPresetRefs.Contains(presetRef) ? "used" : "converted but unused";
            log.WriteLine($"Preset {presetRef.Bank}/{presetRef.Program} -> instrument {instrumentIndex}, authored {instrument.Regions.Count} region(s), {usageLabel}.");
            if (downmixedPseudoStereoRegions > 0)
            {
                log.WriteLine($"Preset {presetRef.Bank}/{presetRef.Program}: downmixed {downmixedPseudoStereoRegions} pseudo-stereo region(s) to centered mono for KH2 playback compatibility.");
            }
        }

        foreach (var (requestedPresetRef, resolvedPreset) in resolvedPresetMap.OrderBy(static pair => pair.Key.Bank).ThenBy(static pair => pair.Key.Program))
        {
            var resolvedPresetRef = new PresetRef(resolvedPreset.Bank, resolvedPreset.Program);
            if (requestedPresetRef == resolvedPresetRef || !programMap.TryGetValue(resolvedPresetRef, out var resolvedMapping))
            {
                continue;
            }

            var aliasInstrumentIndex = compactSparsePrograms
                ? AllocateCompactInstrumentIndex(usedInstrumentIndices, ref nextCompactInstrumentIndex)
                : AllocateInstrumentIndex(requestedPresetRef, usedInstrumentIndices);
            var resolvedInstrument = instruments.FirstOrDefault(instrument => instrument.Index == resolvedMapping.InstrumentIndex);
            if (resolvedInstrument is null)
            {
                programMap[requestedPresetRef] = resolvedMapping;
                continue;
            }

            var aliasInstrument = new AuthoredInstrument(
                aliasInstrumentIndex,
                $"{resolvedInstrument.PresetName} fallback {requestedPresetRef.Bank}/{requestedPresetRef.Program}",
                resolvedInstrument.TemplateInstrumentIndex,
                resolvedInstrument.Regions.ToList());
            instruments.Add(aliasInstrument);
            programMap[requestedPresetRef] = new ProgramMapping((byte)aliasInstrumentIndex, resolvedMapping.PresetName, aliasInstrument.Regions.Count);
            log.WriteLine($"Preset {requestedPresetRef.Bank}/{requestedPresetRef.Program} -> instrument {aliasInstrumentIndex}, aliased to fallback preset {resolvedPresetRef.Bank}/{resolvedPresetRef.Program}.");
        }

        if (effectivePitchVariantPresetRefs.Count > 0)
        {
            AddPitchVariantInstruments(instruments, usedInstrumentIndices, programMap, effectivePitchVariantPresetRefs, log);
        }

        var channelPlans = BuildTrackPlans(midi, programMap, warnings, enablePitchBendWorkaround);

        if (midi.Tracks.SelectMany(static track => track.Events).OfType<MidiPitchBendEvent>().Any())
        {
            warnings.Add(enablePitchBendWorkaround
                ? "Pitch-bend events are approximated by bend-aware note retargeting plus fine-tuned instrument variants. Continuous bends are not yet emitted as native KH2 pitch opcodes."
                : "Pitch-bend events are ignored because midi_pitch_bend_workaround=0.");
        }

        log.WriteLine($"MIDI analysis: format {midi.Format}, PPQN {midi.Division}, {midi.Tracks.Count} track(s).");
        log.WriteLine($"SoundFont analysis: {soundFont.Presets.Count} preset(s), {usedPresetRefs.Count} preset(s) referenced by the MIDI, {instruments.Count} instrument(s) authored into the WD.");
        log.WriteLine($"Authored WD plan: {instruments.Count} instrument(s), {instruments.Sum(static instrument => instrument.Regions.Count)} region(s), {authoredSamples.Count} unique sample(s).");
        log.WriteLine($"Loop policy: source looped {sourceLoopingRegionCount} region(s), source one-shot {sourceOneShotRegionCount}, template forced one-shot {templateForcedOneShotRegionCount} ({exactTemplateOneShotMatchCount} exact-match, {templateInstrumentAllOneShotCount} instrument-wide), source loops preserved {sourceLoopPreservedCount}.");

        foreach (var warning in warnings.OrderBy(static warning => warning))
        {
            log.WriteLine($"Conversion warning: {warning}");
        }

        return new ConversionPlan(
            instruments,
            authoredSamples.Values.ToList(),
            programMap,
            channelPlans,
            warnings.OrderBy(static warning => warning).ToList());
    }

    private static ConversionPlan ConstrainPlanToWdBudget(ConversionPlan plan, int maxWdBytes, TextWriter log)
    {
        if (plan.Samples.Count == 0)
        {
            return plan;
        }

        var currentTotalBytes = EstimateWdSizeBytes(plan);
        if (currentTotalBytes <= maxWdBytes)
        {
            return plan;
        }

        var currentSampleBytes = EstimateStoredSampleBytes(plan.Samples);
        var fixedBytes = currentTotalBytes - currentSampleBytes;
        var availableSampleBytes = maxWdBytes - fixedBytes;
        if (availableSampleBytes <= 0)
        {
            throw new InvalidDataException(
                $"The authored WD layout needs {fixedBytes} bytes before sample data, which already exceeds the configured maximum WD size of {maxWdBytes} bytes.");
        }

        var scale = Math.Clamp((availableSampleBytes / (double)currentSampleBytes) * 0.985, 0.05, 1.0);
        ConversionPlan constrainedPlan = plan;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            constrainedPlan = ResamplePlanSamples(constrainedPlan, scale);
            currentTotalBytes = EstimateWdSizeBytes(constrainedPlan);
            if (currentTotalBytes <= maxWdBytes)
            {
                log.WriteLine(
                    $"WD size guard: resampled authored SF2 content by a {Math.Round(scale * 100.0, MidpointRounding.AwayFromZero)}% sample-rate budget (about {Math.Round(SpuSampleRate * scale)} Hz relative to a 44100 Hz baseline) so the rebuilt WD stays within {maxWdBytes} bytes.");
                return constrainedPlan;
            }

            var constrainedSampleBytes = EstimateStoredSampleBytes(constrainedPlan.Samples);
            scale *= Math.Clamp((availableSampleBytes / (double)constrainedSampleBytes) * 0.99, 0.5, 0.99);
        }

        throw new InvalidDataException(
            $"The authored WD would still exceed the maximum size of {maxWdBytes} bytes even after conservative sample-rate reduction.");
    }

    private static ConversionPlan BuildPlanFromOriginalWd(MidiFile midi, WdBankFile bank, bool enablePitchBendWorkaround, TextWriter log)
    {
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var usedPresetRefs = GetUsedPresetRefs(midi);
        var availableInstrumentIndices = bank.Regions
            .Select(static region => region.InstrumentIndex)
            .Distinct()
            .OrderBy(static index => index)
            .ToHashSet();

        var programMap = new Dictionary<PresetRef, ProgramMapping>();
        var missingInstrumentRefs = new List<PresetRef>();
        foreach (var presetRef in usedPresetRefs.OrderBy(static preset => preset.Bank).ThenBy(static preset => preset.Program))
        {
            if (availableInstrumentIndices.Contains(presetRef.Program))
            {
                var regionCount = bank.Regions.Count(region => region.InstrumentIndex == presetRef.Program);
                programMap[presetRef] = new ProgramMapping((byte)presetRef.Program, $"WD instrument {presetRef.Program}", regionCount);
                continue;
            }

            missingInstrumentRefs.Add(presetRef);
        }

        if (missingInstrumentRefs.Count > 0)
        {
            var missingList = string.Join(", ", missingInstrumentRefs.Select(static preset => $"{preset.Bank}/{preset.Program}"));
            var availableList = string.Join(", ", availableInstrumentIndices.Select(static index => $"0/{index}"));
            throw new InvalidDataException(
                $"The MIDI references program(s) that do not exist as instrument indices in the original WD. Missing: {missingList}. Available WD instruments: {availableList}.");
        }

        var trackPlans = BuildTrackPlans(midi, programMap, warnings, enablePitchBendWorkaround);
        log.WriteLine($"WD fallback analysis: {availableInstrumentIndices.Count} original instrument(s) available, {programMap.Count} MIDI program mapping(s) resolved directly against the original WD.");

        return new ConversionPlan(
            [],
            [],
            programMap,
            trackPlans,
            warnings.OrderBy(static warning => warning).ToList());
    }

    private static HashSet<PresetRef> GetUsedPresetRefs(MidiFile midi)
    {
        var used = new HashSet<PresetRef>();
        foreach (var sourceTrack in GetSourceTrackGroups(midi))
        {
            var bankMsb = 0;
            var bankLsb = 0;
            var currentProgram = 0;

            foreach (var evt in sourceTrack.Events)
            {
                switch (evt)
                {
                    case MidiControlChangeEvent control when control.Controller == 0:
                        bankMsb = control.Value;
                        break;
                    case MidiControlChangeEvent control when control.Controller == 32:
                        bankLsb = control.Value;
                        break;
                    case MidiProgramChangeEvent programChange:
                        currentProgram = programChange.Program;
                        break;
                    case MidiNoteOnEvent:
                        used.Add(GetEffectivePresetRef(sourceTrack.Channel, bankMsb, bankLsb, currentProgram));
                        break;
                }
            }
        }

        return used;
    }

    private static Dictionary<PresetRef, int> GetPreferredVelocities(MidiFile midi)
    {
        var velocities = new Dictionary<PresetRef, List<int>>();
        foreach (var sourceTrack in GetSourceTrackGroups(midi))
        {
            var bankMsb = 0;
            var bankLsb = 0;
            var currentProgram = 0;

            foreach (var evt in sourceTrack.Events)
            {
                switch (evt)
                {
                    case MidiControlChangeEvent control when control.Controller == 0:
                        bankMsb = control.Value;
                        break;
                    case MidiControlChangeEvent control when control.Controller == 32:
                        bankLsb = control.Value;
                        break;
                    case MidiProgramChangeEvent programChange:
                        currentProgram = programChange.Program;
                        break;
                    case MidiNoteOnEvent noteOn:
                        var presetRef = GetEffectivePresetRef(sourceTrack.Channel, bankMsb, bankLsb, currentProgram);
                        if (!velocities.TryGetValue(presetRef, out var samples))
                        {
                            samples = [];
                            velocities.Add(presetRef, samples);
                        }

                        samples.Add(noteOn.Velocity);
                        break;
                }
            }
        }

        var result = new Dictionary<PresetRef, int>();
        foreach (var pair in velocities)
        {
            var ordered = pair.Value.OrderBy(static value => value).ToArray();
            if (ordered.Length == 0)
            {
                continue;
            }

            var median = ordered[ordered.Length / 2];
            result[pair.Key] = Math.Clamp(median, 1, 127);
        }

        return result;
    }

    private static HashSet<PresetRef> GetPitchBendPresetRefs(MidiFile midi)
    {
        var used = new HashSet<PresetRef>();
        foreach (var sourceTrack in GetSourceTrackGroups(midi))
        {
            if (!sourceTrack.Events.OfType<MidiPitchBendEvent>().Any())
            {
                continue;
            }

            var bankMsb = 0;
            var bankLsb = 0;
            var currentProgram = 0;

            foreach (var evt in sourceTrack.Events)
            {
                switch (evt)
                {
                    case MidiControlChangeEvent control when control.Controller == 0:
                        bankMsb = control.Value;
                        break;
                    case MidiControlChangeEvent control when control.Controller == 32:
                        bankLsb = control.Value;
                        break;
                    case MidiProgramChangeEvent programChange:
                        currentProgram = programChange.Program;
                        break;
                    case MidiNoteOnEvent:
                        used.Add(GetEffectivePresetRef(sourceTrack.Channel, bankMsb, bankLsb, currentProgram));
                        break;
                }
            }
        }

        return used;
    }

    private static bool UsesImplicitGeneralMidiPercussionBank(MidiFile midi)
    {
        foreach (var sourceTrack in GetSourceTrackGroups(midi))
        {
            if (sourceTrack.Channel != GeneralMidiPercussionChannel)
            {
                continue;
            }

            var bankMsb = 0;
            var bankLsb = 0;
            foreach (var evt in sourceTrack.Events)
            {
                switch (evt)
                {
                    case MidiControlChangeEvent control when control.Controller == 0:
                        bankMsb = control.Value;
                        break;
                    case MidiControlChangeEvent control when control.Controller == 32:
                        bankLsb = control.Value;
                        break;
                    case MidiNoteOnEvent when GetBankNumber(bankMsb, bankLsb) == 0:
                        return true;
                }
            }
        }

        return false;
    }

    private static int AllocateInstrumentIndex(PresetRef presetRef, HashSet<int> usedInstrumentIndices)
    {
        if (presetRef.Program is >= 0 and <= byte.MaxValue && usedInstrumentIndices.Add(presetRef.Program))
        {
            return presetRef.Program;
        }

        for (var index = 0; index <= byte.MaxValue; index++)
        {
            if (usedInstrumentIndices.Add(index))
            {
                return index;
            }
        }

        throw new InvalidDataException("The converted MIDI references more than 256 unique SoundFont programs, which exceeds the PS2 BGM program limit.");
    }

    private static int AllocateCompactInstrumentIndex(HashSet<int> usedInstrumentIndices, ref int nextCompactInstrumentIndex)
    {
        while (nextCompactInstrumentIndex <= byte.MaxValue)
        {
            var index = nextCompactInstrumentIndex++;
            if (usedInstrumentIndices.Add(index))
            {
                return index;
            }
        }

        throw new InvalidDataException("The compacted PS2 instrument map would exceed the 256 program limit.");
    }

    private static int AllocateNextInstrumentIndex(HashSet<int> usedInstrumentIndices)
    {
        for (var index = 0; index <= byte.MaxValue; index++)
        {
            if (usedInstrumentIndices.Add(index))
            {
                return index;
            }
        }

        throw new InvalidDataException("The converted MIDI references more than 256 authored instruments, which exceeds the PS2 BGM program limit.");
    }

    private static bool ResolveProgramCompactionMode(
        MidiProgramCompactionMode mode,
        Sf2BankMode sf2BankMode,
        IReadOnlyCollection<PresetRef> usedPresetRefs,
        IReadOnlyCollection<PresetRef> pitchVariantPresetRefs)
    {
        return mode switch
        {
            MidiProgramCompactionMode.Compact => true,
            MidiProgramCompactionMode.Preserve => false,
            _ => sf2BankMode == Sf2BankMode.Used && ShouldCompactSparsePrograms(usedPresetRefs, pitchVariantPresetRefs),
        };
    }

    private static bool ShouldCompactSparsePrograms(
        IReadOnlyCollection<PresetRef> usedPresetRefs,
        IReadOnlyCollection<PresetRef> pitchVariantPresetRefs)
    {
        if (usedPresetRefs.Count == 0)
        {
            return false;
        }

        if (pitchVariantPresetRefs.Count > 0)
        {
            return false;
        }

        var maxProgram = usedPresetRefs.Max(static preset => preset.Program);
        var distinctPrograms = usedPresetRefs.Select(static preset => preset.Program).Distinct().Count();
        var spread = maxProgram + 1;
        return maxProgram > 31 && spread > (distinctPrograms * 2);
    }

    private static bool ShouldEnableShortLoopPitchCompensation(
        IReadOnlyList<SoundFontPreset> presetsToAuthor,
        HashSet<string> warnings)
    {
        var totalRegions = 0;
        var foundVeryShortLoop = false;
        foreach (var preset in presetsToAuthor)
        {
            foreach (var region in SoundFontParser.NormalizeRegions(preset.Regions, warnings))
            {
                totalRegions++;
                if (region.Looping && region.Pcm.Length > 0)
                {
                    var safeLoopStart = Math.Clamp(region.LoopStartSample, 0, Math.Max(0, region.Pcm.Length - 1));
                    var loopLength = region.LoopDescriptor.ResolveLengthSamples(region.Pcm.Length);
                    if (loopLength > 0 && loopLength <= ShortLoopAlignmentThresholdSamples)
                    {
                        foundVeryShortLoop = true;
                    }
                }
            }
        }

        if (foundVeryShortLoop)
        {
            return true;
        }

        return totalRegions > 0 && totalRegions <= 12;
    }

    private static List<SoundFontRegion> CollapsePseudoStereoRegions(List<SoundFontRegion> regions)
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
                if (!CanCollapsePseudoStereoRegion(current, candidate))
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
                IdentityKey = $"{left.IdentityKey}|{right.IdentityKey}",
                SourceSampleName = $"{left.SourceSampleName}+{right.SourceSampleName}",
                Pan = 0f,
                Pcm = MixPseudoStereoToMono(left.Pcm, right.Pcm),
                StereoIdentityKey = null,
                StereoSourceSampleName = null,
                StereoPcm = null,
            });
        }

        return result;
    }

    private static short[] MixPseudoStereoToMono(short[] left, short[] right)
    {
        var length = Math.Max(left.Length, right.Length);
        var mixed = new short[length];
        for (var index = 0; index < length; index++)
        {
            var leftSample = index < left.Length ? left[index] : 0;
            var rightSample = index < right.Length ? right[index] : 0;
            mixed[index] = (short)Math.Clamp(
                (int)Math.Round((leftSample + rightSample) / 2.0, MidpointRounding.AwayFromZero),
                short.MinValue,
                short.MaxValue);
        }

        return mixed;
    }

    private static bool CanCollapsePseudoStereoRegion(SoundFontRegion left, SoundFontRegion right)
    {
        if (left.StereoPcm is not null || right.StereoPcm is not null ||
            left.StereoIdentityKey is not null || right.StereoIdentityKey is not null)
        {
            return false;
        }

        if (left.IdentityKey == right.IdentityKey ||
            left.KeyLow != right.KeyLow ||
            left.KeyHigh != right.KeyHigh ||
            left.VelocityLow != right.VelocityLow ||
            left.VelocityHigh != right.VelocityHigh ||
            left.SamplePitch != right.SamplePitch ||
            left.RegionPitch != right.RegionPitch ||
            left.InitialFilterFcCents != right.InitialFilterFcCents ||
            Math.Abs(left.InitialFilterCutoffHz - right.InitialFilterCutoffHz) >= 0.001)
        {
            return false;
        }

        if (Math.Abs(left.Volume - right.Volume) >= 0.15f ||
            Math.Abs(left.PresetBank - right.PresetBank) > 0 ||
            Math.Abs(left.PresetProgram - right.PresetProgram) > 0)
        {
            return false;
        }

        return left.Pan < 0f &&
               right.Pan > 0f &&
               Math.Abs(left.Pan + right.Pan) < 0.25f;
    }

    private static void AddPitchVariantInstruments(
        List<AuthoredInstrument> instruments,
        HashSet<int> usedInstrumentIndices,
        Dictionary<PresetRef, ProgramMapping> programMap,
        HashSet<PresetRef> pitchVariantPresetRefs,
        TextWriter log)
    {
        if (pitchVariantPresetRefs.Count == 0)
        {
            return;
        }

        var instrumentLookup = instruments.ToDictionary(static instrument => instrument.Index);
        foreach (var presetRef in pitchVariantPresetRefs.OrderBy(static preset => preset.Bank).ThenBy(static preset => preset.Program))
        {
            if (!programMap.TryGetValue(presetRef, out var mapping) ||
                !instrumentLookup.TryGetValue(mapping.InstrumentIndex, out var baseInstrument))
            {
                continue;
            }

            var variants = new Dictionary<int, byte>
            {
                [0] = mapping.InstrumentIndex,
            };

            foreach (var cents in EnumeratePitchVariantCents())
            {
                var instrumentIndex = AllocateNextInstrumentIndex(usedInstrumentIndices);
                var variantRegions = baseInstrument.Regions
                    .Select(region => RetuneRegion(region, cents))
                    .ToList();
                var variantInstrument = new AuthoredInstrument(
                    instrumentIndex,
                    $"{baseInstrument.PresetName} pitch {cents:+#;-#;0}c",
                    baseInstrument.TemplateInstrumentIndex,
                    variantRegions);
                instruments.Add(variantInstrument);
                instrumentLookup[instrumentIndex] = variantInstrument;
                variants[cents] = (byte)instrumentIndex;
            }

            programMap[presetRef] = mapping with { PitchVariantPrograms = variants };
            log.WriteLine($"Pitch variants: preset {presetRef.Bank}/{presetRef.Program} -> {variants.Count} tuned instrument state(s) for bend-aware playback.");
        }
    }

    private static IEnumerable<int> EnumeratePitchVariantCents()
    {
        for (var cents = -PitchVariantMaxResidualCents; cents <= PitchVariantMaxResidualCents; cents += PitchVariantStepCents)
        {
            if (cents != 0)
            {
                yield return cents;
            }
        }
    }

    private static SoundFontPreset? ResolvePreset(
        SoundFontFile soundFont,
        PresetRef presetRef,
        HashSet<string> warnings,
        HashSet<PresetRef> missingPresetRefs)
    {
        var preset = soundFont.FindPreset(presetRef.Bank, presetRef.Program);
        if (preset is not null)
        {
            return preset;
        }

        var midiMsbPreset = soundFont.FindPresetByMidiMsbBank(presetRef.Bank, presetRef.Program);
        if (midiMsbPreset is not null)
        {
            warnings.Add($"MIDI bank {presetRef.Bank}, program {presetRef.Program} resolved to SoundFont bank {midiMsbPreset.Bank}/{midiMsbPreset.Program} ({midiMsbPreset.Name}) via direct CC0/MSB-style bank mapping.");
            return midiMsbPreset;
        }

        preset = soundFont.FindPresetExactOrCoarse(presetRef.Bank, presetRef.Program);
        if (preset is not null)
        {
            return preset;
        }

        if (IsPercussionBank(presetRef.Bank))
        {
            warnings.Add($"No SoundFont preset found for percussion bank {presetRef.Bank}, program {presetRef.Program}. Falling back to percussion bank 128 if possible.");
            var percussionFallback = soundFont.FindPercussionFallbackPreset(presetRef.Program);
            if (percussionFallback is not null)
            {
                warnings.Add($"Percussion preset {presetRef.Bank}/{presetRef.Program} resolved to SoundFont preset {percussionFallback.Bank}/{percussionFallback.Program} ({percussionFallback.Name}).");
                return percussionFallback;
            }
        }

        warnings.Add($"No SoundFont preset found for bank {presetRef.Bank}, program {presetRef.Program}. Falling back to bank 0 if possible.");
        preset = soundFont.FindPreset(0, presetRef.Program);
        if (preset is not null)
        {
            return preset;
        }

        warnings.Add($"Skipping missing SoundFont preset bank {presetRef.Bank}, program {presetRef.Program}.");
        missingPresetRefs.Add(presetRef);
        return null;
    }

    private static bool IsPercussionBank(int bank)
    {
        return bank == 128 || (bank & ~0x7F) == 128;
    }

    private static List<(int Channel, string Name, List<MidiEvent> Events)> GetSourceTrackGroups(MidiFile midi)
    {
        var sourceTrackGroups = new List<(int Channel, string Name, List<MidiEvent> Events)>();
        foreach (var track in midi.Tracks)
        {
            var perChannel = track.Events
                .Where(static evt => evt.Channel is >= 0 and < 16)
                .GroupBy(static evt => evt.Channel)
                .OrderBy(static group => group.Key)
                .ToList();
            foreach (var channelGroup in perChannel)
            {
                var groupedEvents = channelGroup
                    .OrderBy(static evt => evt.Tick)
                    .ThenBy(static evt => EventPriority(evt))
                    .ThenBy(static evt => evt.Order)
                    .ToList();
                if (!groupedEvents.OfType<MidiNoteOnEvent>().Any())
                {
                    continue;
                }

                var trackName = string.IsNullOrWhiteSpace(track.Name)
                    ? $"Channel {channelGroup.Key + 1}"
                    : track.Name;
                if (perChannel.Count > 1)
                {
                    trackName = $"{trackName} (ch {channelGroup.Key + 1})";
                }

                sourceTrackGroups.Add((channelGroup.Key, trackName, groupedEvents));
            }
        }

        return sourceTrackGroups;
    }

    private static List<AuthoredTrackPlan> BuildTrackPlans(
        MidiFile midi,
        IReadOnlyDictionary<PresetRef, ProgramMapping> programMap,
        HashSet<string> warnings,
        bool enablePitchBendWorkaround)
    {
        var sourceTrackGroups = GetSourceTrackGroups(midi);
        var plans = new List<AuthoredTrackPlan>();
        foreach (var sourceTrack in sourceTrackGroups)
        {
            var channel = sourceTrack.Channel;
            var events = sourceTrack.Events;
            var authoredEvents = new List<AuthoredTrackEvent>();
            var bankMsb = 0;
            var bankLsb = 0;
            var currentProgram = 0;
            byte? emittedProgram = null;
            int? emittedVolume = null;
            int? emittedExpression = null;
            int? emittedPan = null;
            var sustainDown = false;
            var activeNotes = new List<ActiveChannelNote>();
            var currentPitchBend = 0;
            var rpnMsb = 127;
            var rpnLsb = 127;
            var bendRangeSemitones = 2.0;
            var trackName = sourceTrack.Name;

            foreach (var evt in events)
            {
                switch (evt)
                {
                    case MidiControlChangeEvent control when control.Controller == 0:
                        bankMsb = control.Value;
                        break;
                    case MidiControlChangeEvent control when control.Controller == 32:
                        bankLsb = control.Value;
                        break;
                    case MidiControlChangeEvent control when control.Controller == 100:
                        rpnLsb = control.Value;
                        break;
                    case MidiControlChangeEvent control when control.Controller == 101:
                        rpnMsb = control.Value;
                        break;
                    case MidiControlChangeEvent control when control.Controller == 6:
                        if (rpnMsb == 0 && rpnLsb == 0)
                        {
                            bendRangeSemitones = Math.Clamp(control.Value, 0, 24);
                        }

                        break;
                    case MidiControlChangeEvent control when control.Controller == 38:
                        if (rpnMsb == 0 && rpnLsb == 0)
                        {
                            bendRangeSemitones = Math.Clamp(Math.Truncate(bendRangeSemitones) + (Math.Clamp(control.Value, 0, 99) / 100.0), 0, 24);
                        }

                        break;
                    case MidiProgramChangeEvent programChange:
                        currentProgram = programChange.Program;
                        if (TryResolveProgramMapping(programMap, channel, bankMsb, bankLsb, currentProgram, out var mappedProgram))
                        {
                            if (emittedProgram != mappedProgram.InstrumentIndex)
                            {
                                authoredEvents.Add(new AuthoredProgramEvent(programChange.Tick, mappedProgram.InstrumentIndex));
                                emittedProgram = mappedProgram.InstrumentIndex;
                            }
                        }
                        else
                        {
                            warnings.Add($"No authored WD instrument exists for MIDI bank {GetBankNumber(bankMsb, bankLsb)}, program {currentProgram}. Notes on channel {channel + 1} may be skipped.");
                        }

                        break;
                    case MidiControlChangeEvent control when control.Controller == 7:
                    {
                        var mappedVolume = MapMidiVolumeController(control.Value);
                        if (emittedVolume != mappedVolume)
                        {
                            authoredEvents.Add(new AuthoredVolumeEvent(control.Tick, mappedVolume));
                            emittedVolume = mappedVolume;
                        }

                        break;
                    }
                    case MidiControlChangeEvent control when control.Controller == 10:
                        if (emittedPan != control.Value)
                        {
                            authoredEvents.Add(new AuthoredPanEvent(control.Tick, control.Value));
                            emittedPan = control.Value;
                        }

                        break;
                    case MidiControlChangeEvent control when control.Controller == 11:
                    {
                        var mappedExpression = MapMidiExpressionController(control.Value);
                        if (emittedExpression != mappedExpression)
                        {
                            authoredEvents.Add(new AuthoredExpressionEvent(control.Tick, mappedExpression));
                            emittedExpression = mappedExpression;
                        }

                        break;
                    }
                    case MidiControlChangeEvent control when control.Controller == 64:
                        if (control.Value >= 64)
                        {
                            sustainDown = true;
                        }
                        else
                        {
                            sustainDown = false;
                            foreach (var deferred in activeNotes.Where(static note => note.DeferredRelease).OrderBy(static note => note.SourceKey).ToList())
                            {
                                authoredEvents.Add(new AuthoredNoteOffEvent(control.Tick, deferred.EmittedKey));
                                activeNotes.Remove(deferred);
                            }
                        }

                        break;
                    case MidiNoteOffEvent noteOff:
                    {
                        var activeNote = FindActiveNote(activeNotes, noteOff.Key);
                        if (activeNote is null)
                        {
                            authoredEvents.Add(new AuthoredNoteOffEvent(noteOff.Tick, noteOff.Key));
                        }
                        else if (sustainDown)
                        {
                            activeNote.DeferredRelease = true;
                        }
                        else
                        {
                            authoredEvents.Add(new AuthoredNoteOffEvent(noteOff.Tick, activeNote.EmittedKey));
                            activeNotes.Remove(activeNote);
                        }
                        break;
                    }
                    case MidiNoteOnEvent noteOn:
                        foreach (var deferred in activeNotes.Where(note => note.SourceKey == noteOn.Key && note.DeferredRelease).ToList())
                        {
                            authoredEvents.Add(new AuthoredNoteOffEvent(noteOn.Tick, deferred.EmittedKey));
                            activeNotes.Remove(deferred);
                        }

                        if (TryResolveProgramMapping(programMap, channel, bankMsb, bankLsb, currentProgram, out var noteProgram))
                        {
                            var pitchTarget = ResolvePitchTarget(noteOn.Key, enablePitchBendWorkaround ? currentPitchBend : 0, bendRangeSemitones, noteProgram);
                            if (emittedProgram != pitchTarget.Program)
                            {
                                authoredEvents.Add(new AuthoredProgramEvent(noteOn.Tick, pitchTarget.Program));
                                emittedProgram = pitchTarget.Program;
                            }

                            authoredEvents.Add(new AuthoredNoteOnEvent(noteOn.Tick, pitchTarget.Key, noteOn.Velocity));
                            activeNotes.Add(new ActiveChannelNote(noteOn.Key, pitchTarget.Key, noteOn.Velocity));
                        }

                        break;
                    case MidiPitchBendEvent pitchBend:
                        if (!enablePitchBendWorkaround)
                        {
                            break;
                        }

                        currentPitchBend = pitchBend.Value;
                        if (!emittedProgram.HasValue || activeNotes.Count == 0)
                        {
                            break;
                        }

                        if (!TryResolveProgramMapping(programMap, channel, bankMsb, bankLsb, currentProgram, out var bendProgram))
                        {
                            break;
                        }

                        var retargetedNotes = activeNotes
                            .Select(activeNote => new
                            {
                                Note = activeNote,
                                Target = ResolvePitchTarget(activeNote.SourceKey, currentPitchBend, bendRangeSemitones, bendProgram),
                            })
                            .ToList();
                        var desiredProgram = retargetedNotes.Count == 0
                            ? emittedProgram.Value
                            : retargetedNotes[0].Target.Program;
                        var requiresRetarget = desiredProgram != emittedProgram.Value ||
                            retargetedNotes.Any(entry => entry.Target.Key != entry.Note.EmittedKey);
                        if (!requiresRetarget)
                        {
                            break;
                        }

                        foreach (var entry in retargetedNotes)
                        {
                            authoredEvents.Add(new AuthoredNoteOffEvent(pitchBend.Tick, entry.Note.EmittedKey));
                        }

                        if (desiredProgram != emittedProgram.Value)
                        {
                            authoredEvents.Add(new AuthoredProgramEvent(pitchBend.Tick, desiredProgram));
                            emittedProgram = desiredProgram;
                        }

                        foreach (var entry in retargetedNotes)
                        {
                            authoredEvents.Add(new AuthoredNoteOnEvent(pitchBend.Tick, entry.Target.Key, entry.Note.Velocity));
                            entry.Note.EmittedKey = entry.Target.Key;
                        }

                        break;
                }
            }

            if (activeNotes.Count > 0)
            {
                var releaseTick = authoredEvents.Count == 0 ? 0 : authoredEvents.Max(static evt => evt.Tick);
                foreach (var activeNote in activeNotes.OrderBy(static note => note.SourceKey))
                {
                    authoredEvents.Add(new AuthoredNoteOffEvent(releaseTick, activeNote.EmittedKey));
                }
            }

            if (!authoredEvents.OfType<AuthoredVolumeEvent>().Any())
            {
                authoredEvents.Add(new AuthoredVolumeEvent(0, 127));
            }

            if (!authoredEvents.OfType<AuthoredExpressionEvent>().Any())
            {
                authoredEvents.Add(new AuthoredExpressionEvent(0, 127));
            }

            if (!authoredEvents.OfType<AuthoredPanEvent>().Any())
            {
                authoredEvents.Add(new AuthoredPanEvent(0, DefaultMidiPanCenter));
            }

            authoredEvents = authoredEvents
                .OrderBy(static evt => evt.Tick)
                .ThenBy(static evt => EventPriority(evt))
                .ToList();
            plans.Add(new AuthoredTrackPlan(channel, trackName, authoredEvents, authoredEvents.Count));
        }

        return plans;
    }

    private static int EventPriority(MidiEvent evt)
    {
        return evt switch
        {
            MidiControlChangeEvent control when control.Controller is 0 or 32 => 0,
            MidiProgramChangeEvent => 1,
            MidiControlChangeEvent => 2,
            MidiNoteOffEvent => 3,
            MidiNoteOnEvent => 4,
            MidiPitchBendEvent => 5,
            _ => 10,
        };
    }

    private static int EventPriority(AuthoredTrackEvent evt)
    {
        return evt switch
        {
            AuthoredProgramEvent => 0,
            AuthoredVolumeEvent => 1,
            AuthoredExpressionEvent => 2,
            AuthoredPanEvent => 3,
            AuthoredNoteOffEvent => 4,
            AuthoredNoteOnEvent => 5,
            _ => 10,
        };
    }

    private static ActiveChannelNote? FindActiveNote(List<ActiveChannelNote> activeNotes, int sourceKey)
    {
        for (var index = activeNotes.Count - 1; index >= 0; index--)
        {
            var activeNote = activeNotes[index];
            if (activeNote.SourceKey == sourceKey && !activeNote.DeferredRelease)
            {
                return activeNote;
            }
        }

        for (var index = activeNotes.Count - 1; index >= 0; index--)
        {
            var activeNote = activeNotes[index];
            if (activeNote.SourceKey == sourceKey)
            {
                return activeNote;
            }
        }

        return null;
    }

    private static PitchTarget ResolvePitchTarget(int sourceKey, int pitchBendValue, double bendRangeSemitones, ProgramMapping program)
    {
        var clampedBend = Math.Clamp(pitchBendValue, -8192, 8191);
        var normalized = clampedBend >= 0
            ? clampedBend / 8191.0
            : clampedBend / 8192.0;
        var bentKey = sourceKey + (normalized * bendRangeSemitones);
        var emittedKey = Math.Clamp((int)Math.Round(bentKey, MidpointRounding.AwayFromZero), 0, 127);
        var residualCents = (int)Math.Round((bentKey - emittedKey) * 100.0, MidpointRounding.AwayFromZero);
        return new PitchTarget(ResolvePitchVariantProgram(program, residualCents), emittedKey);
    }

    private static byte ResolvePitchVariantProgram(ProgramMapping program, int residualCents)
    {
        if (program.PitchVariantPrograms is null || program.PitchVariantPrograms.Count == 0)
        {
            return program.InstrumentIndex;
        }

        var quantizedResidual = QuantizePitchVariantCents(residualCents);
        if (program.PitchVariantPrograms.TryGetValue(quantizedResidual, out var exactProgram))
        {
            return exactProgram;
        }

        var nearest = program.PitchVariantPrograms
            .OrderBy(pair => Math.Abs(pair.Key - quantizedResidual))
            .ThenBy(static pair => Math.Abs(pair.Key))
            .First();
        return nearest.Value;
    }

    private static int QuantizePitchVariantCents(int residualCents)
    {
        var clamped = Math.Clamp(residualCents, -PitchVariantMaxResidualCents, PitchVariantMaxResidualCents);
        return (int)(Math.Round(clamped / (double)PitchVariantStepCents, MidpointRounding.AwayFromZero) * PitchVariantStepCents);
    }

    private static bool TryResolveProgramMapping(
        IReadOnlyDictionary<PresetRef, ProgramMapping> programMap,
        int channel,
        int bankMsb,
        int bankLsb,
        int program,
        out ProgramMapping mappedProgram)
    {
        var exact = GetEffectivePresetRef(channel, bankMsb, bankLsb, program);
        if (programMap.TryGetValue(exact, out var exactMapping))
        {
            mappedProgram = exactMapping;
            return true;
        }

        var raw = new PresetRef(GetBankNumber(bankMsb, bankLsb), program);
        if (raw != exact && programMap.TryGetValue(raw, out var rawMapping))
        {
            mappedProgram = rawMapping;
            return true;
        }

        var coarse = new PresetRef(bankMsb << 7, program);
        if (programMap.TryGetValue(coarse, out var coarseMapping))
        {
            mappedProgram = coarseMapping;
            return true;
        }

        var fallback = new PresetRef(0, program);
        if (programMap.TryGetValue(fallback, out var fallbackMapping))
        {
            mappedProgram = fallbackMapping;
            return true;
        }

        mappedProgram = default!;
        return false;
    }

    private static PresetRef GetEffectivePresetRef(int channel, int bankMsb, int bankLsb, int program)
    {
        var bank = GetBankNumber(bankMsb, bankLsb);
        return ShouldUseGeneralMidiPercussionBank(channel, bank)
            ? new PresetRef(GeneralMidiPercussionBank, program)
            : new PresetRef(bank, program);
    }

    private static int GetBankNumber(int bankMsb, int bankLsb)
    {
        return (bankMsb << 7) | bankLsb;
    }

    private static bool ShouldUseGeneralMidiPercussionBank(int channel, int bank)
    {
        return channel == GeneralMidiPercussionChannel && bank == 0;
    }

    private static byte[] BuildWd(string originalWdPath, int bankId, ConversionPlan plan, TextWriter log)
    {
        var bank = WdBankFile.Load(originalWdPath);
        if (bank.Regions.Count == 0)
        {
            throw new InvalidDataException("The original WD does not contain any template regions.");
        }

        var templateHeader = new byte[Math.Min(0x20, bank.OriginalBytes.Length)];
        Buffer.BlockCopy(bank.OriginalBytes, 0, templateHeader, 0, templateHeader.Length);

        var authoredInstrumentCount = plan.Instruments.Count == 0
            ? 0
            : plan.Instruments.Max(static instrument => instrument.Index) + 1;
        var templateInstrumentCount = checked((int)BinaryHelpers.ReadUInt32LE(bank.OriginalBytes, 0x8));
        var instrumentCount = Math.Max(authoredInstrumentCount, templateInstrumentCount);
        var totalRegions = plan.Instruments.Sum(static instrument => instrument.Regions.Count);
        var regionTableOffset = Align16(0x20 + (instrumentCount * 4));
        var sampleCollectionOffset = regionTableOffset + (totalRegions * 0x20);

        var sampleOffsetLookup = new Dictionary<string, int>(StringComparer.Ordinal);
        var sampleBytes = new List<byte>();
        foreach (var sample in plan.Samples)
        {
            sampleOffsetLookup.Add(sample.IdentityKey, sampleBytes.Count);
            sampleBytes.AddRange(WdLayoutHelpers.CreateStoredSampleChunk(sample.EncodedBytes));
        }

        var output = new byte[sampleCollectionOffset + sampleBytes.Count];
        Buffer.BlockCopy(templateHeader, 0, output, 0, templateHeader.Length);
        BinaryHelpers.WriteUInt16LE(output, 0x2, (ushort)bankId);
        BinaryHelpers.WriteUInt32LE(output, 0x4, (uint)sampleBytes.Count);
        BinaryHelpers.WriteUInt32LE(output, 0x8, (uint)instrumentCount);
        BinaryHelpers.WriteUInt32LE(output, 0xC, (uint)totalRegions);

        var currentRegionOffset = regionTableOffset;
        var instrumentByIndex = plan.Instruments.ToDictionary(static instrument => instrument.Index);
        var templateRegionsByInstrument = bank.Regions
            .GroupBy(static region => region.InstrumentIndex)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(region => region.RegionIndex).ToList());
        var templateEnvelopeReuseCount = 0;
        var authoredEnvelopeCount = 0;
        var templateEnvelopeMissCount = 0;
        for (var instrumentIndex = 0; instrumentIndex < instrumentCount; instrumentIndex++)
        {
            BinaryHelpers.WriteUInt32LE(output, 0x20 + (instrumentIndex * 4), (uint)currentRegionOffset);
            if (!instrumentByIndex.TryGetValue(instrumentIndex, out var instrument))
            {
                continue;
            }

            for (var regionIndex = 0; regionIndex < instrument.Regions.Count; regionIndex++)
            {
                var region = instrument.Regions[regionIndex];
                var loopInfo = ResolveAuthoredSampleLoopInfo(region.Sample);
                var effectivePitch = ResolveEffectiveRegionPitch(region);
                var templateRegion = SelectTemplateRegion(templateRegionsByInstrument, bank.Regions, instrument, region, regionIndex);
                var envelopeTemplateRegion = region.PreferAuthoredEnvelope
                    ? null
                    : SelectEnvelopeTemplateRegion(templateRegionsByInstrument, instrument.TemplateInstrumentIndex, region, regionIndex);
                var regionBytes = new byte[0x20];
                var templateRegionOffset = templateRegion?.FileOffset ?? bank.Regions[0].FileOffset;
                Buffer.BlockCopy(bank.OriginalBytes, templateRegionOffset, regionBytes, 0, regionBytes.Length);

                regionBytes[0x00] = region.Stereo ? (byte)0x01 : (byte)0x00;
                regionBytes[0x01] = (byte)((regionIndex == 0 ? 0x01 : 0x00) | (regionIndex == instrument.Regions.Count - 1 ? 0x02 : 0x00));
                regionBytes[0x02] = (byte)((regionIndex == 0 || regionIndex == instrument.Regions.Count - 1) ? 0x01 : 0x00);
                BinaryHelpers.WriteUInt32LE(output, 0x20 + (instrument.Index * 4), (uint)(currentRegionOffset - (regionIndex * 0x20)));
                BinaryHelpers.WriteUInt32LE(regionBytes, 0x04, (uint)sampleOffsetLookup[region.Sample.IdentityKey]);
                BinaryHelpers.WriteUInt32LE(regionBytes, 0x08, (uint)WdLayoutHelpers.OffsetLoopStartForStoredChunk(loopInfo.Looping, loopInfo.LoopStartBytes));
                var adsr = envelopeTemplateRegion is not null
                    ? new AdsrEnvelope(envelopeTemplateRegion.Adsr1, envelopeTemplateRegion.Adsr2)
                    : region.Envelope;
                if (envelopeTemplateRegion is not null)
                {
                    templateEnvelopeReuseCount++;
                }
                else
                {
                    authoredEnvelopeCount++;
                    if (!region.PreferAuthoredEnvelope)
                    {
                        templateEnvelopeMissCount++;
                    }
                }

                BinaryHelpers.WriteUInt16LE(regionBytes, 0x0C, adsr.Adsr1);
                BinaryHelpers.WriteUInt16LE(regionBytes, 0x0E, adsr.Adsr2);
                EncodeRootNote(ComposeRootNote(effectivePitch.RootKey, effectivePitch.FineTuneCents), out var fineTune, out var unityKey);
                regionBytes[0x12] = fineTune;
                regionBytes[0x13] = unityKey;
                regionBytes[0x14] = (byte)Math.Clamp(region.KeyHigh, 0, 127);
                regionBytes[0x15] = (byte)Math.Clamp(region.VelocityHigh, 0, 127);
                regionBytes[0x16] = (byte)Math.Clamp((int)Math.Round(region.Volume * 127.0, MidpointRounding.AwayFromZero), 0, 127);
                regionBytes[0x17] = EncodeWdPan(region.Pan);
                regionBytes[0x18] = loopInfo.Looping ? (byte)0x02 : (byte)0x00;

                Buffer.BlockCopy(regionBytes, 0, output, currentRegionOffset, regionBytes.Length);
                currentRegionOffset += regionBytes.Length;
            }
        }

        sampleBytes.ToArray().CopyTo(output, sampleCollectionOffset);
        var nonLoopSamplesWithLoopFlags = plan.Samples
            .Where(sample => !ResolveAuthoredSampleLoopInfo(sample).Looping)
            .Count(sample => AnalyzeAdpcmFlags(sample.EncodedBytes).LoopFlagBlockCount > 0);
        var loopingRegionCount = plan.Instruments.Sum(static instrument => instrument.Regions.Count(region => ResolveAuthoredSampleLoopInfo(region.Sample).Looping));
        log.WriteLine($"Authored WD from MIDI+SF2: {instrumentCount} instrument(s), {totalRegions} region(s), {sampleBytes.Count} bytes of PSX-ADPCM sample data using KH2-style 16-byte zero lead-ins for each sample chunk.");
        log.WriteLine($"Loop diagnostics: {loopingRegionCount} looping region(s), {totalRegions - loopingRegionCount} one-shot region(s), {nonLoopSamplesWithLoopFlags} non-loop sample(s) still carry ADPCM loop flags.");
        log.WriteLine($"ADSR policy: reused template envelopes for {templateEnvelopeReuseCount} region(s), kept authored envelopes for {authoredEnvelopeCount} region(s), template exact-match misses {templateEnvelopeMissCount}.");
        return output;
    }

    private static int EstimateWdSizeBytes(ConversionPlan plan)
    {
        var instrumentCount = plan.Instruments.Count == 0
            ? 0
            : plan.Instruments.Max(static instrument => instrument.Index) + 1;
        var totalRegions = plan.Instruments.Sum(static instrument => instrument.Regions.Count);
        var regionTableOffset = Align16(0x20 + (instrumentCount * 4));
        var sampleCollectionOffset = regionTableOffset + (totalRegions * 0x20);
        return sampleCollectionOffset + EstimateStoredSampleBytes(plan.Samples);
    }

    private static int EstimateStoredSampleBytes(IEnumerable<AuthoredSample> samples)
        => samples.Sum(static sample => WdLayoutHelpers.CreateStoredSampleChunk(sample.EncodedBytes).Length);

    private static ConversionPlan ResamplePlanSamples(ConversionPlan plan, double scale)
    {
        var targetRate = Math.Clamp((int)Math.Round(SpuSampleRate * scale, MidpointRounding.AwayFromZero), 4_000, SpuSampleRate);
        if (targetRate == SpuSampleRate)
        {
            return plan;
        }

        var sampleMap = new Dictionary<string, AuthoredSample>(StringComparer.Ordinal);
        foreach (var sample in plan.Samples)
        {
            var currentSampleRate = ResolveStoredSampleRate(sample.SampleRate, SpuSampleRate);
            var targetSampleRate = Math.Clamp(
                (int)Math.Round(currentSampleRate * scale, MidpointRounding.AwayFromZero),
                4_000,
                currentSampleRate);
            if (targetSampleRate >= currentSampleRate)
            {
                sampleMap[sample.IdentityKey] = sample;
                continue;
            }

            var pitchSemitones = 12.0 * Math.Log(currentSampleRate / (double)targetSampleRate, 2.0);
            var resampledPcm = AudioDsp.ResampleMono(sample.Pcm, currentSampleRate, targetSampleRate);
            var loopScale = targetSampleRate / (double)currentSampleRate;
            var resampledRequestedLoop = sample.RequestedLooping
                ? sample.RequestedLoopDescriptor.ScaleSamples(loopScale, resampledPcm.Length)
                : LoopDescriptor.None;
            var prepared = PrepareLoopAlignedSample(resampledPcm, resampledRequestedLoop, false);
            var encoded = PsxAdpcmEncoder.Encode(prepared.Pcm, prepared.LoopDescriptor);
            sampleMap[sample.IdentityKey] = sample with
            {
                Pcm = prepared.Pcm,
                EncodedBytes = encoded,
                RequestedLoopDescriptor = resampledRequestedLoop.NormalizeToSamples(prepared.Pcm.Length),
                EffectiveLoopDescriptor = prepared.LoopDescriptor.NormalizeToSamples(prepared.Pcm.Length),
                PitchComponents = sample.PitchComponents with
                {
                    StoredSampleRate = targetSampleRate,
                    SampleRatePitchOffsetSemitones = sample.PitchComponents.SampleRatePitchOffsetSemitones + pitchSemitones,
                    LoopAlignmentPitchOffsetSemitones = prepared.PitchOffsetSemitones,
                },
            };
        }

        var instruments = plan.Instruments
            .Select(instrument => new AuthoredInstrument(
                instrument.Index,
                instrument.PresetName,
                instrument.TemplateInstrumentIndex,
                instrument.Regions.Select(region =>
                {
                    return region with
                    {
                        Sample = sampleMap[region.Sample.IdentityKey],
                    };
                }).ToList()))
            .ToList();

        var programMap = plan.ProgramMap.ToDictionary(static entry => entry.Key, static entry => entry.Value);
        return new ConversionPlan(
            instruments,
            sampleMap.Values.ToList(),
            programMap,
            plan.TrackPlans,
            plan.Warnings);
    }

    private static WdRegionEntry? SelectTemplateRegion(
        IReadOnlyDictionary<int, List<WdRegionEntry>> templateRegionsByInstrument,
        IReadOnlyList<WdRegionEntry> allTemplateRegions,
        AuthoredInstrument instrument,
        AuthoredRegion region,
        int regionIndex)
    {
        if (templateRegionsByInstrument.TryGetValue(instrument.TemplateInstrumentIndex, out var templateRegions) &&
            templateRegions.Count > 0)
        {
            if (regionIndex >= 0 && regionIndex < templateRegions.Count)
            {
                var exactIndexTemplate = templateRegions[regionIndex];
                if (exactIndexTemplate.KeyLow == region.KeyLow &&
                    exactIndexTemplate.KeyHigh == region.KeyHigh &&
                    exactIndexTemplate.VelocityLow == region.VelocityLow &&
                    exactIndexTemplate.VelocityHigh == region.VelocityHigh &&
                    exactIndexTemplate.Stereo == region.Stereo)
                {
                    return exactIndexTemplate;
                }
            }

            return templateRegions
                .OrderByDescending(template => ScoreTemplateRegion(template, region, regionIndex))
                .First();
        }

        return allTemplateRegions
            .OrderByDescending(template => ScoreTemplateRegion(template, region, regionIndex))
            .FirstOrDefault();
    }

    private static WdRegionEntry? SelectEnvelopeTemplateRegion(
        IReadOnlyDictionary<int, List<WdRegionEntry>> templateRegionsByInstrument,
        int templateInstrumentIndex,
        AuthoredRegion region,
        int regionIndex)
    {
        if (!templateRegionsByInstrument.TryGetValue(templateInstrumentIndex, out var templateRegions) ||
            templateRegions.Count == 0)
        {
            return null;
        }

        if (regionIndex >= 0 &&
            regionIndex < templateRegions.Count &&
            IsExactEnvelopeTemplateMatch(templateRegions[regionIndex], region))
        {
            return templateRegions[regionIndex];
        }

        var exactTemplate = templateRegions.FirstOrDefault(template => IsExactEnvelopeTemplateMatch(template, region));
        if (exactTemplate is not null)
        {
            return exactTemplate;
        }

        return templateRegions
            .OrderByDescending(template => ScoreTemplateRegion(template, region, regionIndex))
            .FirstOrDefault();
    }

    private static ResolvedLoopPolicy ResolveLoopPolicy(
        WdBankFile templateBank,
        IReadOnlyDictionary<int, List<WdRegionEntry>> templateRegionsByInstrument,
        int templateInstrumentIndex,
        int keyLow,
        int keyHigh,
        int velocityLow,
        int velocityHigh,
        bool stereo,
        LoopDescriptor sourceLoopDescriptor)
    {
        if (!sourceLoopDescriptor.Looping)
        {
            return new ResolvedLoopPolicy(LoopDescriptor.None, "source:one_shot", false, "source_non_loop");
        }

        if (!templateRegionsByInstrument.TryGetValue(templateInstrumentIndex, out var templateRegions) ||
            templateRegions.Count == 0)
        {
            return new ResolvedLoopPolicy(sourceLoopDescriptor, "source:sf2_loop_no_template", false, "no_template_instrument");
        }

        var exactTemplate = templateRegions.FirstOrDefault(template =>
            template.KeyLow == keyLow &&
            template.KeyHigh == keyHigh &&
            template.VelocityLow == velocityLow &&
            template.VelocityHigh == velocityHigh &&
            template.Stereo == stereo);
        if (exactTemplate is not null && !IsTemplateRegionLooping(templateBank, exactTemplate))
        {
            return new ResolvedLoopPolicy(LoopDescriptor.None, "template:exact_one_shot_match", true, "exact_one_shot_match");
        }

        if (templateRegions.All(template => !IsTemplateRegionLooping(templateBank, template)))
        {
            return new ResolvedLoopPolicy(LoopDescriptor.None, "template:instrument_all_one_shot", true, "instrument_all_one_shot");
        }

        return new ResolvedLoopPolicy(sourceLoopDescriptor, "source:sf2_loop_preserved", false, exactTemplate is null ? "no_exact_template_match" : "exact_looping_match");
    }

    private static bool IsExactEnvelopeTemplateMatch(WdRegionEntry template, AuthoredRegion region)
        => template.KeyLow == region.KeyLow &&
           template.KeyHigh == region.KeyHigh &&
           template.VelocityLow == region.VelocityLow &&
           template.VelocityHigh == region.VelocityHigh &&
           template.Stereo == region.Stereo;

    private static string DescribeEnvelopeTemplateUsage(WdRegionEntry template, AuthoredRegion region, int regionIndex)
    {
        if (IsExactEnvelopeTemplateMatch(template, region))
        {
            return "template:exact_structural_match";
        }

        return region.SourceInfo.WasDownmixedPseudoStereo
            ? "template:best_scored_region_after_downmix"
            : $"template:best_scored_region(score={ScoreTemplateRegion(template, region, regionIndex)})";
    }

    private static bool IsTemplateRegionLooping(WdBankFile bank, WdRegionEntry region)
    {
        var sample = bank.Samples.FirstOrDefault(sample => sample.RelativeOffset == region.SampleOffset);
        if (sample is not null)
        {
            return sample.GetEffectiveLoopInfo(
                region.LoopStartBytes > 0
                    ? LoopDescriptor.FromPsxAdpcmBytes(true, region.LoopStartBytes, Math.Max(0, sample.GetOutputBytes().Length - region.LoopStartBytes))
                    : LoopDescriptor.None).Looping;
        }

        var playbackFlag = region.FileOffset + 0x18 < bank.OriginalBytes.Length
            ? bank.OriginalBytes[region.FileOffset + 0x18]
            : (byte)0x00;
        return region.LoopStartBytes > 0 || (playbackFlag & 0x02) != 0;
    }

    private static string BuildLoopAwareIdentityKey(string baseIdentityKey, LoopDescriptor loopDescriptor)
        => $"{baseIdentityKey}|loop={(loopDescriptor.Looping ? 1 : 0)}|ls={(loopDescriptor.Looping ? Math.Max(0, loopDescriptor.ResolveStartSamples(int.MaxValue)) : 0)}|ll={(loopDescriptor.Looping ? Math.Max(0, loopDescriptor.ResolveLengthSamples(int.MaxValue)) : 0)}|lm={loopDescriptor.StartMeasure}/{loopDescriptor.LengthMeasure}";

    private static int ScoreTemplateRegion(WdRegionEntry template, AuthoredRegion region, int regionIndex)
    {
        var score = 0;
        var effectivePitch = ResolveEffectiveRegionPitch(region);
        var templateRootNote = ComposeRootNote(template.UnityKey, template.FineTuneCents);
        var effectiveRootNote = ComposeRootNote(effectivePitch.RootKey, effectivePitch.FineTuneCents);

        if (template.Stereo == region.Stereo)
        {
            score += 10_000;
        }

        if (template.KeyLow == region.KeyLow && template.KeyHigh == region.KeyHigh)
        {
            score += 4_000;
        }
        else
        {
            score += RangeOverlapScore(template.KeyLow, template.KeyHigh, region.KeyLow, region.KeyHigh) * 40;
            score -= Math.Abs(template.KeyLow - region.KeyLow) * 8;
            score -= Math.Abs(template.KeyHigh - region.KeyHigh) * 8;
        }

        if (template.VelocityLow == region.VelocityLow && template.VelocityHigh == region.VelocityHigh)
        {
            score += 2_000;
        }
        else
        {
            score += RangeOverlapScore(template.VelocityLow, template.VelocityHigh, region.VelocityLow, region.VelocityHigh) * 20;
            score -= Math.Abs(template.VelocityLow - region.VelocityLow) * 4;
            score -= Math.Abs(template.VelocityHigh - region.VelocityHigh) * 4;
        }

        score -= Math.Abs(template.RegionIndex - regionIndex) * 16;
        score -= (int)Math.Round(Math.Abs(templateRootNote - effectiveRootNote) * 24.0, MidpointRounding.AwayFromZero);
        score -= Math.Abs(template.Volume - region.Volume) < 0.0001f ? 0 : (int)Math.Round(Math.Abs(template.Volume - region.Volume) * 400.0, MidpointRounding.AwayFromZero);
        score -= Math.Abs(template.Pan - region.Pan) < 0.0001f ? 0 : (int)Math.Round(Math.Abs(template.Pan - region.Pan) * 300.0, MidpointRounding.AwayFromZero);
        score -= Math.Abs(template.Adsr1 - region.Envelope.Adsr1) / 8;
        score -= Math.Abs(template.Adsr2 - region.Envelope.Adsr2) / 8;
        return score;
    }

    private static List<MidiSf2InstrumentManifest> BuildInstrumentManifests(string originalWdPath, ConversionPlan plan)
    {
        if (plan.Instruments.Count == 0 || !File.Exists(originalWdPath))
        {
            return [];
        }

        var bank = WdBankFile.Load(originalWdPath);
        if (bank.Regions.Count == 0)
        {
            return [];
        }

        var templateRegionsByInstrument = bank.Regions
            .GroupBy(static region => region.InstrumentIndex)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(region => region.RegionIndex).ToList());

        return plan.Instruments
            .OrderBy(static instrument => instrument.Index)
            .Select(instrument =>
            {
                var regionManifests = instrument.Regions
                    .Select((region, regionIndex) =>
                    {
                        var resolvedPitch = ResolveEffectiveRegionPitch(region);
                        var loopInfo = ResolveAuthoredSampleLoopInfo(region.Sample);
                        var templateRegion = SelectTemplateRegion(templateRegionsByInstrument, bank.Regions, instrument, region, regionIndex);
                        var envelopeTemplateRegion = region.PreferAuthoredEnvelope
                            ? null
                            : SelectEnvelopeTemplateRegion(templateRegionsByInstrument, instrument.TemplateInstrumentIndex, region, regionIndex);
                        var loopPolicy = ResolveLoopPolicy(
                            bank,
                            templateRegionsByInstrument,
                            instrument.TemplateInstrumentIndex,
                            region.KeyLow,
                            region.KeyHigh,
                            region.VelocityLow,
                            region.VelocityHigh,
                            region.Stereo,
                            region.SourceInfo.SourceLoopDescriptor);
                        var usedTemplateEnvelope = envelopeTemplateRegion is not null;
                        var finalEnvelope = usedTemplateEnvelope
                            ? new AdsrEnvelope(envelopeTemplateRegion!.Adsr1, envelopeTemplateRegion.Adsr2)
                            : region.Envelope;
                        var envelopeMatchKind = region.PreferAuthoredEnvelope
                            ? "authored_forced"
                            : usedTemplateEnvelope
                                ? IsExactEnvelopeTemplateMatch(envelopeTemplateRegion!, region)
                                    ? "exact_structural_match"
                                    : "best_scored_region"
                                : "no_template_match";
                        var adpcmFlags = AnalyzeAdpcmFlags(region.Sample.EncodedBytes);
                        var sourceLoopManifest = CreateLoopManifest(region.SourceInfo.SourceLoopDescriptor);
                        var effectiveLoopManifest = CreateLoopManifest(region.SourceInfo.EffectiveLoopDescriptor);
                        var sourceManifest = new MidiSf2AuthoredRegionSourceManifest(
                            region.SourceInfo.SourceSampleName,
                            region.SourceInfo.SourceRootKey,
                            region.SourceInfo.SourceFineTuneCents,
                            region.SourceInfo.SourceSampleRate,
                            region.SourceInfo.InitialFilterFcCents,
                            region.SourceInfo.InitialFilterCutoffHz,
                            region.SourceInfo.AttackSeconds,
                            region.SourceInfo.HoldSeconds,
                            region.SourceInfo.DecaySeconds,
                            region.SourceInfo.SustainLevel,
                            region.SourceInfo.ReleaseSeconds,
                            region.SourceInfo.SourceLooping,
                            region.SourceInfo.SourceLoopStartSample,
                            region.SourceInfo.WasDownmixedPseudoStereo,
                            region.SourceInfo.SourceStereoPair,
                            region.SourceInfo.EffectiveLooping,
                            region.SourceInfo.EffectiveLoopStartSample,
                            sourceLoopManifest,
                            effectiveLoopManifest,
                            region.SourceInfo.SoundFontDebug);
                        var templateRegionManifest = templateRegion is null
                            ? null
                            : CreateTemplateRegionManifest(bank, templateRegion);
                        var envelopeTemplateManifest = envelopeTemplateRegion is null
                            ? null
                            : CreateTemplateRegionManifest(bank, envelopeTemplateRegion);
                        var pitchManifest = CreatePitchManifest(region);

                        return new MidiSf2RegionManifest(
                            regionIndex,
                            region.Sample.IdentityKey,
                            region.Sample.SourceSampleName,
                            region.KeyLow,
                            region.KeyHigh,
                            region.VelocityLow,
                            region.VelocityHigh,
                            resolvedPitch.RootKey,
                            resolvedPitch.FineTuneCents,
                            region.Volume,
                            region.Pan,
                            region.Stereo,
                            loopInfo.Looping,
                            loopInfo.LoopStartBytes,
                            CreateLoopManifest(region.Sample.EffectiveLoopDescriptor),
                            region.LoopPolicyReason,
                            region.UsedTemplateLoopPolicy,
                            region.LoopTemplateMatchKind,
                            adpcmFlags,
                            region.EnvelopePolicyReason,
                            region.PreferAuthoredEnvelope,
                            usedTemplateEnvelope,
                            envelopeMatchKind,
                            new MidiSf2EnvelopeManifest(region.Envelope.Adsr1, region.Envelope.Adsr2),
                            new MidiSf2EnvelopeManifest(finalEnvelope.Adsr1, finalEnvelope.Adsr2),
                            pitchManifest,
                            sourceManifest,
                            templateRegionManifest,
                            envelopeTemplateManifest,
                            loopPolicy.UsedTemplateLoopPolicy ? templateRegionManifest : null);
                    })
                    .ToList();

                return new MidiSf2InstrumentManifest(
                    instrument.Index,
                    instrument.PresetName,
                    instrument.TemplateInstrumentIndex,
                    regionManifests);
            })
            .ToList();
    }

    private static MidiSf2TemplateRegionManifest CreateTemplateRegionManifest(WdBankFile bank, WdRegionEntry region)
    {
        var sample = bank.Samples.FirstOrDefault(sample => sample.RelativeOffset == region.SampleOffset);
        var loopInfo = sample?.GetEffectiveLoopInfo(
            region.LoopStartBytes > 0
                ? LoopDescriptor.FromPsxAdpcmBytes(true, region.LoopStartBytes, Math.Max(0, sample.GetOutputBytes().Length - region.LoopStartBytes))
                : LoopDescriptor.None);
        return new MidiSf2TemplateRegionManifest(
            region.InstrumentIndex,
            region.RegionIndex,
            region.FileOffset,
            region.KeyLow,
            region.KeyHigh,
            region.VelocityLow,
            region.VelocityHigh,
            region.Stereo,
            region.UnityKey,
            region.FineTuneCents,
            region.Volume,
            region.Pan,
            loopInfo?.Looping ?? IsTemplateRegionLooping(bank, region),
            loopInfo?.LoopStartBytes ?? region.LoopStartBytes,
            new MidiSf2EnvelopeManifest(region.Adsr1, region.Adsr2));
    }

    private static MidiSf2PitchManifest CreatePitchManifest(AuthoredRegion region)
    {
        var rootNote = GetEffectiveRegionRootNote(region);
        EncodeRootNote(rootNote, out var rawFineTune, out var rawUnityKey);
        var encodedUnityKey = 0x3A - unchecked((sbyte)rawUnityKey);
        var encodedFineTuneCents = WdSampleTool.ConvertWdFineTune(rawFineTune);
        var encodedRootNote = ComposeRootNote(encodedUnityKey, encodedFineTuneCents);
        var quantizationErrorCents = (int)Math.Round((encodedRootNote - rootNote) * 100.0, MidpointRounding.AwayFromZero);

        return new MidiSf2PitchManifest(
            rootNote,
            region.Sample.PitchComponents.SourceRootNoteSemitones,
            region.Sample.PitchComponents.OriginalPitch,
            region.Sample.PitchComponents.PitchCorrectionCents,
            region.PitchComponents.OverridingRootKey,
            region.PitchComponents.CoarseTuneSemitones,
            region.PitchComponents.FineTuneCents,
            region.RegionPitchOffsetSemitones,
            region.Sample.SampleRate,
            region.Sample.PitchComponents.SampleRatePitchOffsetSemitones,
            region.Sample.PitchComponents.LoopAlignmentPitchOffsetSemitones,
            region.Sample.PitchOffsetSemitones,
            encodedUnityKey,
            encodedFineTuneCents,
            encodedRootNote,
            quantizationErrorCents);
    }

    private static MidiSf2LoopManifest CreateLoopManifest(LoopDescriptor loopDescriptor)
    {
        return new MidiSf2LoopManifest(
            loopDescriptor.Looping,
            loopDescriptor.Start,
            loopDescriptor.Length,
            loopDescriptor.StartMeasure.ToString(),
            loopDescriptor.LengthMeasure.ToString(),
            loopDescriptor.Type.ToString());
    }

    private static MidiSf2AdpcmFlagManifest AnalyzeAdpcmFlags(byte[] encodedBytes)
    {
        if (encodedBytes.Length == 0)
        {
            return new MidiSf2AdpcmFlagManifest(0, 0, 0, -1, 0, 0, 0);
        }

        var blockCount = encodedBytes.Length / 0x10;
        var loopFlagBlockCount = 0;
        var loopStartFlagBlockCount = 0;
        var endFlagBlockCount = 0;
        var firstLoopStartFlagBlockIndex = -1;
        byte firstBlockFlag = 0;
        byte lastBlockFlag = 0;
        for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
        {
            var flag = encodedBytes[(blockIndex * 0x10) + 1];
            if (blockIndex == 0)
            {
                firstBlockFlag = flag;
            }

            lastBlockFlag = flag;
            if ((flag & 0x02) != 0)
            {
                loopFlagBlockCount++;
            }

            if ((flag & 0x04) != 0)
            {
                loopStartFlagBlockCount++;
                if (firstLoopStartFlagBlockIndex < 0)
                {
                    firstLoopStartFlagBlockIndex = blockIndex;
                }
            }

            if ((flag & 0x01) != 0)
            {
                endFlagBlockCount++;
            }
        }

        return new MidiSf2AdpcmFlagManifest(blockCount, loopFlagBlockCount, loopStartFlagBlockCount, firstLoopStartFlagBlockIndex, endFlagBlockCount, firstBlockFlag, lastBlockFlag);
    }

    private static int RangeOverlapScore(int leftLow, int leftHigh, int rightLow, int rightHigh)
    {
        var overlapLow = Math.Max(leftLow, rightLow);
        var overlapHigh = Math.Min(leftHigh, rightHigh);
        return Math.Max(0, overlapHigh - overlapLow + 1);
    }

    private static byte[] BuildBgm(string originalBgmPath, int sequenceId, int bankId, MidiFile midi, ConversionPlan plan, bool midiLoop, TextWriter log)
    {
        var templateBytes = File.ReadAllBytes(originalBgmPath);
        if (templateBytes.Length < 0x20)
        {
            throw new InvalidDataException("Original .bgm is too small.");
        }

        var templateTrackCount = Math.Max(1, (int)templateBytes[0x08]);
        var targetPpqn = BinaryHelpers.ReadUInt16LE(templateBytes, 0x0E);
        if (targetPpqn == 0)
        {
            targetPpqn = DefaultPpqn;
        }

        var compactTrackCount = plan.TrackPlans.Count + 1;
        if (compactTrackCount > templateTrackCount)
        {
            throw new InvalidDataException(
                $"The MIDI requires {compactTrackCount} BGM tracks including conductor, but the original file only exposes {templateTrackCount} track slots.");
        }

        var conductorTrack = BuildConductorTrack(midi, targetPpqn);
        var authoredLoop = midiLoop ? DetermineAuthoredLoop(midi, targetPpqn, log) : null;
        var generatedPlaybackTracks = plan.TrackPlans
            .Select((track, index) => new GeneratedTrack(
                track.Channel,
                track.Name,
                BuildPlaybackTrack(
                    track.Events,
                    checked((ushort)midi.Division),
                    targetPpqn,
                    midiLoop ? authoredLoop : null,
                    midiLoop && index == 0)))
            .ToList();

        var trackLayout = ReadTrackLayout(templateBytes, templateTrackCount);
        var outputTrackCount = midiLoop ? templateTrackCount : compactTrackCount;
        var trackBytesBySlot = new byte[outputTrackCount][];
        var slotLengths = new int[outputTrackCount];
        trackBytesBySlot[0] = conductorTrack;
        slotLengths[0] = conductorTrack.Length;

        if (midiLoop)
        {
            for (var trackIndex = 1; trackIndex < outputTrackCount; trackIndex++)
            {
                byte[] trackBytes;
                if (trackIndex - 1 < generatedPlaybackTracks.Count)
                {
                    trackBytes = generatedPlaybackTracks[trackIndex - 1].Bytes;
                }
                else
                {
                    trackBytes = BuildSilentTrack();
                }

                trackBytesBySlot[trackIndex] = trackBytes;
                slotLengths[trackIndex] = trackBytes.Length;
            }
        }
        else
        {
            for (var trackIndex = 0; trackIndex < generatedPlaybackTracks.Count; trackIndex++)
            {
                var generatedTrack = generatedPlaybackTracks[trackIndex];
                var outputIndex = trackIndex + 1;
                trackBytesBySlot[outputIndex] = generatedTrack.Bytes;
                slotLengths[outputIndex] = generatedTrack.Bytes.Length;
            }
        }

        var needsExpansion = slotLengths.Where((length, index) => length > trackLayout[index].Length).Any();
        var outputLength = CalculateExpandedBgmLength(slotLengths);
        var largestGeneratedTrack = generatedPlaybackTracks
            .OrderByDescending(track => track.Bytes.Length)
            .FirstOrDefault();
        if (outputLength > MaxAuthoredBgmBytes)
        {
            var largestTrackMessage = string.Empty;
            if (largestGeneratedTrack is not null)
            {
                var trackSlotIndex = Math.Clamp(generatedPlaybackTracks.IndexOf(largestGeneratedTrack) + 1, 0, trackLayout.Count - 1);
                var originalSlotLength = trackLayout[trackSlotIndex].Length;
                largestTrackMessage =
                    $" The heaviest generated track is '{largestGeneratedTrack.Name}' (channel {largestGeneratedTrack.Channel + 1}) at {largestGeneratedTrack.Bytes.Length} bytes, while its original template slot was only {originalSlotLength} bytes.";
            }

            throw new InvalidDataException(
                $"The authored BGM would be {outputLength} bytes, but the current hard KH2 BGM safety cap is {MaxAuthoredBgmBytes} bytes. " +
                "The WD side already fits; this failure is on the BGM/sequence side, which is usually a sign that the MIDI is too dense for reliable ingame playback." +
                largestTrackMessage +
                " Try simplifying the MIDI itself: thin duplicate or repeated notes, reduce doubled chord/backing layers, or trim overly dense controller / pitch-bend data until the rebuilt BGM is below the cap.");
        }

        var maxAllowedLength = CalculateMaxAllowedBgmLength(templateBytes.Length, midiLoop);
        if (needsExpansion && outputLength > maxAllowedLength)
        {
            if (largestGeneratedTrack is not null)
            {
                var trackSlotIndex = generatedPlaybackTracks.IndexOf(largestGeneratedTrack) + 1;
                var originalSlotLength = trackLayout[trackSlotIndex].Length;
                throw new InvalidDataException(
                    $"The MIDI track '{largestGeneratedTrack.Name}' (channel {largestGeneratedTrack.Channel + 1}) needs {largestGeneratedTrack.Bytes.Length} bytes, but its corresponding compact BGM track slot only has {originalSlotLength} bytes in the template. " +
                    $"A safe expanded rebuild is capped at {maxAllowedLength} bytes, while this MIDI would require {outputLength} bytes.");
            }

            throw new InvalidDataException($"The MIDI is too dense for a safe PS2 rebuild. Expanded output would require {outputLength} bytes, but the conservative safety cap is {maxAllowedLength} bytes.");
        }

        var output = new byte[outputLength];
        Buffer.BlockCopy(templateBytes, 0, output, 0, Math.Min(0x20, templateBytes.Length));
        BinaryHelpers.WriteUInt16LE(output, 0x04, (ushort)sequenceId);
        BinaryHelpers.WriteUInt16LE(output, 0x06, (ushort)bankId);
        output[0x08] = (byte)Math.Clamp(outputTrackCount, 0, 255);
        BinaryHelpers.WriteUInt16LE(output, 0x0E, targetPpqn);
        BinaryHelpers.WriteUInt32LE(output, 0x10, (uint)output.Length);

        var cursor = 0x20;
        for (var trackIndex = 0; trackIndex < outputTrackCount; trackIndex++)
        {
            var slotLength = slotLengths[trackIndex];
            BinaryHelpers.WriteUInt32LE(output, cursor, (uint)slotLength);
            cursor += 4;

            var trackBytes = trackBytesBySlot[trackIndex] ?? Array.Empty<byte>();
            if (trackBytes.Length > slotLength)
            {
                throw new InvalidDataException(
                    $"The generated track {trackIndex} does not fit into its allocated BGM slot (needed {trackBytes.Length} bytes, slot has {slotLength}).");
            }

            Buffer.BlockCopy(trackBytes, 0, output, cursor, trackBytes.Length);
            cursor += slotLength;
        }

        var rebuildMode = needsExpansion
            ? $"expanded from {templateBytes.Length} to {output.Length} bytes"
            : $"in-place at {output.Length} bytes";
        if (midiLoop)
        {
            log.WriteLine(
                $"Authored BGM loop rebuild: preserved {outputTrackCount} original track slot(s), wrote {generatedPlaybackTracks.Count} playback track(s), target PPQN {targetPpqn}, trimmed per-track padding, {rebuildMode}.");
        }
        else
        {
            log.WriteLine(
                $"Authored BGM compact rebuild: wrote {outputTrackCount} track(s) (1 conductor + {generatedPlaybackTracks.Count} playback), target PPQN {targetPpqn}, trimmed per-track padding, {rebuildMode}.");
        }

        if (midiLoop && generatedPlaybackTracks.Count > 0)
        {
            log.WriteLine($"BGM loop option: enabled via config; wrote loop ticks begin={authoredLoop!.BeginTick}, end={authoredLoop.EndTick} on the first authored playback track while preserving the original slot layout.");
        }

        return output;
    }

    private static byte[] BuildConductorTrack(MidiFile midi, ushort targetPpqn)
    {
        var tempoEvents = midi.Tracks
            .SelectMany(static track => track.Events)
            .OfType<MidiTempoEvent>()
            .OrderBy(static evt => evt.Tick)
            .ThenBy(static evt => evt.Order)
            .ToList();

        if (tempoEvents.Count == 0)
        {
            tempoEvents.Add(new MidiTempoEvent(0, 0, 120));
        }

        var bytes = new List<byte> { 0x00, 0x0C, 0x04, 0x04 };
        long currentTick = 0;
        int? previousTempo = null;

        foreach (var tempo in tempoEvents)
        {
            if (previousTempo == tempo.Bpm)
            {
                continue;
            }

            var scaledTick = ScaleTick(tempo.Tick, checked((ushort)midi.Division), targetPpqn);
            WriteDelta(bytes, checked((int)Math.Max(0, scaledTick - currentTick)));
            bytes.Add(0x08);
            bytes.Add((byte)Math.Clamp(tempo.Bpm, 1, 255));
            currentTick = scaledTick;
            previousTempo = tempo.Bpm;
        }

        WriteDelta(bytes, 0);
        bytes.Add(0x00);
        return [.. bytes];
    }

    private static byte[] BuildPlaybackTrack(IReadOnlyList<AuthoredTrackEvent> events, ushort sourcePpqn, ushort targetPpqn, TemplateLoop? templateLoop = null, bool emitLoopMarkers = false)
    {
        var bytes = new List<byte>(Math.Max(32, events.Count * 4));
        long currentTick = 0;
        byte previousKey = 0;
        byte previousVelocity = 0;
        var emittedExplicitNoteOn = false;
        var emittedExplicitNoteOff = false;
        var loopTrack = templateLoop is not null;
        var loopBeginWritten = false;
        var loopBeginTick = loopTrack ? Math.Max(0L, templateLoop!.BeginTick) : 0L;
        var loopEndTick = loopTrack ? Math.Max(loopBeginTick, templateLoop!.EndTick) : 0L;
        foreach (var evt in events)
        {
            var scaledTick = ScaleTick(evt.Tick, sourcePpqn, targetPpqn);
            if (loopTrack && scaledTick >= loopEndTick)
            {
                break;
            }

            if (emitLoopMarkers &&
                !loopBeginWritten &&
                scaledTick >= loopBeginTick)
            {
                WriteDelta(bytes, checked((int)Math.Max(0, loopBeginTick - currentTick)));
                bytes.Add(0x02);
                currentTick = loopBeginTick;
                loopBeginWritten = true;
            }

            WriteDelta(bytes, checked((int)Math.Max(0, scaledTick - currentTick)));
            switch (evt)
            {
                case AuthoredProgramEvent program:
                    bytes.Add(0x20);
                    bytes.Add(program.Program);
                    break;
                case AuthoredVolumeEvent volume:
                    bytes.Add(0x22);
                    bytes.Add((byte)Math.Clamp(volume.Value, 0, 127));
                    break;
                case AuthoredExpressionEvent expression:
                    bytes.Add(0x24);
                    bytes.Add((byte)Math.Clamp(expression.Value, 0, 127));
                    break;
                case AuthoredPanEvent pan:
                    bytes.Add(0x26);
                    bytes.Add((byte)Math.Clamp(pan.Value, 0, 127));
                    break;
                case AuthoredNoteOnEvent noteOn:
                {
                    var key = (byte)Math.Clamp(noteOn.Key, 0, 127);
                    var velocity = (byte)Math.Clamp(noteOn.Velocity, 1, 127);
                    if (!emittedExplicitNoteOn)
                    {
                        bytes.Add(0x11);
                        bytes.Add(key);
                        bytes.Add(velocity);
                        emittedExplicitNoteOn = true;
                    }
                    else if (key == previousKey && velocity == previousVelocity)
                    {
                        bytes.Add(0x10);
                    }
                    else if (velocity == previousVelocity)
                    {
                        bytes.Add(0x12);
                        bytes.Add(key);
                    }
                    else if (key == previousKey)
                    {
                        bytes.Add(0x13);
                        bytes.Add(velocity);
                    }
                    else
                    {
                        bytes.Add(0x11);
                        bytes.Add(key);
                        bytes.Add(velocity);
                    }

                    previousKey = key;
                    previousVelocity = velocity;
                    break;
                }
                case AuthoredNoteOffEvent noteOff:
                {
                    var key = (byte)Math.Clamp(noteOff.Key, 0, 127);
                    WriteNoteOff(bytes, key, ref previousKey, ref emittedExplicitNoteOff);
                    break;
                }
            }

            currentTick = scaledTick;
        }

        if (loopTrack)
        {
            if (emitLoopMarkers && !loopBeginWritten)
            {
                WriteDelta(bytes, checked((int)Math.Max(0, loopBeginTick - currentTick)));
                bytes.Add(0x02);
                currentTick = loopBeginTick;
                loopBeginWritten = true;
            }

            WriteDelta(bytes, checked((int)Math.Max(0, loopEndTick - currentTick)));
            currentTick = loopEndTick;
            if (emitLoopMarkers)
            {
                bytes.Add(0x03);
            }

            WriteDelta(bytes, 0);
            bytes.Add(0x00);
        }
        else
        {
            WriteDelta(bytes, 96);
            bytes.Add(0x00);
        }

        return [.. bytes];
    }

    private static void WriteNoteOff(List<byte> bytes, byte key, ref byte previousKey, ref bool emittedExplicitNoteOff)
    {
        if (!emittedExplicitNoteOff)
        {
            bytes.Add(0x1A);
            bytes.Add(key);
            previousKey = key;
            emittedExplicitNoteOff = true;
        }
        else if (key == previousKey)
        {
            bytes.Add(0x18);
        }
        else
        {
            bytes.Add(0x1A);
            bytes.Add(key);
            previousKey = key;
        }
    }

    private static TemplateLoop DetermineAuthoredLoop(MidiFile midi, ushort targetPpqn, TextWriter log)
    {
        var sourcePpqn = checked((ushort)midi.Division);
        var explicitLoop = TryReadMidiLoop(midi, sourcePpqn, targetPpqn, out var explicitLoopMessage);
        if (explicitLoop is not null)
        {
            log.WriteLine(explicitLoopMessage);
            return explicitLoop;
        }

        if (!string.IsNullOrWhiteSpace(explicitLoopMessage))
        {
            log.WriteLine(explicitLoopMessage);
        }

        var fallbackEndTick = midi.Tracks.Count == 0
            ? 0L
            : midi.Tracks.Max(track => ScaleTick(track.EndTick, sourcePpqn, targetPpqn));
        fallbackEndTick = Math.Max(0L, fallbackEndTick);
        log.WriteLine($"BGM loop option: enabled via config; MIDI has no explicit loop markers, so a fallback loop from tick 0 to {fallbackEndTick} was written.");
        return new TemplateLoop(0, fallbackEndTick);
    }

    private static TemplateLoop? TryReadMidiLoop(MidiFile midi, ushort sourcePpqn, ushort targetPpqn, out string message)
    {
        message = string.Empty;
        var candidates = midi.Tracks
            .SelectMany(static track => track.Events)
            .Select(ClassifyMidiLoopMarker)
            .Where(static candidate => candidate is not null)
            .Cast<MidiLoopCandidate>()
            .OrderBy(static candidate => candidate.Tick)
            .ThenBy(static candidate => candidate.Order)
            .ToList();

        long? loopBegin = null;
        string? loopBeginSource = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Kind == MidiLoopMarkerKind.Begin)
            {
                loopBegin ??= candidate.Tick;
                loopBeginSource ??= candidate.Description;
                continue;
            }

            if (loopBegin.HasValue && candidate.Tick >= loopBegin.Value)
            {
                var beginTick = ScaleTick(loopBegin.Value, sourcePpqn, targetPpqn);
                var endTick = ScaleTick(candidate.Tick, sourcePpqn, targetPpqn);
                if (endTick < beginTick)
                {
                    continue;
                }

                message =
                    $"BGM loop option: enabled via config; reused explicit MIDI loop markers begin={beginTick}, end={endTick} ({loopBeginSource} -> {candidate.Description}).";
                return new TemplateLoop(beginTick, endTick);
            }
        }

        if (candidates.Count > 0)
        {
            message = "BGM loop option: enabled via config; MIDI loop markers were incomplete or invalid, so a fallback start-to-end loop will be used.";
        }

        return null;
    }

    private static MidiLoopCandidate? ClassifyMidiLoopMarker(MidiEvent evt)
    {
        switch (evt)
        {
            case MidiMetaTextEvent metaText:
            {
                if (!TryClassifyLoopText(metaText.Text, out var kind))
                {
                    return null;
                }

                return new MidiLoopCandidate(metaText.Tick, metaText.Order, kind, $"MIDI text marker '{metaText.Text}'");
            }
            case MidiControlChangeEvent control when control.Controller is 111 or 110:
            {
                var kind = control.Controller == 111 ? MidiLoopMarkerKind.Begin : MidiLoopMarkerKind.End;
                return new MidiLoopCandidate(
                    control.Tick,
                    control.Order,
                    kind,
                    $"MIDI CC{control.Controller} on channel {control.Channel + 1}");
            }
            default:
                return null;
        }
    }

    private static bool TryClassifyLoopText(string text, out MidiLoopMarkerKind kind)
    {
        var normalized = new string(text
            .Where(static ch => char.IsLetterOrDigit(ch))
            .Select(static ch => char.ToLowerInvariant(ch))
            .ToArray());

        if (normalized is "loopstart" or "startloop" or "loopbegin" or "beginloop")
        {
            kind = MidiLoopMarkerKind.Begin;
            return true;
        }

        if (normalized is "loopend" or "endloop" or "loopstop" or "stoploop")
        {
            kind = MidiLoopMarkerKind.End;
            return true;
        }

        kind = default;
        return false;
    }

    private static TemplateLoop? TryReadTemplateLoop(string originalBgmPath)
    {
        var data = File.ReadAllBytes(originalBgmPath);
        if (data.Length < 0x24)
        {
            return null;
        }

        var trackCount = Math.Max(1, (int)data[0x08]);
        var trackLayout = ReadTrackLayout(data, trackCount);
        for (var trackIndex = 1; trackIndex < trackLayout.Count; trackIndex++)
        {
            var loop = TryParseLoopMarkers(data, trackLayout[trackIndex]);
            if (loop is not null)
            {
                return loop;
            }
        }

        return null;
    }

    private static TemplateLoop? TryParseLoopMarkers(byte[] data, TrackLayout layout)
    {
        var offset = layout.Start;
        var end = layout.Start + layout.Length;
        long tick = 0;
        long? loopBegin = null;
        long? loopEnd = null;
        while (offset < end)
        {
            tick += ReadVarLen(data, ref offset);
            if (offset >= end)
            {
                break;
            }

            var status = data[offset++];
            switch (status)
            {
                case 0x00:
                    offset = end;
                    break;
                case 0x02:
                    loopBegin ??= tick;
                    break;
                case 0x03:
                    loopEnd ??= tick;
                    break;
                case 0x04:
                case 0x60:
                case 0x61:
                case 0x7F:
                    break;
                case 0x08:
                case 0x0A:
                case 0x0D:
                case 0x28:
                case 0x31:
                case 0x34:
                case 0x35:
                case 0x3E:
                case 0x58:
                case 0x5D:
                    offset += 1;
                    break;
                case 0x0C:
                case 0x19:
                case 0x47:
                case 0x5C:
                    offset += 2;
                    break;
                case 0x40:
                case 0x48:
                case 0x50:
                    offset += 3;
                    break;
                case 0x11:
                    offset += 2;
                    break;
                case 0x12:
                case 0x13:
                case 0x1A:
                case 0x20:
                case 0x22:
                case 0x24:
                case 0x26:
                case 0x3C:
                    offset += 1;
                    break;
                case 0x10:
                case 0x18:
                    break;
                default:
                    return null;
            }
        }

        if (loopBegin.HasValue && loopEnd.HasValue && loopEnd.Value >= loopBegin.Value)
        {
            return new TemplateLoop(loopBegin.Value, loopEnd.Value);
        }

        return null;
    }

    private static int ReadVarLen(byte[] data, ref int offset)
    {
        var value = 0;
        while (offset < data.Length)
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

    private static byte[] BuildSilentTrack()
    {
        return
        [
            0x00, 0x22, 0x00,
            0x00, 0x24, 0x00,
            0x00, 0x00,
        ];
    }

    private static long ScaleTick(long tick, ushort sourcePpqn, ushort targetPpqn)
    {
        if (tick <= 0)
        {
            return 0;
        }

        if (sourcePpqn == 0 || sourcePpqn == targetPpqn)
        {
            return tick;
        }

        return Math.Max(0L, (long)Math.Round(tick * (targetPpqn / (double)sourcePpqn), MidpointRounding.AwayFromZero));
    }

    private static List<TrackLayout> ReadTrackLayout(byte[] data, int trackCount)
    {
        var result = new List<TrackLayout>(trackCount);
        var cursor = 0x20;
        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            if (cursor + 4 > data.Length)
            {
                throw new InvalidDataException("Track table exceeds original BGM length.");
            }

            var trackLength = checked((int)BinaryHelpers.ReadUInt32LE(data, cursor));
            var trackStart = cursor + 4;
            if (trackStart + trackLength > data.Length)
            {
                throw new InvalidDataException("A BGM track exceeds the original file length.");
            }

            result.Add(new TrackLayout(trackStart, trackLength));
            cursor = trackStart + trackLength;
        }

        return result;
    }

    private static void WriteTrackIntoSlot(byte[] output, TrackLayout layout, byte[] trackBytes, string description)
    {
        if (trackBytes.Length > layout.Length)
        {
            throw new InvalidDataException(
                $"The generated {description} does not fit into the original BGM slot (needed {trackBytes.Length} bytes, slot has {layout.Length}).");
        }

        Array.Clear(output, layout.Start, layout.Length);
        Buffer.BlockCopy(trackBytes, 0, output, layout.Start, trackBytes.Length);
    }

    private static int CalculateExpandedBgmLength(IReadOnlyList<int> slotLengths)
    {
        var total = 0x20;
        foreach (var slotLength in slotLengths)
        {
            total += 4 + slotLength;
        }

        return total;
    }

    private static int CalculateMaxAllowedBgmLength(int originalLength, bool midiLoop)
    {
        var growthBytes = midiLoop ? MaxExpandedBgmGrowthBytes : MaxExpandedOneShotBgmGrowthBytes;
        var growthFactor = midiLoop ? MaxExpandedBgmGrowthFactor : MaxExpandedOneShotBgmGrowthFactor;
        var factorCap = (int)Math.Ceiling(originalLength * growthFactor);
        return Math.Max(originalLength + growthBytes, factorCap);
    }

    private static void WriteDelta(List<byte> buffer, int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Span<byte> stack = stackalloc byte[5];
        var index = 0;
        stack[index++] = (byte)(value & 0x7F);
        value >>= 7;
        while (value > 0)
        {
            stack[index++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        for (var i = index - 1; i >= 0; i--)
        {
            buffer.Add(stack[i]);
        }
    }

    private static int MapMidiVolumeController(int value)
        => MapMidiAttenuationController(value, power: 1.65, linearBlend: 0.22);

    private static int MapMidiExpressionController(int value)
        => MapMidiAttenuationController(value, power: 1.35, linearBlend: 0.40);

    private static int MapMidiAttenuationController(int value, double power, double linearBlend)
    {
        var clamped = Math.Clamp(value, 0, 127);
        if (clamped is 0 or 127)
        {
            return clamped;
        }

        // Blend a softer loudness curve with a linear component so quieter backing layers stay present
        // enough to preserve body and bass without letting the whole mix collapse toward full scale.
        var normalized = clamped / 127.0;
        var shaped = Math.Pow(normalized, power);
        var blended = (normalized * linearBlend) + (shaped * (1.0 - linearBlend));
        return Math.Clamp((int)Math.Round(blended * 127.0, MidpointRounding.AwayFromZero), 0, 127);
    }

    private static short[] ApplyVolume(short[] pcm, double volume)
    {
        if (Math.Abs(volume - 1.0) < 0.0000001)
        {
            return (short[])pcm.Clone();
        }

        var output = new short[pcm.Length];
        for (var index = 0; index < pcm.Length; index++)
        {
            var sample = pcm[index] * volume;
            output[index] = (short)Math.Clamp(Math.Round(sample, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
        }

        return output;
    }

    private static AuthoredSample GetOrAddAuthoredSample(
        Dictionary<string, AuthoredSample> authoredSamples,
        string identityKey,
        string sourceSampleName,
        short[] pcm,
        SamplePitchComponents samplePitch,
        LoopDescriptor requestedLoopDescriptor,
        double volume,
        bool enableShortLoopPitchCompensation)
    {
        if (authoredSamples.TryGetValue(identityKey, out var authoredSample))
        {
            return authoredSample;
        }

        var adjustedPcm = ApplyVolume(pcm, volume);
        var prepared = PrepareLoopAlignedSample(adjustedPcm, requestedLoopDescriptor, enableShortLoopPitchCompensation);
        var encoded = PsxAdpcmEncoder.Encode(prepared.Pcm, prepared.LoopDescriptor);
        var effectiveSampleRate = ResolveStoredSampleRate(samplePitch.StoredSampleRate, SpuSampleRate);
        authoredSample = new AuthoredSample(
            identityKey,
            sourceSampleName,
            prepared.Pcm,
            encoded,
            requestedLoopDescriptor.NormalizeToSamples(prepared.Pcm.Length),
            prepared.LoopDescriptor.NormalizeToSamples(prepared.Pcm.Length),
            samplePitch with
            {
                StoredSampleRate = effectiveSampleRate,
                SampleRatePitchOffsetSemitones = GetSampleRatePitchOffsetSemitones(effectiveSampleRate),
                LoopAlignmentPitchOffsetSemitones = prepared.PitchOffsetSemitones,
            });
        authoredSamples.Add(identityKey, authoredSample);
        return authoredSample;
    }

    private static PreparedLoopSample PrepareLoopAlignedSample(short[] pcm, LoopDescriptor loopDescriptor, bool enableShortLoopPitchCompensation)
    {
        if (!loopDescriptor.Looping || pcm.Length == 0)
        {
            return new PreparedLoopSample(pcm, LoopDescriptor.None, 0.0);
        }

        var safeLoopStart = loopDescriptor.ResolveStartSamples(pcm.Length);
        var safeLoopLength = loopDescriptor.ResolveLengthSamples(pcm.Length);
        var safeLoopEnd = safeLoopLength > 0
            ? Math.Clamp(safeLoopStart + safeLoopLength, safeLoopStart + 1, pcm.Length)
            : pcm.Length;
        if (safeLoopEnd < pcm.Length)
        {
            // PSX ADPCM has a loop-start flag but no separate SF2-style loop-end marker;
            // trim the stored sample to the requested loop end so the in-game loop wraps correctly.
            var loopBoundedPcm = new short[safeLoopEnd];
            Array.Copy(pcm, loopBoundedPcm, safeLoopEnd);
            pcm = loopBoundedPcm;
            loopDescriptor = LoopDescriptor.FromSamples(true, safeLoopStart, pcm.Length, loopDescriptor.Type);
        }

        var remainder = safeLoopStart % 28;
        if (remainder == 0)
        {
            return new PreparedLoopSample(pcm, LoopDescriptor.FromSamples(true, safeLoopStart, pcm.Length, loopDescriptor.Type), 0.0);
        }

        var originalLoopLength = pcm.Length - safeLoopStart;
        if (enableShortLoopPitchCompensation &&
            originalLoopLength > 0 &&
            originalLoopLength <= ShortLoopAlignmentThresholdSamples)
        {
            var alignedLoopStart = AlignToNearestAdpcmBoundary(safeLoopStart, allowZero: true);
            var alignedLoopLength = AlignToNearestAdpcmBoundary(originalLoopLength, allowZero: false);

            if (alignedLoopStart == safeLoopStart && alignedLoopLength == originalLoopLength)
            {
                return new PreparedLoopSample(pcm, LoopDescriptor.FromSamples(true, safeLoopStart, pcm.Length, loopDescriptor.Type), 0.0);
            }

            var intro = alignedLoopStart > 0
                ? AudioDsp.ResampleToLength(pcm[..safeLoopStart], alignedLoopStart)
                : [];
            var loop = AudioDsp.ResampleToLength(pcm[safeLoopStart..], alignedLoopLength);
            var rebuilt = new short[intro.Length + loop.Length];
            if (intro.Length > 0)
            {
                Buffer.BlockCopy(intro, 0, rebuilt, 0, intro.Length * sizeof(short));
            }

            Buffer.BlockCopy(loop, 0, rebuilt, intro.Length * sizeof(short), loop.Length * sizeof(short));
            var pitchOffsetSemitones = 12.0 * Math.Log(originalLoopLength / (double)alignedLoopLength, 2.0);
            return new PreparedLoopSample(rebuilt, LoopDescriptor.FromSamples(true, alignedLoopStart, rebuilt.Length, loopDescriptor.Type), pitchOffsetSemitones);
        }

        // For short looping instrument samples, inserting duplicated PCM at the loop point
        // can create an audible stutter each time the sample wraps. Prefer a conservative
        // block-aligned loop that starts slightly earlier over duplicating audio content.
        return new PreparedLoopSample(pcm, LoopDescriptor.FromSamples(true, safeLoopStart - remainder, pcm.Length, loopDescriptor.Type), 0.0);
    }

    private static int AlignToNearestAdpcmBoundary(int sampleCount, bool allowZero)
    {
        if (sampleCount <= 0)
        {
            return allowZero ? 0 : 28;
        }

        var lower = (sampleCount / 28) * 28;
        var upper = lower == sampleCount ? lower : lower + 28;
        if (!allowZero)
        {
            lower = Math.Max(28, lower);
            upper = Math.Max(28, upper);
        }

        if (allowZero && lower < 0)
        {
            lower = 0;
        }

        return Math.Abs(sampleCount - lower) <= Math.Abs(upper - sampleCount) ? lower : upper;
    }

    private static float GetStereoLeftPan(float basePan)
    {
        return Math.Clamp(basePan - 1f, -1f, 1f);
    }

    private static float GetStereoRightPan(float basePan)
    {
        return Math.Clamp(basePan + 1f, -1f, 1f);
    }

    private static int SamplesToLoopStartBytes(int loopStartSample)
        => PsxAdpcmLoopMath.SamplesToBytes(loopStartSample);

    private static int LoopStartBytesToSamples(int loopStartBytes)
        => PsxAdpcmLoopMath.BytesToSamples(loopStartBytes);

    private static double GetEffectiveSampleRootNote(AuthoredSample sample)
        => sample.PitchComponents.EffectiveRootNoteSemitones;

    private static double GetEffectiveRegionRootNote(AuthoredRegion region)
        => region.PitchComponents.ResolveEffectiveRootNoteSemitones(region.Sample.PitchComponents);

    private static (int RootKey, int FineTuneCents) ResolveEffectiveRegionPitch(AuthoredRegion region)
        => CanonicalizeRootNote(GetEffectiveRegionRootNote(region));

    private static PsxAdpcmLoopInfo ResolveAuthoredSampleLoopInfo(AuthoredSample sample)
        => WdSampleTool.ResolvePreferredLoopInfo(sample.EncodedBytes, sample.EffectiveLoopDescriptor);

    private static AuthoredRegion RetuneRegion(AuthoredRegion region, int pitchVariantCents)
    {
        if (pitchVariantCents == 0)
        {
            return region;
        }

        return region with
        {
            PitchComponents = region.PitchComponents with
            {
                FineTuneCents = region.PitchComponents.FineTuneCents - pitchVariantCents,
            },
        };
    }

    private static (int RootKey, int FineTuneCents) ApplyPitchOffset(int rootKey, int fineTuneCents, double pitchOffsetSemitones, bool suppressSmallOffsets)
    {
        var rootNote = ComposeRootNote(rootKey, fineTuneCents);
        if (suppressSmallOffsets && Math.Abs(pitchOffsetSemitones * 100.0) <= PitchRetuneNoOpThresholdCents)
        {
            return CanonicalizeRootNote(rootNote);
        }

        return CanonicalizeRootNote(rootNote + pitchOffsetSemitones);
    }

    private static int ResolveStoredSampleRate(int sourceSampleRate, int fallbackSampleRate)
    {
        if (sourceSampleRate > 0)
        {
            return sourceSampleRate;
        }

        return fallbackSampleRate > 0 ? fallbackSampleRate : SpuSampleRate;
    }

    private static double GetSampleRatePitchOffsetSemitones(int storedSampleRate)
    {
        var effectiveSampleRate = ResolveStoredSampleRate(storedSampleRate, SpuSampleRate);
        if (effectiveSampleRate == SpuSampleRate)
        {
            return 0.0;
        }

        return 12.0 * Math.Log(SpuSampleRate / (double)effectiveSampleRate, 2.0);
    }

    private static double ComposeRootNote(int rootKey, int fineTuneCents)
        => rootKey + (fineTuneCents / 100.0);

    private static (int RootKey, int FineTuneCents) CanonicalizeRootNote(double rootNote)
    {
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

        return (rootKey, fineTuneCents);
    }

    private static void SplitRootNote(double rootNote, out int rootKey, out int fineTuneCents)
    {
        var canonicalPitch = CanonicalizeRootNote(rootNote);
        rootKey = canonicalPitch.RootKey;
        fineTuneCents = canonicalPitch.FineTuneCents;
    }

    private static AdsrEnvelope EncodeAdsr(SoundFontRegion region)
    {
        var attackSeconds = region.AttackSeconds <= FastAttackClampSeconds
            ? 0.0
            : region.AttackSeconds;
        var attack = SelectAttackProfile(attackSeconds);
        var decay = SelectDecayProfile(region.SustainLevel, region.HoldSeconds + region.DecaySeconds);
        var release = SelectReleaseProfile(region.ReleaseSeconds);

        return new AdsrEnvelope(
            ComposePsxAdsr1(attack.Exponential, attack.Rate, decay.Rate, decay.SustainNibble),
            ComposePsxAdsr2(sustainExponential: false, sustainDecreasing: true, sustainRate: 0x7F, release.Exponential, release.Rate));
    }

    private static AttackAdsrProfile SelectAttackProfile(double targetSeconds)
    {
        AttackAdsrProfile? best = null;
        var bestScore = double.MaxValue;
        foreach (var profile in AttackProfiles)
        {
            var score = ScoreDuration(profile.DurationSeconds, targetSeconds);
            if (score < bestScore)
            {
                best = profile;
                bestScore = score;
            }
        }

        return best ?? AttackProfiles[0];
    }

    private static DecayAdsrProfile SelectDecayProfile(float targetSustainLevel, double targetSeconds)
    {
        DecayAdsrProfile? best = null;
        var bestScore = double.MaxValue;
        var clampedSustainLevel = Math.Clamp(targetSustainLevel, 0f, 1f);
        foreach (var profile in DecayProfiles)
        {
            var score = ScoreDuration(profile.DurationSeconds, targetSeconds) +
                        ScoreSustainLevel(profile.SustainLevel, clampedSustainLevel);
            if (score < bestScore)
            {
                best = profile;
                bestScore = score;
            }
        }

        return best ?? DecayProfiles[0];
    }

    private static ReleaseAdsrProfile SelectReleaseProfile(double targetSeconds)
    {
        ReleaseAdsrProfile? best = null;
        var bestScore = double.MaxValue;
        foreach (var profile in ReleaseProfiles)
        {
            var score = ScoreDuration(profile.DurationSeconds, targetSeconds);
            if (score < bestScore)
            {
                best = profile;
                bestScore = score;
            }
        }

        return best ?? ReleaseProfiles[0];
    }

    private static double ScoreDuration(double actualSeconds, double targetSeconds)
    {
        var actual = Math.Max(SanitizeEnvelopeSeconds(actualSeconds), 1.0 / Ps2AdsrSampleRate);
        var target = Math.Max(SanitizeEnvelopeSeconds(targetSeconds), 1.0 / Ps2AdsrSampleRate);
        return Math.Abs(Math.Log(actual / target));
    }

    private static double ScoreSustainLevel(double actualLevel, double targetLevel)
    {
        return Math.Abs(ConvertAmplitudeToDb(actualLevel) - ConvertAmplitudeToDb(targetLevel)) / 12.0;
    }

    private static IReadOnlyList<AttackAdsrProfile> BuildAttackProfiles()
    {
        var profiles = new List<AttackAdsrProfile>();
        for (var rate = 0; rate <= 0x7F; rate++)
        {
            profiles.Add(new AttackAdsrProfile(false, rate, DecodePsxAttackSeconds(false, rate)));
            profiles.Add(new AttackAdsrProfile(true, rate, DecodePsxAttackSeconds(true, rate)));
        }

        return profiles;
    }

    private static IReadOnlyList<DecayAdsrProfile> BuildDecayProfiles()
    {
        var profiles = new List<DecayAdsrProfile>();
        for (var sustainNibble = 0; sustainNibble <= 0x0F; sustainNibble++)
        {
            for (var rate = 0; rate <= 0x0F; rate++)
            {
                var decoded = DecodePsxDecayProfile(rate, sustainNibble);
                profiles.Add(new DecayAdsrProfile(sustainNibble, rate, decoded.SustainLevel, decoded.DurationSeconds));
            }
        }

        return profiles;
    }

    private static IReadOnlyList<ReleaseAdsrProfile> BuildReleaseProfiles()
    {
        var profiles = new List<ReleaseAdsrProfile>();
        for (var rate = 0; rate <= 0x1F; rate++)
        {
            profiles.Add(new ReleaseAdsrProfile(false, rate, DecodePsxReleaseSeconds(false, rate)));
            profiles.Add(new ReleaseAdsrProfile(true, rate, DecodePsxReleaseSeconds(true, rate)));
        }

        return profiles;
    }

    private static uint[] BuildPsxRateTable()
    {
        var table = new uint[160];
        uint rate = 3;
        uint step = 1;
        uint stepCountdown = 0;
        for (var index = 32; index < table.Length; index++)
        {
            if (rate < 0x3FFFFFFF)
            {
                rate += step;
                stepCountdown++;
                if (stepCountdown == 5)
                {
                    stepCountdown = 1;
                    step *= 2;
                }
            }

            if (rate > 0x3FFFFFFF)
            {
                rate = 0x3FFFFFFF;
            }

            table[index] = rate;
        }

        return table;
    }

    private static double DecodePsxAttackSeconds(bool exponential, int attackRate)
    {
        var rate = Math.Clamp(attackRate, 0, 0x7F);
        if ((rate ^ 0x7F) < 0x10)
        {
            rate = 0;
        }

        var firstPhaseRate = GetPsxRate(RoundToZero((rate ^ 0x7F) - 0x10) + 32);
        if (firstPhaseRate == 0)
        {
            return 0.0;
        }

        double samples;
        if (!exponential)
        {
            samples = Math.Ceiling(PsxEnvelopeMaxLevel / (double)firstPhaseRate);
        }
        else
        {
            samples = 0x60000000 / (double)firstPhaseRate;
            var remainder = 0x60000000 % firstPhaseRate;
            var secondPhaseRate = GetPsxRate(RoundToZero((rate ^ 0x7F) - 0x18) + 32);
            if (secondPhaseRate == 0)
            {
                return samples / Ps2AdsrSampleRate;
            }

            samples += Math.Ceiling(Math.Max(0.0, 0x1FFFFFFF - remainder) / secondPhaseRate);
        }

        return samples / Ps2AdsrSampleRate;
    }

    private static DecodedPsxDecayProfile DecodePsxDecayProfile(int decayRate, int sustainNibble)
    {
        var rate = Math.Clamp(decayRate, 0, 0x0F);
        var sustain = Math.Clamp(sustainNibble, 0, 0x0F);
        if ((4 * (rate ^ 0x1F)) < 0x18)
        {
            rate = 0;
        }

        long envelopeLevel = PsxEnvelopeMaxLevel;
        var sampleCount = 0L;
        var sustainThreshold = sustain == 0
            ? 0x07FFFFFFL
            : ((long)sustain << 27) | 0x07FFFFFFL;
        uint decodedSustainLevel = 0;
        var sustainFound = false;

        while (envelopeLevel > 0)
        {
            var segment = (int)((envelopeLevel >> 28) & 0x7);
            var decrement = GetPsxRate(RoundToZero((4 * (rate ^ 0x1F)) - 0x18 + GetPsxSegmentOffset(segment)) + 32);
            if (decrement == 0)
            {
                break;
            }

            var stepsToSegmentBoundary = segment == 0
                ? CeilDivPositive(envelopeLevel, decrement)
                : CeilDivPositive(envelopeLevel - ((((long)segment) << 28) - 1), decrement);
            var steps = Math.Max(stepsToSegmentBoundary, 1L);
            if (!sustainFound && envelopeLevel > sustainThreshold)
            {
                steps = Math.Min(steps, Math.Max(CeilDivPositive(envelopeLevel - sustainThreshold, decrement), 1L));
            }

            envelopeLevel = Math.Max(0, envelopeLevel - (decrement * steps));
            sampleCount += steps;

            if (!sustainFound && (((envelopeLevel >> 27) & 0xF) <= sustain))
            {
                decodedSustainLevel = (uint)envelopeLevel;
                sustainFound = true;
            }
        }

        if (sustain == 0)
        {
            decodedSustainLevel = 0x07FFFFFF;
        }
        else if (!sustainFound)
        {
            decodedSustainLevel = 0;
        }

        return new DecodedPsxDecayProfile(
            sampleCount / (double)Ps2AdsrSampleRate,
            decodedSustainLevel / (double)PsxEnvelopeMaxLevel);
    }

    private static double DecodePsxReleaseSeconds(bool exponential, int releaseRate)
    {
        var rate = Math.Clamp(releaseRate, 0, 0x1F);
        double samples;
        if (!exponential)
        {
            var decrement = GetPsxRate(RoundToZero((4 * (rate ^ 0x1F)) - 0x0C) + 32);
            samples = decrement == 0
                ? 0.0
                : Math.Ceiling(PsxEnvelopeMaxLevel / (double)decrement);
        }
        else
        {
            if (((rate ^ 0x1F) * 4) < 0x18)
            {
                rate = 0;
            }

            long envelopeLevel = PsxEnvelopeMaxLevel;
            var sampleCount = 0L;
            while (envelopeLevel > 0)
            {
                var segment = (int)((envelopeLevel >> 28) & 0x7);
                var decrement = GetPsxRate(RoundToZero((4 * (rate ^ 0x1F)) - 0x18 + GetPsxSegmentOffset(segment)) + 32);
                if (decrement == 0)
                {
                    break;
                }

                var steps = segment == 0
                    ? CeilDivPositive(envelopeLevel, decrement)
                    : CeilDivPositive(envelopeLevel - ((((long)segment) << 28) - 1), decrement);
                steps = Math.Max(steps, 1L);
                envelopeLevel = Math.Max(0, envelopeLevel - (decrement * steps));
                sampleCount += steps;
            }

            samples = sampleCount;
        }

        return LinearAmpDecayTimeToLinDbDecayTime(samples / Ps2AdsrSampleRate);
    }

    private static int GetPsxSegmentOffset(int segment)
    {
        return segment switch
        {
            0 => 0,
            1 => 4,
            2 => 6,
            3 => 8,
            4 => 9,
            5 => 10,
            6 => 11,
            _ => 12,
        };
    }

    private static long CeilDivPositive(long numerator, uint denominator)
    {
        if (numerator <= 0)
        {
            return 0;
        }

        return (numerator + denominator - 1) / denominator;
    }

    private static uint GetPsxRate(int index)
    {
        return index < 0 || index >= PsxRateTable.Length
            ? 0
            : PsxRateTable[index];
    }

    private static int RoundToZero(int value)
    {
        return value < 0 ? 0 : value;
    }

    private static ushort ComposePsxAdsr1(bool attackExponential, int attackRate, int decayRate, int sustainNibble)
    {
        return (ushort)(
            ((attackExponential ? 1 : 0) << 15) |
            ((Math.Clamp(attackRate, 0, 0x7F) & 0x7F) << 8) |
            ((Math.Clamp(decayRate, 0, 0x0F) & 0x0F) << 4) |
            (Math.Clamp(sustainNibble, 0, 0x0F) & 0x0F));
    }

    private static ushort ComposePsxAdsr2(bool sustainExponential, bool sustainDecreasing, int sustainRate, bool releaseExponential, int releaseRate)
    {
        return (ushort)(
            ((sustainExponential ? 1 : 0) << 15) |
            ((sustainDecreasing ? 1 : 0) << 14) |
            ((Math.Clamp(sustainRate, 0, 0x7F) & 0x7F) << 6) |
            ((releaseExponential ? 1 : 0) << 5) |
            (Math.Clamp(releaseRate, 0, 0x1F) & 0x1F));
    }

    private static double LinearAmpDecayTimeToLinDbDecayTime(double secondsToFullAtten)
    {
        if (secondsToFullAtten <= 0.0 || !double.IsFinite(secondsToFullAtten))
        {
            return 0.0;
        }

        const double leastSquaresDb = 70.0;
        const double initialSlopeDb = 140.0;
        const double ln10 = 2.302585092994046;
        const double kneeSeconds = 0.12;
        const double kneePower = 2.0;
        var shortFactor = initialSlopeDb / (20.0 / ln10);
        var longFactor = leastSquaresDb * ln10 / 45.0;
        var normalizedTime = secondsToFullAtten / kneeSeconds;
        var blend = 1.0 / (1.0 + Math.Pow(normalizedTime, kneePower));
        return secondsToFullAtten * ((blend * shortFactor) + ((1.0 - blend) * longFactor));
    }

    private static double ConvertAmplitudeToDb(double amplitude)
    {
        if (!double.IsFinite(amplitude) || amplitude <= 0.0)
        {
            return 100.0;
        }

        return Math.Min(-20.0 * Math.Log10(amplitude), 100.0);
    }

    private static double SanitizeEnvelopeSeconds(double seconds)
    {
        return !double.IsFinite(seconds) || seconds <= 0.0
            ? 0.0
            : seconds;
    }

    private static int Align16(int value)
    {
        return (value + 0x0F) & ~0x0F;
    }

    private static void EncodeRootNote(double rootNote, out byte rawFineTune, out byte rawUnityKey)
    {
        var canonicalPitch = CanonicalizeRootNote(rootNote);
        var unityKey = canonicalPitch.RootKey;
        var fineTune = canonicalPitch.FineTuneCents;
        rawFineTune = WdSampleTool.EncodeWdFineTune(fineTune);
        rawUnityKey = unchecked((byte)(0x3A - unityKey));
    }

    private static byte EncodeWdPan(float pan)
    {
        var clamped = Math.Clamp(pan, -1f, 1f);
        if (clamped < 0f)
        {
            return (byte)Math.Clamp((int)Math.Round(0xC0 + (clamped * 0x40), MidpointRounding.AwayFromZero), 0x80, 0xC0);
        }

        return (byte)Math.Clamp((int)Math.Round(0xC0 + (clamped * 0x3F), MidpointRounding.AwayFromZero), 0xC0, 0xFF);
    }
}

internal sealed record ConversionPlan(
    List<AuthoredInstrument> Instruments,
    List<AuthoredSample> Samples,
    IReadOnlyDictionary<PresetRef, ProgramMapping> ProgramMap,
    List<AuthoredTrackPlan> TrackPlans,
    IReadOnlyList<string> Warnings);

internal sealed record AuthoredInstrument(
    int Index,
    string PresetName,
    int TemplateInstrumentIndex,
    List<AuthoredRegion> Regions);

internal sealed record AuthoredRegion(
    AuthoredSample Sample,
    int KeyLow,
    int KeyHigh,
    int VelocityLow,
    int VelocityHigh,
    RegionPitchComponents PitchComponents,
    float Volume,
    float Pan,
    AdsrEnvelope Envelope,
    bool Stereo,
    bool PreferAuthoredEnvelope,
    AuthoredRegionSourceInfo SourceInfo,
    string EnvelopePolicyReason,
    string LoopPolicyReason,
    bool UsedTemplateLoopPolicy,
    string LoopTemplateMatchKind)
{
    public double RegionPitchOffsetSemitones => PitchComponents.ResolveOffsetFromSourcePitch(Sample.PitchComponents);
}

internal sealed record AuthoredRegionSourceInfo(
    string SourceSampleName,
    int SourceRootKey,
    int SourceFineTuneCents,
    int SourceSampleRate,
    int? InitialFilterFcCents,
    double InitialFilterCutoffHz,
    double AttackSeconds,
    double HoldSeconds,
    double DecaySeconds,
    float SustainLevel,
    double ReleaseSeconds,
    LoopDescriptor SourceLoopDescriptor,
    bool WasDownmixedPseudoStereo,
    bool SourceStereoPair,
    LoopDescriptor EffectiveLoopDescriptor,
    SoundFontRegionDebug SoundFontDebug)
{
    public bool SourceLooping => SourceLoopDescriptor.Looping;

    public int SourceLoopStartSample => SourceLoopDescriptor.ResolveStartSamples(int.MaxValue);

    public bool EffectiveLooping => EffectiveLoopDescriptor.Looping;

    public int EffectiveLoopStartSample => EffectiveLoopDescriptor.ResolveStartSamples(int.MaxValue);
}

internal sealed class ActiveChannelNote
{
    public ActiveChannelNote(int sourceKey, int emittedKey, int velocity)
    {
        SourceKey = sourceKey;
        EmittedKey = emittedKey;
        Velocity = velocity;
    }

    public int SourceKey { get; }
    public int EmittedKey { get; set; }
    public int Velocity { get; }
    public bool DeferredRelease { get; set; }
}

internal sealed record AuthoredSample(
    string IdentityKey,
    string SourceSampleName,
    short[] Pcm,
    byte[] EncodedBytes,
    LoopDescriptor RequestedLoopDescriptor,
    LoopDescriptor EffectiveLoopDescriptor,
    SamplePitchComponents PitchComponents)
{
    public bool RequestedLooping => RequestedLoopDescriptor.Looping;

    public int RequestedLoopStartSample => RequestedLoopDescriptor.ResolveStartSamples(Pcm.Length);

    public bool Looping => EffectiveLoopDescriptor.Looping;

    public int LoopStartSample => EffectiveLoopDescriptor.ResolveStartSamples(Pcm.Length);

    public int SampleRate => PitchComponents.StoredSampleRate;

    public double SampleBaseRootNoteSemitones => PitchComponents.SourceRootNoteSemitones;

    public double PitchOffsetSemitones => PitchComponents.PitchOffsetSemitones;
}

internal sealed record AdsrEnvelope(ushort Adsr1, ushort Adsr2);

internal sealed record AttackAdsrProfile(bool Exponential, int Rate, double DurationSeconds);

internal sealed record DecayAdsrProfile(int SustainNibble, int Rate, double SustainLevel, double DurationSeconds);

internal sealed record ReleaseAdsrProfile(bool Exponential, int Rate, double DurationSeconds);

internal sealed record DecodedPsxDecayProfile(double DurationSeconds, double SustainLevel);

internal sealed record PresetRef(int Bank, int Program);

internal sealed record ProgramMapping(
    byte InstrumentIndex,
    string PresetName,
    int RegionCount,
    IReadOnlyDictionary<int, byte>? PitchVariantPrograms = null);

internal sealed record AuthoredTrackPlan(int Channel, string Name, List<AuthoredTrackEvent> Events, int EventCount);

internal abstract record AuthoredTrackEvent(long Tick);

internal sealed record AuthoredProgramEvent(long Tick, byte Program) : AuthoredTrackEvent(Tick);

internal sealed record AuthoredVolumeEvent(long Tick, int Value) : AuthoredTrackEvent(Tick);

internal sealed record AuthoredExpressionEvent(long Tick, int Value) : AuthoredTrackEvent(Tick);

internal sealed record AuthoredPanEvent(long Tick, int Value) : AuthoredTrackEvent(Tick);

internal sealed record AuthoredNoteOnEvent(long Tick, int Key, int Velocity) : AuthoredTrackEvent(Tick);

internal sealed record AuthoredNoteOffEvent(long Tick, int Key) : AuthoredTrackEvent(Tick);

internal sealed record MidiSf2ReplacementManifest(
    string InputMidi,
    string InputSoundFont,
    string OutputBgm,
    string OutputWd,
    List<MidiSf2TrackManifest> Tracks,
    List<MidiSf2ProgramManifest> Programs,
    List<MidiSf2InstrumentManifest> Instruments,
    IReadOnlyList<string> Warnings);

internal sealed record MidiSf2TrackManifest(int Channel, string Name, int EventCount);

internal sealed record MidiSf2ProgramManifest(int Bank, int Program, byte InstrumentIndex, string PresetName, int RegionCount);

internal sealed record MidiSf2InstrumentManifest(
    int InstrumentIndex,
    string PresetName,
    int TemplateInstrumentIndex,
    List<MidiSf2RegionManifest> Regions);

internal sealed record MidiSf2RegionManifest(
    int RegionIndex,
    string SampleIdentityKey,
    string SourceSampleName,
    int KeyLow,
    int KeyHigh,
    int VelocityLow,
    int VelocityHigh,
    int RootKey,
    int FineTuneCents,
    float Volume,
    float Pan,
    bool Stereo,
    bool Looping,
    int LoopStartBytes,
    MidiSf2LoopManifest Loop,
    string LoopPolicyReason,
    bool UsedTemplateLoopPolicy,
    string LoopTemplateMatchKind,
    MidiSf2AdpcmFlagManifest AdpcmFlags,
    string EnvelopePolicyReason,
    bool PreferAuthoredEnvelope,
    bool UsedTemplateEnvelope,
    string EnvelopeTemplateMatchKind,
    MidiSf2EnvelopeManifest AuthoredEnvelope,
    MidiSf2EnvelopeManifest FinalEnvelope,
    MidiSf2PitchManifest Pitch,
    MidiSf2AuthoredRegionSourceManifest Source,
    MidiSf2TemplateRegionManifest? TemplateRegion,
    MidiSf2TemplateRegionManifest? EnvelopeTemplateRegion,
    MidiSf2TemplateRegionManifest? LoopTemplateRegion);

internal sealed record MidiSf2AdpcmFlagManifest(
    int BlockCount,
    int LoopFlagBlockCount,
    int LoopStartFlagBlockCount,
    int FirstLoopStartFlagBlockIndex,
    int EndFlagBlockCount,
    byte FirstBlockFlag,
    byte LastBlockFlag);

internal sealed record MidiSf2EnvelopeManifest(ushort Adsr1, ushort Adsr2);

internal sealed record MidiSf2PitchManifest(
    double RootNoteSemitones,
    double SampleBaseRootNoteSemitones,
    int SampleOriginalPitch,
    int SamplePitchCorrectionCents,
    int? RegionOverridingRootKey,
    int RegionCoarseTuneSemitones,
    int RegionFineTuneCents,
    double RegionPitchOffsetSemitones,
    int StoredSampleRate,
    double SampleRatePitchOffsetSemitones,
    double LoopAlignmentPitchOffsetSemitones,
    double PitchOffsetSemitones,
    int EncodedUnityKey,
    int EncodedFineTuneCents,
    double EncodedRootNoteSemitones,
    int QuantizationErrorCents);

internal sealed record MidiSf2AuthoredRegionSourceManifest(
    string SourceSampleName,
    int SourceRootKey,
    int SourceFineTuneCents,
    int SourceSampleRate,
    int? InitialFilterFcCents,
    double InitialFilterCutoffHz,
    double AttackSeconds,
    double HoldSeconds,
    double DecaySeconds,
    float SustainLevel,
    double ReleaseSeconds,
    bool SourceLooping,
    int SourceLoopStartSample,
    bool WasDownmixedPseudoStereo,
    bool SourceStereoPair,
    bool EffectiveLooping,
    int EffectiveLoopStartSample,
    MidiSf2LoopManifest SourceLoop,
    MidiSf2LoopManifest EffectiveLoop,
    SoundFontRegionDebug SoundFontDebug);

internal sealed record MidiSf2TemplateRegionManifest(
    int InstrumentIndex,
    int RegionIndex,
    int FileOffset,
    int KeyLow,
    int KeyHigh,
    int VelocityLow,
    int VelocityHigh,
    bool Stereo,
    int UnityKey,
    int FineTuneCents,
    float Volume,
    float Pan,
    bool Looping,
    int LoopStartBytes,
    MidiSf2EnvelopeManifest Envelope);

internal sealed record MidiSf2LoopManifest(
    bool Looping,
    int Start,
    int Length,
    string StartMeasure,
    string LengthMeasure,
    string Type);

internal sealed record GeneratedTrack(int Channel, string Name, byte[] Bytes);

internal sealed record PreparedLoopSample(short[] Pcm, LoopDescriptor LoopDescriptor, double PitchOffsetSemitones)
{
    public bool Looping => LoopDescriptor.Looping;

    public int LoopStartSample => LoopDescriptor.ResolveStartSamples(Pcm.Length);
}

internal sealed record PitchTarget(byte Program, int Key);

internal sealed record ResolvedLoopPolicy(
    LoopDescriptor LoopDescriptor,
    string LoopPolicyReason,
    bool UsedTemplateLoopPolicy,
    string LoopTemplateMatchKind)
{
    public bool Looping => LoopDescriptor.Looping;

    public int LoopStartSample => LoopDescriptor.ResolveStartSamples(int.MaxValue);
}

internal enum MidiLoopMarkerKind
{
    Begin,
    End,
}

internal sealed record MidiLoopCandidate(long Tick, int Order, MidiLoopMarkerKind Kind, string Description);

internal sealed record TemplateLoop(long BeginTick, long EndTick);

internal sealed record TrackLayout(int Start, int Length);

internal sealed record AvailableTrackSlot(int Index, TrackLayout Layout);

internal sealed class MissingSoundFontPresetException : Exception
{
    public MissingSoundFontPresetException(string missingPresets, string availablePresets)
        : base($"The MIDI references SoundFont preset(s) that do not exist in the selected SF2. Missing: {missingPresets}. Available presets: {availablePresets}.")
    {
        MissingPresets = missingPresets;
        AvailablePresets = availablePresets;
    }

    public string MissingPresets { get; }

    public string AvailablePresets { get; }
}

internal sealed record MidiSf2Config(
    double Sf2Volume,
    bool MidiLoop,
    Sf2BankMode Sf2BankMode,
    double Sf2PreEqStrength,
    double Sf2PreLowPassHz,
    bool Sf2AutoLowPass,
    bool MidiPitchBendWorkaround,
    MidiProgramCompactionMode MidiProgramCompaction,
    MidiSf2AdsrMode AdsrMode);
internal enum Sf2BankMode
{
    Used,
    Full,
}

internal enum MidiProgramCompactionMode
{
    Auto,
    Compact,
    Preserve,
}

internal enum MidiSf2AdsrMode
{
    Auto,
    Authored,
    Template,
}
