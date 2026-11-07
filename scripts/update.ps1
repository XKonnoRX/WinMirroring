<#
.SYNOPSIS
    Обновляет WinMirroring: закрывает запущенное приложение, копирует новую сборку и запускает его снова.

.DESCRIPTION
    Обычные сценарии:
      - На ноутбуке из репозитория:      scripts\update.cmd -Build
        (собирает в artifacts\publish\win-x64-staging и обновляет запущенную копию)
      - На другом устройстве: скопируйте туда папку новой сборки и запустите update.cmd из неё
        (скрипт лежит в каждой сборке; обновляется та копия, что сейчас запущена)
      - Из общей папки или архива:       update.cmd -Source \\LAPTOP\share\win-x64-staging
                                         update.cmd -Source D:\Downloads\WinMirroring.zip

    Приложение закрывается по сигналу, как через «Выход» в трее (звук и виртуальный монитор
    возвращаются в исходное состояние). Версии старше этого скрипта сигнал не понимают, их
    приходится завершать принудительно: при следующем запуске они сами всё приводят в порядок.

.PARAMETER Source
    Папка с новой сборкой или .zip с ней. По умолчанию: папка этого скрипта, если в ней есть
    WinMirroring.exe, иначе artifacts\publish\win-x64-staging репозитория.

.PARAMETER Target
    Куда установить. По умолчанию: папка запущенного WinMirroring, иначе artifacts\publish\win-x64
    репозитория.

.PARAMETER Build
    Сначала собрать новую версию (dotnet publish) в папку Source. Только из репозитория.

.PARAMETER NoStart
    Не запускать приложение после обновления. Тогда закрывается только копия, запущенная из
    папки установки.

.PARAMETER DryRun
    Только показать, что будет сделано: ничего не закрывать, не копировать и не запускать.
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Target,
    [switch]$Build,
    [switch]$NoStart,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$ExeName = 'WinMirroring.exe'
$ProcessName = 'WinMirroring'
$ExitEventName = 'Local\WinMirroring.App.Exit'

$ScriptDir = $PSScriptRoot
$RepoRoot = Split-Path -Parent $ScriptDir
$InRepo = Test-Path (Join-Path $RepoRoot 'WinMirroring.slnx')

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

function Stop-WithError([string]$Text) {
    Write-Host "Ошибка: $Text" -ForegroundColor Red
    exit 1
}

# ---- what is running now ---------------------------------------------------------------------------

$RunningPath = $null
$RunningArgs = ''
$running = @(Get-CimInstance Win32_Process -Filter "Name='$ExeName'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -notmatch '--vdd-add-mode|--exit' })
if ($running.Count -gt 0) {
    $RunningPath = $running[0].ExecutablePath
    $commandLine = [string]$running[0].CommandLine
    if ($commandLine -match '^\s*("[^"]*"|\S+)\s*(.*)$') {
        $RunningArgs = ($Matches[2] -replace '--restarted', '').Trim()
    }

    if (-not $RunningPath) {
        Write-Host 'WinMirroring запущен с правами администратора: обновление тоже нужно запустить от имени администратора.' -ForegroundColor Yellow
    }
}

# ---- where from, where to ----------------------------------------------------------------------------

if (-not $Source) {
    if (Test-Path (Join-Path $ScriptDir $ExeName)) {
        $Source = $ScriptDir
    }
    elseif ($InRepo) {
        $Source = Join-Path $RepoRoot 'artifacts\publish\win-x64-staging'
    }
    else {
        Stop-WithError 'не указана папка с новой сборкой (-Source).'
    }
}

if (-not $Target) {
    if ($RunningPath) {
        $Target = Split-Path -Parent $RunningPath
    }
    elseif ($InRepo) {
        $Target = Join-Path $RepoRoot 'artifacts\publish\win-x64'
    }
    else {
        Stop-WithError 'WinMirroring не запущен, поэтому непонятно, куда ставить: укажите -Target.'
    }
}

if ($Build) {
    if (-not $InRepo) {
        Stop-WithError '-Build работает только из репозитория (scripts\update.cmd).'
    }

    Write-Step "Сборка в $Source"
    & dotnet publish (Join-Path $RepoRoot 'src\WinMirroring.App\WinMirroring.App.csproj') -c Release -r win-x64 --self-contained true -o $Source
    if ($LASTEXITCODE -ne 0) {
        Stop-WithError 'сборка не удалась.'
    }
}

$Extracted = $null
if ($Source -like '*.zip') {
    if (-not (Test-Path $Source)) {
        Stop-WithError "нет архива $Source."
    }

    Write-Step "Распаковываю $Source"
    $Extracted = Join-Path $env:TEMP ('WinMirroring-update-' + [guid]::NewGuid().ToString('N'))
    Expand-Archive -Path $Source -DestinationPath $Extracted
    $exe = Get-ChildItem -Path $Extracted -Recurse -Filter $ExeName | Select-Object -First 1
    if (-not $exe) {
        Stop-WithError "в архиве нет $ExeName."
    }

    $Source = $exe.DirectoryName
}

if (-not (Test-Path (Join-Path $Source $ExeName))) {
    Stop-WithError "в папке $Source нет $ExeName."
}

$SourceFull = (Resolve-Path $Source).Path.TrimEnd('\')
$TargetFull = [System.IO.Path]::GetFullPath($Target).TrimEnd('\')
if ($SourceFull -ieq $TargetFull) {
    Stop-WithError "новая сборка и установка в одной папке ($TargetFull). Запустите update из папки с новой сборкой или укажите -Source."
}

# /MIR deletes everything else in the target: only allow an empty folder or an existing installation.
if ((Test-Path $TargetFull) -and (Get-ChildItem -Path $TargetFull -Force | Select-Object -First 1) -and -not (Test-Path (Join-Path $TargetFull $ExeName))) {
    Stop-WithError "в $TargetFull есть файлы, но нет $ExeName. Это не похоже на папку WinMirroring, туда ставить не буду."
}

# ---- close, copy, start --------------------------------------------------------------------------

# Close what is in the way: a copy running from the target (its files are locked) and, when the new version is
# to be started, any copy at all (only one may run).
$processes = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object {
        $path = $null
        try { $path = $_.Path } catch { }
        (-not $NoStart) -or (-not $path) -or $path.StartsWith($TargetFull + '\', [System.StringComparison]::OrdinalIgnoreCase)
    })

if ($DryRun) {
    Write-Host "Новая сборка:  $SourceFull"
    Write-Host "Установка в:   $TargetFull"
    Write-Host ("Будет закрыто: " + $(if ($processes.Count -gt 0) { ($processes | ForEach-Object { "PID $($_.Id)" }) -join ', ' } else { 'ничего' }))
    Write-Host ("Запуск после:  " + $(if ($NoStart) { 'нет' } else { "$TargetFull\$ExeName $(("$RunningArgs --restarted").Trim())" }))
    exit 0
}

if ($processes.Count -gt 0) {
    Write-Step 'Закрываю WinMirroring'
    $signalled = $false
    try {
        $exitEvent = [System.Threading.EventWaitHandle]::OpenExisting($ExitEventName)
        [void]$exitEvent.Set()
        $exitEvent.Dispose()
        $signalled = $true
    }
    catch {
        # older versions have no exit signal
    }

    if ($signalled) {
        $processes | Wait-Process -Timeout 20 -ErrorAction SilentlyContinue
    }

    $left = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) {
        if ($signalled) {
            Write-Host 'Приложение не закрылось само за 20 с, завершаю его принудительно.' -ForegroundColor Yellow
        }
        else {
            Write-Host 'Эта версия не умеет закрываться по сигналу, завершаю её принудительно (следующий запуск сам вернёт звук и уберёт виртуальный монитор).' -ForegroundColor Yellow
        }

        try {
            $left | Stop-Process -Force -ErrorAction Stop
        }
        catch {
            Stop-WithError "не удалось завершить WinMirroring: $($_.Exception.Message) Если он запущен от имени администратора, запустите обновление тоже от имени администратора."
        }

        $left | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
}

Write-Step "Копирую $SourceFull -> $TargetFull"
New-Item -ItemType Directory -Force -Path $TargetFull | Out-Null
$code = 16
for ($attempt = 1; $attempt -le 3; $attempt++) {
    & robocopy $SourceFull $TargetFull /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    $code = $LASTEXITCODE
    if ($code -lt 8) {
        break
    }

    Start-Sleep -Seconds 2
}

if ($Extracted) {
    Remove-Item -Path $Extracted -Recurse -Force -ErrorAction SilentlyContinue
}

if ($code -ge 8) {
    Stop-WithError "копирование не удалось (robocopy, код $code). Файлы могли остаться частично обновлены: запустите обновление ещё раз."
}

$dll = Get-Item (Join-Path $TargetFull 'WinMirroring.dll') -ErrorAction SilentlyContinue
if ($dll) {
    Write-Host "Установлена сборка от $($dll.LastWriteTime.ToString('dd.MM.yyyy HH:mm'))"
}

if (-not $NoStart) {
    Write-Step 'Запускаю WinMirroring'
    $arguments = ("$RunningArgs --restarted").Trim()
    Start-Process -FilePath (Join-Path $TargetFull $ExeName) -ArgumentList $arguments -WorkingDirectory $TargetFull
}

Write-Host 'Готово.' -ForegroundColor Green
