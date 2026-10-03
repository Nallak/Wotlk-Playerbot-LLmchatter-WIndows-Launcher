# =====================================================
# TwoLifeCrew WotLK - Native Windows Edition
# Step 3: Source Patches (MSVC-relevant only)
# Run as Administrator or normal user
# =====================================================

$ErrorActionPreference = "Stop"
$AcoreDir = "C:\Azerothcore"

function Info($msg)  { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Ok($msg)    { Write-Host "[OK]   $msg" -ForegroundColor Green }
function Warn($msg)  { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Fail($msg)  { Write-Host "[FAIL] $msg" -ForegroundColor Red }

Info "TwoLifeCrew WotLK Native Windows Edition - Step 3: Source Patches"
Write-Host ""

if (-not (Test-Path "$AcoreDir\.git")) {
    Fail "AzerothCore not found at $AcoreDir. Run Step 2 first."
    exit 1
}

$MySqlCpp  = "$AcoreDir\src\server\database\Database\MySQLConnection.cpp"
$PoolCpp   = "$AcoreDir\src\server\database\Database\DatabaseWorkerPool.cpp"
$CellImplH = "$AcoreDir\src\server\game\Grids\Cells\CellImpl.h"
$LlmChatterSharedCpp = "$AcoreDir\modules\mod-llm-chatter\src\LLMChatterShared.cpp"

# ─── Patch 1: Remove mysql_ssl_mode block ─────────────────────────────────────
# Same rationale as Linux: this MySQL 8.0.26+ API isn't present/needed against
# our 9.7 client libs in the same way, and causes compile errors under MSVC too.
if (Test-Path $MySqlCpp) {
    $content = Get-Content $MySqlCpp -Raw
    if ($content -match 'mysql_ssl_mode') {
        Info "Patch 1: Removing mysql_ssl_mode block..."
        # Remove the if-block that references m_connectionInfo.ssl and mysql_options(...MYSQL_OPT_SSL_MODE...)
        $pattern = '(?ms)if\s*\(m_connectionInfo\.ssl\s*!=\s*""\)\s*\{.*?MYSQL_OPT_SSL_MODE.*?\}\s*'
        $newContent = [System.Text.RegularExpressions.Regex]::Replace($content, $pattern, '')
        Set-Content -Path $MySqlCpp -Value $newContent -NoNewline
        Ok "Patch 1 applied"
    } else {
        Ok "Patch 1 - skipping (not present)"
    }
} else {
    Warn "MySQLConnection.cpp not found - skipping Patch 1"
}

# ─── Patch 2: mysql_stmt_bind_named_param version guard ──────────────────────
if (Test-Path $MySqlCpp) {
    $content = Get-Content $MySqlCpp -Raw
    if ($content -match 'MYSQL_VERSION_ID >= 80300') {
        Info "Patch 2: Disabling mysql_stmt_bind_named_param branch..."
        $content = $content -replace '#if MYSQL_VERSION_ID >= 80300', '#if 0'
        Set-Content -Path $MySqlCpp -Value $content -NoNewline
        Ok "Patch 2 applied"
    } else {
        Ok "Patch 2 - skipping (not present)"
    }
}

# ─── Patch 3: MySQL client/server version compatibility checks ───────────────
if (Test-Path $PoolCpp) {
    $content = Get-Content $PoolCpp -Raw
    $changed = $false

    if ($content -match 'bool isSupportClientDB = mysql_get_client_version\(\) >= MIN_MYSQL_CLIENT_VERSION;') {
        Info "Patch 3: Forcing isSupportClientDB = true..."
        $content = $content -replace 'bool isSupportClientDB = mysql_get_client_version\(\) >= MIN_MYSQL_CLIENT_VERSION;', 'bool isSupportClientDB = true;'
        $changed = $true
    }
    if ($content -match 'bool isSameClientDB = mysql_get_client_version\(\) == MYSQL_VERSION_ID;') {
        $content = $content -replace 'bool isSameClientDB = mysql_get_client_version\(\) == MYSQL_VERSION_ID;', 'bool isSameClientDB = true;'
        $changed = $true
    }
    if ($content -match 'bool DatabaseIncompatibleVersion\(std::string const mysqlVersion\)' -and $content -notmatch 'DatabaseIncompatibleVersion_UNUSED') {
        $needle = 'bool DatabaseIncompatibleVersion(std::string const mysqlVersion)'
        $replacement = $needle + "`n{`n    return false;`n}`nbool DatabaseIncompatibleVersion_UNUSED(std::string const mysqlVersion)"
        $content = $content.Replace($needle, $replacement)
        $changed = $true
    }

    if ($changed) {
        Set-Content -Path $PoolCpp -Value $content -NoNewline
        Ok "Patch 3 applied"
    } else {
        Ok "Patch 3 - skipping (not present)"
    }
} else {
    Warn "DatabaseWorkerPool.cpp not found - skipping Patch 3"
}

# ─── Patch 5: Cell::VisitObjects inline ODR fix ───────────────────────────────
# Same MSVC/clang ODR (One Definition Rule) linker issue as Linux build.
if (Test-Path $CellImplH) {
    $content = Get-Content $CellImplH -Raw
    if ($content -match '(?m)^inline void Cell::VisitObjects') {
        Info "Patch 5: Removing 'inline' from Cell::VisitObjects..."
        $content = $content -replace '(?m)^inline void Cell::VisitObjects', 'void Cell::VisitObjects'
        Set-Content -Path $CellImplH -Value $content -NoNewline
        Ok "Patch 5 applied"
    } else {
        Ok "Patch 5 - skipping (not present)"
    }
} else {
    Warn "CellImpl.h not found - skipping Patch 5"
}

# ─── Patch 7: LLMChatterShared.cpp BuildChatPacket signature update ──────────
# Not a Linux-parity patch like 1/2/3/5 above - this is a fresh regression from
# the 2026-09-13 core pull: AzerothCore's ChatHandler::BuildChatPacket moved
# Language earlier in the argument list, dropped the old bare-sender-only
# shape in favor of requiring an explicit receiverGUID, and made chatTag a
# required uint8 instead of its old position. SendPartyMessageInstant() is a
# raw party broadcast (see the group->BroadcastPacket(...) right below it in
# source), so there's no single receiver - ObjectGuid::Empty is correct there,
# not a placeholder. mod-llm-chatter is our own module, so nothing upstream
# ever patches this for us; it has to live here like the core patches above.
if (Test-Path $LlmChatterSharedCpp) {
    $content = Get-Content $LlmChatterSharedCpp -Raw
    $oldCall = "ChatHandler::BuildChatPacket(`r`n        data,`r`n        CHAT_MSG_PARTY,`r`n        message,`r`n        LANG_UNIVERSAL,`r`n        CHAT_TAG_NONE,`r`n        bot->GetGUID(),`r`n        bot->GetName());"
    $oldCallLf = $oldCall -replace "`r`n", "`n"
    if ($content.Contains($oldCall) -or $content.Contains($oldCallLf)) {
        Info "Patch 7: Updating BuildChatPacket call in LLMChatterShared.cpp to current core signature..."
        $newCall = "ChatHandler::BuildChatPacket(`r`n        data,`r`n        CHAT_MSG_PARTY,`r`n        LANG_UNIVERSAL,`r`n        bot->GetGUID(),`r`n        ObjectGuid::Empty,`r`n        message,`r`n        CHAT_TAG_NONE,`r`n        bot->GetName());"
        if ($content.Contains($oldCall)) {
            $content = $content.Replace($oldCall, $newCall)
        } else {
            $newCallLf = $newCall -replace "`r`n", "`n"
            $content = $content.Replace($oldCallLf, $newCallLf)
        }
        Set-Content -Path $LlmChatterSharedCpp -Value $content -NoNewline
        Ok "Patch 7 applied"
    } else {
        Ok "Patch 7 - skipping (not present, already patched or call site changed)"
    }
} else {
    Warn "LLMChatterShared.cpp not found - skipping Patch 7"
}

# ─── Patch 8: mod-llm-chatter vs. the core-align API changes (IsBot / IsInChannel) ───
# Fresh regression from the 2026-09-23/24 "core-align" merge (mod-playerbots/
# azerothcore-wotlk PR #257 + upstream headless sessions): the Playerbot fork
# stripped its own helper methods out of core, and Hokken/mod-llm-chatter master
# (checked 2026-10-03) still calls two of them:
#   - WorldSession::IsBot()  -> mod-playerbots itself made the same swap to
#     WorldSession::IsHeadless() (PlayerbotAI.cpp), so chatter's calls get the
#     same rewrite here.
#   - Player::IsInChannel()  -> removed outright, and Channel::IsOn() is private,
#     so there is no public replacement. The original helper (compares channels
#     by ID - "Playerbot helper if bot talks in a different locale") is put back
#     into core instead, byte-for-byte the same behavior chatter was written for.
# Every part is guarded: it does nothing on an older core that still has the
# fork helpers, and it drops out on its own once chatter upstream stops using
# them. Files are read/written as UTF-8 keeping each file's existing BOM state
# and line endings (Get-Content/Set-Content would re-encode them as ANSI).
function Test-Utf8Bom($path) {
    $b = New-Object byte[] 3
    $fs = [System.IO.File]::OpenRead($path)
    try { $n = $fs.Read($b, 0, 3) } finally { $fs.Close() }
    return ($n -eq 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
}
function Write-Utf8Keep($path, $text) {
    $enc = New-Object System.Text.UTF8Encoding((Test-Utf8Bom $path))
    [System.IO.File]::WriteAllText($path, $text, $enc)
}

$ChatterSrcDir = "$AcoreDir\modules\mod-llm-chatter\src"
$WorldSessionH = "$AcoreDir\src\server\game\Server\WorldSession.h"
$PlayerH       = "$AcoreDir\src\server\game\Entities\Player\Player.h"
$PlayerCpp     = "$AcoreDir\src\server\game\Entities\Player\Player.cpp"

if ((Test-Path $ChatterSrcDir) -and (Test-Path $WorldSessionH) -and (Test-Path $PlayerH) -and (Test-Path $PlayerCpp)) {
    $chatterFiles = @(Get-ChildItem -Path $ChatterSrcDir -Recurse -File | Where-Object { $_.Extension -eq '.cpp' -or $_.Extension -eq '.h' })

    # 8a: ->IsBot() becomes ->IsHeadless(), only when core has IsHeadless and no IsBot.
    $wsText = [System.IO.File]::ReadAllText($WorldSessionH)
    $coreHeadlessOnly = ($wsText -match 'bool\s+IsHeadless\s*\(') -and ($wsText -notmatch 'bool\s+IsBot\s*\(')
    $botPatched = 0
    if ($coreHeadlessOnly) {
        foreach ($f in $chatterFiles) {
            $t = [System.IO.File]::ReadAllText($f.FullName)
            if ($t -match '->IsBot\(\)') {
                $t = [regex]::Replace($t, '->IsBot\(\)', '->IsHeadless()')
                Write-Utf8Keep $f.FullName $t
                $botPatched++
            }
        }
    }
    if ($botPatched -gt 0) {
        Info "Patch 8a: IsBot() -> IsHeadless() in $botPatched mod-llm-chatter file(s)"
        Ok   "Patch 8a applied"
    } else {
        Ok "Patch 8a - skipping (core still has IsBot, or chatter no longer calls it)"
    }

    # 8b: put Player::IsInChannel back into core, only when chatter still calls it
    # and core no longer has it.
    $ph = [System.IO.File]::ReadAllText($PlayerH)
    $pc = [System.IO.File]::ReadAllText($PlayerCpp)
    $chatterUsesIsInChannel = $false
    foreach ($f in $chatterFiles) {
        if ([regex]::IsMatch([System.IO.File]::ReadAllText($f.FullName), '->\s*IsInChannel\s*\(')) { $chatterUsesIsInChannel = $true; break }
    }
    if ($chatterUsesIsInChannel -and ($ph -notmatch 'IsInChannel') -and ($pc -notmatch 'Player::IsInChannel') -and
        $ph.Contains('void LeftChannel(Channel* c);') -and ($pc -match '(?m)^void Player::ClearChannelWatch\(\)')) {
        Info "Patch 8b: Restoring Player::IsInChannel (removed by core-align, still used by mod-llm-chatter)..."

        $nlH = "`n"; if ($ph.Contains("`r`n")) { $nlH = "`r`n" }
        $ph = $ph.Replace('void LeftChannel(Channel* c);', 'void LeftChannel(Channel* c);' + $nlH + '    bool IsInChannel(const Channel* c);')
        Write-Utf8Keep $PlayerH $ph

        $nlC = "`n"; if ($pc.Contains("`r`n")) { $nlC = "`r`n" }
        $shim = @(
            '// mod-llm-chatter compat (wowmenu Patch 8) - fork helper removed by core-align',
            'bool Player::IsInChannel(const Channel* c)',
            '{',
            '    for (Channel const* chan : m_channels)',
            '        if (c->GetChannelId() == chan->GetChannelId())',
            '            return true;',
            '',
            '    return false;',
            '}'
        ) -join $nlC
        $pc = [regex]::Replace($pc, '(?m)^void Player::ClearChannelWatch\(\)', ($shim + $nlC + $nlC + 'void Player::ClearChannelWatch()'))
        Write-Utf8Keep $PlayerCpp $pc
        Ok "Patch 8b applied"
    } elseif ($chatterUsesIsInChannel -and ($ph -notmatch 'IsInChannel') -and ($pc -notmatch 'Player::IsInChannel')) {
        # Chatter needs it, the core has no IsInChannel, but the insertion anchors were not
        # found (Player.h / Player.cpp changed). Say so loudly - otherwise this looks like a
        # normal skip and the build then fails with C2039 and no hint why.
        Warn "Patch 8b - mod-llm-chatter calls Player::IsInChannel() but the core has none, and the patch anchors"
        Warn "           (LeftChannel in Player.h / ClearChannelWatch in Player.cpp) were not found - NOT patched."
        Warn "           Expect build error C2039 'IsInChannel'. Patch 8b needs updating for this core version."
    } else {
        Ok "Patch 8b - skipping (core still has IsInChannel, chatter no longer calls it, or already patched)"
    }
} else {
    Warn "Patch 8 - mod-llm-chatter sources or core files not found - skipping"
}

Write-Host ""
Ok "Step 3 complete - MSVC source patches applied"
Write-Host ""
Info "Patches NOT needed on Windows (vs Linux edition):"
Write-Host "  - Patch 4 (jemalloc removal) - MSVC doesn't use jemalloc, N/A"
Write-Host "  - Patch 6 (DBUpdater mysql->mariadb binary) - we're using MySQL natively, N/A"
Write-Host ""
Info "Next: Step 4 - CMake Configure + MSBuild Compile"
