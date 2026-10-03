# =====================================================================#
# TwoLifeCrew WotLK - Native Windows Edition                           #
# Step: LLM Chatter Bridge - Python venv setup                         # 
# Run as Administrator (or as your normal user - no admin needed here) #
# =====================================================================#
$ErrorActionPreference = "Stop"
$InstallDir = "C:\Azerothcore"
$ModuleZipUrl = "https://raw.githubusercontent.com/Nallak/Wotlk-Playerbot-LLmchatter-WIndows-Launcher/main/mod-llm-chatter/mod-llm-chatter.zip"
$BridgeDir = "$InstallDir\modules\mod-llm-chatter\tools"

function Info($msg)  { Write-Host "  [INFO] $msg" -ForegroundColor Cyan }
function Ok($msg)    { Write-Host "  [OK]   $msg" -ForegroundColor Green }
function Warn($msg)  { Write-Host "  [WARN] $msg" -ForegroundColor Yellow }
function Fail($msg)  { Write-Host "  [FAIL] $msg" -ForegroundColor Red }

Info "TwoLifeCrew WotLK Native Windows Edition - LLM Bridge Setup"
Write-Host ""

# -- Download mod-llm-chatter if missing (precompiled installs don't have it) --
if (-not (Test-Path $BridgeDir)) {
    Info "mod-llm-chatter not found - downloading from the fleet..."
    try {
        $zipPath = "$env:TEMP\mod-llm-chatter.zip"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $retries = 5
        for ($i = 1; $i -le $retries; $i++) {
            try {
                Invoke-WebRequest -Uri $ModuleZipUrl -OutFile $zipPath -UseBasicParsing -UserAgent "Mozilla/5.0"
                break
            } catch {
                if ($i -eq $retries) { throw }
                $waitSec = $i * 5
                Warn "Download failed (attempt $i/$retries) - retrying in ${waitSec}s..."
                Start-Sleep -Seconds $waitSec
            }
        }
        Ok "Downloaded mod-llm-chatter.zip"

        $modulesDir = "$InstallDir\modules"
        New-Item -ItemType Directory -Path $modulesDir -Force | Out-Null
        Expand-Archive -Path $zipPath -DestinationPath $modulesDir -Force
        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
        Ok "Extracted to $modulesDir\mod-llm-chatter"
    } catch {
        Fail "Could not download/extract mod-llm-chatter: $_"
        exit 1
    }
}

if (-not (Test-Path $BridgeDir)) {
    Fail "mod-llm-chatter tools folder still not found at $BridgeDir"
    exit 1
}
Ok "mod-llm-chatter tools folder found"

# -- Check Python ----------------------------------------------------------
# NOTE: Windows ships a fake 'python.exe' App Execution Alias stub that
# intercepts the command and just nags to install from the Store - it sits
# ahead of a real install in PATH. We bypass it entirely by searching known
# install locations directly rather than trusting 'python' name resolution.
function Find-RealPython {
    $paths = @(
        "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe",
        "$env:LOCALAPPDATA\Programs\Python\Python313\python.exe",
        "$env:LOCALAPPDATA\Programs\Python\Python311\python.exe",
        "C:\Python312\python.exe",
        "C:\Python313\python.exe",
        "C:\Python311\python.exe",
        "C:\Program Files\Python312\python.exe",
        "C:\Program Files\Python313\python.exe",
        "C:\Program Files\Python311\python.exe"
    )
    foreach ($p in $paths) {
        if (Test-Path $p) { return $p }
    }
    # Fallback: glob search in case of an unlisted version
    $found = Get-ChildItem "C:\Program Files\Python3*\python.exe" -ErrorAction SilentlyContinue
    if ($found) { return $found[0].FullName }
    return $null
}

$PythonExe = Find-RealPython
if (-not $PythonExe) {
    Info "Python not found - installing from python.org (skipping winget - unreliable on some VMs)..."
    try {
        $pyInstaller = "$env:TEMP\python-installer.exe"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri "https://www.python.org/ftp/python/3.12.7/python-3.12.7-amd64.exe" -OutFile $pyInstaller -UseBasicParsing
        Start-Process -FilePath $pyInstaller -ArgumentList "/quiet InstallAllUsers=1 PrependPath=1 Include_test=0" -Wait
        Start-Sleep -Seconds 10
        Remove-Item $pyInstaller -Force -ErrorAction SilentlyContinue
        $PythonExe = Find-RealPython
    } catch {
        Warn "Direct python.org install failed: $_"
    }
}

if (-not $PythonExe) {
    Fail "Python still not found after install - install manually from python.org"
    Fail "Also check: Settings > Apps > Advanced app settings > App execution aliases - disable the 'python.exe' stub if present"
    exit 1
}
Ok "Python found: $PythonExe ($(& $PythonExe --version))"

# -- Create venv -------------------------------------------------------------
Set-Location $BridgeDir
if (Test-Path "$BridgeDir\venv") {
    Ok "venv already exists"
} else {
    Info "Creating virtual environment..."
    & $PythonExe -m venv venv
    if (Test-Path "$BridgeDir\venv\Scripts\python.exe") {
        Ok "venv created"
    } else {
        Fail "venv creation failed - check the output above"
        exit 1
    }
}

# -- Install requirements ----------------------------------------------------
Info "Installing requirements..."
$venvPython = "$BridgeDir\venv\Scripts\python.exe"
$venvPip    = "$BridgeDir\venv\Scripts\pip.exe"

if (Test-Path "$BridgeDir\requirements.txt") {
    & $venvPip install -r requirements.txt -q
    Ok "Requirements installed"
} else {
    Warn "requirements.txt not found in $BridgeDir - skipping"
}

Write-Host ""
Ok "LLM bridge Python environment ready!"
Write-Host ""
Info "Bridge script: $BridgeDir\llm_chatter_bridge.py"
Info "Venv python:   $venvPython"
Info "Start it via wotlk-menu.ps1 -> option 8 (Start LLM bridge)"
Write-Host ""