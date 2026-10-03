// ===============================================================
// TwoLifeCrew WotLK - Windows Server Manager (GUI)                
// Pirate Fleet Edition - native C# WinForms app                   
//
// Build:  run 07-build-wotlk-menu.ps1  (uses the C# compiler
//         that ships with Windows - nothing to install)
// Target: .NET Framework 4.x (preinstalled on Win10/11)
// Code:   C# 5 compatible on purpose, so the built-in
//         csc.exe can compile it. No string interpolation,
//         no ?. operators - keep it that way when editing!
// ==============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace WotlkMenu
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        // -- Paths (mirror wotlk-menu.ps1) --------------------------------
        static readonly string InstallDir     = @"C:\Azerothcore";
        static readonly string ConfigsDir     = InstallDir + @"\configs";
        static readonly string ModulesDir     = ConfigsDir + @"\modules";
        static readonly string PlayerbotsConf = ModulesDir + @"\playerbots.conf";
        static readonly string PlayerbotsDist = ModulesDir + @"\playerbots.conf.dist";
        static readonly string WorldConf      = ConfigsDir + @"\worldserver.conf";
        static readonly string AuthConf       = ConfigsDir + @"\authserver.conf";
        static readonly string LlmConf        = ModulesDir + @"\mod_llm_chatter.conf";
        static readonly string AhbotConf      = ModulesDir + @"\mod_ahbot.conf";
        static readonly string LogsDir        = InstallDir + @"\logs";
        static readonly string WorldExe       = InstallDir + @"\worldserver.exe";
        static readonly string AuthExe        = InstallDir + @"\authserver.exe";
        static readonly string MyCnfFile      = InstallDir + @"\.my.cnf";
        static readonly string DefaultBackupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "wotlk-backups");

        static readonly string AcoreSrcDir = @"C:\Azerothcore";
        static readonly string BridgeDir   = AcoreSrcDir + @"\modules\mod-llm-chatter\tools";
        static readonly string BridgePy    = BridgeDir + @"\venv\Scripts\python.exe";
        static readonly string BridgeScript= BridgeDir + @"\llm_chatter_bridge.py";
        static readonly string BridgePid   = Path.Combine(Path.GetTempPath(), "wotlk-llm-bridge.pid");

        // Dedicated home for the menu's own companion files (build/patch
        // scripts, icon, pin state) - previously these all just sat loose
        // directly in InstallDir (mixed in with the actual AzerothCore git
        // source tree), which is how 02-source-patches.ps1 went missing
        // without anyone noticing. This is a FIXED path (unlike the old
        // Application.StartupPath-based lookups it replaces), so the
        // scripts have one stable, intentional home regardless of where
        // WOTLK-Menu.exe itself happens to be launched from - the exe's own
        // location is a separate, untouched concern (see UpdateFromGitea's
        // versioned-exe logic, which still manages that independently).
        static readonly string ScriptsDir = Path.Combine(InstallDir, "wowmenu-scripts");

        // -- Ollama (installed system-wide by stage 05) -------------------
        // Ollama installation is fully handled by 05-ollama-setup.ps1 (the
        // official signed install.ps1 script) - the menu no longer manages
        // a separate portable download. This just points at where that
        // installer puts it.
        static readonly string OllamaExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Ollama\ollama.exe");

        // ResolveOllamaExe: system install, or null if not found.
        static string ResolveOllamaExe()
        {
            if (File.Exists(OllamaExe)) return OllamaExe;
            return null;
        }

        // -- Portable MariaDB (repack) ------------------------------------
        // The repack ships a self-contained MariaDB under the install dir.
        // Start/Stop buttons drive this; errors surface in the GUI log
        // instead of a silent black console window.
        static readonly string MariaDir     = InstallDir + @"\mariadb";
        static readonly string MariaBin      = MariaDir + @"\bin";
        static readonly string MariaDbd      = MariaBin + @"\mariadbd.exe";
        static readonly string MariaAdmin    = MariaBin + @"\mariadb-admin.exe";
        static readonly string MariaIni      = MariaDir + @"\my.ini";
        static readonly string MariaDataDir  = MariaDir + @"\data";

        // Self-update source - adjust if the repo moves
        const string RawBase = "https://raw.githubusercontent.com/Nallak/Wotlk-Playerbot-LLmchatter-WIndows-Launcher/main/";
        // Bump this string every time a fix is applied here. If the exe
        // you're running doesn't show this exact tag in its startup log,
        // you're testing a stale build, not the code that was actually
        // changed - that's the first thing to rule out before chasing any
        // more paint-code theories.
        const string BuildTag = "update-2026-10-03-dropplayerbotsfrombackup-createcoreconfs";

        // -- Theme --------------------------------------------------------
        // -- Theme (2026 navy/gold refresh) --------------------------------
        // Variable names kept identical to the previous palette so every
        // existing Btn()/HeadLabel() call across the whole file just works
        // unchanged - only the actual colors moved.
        static readonly Color BgDark   = Color.FromArgb(18, 20, 28);
        static readonly Color BgPanel  = Color.FromArgb(26, 29, 39);
        static readonly Color BgCtl    = Color.FromArgb(36, 40, 52);
        static readonly Color FgMain   = Color.Gainsboro;
        static readonly Color AccRed   = Color.FromArgb(214, 92, 92);
        static readonly Color AccCyan  = Color.FromArgb(79, 166, 255);
        static readonly Color AccGreen = Color.FromArgb(68, 209, 122);
        static readonly Color AccGray  = Color.FromArgb(135, 140, 150);
        // #FFCC00 - the actual in-game WoW UI gold (quest text, hotkeys,
        // item name highlights), not the corporate brand-logo yellow
        // (#EDE944), which looks and feels quite different in practice.
        static readonly Color AccGold  = Color.FromArgb(255, 204, 0);
        static readonly Color BorderCol = Color.FromArgb(58, 90, 130);

        // -- State --------------------------------------------------------
        readonly bool installLLM;
        readonly string mysqlExe;   // full path or null
        volatile int ollamaState;   // 0 unknown, 1 ready, 2 down
        DateTime ollamaChecked = DateTime.MinValue;
        volatile bool ollamaChecking;
        volatile bool aiInstalling; // guard so Install AI Chat can't double-run
        // Guards every git/build operation in the Modules tab (Clone,
        // Update, Rollback, Core update/rollback, Build) so a second
        // press while one is still running gets ignored instead of
        // starting a second overlapping run against the same repo folder
        // - that's exactly what produced ".git/index.lock" collisions and
        // a module ending up in a half-merged state. Same pattern as
        // aiInstalling above, just shared across all these buttons since
        // they all ultimately touch the same handful of git working trees.
        volatile bool moduleOpBusy;

        // -- Controls we update -------------------------------------------
        Label lblWorld, lblAuth, lblOllama, lblBridge, lblMaria, lblDashModel;
        TextBox txtOut;
        Label lblBotCur, lblBotCurDash, lblOnlineCounts, lblHorde, lblAlliance;
        DarkSpinner numBots;
        TextBox txtChar, txtIp, txtGmUser, txtGmPass, txtAhGuid, txtModelName;
        DarkCombo cmbInstalledModels;
        Label lblChatterTuning;
        DarkSpinner numAmbientGossip, numScanRadius, numEntityCooldown, numPlayerSayRadius, numGroupMsgCooldown, numGeneralChatCooldown, numBotSpeakerCooldown;
        volatile bool suppressModelPickEvent;
        ListBox lstBackups;
        Label lblBackupDir;
        Control btnInstallAi;   // state-aware Install AI Chat button
        List<SidebarButton> navButtons;
        List<Panel> sectionPanels;

        public MainForm()
        {
            installLLM = File.Exists(LlmConf);
            mysqlExe = FindMySql();

            Text = "WOTLK Server Manager \u2620 Pirate Fleet Edition";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(920, 660);
            MinimumSize = new Size(820, 560);
            BackColor = BgDark;
            ForeColor = FgMain;
            Font = new Font("Segoe UI", 9F);

            // Load wowmenu.ico at runtime for the window's actual Icon
            // (title bar, Alt-Tab, taskbar) - separate from the /win32icon
            // baked into the exe at compile time. Same file, loaded twice
            // for two different purposes. Falls back silently if missing.
            try
            {
                string iconPath = Path.Combine(ScriptsDir, "wowmenu.ico");
                if (File.Exists(iconPath)) Icon = new Icon(iconPath);
            }
            catch { }

            BuildUi();

            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 3000;
            t.Tick += delegate { RefreshStatus(); };
            t.Start();

            Shown += delegate
            {
                Log("[INFO] Ahoy captain! GUI edition reporting for duty.");
                Log("[INFO] Build marker: " + BuildTag + " - if you don't see this exact tag, you're running a stale exe, not the latest source");
                EnableDarkTitleBar();
                if (mysqlExe == null) Log("[WARN] mysql.exe not found (PATH or C:\\Program Files\\MySQL) - DB features disabled");
                if (!File.Exists(MyCnfFile)) Log("[WARN] " + MyCnfFile + " missing - run 04-setup-database.ps1 for DB features");
                EnsureRuntimeDlls();
                EnsureCleanInstallDir();
                EnsureScriptsDirMigrated();
                RefreshStatus();
                RefreshBotLabels();
                RefreshBackupList();
                RefreshChatterTuning();
                RefreshDashboard();
            };
        }

        // ============================ UI BUILD ========================
        void BuildUi()
        {
            BackColor = BgDark;

            // Output box (bottom)
            txtOut = new TextBox();
            txtOut.Multiline = true;
            txtOut.ReadOnly = true;
            txtOut.ScrollBars = ScrollBars.Vertical;
            txtOut.Dock = DockStyle.Bottom;
            txtOut.Height = 150;
            txtOut.BackColor = Color.FromArgb(12, 14, 20);
            txtOut.ForeColor = AccCyan;
            txtOut.Font = new Font("Consolas", 9F);
            txtOut.BorderStyle = BorderStyle.FixedSingle;

            // Sidebar (left) - replaces the old top button row entirely.
            // Same AddSection/ShowSection plumbing as before, just docked
            // left and laid out vertically instead of horizontal buttons.
            Panel sidebar = new Panel();
            sidebar.Dock = DockStyle.Left;
            sidebar.Width = 190;
            sidebar.BackColor = BgPanel;
            sidebar.Padding = new Padding(0, 16, 0, 0);

            Panel sidebarHeader = new Panel();
            sidebarHeader.Dock = DockStyle.Top;
            sidebarHeader.Height = 48;
            sidebarHeader.BackColor = BgPanel;

            FlowLayoutPanel headerFlow = new FlowLayoutPanel();
            headerFlow.AutoSize = true;
            headerFlow.FlowDirection = FlowDirection.LeftToRight;
            headerFlow.Anchor = AnchorStyles.None;
            headerFlow.Location = new Point(20, 8);

            try
            {
                string iconPath = Path.Combine(ScriptsDir, "wowmenu.ico");
                if (File.Exists(iconPath))
                {
                    PictureBox pic = new PictureBox();
                    pic.Image = new Icon(iconPath, 28, 28).ToBitmap();
                    pic.Width = 28;
                    pic.Height = 28;
                    pic.SizeMode = PictureBoxSizeMode.Zoom;
                    pic.Margin = new Padding(0, 4, 8, 0);
                    headerFlow.Controls.Add(pic);
                }
            }
            catch { }

            Label sidebarTitle = new Label();
            sidebarTitle.Text = "WOTLK MENU";
            sidebarTitle.AutoSize = true;
            sidebarTitle.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
            sidebarTitle.ForeColor = AccCyan;
            sidebarTitle.Margin = new Padding(0, 6, 0, 0);
            headerFlow.Controls.Add(sidebarTitle);

            sidebarHeader.Controls.Add(headerFlow);
            sidebar.Controls.Add(sidebarHeader);

            FlowLayoutPanel navFlow = new FlowLayoutPanel();
            navFlow.Dock = DockStyle.Top;
            navFlow.AutoSize = true;
            navFlow.FlowDirection = FlowDirection.TopDown;
            navFlow.WrapContents = false;
            navFlow.BackColor = BgPanel;
            sidebar.Controls.Add(navFlow);
            navFlow.BringToFront();

            Panel content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = BgDark;
            content.Padding = new Padding(16);

            navButtons = new List<SidebarButton>();
            sectionPanels = new List<Panel>();

            // These three are set by RefreshStatus() regardless of whether
            // the dashboard visually surfaces all of them - instantiate
            // here first so that call never hits a null reference. lblWorld/
            // lblAuth get properly parented inside BuildDashboardTab(); the
            // rest can be updated even while unparented (just won't render
            // anywhere until a future dashboard pass adds cards for them).
            lblOllama = StatusValue();
            lblBridge = StatusValue();
            lblMaria = StatusValue();

            AddSection(navFlow, content, "Dashboard", NavGlyph.Dashboard, BuildDashboardTab());
            AddSection(navFlow, content, "World Server", NavGlyph.World, BuildServerTab());
            if (installLLM) AddSection(navFlow, content, "LLM Bridge", NavGlyph.Brain, BuildLlmTab());
            AddSection(navFlow, content, "Playerbots", NavGlyph.Bots, BuildBotsTab());
            AddSection(navFlow, content, "Modules & Data", NavGlyph.Settings, BuildModulesTab());
            AddSection(navFlow, content, "Admin & Monitoring", NavGlyph.Admin, BuildAdminTab());
            AddSection(navFlow, content, "Backups", NavGlyph.Backup, BuildBackupTab());

            if (sectionPanels.Count > 0) ShowSection(0);

            Controls.Add(content);
            Controls.Add(sidebar);
            Controls.Add(txtOut);
        }

        // Registers one sidebar entry + its section panel, wiring the
        // button's click to switch to that panel.
        void AddSection(FlowLayoutPanel nav, Panel content, string title, NavGlyph glyph, Panel panel)
        {
            int index = sectionPanels.Count;
            panel.Dock = DockStyle.Fill;
            panel.Visible = false;
            content.Controls.Add(panel);
            sectionPanels.Add(panel);

            SidebarButton b = new SidebarButton(title, glyph, BgPanel, FgMain, BgCtl, AccCyan);
            b.Width = 190;
            b.Click += delegate { ShowSection(index); };
            nav.Controls.Add(b);
            navButtons.Add(b);
        }

        // Shows the section panel at the given index, hides the rest, and
        // highlights the corresponding sidebar entry with a gold accent +
        // tinted background, matching the dashboard mockup.
        void ShowSection(int index)
        {
            for (int i = 0; i < sectionPanels.Count; i++)
            {
                sectionPanels[i].Visible = (i == index);
                navButtons[i].SetSelected(i == index);
            }
            if (index == 0) RefreshDashboard();
        }

        Panel NewTab(string title, out FlowLayoutPanel flow)
        {
            Panel p = new Panel();
            p.BackColor = BgPanel;
            flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;
            flow.AutoScroll = true;
            flow.Padding = new Padding(14, 10, 14, 10);
            flow.BackColor = BgPanel;
            p.Controls.Add(flow);
            return p;
        }

        // -- DASHBOARD -------------------------------------------------------
        // Card-based summary view, first thing shown on launch. Cards reuse
        // the exact same backend state/methods as the detailed sidebar
        // sections (lblWorld/lblAuth from RefreshStatus(), BackupAll() from
        // the Backups section, etc.) - no duplicate logic, just a second,
        // friendlier place to see and trigger it.
        Panel BuildDashboardTab()
        {
            FlowLayoutPanel outer = new FlowLayoutPanel();
            outer.Dock = DockStyle.Fill;
            outer.FlowDirection = FlowDirection.LeftToRight;
            outer.WrapContents = true;
            outer.AutoScroll = true;
            outer.BackColor = BgDark;
            outer.Padding = new Padding(4);

            // QUICK ACTIONS card
            CardPanel actionsCard = new CardPanel(BgPanel, BorderCol);
            actionsCard.Width = 260;
            actionsCard.Height = 190;
            actionsCard.Controls.Add(CardTitle("QUICK ACTIONS"));
            FlowLayoutPanel actionRow = new FlowLayoutPanel();
            actionRow.Dock = DockStyle.Top;
            actionRow.AutoSize = true;
            actionRow.Margin = new Padding(0, 34, 0, 0);
            actionRow.Controls.Add(Btn("Start All", AccGreen, delegate { StartAllServices(); }));
            actionRow.Controls.Add(Btn("Stop All", AccRed, delegate { StopAllServices(); }));
            actionRow.Controls.Add(Btn("Backup Now", AccCyan, delegate { BackupAccount(); }));
            actionsCard.Controls.Add(actionRow);
            actionRow.BringToFront();
            outer.Controls.Add(actionsCard);

            // SERVER STATUS card
            CardPanel serverCard = new CardPanel(BgPanel, BorderCol);
            serverCard.Width = 260;
            serverCard.Height = 130;
            serverCard.Controls.Add(CardTitle("SERVER STATUS"));
            Label worldRow = new Label();
            worldRow.Text = "World Server";
            worldRow.AutoSize = false;
            worldRow.Dock = DockStyle.Top;
            worldRow.Height = 24;
            worldRow.ForeColor = FgMain;
            worldRow.Margin = new Padding(0, 30, 0, 0);
            serverCard.Controls.Add(worldRow);
            lblWorld = StatusValue();
            lblWorld.Dock = DockStyle.Top;
            serverCard.Controls.Add(lblWorld);
            Label authRow = new Label();
            authRow.Text = "Auth Server";
            authRow.AutoSize = false;
            authRow.Dock = DockStyle.Top;
            authRow.Height = 24;
            authRow.ForeColor = FgMain;
            serverCard.Controls.Add(authRow);
            lblAuth = StatusValue();
            lblAuth.Dock = DockStyle.Top;
            serverCard.Controls.Add(lblAuth);
            worldRow.BringToFront();
            lblWorld.BringToFront();
            authRow.BringToFront();
            lblAuth.BringToFront();
            outer.Controls.Add(serverCard);

            // PLAYERBOTS card
            CardPanel botsCard = new CardPanel(BgPanel, BorderCol);
            botsCard.Width = 260;
            botsCard.Height = 204;
            botsCard.Controls.Add(CardTitle("PLAYERBOTS"));
            lblBotCurDash = HeadLabel("Current: ...", FgMain);
            lblBotCurDash.AutoSize = false;
            lblBotCurDash.Dock = DockStyle.Top;
            lblBotCurDash.Height = 40;
            botsCard.Controls.Add(lblBotCurDash);
            lblOnlineCounts = HeadLabel("Online: ...", AccCyan);
            lblOnlineCounts.AutoSize = false;
            lblOnlineCounts.Dock = DockStyle.Top;
            lblOnlineCounts.Height = 24;
            botsCard.Controls.Add(lblOnlineCounts);
            lblHorde = HeadLabel("Horde: ...", AccRed);
            lblAlliance = HeadLabel("Alliance: ...", AccCyan);
            lblAlliance.Margin = new Padding(12, 12, 0, 2);
            FlowLayoutPanel factionRow = Row(lblHorde, lblAlliance);
            factionRow.Dock = DockStyle.Top;
            botsCard.Controls.Add(factionRow);
            Control jumpToBots = Btn("Open Bot Config", AccCyan, delegate { JumpToSection("Playerbots"); });
            jumpToBots.Dock = DockStyle.Top;
            botsCard.Controls.Add(jumpToBots);
            lblBotCurDash.BringToFront();
            lblOnlineCounts.BringToFront();
            factionRow.BringToFront();
            jumpToBots.BringToFront();
            outer.Controls.Add(botsCard);

            // LLM BRIDGE & OLLAMA card (only if this build has LLM chatter
            // installed) - merged into one card since the two work
            // together, matching the Linux menu's layout.
            if (installLLM)
            {
                CardPanel llmCard = new CardPanel(BgPanel, BorderCol);
                llmCard.Width = 260;
                llmCard.Height = 190;
                llmCard.Controls.Add(CardTitle("LLM BRIDGE & OLLAMA"));

                Label ollamaRow = new Label();
                ollamaRow.Text = "Ollama";
                ollamaRow.AutoSize = false;
                ollamaRow.Dock = DockStyle.Top;
                ollamaRow.Height = 24;
                ollamaRow.ForeColor = FgMain;
                ollamaRow.Margin = new Padding(0, 30, 0, 0);
                llmCard.Controls.Add(ollamaRow);
                lblOllama.Dock = DockStyle.Top;
                llmCard.Controls.Add(lblOllama);

                Label statusRow = new Label();
                statusRow.Text = "Bridge";
                statusRow.AutoSize = false;
                statusRow.Dock = DockStyle.Top;
                statusRow.Height = 24;
                statusRow.ForeColor = FgMain;
                llmCard.Controls.Add(statusRow);
                lblBridge.Dock = DockStyle.Top;
                llmCard.Controls.Add(lblBridge);

                Label modelRow = new Label();
                modelRow.Text = "Model";
                modelRow.AutoSize = false;
                modelRow.Dock = DockStyle.Top;
                modelRow.Height = 24;
                modelRow.ForeColor = FgMain;
                llmCard.Controls.Add(modelRow);
                lblDashModel = StatusValue();
                lblDashModel.Dock = DockStyle.Top;
                llmCard.Controls.Add(lblDashModel);

                // BringToFront order determines Dock=Top stacking order -
                // whichever is called earliest ends up positioned higher
                // (title stays backmost/topmost since it's never brought
                // to front at all). This list is the visual top-to-bottom
                // row order: Ollama, then Bridge, then Model.
                ollamaRow.BringToFront();
                lblOllama.BringToFront();
                statusRow.BringToFront();
                lblBridge.BringToFront();
                modelRow.BringToFront();
                lblDashModel.BringToFront();
                outer.Controls.Add(llmCard);
            }

            return WrapInPanel(outer);
        }

        // Simple wrapper so the FlowLayoutPanel dashboard content matches
        // the Panel return type every other Build*Tab() method uses.
        Panel WrapInPanel(Control c)
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = BgDark;
            c.Dock = DockStyle.Fill;
            p.Controls.Add(c);
            return p;
        }

        Label CardTitle(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.Dock = DockStyle.Top;
            l.Height = 24;
            l.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            l.ForeColor = AccCyan;
            return l;
        }

        // Wraps a section's title + content rows into a card matching the
        // Dashboard's exact visual style (rounded corners, blue-glow
        // border), for reuse across every tab instead of the plain
        // HeadLabel-only groupings used before.
        //
        // IMPORTANT: CardPanel derives from plain Panel, and Panel never
        // overrides GetPreferredSize - so setting AutoSize=true on the
        // CardPanel itself does NOTHING; the card silently kept whatever
        // size it started at (near zero), which is exactly why these cards
        // rendered as thin empty capsules (just the rounded-corner arcs
        // with no room for content) instead of matching the Dashboard.
        // Fix: measure the inner FlowLayoutPanel's real preferred height
        // (FlowLayoutPanel DOES override GetPreferredSize correctly) and
        // size the card explicitly from that measurement.
        Panel SectionCard(string title, params Control[] rows)
        {
            CardPanel card = new CardPanel(BgPanel, BorderCol);
            // No Dock here - this gets added into a FlowLayoutPanel (the
            // tab's outer flow from NewTab), which ignores Dock on its
            // children entirely, same as SidebarButton needed fixing for
            // navFlow earlier. Explicit Width/Height + Margin is what
            // actually works inside a FlowLayoutPanel.
            card.AutoSize = false;
            card.Margin = new Padding(0, 0, 0, 16);
            card.Width = 860;

            Label titleLbl = CardTitle(title);
            card.Controls.Add(titleLbl);

            FlowLayoutPanel inner = new FlowLayoutPanel();
            inner.FlowDirection = FlowDirection.TopDown;
            inner.WrapContents = false;
            inner.AutoSize = true;
            inner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            inner.Dock = DockStyle.Top;
            inner.BackColor = BgPanel;
            inner.Margin = new Padding(0, 30, 0, 0);
            foreach (Control r in rows) inner.Controls.Add(r);
            card.Controls.Add(inner);

            // Dock-layout processes Top-docked siblings back-to-front, so
            // whichever control is brought to front LAST actually docks
            // LAST (i.e. ends up lower, not higher) - the opposite of what
            // you'd guess. Bringing only "inner" to front leaves titleLbl
            // as the back-most control, so it's processed first and lands
            // at the very top, with inner's content correctly below it.
            inner.BringToFront();

            // Explicitly size the card now that its real content is known -
            // proposed width excludes the card's own left/right padding so
            // the measurement matches what inner will actually get once
            // docked, then add the title bar + top margin + padding back in.
            int proposedWidth = card.Width - card.Padding.Horizontal;
            int contentHeight = inner.GetPreferredSize(new Size(proposedWidth, 0)).Height;
            card.Height = card.Padding.Vertical + titleLbl.Height + inner.Margin.Top + contentHeight + 10;

            return card;
        }

        // Switches the sidebar to whichever section has this title.
        void JumpToSection(string title)
        {
            for (int i = 0; i < navButtons.Count; i++)
            {
                if (navButtons[i].LabelText.Trim() == title) { ShowSection(i); return; }
            }
        }

        void StartAllServices()
        {
            Log("[INFO] Starting all services...");
            if (File.Exists(MariaDbd)) StartMaria();
            StartServer(WorldExe, "worldserver", "World server");
            StartServer(AuthExe, "authserver", "Auth server");
            if (installLLM)
            {
                StartOllama();
                StartBridge();
            }
        }

        void StopAllServices()
        {
            Log("[INFO] Stopping all services...");
            if (installLLM) StopBridge();
            StopByName("worldserver", "World server");
            StopByName("authserver", "Auth server");
        }

        // Refreshes dashboard-specific labels not already covered by the
        // regular RefreshStatus()/RefreshBotLabels() calls.
        void RefreshDashboard()
        {
            if (lblDashModel != null)
            {
                string current = File.Exists(LlmConf) ? GetConfValue(LlmConf, "LLMChatter.Model") : null;
                lblDashModel.Text = string.IsNullOrEmpty(current) ? "not set" : current;
            }
            RefreshOnlineCounts();
        }

        // Distinguishes bot characters from real players by account
        // username - AzerothCore's playerbots module names its auto-created
        // random bot account pool RNDBOTn (confirmed from this server's own
        // account list), so that's a reliable, simple filter without needing
        // a dedicated "is bot" column anywhere. Runs on a background thread
        // since it's a live DB query - same pattern as other MySql() calls.
        void RefreshOnlineCounts()
        {
            if (lblOnlineCounts == null) return;
            if (mysqlExe == null || !File.Exists(MyCnfFile))
            {
                lblOnlineCounts.Text = "Online: (DB not available)";
                if (lblHorde != null) lblHorde.Text = "Horde: ?";
                if (lblAlliance != null) lblAlliance.Text = "Alliance: ?";
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                string botQuery =
                    "SELECT COUNT(*) FROM acore_characters.characters c " +
                    "JOIN acore_auth.account a ON c.account = a.id " +
                    "WHERE c.online = 1 AND a.username LIKE 'RNDBOT%';";
                string playerQuery =
                    "SELECT COUNT(*) FROM acore_characters.characters c " +
                    "JOIN acore_auth.account a ON c.account = a.id " +
                    "WHERE c.online = 1 AND a.username NOT LIKE 'RNDBOT%';";
                // Horde: Orc(2) Undead(5) Tauren(6) Troll(8) BloodElf(10)
                // Alliance: Human(1) Dwarf(3) NightElf(4) Gnome(7) Draenei(11)
                // COALESCE handles the zero-characters-online case, where a
                // plain SUM() over no rows returns NULL instead of 0.
                string factionQuery =
                    "SELECT COALESCE(SUM(CASE WHEN race IN (2,5,6,8,10) THEN 1 ELSE 0 END),0), " +
                    "COALESCE(SUM(CASE WHEN race IN (1,3,4,7,11) THEN 1 ELSE 0 END),0) " +
                    "FROM acore_characters.characters WHERE online = 1;";

                string botResult = RunCapture(mysqlExe,
                    "--defaults-extra-file=\"" + MyCnfFile + "\" -N -e \"" + botQuery.Replace("\"", "\\\"") + "\"", null);
                string playerResult = RunCapture(mysqlExe,
                    "--defaults-extra-file=\"" + MyCnfFile + "\" -N -e \"" + playerQuery.Replace("\"", "\\\"") + "\"", null);
                string factionResult = RunCapture(mysqlExe,
                    "--defaults-extra-file=\"" + MyCnfFile + "\" -N -e \"" + factionQuery.Replace("\"", "\\\"") + "\"", null);

                string bots = botResult.Trim();
                string players = playerResult.Trim();
                string display = (string.IsNullOrEmpty(bots) ? "?" : bots) + " bots, "
                    + (string.IsNullOrEmpty(players) ? "?" : players) + " players online";

                string[] factionParts = factionResult.Trim().Split('\t');
                string horde = factionParts.Length > 0 ? factionParts[0].Trim() : "?";
                string alliance = factionParts.Length > 1 ? factionParts[1].Trim() : "?";

                SafeInvoke(delegate
                {
                    lblOnlineCounts.Text = "Online: " + display;
                    if (lblHorde != null) lblHorde.Text = "Horde: " + horde;
                    if (lblAlliance != null) lblAlliance.Text = "Alliance: " + alliance;
                });
            });
        }

        Panel BuildServerTab()
        {
            FlowLayoutPanel f;
            Panel p = NewTab("Server", out f);

            // Portable MariaDB controls only appear when the repack bundles it.
            if (File.Exists(MariaDbd))
            {
                f.Controls.Add(SectionCard("DATABASE (portable MariaDB)",
                    Row(
                        Btn("Start MariaDB", AccGreen, delegate { StartMaria(); }),
                        Btn("Stop MariaDB", AccRed, delegate { StopMaria(); }))));
            }

            f.Controls.Add(SectionCard("SERVER",
                Row(
                    Btn("Start World server", AccGreen, delegate { StartServer(WorldExe, "worldserver", "World server"); Log("[INFO] Wait for AC> prompt, then start Auth"); }),
                    Btn("Start Auth server", AccGreen, delegate { StartServer(AuthExe, "authserver", "Auth server"); }),
                    Btn("Stop World server", AccRed, delegate { StopByName("worldserver", "World server"); }),
                    Btn("Stop Auth server", AccRed, delegate { StopByName("authserver", "Auth server"); }))));

            return p;
        }

        Panel BuildLlmTab()
        {
            FlowLayoutPanel f;
            Panel p = NewTab("LLM Bridge", out f);

            // -- AI MODEL (pick installed, or pull new) ----------------------
            // Ollama itself is installed system-wide by stage 05. The picker
            // lists already-pulled models - selecting one just configures it
            // (instant, no pull needed). The text field + Pull Model button
            // handles fetching something not installed yet. Either path ends
            // by writing LLMChatter.Model in mod_llm_chatter.conf to match.
            // Fully custom dropdown - a native ComboBox's popup list and
            // drop arrow are drawn by Windows itself and ignore BackColor/
            // ForeColor no matter what's set (that's what the old owner-draw
            // workaround here was fighting, and never fully won - the arrow
            // glyph stayed a light system square). DarkCombo sidesteps all
            // of that the same way DarkSpinner does: fully custom-drawn,
            // matching rounded corners and the same blue accent arrow.
            cmbInstalledModels = new DarkCombo(BgCtl, FgMain);
            cmbInstalledModels.Width = 260;
            cmbInstalledModels.Margin = new Padding(0, 8, 8, 3);
            cmbInstalledModels.SelectedIndexChanged += delegate { ConfigureSelectedModel(); };

            Label pickHint = HeadLabel("Pick an already-installed model above to switch to it instantly.", AccGray);
            pickHint.Font = new Font("Segoe UI", 8.25F);

            txtModelName = Txt(200, "model name");
            txtModelName.Text = "mistral-nemo:12b";
            btnInstallAi = Btn("Pull Model", AccGreen, delegate { PullModel(); });
            Label modelHint = HeadLabel("New model? Type its name and Pull. Examples: mistral-nemo:12b | HammerAI/mn-mag-mell-r1 | llama3.2:3b", AccGray);
            modelHint.Font = new Font("Segoe UI", 8.25F);

            f.Controls.Add(SectionCard("AI MODEL",
                Row(cmbInstalledModels,
                    Btn("Refresh list", AccCyan, delegate { RefreshInstalledModels(); }),
                    Btn("Check current model", AccCyan, delegate { ReportAiStatus(); }),
                    Btn("Delete selected", AccRed, delegate { DeleteSelectedModel(); })),
                pickHint,
                Row(txtModelName, btnInstallAi),
                modelHint));

            // -- CHATTER TUNING (presets + manual override) ------------------
            // The module's shipped defaults are very conservative (bots can
            // go quiet for up to 30 minutes, and proximity detection uses
            // the full 40-yard WoW /say range, which can let a distant bot
            // answer instead of the one standing next to you). "Sea-Trialed
            // Tuning" is a real combo confirmed working well on a live
            // server over extended testing - not a guess. Manual fields
            // below let anyone override individual values if they want to
            // experiment further.
            lblChatterTuning = HeadLabel("Pick a preset, or set values manually below.", AccGray);

            numAmbientGossip     = TuningSpinner(0, 3600, 300);
            numScanRadius        = TuningSpinner(5, 40, 10);
            numEntityCooldown    = TuningSpinner(0, 600, 30);
            numPlayerSayRadius   = TuningSpinner(5, 40, 10);
            numGroupMsgCooldown  = TuningSpinner(0, 300, 15);
            numGeneralChatCooldown = TuningSpinner(0, 300, 10);
            numBotSpeakerCooldown  = TuningSpinner(0, 3600, 180);

            Label tuningRestartHint = HeadLabel("Restart LLM bridge after any change for it to take effect.", AccGray);
            tuningRestartHint.Font = new Font("Segoe UI", 8.25F);

            f.Controls.Add(SectionCard("CHATTER TUNING",
                lblChatterTuning,
                Row(
                    Btn("Default (shipped)", AccGray, delegate { ApplyChatterPreset(false); }),
                    Btn("Sea-Trialed Tuning", AccGreen, delegate { ApplyChatterPreset(true); })),
                TuningRow("Ambient gossip cooldown (sec):", numAmbientGossip),
                TuningRow("Proximity scan radius (yards):", numScanRadius),
                TuningRow("Proximity entity cooldown (sec):", numEntityCooldown),
                TuningRow("Player /say scan radius (yards):", numPlayerSayRadius),
                TuningRow("Group msg cooldown (sec):", numGroupMsgCooldown),
                TuningRow("General chat cooldown (sec):", numGeneralChatCooldown),
                TuningRow("Bot speaker cooldown (sec):", numBotSpeakerCooldown),
                Row(Btn("Save Custom Values", AccGreen, delegate { SaveChatterTuning(); })),
                tuningRestartHint));

            f.Controls.Add(SectionCard("OLLAMA",
                Row(
                    Btn("Start Ollama", AccGreen, delegate { StartOllama(); }),
                    Btn("Stop Ollama", AccRed, delegate { StopOllama(); }),
                    Btn("List models", AccCyan, delegate { ListModels(); }))));

            f.Controls.Add(SectionCard("BRIDGE",
                Row(
                    Btn("Start LLM bridge", AccGreen, delegate { StartBridge(); }),
                    Btn("Stop LLM bridge", AccRed, delegate { StopBridge(); }),
                    Btn("Restart LLM bridge", AccCyan, delegate { StopBridge(); Thread.Sleep(800); StartBridge(); }),
                    Btn("Open LLM config", AccGray, delegate { OpenInNotepad(LlmConf); }))));
            return p;
        }

        Panel BuildBotsTab()
        {
            FlowLayoutPanel f;
            Panel p = NewTab("Playerbots", out f);

            lblBotCur = HeadLabel("Current: ...", FgMain);
            lblBotCur.Font = new Font("Segoe UI", 9F);

            numBots = new DarkSpinner(BgCtl, FgMain);
            numBots.Minimum = 1;
            numBots.Maximum = 5000;
            numBots.Value = 500;
            numBots.Width = 90;
            numBots.BackColor = BgCtl;
            numBots.ForeColor = FgMain;
            numBots.Margin = new Padding(0, 8, 8, 0);

            Label hint = HeadLabel("Hint: 8GB RAM -> 50-100 | 16GB -> 200+ | beefy rig -> go wild", AccGray);
            hint.Font = new Font("Segoe UI", 8.25F);

            f.Controls.Add(SectionCard("PLAYERBOT SETTINGS",
                lblBotCur,
                Row(numBots,
                    Btn("Set bot count", AccGreen, delegate { SetBotCount(); })),
                hint,
                Row(
                    Btn("Bots only with players", AccCyan, delegate { SetBotsOnlyWithPlayers(); }),
                    Btn("Bots always online", AccCyan, delegate { SetBotsAlwaysOnline(); }),
                    Btn("Show current settings", AccCyan, delegate { ShowBotSettings(); })),
                Row(
                    Btn("Edit playerbots.conf", AccGray, delegate { if (EnsurePlayerbotsConf()) OpenInNotepad(PlayerbotsConf); }),
                    Btn("Edit worldserver.conf", AccGray, delegate { OpenInNotepad(WorldConf); }))));
            return p;
        }

        Panel BuildAdminTab()
        {
            FlowLayoutPanel f;
            Panel p = NewTab("Admin & Monitoring", out f);

            txtGmUser = Txt(130, "username");
            txtGmPass = Txt(130, "password");
            txtGmPass.UseSystemPasswordChar = true;

            f.Controls.Add(SectionCard("ACCOUNT MENU",
                Row(txtGmUser, txtGmPass,
                    Btn("GM account", AccGreen, delegate { GmCommands(true); }),
                    Btn("Normal account", AccCyan, delegate { GmCommands(false); }))));

            txtChar = Txt(130, "character name");
            txtIp = Txt(130, "IP address");
            txtAhGuid = Txt(130, "Alibaba GUID");

            f.Controls.Add(SectionCard("DATABASE",
                Row(txtIp,
                    Btn("Update realmlist IP", AccCyan, delegate { UpdateRealmlist(); })),
                Row(txtChar,
                    Btn("Get char GUID", AccCyan, delegate { CharGuid(); })),
                Row(txtAhGuid,
                    Btn("Set AH bot GUID", AccCyan, delegate { SetAhGuid(); })),
                Row(
                    Btn("Server info", AccGreen, delegate { ServerInfo(); }),
                    Btn("Reagent bank SQL", AccGray, delegate { ReagentBankSql(); }))));

            f.Controls.Add(SectionCard("MONITORING",
                Row(
                    Btn("Open latest log", AccCyan, delegate { OpenLatestLog(); }),
                    Btn("Watch GPU", AccCyan, delegate { WatchGpu(); }),
                    Btn("System uptime", AccCyan, delegate { ShowUptime(); }))));

            f.Controls.Add(SectionCard("MAINTENANCE",
                Row(
                    Btn("Update WOTLK-Menu", AccRed, delegate { UpdateFromGitea(); }))));
            return p;
        }

        Panel BuildBackupTab()
        {
            FlowLayoutPanel f;
            Panel p = NewTab("Backup", out f);

            lstBackups = new ListBox();
            lstBackups.Width = 520;
            lstBackups.Height = 190;
            lstBackups.BackColor = BgCtl;
            lstBackups.ForeColor = FgMain;
            lstBackups.BorderStyle = BorderStyle.FixedSingle;
            lstBackups.Margin = new Padding(0, 8, 0, 8);

            lblBackupDir = HeadLabel("Folder: " + BackupDir, AccGray);

            // Narrower than the shared Btn() default (170px) on purpose -
            // "Folder" doesn't need that much width, and the full-size
            // button was pushing this row past the card's edge next to
            // the path label. Overriding just this instance rather than
            // touching Btn() itself, which every other button relies on.
            Control btnChooseFolder = Btn("Folder", AccCyan, delegate { ChooseBackupFolder(); });
            btnChooseFolder.MinimumSize = new Size(80, 34);

            f.Controls.Add(SectionCard("BACKUP && RESTORE",
                Row(lblBackupDir, btnChooseFolder),
                Row(
                    Btn("Backup accounts", AccGreen, delegate { BackupAccount(); }),
                    Btn("Backup configs", AccGreen, delegate { BackupConfigs(); }),
                    Btn("Refresh list", AccCyan, delegate { RefreshBackupList(); })),
                lstBackups,
                Row(
                    Btn("Restore selected", AccCyan, delegate { RestoreSelected(); }),
                    Btn("Delete selected", AccRed, delegate { DeleteSelected(); }),
                    Btn("Delete ALL", AccRed, delegate { DeleteAllBackups(); }))));
            return p;
        }

        // Folder picker for the backups location - persists via
        // SetBackupDir (backup-dir.txt next to the pin state), refreshes
        // the visible path label, and reloads the list so it reflects
        // whatever's already in the newly chosen folder.
        void ChooseBackupFolder()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Choose a folder to store backups in";
                dlg.SelectedPath = Directory.Exists(BackupDir) ? BackupDir : DefaultBackupDir;
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    SetBackupDir(dlg.SelectedPath);
                    lblBackupDir.Text = "Folder: " + BackupDir;
                    Log("[OK] Backup folder set to " + BackupDir);
                    RefreshBackupList();
                }
            }
        }

        // -- Small control factories --------------------------------------
        Control Btn(string text, Color accent, EventHandler onClick)
        {
            RoundedButton b = new RoundedButton();
            b.Text = text;
            b.AutoSize = true;
            b.MinimumSize = new Size(170, 34);
            b.BackColor = BgCtl;
            b.ForeColor = accent;
            b.Margin = new Padding(0, 3, 8, 3);
            b.Click += onClick;
            return b;
        }

        // Clips a control to a rounded-rectangle Region, giving it rounded
        // corners without needing a fully custom-drawn replacement control.
        // Re-applied on every Resize since AutoSize controls (like Button)
        // don't have their final Width/Height until after layout runs.
        void ApplyRoundedRegion(Control c, int radius)
        {
            EventHandler apply = delegate
            {
                if (c.Width > 0 && c.Height > 0)
                {
                    using (System.Drawing.Drawing2D.GraphicsPath path =
                        CardPanel.RoundedRectPath(new Rectangle(0, 0, c.Width, c.Height), radius))
                        c.Region = new Region(path);
                }
            };
            c.Resize += apply;
            apply(c, EventArgs.Empty);
        }

        TextBox Txt(int width, string placeholder)
        {
            TextBox t = new TextBox();
            t.Width = width;
            t.BackColor = BgCtl;
            t.ForeColor = FgMain;
            // Kept as a plain native single-pixel border rather than the
            // Region-clip rounding technique: clipping a native TextBox's
            // Region leaves its four corners completely unpainted by
            // anything, exposing whatever the backbuffer happened to hold
            // there - that's exactly what read as stray black/white corner
            // pixels. A native control's own square border never has that
            // failure mode, so text inputs trade a little roundness for
            // guaranteed-clean corners.
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Margin = new Padding(0, 8, 8, 3);
            return t;
        }

        DarkSpinner TuningSpinner(int min, int max, int defaultValue)
        {
            DarkSpinner n = new DarkSpinner(BgCtl, FgMain);
            n.Minimum = min;
            n.Maximum = max;
            n.Value = defaultValue;
            n.Width = 90;
            n.BackColor = BgCtl;
            n.ForeColor = FgMain;
            n.Margin = new Padding(0, 2, 8, 2);
            return n;
        }

        // DarkenSpinButtons() previously worked around native NumericUpDown
        // theming limitations - now that DarkSpinner is a fully custom
        // control with its own colors baked in via constructor, this
        // workaround is obsolete and has been removed entirely.

        FlowLayoutPanel TuningRow(string labelText, DarkSpinner spinner)
        {
            Label lbl = new Label();
            lbl.Text = labelText;
            lbl.AutoSize = false;
            lbl.Width = 220;
            lbl.Height = 20;
            lbl.ForeColor = FgMain;
            lbl.Margin = new Padding(0, 6, 8, 0);
            return Row(lbl, spinner);
        }

        // De-theming via SetWindowTheme("","") was tried here for the
        // ComboBox/DarkSpinner arrow glyph colors, but it destabilizes
        // the control's own rendering (threw "Visual Style handle creation
        // operation did not succeed" and produced broken red-X spin
        // buttons) - removed entirely. The light arrow glyphs are a
        // remaining minor cosmetic gap, safer than a crashing menu.

        // Tells Windows itself to render the native title bar in dark mode
        // - same technique VS Code / Windows Terminal use. The title bar is
        // OS window-chrome, not something WinForms draws, so this needs a
        // real DWM API call rather than any BackColor property.
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        const int DWMWA_USE_IMMERSIVE_DARK_MODE_20 = 20; // Windows 10 2004+ / Windows 11
        const int DWMWA_USE_IMMERSIVE_DARK_MODE_19 = 19; // Some earlier Windows 10 builds

        void EnableDarkTitleBar()
        {
            try
            {
                int enabled = 1;
                int result = DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_20, ref enabled, sizeof(int));
                if (result != 0)
                    DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_19, ref enabled, sizeof(int));
            }
            catch { }
        }

        // -- Console input injection (Windows tmux send-keys equivalent) --
        // Attaches to worldserver's console and writes keystrokes directly
        // into its input buffer. Works with conhost AND Windows Terminal,
        // no window focus needed, works even minimized.
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlags, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool WriteConsoleInputW(IntPtr hConsoleInput, INPUT_RECORD[] lpBuffer,
            uint nLength, out uint lpNumberOfEventsWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct KEY_EVENT_RECORD
        {
            public int bKeyDown;
            public ushort wRepeatCount;
            public ushort wVirtualKeyCode;
            public ushort wVirtualScanCode;
            public char UnicodeChar;
            public uint dwControlKeyState;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct INPUT_RECORD
        {
            [FieldOffset(0)] public ushort EventType;   // 1 = KEY_EVENT
            [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
        }

        static bool InjectConsoleCommand(int pid, string command)
        {
            FreeConsole(); // detach from any console we might hold
            if (!AttachConsole((uint)pid)) return false;
            try
            {
                IntPtr hIn = CreateFile("CONIN$",
                    0x40000000 | unchecked((uint)0x80000000), // GENERIC_WRITE | GENERIC_READ
                    3,          // FILE_SHARE_READ | FILE_SHARE_WRITE
                    IntPtr.Zero,
                    3,          // OPEN_EXISTING
                    0, IntPtr.Zero);
                if (hIn == new IntPtr(-1)) return false;
                try
                {
                    string line = command + "\r";
                    INPUT_RECORD[] recs = new INPUT_RECORD[line.Length * 2];
                    int idx = 0;
                    foreach (char c in line)
                    {
                        INPUT_RECORD down = new INPUT_RECORD();
                        down.EventType = 1; // KEY_EVENT
                        down.KeyEvent.bKeyDown = 1;
                        down.KeyEvent.wRepeatCount = 1;
                        down.KeyEvent.wVirtualKeyCode = c == '\r' ? (ushort)0x0D : (ushort)0;
                        down.KeyEvent.UnicodeChar = c;
                        recs[idx++] = down;
                        INPUT_RECORD up = down;
                        up.KeyEvent.bKeyDown = 0;
                        recs[idx++] = up;
                    }
                    uint written;
                    return WriteConsoleInputW(hIn, recs, (uint)recs.Length, out written) && written > 0;
                }
                finally { CloseHandle(hIn); }
            }
            finally { FreeConsole(); }
        }

        Label HeadLabel(string text, Color c)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = c;
            l.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            l.Margin = new Padding(0, 12, 0, 2);
            return l;
        }

        Label StatusCaption(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = AccGray;
            l.Margin = new Padding(6, 4, 2, 0);
            return l;
        }

        Label StatusValue()
        {
            Label l = new Label();
            l.Text = "...";
            l.AutoSize = true;
            l.ForeColor = AccGray;
            l.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            l.Margin = new Padding(0, 4, 10, 0);
            return l;
        }

        FlowLayoutPanel Row(params Control[] cs)
        {
            FlowLayoutPanel r = new FlowLayoutPanel();
            r.FlowDirection = FlowDirection.LeftToRight;
            r.AutoSize = true;
            r.WrapContents = true;
            r.Margin = new Padding(0, 2, 0, 2);
            // Explicit dark background - without this, Row() defaults to
            // the light system control color, which then peeks through
            // RoundedButton's corner cutouts (which fill using Parent.
            // BackColor) as a "strange" light artifact at each corner.
            r.BackColor = BgPanel;
            foreach (Control c in cs) r.Controls.Add(c);
            return r;
        }

        // ============================ STATUS ==========================
        void RefreshStatus()
        {
            SetStatus(lblWorld, IsRunning("worldserver"));
            SetStatus(lblAuth, IsRunning("authserver"));
            if (File.Exists(MariaDbd))
                SetStatus(lblMaria, IsRunning("mariadbd"));
            if (installLLM)
            {
                KickOllamaCheck(false);
                if (ollamaState == 1) { lblOllama.Text = "READY"; lblOllama.ForeColor = AccGreen; }
                else if (ollamaState == 2) { lblOllama.Text = "stopped"; lblOllama.ForeColor = AccGray; }
                Process bp = GetBridgeProcess();
                if (bp != null)
                {
                    lblBridge.Text = "RUNNING (PID " + bp.Id + ")";
                    lblBridge.ForeColor = AccGreen;
                    bp.Dispose();
                }
                else { lblBridge.Text = "stopped"; lblBridge.ForeColor = AccGray; }
            }
        }

        void SetStatus(Label l, bool running)
        {
            l.Text = running ? "RUNNING" : "stopped";
            l.ForeColor = running ? AccGreen : AccGray;
        }

        static bool IsRunning(string name)
        {
            Process[] ps = Process.GetProcessesByName(name);
            foreach (Process p in ps) p.Dispose();
            return ps.Length > 0;
        }

        void KickOllamaCheck(bool force)
        {
            if (ollamaChecking) return;
            if (!force && (DateTime.Now - ollamaChecked).TotalSeconds < 10) return;
            ollamaChecking = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:11434/api/tags");
                    req.Timeout = 1500;
                    using (WebResponse resp = req.GetResponse()) { ok = true; }
                }
                catch { ok = false; }
                ollamaState = ok ? 1 : 2;
                ollamaChecked = DateTime.Now;
                ollamaChecking = false;
                SafeInvoke(delegate { RefreshStatus(); });
            });
        }

        void SafeInvoke(MethodInvoker m)
        {
            try { if (IsHandleCreated) BeginInvoke(m); }
            catch { }
        }

        // ============================ DATABASE ========================
        // Start/Stop the portable MariaDB bundled in the repack. Unlike a
        // detached .bat (which vanishes into a silent black window), this
        // launches mariadbd with output captured, checks the data dir is
        // initialized, watches for the port to come up, and reports the
        // real error in the GUI log if anything goes wrong.
        static bool IsPortOpen(int port)
        {
            try
            {
                using (System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect("127.0.0.1", port, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(800);
                    if (ok) { try { c.EndConnect(ar); } catch { return false; } return c.Connected; }
                    return false;
                }
            }
            catch { return false; }
        }

        void StartMaria()
        {
            if (!File.Exists(MariaDbd)) { Log("[FAIL] Portable MariaDB not found at " + MariaDbd); return; }
            if (IsRunning("mariadbd") || IsPortOpen(3306))
            {
                Log("[WARN] A database is already running on port 3306");
                Log("[INFO] If that's the system MySQL, stop its service first to avoid a clash");
                RefreshStatus();
                return;
            }
            // Data dir must be initialized (has a 'mysql' system schema folder).
            if (!Directory.Exists(Path.Combine(MariaDataDir, "mysql")))
            {
                Log("[FAIL] MariaDB data dir not initialized: " + MariaDataDir);
                Log("[INFO] Expected a 'mysql' folder inside data\\ - the repack build didn't finish DB init");
                return;
            }

            Log("[INFO] Starting portable MariaDB...");
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(MariaDbd);
                // --console makes startup errors visible on the process's own
                // stream; we redirect + drain them into the GUI log so a bad
                // start shows a reason instead of a black window.
                string iniArg = File.Exists(MariaIni) ? "--defaults-file=\"" + MariaIni + "\" " : "";
                psi.Arguments = iniArg + "--datadir=\"" + MariaDataDir + "\" --console";
                psi.WorkingDirectory = MariaBin;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                Process p = Process.Start(psi);

                // Drain both streams so mariadbd can't block, echoing early
                // lines (errors/warnings) into the log.
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        string ln;
                        while ((ln = p.StandardError.ReadLine()) != null)
                        {
                            string cap = ln;
                            if (cap.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0
                                || cap.IndexOf("[Warning]", StringComparison.OrdinalIgnoreCase) >= 0
                                || cap.IndexOf("ready for connections", StringComparison.OrdinalIgnoreCase) >= 0)
                                SafeInvoke(delegate { Log("  " + cap); });
                        }
                    }
                    catch { }
                });
                ThreadPool.QueueUserWorkItem(delegate { try { while (p.StandardOutput.ReadLine() != null) { } } catch { } });

                // Watch for the port to accept connections (up to ~15s).
                ThreadPool.QueueUserWorkItem(delegate
                {
                    for (int i = 0; i < 15; i++)
                    {
                        Thread.Sleep(1000);
                        if (IsPortOpen(3306))
                        {
                            SafeInvoke(delegate { Log("[OK] MariaDB is up on 127.0.0.1:3306"); RefreshStatus(); });
                            return;
                        }
                        if (p.HasExited)
                        {
                            SafeInvoke(delegate { Log("[FAIL] MariaDB exited during startup (code " + p.ExitCode + ") - see lines above"); RefreshStatus(); });
                            return;
                        }
                    }
                    SafeInvoke(delegate { Log("[WARN] MariaDB started but port 3306 not open yet - check the lines above"); RefreshStatus(); });
                });
            }
            catch (Exception ex)
            {
                Log("[FAIL] Could not start MariaDB: " + ex.Message);
            }
        }

        void StopMaria()
        {
            if (!IsRunning("mariadbd") && !IsPortOpen(3306)) { Log("[WARN] MariaDB not running"); return; }
            Log("[INFO] Stopping MariaDB...");
            // Prefer a clean shutdown via mariadb-admin so InnoDB flushes.
            bool clean = false;
            if (File.Exists(MariaAdmin))
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(MariaAdmin);
                    string iniArg = File.Exists(MariaIni) ? "--defaults-file=\"" + MariaIni + "\" " : "";
                    psi.Arguments = iniArg + "-u root shutdown";
                    psi.WorkingDirectory = MariaBin;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardError = true;
                    psi.RedirectStandardOutput = true;
                    using (Process p = Process.Start(psi))
                    {
                        p.StandardError.ReadToEnd();
                        p.StandardOutput.ReadToEnd();
                        p.WaitForExit(8000);
                        clean = p.HasExited && p.ExitCode == 0;
                    }
                }
                catch { clean = false; }
            }
            // Give it a moment; if still up, hard-kill as a fallback.
            for (int i = 0; i < 5 && (IsRunning("mariadbd") || IsPortOpen(3306)); i++) Thread.Sleep(600);
            if (IsRunning("mariadbd"))
            {
                Process[] ps = Process.GetProcessesByName("mariadbd");
                foreach (Process p in ps) { try { p.Kill(); } catch { } p.Dispose(); }
                Log(clean ? "[OK] MariaDB stopped" : "[OK] MariaDB force-stopped");
            }
            else
            {
                Log("[OK] MariaDB stopped");
            }
            RefreshStatus();
        }

        // ============================ SERVER ==========================
        void StartServer(string exe, string procName, string label)
        {
            if (IsRunning(procName)) { Log("[WARN] " + label + " already running"); return; }
            if (!File.Exists(exe)) { Log("[FAIL] Not found: " + exe); return; }
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.WorkingDirectory = InstallDir;
            psi.UseShellExecute = true;
            Process.Start(psi);
            Log("[OK] " + label + " starting in its own window!");
        }

        void StopByName(string procName, string label)
        {
            Process[] ps = Process.GetProcessesByName(procName);
            if (ps.Length == 0) { Log("[WARN] " + label + " not running"); return; }
            foreach (Process p in ps) { try { p.Kill(); } catch { } p.Dispose(); }
            Log("[OK] " + label + " stopped");
        }

        void OpenLatestLog()
        {
            if (!Directory.Exists(LogsDir)) { Log("[FAIL] No logs dir: " + LogsDir); return; }
            string newest = null;
            DateTime newestTime = DateTime.MinValue;
            foreach (string fpath in Directory.GetFiles(LogsDir, "*.log"))
            {
                DateTime w = File.GetLastWriteTime(fpath);
                if (w > newestTime) { newestTime = w; newest = fpath; }
            }
            if (newest == null) { Log("[FAIL] No logs found in " + LogsDir); return; }
            Process.Start("powershell", "-NoExit -Command Get-Content '" + newest + "' -Wait -Tail 50");
            Log("[OK] Log window opened: " + Path.GetFileName(newest));
        }

        void WatchGpu()
        {
            if (FindOnPath("nvidia-smi.exe") != null || FindOnPath("nvidia-smi") != null)
            {
                Process.Start("cmd", "/k nvidia-smi -l 2");
                Log("[OK] GPU watch window opened (refresh 2s)");
                return;
            }

            // AMD: no nvidia-smi equivalent ships with the driver, so try
            // amdgpu_top first (popular optional community tool, Linux +
            // Windows) if the user has it installed, then fall back to
            // Windows' own built-in "GPU Engine" performance counters via
            // typeperf - no install needed, works for AMD/Intel/Nvidia
            // alike since it's the same DXGI-based data Task Manager's own
            // GPU graph reads from.
            string amdTop = FindOnPath("amdgpu_top.exe");
            if (amdTop == null) amdTop = FindOnPath("amdgpu_top");
            if (amdTop != null)
            {
                Process.Start("cmd", "/k \"" + amdTop + "\"");
                Log("[OK] AMD GPU watch window opened (amdgpu_top)");
                return;
            }

            if (FindOnPath("typeperf.exe") != null || FindOnPath("typeperf") != null)
            {
                Process.Start("cmd", "/k typeperf \"\\GPU Engine(*engtype_3D)\\Utilization Percentage\" -si 2");
                Log("[OK] GPU watch window opened via typeperf (refresh 2s) - works for AMD/Intel/Nvidia");
                return;
            }

            Log("[WARN] No GPU monitor found (nvidia-smi/amdgpu_top/typeperf) - CPU only rig?");
        }

        // -- Uptime / memory via kernel32 ---------------------------------
        [DllImport("kernel32")]
        static extern ulong GetTickCount64();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        [DllImport("kernel32", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buf);

        void ShowUptime()
        {
            TimeSpan up = TimeSpan.FromMilliseconds(GetTickCount64());
            string line = "Uptime: " + up.Days + "d " + up.Hours + "h " + up.Minutes + "m";
            MEMORYSTATUSEX mem = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(mem))
                line += "   Memory: " + (mem.ullAvailPhys / 1024.0 / 1024.0 / 1024.0).ToString("0.0")
                      + " GB free of " + (mem.ullTotalPhys / 1024.0 / 1024.0 / 1024.0).ToString("0.0") + " GB";
            Log("[INFO] " + line);
        }

        // ============================ LLM =============================
        bool IsOllamaReadyNow()
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:11434/api/tags");
                req.Timeout = 1000;
                using (WebResponse resp = req.GetResponse()) { return true; }
            }
            catch { return false; }
        }

        // -- Pull Model (system Ollama, any model) -------------------------
        // Ollama itself is installed system-wide by stage 05. This pulls
        // whatever model name is in the text field, then writes it into
        // mod_llm_chatter.conf so the bridge always matches what's actually
        // installed - mirrors the same picker + config-injection logic
        // that 05-ollama-setup.ps1 does on first install.
        void ReportAiStatus()
        {
            string current = GetConfValue(LlmConf, "LLMChatter.Model");
            if (string.IsNullOrEmpty(current))
            {
                Log("[INFO] No model configured yet in mod_llm_chatter.conf");
                return;
            }
            Log("[INFO] Configured model: " + current);
            if (ResolveOllamaExe() == null)
            {
                Log("[WARN] Ollama not found - run stage 05 (05-ollama-setup.ps1) first");
                return;
            }
            string list = RunCapture(ResolveOllamaExe(), "list", null);
            if (list.IndexOf(current.Split(':')[0], StringComparison.OrdinalIgnoreCase) >= 0)
                Log("[OK] Model appears to be pulled and ready");
            else
                Log("[WARN] Model not found in 'ollama list' - press Pull Model to fetch it");
        }

        // -- Chatter Tuning: presets + manual save ------------------------
        // Two presets, both backed by real values (not guesses):
        //   Default        = the module author's own documented defaults
        //   Sea-Trialed     = confirmed working well on a live server over
        //                     extended real-world testing
        // Manual save writes whatever's currently in the 7 spinner fields,
        // for anyone who wants to fine-tune beyond either preset.
        static readonly string[] TuningKeys = new string[] {
            "LLMChatter.AmbientGossipTargetCooldownSeconds",
            "LLMChatter.ProximityChatter.ScanRadius",
            "LLMChatter.ProximityChatter.EntityCooldown",
            "LLMChatter.ProximityChatter.PlayerSayScanRadius",
            "LLMChatter.GroupChatter.PlayerMsgCooldown",
            "LLMChatter.GeneralChat.Cooldown",
            "LLMChatter.BotSpeakerCooldownSeconds"
        };
        // Author-documented shipped defaults, confirmed from the .conf's
        // own comment blocks (# Default: NNNN above each setting).
        static readonly int[] TuningDefaults = new int[] { 1800, 40, 60, 40, 15, 30, 900 };
        // Confirmed working combo from extended real-world testing.
        static readonly int[] TuningSeaTrialed = new int[] { 300, 10, 30, 10, 15, 10, 180 };

        DarkSpinner[] TuningSpinners()
        {
            return new DarkSpinner[] {
                numAmbientGossip, numScanRadius, numEntityCooldown,
                numPlayerSayRadius, numGroupMsgCooldown, numGeneralChatCooldown,
                numBotSpeakerCooldown
            };
        }

        void ApplyChatterPreset(bool seaTrialed)
        {
            if (!File.Exists(LlmConf))
            {
                Log("[WARN] mod_llm_chatter.conf not found - run 04-setup-database.ps1 first");
                return;
            }
            int[] values = seaTrialed ? TuningSeaTrialed : TuningDefaults;
            for (int i = 0; i < TuningKeys.Length; i++)
                SetConfValue(LlmConf, TuningKeys[i], values[i].ToString());

            Log("[OK] Applied " + (seaTrialed ? "Sea-Trialed Tuning" : "Default") + " chatter preset");
            Log("[INFO] Restart LLM bridge for the change to take effect");
            RefreshChatterTuning();
        }

        void SaveChatterTuning()
        {
            if (!File.Exists(LlmConf))
            {
                Log("[WARN] mod_llm_chatter.conf not found - run 04-setup-database.ps1 first");
                return;
            }
            DarkSpinner[] spinners = TuningSpinners();
            // Same timing fix as SetBotCount() - force any just-typed
            // text into each spinner's Value before reading it, rather
            // than relying on the textbox's Leave event having already
            // fired by the time this button's Click handler runs.
            foreach (DarkSpinner s in spinners) s.CommitPendingEdit();
            for (int i = 0; i < TuningKeys.Length; i++)
                SetConfValue(LlmConf, TuningKeys[i], ((int)spinners[i].Value).ToString());

            Log("[OK] Custom chatter tuning saved");
            Log("[INFO] Restart LLM bridge for the change to take effect");
            RefreshChatterTuning();
        }

        void RefreshChatterTuning()
        {
            if (lblChatterTuning == null) return;
            if (!File.Exists(LlmConf))
            {
                lblChatterTuning.Text = "mod_llm_chatter.conf not found.";
                return;
            }
            DarkSpinner[] spinners = TuningSpinners();
            for (int i = 0; i < TuningKeys.Length; i++)
            {
                string val = GetConfValue(LlmConf, TuningKeys[i]);
                int v;
                if (!string.IsNullOrEmpty(val) && int.TryParse(val, out v)
                    && v >= (int)spinners[i].Minimum && v <= (int)spinners[i].Maximum)
                    spinners[i].Value = v;
            }
            lblChatterTuning.Text = "Pick a preset, or set values manually below.";
        }

        // Populate the picker with models already pulled locally (parses
        // 'ollama list' output - first column of each line after the header,
        // skipping the "NAME ..." header row itself).
        void RefreshInstalledModels()
        {
            string exe = ResolveOllamaExe();
            if (exe == null)
            {
                Log("[FAIL] Ollama not found - run stage 05 (05-ollama-setup.ps1) first");
                return;
            }
            string output = RunCapture(exe, "list", null);
            string[] lines = output.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            suppressModelPickEvent = true;
            cmbInstalledModels.Items.Clear();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase)) continue;
                // First whitespace-separated token on the line is the model name.
                int spaceIdx = line.IndexOfAny(new char[] { ' ', '\t' });
                string name = spaceIdx > 0 ? line.Substring(0, spaceIdx) : line;
                if (name.Length > 0) cmbInstalledModels.Items.Add(name);
            }
            suppressModelPickEvent = false;

            Log("[OK] Found " + cmbInstalledModels.Items.Count + " installed model(s)");

            // Pre-select whatever is currently configured, if it's in the list.
            string current = GetConfValue(LlmConf, "LLMChatter.Model");
            if (!string.IsNullOrEmpty(current) && cmbInstalledModels.Items.Contains(current))
            {
                suppressModelPickEvent = true;
                cmbInstalledModels.SelectedItem = current;
                suppressModelPickEvent = false;
            }
        }

        // Selecting a picker entry writes it straight into mod_llm_chatter.conf
        // - no pull needed since it's already local, so this is instant.
        void ConfigureSelectedModel()
        {
            if (suppressModelPickEvent) return;
            if (cmbInstalledModels.SelectedItem == null) return;
            string model = cmbInstalledModels.SelectedItem.ToString();

            if (!File.Exists(LlmConf))
            {
                Log("[WARN] mod_llm_chatter.conf not found - set LLMChatter.Model = " + model + " manually");
                return;
            }
            SetConfValue(LlmConf, "LLMChatter.Model", model);
            Log("[OK] Switched to " + model + " (mod_llm_chatter.conf updated)");
            Log("[INFO] Restart LLM bridge for the change to take effect");
        }

        // Removes a locally-pulled model via 'ollama rm' to free disk space.
        // Confirms first since this deletes the multi-GB model file - if the
        // deleted model happens to be the one currently configured, warns
        // that the bridge will fail until a different model is picked.
        void DeleteSelectedModel()
        {
            if (cmbInstalledModels.SelectedItem == null) { Log("[WARN] Select a model first"); return; }
            string model = cmbInstalledModels.SelectedItem.ToString();

            if (MessageBox.Show("Delete model '" + model + "'?\nThis frees disk space but cannot be undone - you'll need to pull it again to use it.",
                "Delete model", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            string exe = ResolveOllamaExe();
            if (exe == null) { Log("[FAIL] Ollama not found - run stage 05 (05-ollama-setup.ps1) first"); return; }

            string result = RunCapture(exe, "rm " + model, null);
            if (result.Length > 0) Log(result);
            Log("[OK] Deleted " + model);

            string current = GetConfValue(LlmConf, "LLMChatter.Model");
            if (!string.IsNullOrEmpty(current) && string.Equals(current, model, StringComparison.OrdinalIgnoreCase))
                Log("[WARN] That was the configured model - pick or pull a different one before starting the bridge");

            RefreshInstalledModels();
        }

        void PullModel()
        {
            if (aiInstalling) { Log("[WARN] A model pull is already running"); return; }
            string model = txtModelName.Text.Trim();
            if (model.Length == 0) { Log("[WARN] Enter a model name first"); return; }

            string exe = ResolveOllamaExe();
            if (exe == null)
            {
                Log("[FAIL] Ollama not found - run stage 05 (05-ollama-setup.ps1) first to install it");
                return;
            }

            aiInstalling = true;
            btnInstallAi.Text = "Pulling...";
            btnInstallAi.Enabled = false;
            Log("[INFO] Pulling model: " + model + " (this can take a while, several GB)...");

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (!IsOllamaReadyNow())
                    {
                        SafeInvoke(delegate { Log("[INFO] Starting Ollama, waiting for API..."); });
                        StartOllama();
                        bool up = false;
                        for (int i = 0; i < 20; i++)
                        {
                            Thread.Sleep(1000);
                            if (IsOllamaReadyNow()) { up = true; break; }
                        }
                        if (!up)
                        {
                            SafeInvoke(delegate { Log("[FAIL] Ollama server did not come up"); });
                            return;
                        }
                    }

                    int code = RunOllamaPull(model);
                    if (code != 0)
                    {
                        SafeInvoke(delegate { Log("[FAIL] Model pull returned exit code " + code); });
                        return;
                    }

                    // Write the model into mod_llm_chatter.conf so the bridge
                    // matches whatever was just pulled.
                    if (File.Exists(LlmConf))
                    {
                        SetConfValue(LlmConf, "LLMChatter.Model", model);
                        SafeInvoke(delegate { Log("[OK] mod_llm_chatter.conf updated to use " + model); });
                    }
                    else
                    {
                        SafeInvoke(delegate { Log("[WARN] mod_llm_chatter.conf not found - set LLMChatter.Model = " + model + " manually"); });
                    }

                    SafeInvoke(delegate
                    {
                        Log("[OK] Model " + model + " ready!");
                        Log("[INFO] Use Start LLM bridge to enable bot chat.");
                        RefreshInstalledModels();
                        txtModelName.Clear();
                    });
                }
                catch (Exception ex)
                {
                    SafeInvoke(delegate { Log("[FAIL] Model pull failed: " + ex.Message); });
                }
                finally
                {
                    aiInstalling = false;
                    SafeInvoke(delegate
                    {
                        btnInstallAi.Text = "Pull Model";
                        btnInstallAi.Enabled = true;
                        RefreshStatus();
                    });
                }
            });
        }

        // Run 'ollama pull <model>' with the system binary, returning the
        // process exit code. Output is streamed to the log.
        int RunOllamaPull(string model)
        {
            string exe = ResolveOllamaExe();
            if (exe == null) { Log("[FAIL] No Ollama binary to pull with"); return -1; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, "pull " + model);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    // Drain stderr on a worker (pull progress goes to stderr).
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            string lineE;
                            while ((lineE = p.StandardError.ReadLine()) != null)
                            {
                                string captured = lineE;
                                SafeInvoke(delegate { Log("  " + captured); });
                            }
                        }
                        catch { }
                    });
                    string lineO;
                    while ((lineO = p.StandardOutput.ReadLine()) != null)
                    {
                        string captured = lineO;
                        SafeInvoke(delegate { Log("  " + captured); });
                    }
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { Log("[FAIL] pull error: " + ex.Message); return -1; }
        }

        void StartOllama()
        {
            if (IsOllamaReadyNow()) { Log("[OK] Ollama already running"); ollamaState = 1; return; }
            string exe = ResolveOllamaExe();
            if (exe == null) { Log("[FAIL] Ollama not found - run stage 05 (05-ollama-setup.ps1) first"); return; }
            // Plain server binary (ollama.exe serve), NOT the tray UI wrapper -
            // the wrapper needs WebView2 and fails silently on VMs without it.
            ProcessStartInfo psi = new ProcessStartInfo(exe, "serve");
            psi.UseShellExecute = true;
            psi.WindowStyle = ProcessWindowStyle.Minimized;
            Process.Start(psi);
            Log("[INFO] Starting Ollama, waiting for API...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(1000);
                    if (IsOllamaReadyNow())
                    {
                        ollamaState = 1;
                        SafeInvoke(delegate { Log("[OK] Ollama ready!"); RefreshStatus(); });
                        return;
                    }
                }
                SafeInvoke(delegate { Log("[WARN] Ollama started but API not responding yet"); });
            });
        }

        void StopOllama()
        {
            Process[] ps = Process.GetProcessesByName("ollama");
            if (ps.Length == 0) { Log("[WARN] Ollama not running"); return; }
            foreach (Process p in ps) { try { p.Kill(); } catch { } p.Dispose(); }
            ollamaState = 2;
            Log("[OK] Ollama stopped");
        }

        void ListModels()
        {
            string exe = ResolveOllamaExe();
            if (exe == null) { Log("[FAIL] Ollama not found - run stage 05 (05-ollama-setup.ps1) first"); return; }
            Log(RunCapture(exe, "list", null));
        }

        Process GetBridgeProcess()
        {
            try
            {
                if (!File.Exists(BridgePid)) return null;
                string s = File.ReadAllText(BridgePid).Trim();
                int pid;
                if (!int.TryParse(s, out pid)) return null;
                return Process.GetProcessById(pid);
            }
            catch { return null; }
        }

        void StartBridge()
        {
            Process running = GetBridgeProcess();
            if (running != null) { running.Dispose(); Log("[WARN] LLM bridge already running"); return; }
            if (!File.Exists(BridgePy)) { Log("[FAIL] Bridge venv not found - run 06-llm-bridge-setup.ps1 first"); return; }
            if (!IsOllamaReadyNow()) { Log("[WARN] Ollama isn't responding - starting it first..."); StartOllama(); }
            ProcessStartInfo psi = new ProcessStartInfo(BridgePy,
                "\"" + BridgeScript + "\" --config \"" + LlmConf + "\"");
            psi.WorkingDirectory = BridgeDir;
            psi.UseShellExecute = true;
            Process p = Process.Start(psi);
            File.WriteAllText(BridgePid, p.Id.ToString());
            Log("[OK] LLM bridge started (PID " + p.Id + ") in its own window");
        }

        void StopBridge()
        {
            Process p = GetBridgeProcess();
            if (p == null) { Log("[WARN] LLM bridge not running"); return; }
            try { p.Kill(); } catch { }
            p.Dispose();
            try { File.Delete(BridgePid); } catch { }
            Log("[OK] LLM bridge stopped");
        }

        // ============================ BOTS ============================
        bool EnsurePlayerbotsConf()
        {
            if (File.Exists(PlayerbotsConf)) return true;
            if (File.Exists(PlayerbotsDist))
            {
                File.Copy(PlayerbotsDist, PlayerbotsConf);
                Log("[OK] Created playerbots.conf from .dist");
                return true;
            }
            Log("[FAIL] playerbots.conf.dist not found at " + PlayerbotsDist);
            return false;
        }

        void RefreshBotLabels()
        {
            if (!File.Exists(PlayerbotsConf))
            {
                lblBotCur.Text = "playerbots.conf missing - will be created from .dist on first change";
                lblBotCur.ForeColor = Color.Khaki;
                if (lblBotCurDash != null) { lblBotCurDash.Text = "not configured yet"; lblBotCurDash.ForeColor = Color.Khaki; }
                return;
            }
            string max = GetConfValue(PlayerbotsConf, "AiPlayerbot.MaxRandomBots");
            string idle = GetConfValue(PlayerbotsConf, "AiPlayerbot.DisabledWithoutRealPlayer");
            bool idleOn = idle == "1";
            string summary = "Current: " + (max != null ? max : "500") + " bots - "
                + (idleOn ? "only with players" : "always online");
            lblBotCur.Text = summary;
            lblBotCur.ForeColor = idleOn ? AccGreen : Color.Khaki;
            if (lblBotCurDash != null)
            {
                lblBotCurDash.Text = summary;
                lblBotCurDash.ForeColor = idleOn ? AccGreen : Color.Khaki;
            }
            int v;
            if (max != null && int.TryParse(max, out v) && v >= (int)numBots.Minimum && v <= (int)numBots.Maximum)
                numBots.Value = v;
        }

        void SetBotCount()
        {
            numBots.CommitPendingEdit();
            if (!EnsurePlayerbotsConf()) return;
            string count = ((int)numBots.Value).ToString();
            SetConfValue(PlayerbotsConf, "AiPlayerbot.MinRandomBots", count);
            SetConfValue(PlayerbotsConf, "AiPlayerbot.MaxRandomBots", count);
            Log("[OK] Bot count set to " + count);
            Log("[INFO] Restart World server to apply");
            RefreshBotLabels();
        }

        void SetBotsOnlyWithPlayers()
        {
            if (!EnsurePlayerbotsConf()) return;
            SetConfValue(PlayerbotsConf, "AiPlayerbot.DisabledWithoutRealPlayer", "1");
            Log("[OK] Bots now only spawn when real players are online");
            Log("[INFO] Restart World server to apply");
            RefreshBotLabels();
        }

        void SetBotsAlwaysOnline()
        {
            if (!EnsurePlayerbotsConf()) return;
            SetConfValue(PlayerbotsConf, "AiPlayerbot.DisabledWithoutRealPlayer", "0");
            Log("[OK] Bots now ALWAYS online");
            Log("[INFO] Restart World server to apply");
            RefreshBotLabels();
        }

        void ShowBotSettings()
        {
            if (!File.Exists(PlayerbotsConf)) { Log("[FAIL] playerbots.conf not found"); return; }
            Regex rx = new Regex(@"^AiPlayerbot\.(MinRandomBots|MaxRandomBots|DisabledWithoutRealPlayer|DisabledWithoutRealPlayerLoginDelay|DisabledWithoutRealPlayerLogoutDelay)\s*=");
            foreach (string line in File.ReadAllLines(PlayerbotsConf))
                if (rx.IsMatch(line)) Log("  " + line);
        }

        void CreateAccount(string mode)  // "gm", "normal", "ahbot"
        {
            string u = txtGmUser.Text.Trim();
            string pw = txtGmPass.Text.Trim();
            if (u.Length == 0 || pw.Length == 0) { Log("[WARN] Fill username + password first"); return; }

            string cmd = "account create " + u + " " + pw;
            string cmd2 = mode == "gm" ? "account set gmlevel " + u + " 3 -1" : "";

            // Inject straight into worldserver's console input buffer -
            // the Windows equivalent of tmux send-keys. No focus needed.
            Process[] procs = Process.GetProcessesByName("worldserver");
            if (procs.Length == 0) { Log("[FAIL] World server not running - start it first"); return; }
            int pid = procs[0].Id;
            foreach (Process p in procs) p.Dispose();

            bool ok = InjectConsoleCommand(pid, cmd);
            if (ok)
            {
                Log("[OK] Injected: " + cmd);
                if (cmd2.Length > 0)
                {
                    // A fixed delay here is unreliable: right after worldserver
                    // boots, it can be busy for a variable amount of time
                    // (e.g. auto-logging in playerbots), which can delay
                    // console command processing enough that "account set
                    // gmlevel" silently misses even with a generous sleep.
                    // Instead: inject it, then verify against the database
                    // and retry if it didn't take - this adapts to however
                    // long the server actually needs, rather than guessing.
                    Thread.Sleep(1500);
                    InjectConsoleCommand(pid, cmd2);
                    Log("[INFO] Verifying GM level was actually set...");
                    string uname = u; // capture for the closure below
                    ThreadPool.QueueUserWorkItem(delegate { VerifyAndRetryGmLevel(pid, uname, cmd2); });
                }
                Log("[INFO] Check the World console for the server's confirmation");
                if (mode == "ahbot")
                    Log("[INFO] Log in with this account, create character 'Alibaba', then use Get char GUID + Set AH bot GUID");
                txtGmUser.Clear();
                txtGmPass.Clear();
            }
            else
            {
                // Fallback: clipboard method
                try { Clipboard.SetText(cmd + (cmd2.Length > 0 ? "\r\n" + cmd2 : "")); }
                catch { Log("[FAIL] Could not inject nor copy to clipboard"); return; }
                Log("[WARN] Console injection failed - command copied to clipboard instead.");
                Log("[INFO] Click World console, then Ctrl+V to paste:");
                Log(cmd);
                if (cmd2.Length > 0) Log(cmd2);
                txtGmUser.Clear();
                txtGmPass.Clear();
            }
        }

        // Right after boot, worldserver can be busy for a variable amount
        // of time (e.g. auto-logging in playerbots), which can delay console
        // command processing enough that "account set gmlevel" misses even
        // with a generous fixed sleep. This polls the database directly to
        // confirm the level actually took, and re-injects the command if it
        // hasn't - adapting to however long the server actually needs
        // instead of guessing a delay.
        void VerifyAndRetryGmLevel(int pid, string username, string gmCmd)
        {
            if (mysqlExe == null || !File.Exists(MyCnfFile))
            {
                SafeInvoke(delegate { Log("[WARN] Can't verify GM level (no DB client) - check manually with .account in-game"); });
                return;
            }

            string query = "SELECT aa.gmlevel FROM acore_auth.account a JOIN acore_auth.account_access aa ON a.id = aa.id WHERE a.username = '" + username.Replace("'", "''") + "';";

            for (int attempt = 1; attempt <= 5; attempt++)
            {
                Thread.Sleep(2000);
                string result = RunCapture(mysqlExe,
                    "--defaults-extra-file=\"" + MyCnfFile + "\" -N -e \"" + query.Replace("\"", "\\\"") + "\"", null);
                string trimmed = result.Trim();

                if (trimmed == "3")
                {
                    string attemptText = attempt.ToString();
                    SafeInvoke(delegate { Log("[OK] GM level confirmed set (verified after " + attemptText + " check(s))"); });
                    return;
                }

                if (attempt < 5)
                {
                    string attemptText = attempt.ToString();
                    SafeInvoke(delegate { Log("[INFO] GM level not set yet (attempt " + attemptText + "/5) - retrying..."); });
                    Process[] procs = Process.GetProcessesByName("worldserver");
                    if (procs.Length > 0)
                    {
                        InjectConsoleCommand(procs[0].Id, gmCmd);
                        foreach (Process p in procs) p.Dispose();
                    }
                }
            }

            SafeInvoke(delegate
            {
                Log("[WARN] GM level still not confirmed after 5 attempts.");
                Log("[INFO] Type this directly in the World console: " + gmCmd);
            });
        }

        void GmCommands(bool gm)
        {
            CreateAccount(gm ? "gm" : "normal");
        }

        void AhBotCommands()
        {
            CreateAccount("ahbot");
        }

        void CharGuid()
        {
            string name = txtChar.Text.Trim();
            if (name.Length == 0) { Log("[WARN] Enter a character name"); return; }
            if (mysqlExe == null) { Log("[FAIL] mysql.exe not found - DB features unavailable"); return; }
            if (!File.Exists(MyCnfFile)) { Log("[FAIL] Credentials file not found - run 04-setup-database.ps1 first"); return; }

            Log("[INFO] Querying database...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string safe = name.Replace("'", "''");
                string query = "SELECT guid,name,race,class,level FROM characters WHERE name='" + safe + "';";
                string result = RunCapture(mysqlExe,
                    "--defaults-extra-file=\"" + MyCnfFile + "\" acore_characters -e \"" + query.Replace("\"", "\\\"") + "\"", null);

                SafeInvoke(delegate
                {
                    if (result.Length == 0 || result.Contains("[FAIL]")) { Log("[FAIL] Character '" + name + "' not found (or DB query failed)"); return; }
                    Log(result);
                    Log("[INFO] Copy the GUID value above and use it in the Set AH bot GUID field");
                    txtChar.Clear();
                });
            });
        }

        void UpdateRealmlist()
        {
            string ip = txtIp.Text.Trim();
            if (ip.Length == 0) { Log("[WARN] Enter an IP address"); return; }
            MySql("acore_auth", "UPDATE realmlist SET address='" + ip + "',localAddress='" + ip + "' WHERE id=1;");
            Log("[OK] Realmlist -> " + ip);
            txtIp.Clear();
        }

        void SetAhGuid()
        {
            string guid = txtAhGuid.Text.Trim();
            if (guid.Length == 0) { Log("[WARN] Enter the Alibaba character's GUID"); return; }
            uint g;
            if (!uint.TryParse(guid, out g)) { Log("[FAIL] GUID must be a number (use Get char GUID tab first)"); return; }

            if (!File.Exists(AhbotConf))
            {
                // Try to find it in the modules dir or create it
                Log("[WARN] mod_ahbot.conf not found at " + AhbotConf);
                // Check if modules dir exists
                if (!Directory.Exists(ModulesDir))
                {
                    Log("[FAIL] Modules directory doesn't exist: " + ModulesDir);
                    Log("[INFO] Is the AzerothCore server folder correctly installed at " + InstallDir + "?");
                    return;
                }
                Log("[INFO] Creating default mod_ahbot.conf...");
                File.WriteAllText(AhbotConf, "# mod_ahbot config\r\n");
            }

            try
            {
                SetConfValue(AhbotConf, "AuctionHouseBot.GUIDs", guid);
                SetConfValue(AhbotConf, "AuctionHouseBot.EnableSeller", "true");
                Log("[OK] AH bot GUID set to " + guid);
                Log("[INFO] Restart World server to apply");
                txtAhGuid.Clear();
            }
            catch (Exception ex)
            {
                Log("[FAIL] Could not write config: " + ex.Message);
            }
        }

        void ServerInfo()
        {
            Log("Realmlist:");
            Log(MySql("acore_auth", "SELECT address,port FROM realmlist;"));
            Log("Online:");
            Log(MySql("acore_characters", "SELECT COUNT(*) AS online FROM characters WHERE online=1;"));
            Log("Accounts:");
            Log(MySql("acore_auth", "SELECT COUNT(*) AS accounts FROM account;"));
        }

        void ReagentBankSql()
        {
            string worldSql = AcoreSrcDir + @"\modules\mod-reagent-bank\data\sql\db-world\base\reagent_bank_NPC.sql";
            string charSql = AcoreSrcDir + @"\modules\mod-reagent-bank\data\sql\db-characters\base\create_table.sql";
            if (mysqlExe == null || !File.Exists(worldSql)) { Log("[FAIL] SQL files or mysql.exe not found - check module path"); return; }
            Log(RunCapture(mysqlExe, "--defaults-extra-file=\"" + MyCnfFile + "\" acore_world", File.ReadAllText(worldSql)));
            if (File.Exists(charSql))
                Log(RunCapture(mysqlExe, "--defaults-extra-file=\"" + MyCnfFile + "\" acore_characters", File.ReadAllText(charSql)));
            Log("[OK] Reagent bank SQL applied!");
        }

        // ============================ BACKUP ==========================
        // acore_playerbots is deliberately NOT in "Backup all": it only holds
        // regenerable bot data. (Restore still understands a playerbots_*.sql
        // prefix, so old backups of it can still be restored.)
        static readonly string[] AllDbs = { "acore_auth", "acore_characters" };

        string FindMySqlDump()
        {
            if (mysqlExe != null)
            {
                string near = Path.Combine(Path.GetDirectoryName(mysqlExe), "mysqldump.exe");
                if (File.Exists(near)) return near;
            }
            return FindOnPath("mysqldump.exe");
        }

        bool DbReady()
        {
            if (mysqlExe == null) { Log("[FAIL] mysql.exe not found"); return false; }
            if (!File.Exists(MyCnfFile)) { Log("[FAIL] Credentials file not found at " + MyCnfFile + " - run 04-setup-database.ps1 first"); return false; }
            return true;
        }

        void BackupAll()
        {
            if (!DbReady()) return;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (string db in AllDbs)
                {
                    string shortName = db.Replace("acore_", "");
                    bool ok = DumpDb(db, Path.Combine(BackupDir, shortName + "_" + stamp + ".sql"));
                    string msg = (ok ? "[OK] " : "[FAIL] ") + shortName;
                    SafeInvoke(delegate { Log(msg); });
                }
                SafeInvoke(delegate { Log("[OK] Done - " + stamp); RefreshBackupList(); });
            });
        }

        void BackupOne(string db, string shortName)
        {
            if (!DbReady()) return;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = DumpDb(db, Path.Combine(BackupDir, shortName + "_" + stamp + ".sql"));
                string msg = (ok ? "[OK] " : "[FAIL] ") + shortName + "_" + stamp + ".sql";
                SafeInvoke(delegate { Log(msg); RefreshBackupList(); });
            });
        }

        // "Backup accounts" needs both auth AND characters together, not
        // auth alone - a character row points at an account ID in auth, so
        // a restore from just one half leaves characters orphaned from
        // their account (or an account with no characters to show for it).
        // Same timestamp on both files so it's obvious at a glance which
        // auth/characters pair belongs together in the list.
        void BackupAccount()
        {
            if (!DbReady()) return;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (KeyValuePair<string, string> kv in new Dictionary<string, string> {
                    { "acore_auth", "auth" }, { "acore_characters", "characters" } })
                {
                    bool ok = DumpDb(kv.Key, Path.Combine(BackupDir, kv.Value + "_" + stamp + ".sql"));
                    string msg = (ok ? "[OK] " : "[FAIL] ") + kv.Value + "_" + stamp + ".sql";
                    SafeInvoke(delegate { Log(msg); });
                }
                SafeInvoke(delegate { Log("[OK] Account backup done - " + stamp); RefreshBackupList(); });
            });
        }

        // Separate from BackupAccount() on purpose - configs are a
        // different kind of thing to restore (hand-copy a file back)
        // than a DB dump (import through Restore selected), and change
        // on a different rhythm (tuning tweaks vs. play sessions), so
        // they get their own button rather than being bundled in.
        //
        // Copies the config files people actually hand-tweak (world/auth
        // server conf, playerbots conf, LLM chatter + AH bot module confs)
        // into a per-backup subfolder, one per click, timestamped the
        // same way as the DB dumps. Skips any file that doesn't exist yet
        // (e.g. playerbots.conf before it's ever been created, or the LLM
        // conf on a build without LLM chatter) rather than failing the
        // whole backup over one missing file.
        void BackupConfigs()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dir = Path.Combine(BackupDir, "configs_" + stamp);
            string[] files = { WorldConf, AuthConf, PlayerbotsConf, LlmConf, AhbotConf };
            bool any = false;
            foreach (string f in files)
            {
                if (!File.Exists(f)) continue;
                string fCaptured = f;
                try
                {
                    if (!any) { Directory.CreateDirectory(dir); any = true; }
                    File.Copy(fCaptured, Path.Combine(dir, Path.GetFileName(fCaptured)), true);
                }
                catch (Exception ex)
                {
                    string msgCaptured = "[WARN] Could not back up " + Path.GetFileName(fCaptured) + ": " + ex.Message;
                    SafeInvoke(delegate { Log(msgCaptured); });
                }
            }
            if (any)
            {
                string dirCaptured = dir;
                SafeInvoke(delegate { Log("[OK] Config files backed up to " + dirCaptured); });
            }
            else
            {
                Log("[WARN] No config files found to back up yet");
            }
        }

        bool DumpDb(string db, string outFile)
        {
            string dump = FindMySqlDump();
            if (dump == null) return false;
            try
            {
                Directory.CreateDirectory(BackupDir);
                ProcessStartInfo psi = new ProcessStartInfo(dump,
                    "--defaults-extra-file=\"" + MyCnfFile + "\" " + db);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    // Drain stderr on a worker so big dumps can't deadlock
                    string err = "";
                    ThreadPool.QueueUserWorkItem(delegate { try { err = p.StandardError.ReadToEnd(); } catch { } });
                    using (FileStream fs = File.Create(outFile))
                        p.StandardOutput.BaseStream.CopyTo(fs);
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        void RefreshBackupList()
        {
            lstBackups.Items.Clear();
            if (!Directory.Exists(BackupDir)) return;
            List<string> names = new List<string>();
            foreach (string fpath in Directory.GetFiles(BackupDir, "*.sql")) names.Add(Path.GetFileName(fpath));
            names.Sort();
            names.Reverse();
            foreach (string n in names) lstBackups.Items.Add(n);
        }

        void RestoreSelected()
        {
            if (!DbReady()) return;
            if (lstBackups.SelectedItem == null) { Log("[WARN] Select a backup first"); return; }
            string fname = lstBackups.SelectedItem.ToString();
            string prefix = fname.Split('_')[0];
            string db;
            if (prefix == "auth") db = "acore_auth";
            else if (prefix == "characters") db = "acore_characters";
            else if (prefix == "playerbots") db = "acore_playerbots";
            else { Log("[FAIL] Unknown DB prefix: " + prefix); return; }
            if (MessageBox.Show("Restore " + fname + " into " + db + "?\nThis overwrites current data.",
                "Restore backup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            string full = Path.Combine(BackupDir, fname);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string res = RunCapture(mysqlExe, "--defaults-extra-file=\"" + MyCnfFile + "\" " + db, File.ReadAllText(full));
                SafeInvoke(delegate
                {
                    if (res.Length > 0) Log(res);
                    Log("[OK] Restored " + fname + " -> " + db);
                });
            });
        }

        void DeleteSelected()
        {
            if (lstBackups.SelectedItem == null) { Log("[WARN] Select a backup first"); return; }
            string fname = lstBackups.SelectedItem.ToString();
            try { File.Delete(Path.Combine(BackupDir, fname)); Log("[OK] Deleted " + fname); }
            catch (Exception ex) { Log("[FAIL] " + ex.Message); }
            RefreshBackupList();
        }

        void DeleteAllBackups()
        {
            if (MessageBox.Show("Delete ALL backups in " + BackupDir + "?", "Delete all",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (Directory.Exists(BackupDir))
                foreach (string fpath in Directory.GetFiles(BackupDir, "*.sql"))
                    try { File.Delete(fpath); } catch { }
            Log("[OK] All backups deleted");
            RefreshBackupList();
        }

        // ============================ SELF-UPDATE =====================
        // Some headers ("Connection" in particular) are RESTRICTED by the
        // underlying HttpWebRequest that WebClient wraps - they can only
        // be set via a dedicated property, and attempting to set them
        // through the generic Headers collection throws an exception
        // before any request even goes out. Wrapping each header
        // individually here means one bad header doesn't silently break
        // the whole set, and we can see exactly which one failed if any do.
        // curl.exe ships built into Windows itself since the 2018 update -
        // no extra install needed, and it's the same tool the Linux side's
        // update_wowmenu() uses successfully against this exact same
        // gitea.com infrastructure. -f makes curl exit non-zero on an HTTP
        // error instead of writing the error page to the output file as
        // if it were the real content; -sS keeps it quiet but still shows
        // real errors; -L follows redirects.
        int RunCurlDownload(string url, string outputPath)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("curl.exe",
                    "-fsSL \"" + url + "\" -o \"" + outputPath + "\"");
                psi.UseShellExecute = false;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0 && err.Trim().Length > 0)
                    {
                        string msg = "[FAIL]   curl: " + err.Trim();
                        SafeInvoke(delegate { Log(msg); });
                    }
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                string msg = "[FAIL]   Could not run curl.exe: " + ex.Message;
                SafeInvoke(delegate { Log(msg); });
                return -1;
            }
        }

        // A running exe can never reliably replace ITSELF on disk while
        // it's still executing - Windows locks a file that's actively
        // running, and no amount of careful File.Copy/File.Move ordering
        // in-process changes that fundamentally (confirmed the hard way:
        // in-place overwrite attempts silently failed every time, and a
        // detached-helper-relaunch script worked but added real
        // complexity - a timing window, a .bat file, Application.Exit()).
        // The genuinely simplest fix: stop trying to overwrite the
        // running exe at all. A file that never existed before was never
        // locked by anyone - so just build the new version into its own
        // incrementing filename (WowMenuV2.exe, WowMenuV3.exe, ...)
        // sitting right next to the current one. No relaunch trickery
        // needed, no risk of the swap failing partway through - the
        // trade-off is the user has to know to run the new filename
        // (spelled out clearly in the log) and clean up old ones by hand.
        void UpdateFromGitea()
        {
            Log("[INFO] Checking for a newer WOTLK-Menu...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // Persistent, easy-to-find folder instead of a random
                    // %TEMP%\wotlk-menu-update-<huge tick count> path -
                    // cleared and recreated fresh on every attempt.
                    string wowMenuDir = Path.Combine(InstallDir, "WowMenu");
                    try { if (Directory.Exists(wowMenuDir)) Directory.Delete(wowMenuDir, true); } catch { }
                    Directory.CreateDirectory(wowMenuDir);

                    // WebClient kept getting blocked by AWS WAF sitting in
                    // front of gitea.com (confirmed via the verbose 403
                    // diagnostics: "Server: awselb/2.0", non-Gitea-branded
                    // body). Meanwhile the Linux side's wowmenu.sh update
                    // function uses plain curl against this exact same
                    // gitea.com infrastructure and it just works - curl
                    // ships built into Windows itself since the 2018
                    // update, so shelling out to it instead of continuing
                    // to fight WebClient's header/TLS quirks is reusing a
                    // proven approach, not inventing a new one.
                    string csFile = Path.Combine(wowMenuDir, "08-wotlk-menu.cs");
                    string psFile = Path.Combine(wowMenuDir, "07-build-wotlk-menu.ps1");

                    int code1 = RunCurlDownload(RawBase + "08-wotlk-menu.cs", csFile);
                    int code2 = RunCurlDownload(RawBase + "07-build-wotlk-menu.ps1", psFile);

                    if (code1 != 0 || code2 != 0 || !File.Exists(csFile) || !File.Exists(psFile))
                    {
                        string msg = "[FAIL] curl download failed (exit codes " + code1 + "/" + code2 + ") - keeping current version";
                        SafeInvoke(delegate { Log(msg); });
                        return;
                    }
                    SafeInvoke(delegate { Log("[OK] Downloaded - building new version..."); });
                    string buildOut = RunCapture("powershell",
                        "-NoProfile -ExecutionPolicy Bypass -File \"" + psFile + "\"", wowMenuDir);
                    string newExe = Path.Combine(wowMenuDir, "WOTLK-Menu.exe");
                    if (!File.Exists(newExe))
                    {
                        SafeInvoke(delegate { Log("[FAIL] Build of new version failed - keeping current"); Log(buildOut); });
                        return;
                    }

                    string curExe = Application.ExecutablePath;
                    string appDir = Path.GetDirectoryName(curExe);

                    // Refresh the source/build script in ScriptsDir too (not
                    // appDir - the companion scripts now live in their own
                    // dedicated folder, separate from wherever the exe
                    // itself happens to be), so a future "Rebuild only" /
                    // manual build uses the current source rather than
                    // whatever was there when this copy was first installed.
                    try { Directory.CreateDirectory(ScriptsDir); } catch { }
                    try { File.Copy(csFile, Path.Combine(ScriptsDir, "08-wotlk-menu.cs"), true); } catch { }
                    try { File.Copy(psFile, Path.Combine(ScriptsDir, "07-build-wotlk-menu.ps1"), true); } catch { }

                    // Scan for the highest existing WowMenuV<N>.exe already
                    // sitting in this folder and pick the next number up -
                    // covers both "no versioned exe exists yet" (start at
                    // V1) and "V3 already exists" (this build becomes V4).
                    int nextVersion = 1;
                    foreach (string f in Directory.GetFiles(appDir, "WowMenuV*.exe"))
                    {
                        Match m = Regex.Match(Path.GetFileName(f), @"^WowMenuV(\d+)\.exe$", RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            int n = int.Parse(m.Groups[1].Value);
                            if (n >= nextVersion) nextVersion = n + 1;
                        }
                    }
                    string versionedExe = Path.Combine(appDir, "WowMenuV" + nextVersion + ".exe");
                    File.Copy(newExe, versionedExe, true);
                    string versionedNameCaptured = Path.GetFileName(versionedExe);

                    // The build workspace's own copy of the exe has done
                    // its job the moment it's safely copied out to
                    // versionedExe above - leaving it sitting in WowMenu\
                    // too is just a redundant, confusing duplicate (this
                    // is what cap spotted: an extra "wowmenu.exe" left
                    // behind in the build folder even after the real
                    // versioned copy existed). The whole folder gets
                    // wiped fresh at the START of the next update anyway,
                    // but there's no reason to leave it cluttered in the
                    // meantime.
                    try { Directory.Delete(wowMenuDir, true); } catch { }

                    // Now that each update lands as a brand-new, never-
                    // locked filename (WowMenuV<N>.exe), there's no
                    // Windows file-locking fight left to work around at
                    // all - unlike trying to overwrite curExe in place,
                    // launching a SEPARATE file needs no waiting, no
                    // helper .bat, no timing window. Just start the new
                    // one directly and close this process right after -
                    // the user sees the window close and a new one open,
                    // genuinely one click, with the old exe still sitting
                    // there untouched as a natural rollback if needed.
                    SafeInvoke(delegate { Log("[OK] New version built: " + versionedNameCaptured + " - launching it now..."); });
                    try
                    {
                        Process.Start(versionedExe);
                        SafeInvoke(delegate { Application.Exit(); });
                    }
                    catch (Exception ex)
                    {
                        string msgCaptured = ex.Message;
                        SafeInvoke(delegate
                        {
                            Log("[WARN] Could not auto-launch the new version: " + msgCaptured);
                            Log("[INFO] Close this window and run " + versionedNameCaptured + " manually instead.");
                        });
                    }
                }
                catch (Exception ex)
                {
                    SafeInvoke(delegate { Log("[FAIL] Update failed - keeping current version: " + ex.Message); });
                }
            });
        }

        // ============================ HELPERS =========================
        void Log(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            txtOut.AppendText(msg.TrimEnd() + Environment.NewLine);
        }

        void OpenInNotepad(string file)
        {
            if (!File.Exists(file)) { Log("[FAIL] Not found: " + file); return; }
            Process.Start("notepad", "\"" + file + "\"");
        }

        string MySql(string db, string query)
        {
            if (mysqlExe == null) return "[FAIL] mysql.exe not found in PATH";
            if (!File.Exists(MyCnfFile)) return "[FAIL] Credentials file not found at " + MyCnfFile + " - run 04-setup-database.ps1 first";
            return RunCapture(mysqlExe,
                "--defaults-extra-file=\"" + MyCnfFile + "\" -t " + db + " -e \"" + query.Replace("\"", "\\\"") + "\"", null);
        }

        static string RunCapture(string exe, string args, string stdin)
        {
            return RunCapture(exe, args, stdin, null);
        }

        // workingDir overload - null means "inherit current directory",
        // matching the previous 3-arg behavior exactly for every existing
        // call site. Needed for git operations (clone/pull/checkout must
        // run with the module or core folder as the working directory).
        static string RunCapture(string exe, string args, string stdin, string workingDir)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                if (workingDir != null) psi.WorkingDirectory = workingDir;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                if (stdin != null) psi.RedirectStandardInput = true;
                using (Process p = Process.Start(psi))
                {
                    if (stdin != null)
                    {
                        p.StandardInput.Write(stdin);
                        p.StandardInput.Close();
                    }
                    string outp = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (err.Trim().Length > 0)
                        outp += (outp.Length > 0 ? Environment.NewLine : "") + err.Trim();
                    return outp.TrimEnd();
                }
            }
            catch (Exception ex) { return "[FAIL] " + exe + ": " + ex.Message; }
        }

        // Self-healing check run on every menu launch. If stage 03's build
        // ever got skipped mid-way (e.g. the orchestrator's resume logic
        // treating an already-compiled worldserver.exe as "stage done" and
        // never reaching the trailing DLL-copy step), worldserver.exe would
        // fail to start with a missing-DLL error. This re-copies anything
        // missing from the same known MySQL/OpenSSL locations stage 03 uses,
        // so simply opening the menu repairs it without a manual copy.
        void EnsureRuntimeDlls()
        {
            string mysqlClient = FindMySql();
            string mysqlBaseDir = null;
            if (mysqlClient != null)
            {
                // mysqlClient is .../MySQL Server X.Y/bin/mysql.exe - walk up two levels.
                string binDir = Path.GetDirectoryName(mysqlClient);
                if (binDir != null) mysqlBaseDir = Path.GetDirectoryName(binDir);
            }
            string opensslRoot = @"C:\Program Files\OpenSSL-Win64";

            List<string> sources = new List<string>();
            if (mysqlBaseDir != null)
            {
                sources.Add(Path.Combine(mysqlBaseDir, @"lib\libmysql.dll"));
                sources.Add(Path.Combine(mysqlBaseDir, @"bin\libcrypto-3-x64.dll"));
                sources.Add(Path.Combine(mysqlBaseDir, @"bin\libssl-3-x64.dll"));
            }
            sources.Add(Path.Combine(opensslRoot, @"bin\libcrypto-4-x64.dll"));
            sources.Add(Path.Combine(opensslRoot, @"bin\libssl-4-x64.dll"));
            sources.Add(Path.Combine(opensslRoot, @"bin\legacy.dll"));

            int copied = 0;
            int stillMissing = 0;
            foreach (string src in sources)
            {
                string fileName = Path.GetFileName(src);
                string dest = Path.Combine(InstallDir, fileName);
                if (File.Exists(dest)) continue;

                if (File.Exists(src))
                {
                    try { File.Copy(src, dest, true); copied++; }
                    catch { stillMissing++; }
                }
                else
                {
                    stillMissing++;
                }
            }

            if (copied > 0) Log("[OK] Repaired " + copied + " missing runtime DLL(s)");
            if (stillMissing > 0) Log("[WARN] " + stillMissing + " runtime DLL(s) could not be located - worldserver may fail to start");
        }

        // Self-healing cleanup, run alongside EnsureRuntimeDlls() on every
        // menu launch. IMPORTANT: on Windows, InstallDir and AcoreSrcDir are
        // the SAME folder (C:\Azerothcore) - unlike Linux, which splits the
        // git-cloned source tree (~/azerothcore) from the install
        // destination (/opt/azerothcore). That means CMakeLists.txt,
        // PreLoad.cmake, src\, deps\, and conf\ are NOT leftover clutter
        // here - they're the actual source tree cmake needs to reconfigure
        // every time a module gets added or the core gets rebuilt. This
        // list used to include those as "cleanup," which silently broke
        // every subsequent build with "CMakeLists.txt not found" - fixed by
        // trimming it down to genuinely one-time dev/CI scaffolding that
        // the actual C++ build never touches.
        void EnsureCleanInstallDir()
        {
            string[] cleanupDirs = new string[] {
                ".devcontainer", ".github", ".vscode",
                "apps", "doc", "env", "tools", "var"
            };
            string[] cleanupFiles = new string[] {
                ".coderabbit.yml", ".dockerignore", ".editorconfig", ".git_commit_template",
                ".gitattributes", ".gitignore", ".suppress.cppcheck",
                "AUTHORS", "CLAUDE.md", "docker-compose.yml",
                "flake.lock", "flake.nix", "install", "LICENSE",
                "pull_request_template.md", "acore", "acore.cmd"
            };

            int removed = 0;
            foreach (string d in cleanupDirs)
            {
                string p = Path.Combine(InstallDir, d);
                if (Directory.Exists(p))
                {
                    try { Directory.Delete(p, true); removed++; }
                    catch { }
                }
            }
            foreach (string f in cleanupFiles)
            {
                string p = Path.Combine(InstallDir, f);
                if (File.Exists(p))
                {
                    try { File.Delete(p); removed++; }
                    catch { }
                }
            }

            if (removed > 0) Log("[OK] Cleaned up " + removed + " leftover source/build item(s)");
        }

        // One-time self-heal: creates ScriptsDir the first time this runs,
        // then migrates any of the menu's own companion files that are
        // still sitting loose in the old flat location (InstallDir itself)
        // over to it. Only ever moves a file if the ScriptsDir copy doesn't
        // already exist, so this is safe to run on every single launch -
        // after the first migration it finds nothing left to do and stays
        // silent. Does NOT touch WOTLK-Menu.exe or any WowMenuV*.exe - the
        // exe's own location is managed separately by UpdateFromGitea()'s
        // versioned-copy logic and is intentionally left alone here.
        void EnsureScriptsDirMigrated()
        {
            try { Directory.CreateDirectory(ScriptsDir); }
            catch (Exception ex) { Log("[WARN] Could not create " + ScriptsDir + ": " + ex.Message); return; }

            string[] companionFiles = new string[] {
                "03-build.ps1", "07-build-wotlk-menu.ps1", "08-wotlk-menu.cs",
                "wowmenu.ico", "02-source-patches.ps1", "02-source-pathces.ps1", "pinned-commits.txt"
            };

            int moved = 0;
            foreach (string name in companionFiles)
            {
                string oldPath = Path.Combine(InstallDir, name);
                string newPath = Path.Combine(ScriptsDir, name);
                if (File.Exists(oldPath) && !File.Exists(newPath))
                {
                    try { File.Move(oldPath, newPath); moved++; }
                    catch (Exception ex) { Log("[WARN] Could not migrate " + name + " to " + ScriptsDir + ": " + ex.Message); }
                }
            }
            if (moved > 0) Log("[OK] Migrated " + moved + " companion file(s) into " + ScriptsDir);
        }

        static string FindMySql()
        {
            string p = FindOnPath("mysql.exe");
            if (p != null) return p;
            string baseDir = @"C:\Program Files\MySQL";
            if (Directory.Exists(baseDir))
                foreach (string d in Directory.GetDirectories(baseDir, "MySQL Server *"))
                {
                    string c = Path.Combine(d, @"bin\mysql.exe");
                    if (File.Exists(c)) return c;
                }
            return null;
        }

        static string FindOnPath(string exeName)
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (pathEnv == null) return null;
            foreach (string dir in pathEnv.Split(';'))
            {
                if (dir.Trim().Length == 0) continue;
                try
                {
                    string c = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(c)) return c;
                }
                catch { }
            }
            return null;
        }

        // git.exe is usually on PATH once Git for Windows is installed, but
        // the installer doesn't always add it for every shell/session type
        // - fall back to the two standard install locations before giving
        // up, same pattern as FindMySql() above.
        static string FindGit()
        {
            string p = FindOnPath("git.exe");
            if (p != null) return p;
            string[] candidates = new string[] {
                @"C:\Program Files\Git\bin\git.exe",
                @"C:\Program Files\Git\cmd\git.exe"
            };
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        // Anchored pattern - avoids clobbering longer keys like
        // DisabledWithoutRealPlayerLoginDelay / MinRandomBotsPriceChangeInterval
        static string GetConfValue(string file, string key)
        {
            if (!File.Exists(file)) return null;
            Regex rx = new Regex("^" + Regex.Escape(key) + @"\s*=\s*(.+)$");
            foreach (string line in File.ReadAllLines(file))
            {
                Match m = rx.Match(line);
                if (m.Success) return m.Groups[1].Value.Trim();
            }
            return null;
        }

        static void SetConfValue(string file, string key, string value)
        {
            Regex rx = new Regex("^" + Regex.Escape(key) + @"\s*=");
            List<string> outLines = new List<string>();
            bool found = false;
            if (File.Exists(file))
                foreach (string line in File.ReadAllLines(file))
                {
                    if (!found && rx.IsMatch(line)) { outLines.Add(key + " = " + value); found = true; }
                    else outLines.Add(line);
                }
            if (!found) outLines.Add(key + " = " + value);
            File.WriteAllLines(file, outLines.ToArray());
        }

        // ============================ MODULES ==========================
        // Same "pull first, build once" philosophy as the Linux menu's
        // Modules tab: Clone/Update buttons here only ever touch git, never
        // trigger a compile. Building everything together in one pass (via
        // the Build card below) avoids compiling some files against an old
        // module's headers and others against a freshly-pulled one in the
        // same run - exactly the kind of half-updated state that produces
        // confusing "member not found" errors that look like a real
        // incompatibility but are actually just bad sequencing.
        static readonly string ModulesSrcDir = AcoreSrcDir + @"\modules";

        // The exact commits install-wotlk.sh / 01-Clone-Azertoh-windows.ps1
        // originally pinned core and every module to - lets "Rollback" put
        // things back to a known-working combination if an update ever
        // turns out to break something. Mirrors the Linux menu's pins
        // exactly since both platforms clone the same upstream forks.
        const string CorePinnedCommit = "dfae3da";
        static readonly Dictionary<string, string> PinnedCommits = new Dictionary<string, string>()
        {
            { "mod-playerbots", "085e127e" },
            { "mod-llm-chatter", "029553c" },
            { "mod-reagent-bank", "62c2653" },
            { "mod-junk-to-gold", "2134690" },
            { "mod-no-hearthstone-cooldown", "832ef5e" },
            { "mod-transmog", "33ac64b" },
            { "mod-assistant", "c71ec20" },
            { "mod-npc-all-mounts", "c6899d4" },
            { "mod-ah-bot-plus", "9fbcbe6" },
        };

        // "Pin current state" (below) writes HERE instead of touching the
        // consts above - a plain "name=commit" text file next to the exe,
        // one line per module plus a "__core__" line for AzerothCore
        // itself. Lets a confirmed-working server+module combo become the
        // new Rollback target any time, no source edit/rebuild needed.
        // LoadPins() layers this file on top of the original install-time
        // pins above, so an install that has never pinned anything yet
        // keeps rolling back to the original CorePinnedCommit/
        // PinnedCommits exactly as before.
        static string PinsFile { get { return Path.Combine(ScriptsDir, "pinned-commits.txt"); } }

        // Custom backup folder - a plain text file next to the pin state,
        // same "override on top of a default" pattern as PinsFile above.
        // Empty/missing file (or a file that fails to point at a usable
        // folder) falls back to DefaultBackupDir with no error to the user.
        static string BackupDirFile { get { return Path.Combine(ScriptsDir, "backup-dir.txt"); } }

        static string _backupDir;
        static string BackupDir
        {
            get
            {
                if (_backupDir == null) _backupDir = LoadBackupDir();
                return _backupDir;
            }
        }

        static string LoadBackupDir()
        {
            try
            {
                if (File.Exists(BackupDirFile))
                {
                    string p = File.ReadAllText(BackupDirFile).Trim();
                    if (p.Length > 0) return p;
                }
            }
            catch { }
            return DefaultBackupDir;
        }

        static void SetBackupDir(string path)
        {
            _backupDir = path;
            try
            {
                Directory.CreateDirectory(ScriptsDir);
                File.WriteAllText(BackupDirFile, path);
            }
            catch { }
        }

        static Dictionary<string, string> LoadPins()
        {
            Dictionary<string, string> pins = new Dictionary<string, string>(PinnedCommits);
            pins["__core__"] = CorePinnedCommit;
            if (File.Exists(PinsFile))
            {
                foreach (string line in File.ReadAllLines(PinsFile))
                {
                    string l = line.Trim();
                    if (l.Length == 0 || l.StartsWith("#")) continue;
                    int eq = l.IndexOf('=');
                    if (eq <= 0) continue;
                    pins[l.Substring(0, eq).Trim()] = l.Substring(eq + 1).Trim();
                }
            }
            return pins;
        }

        Label lblCoreCommit;
        Label lblPinnedCommit;
        TextBox txtModuleUrl, txtModuleCommit;
        ListBox lstModules;

        Panel BuildModulesTab()
        {
            FlowLayoutPanel f;
            Panel p = NewTab("Modules & Data", out f);

            // Two columns: left holds Core/Add/Installed, right holds Build,
            // so a long build isn't buried at the bottom of one long scroll.
            FlowLayoutPanel columns = new FlowLayoutPanel();
            columns.FlowDirection = FlowDirection.LeftToRight;
            columns.WrapContents = false;
            columns.AutoSize = true;
            columns.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            columns.Margin = new Padding(0);
            columns.BackColor = BgPanel;
            f.Controls.Add(columns);

            FlowLayoutPanel left = new FlowLayoutPanel();
            left.FlowDirection = FlowDirection.TopDown;
            left.WrapContents = false;
            left.AutoSize = true;
            left.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            left.Margin = new Padding(0, 0, 16, 0);
            left.BackColor = BgPanel;
            columns.Controls.Add(left);

            FlowLayoutPanel right = new FlowLayoutPanel();
            right.FlowDirection = FlowDirection.TopDown;
            right.WrapContents = false;
            right.AutoSize = true;
            right.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            right.Margin = new Padding(0);
            right.BackColor = BgPanel;
            columns.Controls.Add(right);

            lblCoreCommit = HeadLabel("Current commit: ...", FgMain);
            lblPinnedCommit = HeadLabel("Pinned commit: " + LoadPins()["__core__"] + " (Playerbot branch)", AccGray);

            left.Controls.Add(SectionCard("AZEROTHCORE (CORE)",
                lblCoreCommit,
                lblPinnedCommit,
                HeadLabel("HIGHEST RISK action here - can break every module at once. Back up your DB first.", AccRed),
                // Rollback Core to pinned was removed - it only ever
                // checked out core alone, never the modules, so a click
                // here without also rolling back every module to a
                // matching commit silently produced a mismatched,
                // unbuildable pair (core missing/renaming a field a
                // module's source still expects, e.g. CreatureData::id).
                // Rolling core and modules back together isn't safe to
                // automate blindly either (a module could be intentionally
                // ahead of its pin), so for now core-only rollback is gone
                // rather than left as a footgun - use Pin current state to
                // capture a real known-good combo instead, and roll back
                // module-by-module only in tandem with a matching core.
                Row(
                    Btn("Show current commit", AccCyan, delegate { ShowCoreCommit(); }),
                    Btn("Update Core (pull only)", AccRed, delegate { UpdateCore(); })),
                HeadLabel("Once core + all modules are confirmed working together, pin that exact state as the new rollback target below.", AccGray),
                Row(
                    Btn("Pin current state as new baseline (core + all modules)", AccGreen, delegate { PinCurrentState(); }),
                    Btn("Rollback to pinned (core + all modules)", AccRed, delegate { RollbackToPinnedAll(); }))));

            txtModuleUrl = Txt(420, "git clone URL");
            txtModuleCommit = Txt(200, "commit/tag (optional)");

            left.Controls.Add(SectionCard("ADD NEW MODULE / CLIENT DATA",
                Row(txtModuleUrl),
                Row(txtModuleCommit,
                    Btn("Clone only", AccGreen, delegate { CloneModule(); })),
                HeadLabel("Clones only - does not build. Clone/update everything you want first, then Build once below.", AccGray),
                HeadLabel("A core update can also require a newer wowgaming client-data release", AccGray),
                HeadLabel("(mmaps/vmaps format change) - a common symptom is worldserver refusing", AccGray),
                HeadLabel("to boot right after a core update.", AccGray),
                Row(Btn("Download latest client data", AccRed, delegate { DownloadLatestClientData(); }))));

            lstModules = new ListBox();
            lstModules.Width = 420;
            lstModules.Height = 150;
            lstModules.BackColor = BgCtl;
            lstModules.ForeColor = FgMain;
            lstModules.BorderStyle = BorderStyle.FixedSingle;
            lstModules.Margin = new Padding(0, 8, 0, 8);

            left.Controls.Add(SectionCard("INSTALLED MODULES",
                Row(
                    Btn("Refresh list", AccCyan, delegate { RefreshModuleList(); }),
                    Btn("Update selected (pull only)", AccCyan, delegate { UpdateSelectedModule(); }),
                    Btn("Rollback to pinned", AccRed, delegate { RollbackSelectedModule(); }),
                    Btn("Remove selected", AccRed, delegate { RemoveSelectedModule(); })),
                lstModules));

            right.Controls.Add(SectionCard("BUILD",
                HeadLabel("Compiles core + all modules together via 03-build.ps1. Can take a long time.", AccGray),
                HeadLabel("Clean Full Build wipes the build folder first - use it if a module needs a", AccGray),
                HeadLabel("fresh cmake reconfigure (added/removed a module, weird build errors).", AccGray),
                Row(
                    Btn("Rebuild only", AccRed, delegate { RunBuildScript(false); }),
                    Btn("Clean Full Build", AccRed, delegate { RunBuildScript(true); }))));

            RefreshModuleList();
            ShowCoreCommit();
            return p;
        }

        static string DeriveModuleName(string url)
        {
            string name = url.TrimEnd('/');
            int lastSlash = name.LastIndexOf('/');
            if (lastSlash >= 0) name = name.Substring(lastSlash + 1);
            if (name.EndsWith(".git")) name = name.Substring(0, name.Length - 4);
            return name;
        }

        // Streams a command's output into the log line-by-line as it
        // happens, same pattern as RunOllamaPull() - so a multi-minute git
        // clone or checkout never looks hung with no feedback. Returns the
        // process exit code; callers decide what "success" means for them.
        int RunStreamed(string exe, string args, string workingDir)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                if (workingDir != null) psi.WorkingDirectory = workingDir;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            string lineE;
                            while ((lineE = p.StandardError.ReadLine()) != null)
                            {
                                string captured = lineE;
                                SafeInvoke(delegate { Log("  " + captured); });
                            }
                        }
                        catch { }
                    });
                    string lineO;
                    while ((lineO = p.StandardOutput.ReadLine()) != null)
                    {
                        string captured = lineO;
                        SafeInvoke(delegate { Log("  " + captured); });
                    }
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                string msg = "[FAIL] " + ex.Message;
                SafeInvoke(delegate { Log(msg); });
                return -1;
            }
        }

        void RefreshModuleList()
        {
            lstModules.Items.Clear();
            if (!Directory.Exists(ModulesSrcDir)) return;
            List<string> names = new List<string>();
            foreach (string dir in Directory.GetDirectories(ModulesSrcDir))
                names.Add(Path.GetFileName(dir));
            names.Sort();
            foreach (string n in names) lstModules.Items.Add(n);
        }

        // Clones a new module into ModulesSrcDir and (optionally) checks it
        // out to a specific commit/tag - nothing more. CMake only scans the
        // modules folder at configure time, so this deliberately does NOT
        // trigger a build; the Build card handles that once, for everything
        // cloned/updated so far.
        void CloneModule()
        {
            if (moduleOpBusy) { Log("[WARN] Another module/core operation is already running - wait for it to finish"); return; }
            string url = txtModuleUrl.Text.Trim();
            string commit = txtModuleCommit.Text.Trim();
            if (url.Length == 0) { Log("[WARN] Enter a git URL first"); return; }

            string git = FindGit();
            if (git == null) { Log("[FAIL] git.exe not found - install Git for Windows first"); return; }

            string name = DeriveModuleName(url);
            string dest = Path.Combine(ModulesSrcDir, name);
            if (Directory.Exists(dest)) { Log("[WARN] " + name + " already exists in the modules folder"); return; }
            if (!Directory.Exists(ModulesSrcDir)) { Log("[FAIL] Modules folder not found: " + ModulesSrcDir); return; }

            moduleOpBusy = true;
            Log("[INFO] Cloning " + name + " (not building yet)...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int code = RunStreamed(git, "clone " + url + " \"" + dest + "\"", AcoreSrcDir);
                    if (code != 0)
                    {
                        SafeInvoke(delegate { Log("[FAIL] Clone failed (exit " + code + ")"); });
                        return;
                    }
                    if (commit.Length > 0)
                    {
                        int code2 = RunStreamed(git, "checkout " + commit, dest);
                        if (code2 != 0)
                            SafeInvoke(delegate { Log("[WARN] Checkout of " + commit + " failed - module left on default branch"); });
                    }
                    SafeInvoke(delegate
                    {
                        Log("[OK] " + name + " cloned (not built yet).");
                        Log("[INFO] Clone/update any other modules, then use Build once to compile everything together.");
                        RefreshModuleList();
                        txtModuleUrl.Clear();
                        txtModuleCommit.Clear();
                    });
                }
                finally { moduleOpBusy = false; }
            });
        }

        // Moves a module off whatever pinned/detached commit it's on and
        // pulls latest - same detached-HEAD reasoning as the Linux menu's
        // Update selected: a module checked out to a specific commit has
        // no branch to merge "git pull" into until it's put back on one.
        // Deliberately stops after the pull - no cmake, no build.
        void UpdateSelectedModule()
        {
            if (moduleOpBusy) { Log("[WARN] Another module/core operation is already running - wait for it to finish"); return; }
            if (lstModules.SelectedItem == null) { Log("[WARN] Select a module first"); return; }
            string name = lstModules.SelectedItem.ToString();
            string git = FindGit();
            if (git == null) { Log("[FAIL] git.exe not found - install Git for Windows first"); return; }
            string dir = Path.Combine(ModulesSrcDir, name);

            moduleOpBusy = true;
            Log("[INFO] Pulling latest " + name + " (no rebuild yet)...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    RunStreamed(git, "fetch origin", dir);
                    string branch = RunCapture(git, "symbolic-ref refs/remotes/origin/HEAD", null, dir).Trim();
                    if (branch.StartsWith("refs/remotes/origin/")) branch = branch.Substring("refs/remotes/origin/".Length);
                    if (branch.Length == 0) branch = "master";
                    RunStreamed(git, "checkout " + branch, dir);
                    RunStreamed(git, "stash", dir);
                    int code = RunStreamed(git, "pull", dir);
                    SafeInvoke(delegate
                    {
                        if (code == 0)
                        {
                            Log("[OK] " + name + " updated (not rebuilt yet).");
                            Log("[INFO] Update any other modules, then use Build once to compile everything together.");
                        }
                        else Log("[FAIL] Pull failed for " + name + " (exit " + code + ")");
                    });
                }
                finally { moduleOpBusy = false; }
            });
        }

        void RollbackSelectedModule()
        {
            if (moduleOpBusy) { Log("[WARN] Another module/core operation is already running - wait for it to finish"); return; }
            if (lstModules.SelectedItem == null) { Log("[WARN] Select a module first"); return; }
            string name = lstModules.SelectedItem.ToString();
            string commit;
            if (!LoadPins().TryGetValue(name, out commit))
            {
                Log("[WARN] No known pinned commit for '" + name + "' - probably added later, nothing to roll back to.");
                return;
            }
            string git = FindGit();
            if (git == null) { Log("[FAIL] git.exe not found - install Git for Windows first"); return; }
            string dir = Path.Combine(ModulesSrcDir, name);

            moduleOpBusy = true;
            Log("[INFO] Rolling back " + name + " to " + commit + " (not rebuilt yet)...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    RunStreamed(git, "fetch origin", dir);
                    int code = RunStreamed(git, "checkout " + commit, dir);
                    SafeInvoke(delegate
                    {
                        if (code == 0) Log("[OK] " + name + " rolled back to " + commit + " (not rebuilt yet).");
                        else Log("[FAIL] Rollback failed (exit " + code + ")");
                    });
                }
                finally { moduleOpBusy = false; }
            });
        }

        // Mirrors "Clone only" in reverse - deletes the module's source
        // folder and stops there, deliberately NOT rebuilding
        // automatically. CMake only re-scans the modules folder at
        // configure time, so the removal isn't actually reflected in the
        // build until the next Build now anyway - same reasoning as why
        // cloning a module doesn't build immediately either. Leftover
        // config files under configs\modules\ are left untouched
        // (harmless orphans, and deleting them risks losing tuned
        // settings if the module gets re-added later). Confirms first
        // since Directory.Delete(recursive) cannot be undone, matching
        // the same confirmation pattern already used for deleting models
        // and backups elsewhere in this file.
        // Directory.Delete(path, true) refuses to touch read-only files -
        // and git on Windows deliberately marks its packed object files
        // (.git\objects\pack\*.idx, *.pack) read-only to protect them from
        // accidental modification. A plain recursive delete on a git repo
        // folder then fails with "Access to the path ... is denied" the
        // moment it hits one of those files. Clearing the ReadOnly
        // attribute on every file first (recursively) is the standard
        // .NET workaround for this exact git-on-Windows gotcha.
        static void ForceDeleteDirectory(string path)
        {
            foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                FileAttributes attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(path, true);
        }

        void RemoveSelectedModule()
        {
            if (moduleOpBusy) { Log("[WARN] Another module/core operation is already running - wait for it to finish"); return; }
            if (lstModules.SelectedItem == null) { Log("[WARN] Select a module first"); return; }
            string name = lstModules.SelectedItem.ToString();
            string dir = Path.Combine(ModulesSrcDir, name);

            if (name == "mod-playerbots")
                Log("[WARN] mod-playerbots is a dependency for several other modules (mod-dungeon-clear, " +
                "mod-llm-chatter, etc) - removing it will likely break their builds too.");

            if (MessageBox.Show("Remove '" + name + "' from disk?\nThis deletes its entire source folder and cannot be undone.",
                "Remove module", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            if (!Directory.Exists(dir)) { Log("[WARN] " + name + " folder not found - already removed?"); RefreshModuleList(); return; }

            moduleOpBusy = true;
            Log("[INFO] Removing " + name + " from disk (not rebuilt yet)...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    try { ForceDeleteDirectory(dir); }
                    catch (Exception ex)
                    {
                        string msgCaptured = ex.Message;
                        SafeInvoke(delegate { Log("[FAIL] Could not remove " + name + ": " + msgCaptured); });
                        return;
                    }
                    SafeInvoke(delegate
                    {
                        Log("[OK] " + name + " removed. Use Build now for cmake to notice it's gone.");
                        Log("[INFO] Its configs\\modules\\*.conf files (if any) were left untouched - safe to delete manually if you want.");
                        RefreshModuleList();
                    });
                }
                finally { moduleOpBusy = false; }
            });
        }

        void ShowCoreCommit()
        {
            string git = FindGit();
            if (git == null) { lblCoreCommit.Text = "git.exe not found"; return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                string commit = RunCapture(git, "rev-parse --short HEAD", null, AcoreSrcDir).Trim();
                string pinnedCommit = LoadPins()["__core__"];
                SafeInvoke(delegate
                {
                    lblCoreCommit.Text = commit.Length > 0 ? "Current commit: " + commit : "unknown";
                    lblCoreCommit.ForeColor = commit == pinnedCommit ? AccGreen : AccGray;
                    lblPinnedCommit.Text = "Pinned commit: " + pinnedCommit + " (Playerbot branch)";
                });
            });
        }

        // Snapshots the commit core AND every currently-installed module
        // is sitting on right now, and writes them to PinsFile as the new
        // rollback baseline (see LoadPins() above). Meant to be run once a
        // server + module combination has been confirmed working, so
        // "Rollback Core to pinned" / "Rollback to pinned" have somewhere
        // safe to fall back to if a later update breaks something -
        // captures core commit is skipped (and nothing is written) if it
        // can't be read, so a bad pin never silently overwrites a good one.
        // A module whose commit can't be read is skipped individually and
        // simply keeps whatever pin (original or previously-pinned) it had.
        void PinCurrentState()
        {
            if (moduleOpBusy) { Log("[WARN] Another module/core operation is already running - wait for it to finish"); return; }
            string git = FindGit();
            if (git == null) { Log("[FAIL] git.exe not found - install Git for Windows first"); return; }

            if (MessageBox.Show(
                "Pin the CURRENT commit of core and every installed module as the new rollback baseline?\n\n" +
                "Only do this once you've confirmed the server is actually working - this overwrites any previous pin.",
                "Pin current state", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            moduleOpBusy = true;
            Log("[INFO] Pinning current core + module commits as the new rollback baseline...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string coreCommit = RunCapture(git, "rev-parse --short HEAD", null, AcoreSrcDir).Trim();
                    if (coreCommit.Length == 0)
                    {
                        SafeInvoke(delegate { Log("[FAIL] Could not read core's current commit - aborting pin (nothing written)."); });
                        return;
                    }

                    List<string> lines = new List<string>();
                    lines.Add("# Written by Pin current state on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " - do not edit by hand unless you know what you're doing.");
                    lines.Add("__core__=" + coreCommit);

                    List<string> pinnedSummary = new List<string>();
                    if (Directory.Exists(ModulesSrcDir))
                    {
                        foreach (string dir in Directory.GetDirectories(ModulesSrcDir))
                        {
                            string name = Path.GetFileName(dir);
                            string commit = RunCapture(git, "rev-parse --short HEAD", null, dir).Trim();
                            if (commit.Length == 0)
                            {
                                string nameCaptured = name;
                                SafeInvoke(delegate { Log("[WARN] Could not read current commit for " + nameCaptured + " - leaving its previous pin (if any) untouched."); });
                                continue;
                            }
                            lines.Add(name + "=" + commit);
                            pinnedSummary.Add(name + "@" + commit);
                        }
                    }

                    File.WriteAllLines(PinsFile, lines.ToArray());
                    SafeInvoke(delegate
                    {
                        Log("[OK] Pinned core@" + coreCommit + (pinnedSummary.Count > 0 ? ", " + string.Join(", ", pinnedSummary) : "") + " as the new rollback baseline.");
                        Log("[INFO] Saved to " + PinsFile + " - \"Rollback to pinned (core + all modules)\" and per-module \"Rollback to pinned\" now target this state.");
                        ShowCoreCommit();
                    });
                }
                finally { moduleOpBusy = false; }
            });
        }

        // The safe counterpart to Pin current state, and the replacement
        // for the old standalone "Rollback Core to pinned" button (removed
        // after it caused exactly the mismatch this avoids): rolls core
        // AND every currently-installed module back to their pinned
        // commits together, in one action, so core and a module can never
        // end up on two different, incompatible commits from using this.
        // A module with no known pin (added after the last Pin current
        // state, or never pinned at all) is skipped and logged rather than
        // guessed at - everything else still proceeds. Deliberately stops
        // after the checkouts, same as every other rollback/update action
        // here - Build is a separate manual step.
        void RollbackToPinnedAll()
        {
            if (moduleOpBusy) { Log("[WARN] Another module/core operation is already running - wait for it to finish"); return; }
            string git = FindGit();
            if (git == null) { Log("[FAIL] git.exe not found - install Git for Windows first"); return; }

            if (MessageBox.Show(
                "Roll back CORE and EVERY installed module to the pinned baseline together?\n\n" +
                "Not rebuilt automatically - run Build once everything's checked out.",
                "Rollback to pinned (core + modules)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            moduleOpBusy = true;
            Log("[INFO] Rolling back core + all modules to the pinned baseline (not rebuilt yet)...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Dictionary<string, string> pins = LoadPins();

                    string coreCommit = pins["__core__"];
                    RunStreamed(git, "fetch origin", AcoreSrcDir);
                    int coreCode = RunStreamed(git, "checkout " + coreCommit, AcoreSrcDir);
                    SafeInvoke(delegate
                    {
                        if (coreCode == 0) Log("[OK] Core rolled back to " + coreCommit + ".");
                        else Log("[FAIL] Core rollback failed (exit " + coreCode + ") - modules below still proceed.");
                    });

                    if (Directory.Exists(ModulesSrcDir))
                    {
                        foreach (string dir in Directory.GetDirectories(ModulesSrcDir))
                        {
                            string name = Path.GetFileName(dir);
                            string commit;
                            if (!pins.TryGetValue(name, out commit))
                            {
                                string nameCaptured = name;
                                SafeInvoke(delegate { Log("[WARN] No known pin for '" + nameCaptured + "' - left as-is."); });
                                continue;
                            }
                            RunStreamed(git, "fetch origin", dir);
                            int code = RunStreamed(git, "checkout " + commit, dir);
                            string nameCap = name;
                            string commitCap = commit;
                            SafeInvoke(delegate
                            {
                                if (code == 0) Log("[OK] " + nameCap + " rolled back to " + commitCap + ".");
                                else Log("[FAIL] " + nameCap + " rollback failed (exit " + code + ")");
                            });
                        }
                    }

                    SafeInvoke(delegate
                    {
                        Log("[INFO] Rollback complete - not rebuilt yet. Run Build now once everything's checked out.");
                        ShowCoreCommit();
                    });
                }
                finally { moduleOpBusy = false; }
            });
        }

        // Pull-only, same reasoning as UpdateSelectedModule() - moves core
        // off its pinned/detached commit, stashes any local changes, pulls
        // latest. No cmake, no build; Build card handles that separately.
        void UpdateCore()
        {
            if (moduleOpBusy) { Log("[WARN] Another module/core operation is already running - wait for it to finish"); return; }
            string git = FindGit();
            if (git == null) { Log("[FAIL] git.exe not found - install Git for Windows first"); return; }
            moduleOpBusy = true;
            Log("[WARN] Updating the CORE - this can break every module at once. Make sure you have a recent DB backup before restarting the server.");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    RunStreamed(git, "fetch origin", AcoreSrcDir);
                    string branch = RunCapture(git, "symbolic-ref refs/remotes/origin/HEAD", null, AcoreSrcDir).Trim();
                    if (branch.StartsWith("refs/remotes/origin/")) branch = branch.Substring("refs/remotes/origin/".Length);
                    if (branch.Length == 0) branch = "Playerbot";
                    RunStreamed(git, "checkout " + branch, AcoreSrcDir);
                    RunStreamed(git, "stash", AcoreSrcDir);
                    int code = RunStreamed(git, "pull", AcoreSrcDir);
                    SafeInvoke(delegate
                    {
                        if (code == 0)
                        {
                            Log("[OK] Core updated (not rebuilt yet).");
                            Log("[INFO] Update any modules you want, then use Build once for everything.");
                            ShowCoreCommit();
                        }
                        else Log("[FAIL] Core pull failed (exit " + code + ")");
                    });
                }
                finally { moduleOpBusy = false; }
            });
        }

        // The one and only place that actually compiles anything. Shells
        // out to the existing, already-proven 03-build.ps1 rather than
        // reimplementing cmake/MSBuild invocation natively in C# - that
        // script has real MSVC-specific fixes (page file sizing, PCH
        // handling, OpenSSL DLL naming) worked out through painful past
        // debugging, and duplicating that logic here would mean keeping
        // two build pipelines in sync forever instead of one source of
        // truth. Looks for the script next to the running exe, matching
        // where UpdateFromGitea() already keeps 07-build-wotlk-menu.ps1.
        //
        // clean=true wipes the build folder before calling the script,
        // mirroring the Linux menu's Rebuild only / Clean Rebuild split -
        // some module changes (a fresh add/remove, or weird cmake cache
        // state) genuinely need a full reconfigure from zero, while most
        // day-to-day updates don't need paying that time cost every time.
        // 03-build.ps1 itself is a black box to this GUI (no visibility
        // into whether it has its own clean/incremental flag), so the
        // safest generic way to force a clean build is to remove the
        // cmake build directory ourselves first - same approach Linux
        // uses (rm -rf build) since it works regardless of what's inside
        // the script. Assumes the build directory is AcoreSrcDir\build,
        // matching the same convention used everywhere else in this
        // codebase (InstallDir == AcoreSrcDir == C:\Azerothcore) - flag
        // this if 03-build.ps1 actually uses a different path.
        // Confirmed from an actual build log: 03-build.ps1's cmake build
        // directory is C:\Build, NOT AcoreSrcDir\build as first assumed.
        const string BuildDir = @"C:\Build";

        // Self-healing check run before every build. If a launch of this
        // menu ever ran an OLDER version of EnsureCleanInstallDir() (the
        // one that incorrectly deleted CMakeLists.txt/PreLoad.cmake/src/
        // deps as "cleanup" before that bug was fixed), those files stay
        // missing on disk even after upgrading to the fixed exe - fixing
        // a bug going forward never retroactively restores what it
        // already deleted. Since AcoreSrcDir is a git checkout, anything
        // git still tracks can be restored instantly with a plain
        // checkout instead of needing a fresh clone. Runs silently when
        // everything's already present - only logs anything if it
        // actually had to restore something.
        void EnsureCoreSourceFilesRestored()
        {
            string git = FindGit();
            if (git == null) { SafeInvoke(delegate { Log("[WARN] git.exe not found - cannot verify/restore core source files"); }); return; }

            string[] criticalPaths = new string[] { "CMakeLists.txt", "PreLoad.cmake", "src", "deps", "conf" };
            List<string> missing = new List<string>();
            foreach (string p in criticalPaths)
            {
                string full = Path.Combine(AcoreSrcDir, p);
                if (!Directory.Exists(full) && !File.Exists(full)) missing.Add(p);
            }
            if (missing.Count == 0) return;

            string missingList = string.Join(", ", missing);
            SafeInvoke(delegate { Log("[WARN] " + missingList + " missing from " + AcoreSrcDir + " - restoring from git..."); });
            foreach (string p in missing)
            {
                int code = RunStreamed(git, "checkout -- \"" + p + "\"", AcoreSrcDir);
                string pCaptured = p;
                if (code == 0) SafeInvoke(delegate { Log("[OK] Restored " + pCaptured); });
                else SafeInvoke(delegate { Log("[FAIL] Could not restore " + pCaptured + " (exit " + code + ") - may need a fresh clone"); });
            }
        }

        // Runs the numbered-pipeline's own source-compat patch script (the
        // one that removes mysql_ssl_mode, fixes the Cell::VisitObjects ODR
        // issue, updates LLMChatterShared.cpp's BuildChatPacket call, etc.)
        // right before every build - same self-heal-before-build slot as
        // EnsureCoreSourceFilesRestored() right above. Every patch in that
        // script is written to detect-then-replace and skip cleanly when
        // already applied or not present, so calling it unconditionally on
        // every build is safe: it either fixes a known regression once, or
        // does nothing.
        //
        // IMPORTANT - filename unconfirmed: this looks for
        // "02-source-patches.ps1" (matching the file captain actually
        // shared), which may be a local working copy rather than whatever
        // name the real pipeline uses. Rename the file on disk, this
        // constant, or the RawBase download below - whichever is easiest -
        // so they all match exactly, since both File.Exists and the Gitea
        // raw URL need an exact name.
        //
        // Self-heals via the same Gitea raw download as 03-build.ps1 -
        // BUT unlike 03-build.ps1 (a hard requirement; a failed download
        // there aborts the whole build), a missing/failed patch script
        // here only skips the patches and lets the build attempt proceed
        // anyway. Reasoning: patches fix KNOWN regressions, so without
        // them a build might fail with an already-diagnosed error instead
        // of a mysterious one - annoying, but not something to block a
        // build over, and definitely not something to fail loudly on for
        // a less tech-savvy user who just wants Rebuild to do its best.
        // REQUIRES: "02-source-patches.ps1" (or whatever it's renamed to)
        // actually pushed to the wotlk-windows-launcher Gitea repo at the
        // same RawBase path 03-build.ps1/07/08 already live at - confirm
        // that's done, or this download will just 404 every time.
        void RunSourcePatchesScript()
        {
            string script = Path.Combine(ScriptsDir, "02-source-patches.ps1");
            if (!File.Exists(script))
            {
                SafeInvoke(delegate { Log("[WARN] 02-source-patches.ps1 not found at: " + script + " - downloading a fresh copy from Gitea..."); });
                try { Directory.CreateDirectory(ScriptsDir); } catch { }
                int dlCode = RunCurlDownload(RawBase + "02-source-patches.ps1", script);
                if (dlCode != 0 || !File.Exists(script))
                {
                    SafeInvoke(delegate { Log("[WARN] Could not download 02-source-patches.ps1 (curl exit " + dlCode + ") - skipping source-compat patches for this build"); });
                    return;
                }
                SafeInvoke(delegate { Log("[OK] Downloaded 02-source-patches.ps1"); });
            }
            SafeInvoke(delegate { Log("[INFO] Applying known source-compat patches (02-source-patches.ps1)..."); });
            int code = RunStreamed("powershell",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"", ScriptsDir);
            if (code != 0)
                SafeInvoke(delegate { Log("[WARN] Source-compat patch script exited with code " + code + " - check the log above, continuing to build anyway"); });
        }

        void RunBuildScript(bool clean)
        {
            if (moduleOpBusy) { Log("[WARN] Another module/core operation is already running - wait for it to finish"); return; }
            string script = Path.Combine(ScriptsDir, "03-build.ps1");
            moduleOpBusy = true;
            if (clean) Log("[INFO] Clean Full Build - wiping the build folder first, this can take longer than usual...");
            else Log("[INFO] Running 03-build.ps1 - this can take a while, output streams below...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // Self-heal before touching anything else: 03-build.ps1
                    // is the one script this whole build path depends on,
                    // and it's easy to lose (accidental delete, a fresh
                    // AzerothCore root, an exe copied somewhere new). Rather
                    // than failing outright, fetch a fresh copy from the
                    // same Gitea raw source UpdateFromGitea() already uses,
                    // via the same proven curl path (RunCurlDownload) -
                    // before any build work starts.
                    if (!File.Exists(script))
                    {
                        SafeInvoke(delegate { Log("[WARN] 03-build.ps1 not found next to the exe at: " + script + " - downloading a fresh copy from Gitea..."); });
                        int dlCode = RunCurlDownload(RawBase + "03-build.ps1", script);
                        if (dlCode != 0 || !File.Exists(script))
                        {
                            SafeInvoke(delegate
                            {
                                Log("[FAIL] Could not download 03-build.ps1 (curl exit " + dlCode + ") - copy it from your install scripts folder into the same folder as WOTLK-Menu.exe");
                            });
                            return;
                        }
                        SafeInvoke(delegate { Log("[OK] Downloaded 03-build.ps1 - continuing build..."); });
                    }

                    EnsureCoreSourceFilesRestored();
                    RunSourcePatchesScript();

                    if (clean && Directory.Exists(BuildDir))
                    {
                        try
                        {
                            Directory.Delete(BuildDir, true);
                            SafeInvoke(delegate { Log("[INFO] Removed " + BuildDir + " - reconfiguring from scratch."); });
                        }
                        catch (Exception ex)
                        {
                            string msgCaptured = ex.Message;
                            SafeInvoke(delegate { Log("[WARN] Could not remove build folder (" + msgCaptured + ") - continuing anyway, 03-build.ps1 may reuse cached state."); });
                        }
                    }

                    int code = RunStreamed("powershell",
                        "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"", ScriptsDir);
                    List<string> createdConfigs = code == 0 ? EnsureAllModuleConfigsFromDist() : new List<string>();
                    // null means the .dist couldn't be found in the source
                    // tree, or the live .conf doesn't exist yet (both
                    // skipped, not errors) - see ApplyMissingConfigKeys()
                    // below for why this appends rather than replaces, and
                    // always backs up the .conf first when it does.
                    // A missing worldserver.conf / authserver.conf is created
                    // from its .dist first (same first-time copy modules get
                    // above), so the key check right below then runs against
                    // a real file instead of being skipped.
                    bool createdWorldConf = code == 0 && EnsureCoreConfFromDist("worldserver.conf.dist", WorldConf);
                    bool createdAuthConf  = code == 0 && EnsureCoreConfFromDist("authserver.conf.dist", AuthConf);
                    List<string> addedWorldKeys = code == 0 ? ApplyMissingConfigKeys("worldserver.conf", "worldserver.conf.dist", WorldConf) : null;
                    List<string> addedAuthKeys  = code == 0 ? ApplyMissingConfigKeys("authserver.conf", "authserver.conf.dist", AuthConf) : null;
                    SafeInvoke(delegate
                    {
                        if (code == 0)
                        {
                            Log("[OK] Build finished. Restart World/Auth server to use the update.");
                            foreach (string name in createdConfigs)
                                Log("[OK] Created " + name + " from .dist (first-time config for a new module)");
                            if (createdWorldConf)
                                Log("[OK] Created worldserver.conf from worldserver.conf.dist (first-time config) - set the DB login lines and DataDir in it before starting the server");
                            if (createdAuthConf)
                                Log("[OK] Created authserver.conf from authserver.conf.dist (first-time config) - set LoginDatabaseInfo in it before starting the server");
                            LogAppliedConfigKeys("worldserver.conf", addedWorldKeys);
                            LogAppliedConfigKeys("authserver.conf", addedAuthKeys);
                            ShowCoreCommit();
                        }
                        else Log("[FAIL] Build script exited with code " + code + " - check the log above");
                    });
                }
                finally { moduleOpBusy = false; }
            });
        }

        // A brand-new module's build only ever installs its .conf.dist
        // template - it deliberately never auto-creates the live .conf
        // itself the first time, so a module that was just added never
        // gets stuck: worldserver would otherwise log "Failed open file"
        // for it on every boot until someone copies it manually. Handles
        // that first-time copy for ANY module, not just whichever one
        // happened to get reported.
        //
        // For a module .conf that ALREADY exists, this now also runs the
        // same missing-key auto-append used for worldserver.conf/
        // authserver.conf (AppendMissingKeysFromDist: backup to .bck, then
        // append only the genuinely missing key(s) with .dist defaults) -
        // AiPlayerbot.RandomBotConcentrateInPlayerZone showing up missing
        // from playerbots.conf after a mod-playerbots update is exactly
        // this same gap, just for a module conf instead of a core one.
        // Every existing tuned line stays untouched either way.
        List<string> EnsureAllModuleConfigsFromDist()
        {
            List<string> created = new List<string>();
            if (!Directory.Exists(ModulesDir)) return created;
            foreach (string dist in Directory.GetFiles(ModulesDir, "*.conf.dist"))
            {
                string conf = dist.Substring(0, dist.Length - ".dist".Length);
                if (!File.Exists(conf))
                {
                    try { File.Copy(dist, conf); created.Add(Path.GetFileName(conf)); }
                    catch { }
                }
                else
                {
                    string confName = Path.GetFileName(conf);
                    List<string> added = AppendMissingKeysFromDist(dist, conf, confName);
                    if (added.Count > 0)
                    {
                        SafeInvoke(delegate
                        {
                            Log("[OK] " + confName + " was missing " + added.Count + " key(s) from its .dist template - appended with default values, previous file backed up to " + confName + ".bck:");
                            foreach (string key in added)
                                Log("        - " + key);
                        });
                    }
                }
            }
            return created;
        }

        // Unlike module .conf files (handled above), worldserver.conf and
        // authserver.conf are never touched by anything else in this whole
        // file after their original one-time copy at install (stage 04's
        // copy_configs equivalent) - a core update just pulls new source and
        // rebuilds binaries, so a fresh key added to either .dist upstream
        // (TaxiFlightSpeed being the case that first surfaced this) sits
        // there unused until someone notices the server complaining about it
        // at boot.
        //
        // Considered and rejected: replacing the whole .conf with a fresh
        // copy of .dist. A .dist only ever contains DEFAULT values, so a
        // full replace would silently reset every tuned setting - bot
        // counts, rates, module toggles, everything - back to stock the
        // moment core introduces even one new key. A .bck backup would make
        // that recoverable, but only via a manual line-by-line diff
        // afterward, which is far more tedious than the one or two new
        // lines this actually needed.
        //
        // What this does instead: back up the live .conf to .conf.bck (every
        // time it's about to change - never skipped), then APPEND only the
        // genuinely missing key(s) with their .dist default values to the
        // end of the existing file. Every line already there stays
        // byte-for-byte untouched, so nothing tuned is ever at risk, while
        // the server still gets a real value for the new key instead of
        // just a log message about it. Retuning a newly-appended key away
        // from its default is then a normal one-line edit, same as any
        // other setting.
        //
        // Returns null if <distFileName> couldn't be located under
        // AcoreSrcDir\src, or confPath doesn't exist yet (both skipped, not
        // errors - nothing to compare/patch against). Otherwise returns the
        // (possibly empty) sorted list of key names actually appended this
        // run - empty means the conf already had everything, so nothing was
        // touched at all (no backup made either, since nothing changed).
        List<string> ApplyMissingConfigKeys(string label, string distFileName, string confPath)
        {
            string dist = FindConfDist(distFileName);
            if (dist == null || !File.Exists(confPath)) return null;
            return AppendMissingKeysFromDist(dist, confPath, label);
        }

        // First-time copy for worldserver.conf / authserver.conf, the same
        // thing EnsureAllModuleConfigsFromDist() already does for module
        // confs: if the live .conf doesn't exist yet, create it from its
        // .dist. COPY, not move - the build reinstalls the .dist every time
        // and ApplyMissingConfigKeys() needs it as the reference for new
        // keys later. Prefers the .dist the build just installed next to the
        // .conf, falls back to the source-tree search. An existing .conf is
        // never touched here. Returns true only when it was just created.
        bool EnsureCoreConfFromDist(string distFileName, string confPath)
        {
            if (File.Exists(confPath)) return false;
            string dist = Path.Combine(Path.GetDirectoryName(confPath), distFileName);
            if (!File.Exists(dist)) dist = FindConfDist(distFileName);
            if (dist == null || !File.Exists(dist)) return false;
            try { File.Copy(dist, confPath); return true; }
            catch { return false; }
        }

        // The actual shared diff-backup-append logic behind
        // ApplyMissingConfigKeys (world/auth, where the .dist has to be
        // searched for under AcoreSrcDir\src) and EnsureAllModuleConfigsFromDist
        // below (modules, where the .dist already sits right next to the
        // .conf in ModulesDir - no search needed). Split out so both paths
        // share one implementation instead of two copies drifting apart.
        // Returns the (possibly empty) sorted list of key names actually
        // appended - empty means confPath already had everything, so
        // nothing was touched at all (no backup made either).
        List<string> AppendMissingKeysFromDist(string distPath, string confPath, string label)
        {
            Dictionary<string, string> distLines = ParseConfKeyLines(distPath);
            HashSet<string> existingKeys = ParseConfKeys(confPath);

            List<string> missing = new List<string>();
            foreach (string key in distLines.Keys)
                if (!existingKeys.Contains(key)) missing.Add(key);
            missing.Sort();
            if (missing.Count == 0) return missing;

            string backupPath = confPath + ".bck";
            try { File.Copy(confPath, backupPath, true); }
            catch (Exception ex)
            {
                string msgCaptured = ex.Message;
                SafeInvoke(delegate { Log("[WARN] Could not back up " + confPath + " (" + msgCaptured + ") - skipping auto-append of new " + label + " key(s) as a precaution"); });
                return new List<string>();
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(Environment.NewLine);
            sb.Append("# --- Added automatically by wowmenu on " + DateTime.Now.ToString("yyyy-MM-dd") +
                " - new key(s) from a core/module update, default value(s) from " + Path.GetFileName(distPath) +
                " (retune below if you want something other than the default) ---" + Environment.NewLine);
            foreach (string key in missing)
                sb.Append(distLines[key] + Environment.NewLine);

            File.AppendAllText(confPath, sb.ToString());
            return missing;
        }

        // Logs the outcome of one ApplyMissingConfigKeys() call in the GUI
        // log box - split out since RunBuildScript() needs to do this twice
        // (world + auth) with identical wording either way.
        void LogAppliedConfigKeys(string label, List<string> added)
        {
            if (added == null)
            {
                Log("[WARN] Could not find " + label + ".dist under " + AcoreSrcDir + "\\src, or " + label + " itself doesn't exist yet - skipped the new-config-key check");
                return;
            }
            if (added.Count > 0)
            {
                Log("[OK] " + label + " was missing " + added.Count + " key(s) from the current .dist template (likely added by a core/module update) - appended with default values, previous file backed up to " + label + ".bck:");
                foreach (string key in added)
                    Log("        - " + key);
                Log("[INFO] Defaults were used - retune any of these in " + label + " if you want something other than the default, then restart.");
            }
        }

        // AzerothCore has moved conf.dist files' locations in the source
        // tree before (e.g. src\server\worldserver\... in older layouts vs
        // src\server\apps\worldserver\... in current master) - search under
        // src\ instead of hardcoding one path, so this keeps working no
        // matter which commit the core happens to be pinned to. Picks the
        // first match, which is fine since AzerothCore only ships one of
        // each conf.dist.
        string FindConfDist(string distFileName)
        {
            try
            {
                string searchRoot = Path.Combine(AcoreSrcDir, "src");
                if (!Directory.Exists(searchRoot)) return null;
                string[] candidates = Directory.GetFiles(searchRoot, distFileName, SearchOption.AllDirectories);
                if (candidates.Length > 0) return candidates[0];
            }
            catch { }
            return null;
        }

        // Extracts config key names (the part left of '=') from an
        // AzerothCore-style .conf/.conf.dist file - ignores comments (#) and
        // blank lines. Only compares key names, never values, so it never
        // flags a key the captain has deliberately retuned away from the
        // .dist default - just keys that are genuinely absent.
        static HashSet<string> ParseConfKeys(string path)
        {
            HashSet<string> keys = new HashSet<string>();
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                keys.Add(line.Substring(0, eq).Trim());
            }
            return keys;
        }

        // Same idea as ParseConfKeys, but keeps the FULL raw line (key =
        // default value, exactly as .dist has it) so a missing key can be
        // appended with its real default rather than just the bare name.
        // First occurrence of a key wins, matching ParseConfKeys' behavior.
        static Dictionary<string, string> ParseConfKeyLines(string path)
        {
            Dictionary<string, string> lines = new Dictionary<string, string>();
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                if (!lines.ContainsKey(key)) lines[key] = line;
            }
            return lines;
        }

        // A core update can bump the map-data format AzerothCore expects,
        // requiring a newer wowgaming/client-data release than whatever the
        // install originally shipped with. Auto-detects the actual latest
        // release tag via GitHub's API (falls back to v20 if that call
        // fails), downloads via the same curl.exe path UpdateFromGitea()
        // already uses successfully against gitea.com, and extracts via
        // PowerShell's Expand-Archive rather than adding a new .NET
        // zip-handling assembly reference for one feature.
        //
        // IMPORTANT: wowgaming's Data.zip only ever contains the client
        // asset folders (dbc, maps, vmaps, mmaps, Cameras) - it does NOT
        // contain AzerothCore's own data\sql\ folder, which the core
        // build installs into that SAME data\ directory (the DBUpdater
        // reads its update-history comparisons straight from data\sql\).
        // An earlier version of this method backed up and replaced the
        // ENTIRE data\ folder wholesale, which silently deleted data\sql\
        // along with it - the exact cause of DBUpdater then reporting
        // every previously-applied SQL file as "missing in your update
        // directory now" on next boot. Fixed by extracting into an
        // isolated temp folder first, then swapping ONLY the specific
        // client-asset subfolders into place - data\sql\ (and anything
        // else already in data\) is never touched.
        static readonly string DataDir = InstallDir + @"\data";
        static readonly string[] ClientDataSubfolders = { "dbc", "maps", "vmaps", "mmaps", "Cameras" };

        void DownloadLatestClientData()
        {
            if (moduleOpBusy) { Log("[WARN] Another module/core operation is already running - wait for it to finish"); return; }
            moduleOpBusy = true;
            Log("[INFO] Checking latest wowgaming/client-data release...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string tempExtractDir = Path.Combine(Path.GetTempPath(), "wotlk-client-data-extract-" + DateTime.Now.Ticks);
                try
                {
                    string latestTag = RunCapture("powershell",
                        "-NoProfile -Command \"(Invoke-RestMethod 'https://api.github.com/repos/wowgaming/client-data/releases/latest').tag_name\"",
                        null).Trim();
                    if (latestTag.Length == 0 || latestTag.IndexOf("[FAIL]", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        latestTag = "v20";
                        SafeInvoke(delegate { Log("[WARN] Could not reach GitHub API - falling back to " + latestTag); });
                    }
                    else
                    {
                        string tagCaptured = latestTag;
                        SafeInvoke(delegate { Log("[INFO] Latest client-data release: " + tagCaptured); });
                    }

                    string zipPath = Path.Combine(Path.GetTempPath(), "wotlk-client-data.zip");
                    string url = "https://github.com/wowgaming/client-data/releases/download/" + latestTag + "/Data.zip";
                    SafeInvoke(delegate { Log("[INFO] Downloading (this is a large file, can take a while)..."); });
                    int code = RunCurlDownload(url, zipPath);
                    if (code != 0 || !File.Exists(zipPath))
                    {
                        SafeInvoke(delegate { Log("[FAIL] Download failed - keeping existing client data"); });
                        return;
                    }

                    Directory.CreateDirectory(tempExtractDir);
                    SafeInvoke(delegate { Log("[INFO] Extracting to a temp folder first..."); });
                    string extractOut = RunCapture("powershell",
                        "-NoProfile -Command \"Expand-Archive -Path '" + zipPath + "' -DestinationPath '" + tempExtractDir + "' -Force\"", null);
                    if (extractOut.IndexOf("[FAIL]", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string extractCaptured = extractOut;
                        SafeInvoke(delegate { Log("[FAIL] Extraction error: " + extractCaptured); });
                        return;
                    }
                    try { File.Delete(zipPath); } catch { }

                    Directory.CreateDirectory(DataDir); // does nothing if it already exists (with sql\ etc inside)
                    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    int swapped = 0;
                    foreach (string folder in ClientDataSubfolders)
                    {
                        string src = Path.Combine(tempExtractDir, folder);
                        if (!Directory.Exists(src))
                        {
                            string missingFolder = folder;
                            SafeInvoke(delegate { Log("[WARN] Zip did not contain a '" + missingFolder + "' folder - skipping"); });
                            continue;
                        }
                        string dest = Path.Combine(DataDir, folder);
                        if (Directory.Exists(dest))
                        {
                            string backup = dest + ".bak-" + stamp;
                            try { Directory.Move(dest, backup); }
                            catch (Exception ex)
                            {
                                string folderCaptured = folder;
                                string msgCaptured = ex.Message;
                                SafeInvoke(delegate { Log("[FAIL] Could not back up existing '" + folderCaptured + "': " + msgCaptured); });
                                continue;
                            }
                        }
                        Directory.Move(src, dest);
                        swapped++;
                    }

                    string swappedCaptured2 = swapped.ToString();
                    SafeInvoke(delegate
                    {
                        Log("[OK] Client data updated (" + swappedCaptured2 + " folder(s) swapped). Restart World server to use it.");
                        Log("[INFO] data\\sql\\ was never touched - only dbc/maps/vmaps/mmaps/Cameras were replaced.");
                        Log("[INFO] Old folders backed up as data\\<name>.bak-* if you need to roll back.");
                    });
                }
                finally
                {
                    try { if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, true); } catch { }
                    moduleOpBusy = false;
                }
            });
        }
    }

    // Custom-drawn bordered panel used for the dashboard's card layout.
    // Simple 1px border in the theme's border color around a solid panel -
    // deliberately plain, no gradients or shadows, to stay lightweight and
    // render identically regardless of Windows theme/DPI settings.
    // Small custom-drawn triangle arrow button - no native Windows chrome
    // at all, so no theming limitations to fight (unlike DarkSpinner's
    // built-in spin buttons, which partially ignore BackColor and crash
    // if you try to force classic rendering via SetWindowTheme).
    public class SpinArrow : Control
    {
        bool pointUp;
        Color bg, fg;

        public SpinArrow(bool up, Color background, Color foreground)
        {
            pointUp = up;
            bg = background;
            fg = foreground;
            BackColor = background;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            // A push-button-like control that stays keyboard-focusable can
            // get a native focus-cue overlay drawn over it the instant it's
            // clicked - a separate rendering pass from our own OnPaint, and
            // one that always starts from the top-left corner. That's the
            // real source of the stray corner artifact on these arrows (and
            // on RoundedButton, fixed the same way below). Disabling
            // Selectable means it can never receive keyboard focus, so
            // there's nothing left to trigger that overlay at all.
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Opaque, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (SolidBrush bgBrush = new SolidBrush(bg))
                e.Graphics.FillRectangle(bgBrush, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (SolidBrush bgBrush = new SolidBrush(bg))
                e.Graphics.FillRectangle(bgBrush, ClientRectangle);
            using (SolidBrush fgBrush = new SolidBrush(fg))
            {
                Point[] tri;
                if (pointUp)
                    tri = new Point[] { new Point(Width / 2, 2), new Point(2, Height - 2), new Point(Width - 2, Height - 2) };
                else
                    tri = new Point[] { new Point(2, 2), new Point(Width - 2, 2), new Point(Width / 2, Height - 2) };
                e.Graphics.FillPolygon(fgBrush, tri);
            }
        }
    }

    // Fully custom numeric spinner - a TextBox for the value plus two
    // SpinArrow buttons, replacing DarkSpinner entirely. API shape
    // (Minimum/Maximum/Value int properties) mirrors DarkSpinner closely
    // enough that call sites barely needed to change.
    public class DarkSpinner : Panel
    {
        TextBox txt;
        int minVal, maxVal, curVal;
        public event EventHandler ValueChanged;

        public int Minimum { get { return minVal; } set { minVal = value; } }
        public int Maximum { get { return maxVal; } set { maxVal = value; } }
        public int Value
        {
            get { return curVal; }
            set
            {
                int clamped = value;
                if (clamped < minVal) clamped = minVal;
                if (clamped > maxVal) clamped = maxVal;
                curVal = clamped;
                txt.Text = curVal.ToString();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        // Arrows always use the app's blue accent, independent of the text
        // foreground color passed in - matches the accent color used for
        // numbers/values throughout the rest of the theme.
        static readonly Color ArrowColor = Color.FromArgb(79, 166, 255);

        // Same steel-blue border used by CardPanel's "SERVER STATUS" style
        // cards - every rounded rectangle in the app (cards, buttons,
        // spinners, the model dropdown) now shares this one border color
        // instead of each control picking its own slightly different gray.
        static readonly Color BorderColShared = Color.FromArgb(58, 90, 130);
        const int CornerRadius = 5;

        public DarkSpinner(Color bg, Color fg)
        {
            Height = 24;
            Width = 90;
            BackColor = bg;
            minVal = 0;
            maxVal = 100;
            curVal = 0;
            DoubleBuffered = true;
            // Opaque tells WinForms this control paints its ENTIRE surface
            // itself every time, so the OS skips its own WM_ERASEBKGND
            // background clear beforehand - that stray clear (often white)
            // showing through for one frame around the rounded corners is
            // what caused the corner-color glitches on hover/repaint.
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Opaque, true);

            txt = new TextBox();
            txt.BorderStyle = BorderStyle.None;
            txt.BackColor = bg;
            txt.ForeColor = fg;
            txt.Location = new Point(4, 4);
            txt.Width = Width - 24;
            txt.Text = "0";
            txt.KeyPress += delegate(object s, KeyPressEventArgs e)
            {
                if (!char.IsDigit(e.KeyChar) && e.KeyChar != (char)Keys.Back) e.Handled = true;
            };
            txt.Leave += delegate { CommitTextValue(); };
            Controls.Add(txt);

            SpinArrow up = new SpinArrow(true, bg, ArrowColor);
            up.Location = new Point(Width - 20, 0);
            up.Size = new Size(18, 12);
            up.Click += delegate { Value = Value + 1; };
            Controls.Add(up);

            SpinArrow down = new SpinArrow(false, bg, ArrowColor);
            down.Location = new Point(Width - 20, 12);
            down.Size = new Size(18, 12);
            down.Click += delegate { Value = Value - 1; };
            Controls.Add(down);
        }

        void CommitTextValue()
        {
            int parsed;
            if (int.TryParse(txt.Text, out parsed)) Value = parsed;
            else txt.Text = Value.ToString();
        }

        // Public entry point for callers that read .Value immediately
        // on a button click, rather than after the textbox naturally
        // loses focus. Value only auto-commits on the textbox's own
        // Leave event - if a button click's Click handler executes
        // before that Leave event has fully processed (a real
        // possibility depending on click timing), reading .Value
        // directly would silently read the OLD, pre-edit number rather
        // than what's actually typed in the box. Calling this first
        // guarantees whatever's currently displayed gets parsed into
        // Value before it's read, regardless of focus timing.
        public void CommitPendingEdit() { CommitTextValue(); }

        // Same technique as CardPanel/RoundedButton: fill the PARENT's
        // background first (so nothing behind ever shows through), then
        // fill+stroke our own rounded path on top. No Region clipping
        // anywhere - a clipped Region leaves the four corner cutouts
        // completely unpainted by this control, exposing whatever the
        // backbuffer happened to hold there (often a stray black or white
        // sliver) instead of a clean match with the surrounding card.
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Color parentColor = Parent != null ? Parent.BackColor : BackColor;
            using (SolidBrush bg = new SolidBrush(parentColor))
                e.Graphics.FillRectangle(bg, ClientRectangle);
        }

        // Plain square corners, matching RoundedButton's own square style -
        // a rounded spinner sitting right next to a square button in the
        // same row (e.g. "Set bot count") looked visually inconsistent,
        // like two different UI kits mashed together. No GraphicsPath, no
        // rounding math, same "sidesteps the corner artifact question
        // entirely" reasoning RoundedButton already uses.
        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle r = ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            using (SolidBrush fillBrush = new SolidBrush(BackColor))
                e.Graphics.FillRectangle(fillBrush, r);
            using (Pen pen = new Pen(BorderColShared))
                e.Graphics.DrawRectangle(pen, r);
        }
    }

    // Fully custom dropdown replacing ComboBox for the AI Model picker -
    // same reasoning as DarkSpinner replacing NumericUpDown: a native
    // ComboBox's drop arrow and popup list are painted by Windows itself
    // and ignore BackColor/ForeColor no matter what's set, short of a full
    // owner-draw that still leaves the arrow glyph an untouchable light
    // system square. This uses a read-only TextBox for the display plus
    // the same SpinArrow triangle already used by DarkSpinner, and shows
    // its own small borderless popup Form (styled ListBox) for the list -
    // no native combo chrome left to fight.
    public class DarkCombo : Panel
    {
        // Minimal Items collection - just enough surface (Add/Clear/Count/
        // Contains/indexer) for this project's call sites, backed by a
        // plain List<string> since every item here is always a model name.
        public class ItemCollection
        {
            List<string> items = new List<string>();
            public void Add(string s) { items.Add(s); }
            public void Clear() { items.Clear(); }
            public int Count { get { return items.Count; } }
            public bool Contains(string s) { return items.Contains(s); }
            public string this[int i] { get { return items[i]; } }
            public List<string> Raw { get { return items; } }
        }

        TextBox txt;
        SpinArrow dropArrow;
        ItemCollection items;
        string selected;
        Color bgColor, fgColor;
        Form popup;

        public event EventHandler SelectedIndexChanged;
        public ItemCollection Items { get { return items; } }

        // Only ever set programmatically (list refresh, pre-selecting the
        // configured model) or by the popup's own click handler - never
        // fires SelectedIndexChanged on its own, matching how the existing
        // suppressModelPickEvent guard expects programmatic sets to behave.
        public string SelectedItem
        {
            get { return selected; }
            set { selected = value; txt.Text = value == null ? "" : value; }
        }

        public DarkCombo(Color bg, Color fg)
        {
            bgColor = bg;
            fgColor = fg;
            Height = 28;
            Width = 260;
            BackColor = bg;
            items = new ItemCollection();
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Opaque, true);

            txt = new TextBox();
            txt.ReadOnly = true;
            txt.BorderStyle = BorderStyle.None;
            txt.BackColor = bg;
            txt.ForeColor = fg;
            txt.Location = new Point(6, 6);
            txt.Cursor = Cursors.Hand;
            txt.Click += delegate { ToggleDropDown(); };
            Controls.Add(txt);

            dropArrow = new SpinArrow(false, bg, Color.FromArgb(79, 166, 255));
            dropArrow.Click += delegate { ToggleDropDown(); };
            Controls.Add(dropArrow);

            Resize += delegate { LayoutChildren(); };
            LayoutChildren();
        }

        void LayoutChildren()
        {
            dropArrow.Size = new Size(20, Math.Max(Height - 4, 10));
            dropArrow.Location = new Point(Width - 24, 2);
            txt.Width = Math.Max(Width - 34, 10);
        }

        void ToggleDropDown()
        {
            if (popup != null) { ClosePopup(); return; }
            if (items.Count == 0) return;

            popup = new Form();
            popup.FormBorderStyle = FormBorderStyle.None;
            popup.StartPosition = FormStartPosition.Manual;
            popup.ShowInTaskbar = false;
            popup.BackColor = bgColor;

            ListBox lb = new ListBox();
            lb.Dock = DockStyle.Fill;
            lb.BackColor = bgColor;
            lb.ForeColor = fgColor;
            lb.BorderStyle = BorderStyle.None;
            lb.Font = txt.Font;
            foreach (string s in items.Raw) lb.Items.Add(s);
            lb.Click += delegate
            {
                if (lb.SelectedItem != null)
                {
                    SelectedItem = lb.SelectedItem.ToString();
                    if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
                }
                ClosePopup();
            };
            popup.Controls.Add(lb);

            popup.Location = PointToScreen(new Point(0, Height));
            popup.Size = new Size(Width, Math.Min(200, items.Count * 18 + 4));
            popup.Deactivate += delegate { ClosePopup(); };
            popup.Show(this);
        }

        void ClosePopup()
        {
            // popup.Close() below can synchronously fire the popup's own
            // Deactivate event before returning, which calls ClosePopup()
            // again re-entrantly on the same thread. If that inner call
            // nulls out "popup" first, the OUTER call then finds it null
            // when it resumes and crashes with a NullReferenceException.
            // Fix: grab a local reference and null the field FIRST, before
            // touching Close()/Dispose() at all - any reentrant call then
            // sees popup already null and safely does nothing.
            if (popup == null) return;
            Form p = popup;
            popup = null;
            try { p.Close(); } catch { }
            try { p.Dispose(); } catch { }
        }

        // Same fill-parent-then-fill-and-stroke-own-path technique as
        // CardPanel/RoundedButton/DarkSpinner - no Region clipping, so
        // there's never an unpainted corner sliver for a stray backbuffer
        // color to leak through.
        static readonly Color BorderColShared = Color.FromArgb(58, 90, 130);
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Color parentColor = Parent != null ? Parent.BackColor : BackColor;
            using (SolidBrush bg = new SolidBrush(parentColor))
                e.Graphics.FillRectangle(bg, ClientRectangle);
        }

        // Plain square corners, matching RoundedButton and DarkSpinner -
        // this dropdown sits right next to "Refresh list" / "Check
        // current model" / "Delete selected" in the AI MODEL row, and a
        // rounded dropdown next to square buttons looked visually
        // inconsistent, like two different UI kits mashed together.
        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle r = ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            using (SolidBrush fillBrush = new SolidBrush(BackColor))
                e.Graphics.FillRectangle(fillBrush, r);
            using (Pen pen = new Pen(BorderColShared))
                e.Graphics.DrawRectangle(pen, r);
        }
    }

    // Fully custom-drawn button - deliberately NOT derived from Button.
    // A Button (even with UserPaint set) still carries FlatStyle.Flat's
    // native "hot"/pressed visual-style rendering underneath, which ignores
    // FlatAppearance overrides in some Windows theme states and shows up as
    // a mismatched light glow in the corners outside the rounded path on
    // hover. Deriving from plain Control instead - the same approach
    // SpinArrow/DarkSpinner/DarkCombo already use - means there is no
    // native button chrome left at all to produce that artifact.
    public class RoundedButton : Control
    {
        // Same steel-blue border as CardPanel/DarkSpinner/DarkCombo - every
        // rounded rectangle in the app now shares one border color instead
        // of buttons using their own slightly different gray, which is
        // exactly the mismatch that read as a stray corner artifact next
        // to a card's blue border.
        Color borderColor = Color.FromArgb(58, 90, 130);
        const int CornerRadius = 6;
        bool isHover, isPressed;

        public RoundedButton()
        {
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            MinimumSize = new Size(170, 34);
            // See SpinArrow's constructor comment - disabling Selectable
            // stops this control from ever taking keyboard focus, so there
            // is no native focus-cue overlay left that could paint over
            // our own rounded corners.
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Opaque, true);
        }

        // Plain Control doesn't auto-size to its text on its own (same
        // reason CardPanel needed an explicit height fix earlier) - this
        // override is what makes AutoSize actually work inside Row()'s
        // FlowLayoutPanel, matching how Button would have sized itself.
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size textSize = TextRenderer.MeasureText(Text, Font);
            int w = Math.Max(MinimumSize.Width, textSize.Width + 24);
            int h = Math.Max(MinimumSize.Height, textSize.Height + 14);
            return new Size(w, h);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (AutoSize) Size = GetPreferredSize(Size.Empty);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); isHover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); isHover = false; isPressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); isPressed = true; Invalidate(); }

        // IMPORTANT - history of this method, read before touching again:
        // The comment that used to live here claimed "plain Control
        // doesn't raise Click automatically the way Button does" and had
        // this method manually call OnClick() itself. That assumption was
        // WRONG and caused every button in the app to fire its bound
        // action TWICE per left-click. Proof: testing right-click (which
        // the manual call below explicitly excludes via the
        // e.Button==Left check) still fired the action ONCE - meaning a
        // path with NO button-type check at all was already raising
        // Click on its own, deeper than the overridable OnMouseUp/
        // base.OnMouseUp chain (removing the base.OnMouseUp() call alone
        // did not fix the duplicate, confirming it's not routed through
        // that virtual method at all). Since that native path already
        // fires Click once per genuine left-click release, this override
        // now ONLY tracks the pressed visual state - it does NOT call
        // OnClick itself anymore. If a future change needs to gate clicks
        // (e.g. block right-click explicitly), do it by overriding
        // WndProc and filtering the raw WM_LBUTTONUP/WM_RBUTTONUP
        // messages, not by adding another manual OnClick() call here.
        protected override void OnMouseUp(MouseEventArgs e)
        {
            isPressed = false;
            Invalidate();
        }

        // ControlPaint.Light()/.Dark() use an HLS-based algorithm that
        // behaves erratically on very dark colors like this app's theme -
        // instead of a subtle lighten/darken, they can jump much further
        // than intended (close to white, or oddly toward black). That is
        // what was actually causing the white hover wash and the earlier
        // black-corner reports, not any focus rectangle or Windows
        // caching. A plain linear per-channel adjustment is fully
        // predictable regardless of how dark the base color is.
        static Color AdjustBrightness(Color c, int delta)
        {
            int r = c.R + delta; if (r < 0) r = 0; if (r > 255) r = 255;
            int g = c.G + delta; if (g < 0) g = 0; if (g > 255) g = 255;
            int b = c.B + delta; if (b < 0) b = 0; if (b > 255) b = 255;
            return Color.FromArgb(c.A, r, g, b);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Color parentColor = Parent != null ? Parent.BackColor : BackColor;
            using (SolidBrush bg = new SolidBrush(parentColor))
                e.Graphics.FillRectangle(bg, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            Rectangle r = ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;

            Color fill = BackColor;
            if (!Enabled) fill = AdjustBrightness(BackColor, -8);
            else if (isPressed) fill = AdjustBrightness(BackColor, -14);
            else if (isHover) fill = AdjustBrightness(BackColor, 18);

            // Plain square rectangle - no GraphicsPath, no AddArc, nothing
            // for the rounding math to get wrong. Sidesteps the corner
            // artifact question entirely rather than chasing it further,
            // and matches the same blue border already used everywhere
            // else (cards, spinners, the dropdown).
            using (SolidBrush fillBrush = new SolidBrush(fill))
                e.Graphics.FillRectangle(fillBrush, r);
            using (Pen pen = new Pen(borderColor))
                e.Graphics.DrawRectangle(pen, r);

            Color textColor = Enabled ? ForeColor : AdjustBrightness(ForeColor, -40);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    public class CardPanel : Panel
    {
        Color borderColor;
        const int CornerRadius = 10;

        public CardPanel(Color background, Color border)
        {
            DoubleBuffered = true;
            BackColor = background;
            borderColor = border;
            Padding = new Padding(12);
            Margin = new Padding(0, 0, 12, 12);
        }

        // Builds a rounded-rectangle path from arc segments at each corner,
        // connected by straight edges - the standard GDI+ technique since
        // there's no built-in rounded-rectangle primitive.
        public static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(Rectangle bounds, int radius)
        {
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Fill with the PARENT's background color, not our own -
            // otherwise the default square fill would sit behind our
            // rounded path, and the four corner cutouts would just show a
            // hidden square of card-color instead of blending with
            // whatever's actually behind this card on the dashboard.
            Color parentColor = Parent != null ? Parent.BackColor : BackColor;
            using (SolidBrush bg = new SolidBrush(parentColor))
                e.Graphics.FillRectangle(bg, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;

            using (System.Drawing.Drawing2D.GraphicsPath path = RoundedRectPath(r, CornerRadius))
            {
                // Fill background along the rounded path first - the plain
                // Panel base-class fill is always a square rectangle, which
                // would show through behind a rounded border otherwise.
                using (SolidBrush fillBrush = new SolidBrush(BackColor))
                    e.Graphics.FillPath(fillBrush, path);
                using (Pen pen = new Pen(borderColor))
                    e.Graphics.DrawPath(pen, path);
            }
            // Deliberately skip base.OnPaint(e) - we've already handled the
            // background fill above along the rounded path.
        }
    }

    // Status label with a small drawn circle instead of an emoji character
    // (this project stays ASCII-only throughout - emoji in a source file
    // risks the same encoding corruption that bit an earlier draft of this
    // menu when Gitea served box-drawing characters without a BOM).
    public class DotLabel : Label
    {
        Color dotColor = Color.Gray;

        public DotLabel()
        {
            AutoSize = false;
            Height = 24;
            TextAlign = ContentAlignment.MiddleLeft;
            Padding = new Padding(16, 0, 0, 0);
        }

        public void SetState(string text, Color color)
        {
            Text = text;
            dotColor = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (SolidBrush b = new SolidBrush(dotColor))
            {
                int size = 8;
                int y = (Height - size) / 2;
                e.Graphics.FillEllipse(b, 2, y, size, size);
            }
        }
    }

    // Simple vector glyphs drawn with GDI+ primitives for each sidebar
    // entry - kept as plain shapes (no emoji, no external image files) for
    // the same encoding-safety reason the rest of this project stays
    // ASCII-only, and so the look renders identically regardless of the
    // Windows font/emoji set installed on the machine running it.
    public enum NavGlyph
    {
        Dashboard, World, Auth, Brain, Bots, Admin, Backup, Settings, Logs, About
    }

    // Combines a NavIcon glyph + text label into one clickable sidebar row,
    // since a plain Button can't cleanly host a separately-drawn custom
    // icon alongside its text. Click on the icon, label, or empty space
    // within the row all fire the same Click event via passthrough.
    public class SidebarButton : Panel
    {
        public NavIcon NavIconCtl;
        Label label;
        Color normalBack, normalFore, selectedBack, selectedFore;

        public SidebarButton(string text, NavGlyph glyph, Color normalBg, Color normalFg, Color selectedBg, Color selectedFg)
        {
            Height = 40;
            // No Dock here - this control lives inside navFlow, a
            // FlowLayoutPanel, which ignores Dock on its children entirely
            // and positions them via flow rules instead. Fixed Width (set
            // by the caller in AddSection) + Height here + the flow panel's
            // TopDown direction is what actually stacks these correctly.
            Cursor = Cursors.Hand;
            normalBack = normalBg; normalFore = normalFg;
            selectedBack = selectedBg; selectedFore = selectedFg;

            NavIconCtl = new NavIcon();
            NavIconCtl.Glyph = glyph;
            NavIconCtl.Location = new Point(18, 11);
            NavIconCtl.GlyphColor = normalFg;
            NavIconCtl.Cursor = Cursors.Hand;
            Controls.Add(NavIconCtl);

            label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Location = new Point(46, 10);
            label.Font = new Font("Segoe UI", 9.5F);
            label.ForeColor = normalFg;
            label.BackColor = Color.Transparent;
            label.Cursor = Cursors.Hand;
            Controls.Add(label);

            BackColor = normalBg;

            // Passthrough so clicking the icon or label triggers the same
            // handler as clicking empty space in the row.
            NavIconCtl.Click += delegate { OnClick(EventArgs.Empty); };
            label.Click += delegate { OnClick(EventArgs.Empty); };
        }

        public string LabelText { get { return label.Text; } }

        public void SetSelected(bool selected)
        {
            BackColor = selected ? selectedBack : normalBack;
            label.ForeColor = selected ? selectedFore : normalFore;
            NavIconCtl.GlyphColor = selected ? selectedFore : normalFore;
            NavIconCtl.Invalidate();
        }
    }

    public class NavIcon : Control
    {
        public NavGlyph Glyph;
        public Color GlyphColor = Color.Gainsboro;

        public NavIcon()
        {
            Width = 18;
            Height = 18;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(GlyphColor, 1.6f))
            using (SolidBrush fill = new SolidBrush(GlyphColor))
            {
                Rectangle r = new Rectangle(1, 1, Width - 2, Height - 2);
                switch (Glyph)
                {
                    case NavGlyph.Dashboard:
                        // House: triangle roof + square body
                        Point[] roof = new Point[] {
                            new Point(r.Left, r.Top + r.Height / 2),
                            new Point(r.Left + r.Width / 2, r.Top),
                            new Point(r.Right, r.Top + r.Height / 2)
                        };
                        e.Graphics.DrawLines(pen, roof);
                        e.Graphics.DrawRectangle(pen, r.Left + 3, r.Top + r.Height / 2, r.Width - 6, r.Height / 2 - 1);
                        break;
                    case NavGlyph.World:
                        // Globe: circle + horizontal + vertical curve lines
                        e.Graphics.DrawEllipse(pen, r);
                        e.Graphics.DrawLine(pen, r.Left, r.Top + r.Height / 2, r.Right, r.Top + r.Height / 2);
                        e.Graphics.DrawEllipse(pen, r.Left + r.Width / 4, r.Top, r.Width / 2, r.Height);
                        break;
                    case NavGlyph.Auth:
                        // Shield: pentagon-ish outline
                        Point[] shield = new Point[] {
                            new Point(r.Left, r.Top),
                            new Point(r.Right, r.Top),
                            new Point(r.Right, r.Top + r.Height * 2 / 3),
                            new Point(r.Left + r.Width / 2, r.Bottom),
                            new Point(r.Left, r.Top + r.Height * 2 / 3)
                        };
                        e.Graphics.DrawPolygon(pen, shield);
                        break;
                    case NavGlyph.Brain:
                        // Simple brain-ish: two overlapping circles
                        e.Graphics.DrawEllipse(pen, r.Left, r.Top + 2, r.Width * 2 / 3, r.Height - 4);
                        e.Graphics.DrawEllipse(pen, r.Left + r.Width / 3, r.Top + 2, r.Width * 2 / 3, r.Height - 4);
                        break;
                    case NavGlyph.Bots:
                        // Two small circles (heads) side by side
                        e.Graphics.FillEllipse(fill, r.Left, r.Top + 3, r.Width / 2 - 2, r.Width / 2 - 2);
                        e.Graphics.FillEllipse(fill, r.Left + r.Width / 2 + 1, r.Top + 3, r.Width / 2 - 2, r.Width / 2 - 2);
                        e.Graphics.DrawRectangle(pen, r.Left, r.Top + r.Height / 2, r.Width, r.Height / 2 - 2);
                        break;
                    case NavGlyph.Admin:
                        // Person: circle head + trapezoid body
                        e.Graphics.DrawEllipse(pen, r.Left + r.Width / 4, r.Top, r.Width / 2, r.Width / 2);
                        Point[] body = new Point[] {
                            new Point(r.Left + 2, r.Bottom),
                            new Point(r.Right - 2, r.Bottom),
                            new Point(r.Right - 4, r.Top + r.Height * 2 / 3),
                            new Point(r.Left + 4, r.Top + r.Height * 2 / 3)
                        };
                        e.Graphics.DrawPolygon(pen, body);
                        break;
                    case NavGlyph.Backup:
                        // Floppy disk: square with a notch
                        e.Graphics.DrawRectangle(pen, r);
                        e.Graphics.DrawRectangle(pen, r.Left + r.Width / 4, r.Top, r.Width / 2, r.Height / 3);
                        break;
                    case NavGlyph.Settings:
                        // Gear: circle with small tick marks
                        e.Graphics.DrawEllipse(pen, r.Left + 2, r.Top + 2, r.Width - 4, r.Height - 4);
                        e.Graphics.FillEllipse(fill, r.Left + r.Width / 2 - 2, r.Top + r.Height / 2 - 2, 4, 4);
                        for (int i = 0; i < 8; i++)
                        {
                            double ang = i * Math.PI / 4;
                            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
                            int x1 = cx + (int)(Math.Cos(ang) * (r.Width / 2 - 1));
                            int y1 = cy + (int)(Math.Sin(ang) * (r.Height / 2 - 1));
                            int x2 = cx + (int)(Math.Cos(ang) * (r.Width / 2 + 2));
                            int y2 = cy + (int)(Math.Sin(ang) * (r.Height / 2 + 2));
                            e.Graphics.DrawLine(pen, x1, y1, x2, y2);
                        }
                        break;
                    case NavGlyph.Logs:
                        // Document with lines
                        e.Graphics.DrawRectangle(pen, r);
                        for (int i = 0; i < 3; i++)
                        {
                            int ly = r.Top + 4 + i * 4;
                            e.Graphics.DrawLine(pen, r.Left + 3, ly, r.Right - 3, ly);
                        }
                        break;
                    case NavGlyph.About:
                        // Circle with an "i" - drawn as a dot + line, not text
                        e.Graphics.DrawEllipse(pen, r);
                        int cx2 = r.Left + r.Width / 2;
                        e.Graphics.FillEllipse(fill, cx2 - 1, r.Top + 3, 2, 2);
                        e.Graphics.DrawLine(pen, cx2, r.Top + 7, cx2, r.Bottom - 3);
                        break;
                }
            }
        }
    }
}
