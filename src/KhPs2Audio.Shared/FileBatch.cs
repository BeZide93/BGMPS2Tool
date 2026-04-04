namespace KhPs2Audio.Shared;

public static class FileBatch
{
    public static IReadOnlyList<string> Expand(string path, string expectedExtension)
    {
        if (File.Exists(path))
        {
            ValidateExtension(path, expectedExtension);
            return new[] { Path.GetFullPath(path) };
        }

        if (Directory.Exists(path))
        {
            var files = Directory
                .EnumerateFiles(path, $"*{expectedExtension}", SearchOption.TopDirectoryOnly)
                .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return files;
        }

        throw new FileNotFoundException($"Path not found: {path}");
    }

    private static void ValidateExtension(string path, string expectedExtension)
    {
        var actualExtension = Path.GetExtension(path);
        if (!actualExtension.Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Expected a *{expectedExtension} file but got {actualExtension}.");
        }
    }
}
