using System.Text;

namespace KhPs2Audio.Shared;

public static class BinaryHelpers
{
    public static ushort ReadUInt16LE(byte[] data, int offset)
    {
        EnsureAvailable(data, offset, sizeof(ushort));
        return BitConverter.ToUInt16(data, offset);
    }

    public static uint ReadUInt32LE(byte[] data, int offset)
    {
        EnsureAvailable(data, offset, sizeof(uint));
        return BitConverter.ToUInt32(data, offset);
    }

    public static sbyte ReadSByte(byte[] data, int offset)
    {
        EnsureAvailable(data, offset, sizeof(byte));
        return unchecked((sbyte)data[offset]);
    }

    public static void WriteUInt32LE(byte[] data, int offset, uint value)
    {
        EnsureAvailable(data, offset, sizeof(uint));
        BitConverter.GetBytes(value).CopyTo(data, offset);
    }

    public static void WriteUInt16LE(byte[] data, int offset, ushort value)
    {
        EnsureAvailable(data, offset, sizeof(ushort));
        BitConverter.GetBytes(value).CopyTo(data, offset);
    }

    public static string ReadAscii(byte[] data, int offset, int count)
    {
        EnsureAvailable(data, offset, count);
        return Encoding.ASCII.GetString(data, offset, count);
    }

    public static bool ContainsAscii(byte[] data, string marker)
    {
        var markerBytes = Encoding.ASCII.GetBytes(marker);
        if (markerBytes.Length == 0 || markerBytes.Length > data.Length)
        {
            return false;
        }

        for (var i = 0; i <= data.Length - markerBytes.Length; i++)
        {
            var match = true;
            for (var j = 0; j < markerBytes.Length; j++)
            {
                if (data[i + j] != markerBytes[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureAvailable(byte[] data, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > data.Length)
        {
            throw new InvalidDataException($"Offset 0x{offset:X} with size {count} exceeds file length {data.Length}.");
        }
    }
}
