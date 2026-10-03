# =====================================================
# Stage 05: Ollama Setup
# TwoLifeCrew WotLK Windows Installer
# =====================================================

$ErrorActionPreference = "Stop"

function Info($m) { Write-Host "  [INFO] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [WARN] $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "  [FAIL] $m" -ForegroundColor Red }

Info "Stage 05: Ollama Setup"
Write-Host ""

# Check if Ollama is already installed
$ollamaPath = [System.IO.Path]::Combine($env:LOCALAPPDATA, "Programs\Ollama\ollama.exe")
if (Test-Path $ollamaPath) {
    Ok "Ollama already installed"
    exit 0
}

Info "Installing Ollama via the official install script..."
Info "(irm https://ollama.com/install.ps1 | iex - signed by Ollama Inc, verified before running)"

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $installScript = Invoke-RestMethod -Uri "https://ollama.com/install.ps1" -UseBasicParsing
    Invoke-Expression $installScript
} catch {
    Fail "Ollama install script failed: $_"
    Fail "Install manually: irm https://ollama.com/install.ps1 | iex (run PowerShell as Administrator)"
    exit 1
}

Start-Sleep -Seconds 3

if (Test-Path $ollamaPath) {
    Ok "Ollama installed successfully"
} else {
    Fail "Ollama installation completed but ollama.exe not found at $ollamaPath"
    Fail "Check if it installed to a different location, or install manually"
    exit 1
}

Info "Stage 05 complete"
Write-Host ""
Info "Model selection + pulling is handled by WOTLK-Menu.exe's LLM Bridge tab"
Info "(pick from already-installed models, or pull a new one - any model works)"