# =====================================================
# Build WOTLK-Menu.exe from 08-wotlk-menu.cs
# Pirate Fleet Edition - zero-dependency build
#
# Uses the C# compiler that SHIPS WITH WINDOWS
# (.NET Framework 4.x csc.exe) - nothing to install.
# If Visual Studio Build Tools are present (you have them
# for AzerothCore anyway), its newer Roslyn csc is
# preferred for nicer error messages.
#
# Usage:  powershell -ExecutionPolicy Bypass -File 07-build-wotlk-menu.ps1
# =====================================================
$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "08-wotlk-menu.cs"
$out = Join-Path $PSScriptRoot "WOTLK-Menu.exe"
if (-not (Test-Path $src)) {
    Write-Host "[FAIL] 08-wotlk-menu.cs not found next to this script" -ForegroundColor Red
    exit 1
}

# -- Locate a C# compiler --------------------------------------------------
$csc = $null
# 1) Roslyn csc via VS Build Tools (optional, nicer diagnostics)
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vsPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
    if ($vsPath) {
        $rcsc = Get-ChildItem "$vsPath\MSBuild" -Recurse -Filter csc.exe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($rcsc) { $csc = $rcsc.FullName }
    }
}
# 2) Built-in .NET Framework compiler (always present on Win10/11)
if (-not $csc) {
    foreach ($fw in @(
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    )) {
        if (Test-Path $fw) { $csc = $fw; break }
    }
}
if (-not $csc) {
    Write-Host "[FAIL] No C# compiler found - is .NET Framework 4.x installed?" -ForegroundColor Red
    exit 1
}
Write-Host "[INFO] Compiler: $csc" -ForegroundColor Cyan

# -- Icon: fetch wowmenu.ico from Gitea if not already next to this script -
# Auto-download so the build is self-sufficient - no manual step needed to
# drop the icon file in place before running this script.
$iconArgs = @()
$icon = Join-Path $PSScriptRoot "wowmenu.ico"
if (-not (Test-Path $icon)) {
    Write-Host "[INFO] wowmenu.ico not found - downloading from the fleet..." -ForegroundColor Cyan
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $iconUrl = "https://gitea.com/Eldesan/wotlk-windows-launcher/raw/branch/main/wowmenu.ico/wowmenu.ico"
        Invoke-WebRequest -Uri $iconUrl -OutFile $icon -UseBasicParsing -UserAgent "Mozilla/5.0"
        Write-Host "[OK] Downloaded wowmenu.ico" -ForegroundColor Green
    } catch {
        Write-Host "[WARN] Could not download wowmenu.ico - building without an icon: $_" -ForegroundColor Yellow
    }
}
if (Test-Path $icon) {
    $iconArgs = @("/win32icon:$icon")
    Write-Host "[INFO] Using icon: wowmenu.ico" -ForegroundColor Cyan
}

# -- Compile -----------------------------------------------------------------
& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    @iconArgs `
    /out:"$out" "$src"
if ($LASTEXITCODE -eq 0 -and (Test-Path $out)) {
    Write-Host "[OK] Built: $out" -ForegroundColor Green
    Write-Host "[INFO] Fair winds captain - double-click to sail!" -ForegroundColor Cyan
} else {
    Write-Host "[FAIL] Build failed" -ForegroundColor Red
    exit 1
}