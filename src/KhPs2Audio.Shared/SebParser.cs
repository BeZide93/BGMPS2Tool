namespace KhPs2Audio.Shared;

public sealed record SebGroupInfo(int Index, int Offset, int Size);

public sealed record SebFileInfo(
    string FilePath,
    int FileSize,
    int DeclaredSize,
    int TopLevelGroupCount,
    IReadOnlyList<SebGroupInfo> Groups,
    bool HasEmbeddedAudioMarker);

public static class SebParser
{
    public static SebFileInfo Parse(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var data = File.ReadAllBytes(fullPath);

        if (data.Length < 0x1C)
        {
            throw new InvalidDataException("File is too small to be a valid .seb.");
        }

        var magic = BinaryHelpers.ReadAscii(data, 0x00, 8);
        if (!string.Equals(magic, "SeBlock\0", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unexpected magic '{magic}'.");
        }

        var declaredSize = checked((int)BinaryHelpers.ReadUInt32LE(data, 0x0C));
        var topLevelGroupCount = checked((int)BinaryHelpers.ReadUInt32LE(data, 0x18));
        var tableStart = 0x1C;
        var tableEnd = tableStart + (topLevelGroupCount * sizeof(uint));

        if (tableEnd > data.Length)
        {
            throw new InvalidDataException("Top-level group table exceeds file size.");
        }

        var offsets = new List<int>(topLevelGroupCount);
        for (var i = 0; i < topLevelGroupCount; i++)
        {
            offsets.Add(checked((int)BinaryHelpers.ReadUInt32LE(data, tableStart + (i * sizeof(uint)))));
        }

        var groups = new List<SebGroupInfo>(offsets.Count);
        for (var i = 0; i < offsets.Count; i++)
        {
            var current = offsets[i];
            if (current < 0 || current >= data.Length)
            {
                continue;
            }

            var next = data.Length;
            for (var j = i + 1; j < offsets.Count; j++)
            {
                if (offsets[j] > current && offsets[j] <= data.Length)
                {
                    next = offsets[j];
                    break;
                }
            }

            groups.Add(new SebGroupInfo(i + 1, current, next - current));
        }

        var hasEmbeddedAudioMarker =
            BinaryHelpers.ContainsAscii(data, "RIFF") ||
            BinaryHelpers.ContainsAscii(data, "OggS") ||
            BinaryHelpers.ContainsAscii(data, "VAGp");

        return new SebFileInfo(
            fullPath,
            data.Length,
            declaredSize,
            topLevelGroupCount,
            groups,
            hasEmbeddedAudioMarker);
    }
}
