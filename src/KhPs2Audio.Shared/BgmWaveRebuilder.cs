namespace KhPs2Audio.Shared;

public static class BgmWaveRebuilder
{
    private const int DefaultSequenceBpm = 120;
    private const double DefaultLoopHoldMinutes = 60.0;
    private const byte PlaybackKey = 58;
    private const byte PlaybackVelocity = 127;
    private const int SourceSampleRate = 44_100;
    private const int MinimumStereoSampleRate = 12_000;
    private const int MinimumMonoSampleRate = 6_000;
    private const string ConfigFileName = "config.ini";

    public static string ReplaceFromWave(string wavePath, TextWriter log)
    {
        var inputWavePath = Path.GetFullPath(wavePath);
        if (!File.Exists(inputWavePath))
        {
            throw new FileNotFoundException("Input WAV file was not found.", inputWavePath);
        }

        var assetDirectory = Path.GetDirectoryName(inputWavePath)
            ?? throw new InvalidOperationException("Could not determine the input directory.");
        var assetStem = ResolveAssetStem(inputWavePath);
        var bgmPath = Path.Combine(assetDirectory, $"{assetStem}.bgm");
        if (!File.Exists(bgmPath))
        {
            throw new FileNotFoundException($"No matching .bgm was found next to the WAV. Expected: {bgmPath}", bgmPath);
        }

        var bgmInfo = BgmParser.Parse(bgmPath);
        var wdPath = WdLocator.FindForBgm(bgmInfo)
            ?? throw new FileNotFoundException("No matching .wd file was found for the requested .bgm.", bgmPath);

        var config = LoadConfig(log);
        var wave = ApplyVolume(WaveReader.ReadStereoPcm16(inputWavePath), config.Volume);
        var outputDirectory = Path.Combine(assetDirectory, "output");
        Directory.CreateDirectory(outputDirectory);

        var outputBgmPath = Path.Combine(outputDirectory, Path.GetFileName(bgmPath));
        var outputWdPath = Path.Combine(outputDirectory, Path.GetFileName(wdPath));

        var outputWd = BuildWd(wdPath, bgmInfo.BankId, wave, log);
        var outputBgmBytes = BuildBgm(bgmPath, bgmInfo.SequenceId, bgmInfo.BankId, wave, outputWd, config, log);

        File.WriteAllBytes(outputBgmPath, outputBgmBytes);
        File.WriteAllBytes(outputWdPath, outputWd.Bytes);

        log.WriteLine($"Input WAV: {inputWavePath}");
        log.WriteLine($"Matched PS2 pair: {bgmPath} + {wdPath}");
        log.WriteLine($"Wrote rebuilt BGM: {outputBgmPath}");
        log.WriteLine($"Wrote rebuilt WD: {outputWdPath}");
        return outputDirectory;
    }

    private static RebuildConfig LoadConfig(TextWriter log)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        var config = RebuildConfig.Default;
        if (!File.Exists(configPath))
        {
            log.WriteLine($"Config: {ConfigFileName} not found next to the tool. Using defaults volume={config.Volume:0.###}, hold_minutes={config.HoldMinutes:0.###}.");
            return config;
        }

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
            if (!TryParseConfigDouble(valueText, out var value))
            {
                log.WriteLine($"Config warning: could not parse '{key}={valueText}'. Keeping the current value.");
                continue;
            }

            switch (key.ToLowerInvariant())
            {
                case "volume":
                    if (value <= 0)
                    {
                        log.WriteLine("Config warning: volume must be greater than 0. Keeping the current value.");
                    }
                    else
                    {
                        config = config with { Volume = value };
                    }

                    break;
                case "size":
                    log.WriteLine("Config notice: 'size' is no longer supported and will be ignored.");
                    break;
                case "hold_minutes":
                    if (value < 0.1)
                    {
                        log.WriteLine("Config warning: hold_minutes must be at least 0.1. Keeping the current value.");
                    }
                    else if (value > 600.0)
                    {
                        log.WriteLine("Config warning: hold_minutes must not exceed 600. Keeping the current value.");
                    }
                    else
                    {
                        config = config with { HoldMinutes = value };
                    }

                    break;
            }
        }

        log.WriteLine($"Config: loaded {configPath} -> volume={config.Volume:0.###}, hold_minutes={config.HoldMinutes:0.###}");
        return config;
    }

    private static bool TryParseConfigDouble(string valueText, out double value)
    {
        return double.TryParse(valueText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) ||
               double.TryParse(valueText.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static string ResolveAssetStem(string wavePath)
    {
        var fileStem = Path.GetFileNameWithoutExtension(wavePath);
        foreach (var candidate in GetStemCandidates(fileStem))
        {
            if (candidate.Length == 0)
            {
                continue;
            }

            var bgmCandidate = Path.Combine(Path.GetDirectoryName(wavePath)!, $"{candidate}.bgm");
            if (File.Exists(bgmCandidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not infer the target asset name from '{Path.GetFileName(wavePath)}'. " +
            "Place the WAV next to the matching musicXXX.bgm and name it like musicXXX.wav or musicXXX.ps2.wav.");
    }

    private static IEnumerable<string> GetStemCandidates(string fileStem)
    {
        yield return fileStem;

        var current = fileStem;
        foreach (var suffix in new[] { ".ps2", ".native", ".preview", ".custom", ".edited" })
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

    private static AuthoredWdResult BuildWd(string originalWdPath, int bankId, WavePcmData wave, TextWriter log)
    {
        var templateBytes = File.ReadAllBytes(originalWdPath);
        if (templateBytes.Length < 0x20)
        {
            throw new InvalidDataException("Original .wd is too small.");
        }

        var originalInstrumentCount = checked((int)BinaryHelpers.ReadUInt32LE(templateBytes, 0x8));
        var originalRegionCount = checked((int)BinaryHelpers.ReadUInt32LE(templateBytes, 0xC));
        var originalRegionTableOffset = checked((int)BinaryHelpers.ReadUInt32LE(templateBytes, 0x20));
        var originalSampleCollectionOffset = checked(originalRegionTableOffset + (originalRegionCount * 0x20));
        if (originalSampleCollectionOffset > templateBytes.Length)
        {
            throw new InvalidDataException("Original .wd region table exceeds file length.");
        }

        var originalSampleBytes = Math.Min(
            checked((int)BinaryHelpers.ReadUInt32LE(templateBytes, 0x4)),
            templateBytes.Length - originalSampleCollectionOffset);

        var prepared = PrepareReplacementAudio(wave, originalSampleBytes, log);
        var sustainTemplate = BuildHoldAdsrTemplate();
        var output = new byte[templateBytes.Length];
        Buffer.BlockCopy(templateBytes, 0, output, 0, templateBytes.Length);
        output[0] = (byte)'W';
        output[1] = (byte)'D';
        BinaryHelpers.WriteUInt16LE(output, 0x2, (ushort)bankId);
        BinaryHelpers.WriteUInt32LE(output, 0x4, (uint)originalSampleBytes);

        var selectablePrograms = FindSingleRegionPrograms(templateBytes, originalInstrumentCount, originalSampleCollectionOffset);
        var neededPrograms = prepared.IsStereo ? 2 : 1;
        if (selectablePrograms.Count < neededPrograms)
        {
            throw new InvalidDataException("The original .wd does not expose enough single-region instruments for a conservative replacement path.");
        }

        var selectedPrograms = selectablePrograms.Take(neededPrograms).ToArray();
        Array.Clear(output, originalSampleCollectionOffset, originalSampleBytes);

        var sampleCursor = originalSampleCollectionOffset;
        var relativeSampleOffset = 0;
        for (var channelIndex = 0; channelIndex < prepared.EncodedChannels.Count; channelIndex++)
        {
            var encoded = prepared.EncodedChannels[channelIndex];
            var regionOffset = checked((int)BinaryHelpers.ReadUInt32LE(templateBytes, 0x20 + (selectedPrograms[channelIndex] * 4)));
            PatchRegionForLongPlayback(output, regionOffset, relativeSampleOffset, prepared, channelIndex, sustainTemplate);
            Buffer.BlockCopy(encoded, 0, output, sampleCursor, encoded.Length);
            sampleCursor += encoded.Length;
            relativeSampleOffset += encoded.Length;
        }

        var leftProgram = checked((byte)selectedPrograms[0]);
        byte? rightProgram = null;
        if (prepared.IsStereo)
        {
            rightProgram = checked((byte)selectedPrograms[1]);
        }

        log.WriteLine(
            $"Authored WD conservative rebuild: kept {originalInstrumentCount} instrument(s), reused programs {string.Join(", ", selectedPrograms)}, sample budget {originalSampleBytes} bytes, encoded {prepared.TotalEncodedBytes} bytes at {prepared.SampleRate} Hz ({(prepared.IsStereo ? "stereo" : "mono")}){(prepared.HasLoop ? $", loop {prepared.LoopStartSample}..{prepared.LoopEndSample} samples" : string.Empty)}.");
        return new AuthoredWdResult(output, leftProgram, rightProgram);
    }

    private static byte[] BuildBgm(
        string originalBgmPath,
        int sequenceId,
        int bankId,
        WavePcmData wave,
        AuthoredWdResult authoredWd,
        RebuildConfig config,
        TextWriter log)
    {
        var templateBytes = File.ReadAllBytes(originalBgmPath);
        if (templateBytes.Length < 0x20)
        {
            throw new InvalidDataException("Original .bgm is too small.");
        }

        var templateTrackCount = Math.Max(1, (int)templateBytes[0x08]);
        var ppqn = BinaryHelpers.ReadUInt16LE(templateBytes, 0x0E);
        var conductorTrack = TryReadTrack(templateBytes, 0) ?? BuildConductorTrack(DefaultSequenceBpm);
        var bpm = TryReadTempo(conductorTrack) ?? DefaultSequenceBpm;

        var durationSamples = wave.HasLoop && wave.LoopEndSample.HasValue
            ? wave.LoopEndSample.Value
            : Math.Max(wave.Left.Length, wave.Right.Length);
        var durationSeconds = durationSamples / (double)wave.SampleRate;
        if (wave.HasLoop)
        {
            durationSeconds = Math.Max(durationSeconds, config.HoldMinutes * 60.0);
        }

        var endTicks = Math.Max(
            ppqn,
            (int)Math.Ceiling(durationSeconds * bpm * ppqn / 60.0) + (ppqn * 2));

        var output = new byte[templateBytes.Length];
        Buffer.BlockCopy(templateBytes, 0, output, 0, templateBytes.Length);

        BinaryHelpers.WriteUInt16LE(output, 0x04, (ushort)sequenceId);
        BinaryHelpers.WriteUInt16LE(output, 0x06, (ushort)bankId);

        var trackLayout = ReadTrackLayout(templateBytes, templateTrackCount);
        for (var trackIndex = 1; trackIndex < templateTrackCount; trackIndex++)
        {
            var generatedTrack = trackIndex switch
            {
                1 => BuildPlaybackTrack(authoredWd.LeftProgram, 64, endTicks),
                2 when wave.IsStereo && authoredWd.RightProgram.HasValue => BuildPlaybackTrack(authoredWd.RightProgram.Value, 64, endTicks),
                _ => BuildSilentTrack(),
            };

            var targetLength = trackLayout[trackIndex].Length;
            if (generatedTrack.Length > targetLength)
            {
                throw new InvalidDataException(
                    $"The generated replacement track {trackIndex} does not fit into the original BGM slot (needed {generatedTrack.Length} bytes, slot has {targetLength}).");
            }

            Array.Clear(output, trackLayout[trackIndex].Start, targetLength);
            Buffer.BlockCopy(generatedTrack, 0, output, trackLayout[trackIndex].Start, generatedTrack.Length);
        }

        log.WriteLine(
            $"Authored BGM conservative rebuild: preserved {templateTrackCount} original track slot(s), tempo {bpm}, PPQN {ppqn}, end tick {endTicks}.");
        return output;
    }

    private static List<int> FindSingleRegionPrograms(byte[] templateBytes, int instrumentCount, int sampleCollectionOffset)
    {
        var programs = new List<int>();
        for (var instrumentIndex = 0; instrumentIndex < instrumentCount; instrumentIndex++)
        {
            var start = checked((int)BinaryHelpers.ReadUInt32LE(templateBytes, 0x20 + (instrumentIndex * 4)));
            var end = instrumentIndex < instrumentCount - 1
                ? checked((int)BinaryHelpers.ReadUInt32LE(templateBytes, 0x20 + ((instrumentIndex + 1) * 4)))
                : sampleCollectionOffset;

            var regionCount = (end - start) / 0x20;
            if (regionCount == 1)
            {
                programs.Add(instrumentIndex);
            }
        }

        return programs;
    }

    private static PreparedReplacementAudio PrepareReplacementAudio(WavePcmData wave, int budgetBytes, TextWriter log)
    {
        if (budgetBytes < 0x10)
        {
            throw new InvalidDataException("The original WD sample budget is too small.");
        }

        var stereoCandidate = TryFitReplacementAudio(wave, useStereo: wave.IsStereo, budgetBytes);
        if (stereoCandidate is not null && stereoCandidate.SampleRate >= MinimumStereoSampleRate)
        {
            log.WriteLine($"Selected stereo replacement path at {stereoCandidate.SampleRate} Hz to fit {budgetBytes} bytes.");
            return stereoCandidate;
        }

        var monoCandidate = TryFitReplacementAudio(wave, useStereo: false, budgetBytes)
            ?? throw new InvalidDataException("Could not fit the replacement WAV into the original WD sample budget.");
        if (monoCandidate.SampleRate < MinimumMonoSampleRate)
        {
            log.WriteLine(
                $"Warning: the full replacement only fits at about {monoCandidate.SampleRate} Hz mono within the original PS2 sample budget.");
        }
        else
        {
            log.WriteLine($"Selected mono replacement path at {monoCandidate.SampleRate} Hz to fit {budgetBytes} bytes.");
        }

        return monoCandidate;
    }

    private static AdsrTemplate BuildHoldAdsrTemplate()
    {
        // Fast attack, sustain level at maximum, sustain rate disabled, very slow release.
        return new AdsrTemplate(0x000F, 0x1FDF);
    }

    private static PreparedReplacementAudio? TryFitReplacementAudio(WavePcmData wave, bool useStereo, int budgetBytes)
    {
        var sourceChannels = useStereo
            ? new[] { wave.Left, wave.Right }
            : new[] { MixToMono(wave.Left, wave.Right) };

        var sourceLoop = NormalizeLoopPoints(wave, sourceChannels[0].Length);
        var durationSamples = sourceLoop?.EndSample ?? sourceChannels[0].Length;
        var durationSeconds = durationSamples / (double)wave.SampleRate;
        if (durationSeconds <= 0)
        {
            durationSeconds = 1.0 / wave.SampleRate;
        }

        var channelCount = sourceChannels.Length;
        var estimatedRate = Math.Min(
            SourceSampleRate,
            Math.Max(
                1,
                (int)Math.Floor((budgetBytes * 28.0) / (16.0 * channelCount * durationSeconds))));

        for (var candidateRate = estimatedRate; candidateRate >= 4_000; candidateRate--)
        {
            var resampledChannels = sourceChannels
                .Select(channel => ResampleChannel(channel, wave.SampleRate, candidateRate))
                .ToArray();
            WaveLoopPoints? resampledLoop = null;
            if (sourceLoop is not null)
            {
                var loopStart = (int)Math.Round(sourceLoop.StartSample * (candidateRate / (double)wave.SampleRate), MidpointRounding.AwayFromZero);
                var loopEnd = (int)Math.Round(sourceLoop.EndSample * (candidateRate / (double)wave.SampleRate), MidpointRounding.AwayFromZero);
                if (loopEnd > loopStart && loopEnd <= resampledChannels[0].Length)
                {
                    resampledLoop = new WaveLoopPoints(loopStart, loopEnd);
                    for (var channelIndex = 0; channelIndex < resampledChannels.Length; channelIndex++)
                    {
                        if (loopEnd < resampledChannels[channelIndex].Length)
                        {
                            Array.Resize(ref resampledChannels[channelIndex], loopEnd);
                        }
                    }
                }
            }

            var encodedChannels = resampledChannels
                .Select(channel => PsxAdpcmEncoder.Encode(
                    channel,
                    looping: resampledLoop is not null,
                    loopStartBytes: resampledLoop is null ? 0 : SamplesToLoopStartBytes(resampledLoop.StartSample)))
                .ToList();
            var totalBytes = encodedChannels.Sum(static channel => channel.Length);
            if (totalBytes <= budgetBytes)
            {
                return new PreparedReplacementAudio(
                    encodedChannels,
                    candidateRate,
                    useStereo,
                    totalBytes,
                    resampledLoop is not null,
                    resampledLoop?.StartSample,
                    resampledLoop?.EndSample,
                    resampledLoop is null ? 0 : SamplesToLoopStartBytes(resampledLoop.StartSample));
            }
        }

        return null;
    }

    private static WaveLoopPoints? NormalizeLoopPoints(WavePcmData wave, int channelLength)
    {
        if (!wave.HasLoop || !wave.LoopStartSample.HasValue || !wave.LoopEndSample.HasValue)
        {
            return null;
        }

        var start = Math.Clamp(wave.LoopStartSample.Value, 0, Math.Max(0, channelLength - 1));
        var end = Math.Clamp(wave.LoopEndSample.Value, 0, channelLength);
        return end > start ? new WaveLoopPoints(start, end) : null;
    }

    private static int SamplesToLoopStartBytes(int loopStartSample)
    {
        return Math.Max(0, (loopStartSample / 28) * 0x10);
    }

    private static short[] MixToMono(short[] left, short[] right)
    {
        var mono = new short[Math.Min(left.Length, right.Length)];
        for (var i = 0; i < mono.Length; i++)
        {
            mono[i] = (short)Math.Clamp((left[i] + right[i]) / 2, short.MinValue, short.MaxValue);
        }

        return mono;
    }

    private static short[] ResampleChannel(short[] input, int sourceRate, int targetRate)
    {
        if (input.Length == 0)
        {
            return [];
        }

        if (sourceRate == targetRate)
        {
            return (short[])input.Clone();
        }

        var outputLength = Math.Max(1, (int)Math.Round(input.Length * (targetRate / (double)sourceRate), MidpointRounding.AwayFromZero));
        var output = new short[outputLength];
        for (var i = 0; i < outputLength; i++)
        {
            var position = i * (sourceRate / (double)targetRate);
            var index0 = Math.Min((int)position, input.Length - 1);
            var index1 = Math.Min(index0 + 1, input.Length - 1);
            var fraction = position - index0;
            var sample = input[index0] + ((input[index1] - input[index0]) * fraction);
            output[i] = (short)Math.Clamp(Math.Round(sample, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
        }

        return output;
    }

    private static WavePcmData ApplyVolume(WavePcmData wave, double volume)
    {
        if (Math.Abs(volume - 1.0) < 0.0000001)
        {
            return wave;
        }

        return new WavePcmData(
            ScaleChannel(wave.Left, volume),
            ScaleChannel(wave.Right, volume),
            wave.SampleRate,
            wave.IsStereo,
            wave.LoopStartSample,
            wave.LoopEndSample);
    }

    private static short[] ScaleChannel(short[] input, double volume)
    {
        var output = new short[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            var sample = input[i] * volume;
            output[i] = (short)Math.Clamp(Math.Round(sample, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
        }

        return output;
    }

    private static void PatchRegionForLongPlayback(
        byte[] output,
        int regionOffset,
        int sampleOffset,
        PreparedReplacementAudio prepared,
        int channelIndex,
        AdsrTemplate sustainTemplate)
    {
        output[regionOffset + 0x00] = 0x00;
        output[regionOffset + 0x01] = 0x03;
        BinaryHelpers.WriteUInt32LE(output, regionOffset + 0x04, (uint)sampleOffset);
        BinaryHelpers.WriteUInt32LE(output, regionOffset + 0x08, (uint)(prepared.HasLoop ? prepared.LoopStartBytes : 0));
        BinaryHelpers.WriteUInt16LE(output, regionOffset + 0x0E, sustainTemplate.Adsr1);
        BinaryHelpers.WriteUInt16LE(output, regionOffset + 0x10, sustainTemplate.Adsr2);
        var rootNote = PlaybackKey - (12.0 * Math.Log(prepared.SampleRate / (double)SourceSampleRate, 2.0));
        EncodeRootNote(rootNote, out var rawFineTune, out var rawUnityKey);
        output[regionOffset + 0x12] = rawFineTune;
        output[regionOffset + 0x13] = rawUnityKey;
        output[regionOffset + 0x14] = 0x7F;
        output[regionOffset + 0x15] = 0x7F;
        output[regionOffset + 0x16] = 0x7F;
        output[regionOffset + 0x17] = prepared.IsStereo
            ? channelIndex == 0 ? (byte)0x80 : (byte)0xFF
            : (byte)0xC0;
        output[regionOffset + 0x18] = 0x02;
    }

    private static void EncodeRootNote(double rootNote, out byte rawFineTune, out byte rawUnityKey)
    {
        var unityKey = (int)Math.Round(rootNote, MidpointRounding.AwayFromZero);
        var fineTune = (int)Math.Round((rootNote - unityKey) * 100.0, MidpointRounding.AwayFromZero);
        fineTune = Math.Clamp(fineTune, -50, 50);
        rawFineTune = (byte)Math.Clamp((int)Math.Round(((fineTune + 50) / 100.0) * 255.0, MidpointRounding.AwayFromZero), 0, 255);
        rawUnityKey = unchecked((byte)(0x3A - unityKey));
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

    private static byte[]? TryReadTrack(byte[] data, int trackIndex)
    {
        if (data.Length < 0x20 || trackIndex < 0 || trackIndex >= data[0x08])
        {
            return null;
        }

        var cursor = 0x20;
        for (var index = 0; index < data[0x08]; index++)
        {
            if (cursor + 4 > data.Length)
            {
                return null;
            }

            var trackLength = checked((int)BinaryHelpers.ReadUInt32LE(data, cursor));
            var trackStart = cursor + 4;
            var trackEnd = trackStart + trackLength;
            if (trackEnd > data.Length)
            {
                return null;
            }

            if (index == trackIndex)
            {
                var bytes = new byte[trackLength];
                Buffer.BlockCopy(data, trackStart, bytes, 0, trackLength);
                return bytes;
            }

            cursor = trackEnd;
        }

        return null;
    }

    private static int? TryReadTempo(byte[] track)
    {
        var offset = 0;
        while (offset < track.Length)
        {
            _ = ReadVarLen(track, ref offset);
            if (offset >= track.Length)
            {
                break;
            }

            var status = track[offset++];
            switch (status)
            {
                case 0x00:
                    return null;
                case 0x08:
                    return offset < track.Length ? track[offset] : null;
                case 0x02:
                case 0x03:
                case 0x04:
                case 0x10:
                case 0x18:
                case 0x60:
                case 0x61:
                case 0x7F:
                    break;
                case 0x0A:
                case 0x0D:
                case 0x20:
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
                    offset += 1;
                    break;
                case 0x0C:
                case 0x11:
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
                case 0x12:
                case 0x13:
                case 0x1A:
                    offset += 1;
                    break;
                default:
                    return null;
            }
        }

        return null;
    }

    private static byte[] BuildConductorTrack(int bpm)
    {
        return
        [
            0x00, 0x0C, 0x04, 0x04,
            0x00, 0x08, (byte)Math.Clamp(bpm, 1, 255),
            0x00, 0x00,
        ];
    }

    private static byte[] BuildPlaybackTrack(byte program, int pan, int endTicks)
    {
        var bytes = new List<byte>(32);
        WriteDelta(bytes, 0);
        bytes.Add(0x20);
        bytes.Add(program);

        WriteDelta(bytes, 0);
        bytes.Add(0x22);
        bytes.Add(0x7F);

        WriteDelta(bytes, 0);
        bytes.Add(0x24);
        bytes.Add(0x7F);

        WriteDelta(bytes, 0);
        bytes.Add(0x26);
        bytes.Add((byte)pan);

        WriteDelta(bytes, 0);
        bytes.Add(0x11);
        bytes.Add(PlaybackKey);
        bytes.Add(PlaybackVelocity);

        WriteDelta(bytes, endTicks);
        bytes.Add(0x1A);
        bytes.Add(PlaybackKey);

        WriteDelta(bytes, 96);
        bytes.Add(0x00);
        return [.. bytes];
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

    private static int Align16(int value)
    {
        return (value + 0x0F) & ~0x0F;
    }

    private sealed record AdsrTemplate(ushort Adsr1, ushort Adsr2);
    private sealed record TrackLayout(int Start, int Length);
    private sealed record RebuildConfig(double Volume, double HoldMinutes)
    {
        public static RebuildConfig Default { get; } = new(1.0, DefaultLoopHoldMinutes);
    }
    private sealed record PreparedReplacementAudio(
        List<byte[]> EncodedChannels,
        int SampleRate,
        bool IsStereo,
        int TotalEncodedBytes,
        bool HasLoop,
        int? LoopStartSample,
        int? LoopEndSample,
        int LoopStartBytes);
    private sealed record AuthoredWdResult(byte[] Bytes, byte LeftProgram, byte? RightProgram);
}
