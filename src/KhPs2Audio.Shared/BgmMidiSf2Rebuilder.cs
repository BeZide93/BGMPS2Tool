using System.Text.Json;

namespace KhPs2Audio.Shared;

public static class BgmMidiSf2Rebuilder
{
    private const string ConfigFileName = "config.ini";
    private const double DefaultVolume = 1.0;
    private const ushort DefaultPpqn = 48;
    private const int SpuSampleRate = 44100;
    private const int SpuMaxLevel = 0x7FFF;
    private const int MaxEnvelopeSearchSamples = SpuSampleRate * 120;
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

        var volume = LoadVolume(log);
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

        var outputBgm = BuildBgm(bgmPath, bgmInfo.SequenceId, bgmInfo.BankId, midi, plan, log);

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

    private static double LoadVolume(TextWriter log)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        if (!File.Exists(configPath))
        {
            log.WriteLine($"Config: {ConfigFileName} not found next to the tool. Using default volume={DefaultVolume:0.###}.");
            return DefaultVolume;
        }

        var volume = DefaultVolume;
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
            if (!key.Equals("volume", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var valueText = line[(separatorIndex + 1)..].Trim();
            if (!double.TryParse(valueText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out volume) &&
                !double.TryParse(valueText.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out volume))
            {
                log.WriteLine($"Config warning: could not parse volume={valueText}. Using the current value.");
                volume = DefaultVolume;
            }
        }

        if (volume <= 0)
        {
            log.WriteLine("Config warning: volume must be greater than 0. Using the default value.");
            volume = DefaultVolume;
        }

        log.WriteLine($"Config: loaded {configPath} -> volume={volume:0.###}");
        return volume;
    }

    private static ConversionPlan BuildPlan(MidiFile midi, SoundFontFile soundFont, double volume, TextWriter log)
    {
        var warnings = new HashSet<string>(soundFont.Warnings, StringComparer.Ordinal);
        var usedPresetRefs = GetUsedPresetRefs(midi);
        var missingPresetRefs = new HashSet<PresetRef>();
        var preferredVelocities = GetPreferredVelocities(midi);
        var programMap = new Dictionary<PresetRef, ProgramMapping>();
        var authoredSamples = new Dictionary<string, AuthoredSample>(StringComparer.Ordinal);
        var instruments = new List<AuthoredInstrument>();
        var nextInstrumentIndex = 0;

        foreach (var presetRef in usedPresetRefs)
        {
            var preset = ResolvePreset(soundFont, presetRef, warnings, missingPresetRefs);
            if (preset is null)
            {
                continue;
            }

            if (nextInstrumentIndex > byte.MaxValue)
            {
                throw new InvalidDataException("The converted MIDI references more than 256 unique SoundFont programs, which exceeds the PS2 BGM program limit.");
            }

            var preferredVelocity = preferredVelocities.TryGetValue(presetRef, out var mappedVelocity)
                ? mappedVelocity
                : 100;
            var normalizedRegions = SoundFontParser.NormalizeRegions(preset.Regions, warnings, preferredVelocity);
            var authoredRegions = new List<AuthoredRegion>();
            foreach (var region in normalizedRegions.OrderBy(static region => region.KeyHigh))
            {
                var authoredSample = GetOrAddAuthoredSample(
                    authoredSamples,
                    region.IdentityKey,
                    region.SourceSampleName,
                    region.Pcm,
                    region.Looping,
                    region.LoopStartSample,
                    volume);
                var envelope = EncodeAdsr(region);
                var isStereo = region.StereoPcm is not null && !string.IsNullOrWhiteSpace(region.StereoIdentityKey);

                authoredRegions.Add(new AuthoredRegion(
                    authoredSample,
                    region.KeyLow,
                    region.KeyHigh,
                    region.RootKey,
                    region.FineTuneCents,
                    Math.Clamp(region.Volume, 0f, 1f),
                    isStereo ? GetStereoLeftPan(region.Pan) : Math.Clamp(region.Pan, -1f, 1f),
                    envelope,
                    isStereo));

                if (isStereo)
                {
                    var stereoSample = GetOrAddAuthoredSample(
                        authoredSamples,
                        region.StereoIdentityKey!,
                        region.StereoSourceSampleName ?? $"{region.SourceSampleName}-R",
                        region.StereoPcm!,
                        region.Looping,
                        region.LoopStartSample,
                        volume);

                    authoredRegions.Add(new AuthoredRegion(
                        stereoSample,
                        region.KeyLow,
                        region.KeyHigh,
                        region.RootKey,
                        region.FineTuneCents,
                        Math.Clamp(region.Volume, 0f, 1f),
                        GetStereoRightPan(region.Pan),
                        envelope,
                        true));
                }
            }

            var instrument = new AuthoredInstrument(nextInstrumentIndex, preset.Name, authoredRegions);
            instruments.Add(instrument);
            programMap.Add(presetRef, new ProgramMapping((byte)nextInstrumentIndex, preset.Name, instrument.Regions.Count));
            log.WriteLine($"Preset {presetRef.Bank}/{presetRef.Program} -> preferred velocity {preferredVelocity}, authored {instrument.Regions.Count} region(s).");
            nextInstrumentIndex++;
        }

        if (missingPresetRefs.Count > 0)
        {
            var missingList = string.Join(", ", missingPresetRefs.OrderBy(static preset => preset.Bank).ThenBy(static preset => preset.Program).Select(static preset => $"{preset.Bank}/{preset.Program}"));
            var availableList = string.Join(", ", soundFont.Presets.OrderBy(static preset => preset.Bank).ThenBy(static preset => preset.Program).Select(static preset => $"{preset.Bank}/{preset.Program}"));
            throw new MissingSoundFontPresetException(missingList, availableList);
        }

        var channelPlans = BuildTrackPlans(midi, programMap, warnings);

        if (midi.Tracks.SelectMany(static track => track.Events).OfType<MidiPitchBendEvent>().Any())
        {
            warnings.Add("Pitch-bend events are currently ignored because the KH2 BGM pitch opcode mapping is not known yet.");
        }

        log.WriteLine($"MIDI analysis: format {midi.Format}, PPQN {midi.Division}, {midi.Tracks.Count} track(s).");
        log.WriteLine($"SoundFont analysis: {soundFont.Presets.Count} preset(s), {instruments.Count} preset(s) referenced by the MIDI.");
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
            var sustainDown = false;
            var deferredNoteOffs = new HashSet<int>();
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
                    case MidiProgramChangeEvent programChange:
                        currentProgram = programChange.Program;
                        if (TryResolveProgram(programMap, bankMsb, bankLsb, currentProgram, out var mappedProgram))
                        {
                            authoredEvents.Add(new AuthoredProgramEvent(programChange.Tick, mappedProgram));
                            emittedProgram = mappedProgram;
                        }
                        else
                        {
                            warnings.Add($"No authored WD instrument exists for MIDI bank {GetBankNumber(bankMsb, bankLsb)}, program {currentProgram}. Notes on channel {channel + 1} may be skipped.");
                        }

                        break;
                    case MidiControlChangeEvent control when control.Controller == 7:
                        authoredEvents.Add(new AuthoredVolumeEvent(control.Tick, MapMidiAttenuationController(control.Value)));
                        break;
                    case MidiControlChangeEvent control when control.Controller == 10:
                        authoredEvents.Add(new AuthoredPanEvent(control.Tick, control.Value));
                        break;
                    case MidiControlChangeEvent control when control.Controller == 11:
                        authoredEvents.Add(new AuthoredExpressionEvent(control.Tick, MapMidiAttenuationController(control.Value)));
                        break;
                    case MidiControlChangeEvent control when control.Controller == 64:
                        if (control.Value >= 64)
                        {
                            sustainDown = true;
                        }
                        else
                        {
                            sustainDown = false;
                            foreach (var key in deferredNoteOffs.OrderBy(static value => value))
                            {
                                authoredEvents.Add(new AuthoredNoteOffEvent(control.Tick, key));
                            }

                            deferredNoteOffs.Clear();
                        }

                        break;
                    case MidiNoteOffEvent noteOff:
                        if (sustainDown)
                        {
                            deferredNoteOffs.Add(noteOff.Key);
                        }
                        else
                        {
                            authoredEvents.Add(new AuthoredNoteOffEvent(noteOff.Tick, noteOff.Key));
                        }

                        break;
                    case MidiNoteOnEvent noteOn:
                        if (deferredNoteOffs.Remove(noteOn.Key))
                        {
                            authoredEvents.Add(new AuthoredNoteOffEvent(noteOn.Tick, noteOn.Key));
                        }

                        if (TryResolveProgram(programMap, bankMsb, bankLsb, currentProgram, out var noteProgram) && emittedProgram != noteProgram)
                        {
                            authoredEvents.Add(new AuthoredProgramEvent(noteOn.Tick, noteProgram));
                            emittedProgram = noteProgram;
                        }

                        if (emittedProgram.HasValue)
                        {
                            authoredEvents.Add(new AuthoredNoteOnEvent(noteOn.Tick, noteOn.Key, noteOn.Velocity));
                        }

                        break;
                }
            }

            if (deferredNoteOffs.Count > 0)
            {
                var releaseTick = authoredEvents.Count == 0 ? 0 : authoredEvents.Max(static evt => evt.Tick);
                foreach (var key in deferredNoteOffs.OrderBy(static value => value))
                {
                    authoredEvents.Add(new AuthoredNoteOffEvent(releaseTick, key));
                }
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

    private static bool TryResolveProgram(
        IReadOnlyDictionary<PresetRef, ProgramMapping> programMap,
        int bankMsb,
        int bankLsb,
        int program,
        out byte mappedProgram)
    {
        var exact = new PresetRef(GetBankNumber(bankMsb, bankLsb), program);
        if (programMap.TryGetValue(exact, out var exactMapping))
        {
            mappedProgram = exactMapping.InstrumentIndex;
            return true;
        }

        var coarse = new PresetRef(bankMsb << 7, program);
        if (programMap.TryGetValue(coarse, out var coarseMapping))
        {
            mappedProgram = coarseMapping.InstrumentIndex;
            return true;
        }

        var fallback = new PresetRef(0, program);
        if (programMap.TryGetValue(fallback, out var fallbackMapping))
        {
            mappedProgram = fallbackMapping.InstrumentIndex;
            return true;
        }

        mappedProgram = 0;
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
        var templateRegionBytes = new byte[0x20];
        Buffer.BlockCopy(bank.OriginalBytes, bank.Regions[0].FileOffset, templateRegionBytes, 0, templateRegionBytes.Length);

        var instrumentCount = plan.Instruments.Count;
        var totalRegions = plan.Instruments.Sum(static instrument => instrument.Regions.Count);
        var regionTableOffset = Align16(0x20 + (instrumentCount * 4));
        var sampleCollectionOffset = regionTableOffset + (totalRegions * 0x20);

        var sampleOffsetLookup = new Dictionary<string, int>(StringComparer.Ordinal);
        var sampleBytes = new List<byte>();
        foreach (var sample in plan.Samples)
        {
            sampleOffsetLookup.Add(sample.IdentityKey, sampleBytes.Count);
            sampleBytes.AddRange(sample.EncodedBytes);
        }

        var output = new byte[sampleCollectionOffset + sampleBytes.Count];
        Buffer.BlockCopy(templateHeader, 0, output, 0, templateHeader.Length);
        BinaryHelpers.WriteUInt16LE(output, 0x2, (ushort)bankId);
        BinaryHelpers.WriteUInt32LE(output, 0x4, (uint)sampleBytes.Count);
        BinaryHelpers.WriteUInt32LE(output, 0x8, (uint)instrumentCount);
        BinaryHelpers.WriteUInt32LE(output, 0xC, (uint)totalRegions);

        var currentRegionOffset = regionTableOffset;
        foreach (var instrument in plan.Instruments.OrderBy(static instrument => instrument.Index))
        {
            BinaryHelpers.WriteUInt32LE(output, 0x20 + (instrument.Index * 4), (uint)currentRegionOffset);
            for (var regionIndex = 0; regionIndex < instrument.Regions.Count; regionIndex++)
            {
                var region = instrument.Regions[regionIndex];
                var regionBytes = new byte[0x20];
                Buffer.BlockCopy(templateRegionBytes, 0, regionBytes, 0, regionBytes.Length);

                regionBytes[0x00] = region.Stereo ? (byte)0x01 : (byte)0x00;
                regionBytes[0x01] = (byte)((regionIndex == 0 ? 0x01 : 0x00) | (regionIndex == instrument.Regions.Count - 1 ? 0x02 : 0x00));
                BinaryHelpers.WriteUInt32LE(output, 0x20 + (instrument.Index * 4), (uint)(currentRegionOffset - (regionIndex * 0x20)));
                BinaryHelpers.WriteUInt32LE(regionBytes, 0x04, (uint)sampleOffsetLookup[region.Sample.IdentityKey]);
                BinaryHelpers.WriteUInt32LE(regionBytes, 0x08, (uint)(region.Sample.Looping ? region.Sample.LoopStartBytes : 0));
                BinaryHelpers.WriteUInt16LE(regionBytes, 0x0E, region.Envelope.Adsr1);
                BinaryHelpers.WriteUInt16LE(regionBytes, 0x10, region.Envelope.Adsr2);
                EncodeRootNote(region.RootKey + (region.FineTuneCents / 100.0), out var fineTune, out var unityKey);
                regionBytes[0x12] = fineTune;
                regionBytes[0x13] = unityKey;
                regionBytes[0x14] = (byte)Math.Clamp(region.KeyHigh, 0, 127);
                regionBytes[0x15] = 0x7F;
                regionBytes[0x16] = (byte)Math.Clamp((int)Math.Round(region.Volume * 127.0, MidpointRounding.AwayFromZero), 0, 127);
                regionBytes[0x17] = EncodeWdPan(region.Pan);
                regionBytes[0x18] = region.Sample.Looping ? (byte)0x02 : (byte)0x00;

                Buffer.BlockCopy(regionBytes, 0, output, currentRegionOffset, regionBytes.Length);
                currentRegionOffset += regionBytes.Length;
            }
        }

        sampleBytes.ToArray().CopyTo(output, sampleCollectionOffset);
        log.WriteLine($"Authored WD from MIDI+SF2: {instrumentCount} instrument(s), {totalRegions} region(s), {sampleBytes.Count} bytes of PSX-ADPCM sample data.");
        return output;
    }

    private static byte[] BuildBgm(string originalBgmPath, int sequenceId, int bankId, MidiFile midi, ConversionPlan plan, TextWriter log)
    {
        var templateBytes = File.ReadAllBytes(originalBgmPath);
        if (templateBytes.Length < 0x20)
        {
            throw new InvalidDataException("Original .bgm is too small.");
        }

        var ppqn = midi.Division > 0 ? checked((ushort)midi.Division) : DefaultPpqn;
        var trackBuffers = new List<byte[]>
        {
            BuildConductorTrack(midi)
        };
        trackBuffers.AddRange(plan.TrackPlans.Select(static track => BuildPlaybackTrack(track.Events)));

        var output = new List<byte>(0x20 + trackBuffers.Sum(static track => 4 + track.Length));
        output.AddRange(templateBytes.Take(0x20));
        while (output.Count < 0x20)
        {
            output.Add(0);
        }

        var header = output.ToArray();
        BinaryHelpers.WriteUInt16LE(header, 0x04, (ushort)sequenceId);
        BinaryHelpers.WriteUInt16LE(header, 0x06, (ushort)bankId);
        BinaryHelpers.WriteUInt16LE(header, 0x08, checked((ushort)trackBuffers.Count));
        BinaryHelpers.WriteUInt16LE(header, 0x0E, ppqn);

        output.Clear();
        output.AddRange(header);
        foreach (var track in trackBuffers)
        {
            output.AddRange(BitConverter.GetBytes(track.Length));
            output.AddRange(track);
        }

        var bytes = output.ToArray();
        BinaryHelpers.WriteUInt32LE(bytes, 0x10, (uint)bytes.Length);
        log.WriteLine($"Authored BGM from MIDI+SF2: {trackBuffers.Count} track(s), PPQN {ppqn}, file size {bytes.Length} bytes.");
        return bytes;
    }

    private static byte[] BuildConductorTrack(MidiFile midi)
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

            WriteDelta(bytes, checked((int)Math.Max(0, tempo.Tick - currentTick)));
            bytes.Add(0x08);
            bytes.Add((byte)Math.Clamp(tempo.Bpm, 1, 255));
            currentTick = tempo.Tick;
            previousTempo = tempo.Bpm;
        }

        WriteDelta(bytes, 0);
        bytes.Add(0x00);
        return [.. bytes];
    }

    private static byte[] BuildPlaybackTrack(IReadOnlyList<AuthoredTrackEvent> events)
    {
        var bytes = new List<byte>(Math.Max(32, events.Count * 4));
        long currentTick = 0;
        foreach (var evt in events)
        {
            WriteDelta(bytes, checked((int)Math.Max(0, evt.Tick - currentTick)));
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
                    bytes.Add(0x11);
                    bytes.Add((byte)Math.Clamp(noteOn.Key, 0, 127));
                    bytes.Add((byte)Math.Clamp(noteOn.Velocity, 1, 127));
                    break;
                case AuthoredNoteOffEvent noteOff:
                    bytes.Add(0x1A);
                    bytes.Add((byte)Math.Clamp(noteOff.Key, 0, 127));
                    break;
            }

            currentTick = evt.Tick;
        }

        WriteDelta(bytes, 96);
        bytes.Add(0x00);
        return [.. bytes];
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

    private static int MapMidiAttenuationController(int value)
    {
        var clamped = Math.Clamp(value, 0, 127);
        if (clamped is 0 or 127)
        {
            return clamped;
        }

        // SoundFont/GM playback usually treats level controllers more like a concave loudness curve
        // than a strict linear amplitude scalar. Compressing the lower controller range helps keep
        // secondary layers and noisy background content further back in the mix on the PS2 path.
        var normalized = clamped / 127.0;
        return Math.Clamp((int)Math.Round(Math.Pow(normalized, 2.0) * 127.0, MidpointRounding.AwayFromZero), 0, 127);
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
        double volume)
    {
        if (authoredSamples.TryGetValue(identityKey, out var authoredSample))
        {
            return authoredSample;
        }

        var adjustedPcm = ApplyVolume(pcm, volume);
        var encoded = PsxAdpcmEncoder.Encode(
            adjustedPcm,
            looping,
            looping ? SamplesToLoopStartBytes(loopStartSample) : 0);
        authoredSample = new AuthoredSample(
            identityKey,
            sourceSampleName,
            adjustedPcm,
            encoded,
            looping,
            looping ? SamplesToLoopStartBytes(loopStartSample) : 0);
        authoredSamples.Add(identityKey, authoredSample);
        return authoredSample;
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

    private static AdsrEnvelope EncodeAdsr(SoundFontRegion region)
    {
        var sustainNibble = EncodeSustainNibble(region.SustainLevel);
        var attack = SelectAttackProfile(SecondsToSamples(region.AttackSeconds));
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
    int RootKey,
    int FineTuneCents,
    float Volume,
    float Pan,
    AdsrEnvelope Envelope,
    bool Stereo);

internal sealed record AuthoredSample(
    string IdentityKey,
    string SourceSampleName,
    short[] Pcm,
    byte[] EncodedBytes,
    bool Looping,
    int LoopStartBytes);

internal sealed record AdsrEnvelope(ushort Adsr1, ushort Adsr2);

internal sealed record AttackAdsrProfile(bool Exponential, int Shift, int Step, int DurationSamples);

internal sealed record DecayAdsrProfile(int SustainNibble, int Shift, int DurationSamples);

internal sealed record ReleaseAdsrProfile(int SustainNibble, bool Exponential, int Shift, int DurationSamples);

internal sealed record PresetRef(int Bank, int Program);

internal sealed record ProgramMapping(byte InstrumentIndex, string PresetName, int RegionCount);

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
