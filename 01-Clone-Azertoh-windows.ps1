# =====================================================
# TwoLifeCrew WotLK - Native Windows Edition
# Step 2: Clone AzerothCore + Modules (latest, not pinned)
# Run as Administrator (or normal user - git doesn't need elevation)
# =====================================================

$ErrorActionPreference = "Stop"

function Info($msg)  { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Ok($msg)    { Write-Host "[OK]   $msg" -ForegroundColor Green }
function Warn($msg)  { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Fail($msg)  { Write-Host "[FAIL] $msg" -ForegroundColor Red }

# --- Config ---
$AcoreDir = "C:\Azerothcore"

Info "TwoLifeCrew WotLK Native Windows Edition - Step 2: Clone"
Info "Target directory: $AcoreDir"
Write-Host ""

# --- Verify git is available, install if missing ---
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Warn "git not found in PATH - downloading and installing Git for Windows..."
    try {
        # Official Git for Windows release - direct download, not winget
        # (winget is unreliable on fresh VMs/Windows installs).
        $gitApiUrl = "https://api.github.com/repos/git-for-windows/git/releases/latest"
        $release = Invoke-RestMethod -Uri $gitApiUrl -UseBasicParsing
        $asset = $release.assets | Where-Object { $_.name -like "Git-*-64-bit.exe" } | Select-Object -First 1
        if (-not $asset) {
            Fail "Could not find a 64-bit Git installer in the latest release"
            exit 1
        }

        $gitInstaller = "$env:TEMP\$($asset.name)"
        Info "Downloading $($asset.name)..."
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $gitInstaller -UseBasicParsing

        Info "Installing Git for Windows (silent)..."
        Start-Process -FilePath $gitInstaller -ArgumentList "/VERYSILENT", "/NORESTART", "/NOCANCEL", "/SP-" -Wait
        Remove-Item $gitInstaller -Force -ErrorAction SilentlyContinue

        # Refresh PATH in this session so git is found without reopening the shell.
        $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
        $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
        $env:Path = "$machinePath;$userPath"

        if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
            Fail "Git installed but not found on PATH - close and reopen PowerShell, then re-run this script"
            exit 1
        }
        Ok "Git for Windows installed successfully"
    } catch {
        Fail "Git installation failed: $_"
        Fail "Install manually from https://git-scm.com/download/win and re-run this script"
        exit 1
    }
}
Ok "git found: $(git --version)"

# --- Clone AzerothCore (Playerbot branch, latest HEAD) ---
if (Test-Path "$AcoreDir\.git") {
    Ok "AzerothCore already cloned at $AcoreDir - skipping"
} else {
    if (Test-Path $AcoreDir) {
        Warn "Removing incomplete directory $AcoreDir..."
        Remove-Item -Recurse -Force $AcoreDir
    }
    Info "Cloning AzerothCore (Playerbot branch, latest)..."
    git clone https://github.com/mod-playerbots/azerothcore-wotlk.git --branch Playerbot $AcoreDir
    if ($LASTEXITCODE -ne 0) {
        Fail "git clone failed (exit $LASTEXITCODE) - check your network connection and retry"
        exit 1
    }
    Ok "AzerothCore cloned (latest Playerbot branch)"
}

# --- Clone modules ---
$ModulesDir = "$AcoreDir\modules"
if (-not (Test-Path $ModulesDir)) {
    New-Item -ItemType Directory -Force -Path $ModulesDir | Out-Null
}

function Clone-Module {
    param(
        [string]$Url
    )
    $name = [System.IO.Path]::GetFileNameWithoutExtension($Url)
    $modPath = "$ModulesDir\$name"

    if (Test-Path "$modPath\.git") {
        Info "$name already exists - skipping"
        return
    }
    if (Test-Path $modPath) {
        Remove-Item -Recurse -Force $modPath
    }

    Push-Location $ModulesDir
    git clone $Url
    $cloneExit = $LASTEXITCODE
    Pop-Location

    if ($cloneExit -ne 0) {
        Fail "$name : git clone failed (exit $cloneExit) - check your network connection and retry"
        exit 1
    }

    Push-Location $modPath
    $sha = git rev-parse --short HEAD 2>$null
    Pop-Location
    Ok "$name -> $sha (latest)"
}

Info "Cloning modules (latest HEAD, matching branch defaults)..."
Write-Host ""

Clone-Module "https://github.com/mod-playerbots/mod-playerbots.git"
Clone-Module "https://github.com/Hokken/mod-llm-chatter.git"
Clone-Module "https://github.com/ZhengPeiRu21/mod-reagent-bank.git"
Clone-Module "https://github.com/noisiver/mod-junk-to-gold.git"
Clone-Module "https://github.com/BytesGalore/mod-no-hearthstone-cooldown.git"
# mod-transmog INTENTIONALLY EXCLUDED - incompatible with mod-playerbots
# PS_Transmogrification::OnPlayerLootItem crashes when bots loot items (same as Linux edition)
Clone-Module "https://github.com/noisiver/mod-assistant.git"
Clone-Module "https://github.com/azerothcore/mod-npc-all-mounts.git"
Clone-Module "https://github.com/NathanHandley/mod-ah-bot-plus.git"

Write-Host ""
Ok "Step 2 complete - AzerothCore + all modules cloned (latest)"
Write-Host ""
Info "Module list:"
Get-ChildItem -Path $ModulesDir -Directory | ForEach-Object {
    Push-Location $_.FullName
    $sha = git rev-parse --short HEAD 2>$null
    Pop-Location
    Write-Host "  $($_.Name) -> $sha"
}
Write-Host ""
Info "Next: Step 3 - Apply source patches for MSVC compatibility"