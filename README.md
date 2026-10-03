# WOTLK Windows Launcher

**Pirate Fleet Edition** - a native Windows build pipeline for an AzerothCore (WotLK 3.3.5a) server with Playerbots and AI-powered bot chatter, plus a graphical server manager to run it all day-to-day.

---

# 📢 Project Status & Latest Updates

> **Status:** 🟢 Active

## ⚠️ Update (2026-08-27)

Fixed a bug where changing bot count or chatter tuning values and clicking Save could silently write back the old number instead of the new one
Self-updater rewritten: updates now build a new versioned exe (WowMenuV2.exe, WowMenuV3.exe, ...) and auto-launch it, closing the old window automatically - no more manual restart step
If you're on a version before this date, see the one-time fix notice above before your first update

---

##  One-time fix if "Update WOTLK-Menu" seems to do nothing

Versions before **2026-08-27** had a self-update bug: clicking Update
would build a new version but fail to install it, while still saying
"Updated!". Do this once, manually:

1. Download `08-wotlk-menu.cs` and `07-build-wotlk-menu.ps1` into a
   folder, for example `C:\installer`
2. Open PowerShell with Administrator rights
3. `cd C:/installer`
4. Run:
   ```
   powershell -ExecutionPolicy Bypass -File 07-build-wotlk-menu.ps1
   ```
5. Delete the Older WoWmenu Run the fresh `WOTLK-Menu.exe` that has the V_tag it produces instead of your old one

**That's it, one time only.** Every update after this works normally
through the in-app button — no manual steps ever again.

---

## What's new

- **New Modules & Data tab**: brings the Windows menu up to parity with
  the Linux one — Clone/Update/Rollback for individual modules, plus
  Update Core / Rollback Core, all in one place.
- **Pull first, build once**: Clone/Update/Rollback only ever touch git
  — no automatic rebuild after each one. Clone or update everything you
  want first, then hit **Build now** a single time, which runs your
  existing `03-build.ps1` (streamed live into the log).
- **Client Data download**: one click to grab the latest
  wowgaming/client-data release (auto-detects the current tag, falls
  back to v20). Fixed a real bug where an early version of this wiped
  AzerothCore's own `data\sql\` folder along with the client assets —
  now only the specific client-data subfolders (dbc/maps/vmaps/mmaps/
  Cameras) are ever touched; `data\sql\` is never replaced.
- **Auto-creates missing module configs**: after every successful Build,
  any module missing its live `.conf` (only its `.conf.dist` template
  exists — normal for a module cloned after the original install) gets
  one created automatically. No more manually copying `.dist` files for
  newly added modules.
- **Fixed a startup bug**: the menu was silently deleting `CMakeLists.txt`,
  `src\`, and `deps\` on every single launch (leftover from an old,
  incorrect cleanup routine) — this broke every build after the first.
  Fixed by only cleaning genuinely one-time dev/CI files.
- **Concurrency guard**: a second click on any Modules & Data button
  while one is already running now gets ignored instead of starting a
  second overlapping git process (which could leave a module's working
  tree half-merged).
- **Dashboard**: added live Horde (red) / Alliance (blue) online counts.
- Removed the "AH bot account" button (an AH bot is just a normal
  account) and the obsolete "Recompile info" placeholder; reordered the
  Admin database fields to Update Realmlist IP → Get Char GUID → Set AH
  Bot GUID.

## Known limitation to watch for

Same as Linux: updating core or mod-playerbots past the pinned commit
isn't risk-free. Core and mod-playerbots generally move together fine —
the real risk is third-party modules maintained separately, which can
lag behind a mod-playerbots API change and fail to compile against a
newer core. Check a module's own recent activity before updating, and
use Rollback if a build breaks.

---

## What this is

This repo compiles [AzerothCore](https://github.com/azerothcore/azerothcore-wotlk) (Playerbot branch) from source on native Windows, along with a set of gameplay modules:

- **mod-playerbots** - AI-controlled bot characters that can fill out groups, run dungeons, and populate the world
- **mod-llm-chatter** - connects those bots to a local LLM (via [Ollama](https://ollama.com)) so they can actually chat, in character, instead of using canned phrases
- **mod-reagent-bank**, **mod-junk-to-gold**, **mod-no-hearthstone-cooldown**, **mod-assistant**, **mod-npc-all-mounts**, **mod-ah-bot-plus** - quality of life modules that round out the server

The end result is a single **WOTLK-Menu.exe** - a dark-themed WinForms manager where you start/stop the server, manage playerbot settings, create GM accounts, pick and pull AI models for bot chatter, and run backups - all from one window.

---

## Why native Windows, not Docker/Linux

Because sometimes you just want it to run directly on the machine you're already using - no virtualization layer, no WSL, just a normal AzerothCore install that happens to compile and run as a native Windows service.

## Architecture

Everything is a numbered PowerShell stage, run in order by the orchestrator:

```
01  Clone AzerothCore + all modules (git, latest HEAD - not pinned)
02  Apply MSVC-specific source patches (MySQL SSL, ODR fix, version checks)
03  CMake configure + MSBuild compile, install, copy DLLs, clean up the tree
04  Client data + database setup + config patching
05  Ollama install only (no model - that's picked from the menu, see below)
06  Python venv + LLM bridge setup
07  Compile 08 into WOTLK-Menu.exe (with the wowmenu.ico badge)
08  The manager itself (C# WinForms, source)
```

`00-wotlk-azerothcore-orchestrator.ps1` downloads and runs 01 through 07 in order, with resume support: if a stage fails (usually a missing prerequisite), fix it and run the orchestrator again - it picks up where it left off instead of starting over.

**Resume caveat:** the resume-state file tracks whether a stage *completed*, not every individual step inside it. A stale state file can cause stage 03 to be skipped as "already done" on a re-run, silently skipping its trailing steps too (DLL copying, source-tree cleanup). The menu repairs the DLL half of this automatically on every launch (see Notes); the cleanup half needs a manual fix - see Troubleshooting.

## Quick start

1. Read [PREREQUISITES.md](PREREQUISITES.md) and install everything listed
2. Run `00-wotlk-azerothcore-orchestrator.ps1` as Administrator
3. Once it finishes, launch `WOTLK-Menu.exe` from `C:\Azerothcore`
4. MySQL Server runs as a Windows Service and starts automatically - no menu action needed. (The menu's "Start MariaDB" button only appears for a future portable-repack build; it won't show up here.)
5. In the menu, in this order:
   - **Start World server** - wait for `AC>` in its console window
   - **Start Auth server**
   - Create a GM account from the Admin tab, then fully log out and back into the game client for the GM level to take effect

AI bot chatter is optional and handled entirely from the LLM Bridge tab: pick an already-installed model from the dropdown, or pull a new one by name - either way updates `mod_llm_chatter.conf` automatically. Start the LLM bridge **after** World server has booted at least once; the bridge's database tables are created by DBUpdater on World server's first launch, not by the install scripts.

## Running stages individually

Each numbered script can also be run on its own - useful for resuming after a specific failure, or rebuilding just one piece:

```
powershell -ExecutionPolicy Bypass -File 03-build.ps1
```

## Useful in-game commands

**All GM commands below require GM level 3 on your account.** Set it via the Admin tab's account buttons, or directly:
```sql
INSERT INTO acore_auth.account_access (id, gmlevel, RealmID) VALUES (<account_id>, 3, -1);
```
Fully log out and back in afterward - GM level only refreshes on a fresh login, not mid-session.

Two of the modules add vendor NPCs you spawn yourself with a GM macro. Create these as macros in-game (Main Menu -> Macros -> New):

**Assistant NPC** (mod-assistant - repair, portals, etc.)
```
.npc add 9000000
```

**Mounts NPC** (mod-npc-all-mounts - every mount in the game)
```
.npc add 601014
```

**Removing a spawned NPC:** these commands spawn a permanent NPC at your location, and running the macro twice just spawns a duplicate. **Target it** and run the following before spawning a replacement, or you'll end up with a small army of assistants standing around:
```
.npc delete
```

## Notes

- Modules clone at latest HEAD, not pinned commits - if a module update ever breaks compatibility, that'll show up here first.
- `mod-transmog` is intentionally excluded - it crashes when playerbots loot items at the module version this pulls.
- Stage 04 checks for `data/sql/base/` and downloads a ~190MB SQL archive from a GitHub Release only if it's missing. On the self-compile path (this repo), that data comes from AzerothCore's own git clone in stage 01 and the download is skipped. The GitHub archive exists as a fallback for a future precompiled-binaries build path, which has no git clone to pull SQL from at all.
- `WOTLK-Menu.exe` checks for and repairs missing runtime DLLs (libmysql, OpenSSL 3.x + 4.x, legacy provider) every time it launches - if stage 03's own DLL-copy step ever gets skipped, opening the menu fixes it automatically.
- llm-chatter's database tables are created by DBUpdater the first time World server boots, not by any install stage - start World server before the LLM bridge, always.

## Troubleshooting

**"command does not exist" for a GM command in-game** - your account isn't GM level 3, or the level change hasn't taken effect yet. See "Useful in-game commands" above.

**LLM bridge health check fails with "Missing tables"** - World server hasn't been started yet (or wasn't left running long enough to finish its first-boot database update). Start World server, wait for `AC>`, then retry the bridge.

**worldserver.exe fails with a missing DLL error** - open `WOTLK-Menu.exe`; it self-checks and repairs this on every launch. If it still fails after that, MySQL or OpenSSL likely aren't installed at the paths PREREQUISITES.md expects.

**`C:\Azerothcore` still has source-tree clutter (`src`, `deps`, `.github`, etc.) after a full run** - stage 03's cleanup step got skipped, usually because a leftover resume-state file marked stage 03 as already done (see the Resume caveat under Architecture). Delete `.wotlk-orchestrator-state` and re-run the orchestrator, or just manually remove the clutter folders - `bin`, `configs`, `data`, `logs`, `modules`, and the compiled exes are the only things that need to stay.

## Status

Battle-tested end to end on fresh Windows 11, as of July 2026. If something in here breaks for you, it's almost certainly a prerequisite version mismatch - check PREREQUISITES.md first.
