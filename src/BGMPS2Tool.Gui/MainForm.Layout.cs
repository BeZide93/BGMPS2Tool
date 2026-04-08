namespace BGMPS2Tool.Gui;

internal sealed partial class MainForm
{
    private readonly ToolTip _infoToolTip = new();

    private readonly TextBox _templateRootTextBox = CreatePathTextBox();
    private readonly TextBox _midiTextBox = CreatePathTextBox();
    private readonly TextBox _sf2TextBox = CreatePathTextBox();
    private readonly TextBox _waveTextBox = CreatePathTextBox();
    private readonly TextBox _compareBgmTextBox = CreatePathTextBox();
    private readonly TextBox _compareWdTextBox = CreatePathTextBox();
    private readonly TextBox _toolOffsetBgmTextBox = CreatePathTextBox();
    private readonly TextBox _toolOffsetOutputTextBox = CreatePathTextBox();
    private readonly TextBox _toolPrimaryWdTextBox = CreatePathTextBox();
    private readonly TextBox _toolSecondaryWdTextBox = CreatePathTextBox();
    private readonly TextBox _toolPrimaryBgmTextBox = CreatePathTextBox();
    private readonly TextBox _toolSecondaryBgmTextBox = CreatePathTextBox();
    private readonly TextBox _toolCombinerOutputTextBox = CreatePathTextBox();
    private readonly TextBox _trackInfoTextBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly TextBox _logTextBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };

    private readonly NumericUpDown _volumeUpDown = CreateDecimalUpDown(0.001m, 64m, 1.0m, 3);
    private readonly NumericUpDown _sf2VolumeUpDown = CreateDecimalUpDown(0.001m, 64m, 1.0m, 3);
    private readonly ComboBox _sf2BankModeComboBox = CreateComboBox("used", "full");
    private readonly NumericUpDown _sf2PreEqUpDown = CreateDecimalUpDown(0m, 1m, 0m, 3, 0.01m);
    private readonly NumericUpDown _sf2PreLowPassUpDown = CreateDecimalUpDown(0m, 20_000m, 0m, 0, 100m);
    private readonly CheckBox _sf2AutoLowPassCheckBox = new() { Text = "Enable" };
    private readonly ComboBox _midiProgramCompactionComboBox = CreateComboBox("auto", "compact", "preserve");
    private readonly ComboBox _adsrModeComboBox = CreateComboBox("authored", "auto", "template");
    private readonly CheckBox _midiPitchWorkaroundCheckBox = new() { Text = "Enable" };
    private readonly CheckBox _midiLoopCheckBox = new() { Text = "Enable" };
    private readonly NumericUpDown _holdMinutesUpDown = CreateDecimalUpDown(0.1m, 600m, 60m, 1, 0.1m);
    private readonly NumericUpDown _preEqUpDown = CreateDecimalUpDown(0m, 1m, 0m, 3, 0.01m);
    private readonly NumericUpDown _preLowPassUpDown = CreateDecimalUpDown(0m, 20_000m, 0m, 0, 100m);
    private readonly NumericUpDown _toolProgramOffsetUpDown = CreateDecimalUpDown(-255m, 255m, 0m, 0, 1m);

    private readonly Button _rebuildMidiButton = new() { Text = "Rebuild MIDI + SF2", AutoSize = true };
    private readonly Button _rebuildWaveButton = new() { Text = "Rebuild WAV", AutoSize = true };
    private readonly Button _playSourceButton = new() { Text = "Play Source Preview", AutoSize = true };
    private readonly Button _playOutputButton = new() { Text = "Play Output Preview", AutoSize = true };
    private readonly Button _stopPlaybackButton = new() { Text = "Stop", AutoSize = true };
    private readonly Button _clearTempPreviewButton = new() { Text = "Clear Temp Preview", AutoSize = true };
    private readonly Button _saveConfigButton = new() { Text = "Save config.ini", AutoSize = true };
    private readonly Button _reloadConfigButton = new() { Text = "Reload config.ini", AutoSize = true };
    private readonly Button _saveGuiSettingsButton = new() { Text = "Save GUI Paths", AutoSize = true };
    private readonly Button _showTrackListButton = new() { Text = "Show Tracklist", AutoSize = true };
    private readonly Button _autofillOutputButton = new() { Text = "Use Latest Output", AutoSize = true };
    private readonly Button _openOutputFolderButton = new() { Text = "Open Output Folder", AutoSize = true };
    private readonly Button _runOffsetToolButton = new() { Text = "Apply 0020xx Offset", AutoSize = true };
    private readonly Button _runWdCombinerButton = new() { Text = "Build Combined WD", AutoSize = true };
    private readonly Button _autoDetectPrimaryBgmButton = new() { Text = "Auto Detect", AutoSize = true };
    private readonly Button _autoDetectSecondaryBgmButton = new() { Text = "Auto Detect", AutoSize = true };

    private void InitializeComponent()
    {
        Text = "BGMPS2Tool GUI";
        Width = 1480;
        Height = 980;
        MinimumSize = new Size(1200, 820);
        StartPosition = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add((TabPage)BuildRebuildTab());
        tabs.TabPages.Add((TabPage)BuildCompareTab());
        tabs.TabPages.Add((TabPage)BuildToolsTab());

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 72f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 28f));
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(BuildLogGroup(), 0, 1);
        Controls.Add(root);
    }

    private Control BuildRebuildTab()
    {
        var page = new TabPage("Rebuild");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(8),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46f));
        layout.Controls.Add(BuildRebuildLeftColumn(), 0, 0);
        layout.Controls.Add(BuildSettingsGroup(), 1, 0);
        page.Controls.Add(layout);
        return page;
    }

    private Control BuildCompareTab()
    {
        var page = new TabPage("Compare");
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        panel.Controls.Add(BuildSourcePreviewGroup(), 0, 0);
        panel.Controls.Add(BuildOutputPreviewGroup(), 0, 1);
        panel.Controls.Add(BuildCompareNotesGroup(), 0, 2);
        page.Controls.Add(panel);
        return page;
    }

    private Control BuildToolsTab()
    {
        var page = new TabPage("Tools");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        layout.Controls.Add(BuildProgramOffsetToolGroup(), 0, 0);
        layout.Controls.Add(BuildWdCombinerToolGroup(), 0, 1);
        layout.Controls.Add(BuildToolsNotesGroup(), 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    private Control BuildProgramOffsetToolGroup()
    {
        var group = new GroupBox { Text = "BGM 0020xx Offset Tool", Dock = DockStyle.Top, AutoSize = true };
        var layout = CreateToolLayout();
        var description = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1100, 0),
            Text = "Loads a single BGM, offsets every program marker opcode `00 20 XX` by the chosen amount, and writes the patched BGM to the selected output folder."
        };
        layout.Controls.Add(description, 0, 0);
        layout.SetColumnSpan(description, 4);

        AddToolPathRow(layout, 1, "Input BGM", _toolOffsetBgmTextBox, BrowseFileButton(_toolOffsetBgmTextBox, "BGM files (*.bgm)|*.bgm"));
        layout.Controls.Add(new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = "Instrument Offset" }, 0, 2);
        layout.Controls.Add(_toolProgramOffsetUpDown, 1, 2);
        var offsetLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            MaximumSize = new Size(420, 0),
            Text = "Positive values shift program ids upward. Negative values pull them down."
        };
        layout.Controls.Add(offsetLabel, 2, 2);
        layout.SetColumnSpan(offsetLabel, 2);
        AddToolPathRow(layout, 3, "Output Folder", _toolOffsetOutputTextBox, BrowseFolderButton(_toolOffsetOutputTextBox));

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(_runOffsetToolButton);
        layout.Controls.Add(buttons, 1, 4);
        layout.SetColumnSpan(buttons, 3);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildWdCombinerToolGroup()
    {
        var group = new GroupBox { Text = "Field/Battle Maker / WD Combiner", Dock = DockStyle.Top, AutoSize = true };
        var layout = CreateToolLayout();
        var description = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1100, 0),
            Text = "Combines a secondary WD into the primary WD, keeps the primary BGM unchanged, and patches the secondary BGM so its program markers are offset by the original primary instrument count while its bank id is retargeted to the primary WD."
        };
        layout.Controls.Add(description, 0, 0);
        layout.SetColumnSpan(description, 4);

        AddToolPathRow(layout, 1, "Primary WD", _toolPrimaryWdTextBox, BrowseFileButton(_toolPrimaryWdTextBox, "WD files (*.wd)|*.wd"));
        AddToolPathRow(layout, 2, "Secondary WD", _toolSecondaryWdTextBox, BrowseFileButton(_toolSecondaryWdTextBox, "WD files (*.wd)|*.wd"));
        AddToolPathRow(layout, 3, "Primary BGM", _toolPrimaryBgmTextBox, BrowseFileButton(_toolPrimaryBgmTextBox, "BGM files (*.bgm)|*.bgm"), _autoDetectPrimaryBgmButton);
        AddToolPathRow(layout, 4, "Secondary BGM", _toolSecondaryBgmTextBox, BrowseFileButton(_toolSecondaryBgmTextBox, "BGM files (*.bgm)|*.bgm"), _autoDetectSecondaryBgmButton);
        AddToolPathRow(layout, 5, "Output Folder", _toolCombinerOutputTextBox, BrowseFolderButton(_toolCombinerOutputTextBox));

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(_runWdCombinerButton);
        layout.Controls.Add(buttons, 1, 6);
        layout.SetColumnSpan(buttons, 3);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildToolsNotesGroup()
    {
        var group = new GroupBox { Text = "Tool Notes", Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            MaximumSize = new Size(1100, 0),
            Text = "The offset tool only patches BGM program markers. The WD combiner writes the combined primary WD plus, when available, a copied primary BGM and a patched secondary BGM into the chosen output folder. Auto Detect tries to resolve matching BGMs from the selected WD filenames and nearby KH2 ids."
        }, 0, 0);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildRebuildLeftColumn()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.Controls.Add(BuildPathsGroup(), 0, 0);
        layout.Controls.Add(BuildMidiGroup(), 0, 1);
        layout.Controls.Add(BuildWaveGroup(), 0, 2);
        layout.Controls.Add(BuildTrackInfoGroup(), 0, 3);
        return layout;
    }

    private Control BuildPathsGroup()
    {
        var group = new GroupBox { Text = "Global Paths", Dock = DockStyle.Top, AutoSize = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddPathRow(layout, 0, "KH2FM Export BGM Root", _templateRootTextBox, BrowseFolderButton(_templateRootTextBox));
        var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttonRow.Controls.Add(_saveGuiSettingsButton);
        buttonRow.Controls.Add(_showTrackListButton);
        layout.Controls.Add(buttonRow, 1, 1);
        layout.SetColumnSpan(buttonRow, 2);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildMidiGroup()
    {
        var group = new GroupBox { Text = "MIDI + SF2 Workflow", Dock = DockStyle.Top, AutoSize = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddPathRow(layout, 0, "MIDI", _midiTextBox, BrowseFileButton(_midiTextBox, "MIDI files (*.mid)|*.mid"));
        AddPathRow(layout, 1, "SF2 (optional)", _sf2TextBox, BrowseFileButton(_sf2TextBox, "SoundFont files (*.sf2)|*.sf2"));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(_rebuildMidiButton);
        buttons.Controls.Add(_openOutputFolderButton);
        layout.Controls.Add(buttons, 1, 2);
        layout.SetColumnSpan(buttons, 2);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildWaveGroup()
    {
        var group = new GroupBox { Text = "WAV Workflow", Dock = DockStyle.Top, AutoSize = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddPathRow(layout, 0, "WAV", _waveTextBox, BrowseFileButton(_waveTextBox, "Wave files (*.wav)|*.wav"));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(_rebuildWaveButton);
        layout.Controls.Add(buttons, 1, 1);
        layout.SetColumnSpan(buttons, 2);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildTrackInfoGroup()
    {
        var group = new GroupBox { Text = "Track Info", Dock = DockStyle.Fill };
        group.Controls.Add(_trackInfoTextBox);
        return group;
    }

    private Control BuildSettingsGroup()
    {
        var group = new GroupBox { Text = "config.ini Controls", Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            AutoScroll = true,
            Padding = new Padding(4),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        layout.Controls.Add(BuildMidiSettingsBlock(), 0, 0);
        layout.Controls.Add(BuildWaveSettingsBlock(), 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(_reloadConfigButton);
        buttons.Controls.Add(_saveConfigButton);
        layout.Controls.Add(buttons, 0, 2);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildMidiSettingsBlock()
    {
        var group = new GroupBox { Text = "MIDI + SF2 Workflow", Dock = DockStyle.Top, AutoSize = true };
        var layout = CreateConfigBlockLayout();
        AddConfigRow(layout, 0, "sf2_volume", _sf2VolumeUpDown, "Volume multiplier for imported SoundFont sample audio in the MIDI + SF2 workflow. Keep it at 1.0 for the closest possible roundtrip fidelity.");
        AddConfigRow(layout, 1, "sf2_bank_mode", _sf2BankModeComboBox, "Controls whether the rebuilt WD authors only MIDI-used presets or the full SoundFont bank. 'used' is normal; 'full' is mainly for bank conversion or pairing with an existing BGM.");
        AddConfigRow(layout, 2, "sf2_pre_eq", _sf2PreEqUpDown, "Applies gentle pre-conditioning to imported SoundFont sample audio before PS2 encoding. Useful when an SF2 sounds harsh or metallic.");
        AddConfigRow(layout, 3, "sf2_pre_lowpass_hz", _sf2PreLowPassUpDown, "Manual low-pass cutoff for imported SoundFont sample audio before PS2 encoding. Use 0 to disable the manual override.");
        AddConfigRow(layout, 4, "sf2_auto_lowpass", _sf2AutoLowPassCheckBox, "Automatically low-passes explicitly resampled SoundFont samples near their original bandwidth so rebuilt banks keep less empty upscaled high-frequency noise.");
        AddConfigRow(layout, 5, "midi_program_compaction", _midiProgramCompactionComboBox, "Controls whether sparse MIDI program numbers stay sparse in the authored WD or get renumbered densely. 'compact' removes empty WD table gaps.");
        AddConfigRow(layout, 6, "adsr", _adsrModeComboBox, "Chooses how MIDI/SF2 ADSR is authored. 'authored' uses the VGMTrans-style fit, 'auto' keeps the hybrid logic, and 'template' forces template WD ADSR where a match exists.");
        AddConfigRow(layout, 7, "midi_pitch_bend_workaround", _midiPitchWorkaroundCheckBox, "Enables the current pitch-bend approximation path for MIDI/SF2 rebuilds. Turn it off for clean A/B testing when bend behavior itself is under investigation.");
        AddConfigRow(layout, 8, "midi_loop", _midiLoopCheckBox, "Makes the rebuilt PS2 BGM sequence loop. If the MIDI has explicit loop markers, those are preferred; otherwise it falls back to a start-to-end loop.");
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildWaveSettingsBlock()
    {
        var group = new GroupBox { Text = "WAV Workflow", Dock = DockStyle.Top, AutoSize = true };
        var layout = CreateConfigBlockLayout();
        AddConfigRow(layout, 0, "volume", _volumeUpDown, "Volume multiplier for imported WAV replacements.");
        AddConfigRow(layout, 1, "hold_minutes", _holdMinutesUpDown, "Minimum note hold time for the older replacewav loop/sustain path. This mainly affects the WAV workflow.");
        AddConfigRow(layout, 2, "pre_eq", _preEqUpDown, "Optional tone shaping before PS2 encoding for imported WAVs. Useful if replacements sound harsh or overly bright.");
        AddConfigRow(layout, 3, "pre_lowpass_hz", _preLowPassUpDown, "Optional extra low-pass cutoff before PS2 encoding for imported WAVs. Use 0 to disable it.");
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildSourcePreviewGroup()
    {
        var group = new GroupBox { Text = "Source Preview (MIDI + SF2)", Dock = DockStyle.Top, AutoSize = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Text = "Uses the current MIDI, SF2 and GUI setting values to render a direct source preview WAV, then plays it for A/B comparison."
        }, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(_playSourceButton);
        layout.Controls.Add(buttons, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildOutputPreviewGroup()
    {
        var group = new GroupBox { Text = "Output Preview (BGM + WD)", Dock = DockStyle.Top, AutoSize = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddPathRow(layout, 0, "BGM", _compareBgmTextBox, BrowseFileButton(_compareBgmTextBox, "BGM files (*.bgm)|*.bgm"));
        AddPathRow(layout, 1, "WD", _compareWdTextBox, BrowseFileButton(_compareWdTextBox, "WD files (*.wd)|*.wd"));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(_autofillOutputButton);
        buttons.Controls.Add(_playOutputButton);
        layout.Controls.Add(buttons, 1, 2);
        layout.SetColumnSpan(buttons, 2);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildCompareNotesGroup()
    {
        var group = new GroupBox { Text = "Compare Notes", Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            MaximumSize = new Size(1100, 0),
            Text = "The GUI keeps the old batch/config.ini flow alive, but lets you work from a template root instead of physically copying musicXXX.bgm + waveXXXX.wd next to every MIDI. Source preview renders MIDI + SF2 directly, while output preview renders the authored BGM + WD for fast audio comparison."
        }, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        buttons.Controls.Add(_stopPlaybackButton);
        buttons.Controls.Add(_clearTempPreviewButton);
        layout.Controls.Add(buttons, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildLogGroup()
    {
        var group = new GroupBox { Text = "Log", Dock = DockStyle.Fill };
        group.Controls.Add(_logTextBox);
        return group;
    }

    private static void AddPathRow(TableLayoutPanel layout, int row, string label, Control editor, Control browseButton)
    {
        layout.Controls.Add(new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = label }, 0, row);
        layout.Controls.Add(editor, 1, row);
        layout.Controls.Add(browseButton, 2, row);
    }

    private TableLayoutPanel CreateToolLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            AutoSize = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return layout;
    }

    private static void AddToolPathRow(TableLayoutPanel layout, int row, string label, Control editor, Control browseButton, Control? extraButton = null)
    {
        layout.Controls.Add(new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = label }, 0, row);
        layout.Controls.Add(editor, 1, row);
        layout.Controls.Add(browseButton, 2, row);
        if (extraButton is not null)
        {
            layout.Controls.Add(extraButton, 3, row);
        }
    }

    private TableLayoutPanel CreateConfigBlockLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            AutoSize = true,
            Padding = new Padding(4),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return layout;
    }

    private void AddConfigRow(TableLayoutPanel layout, int row, string label, Control editor, string infoText)
    {
        layout.Controls.Add(new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = label }, 0, row);
        layout.Controls.Add(editor, 1, row);
        layout.Controls.Add(CreateInfoButton(label, infoText), 2, row);
    }

    private Button CreateInfoButton(string title, string infoText)
    {
        var button = new Button
        {
            Text = "i",
            Width = 28,
            Height = 26,
            Margin = new Padding(4, 2, 0, 2),
            Anchor = AnchorStyles.Left
        };
        _infoToolTip.SetToolTip(button, infoText);
        button.Click += (_, _) => MessageBox.Show(this, infoText, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return button;
    }

    private Button BrowseFileButton(TextBox target, string filter)
    {
        var button = new Button { Text = "Browse...", AutoSize = true };
        button.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = filter, FileName = target.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                target.Text = dialog.FileName;
            }
        };
        return button;
    }

    private Button BrowseFolderButton(TextBox target)
    {
        var button = new Button { Text = "Browse...", AutoSize = true };
        button.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = target.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                target.Text = dialog.SelectedPath;
            }
        };
        return button;
    }

    private static TextBox CreatePathTextBox()
        => new() { Dock = DockStyle.Fill };

    private static ComboBox CreateComboBox(params string[] items)
    {
        var comboBox = new ComboBox { Dock = DockStyle.Left, DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        comboBox.Items.AddRange(items);
        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }

        return comboBox;
    }

    private static NumericUpDown CreateDecimalUpDown(decimal minimum, decimal maximum, decimal value, int decimals, decimal increment = 0.001m)
        => new()
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            DecimalPlaces = decimals,
            Increment = increment,
            ThousandsSeparator = true,
            Dock = DockStyle.Left,
            Width = 180,
        };
}
