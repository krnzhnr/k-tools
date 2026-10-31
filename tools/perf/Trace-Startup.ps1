#Requires -Version 5.1
<#
.SYNOPSIS
    ETW-трейс старта K-Tools через WPR: запуски процессов и файловый ввод-вывод.
.DESCRIPTION
    Требует запуска от администратора (ограничение WPR). Поднимает WPR-профили,
    запускает приложение, ждет появления главного окна, останавливает запись,
    конвертирует ETL в XML через tracerpt и считает сводку через Summarize-Etl.py.
    Дает истину для приемки блоков 04/05: какие процессы стартовали и сколько
    файловых операций пришлось на старт.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/perf/Trace-Startup.ps1
#>
[CmdletBinding()]
param(
    [string]$ExePath = "",
    [string]$Profiles = "FileIO,DiskIO",
    [int]$TimeoutSec = 120,
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Нужен запуск от администратора: WPR не пишет трейсы без elevated-прав."
}

if ($ExePath -eq "") {
    foreach ($candidate in @(
        "$env:ProgramFiles\KTools\KTools.App.exe",
        "${env:ProgramFiles(x86)}\KTools\KTools.App.exe")) {
        if (Test-Path -LiteralPath $candidate) { $ExePath = $candidate; break }
    }
    if ($ExePath -eq "") {
        $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        $found = Get-ChildItem -LiteralPath (Join-Path $repoRoot "KTools.App\bin") -Recurse -Filter "KTools.App.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "Release" } |
            Sort-Object @{ Expression = { if ($_.FullName -match "publish") { 0 } else { 1 } }; Descending = $false }, @{ Expression = "LastWriteTime"; Descending = $true } |
            Select-Object -First 1
        if ($null -eq $found) { throw "KTools.App.exe не найден. Укажите -ExePath." }
        $ExePath = $found.FullName
    }
}
if ($OutDir -eq "") { $OutDir = Join-Path $PSScriptRoot "results" }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$etl = Join-Path $OutDir "startup-$stamp.etl"
$xml = Join-Path $OutDir "startup-$stamp.xml"

foreach ($proc in (Get-Process -Name "KTools.App" -ErrorAction SilentlyContinue)) {
    try { $null = $proc.CloseMainWindow() } catch { }
}
Start-Sleep -Seconds 3
foreach ($proc in (Get-Process -Name "KTools.App" -ErrorAction SilentlyContinue)) {
    try { Stop-Process -InputObject $proc -Force } catch { }
}
Start-Sleep -Seconds 2

$startArgs = @()
foreach ($profile in ($Profiles -split ",")) {
    $name = $profile.Trim()
    if ($name -ne "") { $startArgs += "-start"; $startArgs += $name }
}
Write-Host "WPR старт: $Profiles"
& wpr.exe @startArgs
if ($LASTEXITCODE -ne 0) { throw "WPR не стартовал (код $LASTEXITCODE)." }

try {
    $t0 = Get-Date
    $proc = Start-Process -FilePath $ExePath -PassThru
    $deadline = $t0.AddSeconds($TimeoutSec)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
        if ($null -ne $p -and $p.MainWindowHandle -ne 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 200
    }
    Start-Sleep -Seconds 5
    if (-not $ready) { Write-Warning "Окно не появилось за $TimeoutSec с, трейс все равно останавливается." }
} finally {
    Write-Host "WPR стоп..."
    & wpr.exe -stop $etl
}

foreach ($proc in (Get-Process -Name "KTools.App" -ErrorAction SilentlyContinue)) {
    try { $null = $proc.CloseMainWindow() } catch { }
}

Write-Host "Конвертация ETL в XML (может занять минуты на больших трейсах)..."
& tracerpt.exe $etl -o $xml -of XML
if ($LASTEXITCODE -ne 0) { throw "tracerpt не сконвертировал трейс (код $LASTEXITCODE)." }
Write-Host "ETL: $etl"
Write-Host "XML: $xml"
& py -3 (Join-Path $PSScriptRoot "Summarize-Etl.py") $xml --root-pid $proc.Id
