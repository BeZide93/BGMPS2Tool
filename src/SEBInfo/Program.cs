using KhPs2Audio.Shared;

return Run(args);

static int Run(string[] args)
{
    if (args.Length < 2 || args.Length > 3)
    {
        PrintUsage();
        return 1;
    }

    var command = args[0].ToLowerInvariant();
    if (command is not ("info" or "extract" or "render" or "inject" or "bankdump" or "bankinject" or "replaceblizzard"))
    {
        PrintUsage();
        return 1;
    }

    var needsSampleDirectory = command is "bankinject" or "inject" or "replaceblizzard";
    if ((!needsSampleDirectory && args.Length != 2) || (needsSampleDirectory && args.Length != 3))
    {
        PrintUsage();
        return 1;
    }

    IReadOnlyList<string> files;
    try
    {
        files = FileBatch.Expand(args[1], ".seb");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    if (files.Count == 0)
    {
        Console.Error.WriteLine("No .seb files found.");
        return 1;
    }

    var exitCode = 0;
    for (var i = 0; i < files.Count; i++)
    {
        var file = files[i];
        try
        {
            var info = SebParser.Parse(file);
            PrintInfo(info);

            if (command == "extract")
            {
                var currentExitCode = ScdBridge.ExtractEquivalentAudio(file, Console.Out);
                if (currentExitCode != 0 && exitCode == 0)
                {
                    exitCode = currentExitCode;
                }
            }
            else if (command == "render")
            {
                var outputPath = SebNativeRenderer.RenderToWaveDirectory(file, Console.Out);
                Console.WriteLine($"Native SEB WAVs: {outputPath}");
            }
            else if (command == "inject")
            {
                var outputPath = SebNativeRenderer.InjectFromWaveDirectory(file, args[2], Console.Out);
                Console.WriteLine($"Rebuilt PS2 pair: {outputPath}");
            }
            else if (command == "bankdump")
            {
                var outputPath = WdSampleTool.ExportForSeb(file, Console.Out);
                Console.WriteLine($"WD sample WAVs: {outputPath}");
            }
            else if (command == "bankinject")
            {
                var outputPath = WdSampleTool.InjectForSeb(file, args[2], Console.Out);
                Console.WriteLine($"Rebuilt PS2 pair: {outputPath}");
            }
            else if (command == "replaceblizzard")
            {
                var outputPath = SebBlizzardReplacement.Replace(file, args[2], Console.Out);
                Console.WriteLine($"Rebuilt Blizzard PS2 pair: {outputPath}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{Path.GetFileName(file)}] {ex.Message}");
            if (exitCode == 0)
            {
                exitCode = 1;
            }
        }

        if (i < files.Count - 1)
        {
            Console.WriteLine();
        }
    }

    return exitCode;
}

static void PrintInfo(SebFileInfo info)
{
    var plan = ScdBridge.BuildPlan(info.FilePath);
    var wdPath = WdLocator.FindForSeb(info.FilePath);
    WdFileInfo? wdInfo = null;
    if (wdPath is not null)
    {
        wdInfo = WdParser.Parse(wdPath);
    }

    var smallestGroup = info.Groups.Count > 0 ? info.Groups.Min(static group => group.Size) : 0;
    var largestGroup = info.Groups.Count > 0 ? info.Groups.Max(static group => group.Size) : 0;
    var previewOffsets = string.Join(", ", info.Groups.Take(8).Select(static group => $"0x{group.Offset:X}"));

    Console.WriteLine($"File: {info.FilePath}");
    Console.WriteLine("Type: PS2 SeBlock event/index container (.seb)");
    Console.WriteLine($"Actual Size: {info.FileSize} bytes");
    Console.WriteLine($"Declared Size: {info.DeclaredSize} bytes");
    Console.WriteLine($"Top-level group count: {info.TopLevelGroupCount}");
    Console.WriteLine($"Parsed top-level groups: {info.Groups.Count}");
    Console.WriteLine($"Smallest top-level group: {smallestGroup} bytes");
    Console.WriteLine($"Largest top-level group: {largestGroup} bytes");
    Console.WriteLine($"First group offsets: {previewOffsets}");
    Console.WriteLine($"Embedded audio marker found: {(info.HasEmbeddedAudioMarker ? "yes" : "no")}");
    Console.WriteLine($"Sibling .win32.scd: {plan.SiblingScdFile ?? "not found"}");
    Console.WriteLine($"Companion .wd: {wdPath ?? "not found"}");
    if (wdInfo is not null)
    {
        Console.WriteLine($"WD Magic: {wdInfo.Magic}");
        Console.WriteLine($"WD Actual Size: {wdInfo.FileSize} bytes");
        Console.WriteLine($"WD Declared Size: {wdInfo.DeclaredSize} bytes");
        Console.WriteLine($"WD Header Count @0x08: {wdInfo.HeaderCount08}");
        Console.WriteLine($"WD Header Count @0x0C: {wdInfo.HeaderCount0C}");
    }

    Console.WriteLine($"SCDInfo.exe: {plan.ScdInfoExe ?? "not found"}");
    Console.WriteLine("Native SEB preview render is available through: SEBInfo render <File/Dir>");
    Console.WriteLine("Native SEB inject is available through: SEBInfo inject <File> <SampleDir>");
    Console.WriteLine("The current SEB workflow remains samplebank-based via the companion WD.");
    Console.WriteLine("render writes a pitch-corrected preview for listening; bankdump writes raw reinjectable WD samples.");
    Console.WriteLine("A future effect-level KH2 SeBlock renderer can still be built on top of this.");
    Console.WriteLine("WD sample export is available through: SEBInfo bankdump <File/Dir>");
    Console.WriteLine("WD sample injection is available through: SEBInfo bankinject <File> <SampleDir>");
    Console.WriteLine("Blizzard replacement for KH2 se000 is available through: SEBInfo replaceblizzard <File> <WavOrDir>");
    Console.WriteLine("The extract command therefore uses the sibling Steam .win32.scd when it is available.");
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  SEBInfo info|extract|render|bankdump <File/Dir>");
    Console.WriteLine("  SEBInfo inject|bankinject|replaceblizzard <File> <SampleDirOrWav>");
}
