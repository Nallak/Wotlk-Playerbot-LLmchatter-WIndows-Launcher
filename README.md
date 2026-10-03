# WotLK Playerbot + LLM Chatter — Native Windows Launcher

**TwoLifeCrew WotLK Native Windows Edition** — a one-command installer and a small manager app for running your own
**AzerothCore (WotLK 3.3.5a) Playerbot** server on Windows, with **AI-driven bot chatter** powered by a local LLM (Ollama).
No Docker, no WSL: it compiles natively with Visual Studio.

> Single-player / private-server tinkering for personal and educational use. No game client or Blizzard files are
> included. This project is not affiliated with Blizzard Entertainment or the AzerothCore project.

## What you get

- The **Playerbot fork of AzerothCore** (`mod-playerbots/azerothcore-wotlk`, branch `Playerbot`) built from source
- **mod-playerbots** (100 bots by default, idle when no players are online) and **mod-llm-chatter**
  (bots talk in chat using a local LLM through Ollama)
- A few quality-of-life modules: reagent bank, junk-to-gold, no-hearthstone-cooldown, assistant, all-mounts NPC, AH bot
- Databases, user and configs set up for you
- **WOTLK-Menu.exe** — a manager app to start/stop servers, edit configs, create accounts, back up, update and rebuild

## Requirements

| What | Notes |
|---|---|
| Windows 10 / 11 (x64) | Run PowerShell **as Administrator** |
| Disk space and time | Client data is several GB. The first build takes 20–60+ minutes |
| **Boost** (msvc, 64-bit) | Installed under `C:\local` — **manual** |
| **MySQL Server** | Under `C:\Program Files\MySQL\MySQL Server *` — **manual** |
| **OpenSSL (Win64)** | Under `C:\Program Files\OpenSSL-Win64` — **manual** |
| Git, CMake, Visual Studio Build Tools (C++ workload, v143) | The installer tries to install these if they are missing |
| Python 3 | For the LLM bridge (a virtual environment is created for you) |
| Ollama | Installed by the installer if missing |

The last tested combination used Visual Studio 2022, Boost 1.84 (vc143), MySQL 9.7 and OpenSSL 4.x. If a prerequisite is
missing, stage 03 stops with a message that says what to install.

## Quick start

1. Make an empty folder (for example `C:\installer`) and open **PowerShell as Administrator** in it.
2. Download and run the orchestrator:

```powershell
Invoke-WebRequest https://raw.githubusercontent.com/Nallak/Wotlk-Playerbot-LLmchatter-WIndows-Launcher/main/00-wotlk-azerothcore-orchestrator.ps1 -OutFile 00-wotlk-azerothcore-orchestrator.ps1
powershell -ExecutionPolicy Bypass -File .\00-wotlk-azerothcore-orchestrator.ps1
```

3. Answer the prompts in stage 04: the MySQL **root** password (blank if none) and a new password for the `acore` database user.
   Avoid these characters in passwords: `/ | & ! @ # $ ' "`
4. When it says **ALL STAGES COMPLETE**, start `C:\Azerothcore\WOTLK-Menu.exe`.

The orchestrator saves progress. If a stage fails, fix the problem and run the same command again; it continues where it
stopped. Use `-Force` to ignore the saved progress and redo everything:

```powershell
powershell -ExecutionPolicy Bypass -File .\00-wotlk-azerothcore-orchestrator.ps1 -Force
```

## First start

1. In the menu, **Start MariaDB/MySQL**.
2. **Start World server**. The first launch imports the base database, which can take a few minutes. Wait for
   `World initialized`.
3. **Start Auth server**.
4. Create your account from the menu's account tools (GM account or normal account).
5. Point your 3.3.5a client's `realmlist.wtf` at `127.0.0.1`.
6. For AI chatter: start Ollama, pull a model in the menu's LLM tab, then **Start LLM bridge**.

## How it works

| Stage | Script | What it does |
|---|---|---|
| 00 | `00-wotlk-azerothcore-orchestrator.ps1` | Downloads each stage fresh from this repo and runs them in order, with resume |
| 01 | `01-Clone-Azertoh-windows.ps1` | Clones the Playerbot core and all modules to `C:\Azerothcore` (latest, not pinned) |
| 02 | `02-source-patches.ps1` | Applies small source-compatibility patches before the build (see below) |
| 03 | `03-build.ps1` | CMake configure + MSBuild (Release x64) in `C:\Build`, installs to `C:\Azerothcore` |
| 04 | `04-setup-database.ps1` | Downloads client data and SQL, creates the databases and the `acore` user, patches the configs |
| 05 | `05-ollama-setup.ps1` | Installs Ollama if missing |
| 06 | `06-llm-bridge-setup.ps1` | Creates the Python environment for the LLM chatter bridge |
| 07 | `07-build-wotlk-menu.ps1` | Compiles `WOTLK-Menu.exe` from `08-wotlk-menu.cs` with the C# compiler that ships with Windows |
| 08 | `08-wotlk-menu.cs` | Source of the manager app (WinForms, no extra dependencies) |

**Source patches (02).** The core, playerbots and modules move fast, so stage 02 applies small guarded fixes before every
build. Each one checks first and does nothing when it is not needed. Example: after the Playerbot "core-align" merge, the
core dropped `WorldSession::IsBot()` and `Player::IsInChannel()`, which mod-llm-chatter still called. Patch 8 bridges that so
the latest core, playerbots and chatter build together.

## The manager app

`WOTLK-Menu.exe` covers day-to-day running:

- Start / stop MySQL, world server, auth server (or all at once); open the latest log
- Account creation (GM / normal), realmlist IP, server info
- Bot settings: bot count, bots only online with players or always online, edit `playerbots.conf` / `worldserver.conf`
- LLM tab: Ollama start/stop, pull / list / delete models, start / stop / restart the bridge, edit the chatter config
- Backups: databases, accounts, configs, with restore
- Updates: update core and modules, rebuild or clean full build, pin a known-good baseline, update the menu itself
- On build, a missing `worldserver.conf` / `authserver.conf` is created from its `.dist`; existing configs only get
  new keys appended (a `.bck` backup is kept)

## Troubleshooting

- **"running scripts is disabled"**: start with `powershell -ExecutionPolicy Bypass -File ...` as shown above.
- **Download fails or hangs**: the host can be slow or return 5xx errors. The orchestrator retries with growing waits; just run it again.
- **Build fails**: the first real `error C....` line is the useful one. `_WIN32_WINNT` and CMake policy warnings are harmless noise.
- **Worldserver says it cannot open `configs/worldserver.conf`**: stage 04 did not run. Run the orchestrator again so it completes stages 04–06.
- **No chatter in game**: check that Ollama is running, a model is pulled, and the LLM bridge is started from the menu.

## Credits

[AzerothCore](https://www.azerothcore.org) · [mod-playerbots](https://github.com/mod-playerbots) ·
[mod-llm-chatter](https://github.com/Hokken/mod-llm-chatter) by Hokken · [Ollama](https://ollama.com) ·
client data from the [wowgaming](https://github.com/wowgaming) releases · plus the other modules listed in stage 01.

Also hosted at [Gitea](https://gitea.com/Eldesan/wotlk-windows-launcher).
