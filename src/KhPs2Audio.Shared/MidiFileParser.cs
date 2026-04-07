namespace KhPs2Audio.Shared;

internal static class MidiFileParser
{
    public static MidiFile Parse(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var data = File.ReadAllBytes(fullPath);
        if (data.Length < 14)
        {
            throw new InvalidDataException("MIDI file is too small.");
        }

        if (!string.Equals(BinaryHelpers.ReadAscii(data, 0, 4), "MThd", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unexpected MIDI header.");
        }

        var headerLength = checked((int)ReadUInt32BE(data, 4));
        if (headerLength < 6 || data.Length < 8 + headerLength)
        {
            throw new InvalidDataException("Invalid MIDI header length.");
        }

        var format = ReadUInt16BE(data, 8);
        var trackCount = ReadUInt16BE(data, 10);
        var division = ReadUInt16BE(data, 12);
        if ((division & 0x8000) != 0)
        {
            throw new InvalidDataException("SMPTE MIDI timing is not supported. Please use PPQN-based MIDI files.");
        }

        var tracks = new List<MidiTrack>(trackCount);
        var offset = 8 + headerLength;
        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            if (offset + 8 > data.Length)
            {
                throw new InvalidDataException("Unexpected end of file while reading MIDI tracks.");
            }

            if (!string.Equals(BinaryHelpers.ReadAscii(data, offset, 4), "MTrk", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unexpected MIDI track header at offset 0x{offset:X}.");
            }

            var trackLength = checked((int)ReadUInt32BE(data, offset + 4));
            var trackStart = offset + 8;
            var trackEnd = trackStart + trackLength;
            if (trackEnd > data.Length)
            {
                throw new InvalidDataException("A MIDI track exceeds the file length.");
            }

            tracks.Add(ParseTrack(trackIndex, data, trackStart, trackEnd));
            offset = trackEnd;
        }

        return new MidiFile(fullPath, format, division, tracks);
    }

    private static MidiTrack ParseTrack(int trackIndex, byte[] data, int start, int end)
    {
        var events = new List<MidiEvent>();
        string? name = null;
        long tick = 0;
        var offset = start;
        var order = 0;
        byte runningStatus = 0;

        while (offset < end)
        {
            tick += ReadVarLen(data, ref offset, end);
            if (offset >= end)
            {
                break;
            }

            var status = data[offset];
            if (status < 0x80)
            {
                if (runningStatus == 0)
                {
                    throw new InvalidDataException($"Running status without a previous status byte in MIDI track {trackIndex}.");
                }

                status = runningStatus;
            }
            else
            {
                offset++;
                runningStatus = status < 0xF0 ? status : (byte)0;
            }

            if (status == 0xFF)
            {
                if (offset >= end)
                {
                    break;
                }

                var metaType = data[offset++];
                var metaLength = ReadVarLen(data, ref offset, end);
                if (offset + metaLength > end)
                {
                    throw new InvalidDataException($"A MIDI meta event exceeds the containing track in track {trackIndex}.");
                }

                switch (metaType)
                {
                    case 0x03:
                        name = System.Text.Encoding.ASCII.GetString(data, offset, metaLength);
                        break;
                    case 0x01:
                    case 0x05:
                    case 0x06:
                    case 0x07:
                    {
                        var text = System.Text.Encoding.ASCII.GetString(data, offset, metaLength);
                        events.Add(new MidiMetaTextEvent(tick, order++, metaType, text));
                        break;
                    }
                    case 0x2F:
                        return new MidiTrack(trackIndex, name, events, tick);
                    case 0x51 when metaLength == 3:
                    {
                        var microsecondsPerQuarter =
                            (data[offset] << 16) |
                            (data[offset + 1] << 8) |
                            data[offset + 2];
                        if (microsecondsPerQuarter > 0)
                        {
                            var bpm = Math.Clamp(
                                (int)Math.Round(60_000_000.0 / microsecondsPerQuarter, MidpointRounding.AwayFromZero),
                                1,
                                255);
                            events.Add(new MidiTempoEvent(tick, order++, bpm));
                        }

                        break;
                    }
                }

                offset += metaLength;
                continue;
            }

            if (status is 0xF0 or 0xF7)
            {
                var sysexLength = ReadVarLen(data, ref offset, end);
                offset = Math.Min(end, offset + sysexLength);
                continue;
            }

            var channel = status & 0x0F;
            switch (status & 0xF0)
            {
                case 0x80:
                {
                    EnsureBytesAvailable(offset, end, 2, trackIndex);
                    var key = data[offset++];
                    var velocity = data[offset++];
                    events.Add(new MidiNoteOffEvent(tick, order++, channel, key, velocity));
                    break;
                }
                case 0x90:
                {
                    EnsureBytesAvailable(offset, end, 2, trackIndex);
                    var key = data[offset++];
                    var velocity = data[offset++];
                    if (velocity == 0)
                    {
                        events.Add(new MidiNoteOffEvent(tick, order++, channel, key, 0));
                    }
                    else
                    {
                        events.Add(new MidiNoteOnEvent(tick, order++, channel, key, velocity));
                    }

                    break;
                }
                case 0xA0:
                    EnsureBytesAvailable(offset, end, 2, trackIndex);
                    offset += 2;
                    break;
                case 0xB0:
                {
                    EnsureBytesAvailable(offset, end, 2, trackIndex);
                    var controller = data[offset++];
                    var value = data[offset++];
                    events.Add(new MidiControlChangeEvent(tick, order++, channel, controller, value));
                    break;
                }
                case 0xC0:
                {
                    EnsureBytesAvailable(offset, end, 1, trackIndex);
                    var program = data[offset++];
                    events.Add(new MidiProgramChangeEvent(tick, order++, channel, program));
                    break;
                }
                case 0xD0:
                    EnsureBytesAvailable(offset, end, 1, trackIndex);
                    offset++;
                    break;
                case 0xE0:
                {
                    EnsureBytesAvailable(offset, end, 2, trackIndex);
                    var lsb = data[offset++];
                    var msb = data[offset++];
                    var value = ((msb << 7) | lsb) - 8192;
                    events.Add(new MidiPitchBendEvent(tick, order++, channel, value));
                    break;
                }
                default:
                    throw new InvalidDataException($"Unsupported MIDI status byte 0x{status:X2} in track {trackIndex}.");
            }
        }

        return new MidiTrack(trackIndex, name, events, tick);
    }

    private static ushort ReadUInt16BE(byte[] data, int offset)
    {
        if (offset < 0 || offset + 2 > data.Length)
        {
            throw new InvalidDataException("Unexpected end of MIDI data.");
        }

        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static uint ReadUInt32BE(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
        {
            throw new InvalidDataException("Unexpected end of MIDI data.");
        }

        return
            ((uint)data[offset] << 24) |
            ((uint)data[offset + 1] << 16) |
            ((uint)data[offset + 2] << 8) |
            data[offset + 3];
    }

    private static int ReadVarLen(byte[] data, ref int offset, int end)
    {
        var value = 0;
        while (offset < end)
        {
            var current = data[offset++];
            value = (value << 7) | (current & 0x7F);
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("Unexpected end of MIDI data while reading a variable-length quantity.");
    }

    private static void EnsureBytesAvailable(int offset, int end, int count, int trackIndex)
    {
        if (offset + count > end)
        {
            throw new InvalidDataException($"Unexpected end of MIDI track {trackIndex}.");
        }
    }
}

internal sealed record MidiFile(
    string FilePath,
    int Format,
    int Division,
    List<MidiTrack> Tracks);

internal sealed record MidiTrack(
    int Index,
    string? Name,
    List<MidiEvent> Events,
    long EndTick);

internal abstract record MidiEvent(long Tick, int Order, int Channel);

internal sealed record MidiTempoEvent(long Tick, int Order, int Bpm) : MidiEvent(Tick, Order, -1);

internal sealed record MidiNoteOnEvent(long Tick, int Order, int Channel, int Key, int Velocity) : MidiEvent(Tick, Order, Channel);

internal sealed record MidiNoteOffEvent(long Tick, int Order, int Channel, int Key, int Velocity) : MidiEvent(Tick, Order, Channel);

internal sealed record MidiControlChangeEvent(long Tick, int Order, int Channel, int Controller, int Value) : MidiEvent(Tick, Order, Channel);

internal sealed record MidiProgramChangeEvent(long Tick, int Order, int Channel, int Program) : MidiEvent(Tick, Order, Channel);

internal sealed record MidiPitchBendEvent(long Tick, int Order, int Channel, int Value) : MidiEvent(Tick, Order, Channel);

internal sealed record MidiMetaTextEvent(long Tick, int Order, int MetaType, string Text) : MidiEvent(Tick, Order, -1);
