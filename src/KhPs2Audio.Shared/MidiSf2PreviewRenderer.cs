using System.Globalization;

namespace KhPs2Audio.Shared;

public static class MidiSf2PreviewRenderer
{
    private const int OutputSampleRate = 48_000;
    private const int TailRenderSamples = OutputSampleRate * 10;
    private const int TailTrimPaddingSamples = OutputSampleRate / 2;
    private const float SilenceThreshold = 0.0001f;
    private const float HardPeakLimit = 0.98f;
    private const int DefaultPitchBendRangeSemitones = 2;

    public static string RenderToWave(string midiPath, string soundFontPath, string outputPath, BgmToolConfig config, TextWriter log)
    {
        var fullMidiPath = Path.GetFullPath(midiPath);
        var fullSf2Path = Path.GetFullPath(soundFontPath);
        if (!File.Exists(fullMidiPath))
        {
            throw new FileNotFoundException("Input MIDI file was not found.", fullMidiPath);
        }

        if (!File.Exists(fullSf2Path))
        {
            throw new FileNotFoundException("Input SoundFont file was not found.", fullSf2Path);
        }

        var midi = MidiFileParser.Parse(fullMidiPath);
        var soundFont = SoundFontParser.Parse(
            fullSf2Path,
            new SoundFontImportOptions(config.Sf2PreEqStrength, config.Sf2PreLowPassHz, config.Sf2AutoLowPass));

        log.WriteLine($"Source preview input: {Path.GetFileName(fullMidiPath)} + {Path.GetFileName(fullSf2Path)}");
        log.WriteLine($"MIDI preview: {midi.Tracks.Count} track(s), PPQN {midi.Division}");
        log.WriteLine($"SoundFont preview: {soundFont.Presets.Count} preset(s)");

        var rendered = Render(midi, soundFont, (float)config.Sf2Volume, log);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        WaveWriter.WriteStereoPcm16(outputPath, rendered.Left, rendered.Right, OutputSampleRate);
        log.WriteLine($"Wrote MIDI/SF2 source preview: {outputPath}");
        return outputPath;
    }

    private static RenderedWave Render(MidiFile midi, SoundFontFile soundFont, float sf2Volume, TextWriter log)
    {
        var tempoMap = TempoMap.Create(midi.Division, midi.Tracks.SelectMany(static track => track.Events).OfType<MidiTempoEvent>().ToList());
        var renderEvents = BuildRenderEvents(midi, tempoMap);
        var lastEventSample = renderEvents.Count == 0 ? 0 : renderEvents[^1].SamplePosition;
        var maxBufferLength = checked(lastEventSample + TailRenderSamples);
        var left = new float[maxBufferLength];
        var right = new float[maxBufferLength];
        var trackStates = Enumerable.Range(0, 16).Select(_ => new TrackState()).ToArray();
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
                ApplyEvent(renderEvents[eventIndex], soundFont, trackStates, activeVoices, sf2Volume, warningCache, log);
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

        log.WriteLine($"Rendered MIDI/SF2 preview length: {(finalLength / (double)OutputSampleRate).ToString("0.000", CultureInfo.InvariantCulture)} s");
        return new RenderedWave(left, right);
    }

    private static List<RenderEvent> BuildRenderEvents(MidiFile midi, TempoMap tempoMap)
    {
        var renderEvents = new List<RenderEvent>();
        foreach (var track in midi.Tracks)
        {
            foreach (var evt in track.Events)
            {
                var samplePosition = checked((int)tempoMap.TicksToSamples(evt.Tick));
                switch (evt)
                {
                    case MidiProgramChangeEvent program:
                        renderEvents.Add(new RenderEvent(samplePosition, evt.Order, evt.Channel, RenderEventKind.Program, program.Program, 0));
                        break;
                    case MidiControlChangeEvent cc:
                        switch (cc.Controller)
                        {
                            case 0:
                                renderEvents.Add(new RenderEvent(samplePosition, evt.Order, evt.Channel, RenderEventKind.BankMsb, cc.Value, 0));
                                break;
                            case 6:
                                renderEvents.Add(new RenderEvent(samplePosition, evt.Order, evt.Channel, RenderEventKind.DataEntryMsb, cc.Value, 0));
                                break;
                            case 7:
                                renderEvents.Add(new RenderEvent(samplePosition, evt.Order, evt.Channel, RenderEventKind.Volume, cc.Value, 0));
                                break;
                            case 10:
                                renderEvents.Add(new RenderEvent(samplePosition, evt.Order, evt.Channel, RenderEventKind.Pan, cc.Value, 0));
                                break;
                            case 11:
                                renderEvents.Add(new RenderEvent(samplePosition, evt.Order, evt.Channel, RenderEventKind.Expression, cc.Value, 0));
                                break;
                            case 32:
                                renderEvents.Add(new RenderEvent(samplePosition, evt.Order, evt.Channel, RenderEventKind.BankLsb, cc.Value, 0));
                                break;
                            case 100:
                                renderEvents.Add(new RenderEvent(samplePosition, evt.Order, evt.Channel, RenderEventKind.RpnLsb, cc.Value, 0));
                                break;
                            case 101:
                                renderEvents.Add(new RenderEvent(samplePosition, evt.Order, evt.Channel, RenderEventKind.RpnMsb, cc.Value, 0));
                                break;
                        }

                        break;
                    case MidiPitchBendEvent bend:
                        renderEvents.Add(new RenderEvent(samplePosition, evt.Order, evt.Channel, RenderEventKind.PitchBend, bend.Value, 0));
                        break;
                    case MidiNoteOnEvent noteOn:
                        renderEvents.Add(new RenderEvent(samplePosition, evt.Order, evt.Channel, RenderEventKind.NoteOn, noteOn.Key, noteOn.Velocity));
                        break;
                    case MidiNoteOffEvent noteOff:
                        renderEvents.Add(new RenderEvent(samplePosition, evt.Order, evt.Channel, RenderEventKind.NoteOff, noteOff.Key, 0));
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
        SoundFontFile soundFont,
        TrackState[] trackStates,
        List<ActiveVoice> activeVoices,
        float sf2Volume,
        HashSet<string> warningCache,
        TextWriter log)
    {
        if (evt.Channel < 0 || evt.Channel >= trackStates.Length)
        {
            return;
        }

        var state = trackStates[evt.Channel];
        switch (evt.Kind)
        {
            case RenderEventKind.BankMsb:
                state.BankMsb = evt.Value1;
                break;
            case RenderEventKind.BankLsb:
                state.BankLsb = evt.Value1;
                break;
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
            case RenderEventKind.PitchBend:
                state.PitchBend = evt.Value1;
                break;
            case RenderEventKind.RpnMsb:
                state.RpnMsb = evt.Value1;
                break;
            case RenderEventKind.RpnLsb:
                state.RpnLsb = evt.Value1;
                break;
            case RenderEventKind.DataEntryMsb:
                if (state.RpnMsb == 0 && state.RpnLsb == 0)
                {
                    state.PitchBendRangeSemitones = Math.Clamp(evt.Value1, 0, 24);
                }

                break;
            case RenderEventKind.NoteOff:
                for (var i = 0; i < activeVoices.Count; i++)
                {
                    var voice = activeVoices[i];
                    if (voice.Channel == evt.Channel && voice.Key == evt.Value1 && voice.ReleaseSample < 0)
                    {
                        voice.ReleaseSample = evt.SamplePosition;
                    }
                }

                break;
            case RenderEventKind.NoteOn:
                if (state.Program < 0)
                {
                    return;
                }

                var preset = soundFont.FindPreset(state.Bank, state.Program)
                    ?? soundFont.FindPreset(state.BankMsb << 7, state.Program)
                    ?? soundFont.FindPreset(0, state.Program);
                if (preset is null)
                {
                    WarnOnce(
                        warningCache,
                        log,
                        $"preset:{state.Bank}:{state.Program}",
                        $"Skipping source preview note on channel {evt.Channel}: preset bank {state.Bank}, program {state.Program} is missing in the SoundFont.");
                    return;
                }

                var matchingLayers = preset.Regions
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
                        $"region:{preset.Bank}:{preset.Program}:{evt.Value1}",
                        $"Skipping source preview note on channel {evt.Channel}: no matching SF2 region for bank {preset.Bank}, program {preset.Program}, key {evt.Value1}.");
                    return;
                }

                var voiceLayers = new List<ActiveLayer>(matchingLayers.Length);
                foreach (var region in matchingLayers)
                {
                    var effectiveRootNote = region.RegionPitch.ResolveEffectiveRootNoteSemitones(region.SamplePitch);
                    var baseStep = Math.Pow(2.0, (evt.Value1 - effectiveRootNote) / 12.0) * region.SampleRate / OutputSampleRate;
                    if (baseStep <= 0)
                    {
                        continue;
                    }

                    var loop = region.LoopDescriptor.NormalizeToSamples(region.Pcm.Length);
                    voiceLayers.Add(new ActiveLayer(
                        region.Pcm,
                        region.StereoPcm,
                        region.AttackSeconds,
                        region.HoldSeconds,
                        region.DecaySeconds,
                        region.SustainLevel,
                        region.ReleaseSeconds,
                        loop,
                        baseStep,
                        (evt.Value2 / 127f) * region.Volume * sf2Volume,
                        (region.Pan + 1f) * 0.5f));
                }

                if (voiceLayers.Count > 0)
                {
                    activeVoices.Add(new ActiveVoice(evt.Channel, evt.Value1, evt.SamplePosition, voiceLayers));
                }

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
            RenderVoice(segmentStart, segmentLength, voice, trackStates[voice.Channel], left, right);
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
        var pitchBendRatio = Math.Pow(2.0, trackState.PitchBendSemitones / 12.0);

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

                if (!TryReadLayer(layer, out var layerLeft, out var layerRight))
                {
                    layer.IsFinished = true;
                    continue;
                }

                anyLayerAlive = true;
                var combinedPan = Math.Clamp(trackPan + (layer.Pan - 0.5f), 0f, 1f);
                GetEqualPowerGains(combinedPan, out var panLeft, out var panRight);

                var value = layer.BaseGain * trackGain * env;
                mixLeft += layerLeft * value * panLeft;
                mixRight += layerRight * value * panRight;
                layer.Position += layer.BaseStep * pitchBendRatio;
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

    private static bool TryReadLayer(ActiveLayer layer, out float left, out float right)
    {
        var position = layer.Position;
        if (!TryReadSample(layer.LeftPcm, ref position, layer.LoopDescriptor, out left))
        {
            right = 0f;
            return false;
        }

        layer.Position = position;

        if (layer.RightPcm is null)
        {
            right = left;
            return true;
        }

        var stereoPosition = layer.Position;
        if (!TryReadSample(layer.RightPcm, ref stereoPosition, layer.LoopDescriptor, out right))
        {
            right = left;
        }

        return true;
    }

    private static bool TryReadSample(short[] pcm, ref double position, LoopDescriptor loopDescriptor, out float sample)
    {
        if (pcm.Length == 0)
        {
            sample = 0f;
            return false;
        }

        var loop = loopDescriptor.NormalizeToSamples(pcm.Length);
        var sampleLength = pcm.Length;
        while (position >= sampleLength)
        {
            if (!loop.Looping)
            {
                sample = 0f;
                return false;
            }

            var loopStart = loop.ResolveStartSamples(sampleLength);
            var loopLength = Math.Max(1, loop.ResolveLengthSamples(sampleLength));
            position = loopStart + ((position - loopStart) % loopLength);
        }

        var index = (int)position;
        if (index < 0 || index >= sampleLength)
        {
            sample = 0f;
            return false;
        }

        var nextIndex = index + 1;
        if (nextIndex >= sampleLength)
        {
            if (loop.Looping)
            {
                nextIndex = loop.ResolveStartSamples(sampleLength);
            }
            else
            {
                nextIndex = sampleLength - 1;
            }
        }

        var fraction = (float)(position - index);
        var current = pcm[index] / 32768f;
        var next = pcm[nextIndex] / 32768f;
        sample = current + ((next - current) * fraction);
        return true;
    }

    private static float GetEnvelope(ActiveVoice voice, int samplePosition)
    {
        var amplitude = GetAmplitudeBeforeRelease(voice, samplePosition);
        if (voice.ReleaseSample < 0 || samplePosition < voice.ReleaseSample)
        {
            return amplitude;
        }

        var releaseAmplitude = GetAmplitudeBeforeRelease(voice, voice.ReleaseSample);
        var maxReleaseSeconds = 0.0;
        foreach (var layer in voice.Layers)
        {
            maxReleaseSeconds = Math.Max(maxReleaseSeconds, layer.ReleaseSeconds);
        }

        var releaseSamples = Math.Max(1, (int)Math.Round(maxReleaseSeconds * OutputSampleRate, MidpointRounding.AwayFromZero));
        var releaseProgress = (samplePosition - voice.ReleaseSample) / (float)releaseSamples;
        if (releaseProgress >= 1f)
        {
            return 0f;
        }

        return releaseAmplitude * (1f - releaseProgress);
    }

    private static float GetAmplitudeBeforeRelease(ActiveVoice voice, int samplePosition)
    {
        var maxAmplitude = 0f;
        foreach (var layer in voice.Layers)
        {
            var localAmplitude = GetAmplitudeBeforeRelease(layer, samplePosition - voice.StartSample);
            if (localAmplitude > maxAmplitude)
            {
                maxAmplitude = localAmplitude;
            }
        }

        return maxAmplitude;
    }

    private static float GetAmplitudeBeforeRelease(ActiveLayer layer, int sampleOffset)
    {
        if (sampleOffset < 0)
        {
            return 0f;
        }

        var attackSamples = Math.Max(0, (int)Math.Round(layer.AttackSeconds * OutputSampleRate, MidpointRounding.AwayFromZero));
        var holdSamples = Math.Max(0, (int)Math.Round(layer.HoldSeconds * OutputSampleRate, MidpointRounding.AwayFromZero));
        var decaySamples = Math.Max(0, (int)Math.Round(layer.DecaySeconds * OutputSampleRate, MidpointRounding.AwayFromZero));
        var sustainLevel = Math.Clamp(layer.SustainLevel, 0f, 1f);

        if (attackSamples > 0 && sampleOffset < attackSamples)
        {
            return sampleOffset / (float)attackSamples;
        }

        sampleOffset -= attackSamples;
        if (sampleOffset < holdSamples)
        {
            return 1f;
        }

        sampleOffset -= holdSamples;
        if (decaySamples > 0 && sampleOffset < decaySamples)
        {
            var decayProgress = sampleOffset / (float)decaySamples;
            return 1f - ((1f - sustainLevel) * decayProgress);
        }

        return sustainLevel;
    }

    private static void GetEqualPowerGains(float pan, out float left, out float right)
    {
        var angle = pan * (MathF.PI / 2f);
        left = MathF.Cos(angle);
        right = MathF.Sin(angle);
    }

    private static void ApplySafetyHeadroom(float[] left, float[] right)
    {
        var peak = 0f;
        for (var i = 0; i < left.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(left[i]));
            peak = Math.Max(peak, Math.Abs(right[i]));
        }

        if (peak <= HardPeakLimit || peak <= 0f)
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

    private static int TrimLength(float[] left, float[] right, int fallbackLength)
    {
        for (var i = Math.Min(left.Length, right.Length) - 1; i >= 0; i--)
        {
            if (Math.Abs(left[i]) > SilenceThreshold || Math.Abs(right[i]) > SilenceThreshold)
            {
                return i + 1;
            }
        }

        return Math.Max(1, fallbackLength);
    }

    private static void WarnOnce(HashSet<string> warningCache, TextWriter log, string key, string message)
    {
        if (warningCache.Add(key))
        {
            log.WriteLine(message);
        }
    }

    private sealed record RenderedWave(float[] Left, float[] Right);

    private sealed class ActiveVoice
    {
        public ActiveVoice(int channel, int key, int startSample, List<ActiveLayer> layers)
        {
            Channel = channel;
            Key = key;
            StartSample = startSample;
            Layers = layers;
        }

        public int Channel { get; }
        public int Key { get; }
        public int StartSample { get; }
        public List<ActiveLayer> Layers { get; }
        public int ReleaseSample { get; set; } = -1;
        public bool IsDead { get; set; }
    }

    private sealed class ActiveLayer
    {
        public ActiveLayer(
            short[] leftPcm,
            short[]? rightPcm,
            double attackSeconds,
            double holdSeconds,
            double decaySeconds,
            float sustainLevel,
            double releaseSeconds,
            LoopDescriptor loopDescriptor,
            double baseStep,
            float baseGain,
            float pan)
        {
            LeftPcm = leftPcm;
            RightPcm = rightPcm;
            AttackSeconds = attackSeconds;
            HoldSeconds = holdSeconds;
            DecaySeconds = decaySeconds;
            SustainLevel = sustainLevel;
            ReleaseSeconds = releaseSeconds;
            LoopDescriptor = loopDescriptor;
            BaseStep = baseStep;
            BaseGain = baseGain;
            Pan = pan;
        }

        public short[] LeftPcm { get; }
        public short[]? RightPcm { get; }
        public double AttackSeconds { get; }
        public double HoldSeconds { get; }
        public double DecaySeconds { get; }
        public float SustainLevel { get; }
        public double ReleaseSeconds { get; }
        public LoopDescriptor LoopDescriptor { get; }
        public double BaseStep { get; }
        public float BaseGain { get; }
        public float Pan { get; }
        public double Position { get; set; }
        public bool IsFinished { get; set; }
    }

    private sealed class TrackState
    {
        public int BankMsb { get; set; }
        public int BankLsb { get; set; }
        public int Program { get; set; }
        public int Volume { get; set; } = 100;
        public int Expression { get; set; } = 127;
        public int Pan { get; set; } = 64;
        public int PitchBend { get; set; }
        public int PitchBendRangeSemitones { get; set; } = DefaultPitchBendRangeSemitones;
        public int RpnMsb { get; set; } = 127;
        public int RpnLsb { get; set; } = 127;

        public int Bank => (BankMsb << 7) | BankLsb;

        public float Gain => (Volume / 127f) * (Expression / 127f);

        public float PanNormalized => Math.Clamp(Pan / 127f, 0f, 1f);

        public double PitchBendSemitones => (PitchBend / 8192.0) * PitchBendRangeSemitones;
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

        public static TempoMap Create(int ppqn, IReadOnlyList<MidiTempoEvent> tempoEvents)
        {
            var ordered = tempoEvents
                .OrderBy(static evt => evt.Tick)
                .ThenBy(static evt => evt.Order)
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
            var point = Points[tempoIndex];
            var seconds = point.SecondsAtTick + ((tick - point.Tick) * 60.0 / (Ppqn * point.Bpm));
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

    private sealed record RenderEvent(int SamplePosition, int Order, int Channel, RenderEventKind Kind, int Value1, int Value2);

    private enum RenderEventKind
    {
        BankMsb,
        BankLsb,
        Program,
        Volume,
        Expression,
        Pan,
        PitchBend,
        RpnMsb,
        RpnLsb,
        DataEntryMsb,
        NoteOn,
        NoteOff,
    }
}
