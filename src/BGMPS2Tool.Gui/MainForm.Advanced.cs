using System.Globalization;
using KhPs2Audio.Shared;

namespace BGMPS2Tool.Gui;

internal sealed partial class MainForm
{
    private readonly TextBox _advancedWdTextBox = CreatePathTextBox();
    private readonly TextBox _advancedOutputTextBox = CreatePathTextBox();
    private readonly Button _advancedLoadWdButton = new() { Text = "Load WD", AutoSize = true };
    private readonly Button _advancedSaveWdButton = new() { Text = "Save Advanced WD", AutoSize = true };
    private readonly Button _advancedReadmeButton = new() { Text = "README", AutoSize = true };
    private readonly Label _advancedStatusLabel = new()
    {
        AutoSize = true,
        MaximumSize = new Size(1200, 0),
        Text = "Load a WD to inspect instruments and apply global instrument edits plus optional region overrides."
    };
    private readonly Panel _advancedInstrumentScrollPanel = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        Padding = new Padding(0, 0, 12, 0),
    };
    private readonly TableLayoutPanel _advancedInstrumentStack = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1,
        Margin = new Padding(0),
        Padding = new Padding(0),
    };

    private readonly List<AdvancedInstrumentEditor> _advancedInstrumentEditors = [];
    private WdAdvancedBankInfo? _advancedBankInfo;

    private Control BuildAdvancedTab()
    {
        var page = new TabPage("Advanced");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.Controls.Add(BuildAdvancedTopGroup(), 0, 0);
        layout.Controls.Add(BuildAdvancedInstrumentGroup(), 0, 1);
        page.Controls.Add(layout);
        return page;
    }

    private Control BuildAdvancedTopGroup()
    {
        var group = new GroupBox { Text = "Advanced WD Instrument Editor", Dock = DockStyle.Top, AutoSize = true };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            AutoSize = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var description = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1180, 0),
            Text = "Loads a WD directly and exposes a two-stage editor: global instrument controls first, then optional per-region overrides underneath. Loop controls remain powerful but are still sample-aware."
        };
        layout.Controls.Add(description, 0, 0);
        layout.SetColumnSpan(description, 3);

        AddPathRow(layout, 1, "Input WD", _advancedWdTextBox, BrowseFileButton(_advancedWdTextBox, "WD files (*.wd)|*.wd"));
        AddPathRow(layout, 2, "Output Folder", _advancedOutputTextBox, BrowseFolderButton(_advancedOutputTextBox));

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(_advancedReadmeButton);
        buttons.Controls.Add(_advancedLoadWdButton);
        buttons.Controls.Add(_advancedSaveWdButton);
        layout.Controls.Add(buttons, 1, 3);
        layout.SetColumnSpan(buttons, 2);

        layout.Controls.Add(_advancedStatusLabel, 0, 4);
        layout.SetColumnSpan(_advancedStatusLabel, 3);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildAdvancedInstrumentGroup()
    {
        var group = new GroupBox { Text = "Instruments", Dock = DockStyle.Fill };
        if (!_advancedInstrumentScrollPanel.Controls.Contains(_advancedInstrumentStack))
        {
            _advancedInstrumentScrollPanel.Controls.Add(_advancedInstrumentStack);
        }

        group.Controls.Add(_advancedInstrumentScrollPanel);
        return group;
    }

    private void HookAdvancedEvents()
    {
        _advancedReadmeButton.Click += (_, _) => ShowAdvancedReadme();
        _advancedLoadWdButton.Click += async (_, _) => await LoadAdvancedWdAsync();
        _advancedSaveWdButton.Click += async (_, _) => await SaveAdvancedWdAsync();
        _advancedInstrumentScrollPanel.SizeChanged += (_, _) => UpdateAdvancedCardWidths();
    }

    private void ShowAdvancedReadme()
    {
        using var viewer = new Form
        {
            Text = "Advanced Tab README",
            Width = 900,
            Height = 700,
            StartPosition = FormStartPosition.CenterParent
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var richTextBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Font = new Font("Segoe UI", 10f),
        };

        AppendReadmeHeading(richTextBox, "Editing Model");
        AppendReadmeBullet(richTextBox, "Global instrument controls are applied first. Region overrides are applied afterwards and can refine or override the global result.");
        AppendReadmeBullet(richTextBox, "Pitch, volume, pan, and ADSR can now be edited both globally per instrument and locally per region.");
        AppendReadmeBullet(richTextBox, "Region controls are best for split-specific fixes such as one velocity layer, one key range, or one odd sample mapping inside an otherwise good instrument.");

        AppendReadmeHeading(richTextBox, "Loop Notes");
        AppendReadmeBullet(richTextBox, "Loop controls are still sample-aware, not purely region-local. If multiple regions share the same sample, a loop edit can affect every linked region.");
        AppendReadmeBullet(richTextBox, "Conflicting loop edits on the same shared sample are blocked instead of being written silently.");
        AppendReadmeBullet(richTextBox, "Loop Offset is written in PSX ADPCM byte units and normalized to valid 16-byte boundaries before save.");
        AppendReadmeBullet(richTextBox, "Force Loop and Force One-Shot rewrite both WD region loop fields and sample-side PSX loop markers so playback stays internally consistent.");

        AppendReadmeHeading(richTextBox, "Pitch / ADSR Notes");
        AppendReadmeBullet(richTextBox, "Hz Retune is a tuning helper, not real resampling.");
        AppendReadmeBullet(richTextBox, "Pitch edits use KH2/SquarePS2 WD tuning bytes, so the result follows the same UnityKey/FineTune model as the main rebuild path.");
        AppendReadmeBullet(richTextBox, "ADSR1 and ADSR2 are raw hex overrides with no extra safety logic.");
        AppendReadmeBullet(richTextBox, "Raw ADSR overrides are powerful but risky. Invalid values can easily make an instrument click, sustain forever, or die too quickly.");

        AppendReadmeHeading(richTextBox, "Practical Tips");
        AppendReadmeBullet(richTextBox, "Saving writes a new WD copy to the selected output folder. The original WD is left untouched.");
        AppendReadmeBullet(richTextBox, "Empty WD slots are shown for reference, but editing them has no audible effect until the slot actually contains regions.");
        AppendReadmeBullet(richTextBox, "The safest workflow is: start with one or two global instrument edits, then only add region overrides where a specific split still sounds wrong.");
        AppendReadmeBullet(richTextBox, "After saving, compare the result in the Compare tab before stacking many more advanced edits on top.");

        richTextBox.SelectionStart = 0;
        richTextBox.SelectionLength = 0;

        var closeButton = new Button { Text = "Close", AutoSize = true, Anchor = AnchorStyles.Right };
        closeButton.Click += (_, _) => viewer.Close();
        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft
        };
        buttonRow.Controls.Add(closeButton);

        layout.Controls.Add(richTextBox, 0, 0);
        layout.Controls.Add(buttonRow, 0, 1);
        viewer.Controls.Add(layout);
        viewer.ShowDialog(this);
    }

    private static void AppendReadmeHeading(RichTextBox richTextBox, string heading)
    {
        richTextBox.SelectionFont = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
        richTextBox.SelectionColor = SystemColors.ControlText;
        richTextBox.AppendText(heading + Environment.NewLine + Environment.NewLine);
        richTextBox.SelectionFont = new Font("Segoe UI", 10f, FontStyle.Regular);
    }

    private static void AppendReadmeBullet(RichTextBox richTextBox, string text)
    {
        richTextBox.SelectionBullet = true;
        richTextBox.SelectionIndent = 16;
        richTextBox.SelectionHangingIndent = 8;
        richTextBox.SelectionRightIndent = 12;
        richTextBox.AppendText(text + Environment.NewLine);
        richTextBox.SelectionBullet = false;
        richTextBox.SelectionIndent = 0;
        richTextBox.SelectionHangingIndent = 0;
        richTextBox.SelectionRightIndent = 0;
    }

    private async Task LoadAdvancedWdAsync()
    {
        try
        {
            await RunBusyAsync(async () =>
            {
                var wdPath = RequireExistingFile(_advancedWdTextBox.Text, "Advanced WD");
                var bankInfo = await Task.Run(() => WdAdvancedTooling.LoadBankInfo(wdPath));
                _advancedBankInfo = bankInfo;
                if (string.IsNullOrWhiteSpace(_advancedOutputTextBox.Text))
                {
                    _advancedOutputTextBox.Text = Path.Combine(Path.GetDirectoryName(bankInfo.WdPath)!, "wd-advanced");
                }

                PopulateAdvancedInstrumentEditors(bankInfo);
                _advancedStatusLabel.Text = $"Loaded WD bank {bankInfo.BankId:D4} with {bankInfo.InstrumentCount} instrument slot(s) and {bankInfo.RegionCount} region(s).";
                _logWriter.WriteLine($"Advanced WD loaded: {bankInfo.WdPath}");
                _logWriter.WriteLine($"Advanced WD instrument slots: {bankInfo.InstrumentCount}, regions: {bankInfo.RegionCount}");
            });
        }
        catch (Exception ex)
        {
            _logWriter.WriteLine($"Advanced WD load failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Advanced WD Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SaveAdvancedWdAsync()
    {
        try
        {
            await RunBusyAsync(async () =>
            {
                var bankInfo = _advancedBankInfo ?? throw new InvalidOperationException("Load a WD first before saving advanced edits.");
                var wdPath = RequireExistingFile(_advancedWdTextBox.Text, "Advanced WD");
                var outputDirectory = ResolveToolOutputDirectory(_advancedOutputTextBox, wdPath, "wd-advanced");
                var instrumentAdjustments = CollectAdvancedInstrumentAdjustments();
                var regionAdjustments = CollectAdvancedRegionAdjustments();
                var result = await Task.Run(() => WdAdvancedTooling.ApplyAdjustments(wdPath, outputDirectory, instrumentAdjustments, regionAdjustments, _logWriter));
                _lastOutputDirectory = outputDirectory;
                _compareWdTextBox.Text = result.OutputWdPath;
                _advancedStatusLabel.Text = $"Saved advanced WD: {Path.GetFileName(result.OutputWdPath)} | modified {result.ModifiedRegionCount} region(s), {result.ModifiedSampleCount} sample loop set(s).";
                _logWriter.WriteLine($"Advanced WD saved from bank {bankInfo.BankId:D4}: {result.OutputWdPath}");
                MessageBox.Show(
                    this,
                    $"Advanced WD written:{Environment.NewLine}{result.OutputWdPath}{Environment.NewLine}{Environment.NewLine}Modified regions: {result.ModifiedRegionCount}{Environment.NewLine}Modified sample loops: {result.ModifiedSampleCount}",
                    "Advanced WD Editor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            });
        }
        catch (Exception ex)
        {
            _logWriter.WriteLine($"Advanced WD save failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Advanced WD Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private List<WdAdvancedInstrumentAdjustment> CollectAdvancedInstrumentAdjustments()
    {
        var adjustments = new List<WdAdvancedInstrumentAdjustment>(_advancedInstrumentEditors.Count);
        foreach (var editor in _advancedInstrumentEditors)
        {
            adjustments.Add(new WdAdvancedInstrumentAdjustment(
                editor.InstrumentIndex,
                PitchOffsetSemitones: (double)editor.PitchOffsetUpDown.Value,
                FineTuneOffsetCents: decimal.ToInt32(editor.FineOffsetUpDown.Value),
                HzRetuneFrom: (double)editor.HzRetuneFromUpDown.Value,
                HzRetuneTo: (double)editor.HzRetuneToUpDown.Value,
                LoopOffsetBytes: decimal.ToInt32(editor.LoopOffsetBytesUpDown.Value),
                VolumeMultiplier: (double)editor.VolumeMultiplierUpDown.Value,
                PanShiftPercent: decimal.ToInt32(editor.PanShiftPercentUpDown.Value),
                LoopMode: editor.LoopModeComboBox.SelectedIndex switch
                {
                    1 => WdAdvancedLoopMode.ForceLoop,
                    2 => WdAdvancedLoopMode.ForceOneShot,
                    _ => WdAdvancedLoopMode.Keep,
                },
                Adsr1Override: ParseOptionalHexUShort(editor.Adsr1TextBox.Text, $"Instrument {editor.InstrumentIndex:D2} ADSR1"),
                Adsr2Override: ParseOptionalHexUShort(editor.Adsr2TextBox.Text, $"Instrument {editor.InstrumentIndex:D2} ADSR2")));
        }

        return adjustments;
    }

    private List<WdAdvancedRegionAdjustment> CollectAdvancedRegionAdjustments()
    {
        var adjustments = new List<WdAdvancedRegionAdjustment>();
        foreach (var instrumentEditor in _advancedInstrumentEditors)
        {
            foreach (var regionEditor in instrumentEditor.RegionEditors)
            {
                adjustments.Add(new WdAdvancedRegionAdjustment(
                    regionEditor.InstrumentIndex,
                    regionEditor.RegionIndex,
                    PitchOffsetSemitones: (double)regionEditor.PitchOffsetUpDown.Value,
                    FineTuneOffsetCents: decimal.ToInt32(regionEditor.FineOffsetUpDown.Value),
                    HzRetuneFrom: (double)regionEditor.HzRetuneFromUpDown.Value,
                    HzRetuneTo: (double)regionEditor.HzRetuneToUpDown.Value,
                    LoopOffsetBytes: decimal.ToInt32(regionEditor.LoopOffsetBytesUpDown.Value),
                    VolumeMultiplier: (double)regionEditor.VolumeMultiplierUpDown.Value,
                    PanShiftPercent: decimal.ToInt32(regionEditor.PanShiftPercentUpDown.Value),
                    LoopMode: regionEditor.LoopModeComboBox.SelectedIndex switch
                    {
                        1 => WdAdvancedLoopMode.ForceLoop,
                        2 => WdAdvancedLoopMode.ForceOneShot,
                        _ => WdAdvancedLoopMode.Keep,
                    },
                    Adsr1Override: ParseOptionalHexUShort(regionEditor.Adsr1TextBox.Text, $"Instrument {regionEditor.InstrumentIndex:D2} Region {regionEditor.RegionIndex:D2} ADSR1"),
                    Adsr2Override: ParseOptionalHexUShort(regionEditor.Adsr2TextBox.Text, $"Instrument {regionEditor.InstrumentIndex:D2} Region {regionEditor.RegionIndex:D2} ADSR2")));
            }
        }

        return adjustments;
    }

    private void PopulateAdvancedInstrumentEditors(WdAdvancedBankInfo bankInfo)
    {
        _advancedInstrumentEditors.Clear();
        _advancedInstrumentStack.SuspendLayout();
        _advancedInstrumentStack.Controls.Clear();
        _advancedInstrumentStack.RowStyles.Clear();
        _advancedInstrumentStack.RowCount = 0;
        foreach (var instrument in bankInfo.Instruments)
        {
            var editor = CreateAdvancedInstrumentEditor(instrument);
            _advancedInstrumentEditors.Add(editor);
            var rowIndex = _advancedInstrumentStack.RowCount++;
            _advancedInstrumentStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _advancedInstrumentStack.Controls.Add(editor.Card, 0, rowIndex);
        }

        _advancedInstrumentStack.ResumeLayout(true);
        UpdateAdvancedCardWidths();
    }

    private AdvancedInstrumentEditor CreateAdvancedInstrumentEditor(WdAdvancedInstrumentSummary instrument)
    {
        var headerButton = new Button
        {
            AutoSize = false,
            Height = 34,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"[+] {instrument.SummaryText}",
        };
        var detailsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(10, 6, 10, 10),
            Visible = false,
        };
        headerButton.Click += (_, _) =>
        {
            detailsPanel.Visible = !detailsPanel.Visible;
            headerButton.Text = $"{(detailsPanel.Visible ? "[-]" : "[+]")} {instrument.SummaryText}";
        };

        var pitchOffsetUpDown = CreateDecimalUpDown(-24m, 24m, 0m, 3, 0.01m);
        var fineOffsetUpDown = CreateDecimalUpDown(-500m, 500m, 0m, 0, 1m);
        var hzRetuneFromUpDown = CreateDecimalUpDown(0m, 192_000m, 0m, 0, 100m);
        var hzRetuneToUpDown = CreateDecimalUpDown(0m, 192_000m, 44_100m, 0, 100m);
        var loopOffsetBytesUpDown = CreateDecimalUpDown(-1_000_000m, 1_000_000m, 0m, 0, 16m);
        var volumeMultiplierUpDown = CreateDecimalUpDown(0.01m, 8m, 1m, 3, 0.01m);
        var panShiftPercentUpDown = CreateDecimalUpDown(-100m, 100m, 0m, 0, 1m);
        var loopModeComboBox = CreateComboBox("Keep", "Force Loop", "Force One-Shot");
        var adsr1TextBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "keep" };
        var adsr2TextBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "keep" };
        var resetButton = new Button { Text = "Reset Instrument", AutoSize = true };
        resetButton.Click += (_, _) =>
        {
            ResetAdvancedControlSet(
                pitchOffsetUpDown,
                fineOffsetUpDown,
                hzRetuneFromUpDown,
                hzRetuneToUpDown,
                loopOffsetBytesUpDown,
                volumeMultiplierUpDown,
                panShiftPercentUpDown,
                loopModeComboBox,
                adsr1TextBox,
                adsr2TextBox);
        };

        var globalContainer = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            AutoSize = true,
        };
        globalContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        globalContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        globalContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        globalContainer.Controls.Add(BuildAdvancedControlGroup(
            "Instrument Pitch / Rate",
            ("Pitch Offset (semitones)", pitchOffsetUpDown, "Global semitone offset for every region in this instrument."),
            ("Fine Offset (cents)", fineOffsetUpDown, "Global cent offset for every region in this instrument."),
            ("Hz Retune From", hzRetuneFromUpDown, "Optional global helper. Together with 'Hz Retune To', this is converted into a pitch offset."),
            ("Hz Retune To", hzRetuneToUpDown, "Optional global target rate for the Hz retune helper.")
        ), 0, 0);
        globalContainer.Controls.Add(BuildAdvancedControlGroup(
            "Instrument Loop / Playback",
            ("Loop Mode", loopModeComboBox, "Global loop intent for the instrument. Loop changes are still sample-aware."),
            ("Loop Offset (bytes)", loopOffsetBytesUpDown, "Global sample loop-start offset in PSX ADPCM byte units."),
            ("Volume Multiplier", volumeMultiplierUpDown, "Global volume multiplier for all regions in the instrument."),
            ("Pan Shift (%)", panShiftPercentUpDown, "Global pan shift for all regions in the instrument.")
        ), 1, 0);
        globalContainer.Controls.Add(BuildAdvancedControlGroup(
            "Instrument Raw Overrides",
            ("ADSR1 Override (hex)", adsr1TextBox, "Optional global ADSR1 override for all regions in this instrument."),
            ("ADSR2 Override (hex)", adsr2TextBox, "Optional global ADSR2 override for all regions in this instrument."),
            ("Current State", BuildInstrumentStateLabel(instrument), "Read-only summary of the loaded instrument."),
            ("Reset", resetButton, "Restores all global controls for this instrument to their neutral values.")
        ), 2, 0);

        var regionEditorsGroup = new GroupBox
        {
            Text = "Per-Region Fine Edits",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        var regionStack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        var regionEditors = new List<AdvancedRegionEditor>(instrument.Regions.Count);
        for (var i = 0; i < instrument.Regions.Count; i++)
        {
            var regionEditor = CreateAdvancedRegionEditor(instrument, instrument.Regions[i]);
            regionEditors.Add(regionEditor);
            regionStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            regionStack.Controls.Add(regionEditor.Card, 0, i);
        }

        regionEditorsGroup.Controls.Add(regionStack);

        detailsPanel.Controls.Add(globalContainer, 0, 0);
        detailsPanel.Controls.Add(regionEditorsGroup, 0, 1);

        var card = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(0),
        };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Controls.Add(headerButton, 0, 0);
        card.Controls.Add(detailsPanel, 0, 1);

        return new AdvancedInstrumentEditor(
            instrument.InstrumentIndex,
            card,
            headerButton,
            detailsPanel,
            pitchOffsetUpDown,
            fineOffsetUpDown,
            hzRetuneFromUpDown,
            hzRetuneToUpDown,
            loopOffsetBytesUpDown,
            volumeMultiplierUpDown,
            panShiftPercentUpDown,
            loopModeComboBox,
            adsr1TextBox,
            adsr2TextBox,
            regionEditors);
    }

    private AdvancedRegionEditor CreateAdvancedRegionEditor(WdAdvancedInstrumentSummary instrument, WdAdvancedRegionSummary region)
    {
        var sharedLabel = region.UsesSharedSample ? " | Shared Sample" : string.Empty;
        var headerButton = new Button
        {
            AutoSize = false,
            Height = 30,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"[+] Region {region.RegionIndex:D2} | Sample {region.SampleIndex:D3} | Key {region.KeyLow}-{region.KeyHigh} | Vel {region.VelocityLow}-{region.VelocityHigh}{sharedLabel}",
        };
        var detailsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(10, 6, 10, 10),
            Visible = false,
        };
        headerButton.Click += (_, _) =>
        {
            detailsPanel.Visible = !detailsPanel.Visible;
            headerButton.Text = $"{(detailsPanel.Visible ? "[-]" : "[+]")} Region {region.RegionIndex:D2} | Sample {region.SampleIndex:D3} | Key {region.KeyLow}-{region.KeyHigh} | Vel {region.VelocityLow}-{region.VelocityHigh}{sharedLabel}";
        };

        var pitchOffsetUpDown = CreateDecimalUpDown(-24m, 24m, 0m, 3, 0.01m);
        var fineOffsetUpDown = CreateDecimalUpDown(-500m, 500m, 0m, 0, 1m);
        var hzRetuneFromUpDown = CreateDecimalUpDown(0m, 192_000m, 0m, 0, 100m);
        var hzRetuneToUpDown = CreateDecimalUpDown(0m, 192_000m, 44_100m, 0, 100m);
        var loopOffsetBytesUpDown = CreateDecimalUpDown(-1_000_000m, 1_000_000m, 0m, 0, 16m);
        var volumeMultiplierUpDown = CreateDecimalUpDown(0.01m, 8m, 1m, 3, 0.01m);
        var panShiftPercentUpDown = CreateDecimalUpDown(-100m, 100m, 0m, 0, 1m);
        var loopModeComboBox = CreateComboBox("Keep", "Force Loop", "Force One-Shot");
        var adsr1TextBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "keep" };
        var adsr2TextBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "keep" };
        var resetButton = new Button { Text = "Reset Region", AutoSize = true };
        resetButton.Click += (_, _) =>
        {
            ResetAdvancedControlSet(
                pitchOffsetUpDown,
                fineOffsetUpDown,
                hzRetuneFromUpDown,
                hzRetuneToUpDown,
                loopOffsetBytesUpDown,
                volumeMultiplierUpDown,
                panShiftPercentUpDown,
                loopModeComboBox,
                adsr1TextBox,
                adsr2TextBox);
        };

        var container = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            AutoSize = true,
        };
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        container.Controls.Add(BuildAdvancedControlGroup(
            "Region Pitch / Rate",
            ("Pitch Offset (semitones)", pitchOffsetUpDown, "Local region semitone offset. Applied after global instrument tuning."),
            ("Fine Offset (cents)", fineOffsetUpDown, "Local region cent offset. Applied after the global instrument tuning path."),
            ("Hz Retune From", hzRetuneFromUpDown, "Optional local helper. Together with 'Hz Retune To', this adds another pitch offset on top of the global result."),
            ("Hz Retune To", hzRetuneToUpDown, "Optional local target rate for the Hz retune helper.")
        ), 0, 0);
        container.Controls.Add(BuildAdvancedControlGroup(
            "Region Loop / Playback",
            ("Loop Mode", loopModeComboBox, "Local loop intent. Loop edits are still sample-aware and can affect linked regions that share the same sample."),
            ("Loop Offset (bytes)", loopOffsetBytesUpDown, "Local loop-start offset in PSX ADPCM bytes. Added on top of any global instrument loop offset."),
            ("Volume Multiplier", volumeMultiplierUpDown, "Local region volume multiplier. Applied after the global instrument multiplier."),
            ("Pan Shift (%)", panShiftPercentUpDown, "Local region pan shift. Added on top of the global instrument pan shift.")
        ), 1, 0);
        container.Controls.Add(BuildAdvancedControlGroup(
            "Region Raw Overrides",
            ("ADSR1 Override (hex)", adsr1TextBox, "Optional local ADSR1 override for this region only."),
            ("ADSR2 Override (hex)", adsr2TextBox, "Optional local ADSR2 override for this region only."),
            ("Current State", BuildRegionStateLabel(region), "Read-only summary of the loaded region."),
            ("Reset", resetButton, "Restores all local controls for this region to neutral values.")
        ), 2, 0);

        detailsPanel.Controls.Add(container, 0, 0);

        var card = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
            Margin = new Padding(0, 0, 0, 6),
            Padding = new Padding(0),
        };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Controls.Add(headerButton, 0, 0);
        card.Controls.Add(detailsPanel, 0, 1);

        return new AdvancedRegionEditor(
            instrument.InstrumentIndex,
            region.RegionIndex,
            card,
            headerButton,
            detailsPanel,
            pitchOffsetUpDown,
            fineOffsetUpDown,
            hzRetuneFromUpDown,
            hzRetuneToUpDown,
            loopOffsetBytesUpDown,
            volumeMultiplierUpDown,
            panShiftPercentUpDown,
            loopModeComboBox,
            adsr1TextBox,
            adsr2TextBox);
    }

    private static void ResetAdvancedControlSet(
        NumericUpDown pitchOffsetUpDown,
        NumericUpDown fineOffsetUpDown,
        NumericUpDown hzRetuneFromUpDown,
        NumericUpDown hzRetuneToUpDown,
        NumericUpDown loopOffsetBytesUpDown,
        NumericUpDown volumeMultiplierUpDown,
        NumericUpDown panShiftPercentUpDown,
        ComboBox loopModeComboBox,
        TextBox adsr1TextBox,
        TextBox adsr2TextBox)
    {
        pitchOffsetUpDown.Value = 0m;
        fineOffsetUpDown.Value = 0m;
        hzRetuneFromUpDown.Value = 0m;
        hzRetuneToUpDown.Value = 44_100m;
        loopOffsetBytesUpDown.Value = 0m;
        volumeMultiplierUpDown.Value = 1m;
        panShiftPercentUpDown.Value = 0m;
        loopModeComboBox.SelectedIndex = 0;
        adsr1TextBox.Clear();
        adsr2TextBox.Clear();
    }

    private Control BuildAdvancedControlGroup(string title, params (string Label, Control Editor, string Info)[] rows)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill, AutoSize = true };
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

        for (var i = 0; i < rows.Length; i++)
        {
            layout.Controls.Add(new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = rows[i].Label }, 0, i);
            layout.Controls.Add(rows[i].Editor, 1, i);
            layout.Controls.Add(CreateInfoButton(rows[i].Label, rows[i].Info), 2, i);
        }

        group.Controls.Add(layout);
        return group;
    }

    private static Control BuildInstrumentStateLabel(WdAdvancedInstrumentSummary instrument)
    {
        return new Label
        {
            AutoSize = true,
            MaximumSize = new Size(320, 0),
            Text = instrument.Empty
                ? "Empty WD slot"
                : $"Looping: {(instrument.Looping ? "yes" : "no")}{Environment.NewLine}Shared Samples: {(instrument.UsesSharedSamples ? "yes" : "no")}{Environment.NewLine}Key Range: {instrument.KeyLow}-{instrument.KeyHigh}{Environment.NewLine}Velocity Range: {instrument.VelocityLow}-{instrument.VelocityHigh}"
        };
    }

    private static Control BuildRegionStateLabel(WdAdvancedRegionSummary region)
    {
        return new Label
        {
            AutoSize = true,
            MaximumSize = new Size(320, 0),
            Text = $"Sample: {region.SampleIndex:D3}{Environment.NewLine}Root: {region.UnityKey}{(region.FineTuneCents >= 0 ? "+" : string.Empty)}{region.FineTuneCents}c{Environment.NewLine}Loop: {(region.Looping ? region.LoopStartBytes.ToString(CultureInfo.InvariantCulture) : "off")}{Environment.NewLine}Shared Sample: {(region.UsesSharedSample ? "yes" : "no")}"
        };
    }

    private static ushort? ParseOptionalHexUShort(string text, string label)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        if (!ushort.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidDataException($"{label} must be a 16-bit hex value like 4F0C or C501.");
        }

        return value;
    }

    private void UpdateAdvancedCardWidths()
    {
        var width = Math.Max(920, _advancedInstrumentScrollPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 12);
        _advancedInstrumentStack.Width = width;
        foreach (var instrumentEditor in _advancedInstrumentEditors)
        {
            instrumentEditor.Card.MinimumSize = new Size(width, 0);
            instrumentEditor.Card.MaximumSize = new Size(width, 0);
            instrumentEditor.Card.Width = width;
            foreach (var regionEditor in instrumentEditor.RegionEditors)
            {
                regionEditor.Card.MinimumSize = new Size(width - 28, 0);
                regionEditor.Card.MaximumSize = new Size(width - 28, 0);
                regionEditor.Card.Width = width - 28;
            }
        }
    }

    private sealed record AdvancedInstrumentEditor(
        int InstrumentIndex,
        Control Card,
        Button HeaderButton,
        Control DetailsPanel,
        NumericUpDown PitchOffsetUpDown,
        NumericUpDown FineOffsetUpDown,
        NumericUpDown HzRetuneFromUpDown,
        NumericUpDown HzRetuneToUpDown,
        NumericUpDown LoopOffsetBytesUpDown,
        NumericUpDown VolumeMultiplierUpDown,
        NumericUpDown PanShiftPercentUpDown,
        ComboBox LoopModeComboBox,
        TextBox Adsr1TextBox,
        TextBox Adsr2TextBox,
        IReadOnlyList<AdvancedRegionEditor> RegionEditors);

    private sealed record AdvancedRegionEditor(
        int InstrumentIndex,
        int RegionIndex,
        Control Card,
        Button HeaderButton,
        Control DetailsPanel,
        NumericUpDown PitchOffsetUpDown,
        NumericUpDown FineOffsetUpDown,
        NumericUpDown HzRetuneFromUpDown,
        NumericUpDown HzRetuneToUpDown,
        NumericUpDown LoopOffsetBytesUpDown,
        NumericUpDown VolumeMultiplierUpDown,
        NumericUpDown PanShiftPercentUpDown,
        ComboBox LoopModeComboBox,
        TextBox Adsr1TextBox,
        TextBox Adsr2TextBox);
}
