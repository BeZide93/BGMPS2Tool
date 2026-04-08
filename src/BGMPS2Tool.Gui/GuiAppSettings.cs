using System.Text.Json;

namespace BGMPS2Tool.Gui;

internal sealed record GuiAppSettings(
    string TemplateRootDirectory,
    string LastMidiPath,
    string LastSf2Path,
    string LastWavePath,
    string LastOutputBgmPath,
    string LastOutputWdPath)
{
    public static GuiAppSettings Default { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
}

internal static class GuiAppSettingsStore
{
    public static GuiAppSettings Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return GuiAppSettings.Default;
        }

        try
        {
            return JsonSerializer.Deserialize<GuiAppSettings>(File.ReadAllText(fullPath)) ?? GuiAppSettings.Default;
        }
        catch
        {
            return GuiAppSettings.Default;
        }
    }

    public static void Save(string path, GuiAppSettings settings)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
