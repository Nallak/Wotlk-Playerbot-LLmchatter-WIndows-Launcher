# =====================================================
# TwoLifeCrew WotLK - Native Windows Edition
# Step 5: Client Data + Database Setup + Config Patching
# Run as Administrator
# =====================================================

$ErrorActionPreference = "Stop"

# -- Dark terminal theme (Pirate Fleet style) ------------------------------
$Host.UI.RawUI.BackgroundColor = "Black"
$Host.UI.RawUI.ForegroundColor = "Gray"
try { Clear-Host } catch { }

$InstallDir     = "C:\Azerothcore"
$ConfigsDir     = "$InstallDir\configs"
$ModulesDir     = "$ConfigsDir\modules"
$DataDir        = "$InstallDir\data"
$LogsDir        = "$InstallDir\logs"
$WorldConf      = "$ConfigsDir\worldserver.conf"
$AuthConf       = "$ConfigsDir\authserver.conf"
$PlayerbotsConf = "$ModulesDir\playerbots.conf"
$LlmConf        = "$ModulesDir\mod_llm_chatter.conf"

function Info($msg)  { Write-Host "  [INFO] $msg" -ForegroundColor Cyan }
function Ok($msg)    { Write-Host "  [OK]   $msg" -ForegroundColor Green }
function Warn($msg)  { Write-Host "  [WARN] $msg" -ForegroundColor Yellow }
function Fail($msg)  { Write-Host "  [FAIL] $msg" -ForegroundColor Red }
function Section($msg) {
    Write-Host ""
    Write-Host "  ====================================================" -ForegroundColor Red
    Write-Host "       $msg" -ForegroundColor Red
    Write-Host "  ====================================================" -ForegroundColor Red
    Write-Host ""
}

# Anchored replace - avoids clobbering longer keys
# (e.g. DisabledWithoutRealPlayerLoginDelay, MinRandomBotsPriceChangeInterval)
function Set-ConfValue {
    param([string]$File, [string]$Key, [string]$Value)
    if (-not (Test-Path $File)) { Warn "$File not found - skipping $Key"; return }
    $pattern = "^$([regex]::Escape($Key))\s*="
    $content = Get-Content $File
    $found = $false
    $newContent = $content | ForEach-Object {
        if ($_ -match $pattern) { $found = $true; "$Key = $Value" } else { $_ }
    }
    if (-not $found) { $newContent += "$Key = $Value" }
    Set-Content -Path $File -Value $newContent
}

Info "TwoLifeCrew WotLK Native Windows Edition - Step 5: Database + Client Data"
Write-Host ""

if (-not (Test-Path "$InstallDir\worldserver.exe")) {
    Fail "worldserver.exe not found at $InstallDir. Run Step 4 (build) first."
    exit 1
}

# -- Pre-flight: locate mysql.exe -----------------------------------------
$MySqlExe = (Get-Command mysql -ErrorAction SilentlyContinue).Source
if (-not $MySqlExe) {
    $mysqlBase = Get-ChildItem -Path "C:\Program Files\MySQL" -Directory -Filter "MySQL Server *" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($mysqlBase -and (Test-Path "$($mysqlBase.FullName)\bin\mysql.exe")) {
        $MySqlExe = "$($mysqlBase.FullName)\bin\mysql.exe"
    }
}
if (-not $MySqlExe) {
    Fail "mysql.exe not found. Ensure MySQL Server is installed (Step 1)."
    exit 1
}
Ok "mysql found: $MySqlExe"

# -- Ensure configs exist (copy from .dist if needed) ----------------------
Section "Config Files"
if (-not (Test-Path $WorldConf)) {
    Copy-Item "$WorldConf.dist" $WorldConf
    Ok "Created worldserver.conf from .dist"
} else { Ok "worldserver.conf already exists" }

if (-not (Test-Path $AuthConf)) {
    Copy-Item "$AuthConf.dist" $AuthConf
    Ok "Created authserver.conf from .dist"
} else { Ok "authserver.conf already exists" }

Get-ChildItem "$ModulesDir\*.conf.dist" -ErrorAction SilentlyContinue | ForEach-Object {
    $target = $_.FullName -replace '\.dist$', ''
    if (-not (Test-Path $target)) {
        Copy-Item $_.FullName $target
        Ok "Created $(Split-Path $target -Leaf)"
    }
}

# -- Client data -----------------------------------------------------------
Section "Client Data"
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

if (Test-Path "$DataDir\dbc") {
    Ok "Client data already exists"
} else {
    # -- Resolve which client-data version to grab -------------------------
    # wowgaming tags a new client-data release (v19, v20, etc) whenever the
    # mmap/vmap generator format changes to match a core update. Instead of
    # hardcoding a version number that goes stale the moment core moves on,
    # ask GitHub directly for whatever is currently tagged "latest".
    # Falls back to a hardcoded version only if the API call itself fails
    # (rate limit, no internet reachability to api.github.com, etc).
    $dataTag = $null
    $url = $null

    Info "Checking latest wowgaming client-data release..."
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/wowgaming/client-data/releases/latest" `
            -UserAgent "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" `
            -Headers @{ "Accept" = "application/vnd.github+json" }
        $dataTag = $release.tag_name
        $asset = $release.assets | Where-Object { $_.name -match '^[Dd]ata\.zip$' } | Select-Object -First 1
        if (-not $asset) { $asset = $release.assets | Select-Object -First 1 }
        if ($asset) { $url = $asset.browser_download_url }
    } catch {
        Warn "Could not reach GitHub API ($_)"
    }

    if (-not $dataTag -or -not $url) {
        Warn "Falling back to hardcoded v20 (update this manually if wowgaming has moved on)"
        $dataTag = "v20"
        $url = "https://github.com/wowgaming/client-data/releases/download/v20/Data.zip"
    } else {
        Ok "Latest client-data tag: $dataTag"
    }

    Info "Downloading wowgaming client data $dataTag (this is several GB, grab a coffee)..."
    $zipPath = "$env:TEMP\Data.zip"

    # WebClient with a live progress bar - Invoke-WebRequest's built-in
    # progress rendering is known to badly throttle download speed in PowerShell
    Add-Type -AssemblyName System.Net.Http
    $httpClient = New-Object System.Net.Http.HttpClient
    $httpClient.Timeout = [System.TimeSpan]::FromHours(2)
    $response = $httpClient.GetAsync($url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).Result
    $totalBytes = $response.Content.Headers.ContentLength
    $stream = $response.Content.ReadAsStreamAsync().Result
    $fileStream = [System.IO.File]::Create($zipPath)
    $buffer = New-Object byte[] 1MB
    $totalRead = 0
    $lastPercent = -1
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastBytes = 0

    while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
        $fileStream.Write($buffer, 0, $read)
        $totalRead += $read
        if ($totalBytes -gt 0) {
            $percent = [math]::Floor(($totalRead / $totalBytes) * 100)
            if ($percent -ne $lastPercent -and $sw.ElapsedMilliseconds -ge 500) {
                $mbTotal = [math]::Round($totalBytes / 1MB, 0)
                $mbDone  = [math]::Round($totalRead / 1MB, 0)
                $speedMBs = [math]::Round((($totalRead - $lastBytes) / 1MB) / ($sw.ElapsedMilliseconds / 1000), 1)
                Write-Progress -Activity "Downloading client data $dataTag" `
                    -Status "$mbDone MB / $mbTotal MB  ($speedMBs MB/s)" `
                    -PercentComplete $percent
                $lastPercent = $percent
                $lastBytes = $totalRead
                $sw.Restart()
            }
        }
    }
    Write-Progress -Activity "Downloading client data $dataTag" -Completed
    $fileStream.Close()
    $stream.Close()
    $httpClient.Dispose()
    Ok "Download complete ($([math]::Round($totalRead / 1MB, 0)) MB) - tag $dataTag"

    Info "Extracting..."
    Expand-Archive -Path $zipPath -DestinationPath $DataDir -Force
    Remove-Item $zipPath -Force
    Ok "Client data extracted"
}

# -- SQL base schema (needed by DBUpdater on first worldserver launch) ------
Section "SQL Base Schema"
if (Test-Path "$DataDir\sql\base\db_auth") {
    Ok "data/sql already present"
} else {
    # Single-file download from GitHub Release - replaces the old 21-part
    # Gitea split download + byte-reassembly (Gitea 502'd on binary serving).
    Info "Downloading data-sql-full.zip from GitHub Release..."
    $sqlTmpDir = "$env:TEMP\data-sql-parts-$(Get-Random)"
    New-Item -ItemType Directory -Path $sqlTmpDir -Force | Out-Null

    $reassembledZip = "$sqlTmpDir\data-sql-full.zip"
    $sqlUrl = "https://github.com/Eldesan/data-sql-full/releases/download/v1.0/data-sql-full.zip"

    $retries = 5
    for ($i = 1; $i -le $retries; $i++) {
        try {
            Invoke-WebRequest -Uri $sqlUrl -OutFile $reassembledZip -UseBasicParsing -UserAgent "Mozilla/5.0"
            break
        } catch {
            if ($i -eq $retries) { throw }
            $waitSec = $i * 5
            Warn "Download failed (attempt $i/$retries) - retrying in ${waitSec}s..."
            Start-Sleep -Seconds $waitSec
        }
    }
    $dlSizeMB = [math]::Round((Get-Item $reassembledZip).Length / 1MB, 1)
    Ok "Downloaded data-sql-full.zip ($dlSizeMB MB)"

    Info "Extracting SQL archive..."
    Expand-Archive -Path $reassembledZip -DestinationPath $DataDir -Force

    # Handle nested zip: 7z's -v splitting on a single zip target can wrap
    # the original archive inside another zip layer rather than doing a raw
    # byte-split. If that happened, there will be a leftover .zip sitting in
    # $DataDir after the first extraction - unwrap it too.
    $innerZip = Get-ChildItem $DataDir -Filter "*.zip" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($innerZip -and -not (Test-Path "$DataDir\sql\base\db_auth")) {
        Info "Detected nested archive ($($innerZip.Name)) - extracting inner zip..."
        Expand-Archive -Path $innerZip.FullName -DestinationPath $DataDir -Force
        Remove-Item $innerZip.FullName -Force -ErrorAction SilentlyContinue
    }

    # If the sql subfolders (archive/base/create/custom/old/updates) landed
    # directly in $DataDir instead of nested under $DataDir\sql\, the zip was
    # built from the CONTENTS of sql\ rather than the sql\ folder itself -
    # move them into a proper sql\ wrapper.
    if (-not (Test-Path "$DataDir\sql\base\db_auth")) {
        $looseSqlFolders = @("archive", "base", "create", "custom", "old", "updates") |
            Where-Object { Test-Path "$DataDir\$_" }
        if ($looseSqlFolders.Count -gt 0) {
            Info "SQL folders landed loose in data\ - moving into data\sql\..."
            New-Item -ItemType Directory -Path "$DataDir\sql" -Force | Out-Null
            foreach ($folder in $looseSqlFolders) {
                Move-Item -Path "$DataDir\$folder" -Destination "$DataDir\sql\$folder" -Force
            }
        }
    }

    if (Test-Path "$DataDir\sql\base\db_auth") {
        Ok "data/sql extracted successfully"
    } else {
        Fail "Extraction completed but sql/base/db_auth not found - check archive contents"
        exit 1
    }

    Remove-Item $sqlTmpDir -Recurse -Force -ErrorAction SilentlyContinue
}

# -- Module SQL schemas (playerbots, llm-chatter, ahbot, reagent-bank, etc.) -
Section "Module SQL Schemas"
$modulesRoot = "$InstallDir\modules"
if (Test-Path "$modulesRoot\mod-playerbots\data\sql\playerbots\base") {
    Ok "Module SQL schemas already present"
} else {
    Info "Downloading module SQL schemas (playerbots, llm-chatter, ahbot, reagent-bank)..."
    $modulesSqlUrl = "https://raw.githubusercontent.com/Nallak/Wotlk-Playerbot-LLmchatter-WIndows-Launcher/main/modules-full-sql/modules-sql.zip"
    $modulesSqlZip = "$env:TEMP\modules-sql-$(Get-Random).zip"

    try {
        $retries = 5
        for ($i = 1; $i -le $retries; $i++) {
            try {
                Invoke-WebRequest -Uri $modulesSqlUrl -OutFile $modulesSqlZip -UseBasicParsing -UserAgent "Mozilla/5.0"
                break
            } catch {
                if ($i -eq $retries) { throw }
                $waitSec = $i * 5
                Warn "Download failed (attempt $i/$retries) - retrying in ${waitSec}s..."
                Start-Sleep -Seconds $waitSec
            }
        }
        Ok "Downloaded"

        Info "Extracting into $modulesRoot..."
        Expand-Archive -Path $modulesSqlZip -DestinationPath $modulesRoot -Force
        Remove-Item $modulesSqlZip -Force -ErrorAction SilentlyContinue

        if (Test-Path "$modulesRoot\mod-playerbots\data\sql\playerbots\base") {
            Ok "Module SQL schemas extracted successfully"
        } else {
            Warn "Extraction completed but expected playerbots base folder not found - check archive structure"
        }
    } catch {
        Warn "Module SQL download/extract failed: $_"
        Warn "Playerbots/ahbot/reagent-bank/llm-chatter databases may fail to auto-populate on first launch"
    }
}

# -- Database password ------------------------------------------------------
Section "Database Setup"
Write-Host "  Almost done! Need a database password."
Write-Host "  Avoid: / | & ! @ # `$ ' `"" -ForegroundColor DarkGray
Write-Host ""

$DbPass = $null
while ($true) {
    $p1 = Read-Host "  Password for 'acore'" -AsSecureString
    $p2 = Read-Host "  Confirm" -AsSecureString
    $bstr1 = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($p1)
    $bstr2 = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($p2)
    $plain1 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr1)
    $plain2 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr2)
    if ($plain1 -eq $plain2 -and $plain1.Length -gt 0) { $DbPass = $plain1; break }
    Fail "Passwords don't match or are empty - try again"
}
Write-Host ""

# -- Create databases + user -------------------------------------------------
Info "Creating databases..."
$rootPass = Read-Host "  MySQL root password (leave blank if none)" -AsSecureString
$bstrRoot = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($rootPass)
$rootPlain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstrRoot)

function Invoke-RootSql($query) {
    # Pass the password via environment variable so it doesn't appear
    # in the terminal or process list (unlike -pPASSWORD on the cmd line).
    $env:MYSQL_PWD = $rootPlain
    try {
        & $MySqlExe -u root -e $query 2>&1 | Out-Null
    } finally {
        Remove-Item Env:\MYSQL_PWD -ErrorAction SilentlyContinue
    }
}

Invoke-RootSql "CREATE DATABASE IF NOT EXISTS acore_world;"
Invoke-RootSql "CREATE DATABASE IF NOT EXISTS acore_characters;"
Invoke-RootSql "CREATE DATABASE IF NOT EXISTS acore_auth;"
Invoke-RootSql "CREATE DATABASE IF NOT EXISTS acore_playerbots;"
Invoke-RootSql "CREATE USER IF NOT EXISTS 'acore'@'localhost' IDENTIFIED BY '$DbPass';"
Invoke-RootSql "GRANT ALL PRIVILEGES ON acore_world.* TO 'acore'@'localhost';"
Invoke-RootSql "GRANT ALL PRIVILEGES ON acore_characters.* TO 'acore'@'localhost';"
Invoke-RootSql "GRANT ALL PRIVILEGES ON acore_auth.* TO 'acore'@'localhost';"
Invoke-RootSql "GRANT ALL PRIVILEGES ON acore_playerbots.* TO 'acore'@'localhost';"
Invoke-RootSql "FLUSH PRIVILEGES;"
Ok "Databases + user created"

# Base schema import - AzerothCore's DBUpdater normally does this automatically
# on first worldserver/authserver launch, so we don't manually import SQL here.
Info "Base schema will auto-import on first worldserver/authserver launch (DBUpdater)"

# -- Patch worldserver.conf ----------------------------------------------------
Section "Patching Configs"
$DataDirEscaped = $DataDir -replace '\\','/'
$LogsDirEscaped = $LogsDir -replace '\\','/'

Set-ConfValue $WorldConf "LoginDatabaseInfo" "`"127.0.0.1;3306;acore;$DbPass;acore_auth`""
Set-ConfValue $WorldConf "WorldDatabaseInfo" "`"127.0.0.1;3306;acore;$DbPass;acore_world`""
Set-ConfValue $WorldConf "CharacterDatabaseInfo" "`"127.0.0.1;3306;acore;$DbPass;acore_characters`""
Set-ConfValue $WorldConf "DataDir" "`"$DataDirEscaped`""
Set-ConfValue $WorldConf "LogsDir" "`"$LogsDirEscaped`""
Ok "worldserver.conf patched"

Set-ConfValue $AuthConf "LoginDatabaseInfo" "`"127.0.0.1;3306;acore;$DbPass;acore_auth`""
Ok "authserver.conf patched"

# -- Patch playerbots.conf with sea-trialed defaults --------------------------
if (Test-Path $PlayerbotsConf) {
    Set-ConfValue $PlayerbotsConf "PlayerbotsDatabaseInfo" "`"127.0.0.1;3306;acore;$DbPass;acore_playerbots`""
    Set-ConfValue $PlayerbotsConf "AiPlayerbot.MinRandomBots" "100"
    Set-ConfValue $PlayerbotsConf "AiPlayerbot.MaxRandomBots" "100"
    Set-ConfValue $PlayerbotsConf "AiPlayerbot.DisabledWithoutRealPlayer" "1"
    Set-ConfValue $PlayerbotsConf "AiPlayerbot.EnableBroadcasts" "0"
    Set-ConfValue $PlayerbotsConf "AiPlayerbot.RandomBotAutologin" "1"
    Ok "playerbots.conf configured - 100 bots, idle when no players online"
} else {
    Warn "playerbots.conf not found - skipping"
}

# -- Patch mod_llm_chatter.conf if present -------------------------------------
if (Test-Path $LlmConf) {
    # These three MUST be set together, or the bridge defaults to the
    # Anthropic provider and fails with 401 invalid x-api-key errors.
    Set-ConfValue $LlmConf "LLMChatter.Provider" "ollama"
    Set-ConfValue $LlmConf "LLMChatter.Model" "mistral-nemo:12b"
    Set-ConfValue $LlmConf "LLMChatter.Ollama.BaseUrl" "http://127.0.0.1:11434"
    Set-ConfValue $LlmConf "LLMChatter.Database.User" "acore"
    Set-ConfValue $LlmConf "LLMChatter.Database.Password" "$DbPass"
    Ok "mod_llm_chatter.conf configured for Ollama (provider/model/baseurl/DB creds)"
}

Write-Host ""
Ok "Step 5 complete - database + configs ready!"

# -- Write MySQL credentials file for wotlk-menu.ps1 (non-interactive DB calls) -
$MyCnfContent = @"
[client]
user=acore
password=$DbPass
"@
Set-Content -Path "$InstallDir\.my.cnf" -Value $MyCnfContent
Ok "Credentials file written for wotlk-menu.ps1 (Setup/Backup menus)"

Write-Host ""
Info "Next steps:"
Write-Host "  1) Run worldserver.exe - first launch runs DBUpdater (imports base schema, may take a few minutes)"
Write-Host "  2) Once you see 'World initialized', run authserver.exe in a separate window"
Write-Host "  3) Use wotlk-menu.ps1 for day-to-day server management"
Write-Host ""