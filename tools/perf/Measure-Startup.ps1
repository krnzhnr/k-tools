#Requires -Version 5.1
<#
.SYNOPSIS
    Замер теплого старта K-Tools: время до первого окна, маркер готовности в логе, пик памяти.
.DESCRIPTION
    Запускает приложение N раз подряд (каждый раз предварительно корректно закрывая
    предыдущий экземпляр — приложение односинглтонное, второй запуск без закрытия
    лишь перенаправил бы аргументы первому). Готовность определяется по появлению
    MainWindowHandle (первый кадр) — этот сигнал не зависит от фаз логирования,
    которые PERF-блоки двигают. Опционально ждет маркер готовности в сессионном логе.
    Результаты: медиана и все прогоны, CSV + JSON в tools/perf/results.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/perf/Measure-Startup.ps1 -Runs 5
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/perf/Measure-Startup.ps1 -Runs 3 -ExePath "C:\Program Files\KTools\KTools.App.exe"
#>
[CmdletBinding()]
param(
    [string]$ExePath = "",
    [int]$Runs = 5,
    [int]$TimeoutSec = 120,
    [int]$SettleSec = 2,
    [string]$ReadyMarker = "system.snapshot.completed,Сведения о системе,обязательные",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"

function Resolve-AppExe {
    param([string]$Hint)
    if ($Hint -ne "" -and (Test-Path -LiteralPath $Hint)) { return $Hint }
    foreach ($candidate in @(
        "$env:ProgramFiles\KTools\KTools.App.exe",
        "${env:ProgramFiles(x86)}\KTools\KTools.App.exe")) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $found = Get-ChildItem -LiteralPath (Join-Path $repoRoot "KTools.App\bin") -Recurse -Filter "KTools.App.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "Release" } |
        Sort-Object @{ Expression = { if ($_.FullName -match "publish") { 0 } else { 1 } }; Descending = $false }, @{ Expression = "LastWriteTime"; Descending = $true } |
        Select-Object -First 1
    if ($null -ne $found) { return $found.FullName }
    throw "Исполняемый файл KTools.App.exe не найден. Укажите -ExePath."
}

function Resolve-LogDir {
    param([string]$AppExe)
    # Порядок важен: dev-сборка пишет рядом с exe, установленная — рядом с exe
    # или в LocalAppData. Возвращаем оба кандидата, поиск идет по обоим.
    $sibling = Join-Path (Split-Path -Parent $AppExe) "logs"
    $local = Join-Path $env:LOCALAPPDATA "KTools\logs"
    return @($sibling, $local)
}

function Find-SessionLog {
    param([string[]]$LogDirs, [datetime]$Since)
    foreach ($dir in $LogDirs) {
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        $log = Get-ChildItem -LiteralPath $dir -Filter "ktools_*.log" -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $Since } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($null -ne $log) { return $log.FullName }
    }
    return $null
}

function Test-LogContains {
    param([string]$Path, [string[]]$Markers)
    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    try {
        $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
        try {
            $reader = New-Object IO.StreamReader($stream)
            while ($null -ne ($line = $reader.ReadLine())) {
                foreach ($marker in $Markers) {
                    if ($marker -ne "" -and $line -like ("*" + $marker + "*")) { return $true }
                }
            }
        } finally { $reader.Dispose(); $stream.Dispose() }
    } catch { }
    return $false
}

function Stop-AppInstance {
    $procs = Get-Process -Name "KTools.App" -ErrorAction SilentlyContinue
    foreach ($proc in $procs) {
        try { $null = $proc.CloseMainWindow() } catch { }
    }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $left = Get-Process -Name "KTools.App" -ErrorAction SilentlyContinue
        if ($null -eq $left) { return }
        Start-Sleep -Milliseconds 300
    }
    $left = Get-Process -Name "KTools.App" -ErrorAction SilentlyContinue
    foreach ($proc in $left) {
        try { Stop-Process -InputObject $proc -Force } catch { }
    }
    Start-Sleep -Seconds 2
}

function Wait-MainWindow {
    param([int]$PidToWatch, [int]$TimeoutSec)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $proc = Get-Process -Id $PidToWatch -ErrorAction SilentlyContinue
        if ($null -eq $proc) { return $null }
        try {
            if ($proc.MainWindowHandle -ne 0) { return (Get-Date) }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Wait-LogMarker {
    param([string[]]$LogDirs, [datetime]$Since, [string[]]$Markers, [int]$BudgetSec = 30)
    $deadline = (Get-Date).AddSeconds($BudgetSec)
    while ((Get-Date) -lt $deadline) {
        $logPath = Find-SessionLog -LogDirs $LogDirs -Since $Since
        if ($null -ne $logPath -and (Test-LogContains -Path $logPath -Markers $Markers)) {
            return @{ At = (Get-Date); Log = $logPath }
        }
        Start-Sleep -Milliseconds 300
    }
    $logPath = Find-SessionLog -LogDirs $LogDirs -Since $Since
    if ($null -ne $logPath) { return @{ At = $null; Log = $logPath } }
    return $null
}

$exe = Resolve-AppExe -Hint $ExePath
$logDirs = Resolve-LogDir -AppExe $exe
if ($OutDir -eq "") { $OutDir = Join-Path $PSScriptRoot "results" }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Write-Host "EXE: $exe"
Write-Host ("Логи: {0}" -f ($logDirs -join "; "))

$rows = @()
for ($i = 1; $i -le $Runs; $i++) {
    Stop-AppInstance
    $t0 = Get-Date
    $proc = Start-Process -FilePath $exe -PassThru
    $handleAt = Wait-MainWindow -PidToWatch $proc.Id -TimeoutSec $TimeoutSec
    if ($null -eq $handleAt) {
        Write-Warning "Прогон ${i}: окно не появилось за $TimeoutSec с"
        Stop-AppInstance
        continue
    }
    $tHandle = ($handleAt - $t0).TotalSeconds

    $peakMb = 0
    $settleEnd = (Get-Date).AddSeconds($SettleSec)
    while ((Get-Date) -lt $settleEnd) {
        $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
        if ($null -ne $p) {
            try {
                $mb = $p.WorkingSet64 / 1MB
                if ($mb -gt $peakMb) { $peakMb = $mb }
            } catch { }
        }
        Start-Sleep -Milliseconds 250
    }

    $marker = Wait-LogMarker -LogDirs $logDirs -Since $t0 -Markers ($ReadyMarker -split ",")
    $tMarker = $null
    $sessionLog = ""
    if ($null -ne $marker) {
        if ($null -ne $marker.At) { $tMarker = ($marker.At - $t0).TotalSeconds }
        $sessionLog = $marker.Log
    }

    Stop-AppInstance
    $markerSeconds = $null
    if ($null -ne $tMarker) { $markerSeconds = [math]::Round($tMarker, 2) }
    $row = [pscustomobject]@{
        Run = $i
        SecondsToWindow = [math]::Round($tHandle, 2)
        SecondsToMarker = $markerSeconds
        PeakWorkingSetMb = [math]::Round($peakMb, 1)
        SessionLog = $sessionLog
    }
    $rows += $row
    Write-Host ("Прогон {0}: окно {1} с, маркер {2}, пик WS {3} МБ" -f $i, $row.SecondsToWindow, $row.SecondsToMarker, $row.PeakWorkingSetMb)
}

if ($rows.Count -eq 0) { throw "Нет успешных прогонов." }
$sorted = @($rows | Sort-Object SecondsToWindow | Select-Object -ExpandProperty SecondsToWindow)
$median = $sorted[[math]::Floor($sorted.Count / 2)]
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$rows | Export-Csv -LiteralPath (Join-Path $OutDir "startup-$stamp.csv") -NoTypeInformation -Encoding UTF8
@{ Exe = $exe; Runs = $rows.Count; MedianSecondsToWindow = $median; Rows = $rows } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutDir "startup-$stamp.json") -Encoding UTF8
Write-Host "Медиана до окна: $median с. Файлы: $OutDir\startup-$stamp.*"
