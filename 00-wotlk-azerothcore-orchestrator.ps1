# =====================================================
# TwoLifeCrew WotLK - AzerothCore Orchestrator
# Pirate Fleet Edition
#
# Runs the full proven self-compile chain in order:
#   01 -> 02 -> 03 -> 04 -> 05 -> 06 -> 07 (builds menu from 08)
#
# Downloads each stage script fresh from Gitea and runs it.
# Progress is saved - if a stage fails (e.g. a missing manual
# prerequisite from PREREQUISITES.md), fix the issue and just
# run this script again; it resumes from where it left off.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File 00-wotlk-azerothcore-orchestrator.ps1
#   powershell -ExecutionPolicy Bypass -File 00-wotlk-azerothcore-orchestrator.ps1 -Force
#   powershell -ExecutionPolicy Bypass -File 00-wotlk-azerothcore-orchestrator.ps1 -InstallDir "D:\MyServer"
#
# Install folder: the orchestrator asks where to install (Enter = C:\Azerothcore).
# Passing -InstallDir or setting WOTLK_INSTALL_DIR skips the question.
# =====================================================

param(
    [switch]$Force,
    [string]$InstallDir = ""
)

$ErrorActionPreference = "Stop"
# Install location: -InstallDir "D:\MyServer" or WOTLK_INSTALL_DIR skip the question.
# Otherwise the orchestrator asks below (Enter = the default C:\Azerothcore).
# Every stage runs as a child process and inherits the environment variables set here.
$DefaultInstallDir = "C:\Azerothcore"
$InstallDirGiven   = $false
if ($InstallDir) { $InstallDirGiven = $true }
elseif ($env:WOTLK_INSTALL_DIR) { $InstallDir = $env:WOTLK_INSTALL_DIR; $InstallDirGiven = $true }
else { $InstallDir = $DefaultInstallDir }
$InstallDir = $InstallDir.TrimEnd('\')
$env:WOTLK_INSTALL_DIR = $InstallDir
$BaseUrl    = "https://raw.githubusercontent.com/Nallak/Wotlk-Playerbot-LLmchatter-WIndows-Launcher/main"
$tmpDir     = "$env:TEMP\wotlk-stages-$(Get-Random)"
$StateFile  = Join-Path $PSScriptRoot ".wotlk-orchestrator-state"

function Info($m) { Write-Host "  [INFO] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [WARN] $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "  [FAIL] $m" -ForegroundColor Red }

# -- Dark terminal theme (Pirate Fleet style) ------------------------------
$Host.UI.RawUI.BackgroundColor = "Black"
$Host.UI.RawUI.ForegroundColor = "Gray"
try { Clear-Host } catch { }

# Disable Windows Console "QuickEdit Mode" - a stray mouse click/drag on
# the terminal window normally pauses the ENTIRE process until Enter is
# pressed, which looks exactly like a hung download at 100%. This is a
# well-known PowerShell gotcha, especially easy to trigger while recording
# a screen capture. Disabling it for this session prevents that entirely.
try {
    Add-Type -Name Console -Namespace Win32QuickEdit -MemberDefinition @'
[DllImport("kernel32.dll")]
public static extern IntPtr GetStdHandle(int nStdHandle);
[DllImport("kernel32.dll")]
public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
[DllImport("kernel32.dll")]
public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
'@ -ErrorAction SilentlyContinue

    $STD_INPUT_HANDLE = -10
    $ENABLE_QUICK_EDIT_MODE = 0x0040
    $ENABLE_EXTENDED_FLAGS = 0x0080

    $consoleHandle = [Win32QuickEdit.Console]::GetStdHandle($STD_INPUT_HANDLE)
    [uint32]$consoleMode = 0
    [Win32QuickEdit.Console]::GetConsoleMode($consoleHandle, [ref]$consoleMode) | Out-Null
    $consoleMode = $consoleMode -band (-bnot $ENABLE_QUICK_EDIT_MODE)
    $consoleMode = $consoleMode -bor $ENABLE_EXTENDED_FLAGS
    [Win32QuickEdit.Console]::SetConsoleMode($consoleHandle, $consoleMode) | Out-Null
} catch {
    # Non-fatal - if this fails for any reason, just proceed normally.
    # Worst case, the original click-to-pause behavior remains.
}

# Download with escalating-backoff retry - Gitea rate-limits rapid
# sequential requests with 502s, so persistence matters here.
function Get-FileWithRetry {
    param([string]$FileName, [string]$OutFile, [int]$Retries = 5)
    $uri = "$BaseUrl/$FileName"
    for ($i = 1; $i -le $Retries; $i++) {
        try {
            Invoke-WebRequest -Uri $uri -OutFile $OutFile -UseBasicParsing -UserAgent "Mozilla/5.0"
            return
        } catch {
            if ($i -eq $Retries) { throw }
            $waitSec = $i * 5
            Warn "Download failed (attempt $i/$Retries) - retrying in ${waitSec}s..."
            Start-Sleep -Seconds $waitSec
        }
    }
}

# Ask where to install. Enter keeps the default.
function Read-InstallDir {
    Write-Host ""
    Write-Host "  Where do you want to install the server?" -ForegroundColor White
    Write-Host "  Press Enter for the default: $DefaultInstallDir (recommended - the same layout as the official AzerothCore guide)" -ForegroundColor DarkGray
    Write-Host "  Or type a full path, for example D:\AzerothCore (a short path without spaces works best)." -ForegroundColor DarkGray
    Write-Host "  The folder is chosen now because AzerothCore builds its own folder path into the server." -ForegroundColor DarkGray
    while ($true) {
        $in = Read-Host "  Install folder [$DefaultInstallDir]"
        if ([string]::IsNullOrWhiteSpace($in)) { return $DefaultInstallDir }
        $p = $in.Trim().Trim('"').TrimEnd('\')
        if ($p -notmatch '^[A-Za-z]:\\.+') {
            Fail "Enter a full path that starts with a drive letter, like D:\AzerothCore (a drive on its own is not enough)"
            continue
        }
        if ($p -ieq $DefaultInstallDir) { return $DefaultInstallDir }
        if (-not (Test-Path "$($p.Substring(0,2))\")) {
            Fail "Drive $($p.Substring(0,2)) not found"
            continue
        }
        # Stage 01 clears a folder that has no AzerothCore clone in it - never point it at someone's files.
        if ((Test-Path -LiteralPath $p) -and -not (Test-Path -LiteralPath "$p\.git")) {
            $has = Get-ChildItem -LiteralPath $p -Force -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($has) {
                Fail "$p already contains files. Pick a new or empty folder (stage 01 clears a folder that has no AzerothCore clone in it)."
                continue
            }
        }
        $warns = @()
        if ($p -match '\s')       { $warns += "the path contains a space (untested - CMake or the build tools may fail)" }
        if ($p -match 'OneDrive') { $warns += "the path is inside OneDrive (untested - syncing thousands of build files can slow or break the build)" }
        if ($warns.Count -gt 0) {
            foreach ($w in $warns) { Warn $w }
            $yes = Read-Host "  Use it anyway? (y/N)"
            if ($yes -notmatch '^[Yy]') { continue }
        }
        return $p
    }
}

Write-Host ""
Write-Host "  ================================================" -ForegroundColor Red
Write-Host "   WOTLK AZEROTHCORE ORCHESTRATOR - Pirate Fleet Edition" -ForegroundColor Cyan
Write-Host "  ================================================" -ForegroundColor Red
Write-Host ""

# -- Admin check ------------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Fail "Must run as Administrator"
    exit 1
}

Info "Have you installed the prerequisites from PREREQUISITES.md?"
Info "(Git, Visual Studio + v143 toolset, CMake, OpenSSL, Boost, MySQL)"
Info "If not, stage 03-build.ps1 will fail with a clear message telling you what's missing."
Write-Host ""

# -- Progress state -----------------------------------------------------
$done = @()
$savedDir = $null
if ((Test-Path $StateFile) -and -not $Force) {
    $stateLines = @(Get-Content $StateFile | Where-Object { $_.Trim() -ne "" })
    $savedLine = $stateLines | Where-Object { $_ -like "InstallDir=*" } | Select-Object -First 1
    if ($savedLine) { $savedDir = $savedLine.Substring(11).Trim() }
    $done = @($stateLines | Where-Object { $_ -notlike "InstallDir=*" })
    if ($done.Count -gt 0) { Info "Resuming - already completed: $($done -join ', ')" }
}
if ($Force -and (Test-Path $StateFile)) {
    Remove-Item $StateFile -Force
    $done = @()
    Info "-Force: ignoring saved progress, running everything"
}

# -- Install folder -----------------------------------------------------
if ($InstallDirGiven) {
    Info "Install folder (from -InstallDir / WOTLK_INSTALL_DIR): $InstallDir"
    if ($savedDir -and ($savedDir -ine $InstallDir)) {
        Warn "The earlier run used $savedDir - use -Force to start over in the new folder"
    }
} elseif ($savedDir) {
    $InstallDir = $savedDir
    Info "Resuming with the install folder from the last run: $InstallDir"
} elseif ($done.Count -gt 0) {
    Info "Resuming an earlier run - install folder: $InstallDir"
} else {
    $InstallDir = Read-InstallDir
}
$InstallDir = $InstallDir.TrimEnd('\')
$env:WOTLK_INSTALL_DIR = $InstallDir
if (-not $savedDir -or ($savedDir -ine $InstallDir)) {
    Add-Content -Path $StateFile -Value "InstallDir=$InstallDir"
}
$buildShown = if ($env:WOTLK_BUILD_DIR) { $env:WOTLK_BUILD_DIR } elseif ($InstallDir -ieq $DefaultInstallDir) { "C:\Build" } else { "$InstallDir-Build" }
Info "Server folder: $InstallDir"
Info "Build folder:  $buildShown"
Write-Host ""

New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
Info "Working directory: $tmpDir"
Write-Host ""

# -- The proven stage chain -----------------------------------------------
$stages = @(
    "01-Clone-Azertoh-windows.ps1",
    "02-source-patches.ps1",
    "03-build.ps1",
    "04-setup-database.ps1",
    "05-ollama-setup.ps1",
    "06-llm-bridge-setup.ps1"
)

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$n = 0
foreach ($stage in $stages) {
    $n++
    if ($done -contains $stage) {
        Ok "[$n/$($stages.Count)] $stage - already done, skipping"
        continue
    }

    Write-Host ""
    Write-Host "  ================================================" -ForegroundColor Cyan
    Write-Host "   STAGE [$n/$($stages.Count)]  $stage" -ForegroundColor White
    Write-Host "  ================================================" -ForegroundColor Cyan
    Write-Host ""

    $path = Join-Path $tmpDir $stage
    Info "Downloading $stage..."
    try {
        Get-FileWithRetry -FileName $stage -OutFile $path
        Ok "Downloaded"
    } catch {
        Fail "Failed to download $stage : $_"
        exit 1
    }

    Info "Running $stage..."
    Write-Host ""
    $global:LASTEXITCODE = 0
    & powershell -NoProfile -ExecutionPolicy Bypass -File $path
    $stageExitCode = $LASTEXITCODE
    Write-Host ""

    if ($stageExitCode -ne 0) {
        Fail "$stage FAILED (exit $stageExitCode) - stopping here"
        Fail "Fix the issue above (check PREREQUISITES.md if this is stage 03),"
        Fail "then just run this orchestrator again - it will resume from here."
        exit 1
    }

    Ok "$stage complete"
    Add-Content -Path $StateFile -Value $stage

    if ($stage -eq "01-Clone-Azertoh-windows.ps1") {
        Info "Waiting for clone to settle..."
        Start-Sleep -Seconds 3
    }
}

# -- Build WOTLK-Menu.exe ---------------------------------------------------
Write-Host ""
Write-Host "  ================================================" -ForegroundColor Cyan
Write-Host "   Building WOTLK-Menu.exe" -ForegroundColor White
Write-Host "  ================================================" -ForegroundColor Cyan
Write-Host ""

try {
    $menuBuildPath = "$InstallDir\07-build-wotlk-menu.ps1"
    $menuCsPath    = "$InstallDir\08-wotlk-menu.cs"

    Info "Downloading menu builder + source..."
    Get-FileWithRetry -FileName "07-build-wotlk-menu.ps1" -OutFile $menuBuildPath
    Get-FileWithRetry -FileName "08-wotlk-menu.cs" -OutFile $menuCsPath
    Ok "Downloaded"

    Info "Building... (this also fetches wowmenu.ico automatically if missing)"
    Push-Location $InstallDir
    powershell -ExecutionPolicy Bypass -File $menuBuildPath
    Pop-Location

    if (Test-Path "$InstallDir\WOTLK-Menu.exe") {
        Ok "WOTLK-Menu.exe built successfully at $InstallDir\WOTLK-Menu.exe"
    } else {
        Warn "Menu build did not produce an exe - check output above"
    }
} catch {
    Warn "Menu build step failed: $_"
    Warn "You can build it manually later: run 07-build-wotlk-menu.ps1 from $InstallDir"
}

# -- Cleanup ------------------------------------------------------------
Info "Cleaning up..."
Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
Ok "Done"

Write-Host ""
Write-Host "  ================================================" -ForegroundColor Green
Write-Host "   ALL STAGES COMPLETE - Fair winds captain!" -ForegroundColor Green
Write-Host "  ================================================" -ForegroundColor Green
Write-Host ""
Info "Launch the manager: $InstallDir\WOTLK-Menu.exe"
Info "First steps: Start MariaDB/MySQL, Start World server, wait for AC> prompt,"
Info "then Start Auth server, then create your GM account from the Admin tab."
Write-Host ""
