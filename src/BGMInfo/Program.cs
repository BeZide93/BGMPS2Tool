using KhPs2Audio.Shared;

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0)
    {
        PrintUsage();
        return 1;
    }

    var command = args[0].ToLowerInvariant();
    if (command == "offsetbgm")
    {
        if (args.Length is < 3 or > 4)
        {
            PrintUsage();
            return 1;
        }

        if (!int.TryParse(args[2], out var instrumentOffset))
        {
            Console.Error.WriteLine("Instrument offset must be an integer.");
            return 1;
        }

        try
        {
            var outputDirectory = args.Length >= 4
                ? args[3]
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "bgm-program-offset");
            var result = BgmWdTooling.OffsetBgmPrograms(args[1], instrumentOffset, outputDirectory, Console.Out);
            Console.WriteLine($"Offset BGM: {result.OutputBgmPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    if (command == "combinewd")
    {
        if (args.Length is < 3 or > 6)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            var outputDirectory = args.Length >= 4
                ? args[3]
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "wd-combiner");
            var result = BgmWdTooling.CombineBanks(
                args[1],
                args[2],
                outputDirectory,
                Console.Out,
                args.Length >= 5 ? args[4] : null,
                args.Length >= 6 ? args[5] : null);
            Console.WriteLine($"Combined WD: {result.CombinedWdPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    if (args.Length < 2 || args.Length > 3)
    {
        PrintUsage();
        return 1;
    }

    if (command is not ("info" or "extract" or "render" or "bankdump" or "bankinject" or "replacewav" or "replacemidi" or "vgmtransdiff"))
    {
        PrintUsage();
        return 1;
    }

    var needsSampleDirectory = command == "bankinject";
    var supportsOptionalThirdArgument = command is "replacemidi" or "vgmtransdiff";
    if ((!needsSampleDirectory && !supportsOptionalThirdArgument && args.Length != 2) ||
        (needsSampleDirectory && args.Length != 3) ||
        (supportsOptionalThirdArgument && args.Length is < 2 or > 3))
    {
        PrintUsage();
        return 1;
    }

    if (command == "replacewav")
    {
        try
        {
            var outputPath = BgmWaveRebuilder.ReplaceFromWave(args[1], Console.Out);
            Console.WriteLine($"Rebuilt PS2 pair: {outputPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    if (command == "replacemidi")
    {
        try
        {
            var outputPath = BgmMidiSf2Rebuilder.ReplaceFromMidi(args[1], args.Length >= 3 ? args[2] : null, Console.Out);
            Console.WriteLine($"Rebuilt PS2 pair: {outputPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    if (command == "vgmtransdiff")
    {
        try
        {
            var reportPath = VgmTransRoundtripDiagnostics.Run(args[1], args.Length >= 3 ? args[2] : null, Console.Out);
            Console.WriteLine($"VGMTrans roundtrip report: {reportPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    IReadOnlyList<string> files;
    try
    {
        files = FileBatch.Expand(args[1], ".bgm");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    if (files.Count == 0)
    {
        Console.Error.WriteLine("No .bgm files found.");
        return 1;
    }

    var exitCode = 0;
    for (var i = 0; i < files.Count; i++)
    {
        var file = files[i];
        try
        {
            var info = BgmParser.Parse(file);
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
                var outputPath = BgmNativeRenderer.RenderToWave(file, Console.Out);
                Console.WriteLine($"Native PS2 WAV: {outputPath}");
            }
            else if (command == "bankdump")
            {
                var outputPath = WdSampleTool.ExportForBgm(file, Console.Out);
                Console.WriteLine($"WD sample WAVs: {outputPath}");
            }
            else if (command == "bankinject")
            {
                var outputPath = WdSampleTool.InjectForBgm(file, args[2], Console.Out);
                Console.WriteLine($"Rebuilt PS2 pair: {outputPath}");
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

static void PrintInfo(BgmFileInfo info)
{
    var plan = ScdBridge.BuildPlan(info.FilePath);
    var wdPath = WdLocator.FindForBgm(info);
    WdFileInfo? wdInfo = null;
    if (wdPath is not null)
    {
        wdInfo = WdParser.Parse(wdPath);
    }

    Console.WriteLine($"File: {info.FilePath}");
    Console.WriteLine("Type: PS2 Square sequence (.bgm)");
    Console.WriteLine($"Actual Size: {info.FileSize} bytes");
    Console.WriteLine($"Declared Size: {info.DeclaredSize} bytes");
    Console.WriteLine($"Sequence Id: {info.SequenceId}");
    Console.WriteLine($"Bank Id: {info.BankId}");
    Console.WriteLine($"Header Word @0x08: 0x{info.HeaderWord08:X4}");
    Console.WriteLine($"Header Word @0x0A: 0x{info.HeaderWord0A:X4}");
    Console.WriteLine($"Header Word @0x0C: 0x{info.HeaderWord0C:X4}");
    Console.WriteLine($"Header Word @0x0E: 0x{info.HeaderWord0E:X4}");
    Console.WriteLine($"Header DWord @0x20: {info.HeaderWord20}");
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
    Console.WriteLine("Native PS2 rendering is available through: BGMInfo render <File/Dir>");
    Console.WriteLine("WD sample export is available through: BGMInfo bankdump <File/Dir>");
    Console.WriteLine("WD sample injection is available through: BGMInfo bankinject <File> <SampleDir>");
    Console.WriteLine("Direct WAV replacement is available through: BGMInfo replacewav <InputWav>");
    Console.WriteLine("Direct MIDI + SoundFont replacement is available through: BGMInfo replacemidi <InputMid> [InputSf2]");
    Console.WriteLine("VGMTrans roundtrip diagnostics are available through: BGMInfo vgmtransdiff <InputMid> [InputSf2]");
    Console.WriteLine("BGM program offset is available through: BGMInfo offsetbgm <InputBgm> <InstrumentOffset> [OutputDir]");
    Console.WriteLine("WD combining is available through: BGMInfo combinewd <PrimaryWd> <SecondaryWd> [OutputDir] [PrimaryBgm] [SecondaryBgm]");
    Console.WriteLine("The native renderer currently targets .bgm + .wd and writes a standalone .ps2.wav next to the output folder.");
    Console.WriteLine("The extract command still uses the sibling Steam .win32.scd when it is available.");
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  BGMInfo info|extract|render|bankdump <File/Dir>");
    Console.WriteLine("  BGMInfo bankinject <File> <SampleDir>");
    Console.WriteLine("  BGMInfo replacewav <InputWav>");
    Console.WriteLine("  BGMInfo replacemidi <InputMid> [InputSf2]");
    Console.WriteLine("  BGMInfo vgmtransdiff <InputMid> [InputSf2]");
    Console.WriteLine("  BGMInfo offsetbgm <InputBgm> <InstrumentOffset> [OutputDir]");
    Console.WriteLine("  BGMInfo combinewd <PrimaryWd> <SecondaryWd> [OutputDir] [PrimaryBgm] [SecondaryBgm]");
}
