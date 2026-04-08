using System.Globalization;

namespace KhPs2Audio.Shared;

public static class BgmNativeRenderer
{
    private const int OutputSampleRate = 48_000;
    private const int SampleBaseRate = 44_100;
    private const int AttackSamples = 480;
    private const int ReleaseSamples = OutputSampleRate * 2;
    private const int TailRenderSamples = OutputSampleRate * 10;
    private const int TailTrimPaddingSamples = OutputSampleRate / 2;
    private const float SilenceThreshold = 0.0001f;
    private const float HardPeakLimit = 0.98f;

    public static string RenderToWave(string bgmPath, TextWriter log)
    {
        var bgmInfo = BgmParser.Parse(bgmPath);
        var wdPath = WdLocator.FindForBgm(bgmInfo)
            ?? throw new FileNotFoundException("No matching .wd file found for the requested .bgm.", bgmInfo.FilePath);

        var sequence = SequenceParser.Parse(bgmInfo.FilePath);
        var bank = WdBank.Load(wdPath);

        if (sequence.BankId != bank.BankId)
        {
            log.WriteLine($"Warning: BGM bank id {sequence.BankId} does not match WD id {bank.BankId}. Continuing anyway.");
        }

        log.WriteLine($"Native render input: {Path.GetFileName(sequence.FilePath)} + {Path.GetFileName(bank.FilePath)}");
        log.WriteLine($"Sequence: {sequence.Tracks.Count} tracks, PPQN {sequence.Ppqn}, end tick {sequence.EndTick}");
        log.WriteLine($"Bank: {bank.Instruments.Count} instruments, {bank.Samples.Count} decoded sample starts");

        var rendered = Render(sequence, bank, log);
        var outputDirectory = Path.Combine(Path.GetDirectoryName(sequence.FilePath)!, Path.GetFileNameWithoutExtension(sequence.FilePath));
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(sequence.FilePath)}.ps2.wav");
        WaveWriter.WriteStereoPcm16(outputPath, rendered.Left, rendered.Right, OutputSampleRate);
        log.WriteLine($"Wrote native PS2 render: {outputPath}");
        return outputPath;
    }

    private static RenderedWave Render(ParsedSequence sequence, WdBank bank, TextWriter log)
    {
        var tempoMap = TempoMap.Create(sequence.Ppqn, sequence.TempoEvents);
        var renderEvents = BuildRenderEvents(sequence, tempoMap);
        var lastEventSample = renderEvents.Count == 0 ? 0 : renderEvents[^1].SamplePosition;
        var maxBufferLength = checked(lastEventSample + TailRenderSamples);
        var left = new float[maxBufferLength];
        var right = new float[maxBufferLength];
        var trackStates = Enumerable.Range(0, sequence.Tracks.Count).Select(_ => new TrackState()).ToArray();
        var activeVoices = new List<ActiveVoice>();
        var warningCache = new HashSet<string>(StringComparer.Ordinal);

        var currentSample = 0;
        var eventIndex = 0;
        while (eventIndex < renderEvents.Count)
        {
            var nextSample = renderEvents[eventIndex].SamplePosition;
            if (nextSample > currentSample)
            {
                RenderSegment(currentSample, nextSample - currentSample, activeVoices, trackStates, left, right);
                currentSample = nextSample;
            }

            while (eventIndex < renderEvents.Count && renderEvents[eventIndex].SamplePosition == nextSample)
            {
                ApplyEvent(renderEvents[eventIndex], bank, trackStates, activeVoices, warningCache, log);
                eventIndex++;
            }
        }

        var renderLimit = maxBufferLength;
        while (currentSample < renderLimit && (activeVoices.Count > 0 || currentSample < lastEventSample + TailTrimPaddingSamples))
        {
            var step = Math.Min(2_048, renderLimit - currentSample);
            RenderSegment(currentSample, step, activeVoices, trackStates, left, right);
            currentSample += step;
        }

        ApplySafetyHeadroom(left, right);
        var finalLength = TrimLength(left, right, Math.Max(currentSample, lastEventSample));
        Array.Resize(ref left, finalLength);
        Array.Resize(ref right, finalLength);

        var renderedSeconds = finalLength / (double)OutputSampleRate;
        log.WriteLine($"Rendered length: {renderedSeconds.ToString("0.000", CultureInfo.InvariantCulture)} s");
        return new RenderedWave(left, right);
    }

    private static List<RenderEvent> BuildRenderEvents(ParsedSequence sequence, TempoMap tempoMap)
    {
        var renderEvents = new List<RenderEvent>();
        foreach (var track in sequence.Tracks)
        {
            foreach (var evt in track.Events)
            {
                var samplePosition = checked((int)tempoMap.TicksToSamples(evt.Tick));
                switch (evt)
                {
                    case ProgramEvent program:
                        renderEvents.Add(new RenderEvent(samplePosition, evt.Order, track.Index, RenderEventKind.Program, program.Value, 0));
                        break;
                    case VolumeEvent volume:
                        renderEvents.Add(new RenderEvent(samplePosition, evt.Order, track.Index, RenderEventKind.Volume, volume.Value, 0));
                        break;
                    case ExpressionEvent expression:
                        renderEvents.Add(new RenderEvent(samplePosition, evt.Order, track.Index, RenderEventKind.Expression, expression.Value, 0));
                        break;
                    case PanEvent pan:
                        renderEvents.Add(new RenderEvent(samplePosition, evt.Order, track.Index, RenderEventKind.Pan, pan.Value, 0));
                        break;
                    case NoteOnEvent noteOn:
                        renderEvents.Add(new RenderEvent(samplePosition, evt.Order, track.Index, RenderEventKind.NoteOn, noteOn.Key, noteOn.Velocity));
                        break;
                    case NoteOffEvent noteOff:
                        renderEvents.Add(new RenderEvent(samplePosition, evt.Order, track.Index, RenderEventKind.NoteOff, noteOff.Key, 0));
                        break;
                }
            }
        }

        renderEvents.Sort(static (a, b) =>
        {
            var sampleComparison = a.SamplePosition.CompareTo(b.SamplePosition);
            return sampleComparison != 0 ? sampleComparison : a.Order.CompareTo(b.Order);
        });
        return renderEvents;
    }

    private static void ApplyEvent(
        RenderEvent evt,
        WdBank bank,
        TrackState[] trackStates,
        List<ActiveVoice> activeVoices,
        HashSet<string> warningCache,
        TextWriter log)
    {
        var state = trackStates[evt.TrackIndex];
        switch (evt.Kind)
        {
            case RenderEventKind.Program:
                state.Program = evt.Value1;
                break;
            case RenderEventKind.Volume:
                state.Volume = evt.Value1;
                break;
            case RenderEventKind.Expression:
                state.Expression = evt.Value1;
                break;
            case RenderEventKind.Pan:
                state.Pan = evt.Value1;
                break;
            case RenderEventKind.NoteOff:
                for (var i = 0; i < activeVoices.Count; i++)
                {
                    var voice = activeVoices[i];
                    if (voice.TrackIndex == evt.TrackIndex && voice.Key == evt.Value1 && voice.ReleaseSample < 0)
                    {
                        voice.ReleaseSample = evt.SamplePosition;
                    }
                }
                break;
            case RenderEventKind.NoteOn:
                if (state.Program < 0 || state.Program >= bank.Instruments.Count)
                {
                    WarnOnce(warningCache, log, $"program:{state.Program}", $"Skipping note on track {evt.TrackIndex}: instrument {state.Program} is missing in the WD bank.");
                    return;
                }

                var matchingLayers = bank.Instruments[state.Program].Regions
                    .Where(region =>
                        evt.Value1 >= region.KeyLow &&
                        evt.Value1 <= region.KeyHigh &&
                        evt.Value2 >= region.VelocityLow &&
                        evt.Value2 <= region.VelocityHigh)
                    .ToArray();

                if (matchingLayers.Length == 0)
                {
                    WarnOnce(
                        warningCache,
                        log,
                        $"region:{state.Program}:{evt.Value1}",
                        $"Skipping note on track {evt.TrackIndex}: no region found for instrument {state.Program}, key {evt.Value1}.");
                    return;
                }

                var voiceLayers = new List<ActiveLayer>(matchingLayers.Length);
                foreach (var layer in matchingLayers)
                {
                    var rootNote = layer.UnityKey + (layer.FineTuneCents / 100.0);
                    var pitchRatio = Math.Pow(2.0, (evt.Value1 - rootNote) / 12.0);
                    var step = pitchRatio * SampleBaseRate / OutputSampleRate;
                    if (step <= 0)
                    {
                        continue;
                    }

                    voiceLayers.Add(new ActiveLayer(layer.Sample, step, (evt.Value2 / 127f) * layer.Volume, layer.Pan));
                }

                if (voiceLayers.Count == 0)
                {
                    return;
                }

                activeVoices.Add(new ActiveVoice(evt.TrackIndex, evt.Value1, evt.SamplePosition, voiceLayers));
                break;
        }
    }

    private static void RenderSegment(
        int segmentStart,
        int segmentLength,
        List<ActiveVoice> activeVoices,
        TrackState[] trackStates,
        float[] left,
        float[] right)
    {
        if (segmentLength <= 0 || activeVoices.Count == 0)
        {
            return;
        }

        for (var voiceIndex = activeVoices.Count - 1; voiceIndex >= 0; voiceIndex--)
        {
            var voice = activeVoices[voiceIndex];
            RenderVoice(segmentStart, segmentLength, voice, trackStates[voice.TrackIndex], left, right);
            if (voice.IsDead)
            {
                activeVoices.RemoveAt(voiceIndex);
            }
        }
    }

    private static void RenderVoice(
        int segmentStart,
        int segmentLength,
        ActiveVoice voice,
        TrackState trackState,
        float[] left,
        float[] right)
    {
        var trackGain = trackState.Gain;
        var trackPan = trackState.PanNormalized;

        for (var frame = 0; frame < segmentLength; frame++)
        {
            var outputIndex = segmentStart + frame;
            var env = GetEnvelope(voice, outputIndex);
            if (env <= 0f)
            {
                voice.IsDead = true;
                return;
            }

            var anyLayerAlive = false;
            var mixLeft = 0f;
            var mixRight = 0f;

            for (var layerIndex = 0; layerIndex < voice.Layers.Count; layerIndex++)
            {
                var layer = voice.Layers[layerIndex];
                if (layer.IsFinished)
                {
                    continue;
                }

                if (!TryReadSample(layer, out var sampleValue))
                {
                    layer.IsFinished = true;
                    continue;
                }

                anyLayerAlive = true;
                var combinedPan = Math.Clamp(trackPan + (layer.Pan - 0.5f), 0f, 1f);
                GetEqualPowerGains(combinedPan, out var panLeft, out var panRight);

                var value = sampleValue * layer.BaseGain * trackGain * env;
                mixLeft += value * panLeft;
                mixRight += value * panRight;
                layer.Position += layer.Step;
            }

            if (!anyLayerAlive)
            {
                voice.IsDead = true;
                return;
            }

            left[outputIndex] += mixLeft;
            right[outputIndex] += mixRight;
        }
    }

    private static float GetEnvelope(ActiveVoice voice, int samplePosition)
    {
        var attack = Math.Min(1f, (samplePosition - voice.StartSample + 1) / (float)AttackSamples);
        if (voice.ReleaseSample < 0 || samplePosition < voice.ReleaseSample)
        {
            return attack;
        }

        var releaseProgress = (samplePosition - voice.ReleaseSample) / (float)ReleaseSamples;
        if (releaseProgress >= 1f)
        {
            return 0f;
        }

        var release = MathF.Exp(-6f * releaseProgress);
        return attack * release;
    }

    private static bool TryReadSample(ActiveLayer layer, out float sample)
    {
        var position = layer.Position;
        var hasLooped = layer.HasLooped;
        var success = PsxSpuInterpolation.TryRead(layer.Sample, ref position, ref hasLooped, out sample);
        layer.Position = position;
        layer.HasLooped = hasLooped;
        return success;
    }

    private static void GetEqualPowerGains(float pan, out float left, out float right)
    {
        var clamped = Math.Clamp(pan, 0f, 1f);
        if (clamped <= 0.5f)
        {
            left = 1f;
            right = clamped * 2f;
            return;
        }

        left = (1f - clamped) * 2f;
        right = 1f;
    }

    private static void ApplySafetyHeadroom(float[] left, float[] right)
    {
        var peak = 0f;
        for (var i = 0; i < left.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(left[i]));
            peak = Math.Max(peak, Math.Abs(right[i]));
        }

        if (peak <= HardPeakLimit)
        {
            return;
        }

        var scale = HardPeakLimit / peak;
        for (var i = 0; i < left.Length; i++)
        {
            left[i] *= scale;
            right[i] *= scale;
        }
    }

    private static int TrimLength(float[] left, float[] right, int maxLength)
    {
        var lastAudible = -1;
        for (var i = Math.Min(maxLength, left.Length) - 1; i >= 0; i--)
        {
            if (Math.Abs(left[i]) > SilenceThreshold || Math.Abs(right[i]) > SilenceThreshold)
            {
                lastAudible = i;
                break;
            }
        }

        if (lastAudible < 0)
        {
            return Math.Min(OutputSampleRate, left.Length);
        }

        return Math.Min(left.Length, lastAudible + TailTrimPaddingSamples);
    }

    private static void WarnOnce(HashSet<string> warningCache, TextWriter log, string key, string message)
    {
        if (warningCache.Add(key))
        {
            log.WriteLine(message);
        }
    }

    private sealed record RenderedWave(float[] Left, float[] Right);

    private sealed class TrackState
    {
        public int Program { get; set; }
        public int Volume { get; set; } = 127;
        public int Expression { get; set; } = 127;
        public int Pan { get; set; } = 64;

        public float Gain => Math.Clamp(Volume / 127f, 0f, 1f) * Math.Clamp(Expression / 127f, 0f, 1f);

        public float PanNormalized => Math.Clamp(Pan / 127f, 0f, 1f);
    }

    private sealed class ActiveVoice
    {
        public ActiveVoice(int trackIndex, int key, int startSample, List<ActiveLayer> layers)
        {
            TrackIndex = trackIndex;
            Key = key;
            StartSample = startSample;
            Layers = layers;
        }

        public int TrackIndex { get; }
        public int Key { get; }
        public int StartSample { get; }
        public List<ActiveLayer> Layers { get; }
        public int ReleaseSample { get; set; } = -1;
        public bool IsDead { get; set; }
    }

    private sealed class ActiveLayer
    {
        public ActiveLayer(DecodedSample sample, double step, float baseGain, float pan)
        {
            Sample = sample;
            Step = step;
            BaseGain = baseGain;
            Pan = pan;
        }

        public DecodedSample Sample { get; }
        public double Step { get; }
        public float BaseGain { get; }
        public float Pan { get; }
        public double Position { get; set; }
        public bool HasLooped { get; set; }
        public bool IsFinished { get; set; }
    }

    private sealed class TempoMap
    {
        private TempoMap(int ppqn, List<TempoPoint> points)
        {
            Ppqn = ppqn;
            Points = points;
        }

        private int Ppqn { get; }
        private List<TempoPoint> Points { get; }

        public static TempoMap Create(int ppqn, IReadOnlyList<TempoEvent> tempoEvents)
        {
            var ordered = tempoEvents
                .OrderBy(evt => evt.Tick)
                .ThenBy(evt => evt.Order)
                .ToList();

            var points = new List<TempoPoint>();
            foreach (var tempo in ordered)
            {
                if (points.Count > 0 && points[^1].Tick == tempo.Tick)
                {
                    points[^1] = points[^1] with { Bpm = tempo.Bpm };
                }
                else
                {
                    points.Add(new TempoPoint(tempo.Tick, tempo.Bpm, 0.0));
                }
            }

            if (points.Count == 0)
            {
                points.Add(new TempoPoint(0, 120, 0.0));
            }
            else if (points[0].Tick != 0)
            {
                points.Insert(0, new TempoPoint(0, points[0].Bpm, 0.0));
            }

            for (var i = 1; i < points.Count; i++)
            {
                var previous = points[i - 1];
                var deltaTicks = points[i].Tick - previous.Tick;
                var seconds = previous.SecondsAtTick + (deltaTicks * 60.0 / (ppqn * previous.Bpm));
                points[i] = points[i] with { SecondsAtTick = seconds };
            }

            return new TempoMap(ppqn, points);
        }

        public long TicksToSamples(long tick)
        {
            var tempoIndex = FindTempoIndex(tick);
            var tempoPoint = Points[tempoIndex];
            var seconds = tempoPoint.SecondsAtTick + ((tick - tempoPoint.Tick) * 60.0 / (Ppqn * tempoPoint.Bpm));
            return (long)Math.Round(seconds * OutputSampleRate, MidpointRounding.AwayFromZero);
        }

        private int FindTempoIndex(long tick)
        {
            var low = 0;
            var high = Points.Count - 1;
            while (low <= high)
            {
                var mid = (low + high) / 2;
                if (Points[mid].Tick <= tick)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return Math.Max(0, high);
        }

        private sealed record TempoPoint(long Tick, int Bpm, double SecondsAtTick);
    }

    private sealed class WdBank
    {
        private WdBank(string filePath, int bankId, List<Instrument> instruments, Dictionary<int, DecodedSample> samples)
        {
            FilePath = filePath;
            BankId = bankId;
            Instruments = instruments;
            Samples = samples;
        }

        public string FilePath { get; }
        public int BankId { get; }
        public List<Instrument> Instruments { get; }
        public Dictionary<int, DecodedSample> Samples { get; }

        public static WdBank Load(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var data = File.ReadAllBytes(fullPath);
            var magic = BinaryHelpers.ReadAscii(data, 0, 2);
            if (!string.Equals(magic, "WD", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unexpected WD magic '{magic}'.");
            }

            var bankId = BinaryHelpers.ReadUInt16LE(data, 0x2);
            var instrumentCount = checked((int)BinaryHelpers.ReadUInt32LE(data, 0x8));
            var totalRegions = checked((int)BinaryHelpers.ReadUInt32LE(data, 0xC));
            var sampleCollectionOffset = checked((int)(BinaryHelpers.ReadUInt32LE(data, 0x20) + (uint)(totalRegions * 0x20)));

            var sampleCache = new Dictionary<int, DecodedSample>();
            var instruments = new List<Instrument>(instrumentCount);
            for (var instrumentIndex = 0; instrumentIndex < instrumentCount; instrumentIndex++)
            {
                var start = checked((int)BinaryHelpers.ReadUInt32LE(data, 0x20 + (instrumentIndex * 4)));
                var end = instrumentIndex < instrumentCount - 1
                    ? checked((int)BinaryHelpers.ReadUInt32LE(data, 0x20 + ((instrumentIndex + 1) * 4)))
                    : sampleCollectionOffset;

                var regions = new List<InstrumentRegion>();
                for (var regionOffset = start; regionOffset < end; regionOffset += 0x20)
                {
                    var firstLastFlags = data[regionOffset + 1];
                    var isFirst = (firstLastFlags & 0x1) != 0;
                    var isLast = (firstLastFlags & 0x2) != 0;
                    var sampleOffset = checked((int)(BinaryHelpers.ReadUInt32LE(data, regionOffset + 0x4) & 0xFFFFFFF0));
                    var loopStartBytes = checked((int)BinaryHelpers.ReadUInt32LE(data, regionOffset + 0x8));
                    var adsr1 = BinaryHelpers.ReadUInt16LE(data, regionOffset + 0x0E);
                    var adsr2 = BinaryHelpers.ReadUInt16LE(data, regionOffset + 0x10);
                    var unityKey = 0x3A - BinaryHelpers.ReadSByte(data, regionOffset + 0x13);
                    var keyHigh = data[regionOffset + 0x14];
                    var velocityHigh = data[regionOffset + 0x15];
                    var volume = data[regionOffset + 0x16] / 127f;
                    var pan = ConvertWdPan(data[regionOffset + 0x17]);
                    var fineTuneCents = ConvertWdFineTune(data[regionOffset + 0x12]);

                    if (!sampleCache.TryGetValue(sampleOffset, out var decodedSample))
                    {
                        decodedSample = DecodeSample(data, sampleCollectionOffset, sampleOffset, loopStartBytes);
                        sampleCache.Add(sampleOffset, decodedSample);
                    }

                    regions.Add(new InstrumentRegion(
                        sampleOffset,
                        isFirst,
                        isLast,
                        0,
                        keyHigh,
                        0,
                        velocityHigh,
                        unityKey,
                        fineTuneCents,
                        volume,
                        pan,
                        adsr1,
                        adsr2,
                        decodedSample));
                }

                for (var regionIndex = 0; regionIndex < regions.Count; regionIndex++)
                {
                    var region = regions[regionIndex];
                    var previous = regionIndex > 0 ? regions[regionIndex - 1] : null;
                    var keyLow = region.IsFirst
                        ? 0
                        : previous is null
                            ? 0
                            : region.KeyHigh == previous.KeyHigh
                                ? previous.KeyLow
                                : previous.KeyHigh + 1;
                    var keyHigh = region.IsLast ? 0x7F : region.KeyHigh;
                    var velocityLow = region.IsFirst
                        ? 0
                        : previous is null
                            ? 0
                            : region.KeyHigh == previous.KeyHigh
                                ? previous.VelocityHigh + 1
                                : 0;
                    velocityLow = Math.Clamp(velocityLow, 0, 127);
                    var velocityHigh = Math.Clamp(region.VelocityHigh, velocityLow, 127);
                    regions[regionIndex] = region with { KeyLow = keyLow, KeyHigh = keyHigh, VelocityLow = velocityLow, VelocityHigh = velocityHigh };
                }

                instruments.Add(new Instrument(instrumentIndex, regions));
            }

            return new WdBank(fullPath, bankId, instruments, sampleCache);
        }

        private static DecodedSample DecodeSample(byte[] data, int sampleCollectionOffset, int relativeSampleOffset, int regionLoopStartBytes)
        {
            var sampleAbsoluteOffset = checked(sampleCollectionOffset + relativeSampleOffset);
            var sampleLengthBytes = MeasureSampleLength(data, sampleAbsoluteOffset);
            var blockCount = sampleLengthBytes / 0x10;
            var pcm = new float[blockCount * 28];
            var previous1 = 0;
            var previous2 = 0;

            for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
            {
                var blockOffset = sampleAbsoluteOffset + (blockIndex * 0x10);
                var filterRange = data[blockOffset];
                var flag = data[blockOffset + 1];
                DecodeBlock(data.AsSpan(blockOffset + 2, 14), filterRange, ref previous1, ref previous2, pcm.AsSpan(blockIndex * 28, 28));
            }

            var sampleBytes = new byte[sampleLengthBytes];
            Buffer.BlockCopy(data, sampleAbsoluteOffset, sampleBytes, 0, sampleLengthBytes);
            var loopInfo = WdSampleTool.ResolvePreferredLoopInfo(
                sampleBytes,
                regionLoopStartBytes > 0
                    ? LoopDescriptor.FromPsxAdpcmBytes(true, regionLoopStartBytes, Math.Max(0, sampleBytes.Length - regionLoopStartBytes))
                    : LoopDescriptor.None);
            var loopStartSample = Math.Clamp((loopInfo.LoopStartBytes / 0x10) * 28, 0, pcm.Length > 0 ? pcm.Length - 1 : 0);
            var looping = loopInfo.Looping && loopStartSample > 0 && pcm.Length > 1;
            return new DecodedSample(pcm, looping, loopStartSample, pcm.Length);
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

            return data.Length - offset;
        }

        private static void DecodeBlock(ReadOnlySpan<byte> source, byte filterRange, ref int previous1, ref int previous2, Span<float> destination)
        {
            ReadOnlySpan<int> coefficients0 = [0, 60, 115, 98, 122];
            ReadOnlySpan<int> coefficients1 = [0, 0, -52, -55, -60];

            var shift = filterRange & 0x0F;
            var filter = Math.Min((filterRange >> 4) & 0x0F, 4);
            var coef0 = coefficients0[filter];
            var coef1 = coefficients1[filter];
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

        private static int ConvertWdFineTune(byte rawFineTune)
        {
            return WdSampleTool.ConvertWdFineTune(rawFineTune);
        }

        private static float ConvertWdPan(byte rawPan)
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

    private sealed record Instrument(int Index, List<InstrumentRegion> Regions);

    private sealed record InstrumentRegion(
        int SampleOffset,
        bool IsFirst,
        bool IsLast,
        int KeyLow,
        int KeyHigh,
        int VelocityLow,
        int VelocityHigh,
        int UnityKey,
        int FineTuneCents,
        float Volume,
        float Pan,
        ushort Adsr1,
        ushort Adsr2,
        DecodedSample Sample);

    private sealed class SequenceParser
    {
        public static ParsedSequence Parse(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var data = File.ReadAllBytes(fullPath);
            if (data.Length < 0x20)
            {
                throw new InvalidDataException("BGM file is too small.");
            }

            if (!string.Equals(BinaryHelpers.ReadAscii(data, 0, 4), "BGM ", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unexpected BGM magic.");
            }

            var sequenceId = BinaryHelpers.ReadUInt16LE(data, 0x4);
            var bankId = BinaryHelpers.ReadUInt16LE(data, 0x6);
            var trackCount = data[0x8];
            var ppqn = BinaryHelpers.ReadUInt16LE(data, 0xE);

            var tracks = new List<ParsedTrack>(trackCount);
            var tempos = new List<TempoEvent>();
            long endTick = 0;
            var trackTableOffset = 0x20;
            for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
            {
                var trackSize = checked((int)BinaryHelpers.ReadUInt32LE(data, trackTableOffset));
                var trackStart = trackTableOffset + 4;
                var trackEnd = Math.Min(trackStart + trackSize, data.Length);
                var track = ParseTrack(trackIndex, data, trackStart, trackEnd);
                tracks.Add(track);
                endTick = Math.Max(endTick, track.EndTick);
                tempos.AddRange(track.Events.OfType<TempoEvent>());
                trackTableOffset = trackEnd;
            }

            return new ParsedSequence(fullPath, sequenceId, bankId, ppqn, tracks, tempos, endTick);
        }

        private static ParsedTrack ParseTrack(int trackIndex, byte[] data, int start, int end)
        {
            var events = new List<SequenceEvent>();
            long tick = 0;
            var offset = start;
            byte previousKey = 0;
            byte previousVelocity = 100;
            var order = 0;

            while (offset < end)
            {
                var delta = ReadVarLen(data, ref offset, end);
                tick += delta;
                if (offset >= end)
                {
                    break;
                }

                var status = data[offset++];
                switch (status)
                {
                    case 0x00:
                        return new ParsedTrack(trackIndex, events, tick);
                    case 0x02:
                    case 0x03:
                    case 0x04:
                    case 0x60:
                    case 0x61:
                    case 0x7F:
                        break;
                    case 0x08:
                        events.Add(new TempoEvent(tick, order++, data[offset++]));
                        break;
                    case 0x0A:
                    case 0x0D:
                    case 0x28:
                    case 0x31:
                    case 0x34:
                    case 0x35:
                    case 0x3E:
                    case 0x58:
                    case 0x5D:
                        offset++;
                        break;
                    case 0x0C:
                        offset += 2;
                        break;
                    case 0x10:
                        events.Add(new NoteOnEvent(tick, order++, previousKey, previousVelocity));
                        break;
                    case 0x11:
                        previousKey = data[offset++];
                        previousVelocity = data[offset++];
                        events.Add(new NoteOnEvent(tick, order++, previousKey, previousVelocity));
                        break;
                    case 0x12:
                        previousKey = data[offset++];
                        events.Add(new NoteOnEvent(tick, order++, previousKey, previousVelocity));
                        break;
                    case 0x13:
                        previousVelocity = data[offset++];
                        events.Add(new NoteOnEvent(tick, order++, previousKey, previousVelocity));
                        break;
                    case 0x18:
                        events.Add(new NoteOffEvent(tick, order++, previousKey));
                        break;
                    case 0x19:
                        offset += 2;
                        break;
                    case 0x1A:
                        previousKey = data[offset++];
                        events.Add(new NoteOffEvent(tick, order++, previousKey));
                        break;
                    case 0x20:
                        events.Add(new ProgramEvent(tick, order++, data[offset++]));
                        break;
                    case 0x22:
                        events.Add(new VolumeEvent(tick, order++, data[offset++]));
                        break;
                    case 0x24:
                        events.Add(new ExpressionEvent(tick, order++, data[offset++]));
                        break;
                    case 0x26:
                        events.Add(new PanEvent(tick, order++, data[offset++]));
                        break;
                    case 0x3C:
                        offset++;
                        break;
                    case 0x40:
                    case 0x48:
                    case 0x50:
                        offset += 3;
                        break;
                    case 0x47:
                    case 0x5C:
                        offset += 2;
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported BGM opcode 0x{status:X2} in track {trackIndex} at offset 0x{offset - 1:X}.");
                }
            }

            return new ParsedTrack(trackIndex, events, tick);
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
    }

    private sealed record ParsedSequence(
        string FilePath,
        int SequenceId,
        int BankId,
        int Ppqn,
        List<ParsedTrack> Tracks,
        List<TempoEvent> TempoEvents,
        long EndTick);

    private sealed record ParsedTrack(int Index, List<SequenceEvent> Events, long EndTick);

    private abstract record SequenceEvent(long Tick, int Order);

    private sealed record TempoEvent(long Tick, int Order, int Bpm) : SequenceEvent(Tick, Order);

    private sealed record ProgramEvent(long Tick, int Order, int Value) : SequenceEvent(Tick, Order);

    private sealed record VolumeEvent(long Tick, int Order, int Value) : SequenceEvent(Tick, Order);

    private sealed record ExpressionEvent(long Tick, int Order, int Value) : SequenceEvent(Tick, Order);

    private sealed record PanEvent(long Tick, int Order, int Value) : SequenceEvent(Tick, Order);

    private sealed record NoteOnEvent(long Tick, int Order, int Key, int Velocity) : SequenceEvent(Tick, Order);

    private sealed record NoteOffEvent(long Tick, int Order, int Key) : SequenceEvent(Tick, Order);

    private sealed record RenderEvent(int SamplePosition, int Order, int TrackIndex, RenderEventKind Kind, int Value1, int Value2);

    private enum RenderEventKind
    {
        Program,
        Volume,
        Expression,
        Pan,
        NoteOn,
        NoteOff,
    }
}
