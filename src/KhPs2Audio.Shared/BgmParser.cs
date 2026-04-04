namespace KhPs2Audio.Shared;

public sealed record BgmFileInfo(
    string FilePath,
    int FileSize,
    int DeclaredSize,
    ushort SequenceId,
    ushort BankId,
    ushort HeaderWord08,
    ushort HeaderWord0A,
    ushort HeaderWord0C,
    ushort HeaderWord0E,
    uint HeaderWord20,
    bool HasEmbeddedAudioMarker);

public static class BgmParser
{
    public static BgmFileInfo Parse(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var data = File.ReadAllBytes(fullPath);

        if (data.Length < 0x24)
        {
            throw new InvalidDataException("File is too small to be a valid .bgm.");
        }

        var magic = BinaryHelpers.ReadAscii(data, 0x00, 4);
        if (!string.Equals(magic, "BGM ", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unexpected magic '{magic}'.");
        }

        var hasEmbeddedAudioMarker =
            BinaryHelpers.ContainsAscii(data, "RIFF") ||
            BinaryHelpers.ContainsAscii(data, "OggS") ||
            BinaryHelpers.ContainsAscii(data, "VAGp");

        return new BgmFileInfo(
            fullPath,
            data.Length,
            checked((int)BinaryHelpers.ReadUInt32LE(data, 0x10)),
            BinaryHelpers.ReadUInt16LE(data, 0x04),
            BinaryHelpers.ReadUInt16LE(data, 0x06),
            BinaryHelpers.ReadUInt16LE(data, 0x08),
            BinaryHelpers.ReadUInt16LE(data, 0x0A),
            BinaryHelpers.ReadUInt16LE(data, 0x0C),
            BinaryHelpers.ReadUInt16LE(data, 0x0E),
            BinaryHelpers.ReadUInt32LE(data, 0x20),
            hasEmbeddedAudioMarker);
    }
}
