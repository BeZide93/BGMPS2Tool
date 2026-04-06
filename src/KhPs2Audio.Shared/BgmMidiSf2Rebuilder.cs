using System.Text.Json;

namespace KhPs2Audio.Shared;

public static class BgmMidiSf2Rebuilder
{
    private const string ConfigFileName = "config.ini";
    private const string Sf2VolumeKey = "sf2_volume";
    private const string MidiLoopKey = "midi_loop";
    private const double DefaultSf2Volume = 1.0;
    private const bool DefaultMidiLoop = false;
    private const ushort DefaultPpqn = 48;
    private const int MaxAuthoredWdBytes = 980 * 1024;
    private const int MaxExpandedBgmGrowthBytes = 64 * 1024;
    private const double MaxExpandedBgmGrowthFactor = 4.0;
    private const int MaxExpandedOneShotBgmGrowthBytes = 96 * 1024;
    private const double MaxExpandedOneShotBgmGrowthFactor = 6.0;
    private const int DefaultMidiPanCenter = 64;
    private const double FastAttackClampSeconds = 0.125;
    private const int SpuSampleRate = 44100;
    private const int SpuMaxLevel = 0x7FFF;
    private const int MaxEnvelopeSearchSamples = SpuSampleRate * 120;
    private const int ShortLoopAlignmentThresholdSamples = 512;
    private const int PitchVariantStepCents = 25;
    private const int PitchVariantMaxResidualCents = 50;
    private const int SustainHoldShift = 31;
    private const int SustainHoldStep = 3;
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
                var soundFont = SoundFontParser.Parse(sf2Path);
                plan = BuildPlan(midi, soundFont, volume, log);
                plan = ConstrainPlanToWdBudget(plan, MaxAuthoredWdBytes, log);
                outputWd = BuildWd(wdPath, bgmInfo.BankId, plan, log);
                programSourceLabel = Path.GetFileName(sf2Path);
            }
            catch (MissingSoundFontPresetException ex)
            {
                log.WriteLine($"SoundFont fallback: {ex.Message}");
                log.WriteLine($"Falling back to original WD instrument mapping from: {wdPath}");
                plan = BuildPlanFromOriginalWd(midi, wdBank, log);
                outputWd = (byte[])wdBank.OriginalBytes.Clone();
                programSourceLabel = $"original WD fallback ({Path.GetFileName(wdPath)})";
            }
        }
        else
        {
            log.WriteLine($"No matching .sf2 was found next to the MIDI/BGM. Falling back to original WD instrument mapping from: {wdPath}");
            plan = BuildPlanFromOriginalWd(midi, wdBank, log);
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
                $"Config: {ConfigFileName} not found next to the tool. Using default {Sf2VolumeKey}={DefaultSf2Volume:0.###} and {MidiLoopKey}=0 for MIDI/SF2 conversion.");
            return new MidiSf2Config(DefaultSf2Volume, DefaultMidiLoop);
        }

        var volume = DefaultSf2Volume;
        var midiLoop = DefaultMidiLoop;
        var foundExplicitSf2Volume = false;
        var foundExplicitMidiLoop = false;
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
            }
        }

        if (volume <= 0)
        {
            log.WriteLine($"Config warning: {Sf2VolumeKey} must be greater than 0. Using the default value.");
            volume = DefaultSf2Volume;
        }

        var volumeLabel = foundExplicitSf2Volume
            ? $"{Sf2VolumeKey}={volume:0.###}"
            : $"{Sf2VolumeKey} not set, using neutral {Sf2VolumeKey}={volume:0.###}";
        var loopLabel = foundExplicitMidiLoop
            ? $"{MidiLoopKey}={(midiLoop ? 1 : 0)}"
            : $"{MidiLoopKey} not set, using default {MidiLoopKey}=0";
        log.WriteLine($"Config: loaded {configPath} -> {volumeLabel}; {loopLabel}");

        return new MidiSf2Config(volume, midiLoop);
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

    private static ConversionPlan BuildPlan(MidiFile midi, SoundFontFile soundFont, double volume, TextWriter log)
    {
        var warnings = new HashSet<string>(soundFont.Warnings, StringComparer.Ordinal);
        var usedPresetRefs = GetUsedPresetRefs(midi);
        var pitchVariantPresetRefs = GetPitchBendPresetRefs(midi);
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

        var compactSparsePrograms = ShouldCompactSparsePrograms(usedPresetRefs);
        if (compactSparsePrograms)
        {
            log.WriteLine("Program compaction: using dense PS2 instrument indices for sparse MIDI program numbers.");
        }

        var presetsToAuthor = compactSparsePrograms
            ? resolvedPresetMap.Values
                .DistinctBy(static preset => new PresetRef(preset.Bank, preset.Program))
                .OrderBy(static preset => preset.Bank)
                .ThenBy(static preset => preset.Program)
                .ToList()
            : availablePresets;
        var enableShortLoopPitchCompensation = ShouldEnableShortLoopPitchCompensation(presetsToAuthor, warnings);
        if (enableShortLoopPitchCompensation)
        {
            log.WriteLine("Short-loop pitch compensation: enabled for simple waveform-style SF2 content.");
        }

        var nextCompactInstrumentIndex = 0;
        foreach (var preset in presetsToAuthor)
        {
            var presetRef = new PresetRef(preset.Bank, preset.Program);
            var instrumentIndex = compactSparsePrograms
                ? AllocateCompactInstrumentIndex(usedInstrumentIndices, ref nextCompactInstrumentIndex)
                : AllocateInstrumentIndex(presetRef, usedInstrumentIndices);
            var normalizedRegions = CollapsePseudoStereoRegions(SoundFontParser.NormalizeRegions(preset.Regions, warnings));
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

                var authoredSample = GetOrAddAuthoredSample(
                    authoredSamples,
                    region.IdentityKey,
                    region.SourceSampleName,
                    region.Pcm,
                    region.Looping,
                    region.LoopStartSample,
                    volume,
                    enableShortLoopPitchCompensation);
                var envelope = EncodeAdsr(region);
                var isStereo = region.StereoPcm is not null && !string.IsNullOrWhiteSpace(region.StereoIdentityKey);
                var leftPitch = ApplySamplePitchOffset(region.RootKey, region.FineTuneCents, authoredSample.PitchOffsetSemitones);
                var preferAuthoredEnvelope = regionWasDownmixed || region.AttackSeconds <= FastAttackClampSeconds;

                authoredRegions.Add(new AuthoredRegion(
                    authoredSample,
                    region.KeyLow,
                    region.KeyHigh,
                    region.VelocityLow,
                    region.VelocityHigh,
                    leftPitch.RootKey,
                    leftPitch.FineTuneCents,
                    Math.Clamp(region.Volume, 0f, 1f),
                    isStereo ? GetStereoLeftPan(region.Pan) : Math.Clamp(region.Pan, -1f, 1f),
                    envelope,
                    isStereo,
                    preferAuthoredEnvelope));

                if (isStereo)
                {
                    var stereoSample = GetOrAddAuthoredSample(
                        authoredSamples,
                        region.StereoIdentityKey!,
                        region.StereoSourceSampleName ?? $"{region.SourceSampleName}-R",
                        region.StereoPcm!,
                        region.Looping,
                        region.LoopStartSample,
                        volume,
                        enableShortLoopPitchCompensation);
                    var rightPitch = ApplySamplePitchOffset(region.RootKey, region.FineTuneCents, stereoSample.PitchOffsetSemitones);

                    authoredRegions.Add(new AuthoredRegion(
                        stereoSample,
                        region.KeyLow,
                        region.KeyHigh,
                        region.VelocityLow,
                        region.VelocityHigh,
                        rightPitch.RootKey,
                        rightPitch.FineTuneCents,
                        Math.Clamp(region.Volume, 0f, 1f),
                        GetStereoRightPan(region.Pan),
                        envelope,
                        true,
                        preferAuthoredEnvelope));
                }
            }

            var instrument = new AuthoredInstrument(instrumentIndex, preset.Name, authoredRegions);
            instruments.Add(instrument);
            programMap[presetRef] = new ProgramMapping((byte)instrumentIndex, preset.Name, instrument.Regions.Count);
            var usageLabel = usedPresetRefs.Contains(presetRef) ? "used" : "preserved";
            log.WriteLine($"Preset {presetRef.Bank}/{presetRef.Program} -> instrument {instrumentIndex}, authored {instrument.Regions.Count} region(s), {usageLabel}.");
            if (downmixedPseudoStereoRegions > 0)
            {
                log.WriteLine($"Preset {presetRef.Bank}/{presetRef.Program}: downmixed {downmixedPseudoStereoRegions} pseudo-stereo region(s) to centered mono for KH2 playback compatibility.");
            }
        }

        foreach (var (requestedPresetRef, resolvedPreset) in resolvedPresetMap)
        {
            var resolvedPresetRef = new PresetRef(resolvedPreset.Bank, resolvedPreset.Program);
            if (requestedPresetRef == resolvedPresetRef || !programMap.TryGetValue(resolvedPresetRef, out var resolvedMapping))
            {
                continue;
            }

            programMap[requestedPresetRef] = resolvedMapping;
        }

        if (pitchVariantPresetRefs.Count > 0)
        {
            AddPitchVariantInstruments(instruments, usedInstrumentIndices, programMap, pitchVariantPresetRefs, log);
        }

        var channelPlans = BuildTrackPlans(midi, programMap, warnings);

        if (midi.Tracks.SelectMany(static track => track.Events).OfType<MidiPitchBendEvent>().Any())
        {
            warnings.Add("Pitch-bend events are approximated by bend-aware note retargeting plus fine-tuned instrument variants. Continuous bends are not yet emitted as native KH2 pitch opcodes.");
        }

        log.WriteLine($"MIDI analysis: format {midi.Format}, PPQN {midi.Division}, {midi.Tracks.Count} track(s).");
        log.WriteLine($"SoundFont analysis: {soundFont.Presets.Count} preset(s), {usedPresetRefs.Count} preset(s) referenced by the MIDI, {instruments.Count} instrument(s) authored into the WD.");
        log.WriteLine($"Authored WD plan: {instruments.Count} instrument(s), {instruments.Sum(static instrument => instrument.Regions.Count)} region(s), {authoredSamples.Count} unique sample(s).");

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
                    $"WD size guard: resampled authored SF2 content to an effective {Math.Round(SpuSampleRate * scale)} Hz budget so the rebuilt WD stays within {maxWdBytes} bytes.");
                return constrainedPlan;
            }

            var constrainedSampleBytes = EstimateStoredSampleBytes(constrainedPlan.Samples);
            scale *= Math.Clamp((availableSampleBytes / (double)constrainedSampleBytes) * 0.99, 0.5, 0.99);
        }

        throw new InvalidDataException(
            $"The authored WD would still exceed the maximum size of {maxWdBytes} bytes even after conservative sample-rate reduction.");
    }

    private static ConversionPlan BuildPlanFromOriginalWd(MidiFile midi, WdBankFile bank, TextWriter log)
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

        var trackPlans = BuildTrackPlans(midi, programMap, warnings);
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
        var bankMsb = new int[16];
        var bankLsb = new int[16];
        var currentProgram = new int[16];

        var orderedEvents = midi.Tracks
            .SelectMany(static track => track.Events)
            .OrderBy(static evt => evt.Tick)
            .ThenBy(static evt => evt.Order)
            .ToList();

        foreach (var evt in orderedEvents)
        {
            if (evt.Channel is < 0 or > 15)
            {
                continue;
            }

            switch (evt)
            {
                case MidiControlChangeEvent control when control.Controller == 0:
                    bankMsb[control.Channel] = control.Value;
                    break;
                case MidiControlChangeEvent control when control.Controller == 32:
                    bankLsb[control.Channel] = control.Value;
                    break;
                case MidiProgramChangeEvent programChange:
                    currentProgram[programChange.Channel] = programChange.Program;
                    break;
                case MidiNoteOnEvent noteOn:
                    used.Add(new PresetRef(GetBankNumber(bankMsb[noteOn.Channel], bankLsb[noteOn.Channel]), currentProgram[noteOn.Channel]));
                    break;
            }
        }

        return used;
    }

    private static Dictionary<PresetRef, int> GetPreferredVelocities(MidiFile midi)
    {
        var velocities = new Dictionary<PresetRef, List<int>>();
        var bankMsb = new int[16];
        var bankLsb = new int[16];
        var currentProgram = new int[16];

        var orderedEvents = midi.Tracks
            .SelectMany(static track => track.Events)
            .OrderBy(static evt => evt.Tick)
            .ThenBy(static evt => evt.Order)
            .ToList();

        foreach (var evt in orderedEvents)
        {
            if (evt.Channel is < 0 or > 15)
            {
                continue;
            }

            switch (evt)
            {
                case MidiControlChangeEvent control when control.Controller == 0:
                    bankMsb[control.Channel] = control.Value;
                    break;
                case MidiControlChangeEvent control when control.Controller == 32:
                    bankLsb[control.Channel] = control.Value;
                    break;
                case MidiProgramChangeEvent programChange:
                    currentProgram[programChange.Channel] = programChange.Program;
                    break;
                case MidiNoteOnEvent noteOn:
                    var presetRef = new PresetRef(GetBankNumber(bankMsb[noteOn.Channel], bankLsb[noteOn.Channel]), currentProgram[noteOn.Channel]);
                    if (!velocities.TryGetValue(presetRef, out var samples))
                    {
                        samples = [];
                        velocities.Add(presetRef, samples);
                    }

                    samples.Add(noteOn.Velocity);
                    break;
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
        var pitchBendChannels = midi.Tracks
            .SelectMany(static track => track.Events)
            .OfType<MidiPitchBendEvent>()
            .Select(static evt => evt.Channel)
            .Where(static channel => channel is >= 0 and < 16)
            .ToHashSet();
        if (pitchBendChannels.Count == 0)
        {
            return [];
        }

        var used = new HashSet<PresetRef>();
        var bankMsb = new int[16];
        var bankLsb = new int[16];
        var currentProgram = new int[16];

        var orderedEvents = midi.Tracks
            .SelectMany(static track => track.Events)
            .OrderBy(static evt => evt.Tick)
            .ThenBy(static evt => evt.Order)
            .ToList();

        foreach (var evt in orderedEvents)
        {
            if (evt.Channel is < 0 or > 15)
            {
                continue;
            }

            switch (evt)
            {
                case MidiControlChangeEvent control when control.Controller == 0:
                    bankMsb[control.Channel] = control.Value;
                    break;
                case MidiControlChangeEvent control when control.Controller == 32:
                    bankLsb[control.Channel] = control.Value;
                    break;
                case MidiProgramChangeEvent programChange:
                    currentProgram[programChange.Channel] = programChange.Program;
                    break;
                case MidiNoteOnEvent noteOn when pitchBendChannels.Contains(noteOn.Channel):
                    used.Add(new PresetRef(GetBankNumber(bankMsb[noteOn.Channel], bankLsb[noteOn.Channel]), currentProgram[noteOn.Channel]));
                    break;
            }
        }

        return used;
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

    private static bool ShouldCompactSparsePrograms(IReadOnlyCollection<PresetRef> usedPresetRefs)
    {
        if (usedPresetRefs.Count == 0)
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
        foreach (var preset in presetsToAuthor)
        {
            totalRegions += SoundFontParser.NormalizeRegions(preset.Regions, warnings).Count;
            if (totalRegions > 12)
            {
                return false;
            }
        }

        return totalRegions > 0;
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
            left.RootKey != right.RootKey ||
            left.FineTuneCents != right.FineTuneCents)
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

        if (presetRef.Bank == 128)
        {
            warnings.Add($"No SoundFont preset found for percussion bank {presetRef.Bank}, program {presetRef.Program}. Falling back to percussion bank 128 if possible.");
            preset = soundFont.FindPreset(128, 0);
            if (preset is not null)
            {
                return preset;
            }

            var nearestPercussion = soundFont.Presets
                .Where(static candidate => candidate.Bank == 128)
                .OrderBy(candidate => Math.Abs(candidate.Program - presetRef.Program))
                .ThenBy(static candidate => candidate.Program)
                .FirstOrDefault();
            if (nearestPercussion is not null)
            {
                return nearestPercussion;
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

    private static List<AuthoredTrackPlan> BuildTrackPlans(
        MidiFile midi,
        IReadOnlyDictionary<PresetRef, ProgramMapping> programMap,
        HashSet<string> warnings)
    {
        var channelEvents = new List<MidiEvent>[16];
        for (var channel = 0; channel < channelEvents.Length; channel++)
        {
            channelEvents[channel] = [];
        }

        foreach (var track in midi.Tracks)
        {
            foreach (var evt in track.Events)
            {
                if (evt.Channel is >= 0 and < 16)
                {
                    channelEvents[evt.Channel].Add(evt);
                }
            }
        }

        var plans = new List<AuthoredTrackPlan>();
        for (var channel = 0; channel < channelEvents.Length; channel++)
        {
            var events = channelEvents[channel]
                .OrderBy(static evt => evt.Tick)
                .ThenBy(static evt => EventPriority(evt))
                .ThenBy(static evt => evt.Order)
                .ToList();
            if (!events.OfType<MidiNoteOnEvent>().Any())
            {
                continue;
            }

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
            var trackName = midi.Tracks.FirstOrDefault(track => track.Events.Any(evt => evt.Channel == channel))?.Name ?? $"Channel {channel + 1}";

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
                        if (TryResolveProgramMapping(programMap, bankMsb, bankLsb, currentProgram, out var mappedProgram))
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

                        if (TryResolveProgramMapping(programMap, bankMsb, bankLsb, currentProgram, out var noteProgram))
                        {
                            var pitchTarget = ResolvePitchTarget(noteOn.Key, currentPitchBend, bendRangeSemitones, noteProgram);
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
                        currentPitchBend = pitchBend.Value;
                        if (!emittedProgram.HasValue || activeNotes.Count == 0)
                        {
                            break;
                        }

                        if (!TryResolveProgramMapping(programMap, bankMsb, bankLsb, currentProgram, out var bendProgram))
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
            MidiProgramChangeEvent => 0,
            MidiControlChangeEvent control when control.Controller is 0 or 32 => 1,
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
        int bankMsb,
        int bankLsb,
        int program,
        out ProgramMapping mappedProgram)
    {
        var exact = new PresetRef(GetBankNumber(bankMsb, bankLsb), program);
        if (programMap.TryGetValue(exact, out var exactMapping))
        {
            mappedProgram = exactMapping;
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

    private static int GetBankNumber(int bankMsb, int bankLsb)
    {
        return (bankMsb << 7) | bankLsb;
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

        var instrumentCount = plan.Instruments.Count == 0
            ? 0
            : plan.Instruments.Max(static instrument => instrument.Index) + 1;
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
                var templateRegion = SelectTemplateRegion(templateRegionsByInstrument, instrument, region, regionIndex);
                var regionBytes = new byte[0x20];
                var templateRegionOffset = templateRegion?.FileOffset ?? bank.Regions[0].FileOffset;
                Buffer.BlockCopy(bank.OriginalBytes, templateRegionOffset, regionBytes, 0, regionBytes.Length);

                regionBytes[0x00] = region.Stereo ? (byte)0x01 : (byte)0x00;
                regionBytes[0x01] = (byte)((regionIndex == 0 ? 0x01 : 0x00) | (regionIndex == instrument.Regions.Count - 1 ? 0x02 : 0x00));
                BinaryHelpers.WriteUInt32LE(output, 0x20 + (instrument.Index * 4), (uint)(currentRegionOffset - (regionIndex * 0x20)));
                BinaryHelpers.WriteUInt32LE(regionBytes, 0x04, (uint)sampleOffsetLookup[region.Sample.IdentityKey]);
                BinaryHelpers.WriteUInt32LE(regionBytes, 0x08, (uint)WdLayoutHelpers.OffsetLoopStartForStoredChunk(region.Sample.Looping, region.Sample.LoopStartBytes));
                var adsr = templateRegion is not null && !region.PreferAuthoredEnvelope
                    ? new AdsrEnvelope(templateRegion.Adsr1, templateRegion.Adsr2)
                    : region.Envelope;
                BinaryHelpers.WriteUInt16LE(regionBytes, 0x0E, adsr.Adsr1);
                BinaryHelpers.WriteUInt16LE(regionBytes, 0x10, adsr.Adsr2);
                EncodeRootNote(region.RootKey + (region.FineTuneCents / 100.0), out var fineTune, out var unityKey);
                regionBytes[0x12] = fineTune;
                regionBytes[0x13] = unityKey;
                regionBytes[0x14] = (byte)Math.Clamp(region.KeyHigh, 0, 127);
                regionBytes[0x15] = (byte)Math.Clamp(region.VelocityHigh, 0, 127);
                regionBytes[0x16] = (byte)Math.Clamp((int)Math.Round(region.Volume * 127.0, MidpointRounding.AwayFromZero), 0, 127);
                regionBytes[0x17] = EncodeWdPan(region.Pan);
                regionBytes[0x18] = region.Sample.Looping ? (byte)0x02 : (byte)0x00;

                Buffer.BlockCopy(regionBytes, 0, output, currentRegionOffset, regionBytes.Length);
                currentRegionOffset += regionBytes.Length;
            }
        }

        sampleBytes.ToArray().CopyTo(output, sampleCollectionOffset);
        log.WriteLine($"Authored WD from MIDI+SF2: {instrumentCount} instrument(s), {totalRegions} region(s), {sampleBytes.Count} bytes of PSX-ADPCM sample data using KH2-style 16-byte zero lead-ins for each sample chunk.");
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

        var pitchSemitones = 12.0 * Math.Log(SpuSampleRate / (double)targetRate, 2.0);
        var sampleMap = new Dictionary<string, AuthoredSample>(StringComparer.Ordinal);
        foreach (var sample in plan.Samples)
        {
            var loopStartSamples = sample.Looping ? LoopStartBytesToSamples(sample.LoopStartBytes) : 0;
            var resampledPcm = AudioDsp.ResampleMono(sample.Pcm, SpuSampleRate, targetRate);
            var resampledLoopStart = sample.Looping
                ? Math.Clamp((int)Math.Round(loopStartSamples * (targetRate / (double)SpuSampleRate), MidpointRounding.AwayFromZero), 0, Math.Max(0, resampledPcm.Length - 1))
                : 0;
            var prepared = PrepareLoopAlignedSample(resampledPcm, sample.Looping, resampledLoopStart, false);
            var encoded = PsxAdpcmEncoder.Encode(
                prepared.Pcm,
                prepared.Looping,
                prepared.Looping ? SamplesToLoopStartBytes(prepared.LoopStartSample) : 0);
            sampleMap[sample.IdentityKey] = sample with
            {
                Pcm = prepared.Pcm,
                EncodedBytes = encoded,
                LoopStartBytes = prepared.Looping ? SamplesToLoopStartBytes(prepared.LoopStartSample) : 0,
            };
        }

        var instruments = plan.Instruments
            .Select(instrument => new AuthoredInstrument(
                instrument.Index,
                instrument.PresetName,
                instrument.Regions.Select(region =>
                {
                    var shiftedRoot = region.RootKey + (region.FineTuneCents / 100.0) + pitchSemitones;
                    SplitRootNote(shiftedRoot, out var shiftedRootKey, out var shiftedFineTune);
                    return region with
                    {
                        Sample = sampleMap[region.Sample.IdentityKey],
                        RootKey = shiftedRootKey,
                        FineTuneCents = shiftedFineTune,
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
        AuthoredInstrument instrument,
        AuthoredRegion region,
        int regionIndex)
    {
        if (templateRegionsByInstrument.TryGetValue(instrument.Index, out var templateRegions) &&
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

        return null;
    }

    private static int ScoreTemplateRegion(WdRegionEntry template, AuthoredRegion region, int regionIndex)
    {
        var score = 0;

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
        return score;
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

        var activeTrackCount = plan.TrackPlans.Count + 1;
        if (activeTrackCount > templateTrackCount)
        {
            throw new InvalidDataException(
                $"The MIDI requires {activeTrackCount} BGM tracks including conductor, but the original file only exposes {templateTrackCount} track slots.");
        }

        var conductorTrack = BuildConductorTrack(midi, targetPpqn);
        var templateLoop = midiLoop ? TryReadTemplateLoop(originalBgmPath) : null;
        var generatedPlaybackTracks = plan.TrackPlans
            .Select((track, index) => new GeneratedTrack(
                track.Channel,
                track.Name,
                BuildPlaybackTrack(
                    track.Events,
                    checked((ushort)midi.Division),
                    targetPpqn,
                    midiLoop ? templateLoop : null,
                    midiLoop && index == 0)))
            .ToList();

        var trackLayout = ReadTrackLayout(templateBytes, templateTrackCount);
        var trackBytesBySlot = new byte[activeTrackCount][];
        var slotLengths = new int[activeTrackCount];
        trackBytesBySlot[0] = conductorTrack;
        slotLengths[0] = Math.Max(trackLayout[0].Length, conductorTrack.Length);

        for (var trackIndex = 0; trackIndex < generatedPlaybackTracks.Count; trackIndex++)
        {
            var generatedTrack = generatedPlaybackTracks[trackIndex];
            var outputIndex = trackIndex + 1;
            trackBytesBySlot[outputIndex] = generatedTrack.Bytes;
            slotLengths[outputIndex] = Math.Max(trackLayout[outputIndex].Length, generatedTrack.Bytes.Length);
        }

        var needsExpansion = slotLengths.Where((length, index) => length > trackLayout[index].Length).Any();
        var outputLength = CalculateExpandedBgmLength(slotLengths);
        var maxAllowedLength = CalculateMaxAllowedBgmLength(templateBytes.Length, midiLoop);
        if (needsExpansion && outputLength > maxAllowedLength)
        {
            var largestGeneratedTrack = generatedPlaybackTracks
                .OrderByDescending(track => track.Bytes.Length)
                .FirstOrDefault();
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
        BinaryHelpers.WriteUInt16LE(output, 0x08, (ushort)activeTrackCount);
        BinaryHelpers.WriteUInt16LE(output, 0x0E, targetPpqn);
        BinaryHelpers.WriteUInt32LE(output, 0x10, (uint)output.Length);

        var cursor = 0x20;
        for (var trackIndex = 0; trackIndex < activeTrackCount; trackIndex++)
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
        log.WriteLine(
            $"Authored BGM compact rebuild: wrote {activeTrackCount} track(s) (1 conductor + {generatedPlaybackTracks.Count} playback), target PPQN {targetPpqn}, {rebuildMode}.");
        if (midiLoop && generatedPlaybackTracks.Count > 0)
        {
            if (templateLoop is not null)
            {
                log.WriteLine($"BGM loop option: enabled via config; reused template loop ticks begin={templateLoop.BeginTick}, end={templateLoop.EndTick} on the first playback track.");
            }
            else
            {
                log.WriteLine("BGM loop option: enabled via config; no template loop was found, so a fallback loop was written on the first playback track.");
            }
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
        byte previousVelocity = 100;
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
                evt is AuthoredNoteOnEvent &&
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
                    if (key == previousKey && velocity == previousVelocity)
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
                    WriteNoteOff(bytes, key, ref previousKey);
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
                WriteDelta(bytes, 0);
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

    private static void WriteNoteOff(List<byte> bytes, byte key, ref byte previousKey)
    {
        if (key == previousKey)
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
        bool looping,
        int loopStartSample,
        double volume,
        bool enableShortLoopPitchCompensation)
    {
        if (authoredSamples.TryGetValue(identityKey, out var authoredSample))
        {
            return authoredSample;
        }

        var adjustedPcm = ApplyVolume(pcm, volume);
        var prepared = PrepareLoopAlignedSample(adjustedPcm, looping, loopStartSample, enableShortLoopPitchCompensation);
        var encoded = PsxAdpcmEncoder.Encode(
            prepared.Pcm,
            prepared.Looping,
            prepared.Looping ? SamplesToLoopStartBytes(prepared.LoopStartSample) : 0);
        authoredSample = new AuthoredSample(
            identityKey,
            sourceSampleName,
            prepared.Pcm,
            encoded,
            prepared.Looping,
            prepared.Looping ? SamplesToLoopStartBytes(prepared.LoopStartSample) : 0,
            prepared.PitchOffsetSemitones);
        authoredSamples.Add(identityKey, authoredSample);
        return authoredSample;
    }

    private static PreparedLoopSample PrepareLoopAlignedSample(short[] pcm, bool looping, int loopStartSample, bool enableShortLoopPitchCompensation)
    {
        if (!looping || pcm.Length == 0)
        {
            return new PreparedLoopSample(pcm, looping, 0, 0.0);
        }

        var safeLoopStart = Math.Clamp(loopStartSample, 0, Math.Max(0, pcm.Length - 1));
        var remainder = safeLoopStart % 28;
        if (remainder == 0)
        {
            return new PreparedLoopSample(pcm, true, safeLoopStart, 0.0);
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
                return new PreparedLoopSample(pcm, true, safeLoopStart, 0.0);
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
            return new PreparedLoopSample(rebuilt, true, alignedLoopStart, pitchOffsetSemitones);
        }

        // For short looping instrument samples, inserting duplicated PCM at the loop point
        // can create an audible stutter each time the sample wraps. Prefer a conservative
        // block-aligned loop that starts slightly earlier over duplicating audio content.
        return new PreparedLoopSample(pcm, true, safeLoopStart - remainder, 0.0);
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
    {
        return Math.Max(0, (loopStartSample / 28) * 0x10);
    }

    private static int LoopStartBytesToSamples(int loopStartBytes)
        => Math.Max(0, (loopStartBytes / 0x10) * 28);

    private static (int RootKey, int FineTuneCents) ApplySamplePitchOffset(int rootKey, int fineTuneCents, double pitchOffsetSemitones)
    {
        if (Math.Abs(pitchOffsetSemitones) < 0.000001)
        {
            return (rootKey, fineTuneCents);
        }

        var shiftedRoot = rootKey + (fineTuneCents / 100.0) + pitchOffsetSemitones;
        SplitRootNote(shiftedRoot, out var shiftedRootKey, out var shiftedFineTune);
        return (shiftedRootKey, shiftedFineTune);
    }

    private static AuthoredRegion RetuneRegion(AuthoredRegion region, int pitchVariantCents)
    {
        if (pitchVariantCents == 0)
        {
            return region;
        }

        var shiftedRoot = region.RootKey + (region.FineTuneCents / 100.0) - (pitchVariantCents / 100.0);
        SplitRootNote(shiftedRoot, out var shiftedRootKey, out var shiftedFineTune);
        return region with
        {
            RootKey = shiftedRootKey,
            FineTuneCents = shiftedFineTune,
        };
    }

    private static void SplitRootNote(double rootNote, out int rootKey, out int fineTuneCents)
    {
        var totalCents = (int)Math.Round(rootNote * 100.0, MidpointRounding.AwayFromZero);
        rootKey = (int)Math.Floor(totalCents / 100.0);
        fineTuneCents = totalCents - (rootKey * 100);
        if (fineTuneCents >= 100)
        {
            rootKey += fineTuneCents / 100;
            fineTuneCents %= 100;
        }
        else if (fineTuneCents <= -100)
        {
            var delta = (int)Math.Ceiling(Math.Abs(fineTuneCents) / 100.0);
            rootKey -= delta;
            fineTuneCents += delta * 100;
        }
    }

    private static AdsrEnvelope EncodeAdsr(SoundFontRegion region)
    {
        var sustainNibble = EncodeSustainNibble(region.SustainLevel);
        var attackSeconds = region.AttackSeconds <= FastAttackClampSeconds
            ? 0.0
            : region.AttackSeconds;
        var attack = SelectAttackProfile(SecondsToSamples(attackSeconds));
        var decay = SelectDecayProfile(sustainNibble, SecondsToSamples(region.HoldSeconds + region.DecaySeconds));
        var release = SelectReleaseProfile(sustainNibble, SecondsToSamples(region.ReleaseSeconds));

        var adsr1 = (ushort)(
            ((attack.Exponential ? 1 : 0) << 15) |
            (attack.Shift << 10) |
            (attack.Step << 8) |
            (decay.Shift << 4) |
            sustainNibble);

        var adsr2 = (ushort)(
            (1 << 14) |
            (SustainHoldShift << 8) |
            (SustainHoldStep << 6) |
            ((release.Exponential ? 1 : 0) << 5) |
            release.Shift);

        return new AdsrEnvelope(adsr1, adsr2);
    }

    private static int EncodeSustainNibble(float sustainLevel)
    {
        var clamped = Math.Clamp(sustainLevel, 0f, 1f);
        var targetLevel = (int)Math.Round(clamped * SpuMaxLevel, MidpointRounding.AwayFromZero);
        var sustainNibble = (int)Math.Round((targetLevel / 2048.0) - 1.0, MidpointRounding.AwayFromZero);
        return Math.Clamp(sustainNibble, 0, 15);
    }

    private static int SecondsToSamples(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return 0;
        }

        return (int)Math.Clamp(
            Math.Round(seconds * SpuSampleRate, MidpointRounding.AwayFromZero),
            0,
            MaxEnvelopeSearchSamples);
    }

    private static AttackAdsrProfile SelectAttackProfile(int targetSamples)
    {
        AttackAdsrProfile? best = null;
        var bestScore = double.MaxValue;
        foreach (var profile in AttackProfiles)
        {
            var score = ScoreDuration(profile.DurationSamples, targetSamples);
            if (score < bestScore)
            {
                best = profile;
                bestScore = score;
            }
        }

        return best ?? AttackProfiles[0];
    }

    private static DecayAdsrProfile SelectDecayProfile(int sustainNibble, int targetSamples)
    {
        DecayAdsrProfile? best = null;
        var bestScore = double.MaxValue;
        foreach (var profile in DecayProfiles)
        {
            if (profile.SustainNibble != sustainNibble)
            {
                continue;
            }

            var score = ScoreDuration(profile.DurationSamples, targetSamples);
            if (score < bestScore)
            {
                best = profile;
                bestScore = score;
            }
        }

        return best ?? DecayProfiles.First(profile => profile.SustainNibble == sustainNibble);
    }

    private static ReleaseAdsrProfile SelectReleaseProfile(int sustainNibble, int targetSamples)
    {
        ReleaseAdsrProfile? best = null;
        var bestScore = double.MaxValue;
        foreach (var profile in ReleaseProfiles)
        {
            if (profile.SustainNibble != sustainNibble)
            {
                continue;
            }

            var score = ScoreDuration(profile.DurationSamples, targetSamples);
            if (score < bestScore)
            {
                best = profile;
                bestScore = score;
            }
        }

        return best ?? ReleaseProfiles.First(profile => profile.SustainNibble == sustainNibble);
    }

    private static double ScoreDuration(int actualSamples, int targetSamples)
    {
        var actual = actualSamples + 1.0;
        var target = targetSamples + 1.0;
        return Math.Abs(Math.Log(actual / target));
    }

    private static IReadOnlyList<AttackAdsrProfile> BuildAttackProfiles()
    {
        var profiles = new List<AttackAdsrProfile>();
        for (var shift = 0; shift <= 31; shift++)
        {
            for (var step = 0; step <= 3; step++)
            {
                if (shift == SustainHoldShift && step == SustainHoldStep)
                {
                    continue;
                }

                profiles.Add(new AttackAdsrProfile(false, shift, step, SimulateAttackDuration(false, shift, step)));
                profiles.Add(new AttackAdsrProfile(true, shift, step, SimulateAttackDuration(true, shift, step)));
            }
        }

        return profiles;
    }

    private static IReadOnlyList<DecayAdsrProfile> BuildDecayProfiles()
    {
        var profiles = new List<DecayAdsrProfile>();
        for (var sustainNibble = 0; sustainNibble <= 15; sustainNibble++)
        {
            var sustainLevel = DecodeSustainNibble(sustainNibble);
            for (var shift = 0; shift <= 15; shift++)
            {
                profiles.Add(new DecayAdsrProfile(sustainNibble, shift, SimulateDecayDuration(shift, sustainLevel)));
            }
        }

        return profiles;
    }

    private static IReadOnlyList<ReleaseAdsrProfile> BuildReleaseProfiles()
    {
        var profiles = new List<ReleaseAdsrProfile>();
        for (var sustainNibble = 0; sustainNibble <= 15; sustainNibble++)
        {
            var startLevel = DecodeSustainNibble(sustainNibble);
            for (var shift = 0; shift <= 31; shift++)
            {
                profiles.Add(new ReleaseAdsrProfile(sustainNibble, false, shift, SimulateReleaseDuration(false, shift, startLevel)));
                profiles.Add(new ReleaseAdsrProfile(sustainNibble, true, shift, SimulateReleaseDuration(true, shift, startLevel)));
            }
        }

        return profiles;
    }

    private static int DecodeSustainNibble(int sustainNibble)
    {
        return (Math.Clamp(sustainNibble, 0, 15) + 1) * 0x800;
    }

    private static int SimulateAttackDuration(bool exponential, int shift, int step)
    {
        return SimulateEnvelopePhase(
            0,
            level => level >= SpuMaxLevel,
            level => CalculateAdsrStep(exponential, false, shift, step, level),
            level => CalculateCounterIncrement(exponential, false, shift, step, level));
    }

    private static int SimulateDecayDuration(int shift, int sustainLevel)
    {
        return SimulateEnvelopePhase(
            SpuMaxLevel,
            level => level <= sustainLevel,
            level => CalculateAdsrStep(true, true, shift, 0, level),
            level => CalculateCounterIncrement(true, true, shift, 0, level));
    }

    private static int SimulateReleaseDuration(bool exponential, int shift, int startLevel)
    {
        return SimulateEnvelopePhase(
            startLevel,
            level => level <= 0,
            level => CalculateAdsrStep(exponential, true, shift, 0, level),
            level => CalculateCounterIncrement(exponential, true, shift, 0, level));
    }

    private static int SimulateEnvelopePhase(
        int initialLevel,
        Func<int, bool> complete,
        Func<int, int> stepSelector,
        Func<int, int> incrementSelector)
    {
        var level = initialLevel;
        var counter = 0;
        var samples = 0;
        while (samples < MaxEnvelopeSearchSamples && !complete(level))
        {
            counter = (counter + incrementSelector(level)) & 0xFFFF;
            if ((counter & 0x8000) != 0)
            {
                level += stepSelector(level);
                level = Math.Clamp(level, 0, SpuMaxLevel);
            }

            samples++;
        }

        return samples;
    }

    private static int CalculateAdsrStep(bool exponential, bool decreasing, int shift, int step, int level)
    {
        var adsrStep = 7 - step;
        if (decreasing)
        {
            adsrStep = ~adsrStep;
        }

        adsrStep <<= Math.Max(0, 11 - shift);

        if (exponential && !decreasing && level > 0x6000)
        {
            adsrStep >>= 2;
        }
        else if (exponential && decreasing)
        {
            adsrStep = (adsrStep * Math.Max(level, 0)) / 0x8000;
        }

        return adsrStep;
    }

    private static int CalculateCounterIncrement(bool exponential, bool decreasing, int shift, int step, int level)
    {
        var increment = 0x8000 >> Math.Max(0, shift - 11);
        if (exponential && !decreasing && level > 0x6000 && shift >= 11)
        {
            increment >>= 2;
        }

        return Math.Max(increment, 1);
    }

    private static int Align16(int value)
    {
        return (value + 0x0F) & ~0x0F;
    }

    private static void EncodeRootNote(double rootNote, out byte rawFineTune, out byte rawUnityKey)
    {
        var unityKey = (int)Math.Round(rootNote, MidpointRounding.AwayFromZero);
        var fineTune = (int)Math.Round((rootNote - unityKey) * 100.0, MidpointRounding.AwayFromZero);
        fineTune = Math.Clamp(fineTune, -50, 50);
        rawFineTune = (byte)Math.Clamp((int)Math.Round(((fineTune + 50) / 100.0) * 255.0, MidpointRounding.AwayFromZero), 0, 255);
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
    List<AuthoredRegion> Regions);

internal sealed record AuthoredRegion(
    AuthoredSample Sample,
    int KeyLow,
    int KeyHigh,
    int VelocityLow,
    int VelocityHigh,
    int RootKey,
    int FineTuneCents,
    float Volume,
    float Pan,
    AdsrEnvelope Envelope,
    bool Stereo,
    bool PreferAuthoredEnvelope);

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
    bool Looping,
    int LoopStartBytes,
    double PitchOffsetSemitones);

internal sealed record AdsrEnvelope(ushort Adsr1, ushort Adsr2);

internal sealed record AttackAdsrProfile(bool Exponential, int Shift, int Step, int DurationSamples);

internal sealed record DecayAdsrProfile(int SustainNibble, int Shift, int DurationSamples);

internal sealed record ReleaseAdsrProfile(int SustainNibble, bool Exponential, int Shift, int DurationSamples);

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
    IReadOnlyList<string> Warnings);

internal sealed record MidiSf2TrackManifest(int Channel, string Name, int EventCount);

internal sealed record MidiSf2ProgramManifest(int Bank, int Program, byte InstrumentIndex, string PresetName, int RegionCount);

internal sealed record GeneratedTrack(int Channel, string Name, byte[] Bytes);

internal sealed record PreparedLoopSample(short[] Pcm, bool Looping, int LoopStartSample, double PitchOffsetSemitones);

internal sealed record PitchTarget(byte Program, int Key);

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

internal sealed record MidiSf2Config(double Sf2Volume, bool MidiLoop);
