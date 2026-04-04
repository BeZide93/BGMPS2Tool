using System.Diagnostics;

namespace KhPs2Audio.Shared;

public sealed record ScdExtractionPlan(string InputFile, string? SiblingScdFile, string? ScdInfoExe);

public static class ScdBridge
{
    public static string GetSiblingScdPath(string inputFile)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(inputFile))
            ?? throw new InvalidOperationException("Input file directory could not be resolved.");
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(inputFile);
        return Path.Combine(directory, $"{fileNameWithoutExtension}.win32.scd");
    }

    public static ScdExtractionPlan BuildPlan(string inputFile)
    {
        var siblingScd = GetSiblingScdPath(inputFile);
        if (!File.Exists(siblingScd))
        {
            siblingScd = null;
        }

        var scdInfoExe = FindScdInfoExe(inputFile);
        return new ScdExtractionPlan(Path.GetFullPath(inputFile), siblingScd, scdInfoExe);
    }

    public static int ExtractEquivalentAudio(string inputFile, TextWriter output)
    {
        var plan = BuildPlan(inputFile);
        if (plan.SiblingScdFile is null)
        {
            output.WriteLine("No sibling .win32.scd file was found.");
            output.WriteLine("Direct WAV extraction from the standalone PS2 file is not implemented, because the PS2 asset is sequence/event data rather than self-contained audio.");
            return 2;
        }

        if (plan.ScdInfoExe is null)
        {
            output.WriteLine("Could not locate SCDInfo.exe.");
            output.WriteLine("Place SCDInfo.exe next to the input file or next to this tool.");
            return 3;
        }

        var inputDirectory = Path.GetDirectoryName(plan.InputFile)!;
        var stem = Path.GetFileNameWithoutExtension(plan.InputFile);
        var outputDirectory = Path.Combine(inputDirectory, stem);
        var stagingRoot = Path.Combine(inputDirectory, ".scd_extract_temp");
        var stagingDirectory = Path.Combine(stagingRoot, $"{stem}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        var stagedScdFile = Path.Combine(stagingDirectory, Path.GetFileName(plan.SiblingScdFile));
        File.Copy(plan.SiblingScdFile, stagedScdFile, overwrite: true);

        var stagedScdInfoExe = StageScdInfo(plan.ScdInfoExe, stagingDirectory);

        output.WriteLine($"Using sibling Steam file: {plan.SiblingScdFile}");
        output.WriteLine($"Using SCDInfo executable: {plan.ScdInfoExe}");
        output.WriteLine($"Staging directory: {stagingDirectory}");

        var startInfo = new ProcessStartInfo
        {
            FileName = stagedScdInfoExe,
            WorkingDirectory = stagingDirectory,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("extract");
        startInfo.ArgumentList.Add(stagedScdFile);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            output.WriteLine("Failed to start SCDInfo.exe.");
            return 4;
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            output.WriteLine($"SCDInfo returned exit code {process.ExitCode}.");
            return process.ExitCode;
        }

        CollectOutputs(stagingDirectory, outputDirectory, stem, Path.GetFileName(stagedScdFile));
        ConvertOggOutputsToWav(outputDirectory, output);
        TryDeleteDirectory(stagingDirectory);
        TryDeleteDirectoryIfEmpty(stagingRoot);

        output.WriteLine($"Collected WAV output in: {outputDirectory}");
        output.WriteLine("SCD extraction completed.");
        return 0;
    }

    private static string StageScdInfo(string scdInfoExe, string stagingDirectory)
    {
        var sourceDirectory = Path.GetDirectoryName(scdInfoExe)!;
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "SCDInfo*", SearchOption.TopDirectoryOnly))
        {
            var destinationFile = Path.Combine(stagingDirectory, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destinationFile, overwrite: true);
        }

        return Path.Combine(stagingDirectory, Path.GetFileName(scdInfoExe));
    }

    private static void CollectOutputs(string stagingDirectory, string outputDirectory, string stem, string stagedScdFileName)
    {
        Directory.CreateDirectory(outputDirectory);

        var sameNameDirectory = Path.Combine(stagingDirectory, stem);
        if (Directory.Exists(sameNameDirectory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(sameNameDirectory))
            {
                MoveEntryInto(entry, outputDirectory);
            }
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(stagingDirectory))
        {
            var name = Path.GetFileName(entry);
            if (string.Equals(entry, sameNameDirectory, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, stagedScdFileName, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("SCDInfo", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            MoveEntryInto(entry, outputDirectory);
        }
    }

    private static void ConvertOggOutputsToWav(string outputDirectory, TextWriter output)
    {
        var ffmpegPath = FindExecutableOnPath("ffmpeg.exe");
        if (ffmpegPath is null)
        {
            if (Directory.EnumerateFiles(outputDirectory, "*.ogg", SearchOption.AllDirectories).Any())
            {
                output.WriteLine("ffmpeg.exe was not found on PATH, so .ogg outputs could not be converted to .wav.");
            }

            return;
        }

        foreach (var oggFile in Directory.EnumerateFiles(outputDirectory, "*.ogg", SearchOption.AllDirectories))
        {
            var wavFile = Path.ChangeExtension(oggFile, ".wav");
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                WorkingDirectory = Path.GetDirectoryName(oggFile)!,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(oggFile);
            startInfo.ArgumentList.Add(wavFile);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                output.WriteLine($"Failed to start ffmpeg for {oggFile}.");
                continue;
            }

            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                output.WriteLine($"Converted to WAV: {wavFile}");
            }
            else
            {
                output.WriteLine($"ffmpeg failed for {oggFile} with exit code {process.ExitCode}.");
            }
        }
    }

    private static void MoveEntryInto(string sourceEntry, string outputDirectory)
    {
        var destination = Path.Combine(outputDirectory, Path.GetFileName(sourceEntry));
        if (File.Exists(sourceEntry))
        {
            File.Copy(sourceEntry, destination, overwrite: true);
            return;
        }

        if (Directory.Exists(sourceEntry))
        {
            var destinationDirectory = Path.Combine(outputDirectory, Path.GetFileName(sourceEntry));
            CopyDirectory(sourceEntry, destinationDirectory);
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationFile = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path, recursive: false);
        }
    }

    private static string? FindExecutableOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static string? FindScdInfoExe(string inputFile)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in EnumerateCandidateDirectories(inputFile))
        {
            if (!seen.Add(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, "SCDInfo.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidateDirectories(string inputFile)
    {
        yield return Path.GetDirectoryName(Path.GetFullPath(inputFile))!;
        yield return Environment.CurrentDirectory;

        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            yield return current;
            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }
    }
}
