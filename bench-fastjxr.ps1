<#
.SYNOPSIS
    Benchmarks Fast JXR HDR full-resolution decode latency in ImageGlass.

.DESCRIPTION
    Runs ImageGlass in a fresh process for each trial so the plugin's in-process decoded-image
    cache is empty every time. The Fast JXR plugin writes its internal Decode() timing to a
    per-run trace file via FASTJXR_TRACE_FILE.

    By default the script:
      1. performs one ignored 8-worker warm-up;
      2. tests 1, 4, 8, 12 and 16 workers;
      3. runs three measured repetitions;
      4. alternates ascending/descending worker order to reduce ordering bias;
      5. writes raw.csv, summary.csv and the individual plugin trace logs.

    The ranking uses plugin-internal full-resolution decode time, not ImageGlass startup time.

.PARAMETER JxrPath
    JPEG XR image to benchmark.

.PARAMETER ImageGlassExe
    Optional path to ImageGlass.exe. If omitted, common install locations and PATH are searched.

.PARAMETER Workers
    Worker counts to test. Default: 1,4,8,12,16.

.PARAMETER Repeats
    Number of measured repetitions per worker count. Default: 3.

.PARAMETER TimeoutSeconds
    Maximum time to wait for one full-resolution decode trace. Default: 120 seconds.

.PARAMETER OutputDirectory
    Directory for CSV and trace logs. Defaults to fastjxr-bench_<timestamp> beside this script.

.PARAMETER NoWarmup
    Skip the ignored warm-up decode.

.EXAMPLE
    .\bench-fastjxr.ps1 -JxrPath "D:\HDR\Big Sur Coastline_hdr.jxr"

.EXAMPLE
    .\bench-fastjxr.ps1 -JxrPath "D:\HDR\test.jxr" -ImageGlassExe "C:\Program Files\ImageGlass\ImageGlass.exe" -Repeats 5
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $JxrPath,

    [string] $ImageGlassExe,

    [ValidateRange(1, 64)]
    [int[]] $Workers = @(1, 4, 8, 12, 16),

    [ValidateRange(1, 20)]
    [int] $Repeats = 3,

    [ValidateRange(5, 600)]
    [int] $TimeoutSeconds = 120,

    [string] $OutputDirectory,

    [switch] $NoWarmup
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Resolve-ImageGlassExe {
    param([string] $ExplicitPath)

    if ($ExplicitPath) {
        $resolved = Resolve-Path -LiteralPath $ExplicitPath -ErrorAction Stop
        return $resolved.Path
    }

    $candidates = New-Object System.Collections.Generic.List[string]

    try {
        $cmd = Get-Command ImageGlass.exe -ErrorAction Stop
        if ($cmd.Source) { $candidates.Add($cmd.Source) }
    }
    catch { }

    if ($env:LOCALAPPDATA) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\ImageGlass\ImageGlass.exe'))
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'ImageGlass\ImageGlass.exe'))
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\ImageGlass.exe'))
    }
    if ($env:ProgramFiles) {
        $candidates.Add((Join-Path $env:ProgramFiles 'ImageGlass\ImageGlass.exe'))
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'ImageGlass\ImageGlass.exe'))
    }

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw @"
ImageGlass.exe could not be found automatically.
Pass it explicitly:
  .\bench-fastjxr.ps1 -JxrPath "..." -ImageGlassExe "C:\...\ImageGlass.exe"
"@
}

function Stop-AllImageGlass {
    # A fresh ImageGlass process is required for every trial so the plugin's in-process cache
    # cannot turn the test into a cache-hit benchmark.
    $procs = @(Get-Process -Name 'ImageGlass' -ErrorAction SilentlyContinue)
    foreach ($p in $procs) {
        try {
            if (-not $p.HasExited) {
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            }
        }
        catch { }
    }

    if ($procs.Count -gt 0) {
        Start-Sleep -Milliseconds 350
    }
}

function Parse-TraceFile {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    $lines = @(Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue)
    if ($lines.Count -eq 0) { return $null }

    $timing = $null
    foreach ($line in $lines) {
        if ($line -match '\tevent=timing\top=full-res decode \(([^)]+)\)\tms=([0-9.]+)(?:\s|$)') {
            $timing = [pscustomobject]@{
                Status   = $matches[1]
                DecodeMs = [double]::Parse($matches[2], [Globalization.CultureInfo]::InvariantCulture)
            }
            break
        }
    }

    if ($null -eq $timing) { return $null }

    $route = 'generic'
    foreach ($line in $lines) {
        if ($line -match '\tevent=info\tmessage=parallel ROI path workers=') {
            $route = 'parallel-roi'
            break
        }
        if ($line -match '\tevent=info\tmessage=sequential RGBA32F path') {
            $route = 'sequential-rgba32f'
        }
    }

    return [pscustomobject]@{
        Status   = $timing.Status
        DecodeMs = $timing.DecodeMs
        Route    = $route
    }
}

function Invoke-FastJxrTrial {
    param(
        [int] $WorkerCount,
        [int] $Repeat,
        [string] $TracePath,
        [bool] $Warmup
    )

    Stop-AllImageGlass

    if (Test-Path -LiteralPath $TracePath) {
        Remove-Item -LiteralPath $TracePath -Force
    }

    $env:FASTJXR_WORKERS = [string]$WorkerCount
    $env:FASTJXR_TRACE = '1'
    $env:FASTJXR_TRACE_FILE = $TracePath

    # ImageGlass accepts an image path as a normal command-line argument.
    # Use an explicitly quoted single argument so paths containing spaces survive Start-Process.
    $quotedImageArg = '"' + $script:JxrPathResolved.Replace('"', '\"') + '"'

    $wall = [Diagnostics.Stopwatch]::StartNew()

    $proc = Start-Process `
        -FilePath $script:ImageGlassExeResolved `
        -ArgumentList $quotedImageArg `
        -WorkingDirectory (Split-Path -Parent $script:ImageGlassExeResolved) `
        -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds($script:TimeoutSeconds)
    $parsed = $null

    try {
        while ([DateTime]::UtcNow -lt $deadline) {
            $parsed = Parse-TraceFile -Path $TracePath
            if ($null -ne $parsed) { break }

            if ($proc.HasExited -and -not (Test-Path -LiteralPath $TracePath)) {
                throw "ImageGlass exited before Fast JXR created a trace file."
            }

            Start-Sleep -Milliseconds 75
        }

        if ($null -eq $parsed) {
            throw "Timed out after $($script:TimeoutSeconds)s waiting for Fast JXR full-resolution decode trace."
        }

        $wall.Stop()

        if (-not $Warmup) {
            return [pscustomobject]@{
                Worker    = $WorkerCount
                Repeat    = $Repeat
                DecodeMs  = [math]::Round($parsed.DecodeMs, 3)
                Route     = $parsed.Route
                Status    = $parsed.Status
                WallMs    = [math]::Round($wall.Elapsed.TotalMilliseconds, 1)
                TraceFile = [IO.Path]::GetFileName($TracePath)
            }
        }
    }
    finally {
        Stop-AllImageGlass
    }

    return $null
}

function Get-Median {
    param([double[]] $Values)

    if ($Values.Count -eq 0) { return [double]::NaN }

    $sorted = @($Values | Sort-Object)
    $middle = [int][math]::Floor($sorted.Count / 2)

    if (($sorted.Count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }

    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}


$JxrPathResolved = (Resolve-Path -LiteralPath $JxrPath -ErrorAction Stop).Path
if ([IO.Path]::GetExtension($JxrPathResolved).ToLowerInvariant() -notin @('.jxr', '.wdp', '.hdp')) {
    Write-Warning "The input extension is not .jxr/.wdp/.hdp: $JxrPathResolved"
}

$ImageGlassExeResolved = Resolve-ImageGlassExe -ExplicitPath $ImageGlassExe

if (-not $OutputDirectory) {
    $base = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
    $OutputDirectory = Join-Path $base ('fastjxr-bench_' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# Save and restore the caller's environment.
$oldWorkers = $env:FASTJXR_WORKERS
$oldTrace = $env:FASTJXR_TRACE
$oldTraceFile = $env:FASTJXR_TRACE_FILE

$results = New-Object System.Collections.Generic.List[object]

try {
    Write-Host ""
    Write-Host "Fast JXR full-resolution benchmark" -ForegroundColor Cyan
    Write-Host "  Image:       $JxrPathResolved"
    Write-Host "  ImageGlass:  $ImageGlassExeResolved"
    Write-Host "  Workers:     $($Workers -join ', ')"
    Write-Host "  Repeats:     $Repeats"
    Write-Host "  Output:      $OutputDirectory"
    Write-Host ""
    Write-Host "NOTE: all running ImageGlass instances will be terminated between trials." -ForegroundColor Yellow
    Write-Host ""

    if (-not $NoWarmup) {
        $warmWorker = if ($Workers -contains 8) { 8 } else { $Workers[[int][math]::Floor($Workers.Count / 2)] }
        $warmTrace = Join-Path $OutputDirectory ("warmup-w{0}.log" -f $warmWorker)

        Write-Host ("Warm-up  workers={0} ..." -f $warmWorker) -NoNewline
        try {
            $null = Invoke-FastJxrTrial -WorkerCount $warmWorker -Repeat 0 -TracePath $warmTrace -Warmup $true
            $warmParsed = Parse-TraceFile -Path $warmTrace
            if ($warmParsed) {
                Write-Host (" {0:N1} ms [{1}]" -f $warmParsed.DecodeMs, $warmParsed.Route) -ForegroundColor DarkGray
            }
            else {
                Write-Host " done" -ForegroundColor DarkGray
            }
        }
        catch {
            Write-Host " FAILED" -ForegroundColor Red
            throw "Warm-up failed: $($_.Exception.Message)"
        }
    }

    for ($repeat = 1; $repeat -le $Repeats; $repeat++) {
        $order = @($Workers)
        if (($repeat % 2) -eq 0) {
            [array]::Reverse($order)
        }

        foreach ($worker in $order) {
            $traceName = "run-r{0:D2}-w{1:D2}.log" -f $repeat, $worker
            $tracePath = Join-Path $OutputDirectory $traceName

            Write-Host ("Run {0}/{1}  workers={2,2} ..." -f $repeat, $Repeats, $worker) -NoNewline

            try {
                $result = Invoke-FastJxrTrial `
                    -WorkerCount $worker `
                    -Repeat $repeat `
                    -TracePath $tracePath `
                    -Warmup $false

                $results.Add($result)
                Write-Host (" {0,8:N1} ms  {1}" -f $result.DecodeMs, $result.Route) -ForegroundColor Green
            }
            catch {
                $failure = [pscustomobject]@{
                    Worker    = $worker
                    Repeat    = $repeat
                    DecodeMs  = [double]::NaN
                    Route     = 'failed'
                    Status    = 'FAILED'
                    WallMs    = [double]::NaN
                    TraceFile = $traceName
                }
                $results.Add($failure)

                Write-Host " FAILED" -ForegroundColor Red
                Write-Warning $_.Exception.Message
            }
        }
    }
}
finally {
    Stop-AllImageGlass

    $env:FASTJXR_WORKERS = $oldWorkers
    $env:FASTJXR_TRACE = $oldTrace
    $env:FASTJXR_TRACE_FILE = $oldTraceFile
}

$rawPath = Join-Path $OutputDirectory 'raw.csv'
$results | Export-Csv -LiteralPath $rawPath -NoTypeInformation -Encoding UTF8

$summary = foreach ($worker in $Workers) {
    $valid = @($results | Where-Object {
        $_.Worker -eq $worker -and
        $_.Status -ne 'FAILED' -and
        -not [double]::IsNaN([double]$_.DecodeMs)
    })

    if ($valid.Count -eq 0) {
        [pscustomobject]@{
            Worker   = $worker
            Runs     = 0
            MedianMs = [double]::NaN
            MeanMs   = [double]::NaN
            BestMs   = [double]::NaN
            WorstMs  = [double]::NaN
            Route    = 'failed'
        }
        continue
    }

    $values = [double[]]@($valid | ForEach-Object { [double]$_.DecodeMs })
    $measure = $values | Measure-Object -Average -Minimum -Maximum
    $route = (($valid | Group-Object Route | Sort-Object Count -Descending | Select-Object -First 1).Name)

    [pscustomobject]@{
        Worker   = $worker
        Runs     = $valid.Count
        MedianMs = [math]::Round((Get-Median $values), 3)
        MeanMs   = [math]::Round([double]$measure.Average, 3)
        BestMs   = [math]::Round([double]$measure.Minimum, 3)
        WorstMs  = [math]::Round([double]$measure.Maximum, 3)
        Route    = $route
    }
}

$validSummary = @($summary | Where-Object { -not [double]::IsNaN([double]$_.MedianMs) })
if ($validSummary.Count -gt 0) {
    $bestMedian = ($validSummary | Measure-Object -Property MedianMs -Minimum).Minimum
    $summary = @($summary | ForEach-Object {
        $speed = if ([double]::IsNaN([double]$_.MedianMs)) {
            [double]::NaN
        }
        else {
            [math]::Round(([double]$_.MedianMs / [double]$bestMedian), 3)
        }

        $_ | Add-Member -NotePropertyName RelativeToBest -NotePropertyValue $speed -PassThru
    })
}
else {
    # Keep a stable schema even if every run failed.
    $summary = @($summary | ForEach-Object {
        $_ | Add-Member -NotePropertyName RelativeToBest -NotePropertyValue ([double]::NaN) -PassThru
    })
}

$summaryPath = Join-Path $OutputDirectory 'summary.csv'
$summary | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding UTF8

Write-Host ""
Write-Host "Results (plugin-internal full-resolution Decode time)" -ForegroundColor Cyan
$summary |
    Sort-Object @{ Expression = {
        if ([double]::IsNaN([double]$_.MedianMs)) { [double]::PositiveInfinity }
        else { [double]$_.MedianMs }
    }} |
    Format-Table Worker, Runs, MedianMs, MeanMs, BestMs, WorstMs, RelativeToBest, Route -AutoSize

if ($validSummary.Count -gt 0) {
    $winner = $validSummary | Sort-Object MedianMs | Select-Object -First 1
    Write-Host ("Best median: FASTJXR_WORKERS={0}  ({1:N3} ms)" -f $winner.Worker, $winner.MedianMs) -ForegroundColor Green
}

Write-Host ""
Write-Host "Raw results:  $rawPath"
Write-Host "Summary:      $summaryPath"
Write-Host "Trace logs:   $OutputDirectory"
