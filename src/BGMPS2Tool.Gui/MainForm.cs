using System.Diagnostics;
using System.Media;
using KhPs2Audio.Shared;

namespace BGMPS2Tool.Gui;

internal sealed partial class MainForm : Form
{
    private readonly string _configPath = Path.Combine(AppContext.BaseDirectory, "config.ini");
    private readonly string _guiSettingsPath = Path.Combine(AppContext.BaseDirectory, "gui-settings.json");
    private readonly string _trackListPath = Path.Combine(AppContext.BaseDirectory, "tracklist.txt");

    private readonly UiTextWriter _logWriter;
    private GuiAppSettings _guiSettings = GuiAppSettings.Default;
    private IReadOnlyDictionary<int, TrackMetadataEntry> _trackMetadata = new Dictionary<int, TrackMetadataEntry>();
    private SoundPlayer? _player;
    private string? _activePreviewWavePath;
    private string? _lastOutputDirectory;

    public MainForm()
    {
        InitializeComponent();
        ApplyWindowIcon();
        _logWriter = new UiTextWriter(_logTextBox);
        HookEvents();
        LoadState();
    }

    private void ApplyWindowIcon()
    {
        try
        {
            using var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (appIcon is not null)
            {
                Icon = (System.Drawing.Icon)appIcon.Clone();
            }
        }
        catch
        {
            // Keep the default icon if the executable icon cannot be extracted.
        }
    }

    private void HookEvents()
    {
        HookAdvancedEvents();
        _reloadConfigButton.Click += (_, _) => LoadConfigIntoControls();
        _saveConfigButton.Click += (_, _) => SaveConfigFromControls();
        _saveGuiSettingsButton.Click += (_, _) => SaveGuiSettingsFromControls();
        _showTrackListButton.Click += (_, _) => ShowTrackList();
        _rebuildMidiButton.Click += async (_, _) => await RunMidiReplacementAsync();
        _rebuildWaveButton.Click += async (_, _) => await RunWaveReplacementAsync();
        _playSourceButton.Click += async (_, _) => await PlaySourcePreviewAsync();
        _playOutputButton.Click += async (_, _) => await PlayOutputPreviewAsync();
        _stopPlaybackButton.Click += (_, _) => StopPlayback();
        _clearTempPreviewButton.Click += (_, _) => ClearTempPreview();
        _autofillOutputButton.Click += (_, _) => AutofillOutputPaths();
        _openOutputFolderButton.Click += (_, _) => OpenLastOutputDirectory();
        _runOffsetToolButton.Click += async (_, _) => await RunProgramOffsetToolAsync();
        _runWdCombinerButton.Click += async (_, _) => await RunWdCombinerAsync();
        _autoDetectPrimaryBgmButton.Click += (_, _) => AutoDetectToolBgm(_toolPrimaryWdTextBox, _toolPrimaryBgmTextBox, "primary");
        _autoDetectSecondaryBgmButton.Click += (_, _) => AutoDetectToolBgm(_toolSecondaryWdTextBox, _toolSecondaryBgmTextBox, "secondary");

        _templateRootTextBox.TextChanged += (_, _) => UpdateTrackInfo();
        _midiTextBox.TextChanged += (_, _) => UpdateTrackInfo();
        _waveTextBox.TextChanged += (_, _) => UpdateTrackInfo();
    }

    private void LoadState()
    {
        _guiSettings = GuiAppSettingsStore.Load(_guiSettingsPath);
        _templateRootTextBox.Text = _guiSettings.TemplateRootDirectory;
        _midiTextBox.Text = _guiSettings.LastMidiPath;
        _sf2TextBox.Text = _guiSettings.LastSf2Path;
        _waveTextBox.Text = _guiSettings.LastWavePath;
        _compareBgmTextBox.Text = _guiSettings.LastOutputBgmPath;
        _compareWdTextBox.Text = _guiSettings.LastOutputWdPath;

        LoadConfigIntoControls();
        ReloadTrackMetadata();
        UpdateTrackInfo();
    }

    private void LoadConfigIntoControls()
    {
        var config = BgmToolGuiBridge.LoadConfig(_configPath);
        _volumeUpDown.Value = ToDecimal(config.Volume, _volumeUpDown);
        _sf2VolumeUpDown.Value = ToDecimal(config.Sf2Volume, _sf2VolumeUpDown);
        _sf2BankModeComboBox.SelectedItem = config.Sf2BankMode;
        _sf2PreEqUpDown.Value = ToDecimal(config.Sf2PreEqStrength, _sf2PreEqUpDown);
        _sf2PreLowPassUpDown.Value = ToDecimal(config.Sf2PreLowPassHz, _sf2PreLowPassUpDown);
        _sf2AutoLowPassCheckBox.Checked = config.Sf2AutoLowPass;
        _sf2LoopPolicyComboBox.SelectedItem = config.Sf2LoopPolicy;
        _sf2LoopMicroCrossfadeCheckBox.Checked = config.Sf2LoopMicroCrossfade;
        _sf2LoopTailWrapFillCheckBox.Checked = config.Sf2LoopTailWrapFill;
        _sf2LoopStartContentAlignCheckBox.Checked = config.Sf2LoopStartContentAlign;
        _sf2LoopEndContentAlignCheckBox.Checked = config.Sf2LoopEndContentAlign;
        _midiProgramCompactionComboBox.SelectedItem = config.MidiProgramCompaction;
        _adsrModeComboBox.SelectedItem = config.AdsrMode;
        _midiPitchWorkaroundCheckBox.Checked = config.MidiPitchBendWorkaround;
        _midiLoopCheckBox.Checked = config.MidiLoop;
        _holdMinutesUpDown.Value = ToDecimal(config.HoldMinutes, _holdMinutesUpDown);
        _preEqUpDown.Value = ToDecimal(config.PreEqStrength, _preEqUpDown);
        _preLowPassUpDown.Value = ToDecimal(config.PreLowPassHz, _preLowPassUpDown);
        _logWriter.WriteLine($"Loaded config.ini from: {_configPath}");
    }

    private void SaveConfigFromControls()
    {
        var config = ReadConfigFromControls();
        BgmToolGuiBridge.SaveConfig(_configPath, config);
        _logWriter.WriteLine($"Saved config.ini to: {_configPath}");
    }

    private void SaveGuiSettingsFromControls()
    {
        _guiSettings = _guiSettings with
        {
            TemplateRootDirectory = _templateRootTextBox.Text.Trim(),
            LastMidiPath = _midiTextBox.Text.Trim(),
            LastSf2Path = _sf2TextBox.Text.Trim(),
            LastWavePath = _waveTextBox.Text.Trim(),
            LastOutputBgmPath = _compareBgmTextBox.Text.Trim(),
            LastOutputWdPath = _compareWdTextBox.Text.Trim(),
        };
        GuiAppSettingsStore.Save(_guiSettingsPath, _guiSettings);
        _logWriter.WriteLine($"Saved GUI settings to: {_guiSettingsPath}");
    }

    private void ReloadTrackMetadata()
    {
        try
        {
            _trackMetadata = TrackMetadataTextLoader.Load(_trackListPath);
            _logWriter.WriteLine($"Track metadata entries loaded: {_trackMetadata.Count}");
        }
        catch (Exception ex)
        {
            _trackMetadata = new Dictionary<int, TrackMetadataEntry>();
            _logWriter.WriteLine($"Track metadata load failed: {ex.Message}");
        }

        UpdateTrackInfo();
    }

    private void ShowTrackList()
    {
        var table = TrackMetadataTextLoader.LoadTable(_trackListPath);
        var viewer = new Form
        {
            Text = "Tracklist",
            Width = 980,
            Height = 720,
            StartPosition = FormStartPosition.CenterParent
        };

        if (table.Headers.Count == 0)
        {
            var emptyLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "tracklist.txt not found next to the GUI package."
            };
            viewer.Controls.Add(emptyLabel);
            viewer.ShowDialog(this);
            return;
        }

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
        };

        foreach (var header in table.Headers)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                SortMode = DataGridViewColumnSortMode.Automatic
            });
        }

        foreach (var row in table.Rows)
        {
            grid.Rows.Add(row.ToArray());
        }

        if (grid.Columns.Count > 0)
        {
            grid.Columns[0].FillWeight = 18;
        }

        if (grid.Columns.Count > 1)
        {
            grid.Columns[1].FillWeight = 34;
        }

        if (grid.Columns.Count > 2)
        {
            grid.Columns[2].FillWeight = 34;
        }

        for (var i = 3; i < grid.Columns.Count; i++)
        {
            grid.Columns[i].FillWeight = 14;
        }

        viewer.Controls.Add(grid);
        viewer.ShowDialog(this);
    }

    private async Task RunMidiReplacementAsync()
    {
        try
        {
            await RunBusyAsync(async () =>
            {
                SaveGuiSettingsFromControls();
                var request = new GuiMidiReplacementRequest(
                    MidiPath: RequireExistingFile(_midiTextBox.Text, "MIDI"),
                    SoundFontPath: _sf2TextBox.Text.Trim().Length == 0 ? null : RequireExistingFile(_sf2TextBox.Text, "SF2"),
                    TemplateRootDirectory: NullIfWhiteSpace(_templateRootTextBox.Text),
                    ConfigPath: _configPath,
                    Config: ReadConfigFromControls());
                var outputDirectory = await Task.Run(() => BgmToolGuiBridge.RunMidiReplacement(request, _logWriter));
                AfterRebuild(outputDirectory, request.MidiPath);
            });
        }
        catch (Exception ex)
        {
            _logWriter.WriteLine($"MIDI rebuild failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "MIDI rebuild failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunWaveReplacementAsync()
    {
        try
        {
            await RunBusyAsync(async () =>
            {
                SaveGuiSettingsFromControls();
                var request = new GuiWaveReplacementRequest(
                    WavePath: RequireExistingFile(_waveTextBox.Text, "WAV"),
                    TemplateRootDirectory: NullIfWhiteSpace(_templateRootTextBox.Text),
                    ConfigPath: _configPath,
                    Config: ReadConfigFromControls());
                var outputDirectory = await Task.Run(() => BgmToolGuiBridge.RunWaveReplacement(request, _logWriter));
                AfterRebuild(outputDirectory, request.WavePath);
            });
        }
        catch (Exception ex)
        {
            _logWriter.WriteLine($"WAV rebuild failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "WAV rebuild failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunProgramOffsetToolAsync()
    {
        try
        {
            await RunBusyAsync(async () =>
            {
                var bgmPath = RequireExistingFile(_toolOffsetBgmTextBox.Text, "Tool BGM");
                var outputDirectory = ResolveToolOutputDirectory(_toolOffsetOutputTextBox, bgmPath, "bgm-program-offset");
                var offset = decimal.ToInt32(_toolProgramOffsetUpDown.Value);
                var result = await Task.Run(() => BgmWdTooling.OffsetBgmPrograms(bgmPath, offset, outputDirectory, _logWriter));
                _lastOutputDirectory = outputDirectory;
                _logWriter.WriteLine($"Offset tool output: {result.OutputBgmPath}");
                MessageBox.Show(this, $"Patched {result.PatchedProgramCount} program marker(s) and wrote:{Environment.NewLine}{result.OutputBgmPath}", "BGM 0020xx Offset Tool", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }
        catch (Exception ex)
        {
            _logWriter.WriteLine($"BGM offset tool failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "BGM 0020xx Offset Tool", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunWdCombinerAsync()
    {
        try
        {
            await RunBusyAsync(async () =>
            {
                var primaryWdPath = RequireExistingFile(_toolPrimaryWdTextBox.Text, "Primary WD");
                var secondaryWdPath = RequireExistingFile(_toolSecondaryWdTextBox.Text, "Secondary WD");
                var outputDirectory = ResolveToolOutputDirectory(_toolCombinerOutputTextBox, primaryWdPath, "wd-combiner");
                var result = await Task.Run(() => BgmWdTooling.CombineBanks(
                    primaryWdPath,
                    secondaryWdPath,
                    outputDirectory,
                    _logWriter,
                    NullIfWhiteSpace(_toolPrimaryBgmTextBox.Text),
                    NullIfWhiteSpace(_toolSecondaryBgmTextBox.Text)));
                _lastOutputDirectory = outputDirectory;
                _logWriter.WriteLine($"WD combiner output: {result.CombinedWdPath}");
                if (result.SecondaryBgmOutputPath is not null)
                {
                    _compareBgmTextBox.Text = result.SecondaryBgmOutputPath;
                    _compareWdTextBox.Text = result.CombinedWdPath;
                }

                MessageBox.Show(
                    this,
                    $"Combined WD written:{Environment.NewLine}{result.CombinedWdPath}{Environment.NewLine}{Environment.NewLine}Secondary BGM patched events: {result.SecondaryProgramPatchedCount}",
                    "Field/Battle Maker / WD Combiner",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            });
        }
        catch (Exception ex)
        {
            _logWriter.WriteLine($"WD combiner failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Field/Battle Maker / WD Combiner", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task PlaySourcePreviewAsync()
    {
        try
        {
            await RunBusyAsync(async () =>
            {
                var midiPath = RequireExistingFile(_midiTextBox.Text, "MIDI");
                var sf2Path = ResolveSourceSf2Path(midiPath);
                var previewPath = await Task.Run(() => BgmToolGuiBridge.RenderMidiSf2Preview(midiPath, sf2Path, ReadConfigFromControls(), _logWriter));
                PlayWave(previewPath);
            });
        }
        catch (Exception ex)
        {
            _logWriter.WriteLine($"Source preview failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Source preview failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task PlayOutputPreviewAsync()
    {
        try
        {
            await RunBusyAsync(async () =>
            {
                var bgmPath = RequireExistingFile(_compareBgmTextBox.Text, "BGM");
                var wdPath = RequireExistingFile(_compareWdTextBox.Text, "WD");
                var previewPath = await Task.Run(() => BgmToolGuiBridge.RenderOutputPreview(bgmPath, wdPath, _logWriter));
                PlayWave(previewPath);
            });
        }
        catch (Exception ex)
        {
            _logWriter.WriteLine($"Output preview failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Output preview failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        UseWaitCursor = true;
        ToggleActionButtons(false);
        try
        {
            await action();
        }
        finally
        {
            ToggleActionButtons(true);
            UseWaitCursor = false;
        }
    }

    private void ToggleActionButtons(bool enabled)
    {
        _rebuildMidiButton.Enabled = enabled;
        _rebuildWaveButton.Enabled = enabled;
        _playSourceButton.Enabled = enabled;
        _playOutputButton.Enabled = enabled;
        _runOffsetToolButton.Enabled = enabled;
        _runWdCombinerButton.Enabled = enabled;
        _advancedLoadWdButton.Enabled = enabled;
        _advancedSaveWdButton.Enabled = enabled;
    }

    private void AfterRebuild(string outputDirectory, string sourcePath)
    {
        _lastOutputDirectory = outputDirectory;
        var stem = TryInferOutputBgmStem(Path.GetFileNameWithoutExtension(sourcePath));
        if (stem is not null)
        {
            var bgmPath = Path.Combine(outputDirectory, $"{stem}.bgm");
            if (File.Exists(bgmPath))
            {
                _compareBgmTextBox.Text = bgmPath;
                try
                {
                    var info = BgmParser.Parse(bgmPath);
                    var wdPath = WdLocator.FindForBgm(info);
                    if (wdPath is not null)
                    {
                        _compareWdTextBox.Text = wdPath;
                    }
                }
                catch
                {
                    // Keep compare paths best-effort only.
                }
            }
        }

        SaveGuiSettingsFromControls();
    }

    private void AutofillOutputPaths()
    {
        var sourcePath = _midiTextBox.Text.Trim();
        if (sourcePath.Length == 0)
        {
            sourcePath = _waveTextBox.Text.Trim();
        }

        if (sourcePath.Length == 0)
        {
            return;
        }

        var outputDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!, "output");
        if (!Directory.Exists(outputDirectory))
        {
            return;
        }

        var stem = TryInferOutputBgmStem(Path.GetFileNameWithoutExtension(sourcePath));
        if (stem is null)
        {
            return;
        }

        var bgmPath = Path.Combine(outputDirectory, $"{stem}.bgm");
        if (!File.Exists(bgmPath))
        {
            return;
        }

        _compareBgmTextBox.Text = bgmPath;
        try
        {
            var info = BgmParser.Parse(bgmPath);
            var wdPath = WdLocator.FindForBgm(info);
            if (wdPath is not null)
            {
                _compareWdTextBox.Text = wdPath;
            }
        }
        catch
        {
            // Ignore parse issues while autofilling compare paths.
        }
    }

    private void OpenLastOutputDirectory()
    {
        var directory = _lastOutputDirectory;
        if (directory is null)
        {
            var sourcePath = _midiTextBox.Text.Trim();
            if (sourcePath.Length == 0)
            {
                sourcePath = _waveTextBox.Text.Trim();
            }

            if (sourcePath.Length > 0)
            {
                directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!, "output");
            }
        }

        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{directory}\"",
            UseShellExecute = true,
        });
    }

    private void StopPlayback()
    {
        _player?.Stop();
        _player?.Dispose();
        _player = null;
        _activePreviewWavePath = null;
    }

    private void PlayWave(string wavePath)
    {
        StopPlayback();
        _activePreviewWavePath = wavePath;
        _player = new SoundPlayer(wavePath);
        _player.Load();
        _player.Play();
        _logWriter.WriteLine($"Playing preview WAV: {wavePath}");
    }

    private void ClearTempPreview()
    {
        StopPlayback();
        var deletedCount = BgmToolGuiBridge.ClearPreviewTempFiles(_logWriter);
        MessageBox.Show(
            this,
            deletedCount > 0
                ? $"Deleted {deletedCount} temp preview director{(deletedCount == 1 ? "y" : "ies")}."
                : "No temp preview directories were found.",
            "Clear Temp Preview",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void AutoDetectToolBgm(TextBox wdTextBox, TextBox bgmTextBox, string label)
    {
        var wdPath = wdTextBox.Text.Trim();
        if (wdPath.Length == 0)
        {
            return;
        }

        try
        {
            var resolved = BgmWdTooling.TryResolveMatchingBgmForWd(RequireExistingFile(wdPath, $"{label} WD"));
            if (resolved is null)
            {
                MessageBox.Show(this, $"No matching {label} BGM could be resolved automatically.", "Auto Detect", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bgmTextBox.Text = resolved;
            _logWriter.WriteLine($"Auto-detected {label} BGM: {resolved}");
        }
        catch (Exception ex)
        {
            _logWriter.WriteLine($"Auto-detect {label} BGM failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Auto Detect", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateTrackInfo()
    {
        var currentSource = !string.IsNullOrWhiteSpace(_midiTextBox.Text) ? _midiTextBox.Text.Trim() : _waveTextBox.Text.Trim();
        if (currentSource.Length == 0)
        {
            _trackInfoTextBox.Text = "Select a MIDI or WAV file to see track details.";
            return;
        }

        var lines = new List<string> { $"Source: {currentSource}" };
        try
        {
            var template = BgmToolGuiBridge.ResolveTemplatePair(currentSource, NullIfWhiteSpace(_templateRootTextBox.Text));
            lines.Add($"Resolved Template BGM: {template.BgmPath}");
            lines.Add($"Resolved Template WD:  {template.WdPath}");
            lines.Add($"Sequence ID: {template.SequenceId}");
            lines.Add($"Bank ID: {template.BankId}");

            if (TryExtractTrackNumber(template.AssetStem, out var trackNumber))
            {
                lines.Add($"Track Number: {trackNumber}");
                if (_trackMetadata.TryGetValue(trackNumber, out var metadata))
                {
                    lines.Add($"Name: {metadata.Name}");
                    lines.Add($"Description: {metadata.Description}");
                }
                else
                {
                    lines.Add("Name: not found in loaded track list");
                    lines.Add("Description: not found in loaded track list");
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add($"Template resolution: {ex.Message}");
        }

        _trackInfoTextBox.Text = string.Join(Environment.NewLine, lines);
    }

    private string ResolveSourceSf2Path(string midiPath)
    {
        if (!string.IsNullOrWhiteSpace(_sf2TextBox.Text))
        {
            return RequireExistingFile(_sf2TextBox.Text, "SF2");
        }

        var template = BgmToolGuiBridge.ResolveTemplatePair(midiPath, NullIfWhiteSpace(_templateRootTextBox.Text));
        var candidate = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(midiPath))!, $"wave{template.BankId:D4}.sf2");
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"No SF2 was selected and no matching wave{template.BankId:D4}.sf2 was found next to the MIDI.", candidate);
        }

        return candidate;
    }

    private BgmToolConfig ReadConfigFromControls()
    {
        return new BgmToolConfig(
            Volume: (double)_volumeUpDown.Value,
            Sf2Volume: (double)_sf2VolumeUpDown.Value,
            Sf2BankMode: _sf2BankModeComboBox.SelectedItem?.ToString() ?? "used",
            Sf2PreEqStrength: (double)_sf2PreEqUpDown.Value,
            Sf2PreLowPassHz: (double)_sf2PreLowPassUpDown.Value,
            Sf2AutoLowPass: _sf2AutoLowPassCheckBox.Checked,
            Sf2LoopPolicy: _sf2LoopPolicyComboBox.SelectedItem?.ToString() ?? "safe",
            Sf2LoopMicroCrossfade: _sf2LoopMicroCrossfadeCheckBox.Checked,
            Sf2LoopTailWrapFill: _sf2LoopTailWrapFillCheckBox.Checked,
            Sf2LoopStartContentAlign: _sf2LoopStartContentAlignCheckBox.Checked,
            Sf2LoopEndContentAlign: _sf2LoopEndContentAlignCheckBox.Checked,
            MidiProgramCompaction: _midiProgramCompactionComboBox.SelectedItem?.ToString() ?? "compact",
            AdsrMode: _adsrModeComboBox.SelectedItem?.ToString() ?? "authored",
            MidiPitchBendWorkaround: _midiPitchWorkaroundCheckBox.Checked,
            MidiLoop: _midiLoopCheckBox.Checked,
            HoldMinutes: (double)_holdMinutesUpDown.Value,
            PreEqStrength: (double)_preEqUpDown.Value,
            PreLowPassHz: (double)_preLowPassUpDown.Value);
    }

    private static decimal ToDecimal(double value, NumericUpDown target)
    {
        var decimalValue = (decimal)value;
        return Math.Min(target.Maximum, Math.Max(target.Minimum, decimalValue));
    }

    private static string RequireExistingFile(string pathText, string label)
    {
        var fullPath = Path.GetFullPath(pathText.Trim());
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"{label} file not found.", fullPath);
        }

        return fullPath;
    }

    private static string ResolveToolOutputDirectory(TextBox outputTextBox, string inputPath, string defaultFolderName)
    {
        var outputText = outputTextBox.Text.Trim();
        var fullPath = outputText.Length > 0
            ? Path.GetFullPath(outputText)
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputPath))!, defaultFolderName);
        outputTextBox.Text = fullPath;
        return fullPath;
    }

    private static string? NullIfWhiteSpace(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryExtractTrackNumber(string text, out int trackNumber)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?i)(\d{2,3})");
        if (match.Success && int.TryParse(match.Groups[1].Value, out trackNumber))
        {
            return true;
        }

        trackNumber = -1;
        return false;
    }

    private static string? TryInferOutputBgmStem(string fileStem)
    {
        if (fileStem.EndsWith(".ps2", StringComparison.OrdinalIgnoreCase))
        {
            fileStem = fileStem[..^4];
        }

        if (TryExtractTrackNumber(fileStem, out var trackNumber))
        {
            return $"music{trackNumber:D3}";
        }

        return null;
    }
}
