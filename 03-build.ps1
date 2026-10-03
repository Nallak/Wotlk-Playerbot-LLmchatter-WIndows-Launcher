# =====================================================
# TwoLifeCrew WotLK - Native Windows Edition
# Step 4: CMake Configure + MSBuild Compile
# Run as Administrator (first build can take 20-60+ min;
# later incremental builds after adding a module are much
# faster, same as the Linux menu's plain "make" recompile)
# =====================================================

$ErrorActionPreference = "Stop"
$AcoreDir   = "C:\Azerothcore"
$BuildDir   = "C:\Build"
$InstallDir = "C:\Azerothcore"

function Info($msg)  { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Ok($msg)    { Write-Host "[OK]   $msg" -ForegroundColor Green }
function Warn($msg)  { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Fail($msg)  { Write-Host "[FAIL] $msg" -ForegroundColor Red }

Info "TwoLifeCrew WotLK Native Windows Edition - Step 4: Build"
Write-Host ""

# --- Pre-flight checks ---------------------------------------------------
if (-not (Test-Path "$AcoreDir\.git")) {
    Fail "AzerothCore not found at $AcoreDir. Run Step 2 first."
    exit 1
}

if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
    Warn "cmake not found in PATH - downloading and installing CMake..."
    try {
        # Official CMake release via GitHub - direct download, not winget
        $cmakeApiUrl = "https://api.github.com/repos/Kitware/CMake/releases/latest"
        $release = Invoke-RestMethod -Uri $cmakeApiUrl -UseBasicParsing
        $asset = $release.assets | Where-Object { $_.name -like "cmake-*-windows-x86_64.msi" } | Select-Object -First 1
        if (-not $asset) {
            Fail "Could not find a Windows x64 MSI in the latest CMake release"
            exit 1
        }

        $cmakeInstaller = "$env:TEMP\$($asset.name)"
        Info "Downloading $($asset.name)..."
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $cmakeInstaller -UseBasicParsing

        Info "Installing CMake (silent, adding to system PATH)..."
        Start-Process -FilePath "msiexec.exe" -ArgumentList "/i", "`"$cmakeInstaller`"", "/quiet", "/norestart", "ADD_CMAKE_TO_PATH=System" -Wait
        Remove-Item $cmakeInstaller -Force -ErrorAction SilentlyContinue

        # Refresh PATH in this session so cmake is found without reopening the shell.
        $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
        $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
        $env:Path = "$machinePath;$userPath"

        if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
            Fail "CMake installed but not found on PATH - close and reopen PowerShell, then re-run this script"
            exit 1
        }
        Ok "CMake installed successfully"
    } catch {
        Fail "CMake installation failed: $_"
        Fail "Install manually from https://cmake.org/download/ and re-run this script"
        exit 1
    }
}
Ok "cmake found: $(cmake --version | Select-Object -First 1)"

$vsWhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vsWhere)) {
    Warn "Visual Studio Build Tools not found - downloading and installing..."
    Warn "This is a large download (several GB) and can take a while."
    try {
        $vsBootstrapper = "$env:TEMP\vs_buildtools.exe"
        Info "Downloading VS Build Tools bootstrapper..."
        Invoke-WebRequest -Uri "https://aka.ms/vs/17/release/vs_buildtools.exe" -OutFile $vsBootstrapper -UseBasicParsing

        Info "Installing Visual Studio Build Tools (C++ workload, silent)..."
        Info "This can take 20-40+ minutes depending on connection speed."
        $vsArgs = @(
            "--quiet", "--wait", "--norestart", "--nocache",
            "--add", "Microsoft.VisualStudio.Workload.VCTools",
            "--includeRecommended"
        )
        $proc = Start-Process -FilePath $vsBootstrapper -ArgumentList $vsArgs -Wait -PassThru
        Remove-Item $vsBootstrapper -Force -ErrorAction SilentlyContinue

        # VS installer exit codes: 0 = success, 3010 = success but reboot needed
        if ($proc.ExitCode -ne 0 -and $proc.ExitCode -ne 3010) {
            Fail "VS Build Tools installer exited with code $($proc.ExitCode)"
            exit 1
        }
        if ($proc.ExitCode -eq 3010) {
            Warn "VS Build Tools installed but a reboot is recommended before building"
        }

        if (-not (Test-Path $vsWhere)) {
            Fail "VS Build Tools install completed but vswhere.exe still not found at $vsWhere"
            exit 1
        }
        Ok "Visual Studio Build Tools installed successfully"
    } catch {
        Fail "VS Build Tools installation failed: $_"
        Fail "Install manually from https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022"
        Fail "Select the 'Desktop development with C++' workload, then re-run this script"
        exit 1
    }
}
$vsInstall = & $vsWhere -latest -products * -property installationPath
if (-not $vsInstall) {
    Fail "No Visual Studio / Build Tools installation found via vswhere."
    exit 1
}
Ok "Visual Studio Build Tools found: $vsInstall"

# --- Locate Boost ----------------------------------------------------------
$boostRoot = Get-ChildItem -Path "C:\local" -Directory -Filter "boost_*" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $boostRoot) {
    Fail "Boost not found under C:\local. Install Boost (msvc, 64-bit) first."
    Fail "Download from https://sourceforge.net/projects/boost/files/boost-binaries/"
    Fail "Install to C:\local, then re-run this script."
    exit 1
}
$BoostRoot = $boostRoot.FullName
$BoostLibDir = Get-ChildItem -Path $BoostRoot -Directory -Filter "lib64-msvc-*" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $BoostLibDir) {
    Fail "Boost lib directory (lib64-msvc-*) not found under $BoostRoot."
    exit 1
}
Ok "Boost found: $BoostRoot (libs: $($BoostLibDir.Name))"

# --- Locate MySQL include/lib -----------------------------------------------
$mysqlBase = Get-ChildItem -Path "C:\Program Files\MySQL" -Directory -Filter "MySQL Server *" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $mysqlBase) {
    Fail "MySQL Server installation not found under C:\Program Files\MySQL."
    exit 1
}
$MySqlInclude = "$($mysqlBase.FullName)\include"
$MySqlLib     = "$($mysqlBase.FullName)\lib\libmysql.lib"
if (-not (Test-Path $MySqlInclude) -or -not (Test-Path $MySqlLib)) {
    Fail "MySQL include/lib not found at expected paths. Include: $MySqlInclude | Lib: $MySqlLib"
    exit 1
}
Ok "MySQL found: $($mysqlBase.FullName)"

# --- Locate OpenSSL ----------------------------------------------------------
$opensslRoot = "C:\Program Files\OpenSSL-Win64"
if (-not (Test-Path "$opensslRoot\include\openssl\ssl.h")) {
    Fail "OpenSSL not found at $opensslRoot. Install OpenSSL (Win64) first."
    Fail "Download from https://slproweb.com/products/Win32OpenSSL.html"
    Fail "Pick the Win64 (non-Light) installer, install to $opensslRoot, then re-run this script."
    exit 1
}
Ok "OpenSSL found: $opensslRoot"

# --- CMake Configure ----------------------------------------------------------
Write-Host ""
Info "Configuring with CMake (this generates the Visual Studio solution)..."
Info "/Zm200 raises the PCH memory allocation ceiling - fixes C1060 without breaking PCH-umbrella includes"

# Only create the build dir if it doesn't already exist. Previously this
# always wiped $BuildDir first, which threw away every prior build's
# object files and forced a full from-scratch CMake configure + MSBuild
# compile on every run - even a routine "add one module, recompile" cycle
# rebuilt the entire server. The Linux menu's recompile option never does
# this; it just runs `make` again inside the existing build/ folder and
# lets make's own timestamp-based dependency tracking figure out what
# actually changed. CMake + MSBuild have the same incremental capability -
# reusing $BuildDir here lets a reconfigure (fast, just regenerates project
# files) and MSBuild (only recompiles stale/new sources) behave the same
# way. Delete $BuildDir yourself first if you ever want a genuine clean
# rebuild from zero.
if (-not (Test-Path $BuildDir)) {
    New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null
    Info "Created build dir at $BuildDir"
} else {
    Info "Reusing existing build dir at $BuildDir for an incremental build"
}

# Detect the actual compiler tag from the lib files themselves, rather than
# hardcoding a version. Newer MSVC point releases can produce Boost libs
# tagged with a compiler suffix (e.g. vc145) that older CMake FindBoost
# modules don't auto-detect - they guess an older tag and report the libs
# as "not found" even though the version check passes. Reading the real
# tag off disk means this keeps working if the tag shifts again later
# (vc146, vc150, etc.) without needing another manual fix.
$sampleLib = Get-ChildItem -Path $BoostLibDir.FullName -Filter "boost_filesystem-vc*-mt-x64-*.lib" -ErrorAction SilentlyContinue | Select-Object -First 1
$boostCompilerTag = $null
if ($sampleLib -and $sampleLib.Name -match 'boost_filesystem-(vc\d+)-') {
    $boostCompilerTag = $matches[1]
    Info "Detected Boost compiler tag: $boostCompilerTag"
} else {
    Warn "Could not auto-detect Boost compiler tag from lib filenames - CMake will use its own guess"
}

$cmakeArgs = @(
    "-S", $AcoreDir,
    "-B", $BuildDir,
    "-A", "x64",
    "-DTOOLS_BUILD=none",
    "-DSCRIPTS=static",
    "-DMODULES=static",
    "-DCMAKE_INSTALL_PREFIX=$InstallDir",
    "-DBOOST_ROOT=$BoostRoot",
    "-DBOOST_LIBRARYDIR=$($BoostLibDir.FullName)",
    "-DOPENSSL_ROOT_DIR=$opensslRoot",
    "-DMYSQL_INCLUDE_DIR=$MySqlInclude",
    "-DMYSQL_LIBRARY=$MySqlLib",
    "-DCMAKE_CXX_FLAGS=/Zm200 /EHsc"
)
if ($boostCompilerTag) {
    $cmakeArgs += "-DBoost_COMPILER=-$boostCompilerTag"
}

& cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) {
    Fail "CMake configure failed. Check the output above for missing dependencies."
    exit 1
}
Ok "CMake configure complete"

# --- MSBuild Compile ----------------------------------------------------------
Write-Host ""
Info "Building with MSBuild (Release, x64, /m:6) - this WILL take a while, grab a coffee..."
Info "Release (not RelWithDebInfo) skips PDB generation - saves disk + avoids PDB write conflicts"
Info "/m:6 caps parallelism - gives each cl.exe process enough heap even with PCH off"

$slnFile = Get-ChildItem -Path $BuildDir -Filter "*.sln" | Select-Object -First 1
if (-not $slnFile) {
    Fail "No .sln file found in $BuildDir after CMake configure."
    exit 1
}

$msbuildPath = & $vsWhere -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
if (-not $msbuildPath) {
    Fail "MSBuild.exe not found via vswhere."
    exit 1
}
Ok "MSBuild found: $msbuildPath"

& $msbuildPath $slnFile.FullName /p:Configuration=Release /p:Platform=x64 /m:6 /v:minimal
if ($LASTEXITCODE -ne 0) {
    Fail "Build FAILED. Check the output above for the first error."
    Warn "Common causes: missing Boost libs, wrong MySQL lib path, OpenSSL version mismatch, C1060 heap exhaustion (lower /m: further)."
    exit 1
}
Ok "Build complete"

# --- Install ----------------------------------------------------------
Write-Host ""
Info "Running INSTALL target..."
& $msbuildPath "$BuildDir\INSTALL.vcxproj" /p:Configuration=Release /p:Platform=x64 /v:minimal
if ($LASTEXITCODE -ne 0) {
    Fail "INSTALL target failed."
    exit 1
}
Ok "Installed to $InstallDir"

# --- Copy required DLLs (per AzerothCore wiki) ----------------------------------------------------------
Write-Host ""
Info "Copying required DLLs..."
$binDir = "$InstallDir\bin"
if (-not (Test-Path $binDir)) {
    Warn "Expected bin directory not found at $binDir - checking alternate location..."
    $binDir = $InstallDir
}

$mysqlDll = "$($mysqlBase.FullName)\lib\libmysql.dll"
if (Test-Path $mysqlDll) {
    Copy-Item $mysqlDll -Destination $binDir -Force
    Ok "Copied libmysql.dll"
} else {
    Warn "libmysql.dll not found at $mysqlDll - copy manually"
}

# OpenSSL 4.0 uses libssl-4-x64.dll / libcrypto-4-x64.dll instead of the
# -3- naming the official wiki documents (which targets OpenSSL 3.x)
$opensslDlls = Get-ChildItem -Path "$opensslRoot\bin" -Filter "lib*-x64.dll" -ErrorAction SilentlyContinue
foreach ($dll in $opensslDlls) {
    Copy-Item $dll.FullName -Destination $binDir -Force
    Ok "Copied $($dll.Name)"
}
if ($opensslDlls.Count -eq 0) {
    Warn "No OpenSSL DLLs found at $opensslRoot\bin - copy libssl/libcrypto DLLs manually"
}

Write-Host ""
Ok "Step 4 complete - AzerothCore built and installed to $InstallDir"
Write-Host ""
Info "Next: Step 5 - Download client data"