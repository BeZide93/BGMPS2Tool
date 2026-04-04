namespace KhPs2Audio.Shared;

public sealed record WdFileInfo(
    string FilePath,
    string Magic,
    int FileSize,
    int DeclaredSize,
    int HeaderCount08,
    int HeaderCount0C);

public static class WdParser
{
    public static WdFileInfo Parse(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var data = File.ReadAllBytes(fullPath);
        if (data.Length < 0x10)
        {
            throw new InvalidDataException("File is too small to be a valid .wd.");
        }

        var magic = BinaryHelpers.ReadAscii(data, 0x00, 4);
        if (!magic.StartsWith("WD", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unexpected magic '{magic}'.");
        }

        return new WdFileInfo(
            fullPath,
            magic,
            data.Length,
            checked((int)BinaryHelpers.ReadUInt32LE(data, 0x04)),
            checked((int)BinaryHelpers.ReadUInt32LE(data, 0x08)),
            checked((int)BinaryHelpers.ReadUInt32LE(data, 0x0C)));
    }
}

public static class WdLocator
{
    public static string? FindForBgm(BgmFileInfo info)
    {
        var directory = Path.GetDirectoryName(info.FilePath)!;
        var candidate = Path.Combine(directory, $"wave{info.BankId:D4}.wd");
        return File.Exists(candidate) ? candidate : null;
    }

    public static string? FindForSeb(string sebFilePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(sebFilePath);
        var digits = new string(baseName.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        if (string.IsNullOrEmpty(digits) || !int.TryParse(digits, out var numericId))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(sebFilePath))!;
        var candidate = Path.Combine(directory, $"wave{numericId:D4}.wd");
        return File.Exists(candidate) ? candidate : null;
    }
}
