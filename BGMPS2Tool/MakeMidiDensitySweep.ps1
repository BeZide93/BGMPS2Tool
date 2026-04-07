param(
    [string[]]$Assets = @("152", "188"),
    [double[]]$KeepRatios = @(1.00, 0.95, 0.90, 0.85, 0.80, 0.75, 0.70, 0.65, 0.60, 0.55, 0.50),
    [string]$OutputFolderName = "density-sweep"
)

$ErrorActionPreference = "Stop"

$toolDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Join-Path $toolDir $OutputFolderName
$halverExe = Join-Path (Join-Path (Split-Path -Parent $toolDir) ".tmp_midi_halver\bin\Release\net10.0") "MidiHalver.exe"
$bgmInfoExe = Join-Path $toolDir "BGMInfo.exe"

if (-not (Test-Path $halverExe)) {
    throw "MidiHalver.exe was not found at $halverExe"
}

if (-not (Test-Path $bgmInfoExe)) {
    throw "BGMInfo.exe was not found at $bgmInfoExe"
}

if (Test-Path $rootDir) {
    Remove-Item -Recurse -Force $rootDir
}

New-Item -ItemType Directory -Path $rootDir | Out-Null

$reportRows = New-Object System.Collections.Generic.List[object]

foreach ($asset in $Assets) {
    $assetId = [int]$asset
    $assetStem = "music{0:D3}" -f $assetId
    $waveStem = "wave{0:D4}" -f $assetId

    $fullMidiPath = Join-Path $toolDir ("{0}-b.mid" -f $assetStem)
    if (-not (Test-Path $fullMidiPath)) {
        $fullMidiPath = Join-Path $toolDir ("{0}.mid" -f $assetStem)
    }

    $bgmPath = Join-Path $toolDir ("{0}.bgm" -f $assetStem)
    $sf2Path = Join-Path $toolDir ("{0}.sf2" -f $waveStem)
    $wdPath = Join-Path $toolDir ("{0}.wd" -f $waveStem)

    foreach ($required in @($fullMidiPath, $bgmPath, $sf2Path, $wdPath)) {
        if (-not (Test-Path $required)) {
            throw "Missing required file for asset ${asset}: $required"
        }
    }

    foreach ($ratio in $KeepRatios) {
        $percent = [int][Math]::Round($ratio * 100.0, 0, [System.MidpointRounding]::AwayFromZero)
        $label = "keep-{0:D3}" -f $percent
        $caseDir = Join-Path (Join-Path $rootDir $assetStem) $label
        New-Item -ItemType Directory -Force -Path $caseDir | Out-Null

        $caseMidiPath = Join-Path $caseDir ("{0}.mid" -f $assetStem)
        $caseBgmPath = Join-Path $caseDir ("{0}.bgm" -f $assetStem)
        $caseSf2Path = Join-Path $caseDir ("{0}.sf2" -f $waveStem)
        $caseWdPath = Join-Path $caseDir ("{0}.wd" -f $waveStem)

        Copy-Item $bgmPath $caseBgmPath -Force
        Copy-Item $sf2Path $caseSf2Path -Force
        Copy-Item $wdPath $caseWdPath -Force

        $halverOutput = & $halverExe $fullMidiPath $caseMidiPath ([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, "{0:0.00}", $ratio)) 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "MidiHalver failed for ${assetStem} ${label}`n$($halverOutput | Out-String)"
        }

        $buildOutput = & $bgmInfoExe replacemidi $caseMidiPath 2>&1
        $buildExitCode = $LASTEXITCODE

        $outputDir = Join-Path $caseDir "output"
        $outputBgmPath = Join-Path $outputDir ("{0}.bgm" -f $assetStem)
        $outputWdPath = Join-Path $outputDir ("{0}.wd" -f $waveStem)

        $row = [ordered]@{
            Asset = $assetStem
            KeepPercent = $percent
            SourceMidiBytes = (Get-Item $fullMidiPath).Length
            GeneratedMidiBytes = (Get-Item $caseMidiPath).Length
            RebuildOk = $buildExitCode -eq 0
            OutputBgmBytes = if (Test-Path $outputBgmPath) { (Get-Item $outputBgmPath).Length } else { $null }
            OutputWdBytes = if (Test-Path $outputWdPath) { (Get-Item $outputWdPath).Length } else { $null }
            CaseDir = $caseDir
        }
        $reportRows.Add([pscustomobject]$row)

        $logPath = Join-Path $caseDir "rebuild.log"
        @(
            "=== MidiHalver ==="
            ($halverOutput | ForEach-Object { $_.ToString() })
            ""
            "=== BGMInfo replacemidi ==="
            ($buildOutput | ForEach-Object { $_.ToString() })
        ) | Set-Content -Path $logPath -Encoding UTF8
    }
}

$csvPath = Join-Path $rootDir "density-sweep-report.csv"
$reportRows | Sort-Object Asset, KeepPercent -Descending | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8

$mdPath = Join-Path $rootDir "density-sweep-report.md"
$mdLines = @(
    "# MIDI Density Sweep",
    "",
    "Generated on $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
    "",
    "| Asset | Keep % | Source MID bytes | Generated MID bytes | Output BGM bytes | Output WD bytes | Rebuild | Folder |",
    "| --- | ---: | ---: | ---: | ---: | ---: | --- | --- |"
)

foreach ($row in $reportRows | Sort-Object Asset, KeepPercent -Descending) {
    $mdLines += "| $($row.Asset) | $($row.KeepPercent) | $($row.SourceMidiBytes) | $($row.GeneratedMidiBytes) | $($row.OutputBgmBytes) | $($row.OutputWdBytes) | $($row.RebuildOk) | $($row.CaseDir.Replace($rootDir + '\', '')) |"
}

$mdLines | Set-Content -Path $mdPath -Encoding UTF8

Write-Host "Density sweep complete."
Write-Host "Report: $mdPath"
Write-Host "CSV:    $csvPath"
