namespace KhPs2Audio.Shared;

public static class SebNativeRenderer
{
    public static string RenderToWaveDirectory(string sebPath, TextWriter log)
    {
        log.WriteLine("SEB native render now exports pitch-corrected PS2 preview WAVs derived from the WD sample bank.");
        log.WriteLine("This is meant for listening and comparison. Use bankdump if you want raw, reinjectable WD samples.");
        log.WriteLine("A fully event-accurate SeBlock renderer is still a later step.");
        return WdSampleTool.ExportPreviewForSeb(sebPath, log);
    }

    public static string InjectFromWaveDirectory(string sebPath, string sampleDirectory, TextWriter log)
    {
        log.WriteLine("SEB native inject rebuilds the PS2 WD sample bank from edited WAVs and copies the companion .seb.");
        return WdSampleTool.InjectForSeb(sebPath, sampleDirectory, log);
    }
}
