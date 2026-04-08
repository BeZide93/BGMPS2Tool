namespace BGMPS2Tool.Gui;

internal sealed record TrackMetadataEntry(int TrackNumber, string Name, string Description);
internal sealed record TrackTableModel(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

internal static class TrackMetadataTextLoader
{
    public static IReadOnlyDictionary<int, TrackMetadataEntry> Load(string trackListPath)
    {
        var result = new Dictionary<int, TrackMetadataEntry>();
        var table = LoadTable(trackListPath);
        foreach (var row in table.Rows)
        {
            if (row.Count < 3)
            {
                continue;
            }

            if (!int.TryParse(row[0].Trim(), out var trackNumber))
            {
                continue;
            }

            var name = row[1].Trim();
            var description = row[2].Trim();
            result[trackNumber] = new TrackMetadataEntry(trackNumber, name, description);
        }

        return result;
    }

    public static TrackTableModel LoadTable(string trackListPath)
    {
        var fullPath = Path.GetFullPath(trackListPath);
        if (!File.Exists(fullPath))
        {
            return new TrackTableModel(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
        }

        var rawLines = File.ReadLines(fullPath)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        if (rawLines.Count == 0)
        {
            return new TrackTableModel(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
        }

        var headers = rawLines[0].Split('\t')
            .Select(static part => part.Trim())
            .ToArray();
        var rows = new List<IReadOnlyList<string>>();
        for (var i = 1; i < rawLines.Count; i++)
        {
            var values = rawLines[i].Split('\t')
                .Select(static part => part.Trim())
                .ToArray();
            if (values.Length < headers.Length)
            {
                Array.Resize(ref values, headers.Length);
            }

            for (var column = 0; column < values.Length; column++)
            {
                values[column] ??= string.Empty;
            }

            rows.Add(values);
        }

        return new TrackTableModel(headers, rows);
    }
}
