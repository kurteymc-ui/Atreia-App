using System.IO;
using System.Runtime.InteropServices;
using AionDPS.Combat;
using AionDPS.Data;
using Velopack;

namespace AionDPS;

/// <summary>
/// Entry point: the meter window (no arguments), the self-check suite, and the Aion 2 tools
/// (recorder, replay, live and upload dry runs).
///
/// Everything the meter knows comes from Aion 2's network traffic, read passively through the Npcap
/// driver (see Aion2/). There is no access to the game process.
/// </summary>
internal static class Program
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>
    /// The meter window needs admin rights for the packet capture, and the manifest no longer
    /// demands them (see app.manifest): a plain start re-launches itself through the "runas" verb -
    /// the UAC prompt - and this instance ends. Only for a plain start: the command-line modes
    /// (selftest, recording, replays) run as they were started, and Velopack's hook runs never get
    /// here. Declining the prompt simply ends the meter, as the old manifest did.
    /// </summary>
    private static bool RelaunchElevatedIfNeeded(string[] args)
    {
        if (args.Length > 0)
        {
            return false;
        }

        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            if (new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                return false;
            }
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC prompt declined: nothing to start.
        }

        return true;
    }

    [STAThread] // required for WPF (Ui/MainWindow) -- Clipboard, drag-move etc. need the STA apartment.
    private static void Main(string[] args)
    {
        // The csproj builds this as WinExe (no automatic console), specifically so the GUI doesn't
        // pop up an empty terminal window next to the meter - found by the user. The CLI modes
        // (selftest/chatlog) still need visible Console.WriteLine output when launched from an
        // existing shell, so attach to whichever console started this process, if any; a no-op
        // when there is none.
        //
        // No arguments opens the GUI, always -- per the user: the installed exe should show the
        // meter with no parameters, and the CLI is what needs one. The rule therefore does not
        // depend on how the process was started: the installer's shortcuts (Velopack creates them
        // with no arguments), a double-click, and "Aion DPS" typed in a shell all open the
        // window.
        AttachConsole(AttachParentProcess);

        // Must run before anything else, and before any window exists: this is what handles
        // Velopack's own install/update/uninstall hook arguments, which the updater passes to a
        // freshly-swapped build. Getting a meter window on screen in those runs instead of doing
        // the hook's job is exactly how a self-updating app breaks its own update.
        //
        // No manual shortcut handling here on purpose: Velopack.Windows.Shortcuts is explicitly
        // marked obsolete ("Desktop and StartMenuRoot shortcuts are now created and removed
        // automatically when your app is installed / uninstalled"), and upstream issue #67
        // ("Shortcuts not updated when --packTitle or application icon changes", fixed by PR
        // #165) covers exactly the symptom the user reported (Start Menu still showing the old
        // app icon after an in-place update) -- already handled by Velopack itself on the 1.2.0
        // this project packs with (see AionDpsMeter's release.yml). If it recurs, suspect Windows'
        // own shell icon cache (a stale bitmap cached against the unchanged .lnk file) or a
        // locally installed build old enough to predate that upstream fix, not a gap here.
        // A downloaded update is not put in by itself at the next start (7 November 2026, the owner: an update goes to the newest version, the ones between
        // are skipped): it may be one or two versions old by then, and the newest would come as a second update right after. The start-up check offers the
        // newest instead, with the notes of every version missed (MainWindow.RunUpdateCheck, MainWindow.UpdateNotes.cs).
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        if (RelaunchElevatedIfNeeded(args))
        {
            return;
        }

        if (args.Length > 0 && args[0] == "selftest")
        {
            bool ok = SelfCheck.Run();
            Console.WriteLine(ok ? "\n[selftest] ALL CHECKS PASSED" : "\n[selftest] SOME CHECKS FAILED");
            Environment.Exit(ok ? 0 : 1);
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-record")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: AionDPS aion2-record <out.jsonl> [port[,port...]]");
                Console.WriteLine("  Records the raw TCP payloads exchanged with the Aion 2 game server (Npcap,");
                Console.WriteLine("  passive) into a JSON-lines file for protocol calibration - see");
                Console.WriteLine("  assets/aion2/protocol/opcodes.json. Without ports, every TCP stream is");
                Console.WriteLine("  recorded; type stop + Enter to end.");
                return;
            }

            // "--points": the recorder with debug points (see RunAion2RecordMode)
            string[] positional = args.Skip(1).Where(a => a != "--points").ToArray();
            RunAion2RecordMode(positional[0], positional.Length > 1 ? positional[1] : null, args.Contains("--points"));
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-opcode-census")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: AionDPS aion2-opcode-census <recording.jsonl>... [--port=13328]");
                Console.WriteLine("  Replays recordings and lists the opcodes of the frames that the project neither documents nor decodes (what the recorder's debug points would beep for). No window.");
                return;
            }

            RunAion2OpcodeCensus(args.Skip(1).Where(a => !a.StartsWith("--port=", StringComparison.Ordinal)).ToArray(),
                args.Where(a => a.StartsWith("--port=", StringComparison.Ordinal)).Select(a => int.Parse(a[7..])).DefaultIfEmpty(13328).First());
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-view-check")
        {
            // aion2-view-check <recording.jsonl>...: the list's views from a real recording, the meter's way against a full rescan
            ViewCheck.Run(args);
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-session-sim")
        {
            // aion2-session-sim <recording.jsonl>: stay, change the map, fight, change back - what the list and the session menu show at each step
            SessionSim.Run(args);
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-profile-upload")
        {
            // aion2-profile-upload <recording> <data-folder copy> <names> [--send]: puts the profile of looked-at players online after the fact
            ProfileUploadTool.Run(args);
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-stress")
        {
            // aion2-stress [duel|raid] [stages, comma separated]: made-up game traffic through the real decoder and window logic
            StressTest.Run(args);
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-ui-test")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: AionDPS aion2-ui-test <recording.jsonl> [server-port]");
                Console.WriteLine("  Runs the REAL meter window logic (never shown) on a recording: plays part of it, presses");
                Console.WriteLine("  Clear (the red X), plays on, and prints how many rows the list has at each stage.");
                return;
            }

            RunAion2UiTestMode(args[1], args.Length > 2 ? int.Parse(args[2]) : 13328);
            return;
        }

        if (args.Length > 0 && args[0] == "render-bossoverlay")
        {
            RunRenderBossOverlayMode(args.Length > 1 ? args[1] : "bossoverlay.png", args.Length > 2 ? int.Parse(args[2]) : 0);
            return;
        }

        if (args.Length > 1 && args[0] == "render-themeeditor")
        {
            RunRenderThemeEditorMode(args[1]);
            return;
        }

        if (args.Length > 1 && args[0] == "render-assist")
        {
            RunRenderAssistMode(args[1], args.Length > 2 ? args[2] : "");
            return;
        }

        if (args.Length > 1 && args[0] == "render-checklist")
        {
            // render-checklist <out.png> [language | roster | character | character-season | character-supply | character-inventory]: the page of all characters
            // (roster) or of one (character: its counters; or one other part of its page)
            string third = args.Length > 2 ? args[2] : "";
            bool page = third is "roster" || third.StartsWith("character", StringComparison.Ordinal);
            RunRenderChecklistMode(args[1], page ? "" : third, page ? third : "");
            return;
        }

        if (args.Length > 1 && args[0] == "render-season")
        {
            RunRenderSeasonMode(args[1]);
            return;
        }

        if (args.Length > 1 && args[0] == "render-gather")
        {
            RunRenderGatherOverlayMode(args[1]);
            return;
        }

        if (args.Length > 1 && args[0] == "render-loot")
        {
            RunRenderLootMode(args[1], args.Length > 2 ? args[2] : "");
            return;
        }

        if (args.Length > 1 && args[0] == "render-cpcalc")
        {
            RunRenderCpCalcMode(args[1], args.Length > 2 ? args[2] : "", args.Length > 3 ? args[3] : "gear");
            return;
        }

        if (args.Length > 1 && args[0] == "render-statcalc")
        {
            RunRenderStatCalcMode(args[1], args.Length > 2 && args[2] == "expert", args.Length > 3 ? args[3] : "");
            return;
        }

        if (args.Length > 1 && args[0] == "render-attributes")
        {
            RunRenderAttributesMode(args[1], args.Length > 2 && args[2] == "stats", args.Length > 3 ? args[3] : "");
            return;
        }

        if (args.Length > 0 && args[0] == "render-network")
        {
            RunRenderNetworkMode(args.Length > 1 ? args[1] : "network.png", args.Length > 2 ? int.Parse(args[2]) : 2);
            return;
        }

        if (args.Length > 0 && args[0] == "render-combatoverlay")
        {
            RunRenderCombatOverlayMode(args.Length > 1 ? args[1] : "combatoverlay.png", args.Length > 2 ? args[2] : "live", args.Length > 3 ? int.Parse(args[3]) : 2);
            return;
        }

        if (args.Length > 0 && args[0] == "render-replay")
        {
            RunRenderReplayMode(args.Length > 1 ? args[1] : "replay.png", args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 14.0, args.Length > 3 ? int.Parse(args[3]) : 1);
            return;
        }

        if (args.Length > 0 && args[0] == "render-tool")
        {
            Ui.ToolRender.Run(args.Length > 1 ? args[1] : "tool.png", args.Length > 2 ? args[2] : "tools");
            return;
        }

        if (args.Length > 0 && args[0] == "render-privacy")
        {
            Ui.PrivacyRender.Run(args.Length > 1 ? args[1] : "privacy.png", args.Length > 2 ? args[2] : "dialog");
            return;
        }

        if (args.Length > 0 && args[0] == "render-app")
        {
            // an optional fourth argument makes the picture taller (a long page drawn whole, the Timers page)
            RunRenderAppMode(args.Length > 1 ? args[1] : "app.png", args.Length > 2 ? args[2] : "home",
                args.Length > 3 && int.TryParse(args[3], out int appHeight) ? appHeight : 760);
            return;
        }

        if (args.Length > 0 && args[0] == "render-history")
        {
            RunRenderHistoryMode(args.Length > 1 ? args[1] : "history.png", args.Length > 2 ? args[2] : "Boss fights");
            return;
        }

        if (args.Length > 0 && args[0] == "render-report")
        {
            RunRenderReportMode(args.Length > 1 ? args[1] : "report.png", args.Length > 2 ? args[2] : "Damage");
            return;
        }

        if (args.Length > 0 && args[0] == "render-messagebox")
        {
            RunRenderMessageBoxMode(args.Length > 1 ? args[1] : "messagebox.png");
            return;
        }

        if (args.Length > 0 && args[0] == "render-settings")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: AionDPS render-settings <out.png> [Interface|Capture|Hotkeys|Timetable|Updates]");
                Console.WriteLine("  Draws the Settings window's content into a picture; no window is ever shown.");
                return;
            }

            RunRenderSettingsMode(args[1], args.Length > 2 ? args[2] : "Interface");
            return;
        }

        if (args.Length > 0 && args[0] == "render-overlay")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: AionDPS render-overlay <out.png> [compact|chips|window]");
                Console.WriteLine("  Draws the compact overlay panel, the chips overlay or the normal window with sample rows into a picture; no window is shown.");
                return;
            }

            RunRenderOverlayMode(args[1], args.Length > 2 ? args[2] : "compact");
            return;
        }

        if (args.Length > 0 && args[0] == "render-character")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: AionDPS render-character <out.png> [character|equipment|arcana|skills|board|species]");
                Console.WriteLine("  Draws the saved character's profile window into a picture; no window is ever shown.");
                return;
            }

            RunRenderCharacterMode(args[1], args.Length > 2 ? args[2] : "equipment");
            return;
        }

        if (args.Length > 0 && args[0] == "render-sessions")
        {
            RunRenderSessionsMode(args.Length > 1 ? args[1] : "sessions.png", args.Length > 2 ? int.Parse(args[2]) : 1, args.Length > 3 ? args[3] : null);
            return;
        }

        if (args.Length > 0 && args[0] == "render-details")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: AionDPS render-details <out.png> [skills|rotation|buffs]");
                Console.WriteLine("  Draws a player's details window with sample hits into a picture; no window is shown.");
                return;
            }

            RunRenderDetailsMode(args[1], args.Length > 2 ? args[2] : "skills");
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-upload-dryrun")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: AionDPS aion2-upload-dryrun <recording.jsonl> [server-name] [server-port] [--send Boss@HH:mm ...]");
                Console.WriteLine("  Plays a recording through the meter's own window logic (no window is shown), then builds the");
                Console.WriteLine("  upload for every boss it recognised and prints it. Nothing is sent - unless --send names bosses");
                Console.WriteLine("  by name and UTC start minute (Thamon@15:14): exactly those fights are uploaded, to catch up on");
                Console.WriteLine("  runs that never went online.");
                return;
            }

            int sendAt = Array.IndexOf(args, "--send");
            string[] positional = sendAt >= 0 ? args[..sendAt] : args;
            RunAion2UploadDryRun(args[1], positional.Length > 2 ? positional[2] : "Europe - Kaisinel", positional.Length > 3 ? int.Parse(positional[3]) : 13328,
                sendAt >= 0 ? args[(sendAt + 1)..] : Array.Empty<string>());
            return;
        }

        if (args.Length > 1 && args[0] == "aion2-preset-check")
        {
            RunAion2PresetCheck(args[1], args.Length > 2 ? int.Parse(args[2]) : 13328);
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-ui-live")
        {
            RunAion2UiLiveMode(args.Length > 1 ? int.Parse(args[1]) : 70);
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-live")
        {
            RunAion2LiveMode(args.Length > 1 ? int.Parse(args[1]) : 20);
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-skill-averages")
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: AionDPS aion2-skill-averages <out.json> <recording.jsonl> [more recordings...] [--port N]");
                Console.WriteLine("  Replays the recordings and writes the average hit of every skill (players' hits on monsters) to the table");
                Console.WriteLine("  the world boss estimate reads: assets/aion2/protocol/skill_hit_average.json. No game, no network, no window.");
                return;
            }

            RunAion2SkillAverages(args);
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-buff-selftest")
        {
            // only the buff-upload check, no window (the full selftest builds overlays)
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            bool buffOk = Combat.SelfCheckBuffUpload.Run();
            Console.WriteLine(buffOk ? "[selftest] BUFF CHECKS PASSED" : "[selftest] BUFF CHECKS FAILED");
            Environment.Exit(buffOk ? 0 : 1);
            return;
        }

        if (args.Length > 2 && args[0] == "aion2-buff-dump")
        {
            Combat.BuffDump.Run(args[1], args[2], 13328, args.Length > 3 ? args[3] : null);
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-replay")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: AionDPS aion2-replay <recording.jsonl> [server-port] [own-character-name]");
                Console.WriteLine("  Replays an aion2-record file through the same reassembly and decoder the live meter");
                Console.WriteLine("  uses and prints damage/heal per actor and per skill. No game, no network, no window.");
                return;
            }

            RunAion2ReplayMode(args[1], args.Length > 2 ? int.Parse(args[2]) : 13328, args.Length > 3 ? args[3] : null);
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-frame-dump")
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: AionDPS aion2-frame-dump <recording.jsonl> <opcode hex, e.g. 058a> [max frames] [--port=13328]");
                Console.WriteLine("  Prints the frames of one opcode of a recording as hex (opcode as the decoder reads it). No window.");
                return;
            }

            int max = args.Skip(3).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Select(int.Parse).DefaultIfEmpty(5).First();
            int port = args.Where(a => a.StartsWith("--port=", StringComparison.Ordinal)).Select(a => int.Parse(a[7..])).DefaultIfEmpty(13328).First();
            RunAion2FrameDump(args[1], Convert.ToInt32(args[2], 16), max, port);
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-position-check")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: AionDPS aion2-position-check <recording.jsonl>... [--port=13328]");
                Console.WriteLine("  Replays recordings and, at every exact own position, compares the previous exact place and the middle of the entities around with it. No window.");
                return;
            }

            RunAion2PositionCheckMode(args.Skip(1).Where(a => !a.StartsWith("--port=", StringComparison.Ordinal)).ToArray(),
                args.Where(a => a.StartsWith("--port=", StringComparison.Ordinal)).Select(a => int.Parse(a[7..])).DefaultIfEmpty(13328).First());
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-cube-check")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: AionDPS aion2-cube-check <recording.jsonl>... [--port=13328]");
                Console.WriteLine("  Replays recordings and, for every Hidden Cube, tells how long it was in sight and for how many of those seconds the own position was current (what the approach sound needs). No window.");
                return;
            }

            RunAion2CubeCheckMode(args.Skip(1).Where(a => !a.StartsWith("--port=", StringComparison.Ordinal)).ToArray(),
                args.Where(a => a.StartsWith("--port=", StringComparison.Ordinal)).Select(a => int.Parse(a[7..])).DefaultIfEmpty(13328).First());
            return;
        }

        if (args.Length > 0 && args[0] == "aion2-stock-check")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: AionDPS aion2-stock-check <recording.jsonl> [server-port]");
                Console.WriteLine("  Replays a recording through the decoder and prints what the crafting tool would count: inventory, character storage, account storage. No window.");
                return;
            }

            RunAion2StockCheckMode(args[1], args.Length > 2 ? int.Parse(args[2]) : 13328);
            return;
        }

        if (args.Length > 0 && args[0] == "render-craft")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: AionDPS render-craft <out.png> [recording.jsonl] [language]");
                Console.WriteLine("  Draws the crafting tool (page, overlay, configuration) with a made-up plan and the stock of a recording into one picture. No window is shown; set AIONDPS_DATA_DIR to a throw-away folder.");
                return;
            }

            RunRenderCraftMode(args[1], args.Length > 2 ? args[2] : "", args.Length > 3 ? args[3] : "");
            return;
        }

        if (args.Length > 0 && args[0] == "settings-dirty-check")
        {
            RunSettingsDirtyCheckMode();
            return;
        }

        if (args.Length > 0 && args[0] == "render-petmap")
        {
            RunRenderPetMapMode(args.Length > 1 ? args[1] : "petmap.png", args.Length > 2 ? args[2] : "verteron", args.Length > 4 ? double.Parse(args[3]) : -215789, args.Length > 4 ? double.Parse(args[4]) : 262953,
                args.Length > 5 && args[5] == "guards", args.Length > 5 && args[5].StartsWith("route", StringComparison.Ordinal) ? args[5] : null);
            return;
        }

        if (args.Length > 0 && args[0] == "render-petfarm")
        {
            RunRenderPetFarmMode(args.Length > 1 ? args[1] : "petfarm.png");
            return;
        }

        if (args.Length > 0 && args[0] == "render-player")
        {
            // render-player <out.png>: the player window (Find a player → Open player page), made-up character: overview, gear, Genus and pets, and a player without the app
            RunRenderPlayerMode(args.Length > 1 ? args[1] : "player.png");
            return;
        }

        if (args.Length > 0 && args[0] == "render-petlist-shop")
        {
            RunRenderPetListShopMode(args.Length > 1 ? args[1] : "petlist-shop.png");
            return;
        }

        if (args.Length == 0 || args[0] == "gui")
        {
            // A second meter would capture the same traffic twice: bring the running one forward instead.
            if (!SingleInstance.TryBecomeFirst())
            {
                return;
            }

            // No App.xaml on purpose: an ApplicationDefinition item would generate its own Main
            // and collide with this one. Building System.Windows.Application by hand keeps the
            // console entry points and the GUI in the same exe without fighting over program entry.
            var app = new System.Windows.Application();
            bool started = false; // the window was handed to the app: an error after this is no failed start
            CrashLog.Hook(app); // crash.log in the data folder: what an unhandled error was, for the next report
            // A screen reader, an overlay or a capture tool that reads the window through UI Automation
            // can ask a list item for its name just after the row behind it was replaced; WPF's own
            // ItemAutomationPeer.GetNameCore then throws a NullReferenceException from inside layout
            // (seen with v0.10.5: the meter closed with "konnte nicht starten"). It only concerns what
            // the accessibility tree reports, so it is skipped instead of ending the meter.
            app.DispatcherUnhandledException += (_, e) =>
            {
                if (e.Exception is NullReferenceException && e.Exception.StackTrace?.Contains("AutomationPeer", StringComparison.Ordinal) == true)
                {
                    e.Handled = true;
                }
            };
            try
            {
                // Before the first window exists: every window's brushes resolve through the
                // theme dictionary this merges in (see Ui/ThemeManager). Shared.xaml is
                // theme-independent (DynamicResource brush refs only) and merged once here so any
                // window can reference its styles (e.g. ShareBar) via StaticResource without
                // redefining them.
                app.Resources.MergedDictionaries.Add(
                    (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
                Ui.MeterSettings startupSettings = Ui.MeterSettings.Load();
                Ui.AudioOutput.DeviceId = startupSettings.AudioDeviceId;
                Ui.ThemeManager.Apply(app, startupSettings);
                // Every window fits on the screen it opens on, from 1280 x 720 to 4K and ultrawide, and again when the screens change (Ui/ScreenFit).
                Ui.ScreenFit.Install(app.Dispatcher);
                var mainWindow = new Ui.MainWindow();
                // The install's move to %LOCALAPPDATA%\AtreiaApp (Update/InstallMove): the old folder's copy fetches the new installer once its window is
                // up; the new install removes what is left of the old one.
                mainWindow.Loaded += async (_, _) =>
                {
                    try
                    {
                        await AionDPS.Update.InstallMove.OfferAsync(mainWindow);
                    }
                    catch (Exception)
                    {
                        // tried again at the next start
                    }
                };
                _ = Task.Run(AionDPS.Update.InstallMove.CleanUpOldInstall);
                // Closing the meter's window ends the meter, even while an overlay or another window of it is still open; otherwise it kept running
                // without a window, and starting it again found that hidden copy instead of opening.
                app.ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
                SingleInstance.ListenForShow(mainWindow);
                started = true;
                app.Run(mainWindow);
                AionDPS.Update.InstallMove.RunIfReady();
            }
            catch (Exception ex)
            {
                // Without a console there is nowhere for an unhandled startup exception to show
                // up, so the failure looks like nothing happening at all -- put it on screen
                // instead of letting the process die silently.
                CrashLog.Write(started ? "the meter stopped with an error" : "the meter could not start", ex);
                System.Windows.MessageBox.Show(
                    (started ? $"{Brand.AppName} stopped because of an error." : $"{Brand.AppName} could not start.")
                    + " Start it again; if this keeps happening, send the text below (also saved in crash.log in %APPDATA%\\" + Brand.DataFolder + ").\n\n" + ex,
                    started ? $"{Brand.AppName} stopped" : $"{Brand.AppName} could not start",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }

            return;
        }

        // Anything else is a typo far more often than it is an attempt at something real, so say
        // what the program accepts rather than failing silently or opening the window anyway.
        Console.WriteLine("Usage: AionDPS                       (no arguments: opens the meter window)");
        Console.WriteLine("       AionDPS selftest                     (runs the self-checks)");
        Console.WriteLine("       AionDPS aion2-record <out.jsonl> [ports]  (records Aion 2 game traffic for protocol calibration)");
    }

    /// <summary>
    /// Calibration recorder for the Aion 2 packet source (see Aion2/Protocol/Aion2Protocol.cs):
    /// captures the game-server TCP stream passively and writes it as JSON lines. The first real
    /// fight recorded this way is what turns opcodes.json from a template into a working layout,
    /// and then becomes a replayable SelfCheck fixture. Ports come from the argument, else from
    /// the shipped protocol file, else everything TCP is recorded.
    /// </summary>
    private static void RunAion2RecordMode(string outPath, string? portList, bool points = false)
    {
        var npcap = Aion2.Capture.NpcapAvailability.Detect();
        if (!npcap.IsInstalled)
        {
            Console.WriteLine($"aion2-record: Npcap is not installed - get it from {Aion2.Capture.NpcapAvailability.DownloadUrl}");
            Environment.Exit(1);
            return;
        }

        // "net:193.202.112.0/24" records every TCP port of that network instead of the game's ports.
        string? network = portList is not null && portList.StartsWith("net:", StringComparison.Ordinal) ? portList[4..] : null;
        List<int> ports = network is not null
            ? new List<int>()
            : portList is null
                ? Aion2.Protocol.Aion2Protocol.Load().ServerPorts.ToList()
                : portList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

        // Calibration defaults to every adapter (the game might run through a VPN adapter);
        // an explicit choice in Settings narrows it.
        string adapterId = Ui.MeterSettings.Load().CaptureAdapterId is { Length: > 0 } chosen ? chosen : Aion2.Capture.CaptureAdapters.AllAdapters;
        foreach (var adapter in Aion2.Capture.CaptureAdapters.List())
        {
            Console.WriteLine($"aion2-record: adapter {adapter.Label}{(adapter.Id == adapterId ? " <- selected" : "")}");
        }

        Aion2.Capture.SegmentRecording.Writer writerOrNull;
        try
        {
            writerOrNull = new Aion2.Capture.SegmentRecording.Writer(outPath);
        }
        catch (IOException ex)
        {
            // Usually a second recorder window (or one started in the same minute) already holds the file.
            Console.WriteLine($"aion2-record: cannot open {outPath}: {ex.Message}");
            Console.WriteLine("aion2-record: another recording is probably still running - end it first (type  stop  in its window) or wait a minute.");
            Environment.Exit(1);
            return;
        }

        using var writer = writerOrNull;
        // With debug points the frames are decoded while they are recorded: one of an opcode nobody has described gives a tone and a line in <recording>.points.jsonl
        Aion2.Capture.DebugPointLog? pointLog = points ? new Aion2.Capture.DebugPointLog(outPath) : null;
        Aion2.Aion2PacketCombatSource? watchSource = points ? new Aion2.Aion2PacketCombatSource(Aion2.Protocol.Aion2Protocol.Load()) : null;
        int gamePort = ports.Count > 0 ? ports[0] : 13328;
        DateTime lastBeep = DateTime.MinValue;
        DateTime lastPoint = DateTime.MinValue;
        DateTime recordingStart = DateTime.UtcNow;
        // the login sends a burst of frames nobody described: they are logged but give no tone for the first minute
        TimeSpan loginBurst = TimeSpan.FromSeconds(60);
        if (points)
        {
            var watch = new Aion2.Capture.UnknownFrameWatch(Aion2.Capture.UnknownFrameWatch.LoadKnown());
            Aion2.Protocol.Aion2FrameDecoder.FrameTap = (opcode, frame, at) =>
            {
                if (watch.Note(opcode, frame, at) is { } point)
                {
                    pointLog!.Point(point);
                    lastPoint = point.At;
                    bool loginPhase = DateTime.UtcNow - recordingStart < loginBurst;
                    Console.WriteLine($"aion2-record: DEBUG POINT {point.At:HH:mm:ss}  unknown frame 0x{point.Opcode:x4} ({point.Length} bytes, seen {point.Seen}x){(loginPhase ? " (first minute: no tone)" : " - tell me what you just did")}");
                    if (!loginPhase && DateTime.UtcNow - lastBeep > TimeSpan.FromSeconds(2))
                    {
                        lastBeep = DateTime.UtcNow;
                        ThreadPool.QueueUserWorkItem(_ => { try { Console.Beep(1100, 160); Console.Beep(1500, 220); } catch (Exception) { /* no sound device: the line above still says it */ } });
                    }
                }
            };
        }

        using var capture = new Aion2.Capture.NpcapCaptureService(
            ports,
            segment =>
            {
                writer.Write(segment);
                if (watchSource is not null)
                {
                    bool fromServer = segment.Source.EndsWith(":" + gamePort, StringComparison.Ordinal);
                    watchSource.Ingest(segment with { FromServer = fromServer });
                }
            },
            (state, message) => Console.WriteLine($"aion2-record: [{state}] {message}"),
            adapterId) { Network = network };

        capture.Start();
        Console.WriteLine($"aion2-record: writing to {outPath} (filter \"{capture.Filter}\").");
        if (points)
        {
            Console.WriteLine($"aion2-record: DEBUG POINTS on - a frame nobody has described gives two tones and a line in {pointLog!.Path}. Then tell me (in chat) what you did; the time is on the line.");
            Console.WriteLine("aion2-record: you may also type a short note here and press Enter, it is saved with its time next to the point (alt-tab away from the game for it).");
        }

        Console.WriteLine("aion2-record: to END the recording type  stop  and press Enter (closing this window also ends it).");
        Console.WriteLine("aion2-record: a plain Enter - for example one meant for the game's chat - does NOT stop it.");

        // A bare ReadLine() returned on any Enter and on end-of-input, which ended recordings by
        // accident (one stopped 13 minutes before the run it was meant for). Now only the word "stop"
        // ends it, and a closed input stream just keeps recording. A line every minute shows it lives.
        using var stopRequested = new ManualResetEventSlim();
        var reader = new Thread(() =>
        {
            while (Console.ReadLine() is { } line)
            {
                if (line.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    stopRequested.Set();
                    return;
                }

                if (pointLog is not null && line.Trim().Length > 0)
                {
                    pointLog.Note(DateTime.Now, line.Trim());
                    Console.WriteLine($"aion2-record: note saved ({(DateTime.Now - lastPoint).TotalSeconds:0} s after the last debug point).");
                    continue;
                }

                Console.WriteLine("aion2-record: still recording - type  stop  to end it.");
            }
        }) { IsBackground = true };
        reader.Start();
        while (!stopRequested.Wait(watchSource is null ? TimeSpan.FromSeconds(60) : TimeSpan.FromMilliseconds(250)))
        {
            // the frames are decoded as they come: the poll drives the decoder (the damage events are not used here)
            watchSource?.Poll(false);
            if (DateTime.UtcNow.Second < 1 && (DateTime.UtcNow - lastBeep).TotalSeconds > 5)
            {
                Console.WriteLine($"aion2-record: recording ... {writer.Count} segment(s), {pointLog?.Count ?? 0} debug point(s) so far ({DateTime.Now:HH:mm:ss}).");
                lastBeep = DateTime.UtcNow;
            }
        }

        Aion2.Protocol.Aion2FrameDecoder.FrameTap = null;
        pointLog?.Dispose();
        watchSource?.Dispose();

        Console.WriteLine($"aion2-record: {writer.Count} segment(s) from {capture.Packets} packet(s) written; server endpoint {capture.ServerEndpoint ?? "not seen"}.");
    }

    /// <summary>
    /// Runs the meter's real live path - the same Aion2PacketCombatSource with the same Settings
    /// (adapter, own character) - headless for a few seconds and prints what it sees: status text,
    /// frames, events and the top players. The fastest way to tell "capture sees nothing" from
    /// "decoder sees nothing" without a window.
    /// </summary>
    private static void RunAion2LiveMode(int seconds)
    {
        var settings = Ui.MeterSettings.Load();
        var protocol = Aion2.Protocol.Aion2Protocol.Load();
        using var source = new Aion2.Aion2PacketCombatSource(protocol, settings.CaptureAdapterId);
        source.StatusChanged += status => Console.WriteLine($"aion2-live: [{status.State}] {status.Message}");
        Console.WriteLine($"aion2-live: adapter setting \"{settings.CaptureAdapterId ?? "(automatic)"}\", calibrated={protocol.IsCalibrated}, ports {string.Join(",", protocol.ServerPorts)}");
        source.Start();
        var events = new List<Combat.DamageEvent>();
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(1000);
            events.AddRange(source.Poll(false).Damage);
        }

        Console.WriteLine($"aion2-live: {events.Count} damage/heal event(s) in {seconds}s");
        foreach (var actor in events.Where(e => !e.IsHeal).GroupBy(e => e.SourceObjectId).OrderByDescending(g => g.Sum(e => e.Amount)).Take(6))
        {
            Console.WriteLine($"  {source.Entities.NameFor(actor.Key) ?? actor.Key.ToString(),-22} damage {actor.Sum(e => e.Amount),9:N0}  hits {actor.Count(),4}");
        }
    }

    /// <summary>Renders a player's details window with generated hits to a PNG, never showing a window.</summary>
    private static void RunRenderCharacterMode(string path, string tab)
    {
        var app = new System.Windows.Application();
        var settings = Ui.MeterSettings.Load();
        if (Environment.GetEnvironmentVariable("AIONDPS_LANG") is { Length: > 0 } picked)
        {
            Ui.LocalizationManager.Instance.Language = picked;
        }
        else if (settings.Language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = settings.Language; // skill and item names in the chosen language
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        if (Aion2.Aion2CharacterStore.Load(Aion2.Aion2CharacterStore.DefaultPath) is not { } saved)
        {
            Console.WriteLine("render-character: no saved character");
            Environment.Exit(1);
            return;
        }

        var directory = new Aion2.Aion2EntityDirectory();
        directory.RestoreFrom(saved);
        var window = new Ui.CharacterWindow(directory);
        window.ShowTab(tab);
        // AIONDPS_OTHER = "name|class code|level|item level|server id|legion" shows another character of the account instead; AIONDPS_OTHER_JSON is the file with
        // the server's answer for it (GET /api/players/:id), none = the character the server does not know.
        if (Environment.GetEnvironmentVariable("AIONDPS_OTHER") is { Length: > 0 } other)
        {
            string[] f = other.Split('|');
            var listed = new Aion2.AccountCharacter(f[0], int.Parse(f[1]), 1, int.Parse(f[2]), int.Parse(f[3]), f.Length > 5 && f[5].Length > 0 ? f[5] : null, 0, 0, int.Parse(f[4]), 0);
            Aion2.Aion2Tickets.NoteListedCharacters(null, new[] { listed });
            Aion2.Aion2Tickets.NoteListedCharacters(null, new[] { new Aion2.AccountCharacter(saved.Name, saved.ClassCode, 1, saved.Level, 0, null, 0, 0, saved.ServerId, 0) });
            Upload.RemoteCharacter? remote = null;
            if (Environment.GetEnvironmentVariable("AIONDPS_OTHER_JSON") is { Length: > 0 } json && File.Exists(json))
            {
                using System.Text.Json.JsonDocument answer = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
                remote = Upload.RemoteCharacterClient.Parse(answer.RootElement);
            }

            window.ShowOtherForPicture(f[0], remote);
        }

        const double width = 1180, height = 1000;
        // a Window cannot be the child of a border: its content (the page inside the window's chrome) is drawn instead
        System.Windows.UIElement? inner = window.Content as System.Windows.UIElement;
        window.Content = null;
        var content = new System.Windows.Controls.Border { Child = inner };
        content.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0E, 0x18, 0x20));
        content.Measure(new System.Windows.Size(width, height));
        content.Arrange(new System.Windows.Rect(0, 0, width, height));
        content.UpdateLayout();
        content.UpdateLayout();
        var bitmap = Shot((int)width, (int)height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var backdrop = new System.Windows.Controls.Border { Width = width, Height = height, Background = (System.Windows.Media.Brush)app.FindResource("Brush.Window") };
        backdrop.Measure(new System.Windows.Size(width, height));
        backdrop.Arrange(new System.Windows.Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine("render-character: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>Draws the session manager with a day of invented stays into a picture; no window is shown.</summary>
    private static void RunRenderSessionsMode(string path, int args_selected = 1, string? analyzerTab = null)
    {
        var app = new System.Windows.Application();
        var settings = Ui.MeterSettings.Load();
        if (settings.Language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = settings.Language;
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));

        var recorder = new History.SessionRecorder(zone => Aion2.Protocol.Aion2Zones.IsInstance(zone) ? History.SessionKind.Instance : History.SessionKind.Map);
        var names = new Dictionary<int, (string Name, string Class)> { [1] = ("Corwyn", "Sorcerer"), [2] = ("Thorvald", "Gladiator"), [3] = ("Elyndra", "Cleric"), [4] = ("Nunzio", "Assassin") };
        recorder.Identify = id => names.TryGetValue(id, out var n) ? (n.Name, n.Class) : (id == 100 ? "Ruthilis of Pain" : null, null);
        var random = new Random(3);
        DateTime day = DateTime.Today.AddHours(7);
        (int Zone, int Minutes, int Hits)[] stays = { (1010, 120, 300), (600001, 25, 900), (20, 150, 500), (1010, 8, 0), (600092, 31, 1100), (1010, 40, 120) };
        DateTime at = day;
        int stayNo = 0;
        foreach ((int zone, int minutes, int hits) in stays)
        {
            recorder.Enter(new Combat.Sources.ZoneChange(at, zone));
            var batch = new List<Combat.DamageEvent>();
            for (int i = 0; i < hits; i++)
            {
                batch.Add(new Combat.DamageEvent(at.AddSeconds(minutes * 60.0 * i / Math.Max(1, hits)), random.Next(1, 5), 100, random.Next(800, 9000) / (random.Next(1, 5)), false, "Hit"));
            }

            // The instances have a boss: it hits the group every two seconds and goes into a rage 55 % of the way in.
            if (zone is 600001 or 600092)
            {
                for (int i = 0; i < minutes * 30; i++)
                {
                    bool raging = i > minutes * 30 * 0.55;
                    batch.Add(new Combat.DamageEvent(at.AddSeconds(i * 2), 100, random.Next(1, 5), (raging ? 3600 : 1200) + random.Next(-150, 150), false, "Smash"));
                }
            }

            batch = batch.OrderBy(e => e.Timestamp).ToList();
            recorder.Feed(Combat.Sources.CombatBatch.DamageOnly(batch));
            if (zone is 600001 or 600092)
            {
                // What the server said the boss lost: the second instance's totals are twice what it really lost.
                long total = batch.Where(e => e.TargetObjectId == 100).Sum(e => e.Amount);
                bool honest = zone == 600001;
                long hp = total + 4000, max = hp;
                var samples = new List<Aion2.HpSample> { new(at.AddSeconds(-1), hp) };
                foreach (Combat.DamageEvent e in batch.Where(e => e.TargetObjectId == 100))
                {
                    hp = Math.Max(0, hp - (honest ? e.Amount : e.Amount / 2));
                    samples.Add(new Aion2.HpSample(e.Timestamp, hp));
                }

                recorder.Sessions[^1].Hp[100] = new History.TargetHp(max, samples, Array.Empty<DateTime>());
            }

            stayNo++;
            at = at.AddMinutes(minutes);
        }

        recorder.Sessions[1].IsSaved = true;
        var window = new Ui.SessionManagerWindow(recorder);
        window.AnalyzerFactory = (session, row, mode, boss) => analyzerTab is not null ? CreateSampleDetails(analyzerTab, row.Name, row.ClassName) : new Ui.PlayerDetailsView(row.Name, row.ClassName, "",
            session.Data.Events.Where(ev => ev.SourceObjectId == row.Id).ToList(), id => session.Names.GetValueOrDefault(id),
            fightEvents: session.Data.Events.Where(ev => !ev.IsHeal).ToList());
        const double width = 1760, height = 820;
        var content = (System.Windows.UIElement)window.Content;
        content.Measure(new System.Windows.Size(width, height));
        content.Arrange(new System.Windows.Rect(0, 0, width, height));
        content.UpdateLayout();
        if (window.FindName("SessionList") is System.Windows.Controls.ListBox list && list.Items.Count > 1)
        {
            list.SelectedIndex = args_selected;
        }

        if (analyzerTab is not null && window.FindName("PlayersGrid") is System.Windows.Controls.DataGrid players && players.Items.Count > 1)
        {
            players.SelectedIndex = 1; // the Sorcerer: the sample skills are a Sorcerer's
        }

        content.UpdateLayout();
        content.UpdateLayout();
        var bitmap = Shot((int)width, (int)height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var back = new System.Windows.Shapes.Rectangle { Width = width, Height = height, Fill = (System.Windows.Media.Brush)app.Resources["Brush.Window"] };
        back.Measure(new System.Windows.Size(width, height));
        back.Arrange(new System.Windows.Rect(0, 0, width, height));
        bitmap.Render(back);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine("render-sessions: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>A player's details with made-up fights (five real Sorcerer skills with their icons), for the pictures of the details window and of the session manager.</summary>
    private static Ui.PlayerDetailsView CreateSampleDetails(string tab, string name, string className)
    {
        var random = new Random(7);
        var events = new List<Combat.DamageEvent>();
        DateTime start = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Local);
        // five real Sorcerer skills (with icons) so the rotation tab shows what a player really sees
        var table = Aion2.Protocol.Aion2SkillNames.Load();
        var sorcerer = table.Where(kv => Aion2.Protocol.Aion2SkillNames.ClassOf(kv.Key) == "Sorcerer" && !Aion2.Protocol.Aion2Artwork.IsPassive(kv.Key) && !Aion2.Protocol.Aion2SkillNames.IsNonDamageEffect(kv.Key)
                && Aion2.Protocol.Aion2SkillIcons.PathFor(kv.Key) is not null && kv.Value is not ("Dodge" or "Basic Attack"))
            .GroupBy(kv => kv.Value).Select(g => g.First()).Take(5).ToList();
        (string Skill, int Id, int Weight)[] skills = sorcerer.Count >= 5
            ? sorcerer.Select((kv, i) => (kv.Value, kv.Key, new[] { 5, 2, 9, 9, 20 }[i])).ToArray()
            : new[] { ("Blaze", 11000000, 5), ("Hellfire", 11100000, 2), ("Cold Wave", 12000000, 9), ("Ice Chain", 13000000, 9), ("Bittercold Wind", 15280000, 20) };
        for (int t = 0; t < 75; t++)
        {
            double burst = t is > 28 and < 36 ? 3.5 : 1.0;
            for (int k = 0; k < (int)(2 * burst); k++)
            {
                var skill = skills[random.Next(skills.Length)];
                int target = random.Next(5) == 0 ? 2 : 1;
                {
                    double roll = random.NextDouble();
                    var kinds = (roll < 0.18 ? Combat.HitKinds.Back : Combat.HitKinds.Front) | (random.NextDouble() < 0.12 ? Combat.HitKinds.Perfect : 0) | (random.NextDouble() < 0.04 ? Combat.HitKinds.Double : 0);
                    events.Add(new Combat.DamageEvent(start.AddSeconds(t + random.NextDouble()), 5, target, random.Next(300, 3000) * (skill.Weight > 8 ? 1 : 3), false, skill.Skill, random.Next(5) == 0, SkillId: skill.Id,
                        Kinds: kinds, HitCount: random.NextDouble() < 0.1 ? 2 : 1));
                }
            }
        }

        var all = events.Concat(events.Select(e => e with { SourceObjectId = 6, Amount = e.Amount / 2 })).ToList();
        // made-up extras for the rotation and effect tabs: a use before every hit, support skills now and then, three effects and two staggers
        var support = Aion2.Protocol.Aion2SkillNames.Load().Where(kv => kv.Value.Contains("Armor") || kv.Value.Contains("Shield") || kv.Value.Contains("Boost") || kv.Value.Contains("Stance")).Take(3).Select(kv => kv.Key).ToList();
        var uses = events.Select(e => new Combat.SkillUse(e.Timestamp.AddSeconds(-0.12), 5, e.SkillId)).ToList();
        for (int t = 0; t < 75; t += 18)
        {
            for (int i = 0; i < support.Count; i++)
            {
                uses.Add(new Combat.SkillUse(start.AddSeconds(t + i * 0.2), 5, support[i]));
            }
        }

        var effects = new List<Combat.BuffEvent>();
        for (int i = 0; i < support.Count; i++)
        {
            effects.Add(new Combat.BuffEvent(start.AddSeconds(i * 0.2), Combat.BuffKind.On, 5, 10 + i, support[i], i == 0 ? 6 : 5, i == 2 ? 12000 : -1));
        }

        var extras = new Combat.FightExtras(uses.OrderBy(u => u.At).ToList(), effects,
            new[] { new Combat.StaggerEvent(start.AddSeconds(21), 1, 0, 0, true), new Combat.StaggerEvent(start.AddSeconds(52), 1, 0, 0, true) },
            new Dictionary<int, string> { [5] = name, [6] = "Nyxelle" });
        var window = new Ui.PlayerDetailsView(name, className, "", events, id => id == 1 ? "Training Scarecrow" : "Elite Guard", fightEvents: all,
            extras: extras, playerId: 5, fightStart: start, fightEnd: start.AddSeconds(75));
        window.TabRotationButton.IsChecked = tab == "rotation";
        window.TabBuffsButton.IsChecked = tab == "buffs";
        return window;
    }

    /// <summary>Draws the fight report of a made-up fight on one of its tabs ("Damage", "Healing", "Damage taken", "Shields", "Defense", "Buffs", "Support",
    /// "Rotation", "Timeline") into a picture; no window is shown.</summary>
    private static void RunRenderReportMode(string path, string tab)
    {
        Environment.SetEnvironmentVariable("AIONDPS_DATA_DIR", Path.Combine(Path.GetTempPath(), "aiondps-render-report"));
        var app = new System.Windows.Application();
        var settings = Ui.MeterSettings.Load();
        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        try
        {
            RenderReport(path, tab);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.ChangeExtension(path, ".error.txt"), ex.ToString());
            Console.WriteLine("render-report: " + ex.Message);
            Environment.Exit(1);
        }

        Environment.Exit(0);
    }

    /// <summary>Draws the history window on one of its tabs, with made-up entries (the sample fight saved as runs, boss fights and dummy parses on
    /// a few days) in a folder of its own; no window is shown.</summary>
    private static void RunRenderHistoryMode(string path, string tab)
    {
        Environment.SetEnvironmentVariable("AIONDPS_DATA_DIR", Path.Combine(Path.GetTempPath(), "aiondps-render-history"));
        var app = new System.Windows.Application();
        var settings = Ui.MeterSettings.Load();
        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        try
        {
            string folder = Path.Combine(Path.GetTempPath(), "a2h-render-history");
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            History.FightLog.Folder = folder;
            History.FightLog.Reload();
            Combat.ReportInput sample = Ui.FightReportSample.Input();
            for (int day = 0; day < 2; day++)
            {
                // day 2 is played on a second character of the roster, so the character chips show
                Combat.ReportInput played = day == 0 ? sample : sample with { Players = sample.Players.Select(p => p with { IsSelf = p.Name == "Nyxelle" }).ToList() };
                for (int k = 0; k < 3; k++)
                {
                    TimeSpan shift = TimeSpan.FromDays(-day) + TimeSpan.FromMinutes(-40 * k);
                    Combat.ReportInput moved = Shift(played, shift);
                    string dungeon = k == 1 ? "Shattered Arkanis" : "Ferocious Horn Den";
                    int zone = 600001 + k;
                    History.FightLog.Save(History.FightLogKind.Run, moved with { Title = dungeon, Targets = null }, null, zone, 2);
                    History.FightLog.Save(History.FightLogKind.Boss, moved with { Killed = k != 2 }, dungeon, zone);
                    History.FightLog.Save(History.FightLogKind.Boss, Shift(played, shift + TimeSpan.FromSeconds(20)) with { Killed = true }, dungeon, zone);
                }

                // Nightmare and an open-world boss: no run around them
                History.FightLog.Save(History.FightLogKind.Boss, Shift(played, TimeSpan.FromDays(-day) + TimeSpan.FromHours(-2)) with { Killed = day == 0 }, "Nightmare", 200003);
                History.FightLog.Save(History.FightLogKind.Boss, Shift(played, TimeSpan.FromDays(-day) + TimeSpan.FromHours(-2) + TimeSpan.FromMinutes(3)), "Nightmare", 200003);
                History.FightLog.Save(History.FightLogKind.Boss, Shift(played, TimeSpan.FromDays(-day) + TimeSpan.FromHours(-4)), null, 0);
                History.FightLog.Save(History.FightLogKind.Dummy, Shift(played, TimeSpan.FromDays(-day) + TimeSpan.FromHours(-3)) with { Title = "Training Scarecrow" }, "2:00 parse");
            }

            SampleRanks();
            var window = new Ui.HistoryWindow();
            window.ShowTab(tab.Replace('-', ' '));
            var page = window.PageContent;
            ((System.Windows.Controls.ScrollViewer)page.Parent).Content = null;
            const double width = 1040;
            var content = new System.Windows.Controls.Border { Child = page };
            content.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Brush.Window");
            content.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.Text");
            content.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "Font.Ui");
            content.Measure(new System.Windows.Size(width, double.PositiveInfinity));
            double height = Math.Min(2400, Math.Ceiling(content.DesiredSize.Height));
            content.Arrange(new System.Windows.Rect(0, 0, width, height));
            content.UpdateLayout();
            var bitmap = Shot((int)width, (int)height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = File.Create(path))
            {
                encoder.Save(file);
            }

            Console.WriteLine("render-history: wrote " + path);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.ChangeExtension(path, ".error.txt"), ex.ToString());
            Environment.Exit(1);
        }

        Environment.Exit(0);
    }

    /// <summary>
    /// Made-up lists of the game for the world boss page's picture: Altgard with some bosses up, most dead with their time back, Lower Reshanta with its
    /// Executor up; the other regions not read (nothing is saved).
    /// </summary>
    private static void SampleFieldBosses()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var altgard = new List<Aion2.Aion2FieldBosses.Slot>();
        for (int n = 1; n <= 24; n++)
        {
            bool alive = n % 6 == 2;
            long time = alive ? now - (n * 7 * 60_000) : n % 9 == 0 ? 0 : now + ((n * 37 % 300) + 3) * 60_000L;
            altgard.Add(new Aion2.Aion2FieldBosses.Slot(n, alive, 0, 0, time));
        }

        Aion2.Aion2FieldBosses.Update(1110, altgard);
        Aion2.Aion2FieldBosses.Update(20, new[]
        {
            new Aion2.Aion2FieldBosses.Slot(1, false, 0, 0, now + (14 * 3_600_000L)), new Aion2.Aion2FieldBosses.Slot(2, false, 0, 0, 0),
            new Aion2.Aion2FieldBosses.Slot(8, true, -17728, -30749, now - (25 * 60_000L)),
        });
    }

    /// <summary>
    /// Made-up website ranks for the pictures' killed boss fights (nothing is asked of the website): most of them on the website with a percentile of every
    /// colour, one with too few logs of the class yet, every fourth not uploaded.
    /// </summary>
    private static void SampleRanks()
    {
        History.FightRanks.Offline = true;
        History.FightRanks.Reload();
        double[] percents = { 96.4, 78.2, 54.9, 99.3, 31.0, 88.6, 63.1, 100, 12.5, 81.7 };
        int i = 0;
        foreach (History.FightLogEntry e in History.FightLog.Entries().Where(e => e.Kind == History.FightLogKind.Boss && e.Killed == true).ToList())
        {
            int n = i++;
            if (n % 4 == 3)
            {
                continue;
            }

            string url = $"https://atreia.app/meter/logs/sample{n:0000}";
            History.FightLog.SetUrl(e.Id, url);
            int of = 180 + (n * 37 % 160);
            double pct = percents[n % percents.Length];
            History.FightRanks.Seed(url, n == 6
                ? new History.FightRanks.Own(0, 0, 6, 10, null, null, DateTime.UtcNow)
                : new History.FightRanks.Own(pct, (int)Math.Round((100 - pct) / 100 * (of - 1)) + 1, of, 10, 50_000, 60_000, DateTime.UtcNow));
        }
    }

    /// <summary>The sample fight moved in time (every event, effect and cast), for the history picture's other days.</summary>
    /// <summary>Draws the app window (its sections on the left, a section on the right: home, overlays, characters, history, meter) with made-up fights and
    /// characters into a picture; no window is ever shown.</summary>
    private static void RunRenderAppMode(string path, string page, int height = 760)
    {
        Environment.SetEnvironmentVariable("AIONDPS_DATA_DIR", Path.Combine(Path.GetTempPath(), "aiondps-render-app"));
        var app = new System.Windows.Application();
        var settings = Ui.MeterSettings.Load();
        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        try
        {
            string folder = Path.Combine(Path.GetTempPath(), "a2h-render-app");
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            History.FightLog.Folder = folder;
            History.FightLog.Reload();
            Combat.ReportInput sample = Ui.FightReportSample.Input();
            for (int k = 0; k < 4; k++)
            {
                Combat.ReportInput moved = Shift(sample, TimeSpan.FromMinutes(-50 * k - 20));
                History.FightLog.Save(k == 2 ? History.FightLogKind.Dummy : History.FightLogKind.Boss, k == 2 ? moved with { Title = "Training Scarecrow" } : moved with { Killed = k != 3 }, k == 2 ? "2:00 parse" : null);
            }

            SampleRanks();
            SampleFieldBosses();

            // characters: the checklist's real login list, three characters, one of them full of Odyle Energy
            Aion2.Aion2Tickets.Reset();
            Aion2.Aion2Tickets.TryParseList(Convert.FromHexString(Combat.SelfCheckTickets.SampleLogin), out var entries);
            var t0 = DateTime.Now;
            foreach (var (name, odyle, charged, at) in new[] { ("Seraphyne", 520, 640, 0), ("Nyxelle", 840, 0, 10), ("Draegor", 300, 120, 20) })
            {
                Aion2.Aion2Tickets.ReceiveList(entries, t0.AddSeconds(at));
                Aion2.Aion2Tickets.NoteCharacter(name, 2302, t0.AddSeconds(at + 4), level: 45);
                Aion2.Aion2Tickets.ReceiveUpdate(new Aion2.TicketReading(Aion2.Aion2Tickets.OdyleId, new Aion2.TicketValue(odyle, charged, 0)), t0.AddSeconds(at + 6));
                Aion2.Aion2Tickets.ReceiveUpdate(new Aion2.TicketReading(Aion2.Aion2Tickets.PremiumId, new Aion2.TicketValue(0, 0, DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeMilliseconds())), t0.AddSeconds(at + 6));
                if (name != "Nyxelle")
                {
                    // the Abyss Corridors given out on entering the Abyss after the last siege (two open to the side); Draegor went through one, Nyxelle was not in the Abyss since
                    foreach (var (corridor, ms) in new[] { (Aion2.Aion2Corridors.FirstId, 0L), (Aion2.Aion2Corridors.FirstId + 1, 130_000L), (Aion2.Aion2Corridors.FirstId + 2, 130_000L) })
                    {
                        Aion2.Aion2Tickets.ReceiveUpdate(new Aion2.TicketReading(corridor, new Aion2.TicketValue(0, 0, ms)), t0.AddSeconds(at + 7), reason: 2);
                    }

                    if (name == "Draegor")
                    {
                        Aion2.Aion2Tickets.ReceiveUpdate(new Aion2.TicketReading(Aion2.Aion2Corridors.FirstId + 1, default), t0.AddSeconds(at + 8), reason: 3);
                    }
                }
            }

            // their classes, as the login's character list gives them (made up: a Gladiator, a Cleric, an Assassin)
            long seenMs = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
            Aion2.Aion2Tickets.NoteListedCharacters(null, new[]
            {
                new Aion2.AccountCharacter("Seraphyne", 5, 1, 45, 2890, "Dawnbreakers", seenMs, seenMs, 2302, 101),
                new Aion2.AccountCharacter("Nyxelle", 29, 1, 45, 2760, "Dawnbreakers", seenMs, seenMs, 2302, 102),
                new Aion2.AccountCharacter("Draegor", 17, 1, 45, 2655, "Dawnbreakers", seenMs, seenMs, 2302, 103),
            });

            // the Black Cloud Merchants: the three characters' pets (Nyxelle has Swift Lugi at the top level, Draegor at 1), the merchant's last offers to Seraphyne
            Aion2.Aion2BlackCloud.Reset();
            var everyPet = Aion2.Protocol.Aion2Pets.AllPetIds.Where(p => !Aion2.Protocol.Aion2MerchantPets.Is(p)).Select(p => new Aion2.Protocol.Aion2PetState(p, 3, 0)).ToList();
            Aion2.Aion2BlackCloud.NotePets("Seraphyne", 2302, everyPet);
            Aion2.Aion2BlackCloud.NotePets("Nyxelle", 2302, everyPet.Append(new Aion2.Protocol.Aion2PetState(1111, 3, 0)).Append(new Aion2.Protocol.Aion2PetState(1240, 3, 0)).ToList());
            Aion2.Aion2BlackCloud.NotePets("Draegor", 2302, everyPet.Append(new Aion2.Protocol.Aion2PetState(1111, 1, 2)).ToList());
            Aion2.Aion2BlackCloud.NoteOffers("Seraphyne", 2302, 0xc9, new[] { new Aion2.Protocol.Aion2ShopPet(1111, 0xc9, 1_200_000), new Aion2.Protocol.Aion2ShopPet(1103, 0xc9, 50_000),
                    new Aion2.Protocol.Aion2ShopPet(1179, 0xc9, 90_000) },
                new DateTimeOffset(DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour + 1)).ToUnixTimeMilliseconds(), DateTime.Now.AddMinutes(-12));

            // the gathering page's tally: a session of ore, wood, gems and herbs as the game gives them (mostly normal, now and then Fine, rarely Pure or Radiant)
            Aion2.Aion2GatherLog.Clear(false);
            var gatherNear = new[] { "Orichalcum", "Yggdrasil", "Sapphire", "Aria", "Odyle", "Kukuru" };
            foreach ((int item, long amount, int times) in new[] { (610620013, 3L, 41), (610620014, 1L, 9), (610620015, 1L, 2), (610620016, 1L, 1), (610650011, 2L, 22), (610650013, 1L, 4),
                         (610660025, 1L, 12), (610660028, 1L, 3), (610670041, 2L, 18), (610670045, 1L, 5), (610610013, 2L, 15), (610610016, 1L, 1), (610680044, 1L, 7) })
            {
                for (int k = 0; k < times; k++)
                {
                    Aion2.Aion2GatherLog.NoteGains(new[] { (item, amount) }, DateTime.Now.AddMinutes(-30 + k), gatherNear);
                }
            }

            Announcements.NewsClient.UseForPictures(Ui.MainWindow.SampleNews());
            var window = new Ui.MainWindow { Title = Brand.AppName };
            window.BuildShell();
            window.ShowShellPage(page.StartsWith("updates", StringComparison.Ordinal) ? "home" : page);
            if (page.StartsWith("updates", StringComparison.Ordinal))
            {
                // the update question with its notes: shut (updates) or open (updates-open), the notes shipped with this build
                var releases = Update.UpdateNotes.Bundled();
                string newest = releases.Count > 0 ? releases[0].Version : "1.0.0";
                window.ShowUpdatePromptSample(releases.Take(6).ToList(), newest, releases.Count > 6 ? releases[6].Version : "1.0.0", page == "updates-open");
            }

            const int w = 1180;
            int h = Math.Clamp(height, 400, 4000);
            var content = (System.Windows.FrameworkElement)window.Content;
            for (int pass = 0; pass < 3; pass++)
            {
                content.Measure(new System.Windows.Size(w, h));
                content.Arrange(new System.Windows.Rect(0, 0, w, h));
                content.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            }

            var bitmap = Shot(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = File.Create(path))
            {
                encoder.Save(file);
            }

            Console.WriteLine("render-app: wrote " + path);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.ChangeExtension(path, ".error.txt"), ex.ToString());
            Environment.Exit(1);
        }

        Environment.Exit(0);
    }

    private static Combat.ReportInput Shift(Combat.ReportInput input, TimeSpan by)
    {
        Combat.FightExtras x = input.Extras;
        var extras = new Combat.FightExtras(x.Uses.Select(u => u with { At = u.At + by }).ToList(), x.Buffs.Select(b => b with { At = b.At + by }).ToList(),
            x.Staggers.Select(s => s with { At = s.At + by }).ToList(), x.Names, null, x.Windups?.Select(w => w with { At = w.At + by }).ToList(),
            x.Deaths?.Select(d => d with { At = d.At + by, RevivedAt = d.RevivedAt + by }).ToList(), null, x.Targets?.Select(t => t with { At = t.At + by }).ToList());
        return input with
        {
            Start = input.Start + by, End = input.End + by, Extras = extras,
            Events = input.Events.Select(e => e with { Timestamp = e.Timestamp + by }).ToList(),
            Shields = input.Shields.Select(e => e with { Timestamp = e.Timestamp + by }).ToList(),
            Avoids = input.Avoids.Select(a => a with { Timestamp = a.Timestamp + by }).ToList(),
        };
    }

    private static void RenderReport(string path, string tab)
    {
        var window = new Ui.FightReportWindow(Combat.FightReport.Build(Ui.FightReportSample.Input()));
        window.ShowTab(tab.Replace('-', ' '));
        var page = window.PageContent;
        ((System.Windows.Controls.ScrollViewer)page.Parent).Content = null;
        const double width = 1240;
        var content = new System.Windows.Controls.Border { Child = page };
        content.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Brush.Window");
        content.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.Text");
        content.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "Font.Ui");
        content.Measure(new System.Windows.Size(width, double.PositiveInfinity));
        double height = Math.Min(3200, Math.Ceiling(content.DesiredSize.Height));
        content.Arrange(new System.Windows.Rect(0, 0, width, height));
        content.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        content.UpdateLayout();
        var bitmap = Shot((int)width, (int)height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine("render-report: wrote " + path);
    }

    private static void RunRenderDetailsMode(string path, string tab)
    {
        Environment.SetEnvironmentVariable("AIONDPS_DATA_DIR", Path.Combine(Path.GetTempPath(), "aiondps-render-details"));
        var app = new System.Windows.Application();
        var settings = Ui.MeterSettings.Load();
        if (Environment.GetEnvironmentVariable("AIONDPS_LANG") is { Length: > 0 } picked)
        {
            Ui.LocalizationManager.Instance.Language = picked;
        }
        else if (settings.Language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = settings.Language; // skill and item names in the chosen language
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var window = CreateSampleDetails(tab, "Seraphyne", "Sorcerer");
        const double width = 860, height = 680;
        var content = new System.Windows.Controls.Border { Child = window, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0E, 0x18, 0x20)) };
        content.Measure(new System.Windows.Size(width, height));
        content.Arrange(new System.Windows.Rect(0, 0, width, height));
        content.UpdateLayout();
        content.UpdateLayout();
        var bitmap = Shot((int)width, (int)height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine("render-details: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>Renders the compact overlay panel with a few sample rows to a PNG, never showing a window.</summary>
    /// <summary>The open list of the target dropdown drawn under its closed box (a real popup would be a window of its own, and none is shown here).</summary>
    /// <summary>The pictures' scale: A2H_SHOT_SCALE=2 draws every picture at twice the pixels (sharp on high-density screens, for the project page); 1 otherwise.</summary>
    private static readonly double ShotScale = double.TryParse(Environment.GetEnvironmentVariable("A2H_SHOT_SCALE"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out double shotScale) && shotScale is >= 1 and <= 4 ? shotScale : 1;

    /// <summary>A picture to draw into, at <see cref="ShotScale"/>: as many more pixels, at as many more dots per inch, so the layout stays the same.</summary>
    private static System.Windows.Media.Imaging.RenderTargetBitmap Shot(int width, int height, double dpiX, double dpiY, System.Windows.Media.PixelFormat format) =>
        new((int)Math.Ceiling(width * ShotScale), (int)Math.Ceiling(height * ShotScale), dpiX * ShotScale, dpiY * ShotScale, format);

    private static void DrawOpenTargetList(Ui.MainWindow window, System.Windows.FrameworkElement panel, System.Windows.Media.Imaging.RenderTargetBitmap bitmap)
    {
        var combo = window.MobBossFilter;
        foreach (string name in new[] { "Talisra of the Void", "Siliator of Deceit", "Ruthilis of Pain", "Transcendent Bakarma, Keeper of the Lost Gate" })
        {
            combo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = name });
        }

        combo.ApplyTemplate();
        System.Windows.Controls.Primitives.Popup? popup = null;
        var queue = new Queue<System.Windows.DependencyObject>();
        queue.Enqueue(combo);
        while (queue.Count > 0 && popup is null)
        {
            var node = queue.Dequeue();
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
                if (child is System.Windows.Controls.Primitives.Popup found)
                {
                    popup = found;
                    break;
                }

                queue.Enqueue(child);
            }
        }

        if (popup?.Child is not System.Windows.FrameworkElement list)
        {
            Console.WriteLine("render-overlay: the dropdown has no popup to draw");
            return;
        }

        for (int pass = 0; pass < 4; pass++)
        {
            list.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            list.Arrange(new System.Windows.Rect(list.DesiredSize));
            list.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        var at = combo.TranslatePoint(new System.Windows.Point(0, combo.ActualHeight), panel);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(list), null, new System.Windows.Rect(at.X, at.Y, list.ActualWidth, list.ActualHeight));
        }

        bitmap.Render(visual);
        Console.WriteLine($"render-overlay: the open list is {list.ActualWidth:0} px wide, the closed box {combo.ActualWidth:0} px");
    }

    private static void RunRenderOverlayMode(string path, string kind)
    {
        // "chips-all" etc.: the same look in the All in One mode.
        bool allCompact = kind.EndsWith("-allc", StringComparison.Ordinal);
        bool all = kind.EndsWith("-all", StringComparison.Ordinal) || allCompact;
        kind = allCompact ? kind[..^5] : all ? kind[..^4] : kind;
        var app = new System.Windows.Application();
        var settings = Ui.MeterSettings.Load();
        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        if (Environment.GetEnvironmentVariable("AIONDPS_LANG") is { Length: > 0 } pictureLanguage)
        {
            Ui.LocalizationManager.Instance.Language = pictureLanguage; // pictures in a chosen language, whatever the system's is
        }

        var window = new Ui.MainWindow();
        window.Title = Brand.AppName; // the pictures carry no version number
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
        var type = typeof(Ui.MainWindow);
        var rows = (System.Collections.ObjectModel.ObservableCollection<Ui.PlayerRow>)type.GetField("_rows", flags)!.GetValue(window)!;
        (string Name, string Cls, long Dmg, double Dps)[] sample =
        {
            ("Kaivhan", "Gladiator", 916_700, 8300), ("Nyxelle", "Ranger", 790_800, 7100), ("Seraphyne", "Sorcerer", 757_700, 6800),
            ("Draegor", "Cleric", 434_200, 3900), ("Lumiael", "Spiritmaster", 232_800, 2100),
        };
        long total = sample.Sum(x => x.Dmg);
        int rank = 0;
        foreach (var x in sample)
        {
            var row = new Ui.PlayerRow(++rank) { Name = x.Name, ClassName = x.Cls, Damage = x.Dmg, Dps = x.Dps, SharePercent = 100.0 * x.Dmg / total, FillPercent = 100.0 * x.Dmg / sample[0].Dmg,
                Rank = rank, Faction = rank is 1 or 5 ? "Elyos" : rank is 3 ? "" : "Asmodian", // a sample of both factions and one unknown
                CombatPowerText = Ui.PlayerRow.Compact(150_000 + 9_300 * (6 - rank)) };
            if (all)
            {
                row.AllMode = !allCompact;
                row.AllCompact = allCompact;
                row.Healing = x.Cls == "Cleric" ? 384_500 : rank * 3_100;
                row.DamageTaken = 100_000 / rank;
                row.Absorbed = rank switch { 1 => 41_800, 3 => 12_400, 4 => 18_900, _ => 0 }; // a group shield on three of them
            }

            // the bar's parts (MeterBarParts): what the buffs lent the others - much from the Cleric, a little from the Gladiator's counter, nothing from a
            // Ranger or a Sorcerer (their effects are all on themselves) - and the healing on the others, over the fight's 1:51
            row.Given = x.Cls switch { "Cleric" => 1_480, "Gladiator" => 210, _ => 0 };
            row.HealOthers = x.Cls switch { "Cleric" => 352_000, "Spiritmaster" => 21_600, _ => 0 };
            row.HealOthersRate = row.HealOthers > 0 ? row.HealOthers / 111.0 : null;
            if (kind == "rdps")
            {
                // the rDPS view: each one's damage without the others' buffs, and with what their own buffs lent
                row.Ndps = x.Dps * (x.Cls == "Cleric" ? 0.97 : 0.88);
                row.Rdps = row.Ndps + row.Given;
                row.RdpsView = true;
            }

            rows.Add(row);
        }

        if (kind == "rdps")
        {
            // the rDPS view with the rDPS and Given columns and their titles: the big rate is rDPS, the rDPS column shows the plain DPS
            window.ApplyMeterLook(new Ui.MeterSettings { MeterColumnTitles = true, MeterColumns = new() { "icon", "cp", "rdps", "given", "dps", "share", "total" } });
            Ui.MeterTitles.Instance.Set("rDPS", "%", "Total", "DPS");
        }

        window.ShowSampleBarParts(rdpsView: kind == "rdps", seconds: 111);
        if (kind == "rdps")
        {
            window.OverlayModeText.Text = "rDPS";
            window.OverlayTotalRateText.Text = Ui.PlayerRow.Compact((long)rows.Sum(r => r.Rdps ?? 0));
        }

        window.OverlayTargetText.Text = "Transcendent Bakarma";
        window.OverlayTimeText.Text = "1:51";
        window.OverlayModeText.Text = allCompact ? "ALL+" : all ? "ALL" : "DMG";
        window.OverlayTotalRateText.Text = Ui.PlayerRow.Compact((long)sample.Sum(x => x.Dps));
        if (all && !allCompact)
        {
            window.OverlayRateHeader.Text = "Damage";
            window.OverlayShareHeader.Text = "Heal";
            window.OverlayTotalHeader.Text = "Taken";
        }

        System.Windows.FrameworkElement panel;
        if (kind is "menu" or "menu-dummy" or "tools")
        {
            // the meter's own menu ("…" in its header), or its Training dummy submenu, as they open over the game
            // "menu-dummy": the pill's menu (what is measured, the training dummy, Grind)
            var items = kind == "menu" ? window.OverlaySettingsItems() : kind == "tools" ? window.OverlayToolsItems() : window.OverlayModeItems();
            var menu = new System.Windows.Controls.ContextMenu();
            if (window.TryFindResource(typeof(System.Windows.Controls.ContextMenu)) is System.Windows.Style menuStyle)
            {
                menu.Style = menuStyle;
            }

            var lines = items.Cast<object>().ToList();
            foreach (object line in lines)
            {
                menu.Items.Add(line);
            }

            panel = menu;
        }
        else if (kind == "chips")
        {
            panel = window.OverlayContent;
            panel.Visibility = System.Windows.Visibility.Visible;
            panel.Width = 390;
        }
        else if (kind is "window" or "window-open")
        {
            // The normal window as it is without Hide UI: its whole content at a typical size.
            window.OverlayTargetText.Text = "Transcendent Bakarma";
            panel = (System.Windows.FrameworkElement)window.Content;
            panel.Measure(new System.Windows.Size(520, 420));
            panel.Arrange(new System.Windows.Rect(0, 0, 520, 420));
            panel.UpdateLayout();
            for (int pass = 0; pass < 3; pass++)
            {
                // the grid builds its rows and columns on the dispatcher; let it, then lay out again
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                panel.Measure(new System.Windows.Size(520, 420));
                panel.Arrange(new System.Windows.Rect(0, 0, 520, 420));
                panel.UpdateLayout();
            }
        }
        else
        {
            window.CompactOverlayPanel.Visibility = System.Windows.Visibility.Visible;
            panel = window.CompactOverlayPanel;
            // the bar's own character and the counters at the foot, as on any meter in the game
            window.ShowOverlayIdentity("Kaivhan", "Gladiator", 196_500);
            window.ShowOverlayTickets(Ui.MainWindow.SampleTickets());
            if (kind == "crowd")
            {
                // a field boss with fourteen players on a meter the player made narrower (380), the analyst design's many columns: ten lines, fitted to the width
                rows.Clear();
                string[] classes = { "Gladiator", "Ranger", "Sorcerer", "Templar", "Assassin", "Spiritmaster", "Chanter", "Cleric" };
                string[] crowd = { "Aethryn", "Brakkus", "Caelith", "Dravenna", "Eldrion", "Faelyx", "Gorvane", "Hyrelle", "Isveth", "Joruhn", "Kestrael", "Lorwyn", "Maelis", "Noctavian" };
                for (int i = 0; i < 14; i++)
                {
                    long dmg = 2_400_000 - i * 150_000;
                    string cls = classes[i % classes.Length];
                    rows.Add(new Ui.PlayerRow(100 + i)
                    {
                        Name = crowd[i], ClassName = cls, Damage = dmg, Dps = dmg / 111.0, Rank = i + 1,
                        CombatPowerText = Ui.PlayerRow.Compact(190_000 - i * 3_000),
                        Given = cls is "Chanter" or "Cleric" ? 1_400 : cls == "Gladiator" ? 180 : 0,
                        HealOthers = cls == "Cleric" ? 300_000 : cls == "Chanter" ? 120_000 : 0,
                        HealOthersRate = cls == "Cleric" ? 300_000 / 111.0 : cls == "Chanter" ? 120_000 / 111.0 : null,
                        Hits = new HitStats { Direct = 500, Crit = 210 - i * 5, Kinded = 500, Back = 120 + i, Front = 300 - i * 4, Perfect = 40, Double = 20 },
                    });
                }

                window.ApplyMeterLook(Ui.MeterLook.Presets.First(design => design.Id == "analyst").Settings());
                window.ShowSampleCrowd(380);
                window.OverlayTotalRateText.Text = Ui.PlayerRow.Compact((long)rows.Sum(r => r.Dps ?? 0));
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            }

            if (kind is "compact" or "rdps")
            {
                // the boss line over the lines, as in a boss fight: what is left of it, when it dies at the group's pace, its enrage
                window.ShowBossBar("Transcendent Bakarma", 3_140_000, 12_600_000, TimeSpan.FromSeconds(111), TimeSpan.FromSeconds(129));
            }

            if (kind == "bar")
            {
                // folded between fights: the bar (with a cube in range) and the counters, nothing of the last fight
                window.OverlayBody.Visibility = System.Windows.Visibility.Collapsed;
                window.OverlayTimeText.Visibility = System.Windows.Visibility.Collapsed;
                Ui.MainWindow.ShowCubeChip(window.OverlayCubeCallout, window.OverlayCubeCalloutText, 1, "38 m");
                window.OverlayCubeCallout.Visibility = System.Windows.Visibility.Visible;
            }
            else if (kind == "find")
            {
                // Find a player: two characters of the name found, the first picked
                window.OverlayBody.Visibility = System.Windows.Visibility.Collapsed;
                window.OverlayTimeText.Visibility = System.Windows.Visibility.Collapsed;
                window.OverlayFind.Visibility = System.Windows.Visibility.Visible;
                window.OverlayFindBox.Text = "Valtheris";
                window.OverlayFindHint.Visibility = System.Windows.Visibility.Collapsed;
                var found = new[]
                {
                    new Upload.FoundPlayer("Valtheris", "Templar", 45, "Vaizel", 2302, "eu", 182_730, 2968, 21_480, "Thamon", "https://atreia.app/armory/?region=eu&server=Vaizel&name=Valtheris", 92, 3, 31),
                    new Upload.FoundPlayer("Valtheris", "Cleric", 32, "Siel", 1101, "nae", null, null, null, null, "https://atreia.app/armory/?region=nae&server=Siel&name=Valtheris"),
                };
                window.ShowFindResults(found);
                window.PickFindResult(found[0]); // the one clicked: the others go, "All 2 results" brings them back
            }
            else if (kind is "stagger" or "stagger-failed" or "stagger-tip")
            {
                // the meter's Stagger view (its switch after Group): a check running 5.2 s into an 8 s cast, or failed with the next one expected, the boss's
                // earlier checks as chips under it; stagger-tip: with an icon's tooltip under it (a tooltip is a window of its own: drawn here as it looks)
                window.ShowBossBar("Ultimate Berk", 2_460_000, 9_800_000, TimeSpan.FromSeconds(95), TimeSpan.FromSeconds(140));
                var (checks, now) = Ui.StaggerSample.Series(kind != "stagger");
                StaggerCheck check = checks[^1];
                window.ShowStaggerView(checks, check, now, Ui.StaggerSample.People, kind == "stagger" ? null : "Next stagger check: Extinction for Eternity in 41 s");
                if (kind == "stagger-tip" && check.Credit.Players.FirstOrDefault(p => p.Player == 2) is { } goku)
                {
                    var tip = new System.Windows.Controls.Border
                    {
                        Margin = new System.Windows.Thickness(40, 0, 10, 8), Padding = new System.Windows.Thickness(9, 7, 9, 8), CornerRadius = new System.Windows.CornerRadius(5),
                        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x22, 0x28)),
                        BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x46, 0x50)), BorderThickness = new System.Windows.Thickness(1),
                        Child = Ui.StaggerCardView.SkillTip(goku.Skills[0], Ui.StaggerSample.People[1], check.Credit, "seconds into the check"),
                    };
                    System.Windows.Documents.TextElement.SetForeground(tip, System.Windows.Media.Brushes.WhiteSmoke);
                    int at = window.OverlayBody.Children.IndexOf(window.OverlayStaggerCard);
                    window.OverlayBody.Children.Insert(at + 1, tip);
                }
            }
            else if (kind == "bossbar")
            {
                // the boss bar over the lines: a boss at 62 %, dying in 1:23 at the group's pace, its enrage in 2:10
                window.ShowBossBar("Kromede the Corrupt", 7_840_000, 12_600_000, TimeSpan.FromSeconds(83), TimeSpan.FromSeconds(130));
            }
            else if (kind == "findservers")
            {
                // Find a player's server picker: Europe's Asmodian servers, Vaizel picked
                window.OverlayBody.Visibility = System.Windows.Visibility.Collapsed;
                window.OverlayTimeText.Visibility = System.Windows.Visibility.Collapsed;
                window.OverlayFind.Visibility = System.Windows.Visibility.Visible;
                window.OverlayFindBox.Text = "Valtheris";
                window.OverlayFindHint.Visibility = System.Windows.Visibility.Collapsed;
                window.OpenServerPicker("eu", 2302, asmodian: true);
            }
            else if (kind == "dummy")
            {
                // a dummy parse running: its card stands in for the lines
                var t0 = new DateTime(2026, 11, 5, 12, 0, 0);
                var hits = new List<DamageEvent>();
                for (int i = 0; i < 180; i++)
                {
                    HitKinds kinds = (i % 9 == 0 ? HitKinds.Perfect : 0) | (i % 6 == 0 ? HitKinds.Double : 0) | (i % 4 == 0 ? HitKinds.Back : HitKinds.Front);
                    hits.Add(new DamageEvent(t0.AddSeconds(i * 0.23), 1, 900, 4_000 + (i * 37 % 9_000), false, IsCritical: i % 5 < 2, Kinds: kinds));
                }

                window.ShowDummyCard("Run 3  ·  running", "0:41 / 1:00", 27_600, Ui.MainWindow.DummyStatsOf(hits));
                window.OverlayDummyCard.Visibility = System.Windows.Visibility.Visible;
                window.OverlayDummyBar.Visibility = System.Windows.Visibility.Visible;
                window.OverlayDummyBar.Value = 0.32;
                window.CompactOverlayRowsArea.Visibility = System.Windows.Visibility.Collapsed;
                window.OverlayTotalLine.Visibility = System.Windows.Visibility.Collapsed;
                window.OverlayTargetText.Visibility = System.Windows.Visibility.Collapsed;
                window.OverlayTimeText.Text = "0:19";
                window.OverlayModeShort.Text = "Dummy";
            }
            if (kind.StartsWith("look-", StringComparison.Ordinal))
            {
                // the meter in other looks (Overlays → Meter look): the page's sample players, with hit kinds and support buffs
                rows.Clear();
                foreach (Ui.PlayerRow sampleRow in Ui.MainWindow.SampleMeterRows())
                {
                    rows.Add(sampleRow);
                }

                window.OverlayTotalRateText.Text = Ui.PlayerRow.Compact((long)rows.Sum(r => r.Dps ?? 0));
                // look-<design> draws one of the page's premade designs (look-raidlog, look-minimal ...); the others are mixes of their own
                Ui.MeterLookPreset? design = Ui.MeterLook.Presets.FirstOrDefault(p => "look-" + p.Id == kind);
                window.ApplyMeterLook(design?.Settings() ?? kind switch
                {
                    "look-wide" => new Ui.MeterSettings
                    {
                        MeterBarStyle = "capsule", MeterFont = "bahnschrift", MeterColumnTitles = true,
                        MeterColumns = new() { "rank", "icon", "cp", "crit", "back", "front", "perfect", "rdps", "given", "dps", "share", "total" },
                    },
                    "look-thread" => new Ui.MeterSettings
                    {
                        MeterBarStyle = "thread", MeterRowSize = "compact", MeterFont = "mono", MeterShowTotal = false, MeterBestLast = true,
                        MeterColumns = new() { "icon", "classcolor", "crit", "back", "dps", "total" },
                    },
                    _ => new Ui.MeterSettings
                    {
                        MeterBarStyle = "frame", MeterRowSize = "roomy", MeterFont = "condensed", MeterTextScale = 1.1, MeterHeaderButtons = "hover", MeterShowStatus = false,
                        MeterColumns = new() { "rank", "icon", "dps", "share" },
                    },
                });
                window.UpdateBarLegend();
                // the rows are sorted by the view: one pass so the order of the look is drawn
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            }

            if (kind == "callout")
            {
                // the boss timers line: one cast coming now, the next in 12 s, the enrage in 48 s
                Ui.MainWindow.ShowBossCallout(window.OverlayBossCalloutText, new Upload.BossCalloutFrame("Brigade General Vasharti",
                    new[] { new Upload.BossCallout("Earth Shatter", 0, true), new Upload.BossCallout("Flame Breath", 11.4, false) }, TimeSpan.FromSeconds(48), 37));
                window.OverlayBossCallout.Visibility = System.Windows.Visibility.Visible;
                // and the cube finder's line under it
                Ui.MainWindow.ShowCubeChip(window.OverlayCubeCallout, window.OverlayCubeCalloutText, 1, "42 m");
                window.OverlayCubeCallout.Visibility = System.Windows.Visibility.Visible;
            }
        }

        if (kind is not ("window" or "window-open"))
        {
            panel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            panel.Arrange(new System.Windows.Rect(panel.DesiredSize));
        }

        panel.UpdateLayout();
        int w = kind is "window" or "window-open" ? 520 : (int)Math.Ceiling(panel.DesiredSize.Width), h = kind is "window" or "window-open" ? 420 : (int)Math.Ceiling(panel.DesiredSize.Height); // the window is drawn at the size it was arranged at
        var backdrop = new System.Windows.Controls.Border { Width = w, Height = h, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x30, 0x40, 0x30)) };
        int overlayScale = kind == "window-open" ? 1 : int.TryParse(Environment.GetEnvironmentVariable("AIONDPS_RENDER_SCALE"), out int osc) && osc is >= 1 and <= 4 ? osc : 1; // 2 = a sharp picture
        var bitmap = Shot(w * overlayScale, h * overlayScale, 96 * overlayScale, 96 * overlayScale, System.Windows.Media.PixelFormats.Pbgra32);
        backdrop.Measure(new System.Windows.Size(w, h));
        backdrop.Arrange(new System.Windows.Rect(0, 0, w, h));
        bitmap.Render(backdrop);
        bitmap.Render(panel);
        if (kind == "window-open")
        {
            DrawOpenTargetList(window, panel, bitmap);
        }

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine("render-overlay: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>Renders the Settings window's content (a tab of it) to a PNG without showing a window:
    /// the way to look at a layout change without opening anything on the player's screen.</summary>
    /// <summary>Draws the theme editor into a picture; no window is ever shown.</summary>
    private static void RunRenderThemeEditorMode(string path)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        if (settings.Language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = settings.Language;
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        var window = new Ui.ThemeEditorWindow(settings);
        var content = (System.Windows.FrameworkElement)window.Content;
        int w = (int)window.Width, h = (int)window.Height;
        for (int pass = 0; pass < 2; pass++)
        {
            content.Measure(new System.Windows.Size(w, h));
            content.Arrange(new System.Windows.Rect(0, 0, w, h));
            content.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        var backdrop = new System.Windows.Controls.Border { Width = w, Height = h, Background = (System.Windows.Media.Brush)app.Resources["Brush.Window"] ?? System.Windows.Media.Brushes.Black };
        var bitmap = Shot(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        backdrop.Measure(new System.Windows.Size(w, h));
        backdrop.Arrange(new System.Windows.Rect(0, 0, w, h));
        bitmap.Render(backdrop);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        // the colour picker as it opens from a field (the popup itself is a window of its own that is never shown here)
        var picker = new Ui.ColorPickerPanel();
        picker.SetColor(System.Windows.Media.Color.FromRgb(0xFF, 0xBA, 0x42));
        var pickerCard = new System.Windows.Controls.Border { Padding = new System.Windows.Thickness(8), Child = picker, BorderThickness = new System.Windows.Thickness(1) };
        pickerCard.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Brush.Panel");
        pickerCard.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "Brush.Border");
        pickerCard.Measure(new System.Windows.Size(400, 400));
        pickerCard.Arrange(new System.Windows.Rect(0, 0, pickerCard.DesiredSize.Width, pickerCard.DesiredSize.Height));
        pickerCard.UpdateLayout();
        var pickerBitmap = Shot((int)pickerCard.DesiredSize.Width, (int)pickerCard.DesiredSize.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        pickerBitmap.Render(pickerCard);
        var pickerEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        pickerEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(pickerBitmap));
        using (var pickerFile = File.Create(Path.ChangeExtension(path, ".picker.png")))
        {
            pickerEncoder.Save(pickerFile);
        }

        Console.WriteLine("render-themeeditor: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>Draws the checklist window (Tools menu) with two characters made of a real login list into a picture; no window is ever shown.</summary>
    private static void RunRenderChecklistMode(string path, string language, string page = "")
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        string chosen = language.Length > 0 ? language : settings.Language;
        if (chosen.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = chosen;
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        if (Environment.GetEnvironmentVariable("AIONDPS_REAL_TICKETS") == "1")
        {
            Aion2.Aion2Tickets.Reset();
            Aion2.Aion2Tickets.EnablePersistence(); // the file in AIONDPS_DATA_DIR: the characters as the meter saved them
        }
        else
        {
            Aion2.Aion2Tickets.Reset();
            Aion2.Aion2Tickets.TryParseList(Convert.FromHexString(Combat.SelfCheckTickets.SampleLogin), out var entries);
            var t0 = DateTime.Now;
            Aion2.Aion2Tickets.ReceiveList(entries, t0);
            // Elyndra's week in the dungeons: some Conquest rewards and bosses done, one Sanctuary attempt, a cube not yet taken
            foreach ((int id, int left) in new[] { (60000006, 9), (60000007, 22), (60000008, 26), (60005301, 2), (90000001, 3), (90000002, 0), (60000010, 6), (80000001, 1), (4, 6) })
            {
                Aion2.Aion2Tickets.ReceiveUpdate(new Aion2.TicketReading(id, new Aion2.TicketValue(left, 0, 0)), t0.AddSeconds(2));
            }

            Aion2.Aion2Tickets.NoteCharacter("Elyndra", 1304, t0.AddSeconds(4));
            // a second character that has used a lot: the Odyle energy is down and the bonus is in use, some counters are used up
            Aion2.Aion2Tickets.ReceiveList(entries, t0.AddSeconds(10));
            Aion2.Aion2Tickets.ReceiveUpdate(new Aion2.TicketReading(Aion2.Aion2Tickets.OdyleId, new Aion2.TicketValue(75, 520, 0)), t0.AddSeconds(11));
            Aion2.Aion2Tickets.ReceiveUpdate(new Aion2.TicketReading(Aion2.Aion2Tickets.OdyleId, new Aion2.TicketValue(90, 520, 0)), t0.AddSeconds(12), reason: 2); // a recharge tick: +15, the clock
            Aion2.Aion2Tickets.ReceiveUpdate(new Aion2.TicketReading(60000003, new Aion2.TicketValue(0, 0, 0)), t0.AddSeconds(11));
            Aion2.Aion2Tickets.ReceiveUpdate(new Aion2.TicketReading(102, new Aion2.TicketValue(0, 0, 0)), t0.AddSeconds(11));
            Aion2.Aion2Tickets.ReceiveUpdate(new Aion2.TicketReading(Aion2.Aion2Tickets.MissionsId, new Aion2.TicketValue(5, 5, 0)), t0.AddSeconds(11));
            Aion2.Aion2Tickets.ReceiveUpdate(new Aion2.TicketReading(Aion2.Aion2Tickets.PremiumId, new Aion2.TicketValue(0, 0, DateTimeOffset.UtcNow.AddHours(16 * 24 + 20).ToUnixTimeMilliseconds())), t0.AddSeconds(11));
            Aion2.Aion2Tickets.NoteCharacter("Thorvald", 1304, t0.AddSeconds(14), level: 28); // a level 28 character: the daily dungeon (30) and the level 45 contents are still locked

            // Elyndra's season missions (Elyos ids): some under way, some finished, a condition step, one of last week
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), lastWeek = new DateTimeOffset(Aion2.ChecklistManual.LastWeeklyReset(DateTime.Now).AddHours(-2)).ToUnixTimeMilliseconds();
            var season = new List<(int Id, Aion2.AchievementValue Value)>
            {
                (230211000, new(20, 3, nowMs)), (230211100, new(451, 2, nowMs)), (230211200, new(1, 1, nowMs)), (230210900, new(37, 0, nowMs)),
                (230211500, new(2, 2, lastWeek)), (231210800, new(7, 2, nowMs)), (232210900, new(4, 1, nowMs)), (232211100, new(1, 1, nowMs)),
                (233210800, new(1240, 1, nowMs)), (234210900, new(19, 9, nowMs)), (234211000, new(58, 9, nowMs)), (234211400, new(10, 3, nowMs)),
                (239241900, new(3, 1, nowMs)), (230111300, new(125, 2, nowMs)), (230111000, new(1198, 1, nowMs)), (233111400, new(10, 7, nowMs)),
            };
            Func<string?> wasName = Aion2.Aion2Achievements.CurrentName;
            Aion2.Aion2Achievements.Reset();
            Aion2.Aion2Achievements.CurrentName = () => "Elyndra";
            Aion2.Aion2Achievements.Note(season, whole: false);
            Aion2.Aion2Achievements.CurrentName = wasName;

            // Elyndra's supply requests (Elyos ids) and both characters' inventories on one server: Silentium (bound) and Raw Leather Elyndra can deliver now,
            // Orichalcum Ore that Thorvald's ore and the server storage complete, a few delivered; Craft and Boss as long as the game's (54 each)
            int First(int request) => Aion2.Aion2Supply.Find(request)?.Items[0] ?? 0;
            Aion2.SupplyGroup Tab(int group, int tab, long endMs, IEnumerable<(int Id, int Progress)> requests)
            {
                var g = new Aion2.SupplyGroup { Group = group, Tab = tab, EndMs = endMs };
                g.Requests.AddRange(requests.Where(r => Aion2.Aion2Supply.Find(r.Id) is not null).Select(r => new Aion2.SupplyEntry { Id = r.Id, Progress = r.Progress }));
                return g;
            }

            const long regular = 4072575600999; // the Regular tabs end in 2099
            Aion2.Aion2Supply.Reset();
            Aion2.Aion2Supply.Put("Elyndra", new Aion2.SupplyCharacter
            {
                SavedAt = DateTime.UtcNow,
                Groups = new()
                {
                    Tab(5401520, 1, Aion2.Aion2Supply.NextReset(DateTime.UtcNow, daily: true), new[] { (1011116, 2), (1011126, 0), (1011153, 1), (1011001, 3), (1011118, 0), (1011007, 1), (1011011, 3), (1011131, 0), (1011009, 0), (1011015, 0) }),
                    Tab(5401521, 2, Aion2.Aion2Supply.NextReset(DateTime.UtcNow, daily: false), new[] { (1012001, 7), (1012002, 2), (1012003, 0), (1012004, 7), (1012005, 0), (1012006, 0), (1012007, 3), (1012008, 0), (1012009, 7), (1012010, 0) }),
                    Tab(5403141, 3, regular, Enumerable.Range(1013001, 54).Select(id => (id, id < 1013013 ? Aion2.Aion2Supply.Find(id)?.Target ?? 0 : 0))),
                    Tab(5403131, 4, regular, Enumerable.Range(1014001, 54).Select(id => (id, 0))),
                },
            });
            const int silentium = 630840002, ore = 610620013, leather = 610530011, aria = 610670041, refining = 610530001, refiningBound = 630530001;
            Aion2.Aion2Inventories.Reset();
            Aion2.Aion2Inventories.Put(new Aion2.CharacterHoldings
            {
                Name = "Elyndra", ServerId = 1304, SavedAt = DateTime.UtcNow,
                Bag = new() { [silentium] = 3, [ore] = 4, [leather] = 6, [First(1011007)] = 1, [refining] = 14, [Aion2.SeasonMissions.OathCoin] = 40310, [503700031] = 6, [562000038] = 30 },
                Storage = new() { [refiningBound] = 4, [503700031] = 2, [503700032] = 4 },
            }, new() { [ore] = 1, [refining] = 30, [aria] = 12 });
            Aion2.Aion2Inventories.Put(new Aion2.CharacterHoldings
            {
                Name = "Thorvald", ServerId = 1304, SavedAt = DateTime.UtcNow.AddHours(-3),
                Bag = new() { [silentium] = 6, [ore] = 5, [aria] = 8, [refiningBound] = 9, [Aion2.SeasonMissions.OathCoin] = 12650 }, Storage = new() { [refining] = 22 },
            });
        }

        int scale = int.TryParse(Environment.GetEnvironmentVariable("AIONDPS_RENDER_SCALE"), out int sc) && sc is >= 1 and <= 4 ? sc : 1; // 2 = a sharp picture for the website
        var window = new Ui.ChecklistWindow();
        if (page == "roster")
        {
            window.ShowRoster();
        }
        else if (page.StartsWith("character", StringComparison.Ordinal))
        {
            window.ShowCharacter("Elyndra", page switch
            {
                "character-season" => Ui.ChecklistWindow.Section.Season,
                "character-supply" => Ui.ChecklistWindow.Section.Supply,
                "character-inventory" => Ui.ChecklistWindow.Section.Inventory,
                _ => Ui.ChecklistWindow.Section.Counters,
            });
        }

        var content = (System.Windows.FrameworkElement)window.Content;
        int w = (int)window.Width;
        content.Measure(new System.Windows.Size(w, double.PositiveInfinity));
        int h = (int)Math.Ceiling(content.DesiredSize.Height);
        content.Measure(new System.Windows.Size(w, h));
        content.Arrange(new System.Windows.Rect(0, 0, w, h));
        content.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        content.Measure(new System.Windows.Size(w, h));
        content.Arrange(new System.Windows.Rect(0, 0, w, h));
        content.UpdateLayout();
        var backdrop = new System.Windows.Controls.Border { Width = w, Height = h, Background = (System.Windows.Media.Brush)app.Resources["Brush.Window"] ?? System.Windows.Media.Brushes.Black };
        var bitmap = Shot(w * scale, h * scale, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        backdrop.Measure(new System.Windows.Size(w, h));
        backdrop.Arrange(new System.Windows.Rect(0, 0, w, h));
        bitmap.Render(backdrop);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine("render-checklist: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>Draws the assist window (Tools menu) with made-up cooldowns and effects into a picture; no window is ever shown.</summary>
    private static void RunRenderAssistMode(string path, string language)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        bool buffsTab = language == "buffs"; // the Buffs tab, in the saved language
        if (buffsTab)
        {
            language = "";
        }

        string chosen = language.Length > 0 && language != "overlays" ? language : settings.Language;
        if (chosen.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = chosen;
            Aion2.Protocol.Aion2SkillNames.Language = chosen;
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        var table = Aion2.Protocol.Aion2SkillNames.Load();
        // a Templar's skill bar (made-up times): its uses (the cast ids carry the specializations) start the cooldowns with the client's lengths (Taunt with
        // the specialization 4 25 s, Banner 60 s ...), the game's number for Punishment sets its time exactly; Vicious Strike (a basic attack) is never listed
        int[] bar = { 12350000, 12300000, 12120000, 12430000, 12100000, 12130000, 12260000, 12230000, 12270000, 12450000, 12090000, 12010000 };
        var now = DateTime.Now;
        Aion2.Aion2Cooldowns.Reset();
        (int Id, double Ago)[] uses = { (12350150, 19.6), (12120040, 5.3), (12100340, 6.4), (12260000, 14.2), (12230030, 18.5), (12450040, 20.1), (12090350, 4.2) };
        foreach ((int id, double ago) in uses)
        {
            Aion2.Aion2Cooldowns.NoteOwnCast(id, now.AddSeconds(-ago));
        }

        Aion2.Aion2Cooldowns.Receive(new List<Aion2.CooldownEntry> { new(12090000, 12_350) }, now);
        var effects = table.Where(kv => kv.Value.Contains("Armor") || kv.Value.Contains("Shield") || kv.Value.Contains("Boost") || kv.Value.Contains("Stance"))
            .Where(kv => Aion2.Protocol.Aion2SkillIcons.PathFor(kv.Key) is not null).GroupBy(kv => kv.Value).Select(g => g.First().Key).Take(3).ToList();
        var dir = new Aion2.Aion2EntityDirectory { LocalPlayerId = 8956 };
        dir.SetLocalBar(bar.ToHashSet(), bar);
        int[] lasts = { 18_000, 42_000, -1 };
        for (int i = 0; i < effects.Count; i++)
        {
            dir.Combat.NoteBuff(new Aion2.BuffNotice(now.AddSeconds(-5), 8956, 100 + i, effects[i] * 10, effects[i], lasts[i], 8956));
        }

        int scale = int.TryParse(Environment.GetEnvironmentVariable("AIONDPS_RENDER_SCALE"), out int sc) && sc is >= 1 and <= 4 ? sc : 1; // 2 = a sharp picture for the website
        if (language == "overlays")
        {
            // as the game shows them, no box around them: the cooldowns as bars (a column, as placed by default) and as tiles (a row), then the effects; above
            // them the bar shown at the top of the screen while they are being placed
            var page = new System.Windows.Controls.StackPanel { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3a, 0x45, 0x3c)) };
            var editRow = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            Ui.CooldownEditBar.Fill(editRow, Ui.CooldownArt.Tiles, true, _ => { }, () => { }, () => { }, () => { }, () => { });
            var editBar = Ui.CooldownEditBar.BarFrame(editRow);
            editBar.Margin = new System.Windows.Thickness(12, 12, 12, 0);
            editBar.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            page.Children.Add(editBar);
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            page.Children.Add(row);
            var shown = Aion2.Aion2Cooldowns.Skills(bar).Where(id => Ui.AssistPanel.Listed(id, bar)).ToList();
            var bars = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(12) };
            var tiles = new System.Windows.Controls.WrapPanel { Margin = new System.Windows.Thickness(12), MaxWidth = 6 * 44, VerticalAlignment = System.Windows.VerticalAlignment.Top };
            foreach (int id in shown)
            {
                var view = Aion2.Aion2Cooldowns.View(id, now);
                var barLook = new Ui.CooldownBar(id, 170) { Margin = new System.Windows.Thickness(0, 0, 0, 2) };
                barLook.Show(view);
                bars.Children.Add(barLook);
                var tile = new Ui.CooldownTile(id) { Margin = new System.Windows.Thickness(1) }; // as placed: a unit of edge and a gap of two between them
                tile.Show(view);
                tiles.Children.Add(tile);
            }

            row.Children.Add(bars);
            row.Children.Add(tiles);
            var overlayPanel = new Ui.AssistPanel(true) { Part = Ui.AssistPart.Effects, MinWidth = 44, Margin = new System.Windows.Thickness(12), VerticalAlignment = System.Windows.VerticalAlignment.Top };
            overlayPanel.Refresh(dir);
            row.Children.Add(overlayPanel);

            page.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            page.Arrange(new System.Windows.Rect(page.DesiredSize));
            page.UpdateLayout();
            var shot = Shot((int)Math.Ceiling(page.DesiredSize.Width) * scale, (int)Math.Ceiling(page.DesiredSize.Height) * scale, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
            shot.Render(page);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));
            using (var file = File.Create(path))
            {
                png.Save(file);
            }

            Console.WriteLine("render-assist: wrote the overlays to " + path);
            Environment.Exit(0);
        }

        var window = new Ui.AssistWindow(() => dir);
        window.ShowTab(buffsTab ? Ui.AssistWindow.BuffsTab : Ui.AssistWindow.CooldownsTab, remember: false);
        var content = (System.Windows.FrameworkElement)window.Content;
        int w = (int)window.Width;
        content.Measure(new System.Windows.Size(w, double.PositiveInfinity));
        int h = (int)Math.Ceiling(content.DesiredSize.Height);
        content.Measure(new System.Windows.Size(w, h));
        content.Arrange(new System.Windows.Rect(0, 0, w, h));
        content.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        content.Measure(new System.Windows.Size(w, h));
        content.Arrange(new System.Windows.Rect(0, 0, w, h));
        content.UpdateLayout();
        var backdrop = new System.Windows.Controls.Border { Width = w, Height = h, Background = (System.Windows.Media.Brush)app.Resources["Brush.Window"] ?? System.Windows.Media.Brushes.Black };
        var bitmap = Shot(w * scale, h * scale, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        backdrop.Measure(new System.Windows.Size(w, h));
        backdrop.Arrange(new System.Windows.Rect(0, 0, w, h));
        bitmap.Render(backdrop);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine("render-assist: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>Draws the world boss overlay with the bosses of the saved lists into a picture; no window is ever shown.</summary>
    private static void RunRenderBossOverlayMode(string path, int mapIndex)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        if (settings.Language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = settings.Language;
        }

        if (Environment.GetEnvironmentVariable("AIONDPS_SPEECH_ON") is { Length: > 0 })
        {
            settings.SpeechOn = true; // layout check only (nothing is saved here)
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        Aion2.Aion2FieldBosses.EnablePersistence();
        var now = DateTime.Now;
        string language = Ui.LocalizationManager.Instance.Language;
        var rows = new List<(string, string, bool, Aion2.FieldBoss?, Aion2.BossStatus?)>();
        var shown = Aion2.Aion2FieldBosses.Maps[mapIndex];
        foreach (var boss in shown.Bosses.Take(8))
        {
            var status = Aion2.Aion2FieldBosses.StatusOf(boss, now);
            rows.Add((Aion2.Aion2FieldBosses.DisplayName(shown, boss, language), Ui.WorldBossText.Of(status, now), status.Phase == Aion2.BossPhase.Now, boss, status));
        }

        var window = new Ui.WorldBossOverlayWindow();
        window.Render(rows.OrderBy(r => r.Item3 ? 0 : 1).ToList());
        var content = (System.Windows.UIElement)window.Content;
        content.Measure(new System.Windows.Size(600, 600));
        int w = (int)Math.Ceiling(content.DesiredSize.Width), h = (int)Math.Ceiling(content.DesiredSize.Height);
        content.Arrange(new System.Windows.Rect(0, 0, w, h));
        content.UpdateLayout();
        var backdrop = new System.Windows.Controls.Border { Width = w, Height = h, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2a, 0x36, 0x2c)), Child = null };
        var bitmap = Shot(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        backdrop.Measure(new System.Windows.Size(w, h));
        backdrop.Arrange(new System.Windows.Rect(0, 0, w, h));
        bitmap.Render(backdrop);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine("render-bossoverlay: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>Draws the network overlay with sample measurements into a picture (scale 2 = twice the pixels); no window is ever shown.</summary>
    /// <summary>Draws the loot tool (its page and its overlay) with made-up pickups into one picture; no window is ever shown, the loot log is not written.</summary>
    /// <summary>Draws the gathering overlay with a sample session (ore, herbs, Odyle at their grades) over a game-like strip; no window is ever shown.</summary>
    private static void RunRenderGatherOverlayMode(string path)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        Ui.ThemeManager.Apply(app, Ui.MeterSettings.Load());
        Ui.MainWindow.Headless = true;
        Aion2.Aion2GatherLog.Clear(false);
        // gatherings as the game tells them: a start, then what came of it; a few bring nothing (failed)
        DateTime at = DateTime.Now.AddHours(-1);
        int env = 1000;
        foreach ((int item, long amount, int times) in new[] { (610620013, 3L, 14), (610620014, 1L, 4), (610620015, 1L, 1), (610670041, 2L, 9), (610670045, 1L, 3),
                     (610610013, 2L, 6), (610610016, 1L, 1), (610660025, 1L, 4), (610680044, 1L, 3) })
        {
            string key = Aion2.Aion2GatherLog.FamilyOf(item)?.Key ?? "";
            for (int k = 0; k < times; k++)
            {
                Aion2.Aion2GatherLog.NoteAttempt(key, env++, at);
                Aion2.Aion2GatherLog.NoteGains(new[] { (item, amount) }, at.AddSeconds(3), Array.Empty<string>());
                at = at.AddSeconds(20);
            }
        }

        foreach (string key in new[] { "Orichalcum", "Orichalcum", "Aria" })
        {
            Aion2.Aion2GatherLog.NoteAttempt(key, env++, at);
            at = at.AddSeconds(20);
        }

        var window = new Ui.GatherOverlayWindow();
        window.Render(Aion2.Aion2GatherLog.Tallies(session: true), sample: false);
        var body = window.DetachBody()!;
        var holder = new System.Windows.Controls.Border { Child = body, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Width = 300 };
        var strip = new System.Windows.Controls.Border { Padding = new System.Windows.Thickness(20), Width = 360,
            Background = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x5a, 0x4e, 0x3a), System.Windows.Media.Color.FromRgb(0x24, 0x2c, 0x22), 60), Child = holder };
        strip.Measure(new System.Windows.Size(360, 2000));
        strip.Arrange(new System.Windows.Rect(0, 0, 360, strip.DesiredSize.Height));
        strip.UpdateLayout();
        var bitmap = Shot(360, (int)Math.Ceiling(strip.DesiredSize.Height), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(strip);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Aion2.Aion2GatherLog.Clear(false);
        Console.WriteLine("render-gather: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>Draws the season overlay with made-up progress (some missions picked, one finished) and the hover card of one of them beside it.</summary>
    /// <summary>The player window with a made-up character (no real player): its Overview, Gear (a piece opened), Genus &amp; Pets, and the same character as seen
    /// when its player doesn't use the app (no Genus &amp; Pets tab).</summary>
    private static void RunRenderPlayerMode(string path)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        Ui.ThemeManager.Apply(app, Ui.MeterSettings.Load());
        Ui.MainWindow.Headless = true;
        var who = new Upload.FoundPlayer("Valtheris", "Templar", 45, "Vaizel", 2302, "eu", 182_730, 2968, 21_480, "Thamon", "https://atreia.app/armory/?region=eu&server=Vaizel&name=Valtheris", 92, 3, 31);
        static Upload.PlayerLine L(string name, string value, string? grade = null, string? desc = null) => new(name, value, grade, null, desc);
        var none = Array.Empty<Upload.PlayerLine>();
        // the game's site names its pictures by the client's icon names: the app's own copies are drawn (nothing is downloaded here)
        static string I(string name) => "https://assets.playnccdn.com/static-aion2-gamedata/resources/" + name + ".png";
        var gear = new List<Upload.PlayerPiece>
        {
            new("MainHand", "Tempered Greatsword", "Unique", 15, 2, 3010, I("Icon_WP_SW_011_T01"), false, new[] { L("Attack", "912"), L("Accuracy", "120") },
                new[] { L("Critical Strike", "+64"), L("Boss damage", "+3.5%") }, new[] { L("Shield Bash", "+2") },
                new[] { L("Attack", "+12", "Legend"), L("Critical Strike", "+16", "Legend"), L("Accuracy", "+14", "Rare") }, new[] { L("Wrath", "", "Unique", "On hit, a 5% chance of a second strike") }),
            new("SubHand", "Tempered Bulwark", "Unique", 12, 0, 2990, I("Icon_Weapon_Guarder_U_005"), false, new[] { L("Defense", "740") }, new[] { L("Block", "+48") }, none, new[] { L("Defense", "+20", "Legend") }, none),
            new("Helmet", "Warden's Helm", "Legend", 10, 0, 2960, I("Icon_Equip_AR_L_0001_T04_Helmet"), false, new[] { L("Defense", "380") }, none, none, none, none),
            new("Torso", "Warden's Breastplate", "Legend", 10, 1, 2975, I("Icon_Equip_AR_L_0001_T04_Torso"), false, new[] { L("Defense", "520") }, none, none, none, none),
            new("Gloves", "Warden's Gauntlets", "Epic", 15, 3, 3040, I("Icon_Equip_AR_L_0001_T04_Gloves"), false, none, none, none, none, none),
            new("Necklace", "Pendant of the Pass", "Unique", 8, 0, 2950, I("Icon_ACC_NL_0011"), false, none, none, none, none, none),
            new("Arcana1", "Arcana of Valor", "Unique", 4, 0, 80, I("Icon_Arcana_Card_Bell_u_DefenceSet2"), true, none, none, none, none, none),
            new("Arcana2", "Arcana of the Wall", "Legend", 3, 0, 60, I("Icon_Arcana_Card_Compass_u_DefenceSet2"), true, none, none, none, none, none),
        };
        var stats = new List<(string, string)>
        {
            ("Attack", "2,412"), ("Defense", "3,180"), ("Accuracy", "1,955"), ("Evasion", "1,204"), ("Critical Strike", "1,066"), ("Critical resist", "742"),
            ("HP", "48,230"), ("MP", "6,400"), ("Boss damage", "8.5%"), ("PvE damage", "6.0%"), ("Block", "1,480"), ("Parry", "960"),
        };
        var skills = new List<Upload.PlayerSkill>
        {
            new("Shield Bash", 20, "Active", true, I("ICON_TE_SKILL_001")), new("Punishment", 18, "Active", true, I("ICON_TE_SKILL_002")), new("Divine Fury", 16, "Active", true, I("ICON_TE_SKILL_003")),
            new("Aegis", 15, "Active", true, I("ICON_TE_SKILL_004")), new("Iron Skin", 12, "Passive", false, I("ICON_TE_SKILL_006")), new("Taunt", 10, "Active", false, I("ICON_TE_SKILL_007")),
        };
        var parses = new List<Upload.PlayerParse> { new("Thamon", 21_480, 92, 3, 31), new("Kromede", 18_210, 77, 9, 40), new("Debilkarim", 15_400, 51, 22, 45) };
        var boards = new List<Upload.PlayerBoard> { new("Nezekan", 48, 60), new("Zikel", 36, 60), new("Vaizel", 22, 60), new("Triniel", 9, 60) };
        int Stat(string token) => Aion2.Protocol.Aion2CpData.StatId(token);
        var genus = new List<Upload.PlayerGenus>
        {
            new(2, 9, new[] { (1, 0, Stat("Critical"), 64L, 4), (1, 1, Stat("Accuracy"), 52L, 3), (1, 2, Stat("AmplifyCriticalDamage"), 180L, 5), (1, 3, Stat("HPMax"), 900L, 2) }),
            new(3, 7, new[] { (1, 0, Stat("BossNpcAddDamage"), 40L, 3), (1, 1, Stat("Defense"), 120L, 2), (1, 2, Stat("PvEAddDamage"), 36L, 4) }),
            new(4, 6, new[] { (1, 0, Stat("AmplifyBackAttack"), 120L, 3), (1, 1, Stat("Evasion"), 44L, 1) }),
            new(5, 4, new[] { (1, 0, Stat("HPRegen"), 30L, 2) }),
        };
        var pets = new List<(int, int)>();
        foreach (string species in Aion2.Protocol.Aion2Pets.Species)
        {
            int n = 0;
            foreach (int id in Aion2.Protocol.Aion2Pets.AllPetIds.Where(id => Aion2.Protocol.Aion2Pets.SpeciesOf(id) == species).OrderBy(id => id).Take(14))
            {
                pets.Add((id, n % 5 == 4 ? 1 : n % 3 == 2 ? 2 : 3));
                n++;
            }
        }

        DateTime now = DateTime.UtcNow;
        var page = new Upload.PlayerPage("Valtheris", "Templar", 45, "Vaizel", 2302, "eu", "Asmodian", "Wanderers of the Pass", "Daeva of the Pass", 182_730, 2968, gear, stats, skills,
            214, 380, ("Bolt", 3), ("Wings of the Pass", "Epic"), boards, parses, new Upload.PlayerAppData(genus, pets, now.AddMinutes(-42)), who.Page, now.AddMinutes(-3));
        Ui.PlayerView View(Upload.PlayerPage shown, string tab)
        {
            var view = new Ui.PlayerView(who, () => { }, () => { });
            view.ShowAll();
            view.Show(shown);
            view.SelectTab(tab);
            view.Margin = new System.Windows.Thickness(0, 0, 18, 18);
            view.VerticalAlignment = System.Windows.VerticalAlignment.Top;
            return view;
        }

        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        grid.Children.Add(View(page, "Overview"));
        var gearView = View(page, "Gear");
        grid.Children.Add(gearView);
        grid.Children.Add(View(page, "Genus & Pets"));
        grid.Children.Add(View(page, "Skills"));
        grid.Children.Add(View(page with { App = null }, "Overview"));
        var strip = new System.Windows.Controls.Border { Padding = new System.Windows.Thickness(20, 20, 2, 2), Child = grid,
            Background = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x4a, 0x56, 0x3a), System.Windows.Media.Color.FromRgb(0x22, 0x2c, 0x30), 60) };
        strip.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        strip.Arrange(new System.Windows.Rect(0, 0, strip.DesiredSize.Width, strip.DesiredSize.Height));
        strip.UpdateLayout();
        gearView.OpenFirstPiece(); // a piece opened, as after a click
        strip.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        strip.Arrange(new System.Windows.Rect(0, 0, strip.DesiredSize.Width, strip.DesiredSize.Height));
        strip.UpdateLayout();
        int width = (int)Math.Ceiling(strip.DesiredSize.Width), height = (int)Math.Ceiling(strip.DesiredSize.Height);
        var bitmap = Shot(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(strip);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine($"render-player: wrote {path} ({width}x{height})");
        Environment.Exit(0);
    }

    private static void RunRenderSeasonMode(string path)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        Ui.ThemeManager.Apply(app, Ui.MeterSettings.Load());
        Ui.MainWindow.Headless = true;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var counters = new Dictionary<int, Aion2.AchievementValue>
        {
            [233210800] = new(1240, 1, nowMs), [234210900] = new(19, 9, nowMs), [234211000] = new(58, 9, nowMs), [230211100] = new(451, 2, nowMs),
            [230211000] = new(20, 3, nowMs), [232211100] = new(1, 1, nowMs), [239241900] = new(3, 1, nowMs),
        };
        var all = Aion2.SeasonMissions.Progress('L', counters, Aion2.ChecklistManual.LastWeeklyReset(DateTime.Now));
        var chosen = new List<int> { 233210800, 234210900, 230211100, 234211000, 230211000, 232211100 };
        var list = Aion2.SeasonMissions.ForOverlay(all, chosen);
        var window = new Ui.SeasonOverlayWindow();
        window.Render(list, TimeSpan.FromHours(21), sample: false);
        var body = window.DetachBody()!;
        var holder = new System.Windows.Controls.Border { Child = body, Width = 300, VerticalAlignment = System.Windows.VerticalAlignment.Top };
        var card = Ui.SeasonLook.Card(list[0], "en");
        card.Margin = new System.Windows.Thickness(10, 30, 0, 0);
        card.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(holder);
        row.Children.Add(card);
        var strip = new System.Windows.Controls.Border { Padding = new System.Windows.Thickness(20), Width = 760, Child = row,
            Background = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x4a, 0x56, 0x3a), System.Windows.Media.Color.FromRgb(0x22, 0x2c, 0x30), 60) };
        strip.Measure(new System.Windows.Size(760, 2000));
        strip.Arrange(new System.Windows.Rect(0, 0, 760, strip.DesiredSize.Height));
        strip.UpdateLayout();
        var bitmap = Shot(760, (int)Math.Ceiling(strip.DesiredSize.Height), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(strip);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine("render-season: wrote " + path);
        Environment.Exit(0);
    }

    private static void RunRenderLootMode(string path, string language)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        if (language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = language;
        }
        else if (settings.Language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = settings.Language;
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        DateTime t = DateTime.Now.AddMinutes(-12);
        Aion2.Aion2Loot.Clear();
        Aion2.Aion2Loot.ClearMobs();
        // the monsters of the sample: Kobold Workers in the open world, Enhanced Harcon in a dungeon; the player is entity 1
        var npcs = new Dictionary<int, int>();
        void Pick(uint id, int item, uint count, int seconds, int monster = 0)
        {
            Aion2.Aion2Loot.NoteGroundItem(Combat.SelfCheckLoot.Ground(id, item, count, monster), e => npcs.TryGetValue(e, out int n) ? n : null);
            Aion2.Aion2Loot.NoteInventory(Combat.SelfCheckLoot.Inventory(0, item), t.AddSeconds(seconds));
            Aion2.Aion2Loot.NoteGroundGone(Combat.SelfCheckLoot.Gone(id, 1), t.AddSeconds(seconds).AddMilliseconds(60));
        }

        void Kill(int entity, int npc, int seconds)
        {
            npcs[entity] = npc;
            DateTime at = t.AddSeconds(seconds);
            Aion2.Aion2Loot.NoteCombat(new[] { new Combat.DamageEvent(at, 1, entity, 5000, false) }, new[] { new Combat.Sources.KillEvent(at.AddSeconds(1), 1, entity, false) },
                id => id == 1, e => npcs.TryGetValue(e, out int n) ? n : null);
        }

        Aion2.Aion2Loot.NoteZone(1010);
        uint loot = 1;
        for (int k = 0; k < 48; k++)
        {
            Kill(5000 + k, 2090666, k * 8);
            if (k % 4 == 0)
            {
                Pick(loot++, 534660001, k % 8 == 0 ? 2u : 1u, k * 8 + 2, 5000 + k);
            }

            if (k == 13 || k == 37)
            {
                Pick(loot++, 513560001, 3, k * 8 + 3, 5000 + k);
            }

            // gear: two gold pieces (a greatsword, a breastplate: one "Gold gear" line), a blue and a green one
            int gear = k switch { 11 => 110120003, 37 => 210130001, 25 => 210140002, 15 => 210150003, _ => 0 };
            if (gear > 0)
            {
                Pick(loot++, gear, 1, k * 8 + 4, 5000 + k);
            }
        }

        Aion2.Aion2Loot.NoteZone(20);
        Kill(6001, 2300104, 420);
        Pick(loot++, 513551001, 1, 422, 6001);
        Pick(loot++, 534660002, 4, 500);
        // back on the first map: its session carries on (a second visit), it comes back to the top
        Aion2.Aion2Loot.NoteZone(1010);
        Kill(5100, 2090666, 560);
        Pick(loot++, 534660001, 1, 562, 5100);
        // three Hidden Cubes on the way (their loot goes to the cube tab only)
        void Cube(int entity, int seconds, params (int Item, uint Count)[] items)
        {
            foreach ((int item, uint count) in items)
            {
                uint id = loot++;
                Aion2.Aion2Loot.NoteGroundItem(Combat.SelfCheckLoot.Ground(id, item, count, entity), null, e => e >= 9000);
                Aion2.Aion2Loot.NoteInventory(Combat.SelfCheckLoot.Inventory(0, item), t.AddSeconds(seconds));
                Aion2.Aion2Loot.NoteGroundGone(Combat.SelfCheckLoot.Gone(id, 1), t.AddSeconds(seconds).AddMilliseconds(60));
            }
        }

        Aion2.Aion2Loot.ClearCubes();
        Cube(9001, 120, (535400001, 5), (533460001, 180), (531900009, 1000), (511490001, 1), (630840002, 11), (503700032, 1));
        Cube(9002, 300, (535400001, 5), (533460004, 100), (531900009, 1000), (530126005, 5), (532760001, 1), (630840002, 12));
        Cube(9003, 580, (535400001, 5), (533460004, 140), (531900009, 1000), (511060025, 1), (535200001, 1), (515910009, 1));
        var overlay = Ui.LootText.OverlayLines(Aion2.Aion2Loot.Current, Aion2.Aion2Loot.CurrentZone, settings, sample: false);
        var window = new Ui.LootOverlayWindow();
        window.Render(overlay);
        var overlayContent = window.DetachBody()!;
        var windowBrush = (System.Windows.Media.Brush)app.Resources["Brush.Window"] ?? System.Windows.Media.Brushes.Black;
        var root = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical, Width = 960, Background = windowBrush };
        // the three tabs as the page shows them, each at the width of the tools pane (Overview inside the module page with its header)
        var page = new Ui.ModulePage(Ui.LocalizationManager.Instance["Main.MenuTools.Loot"], new Ui.LootPage(), s => true, (s, v) => { }, s => "Ctrl+Alt+L", () => { }, () => { }, s => { }) { FitWidth = true };
        root.Children.Add(new System.Windows.Controls.Border { Width = 960, Padding = new System.Windows.Thickness(20, 16, 20, 20), Child = page });
        var sessionsPage = new Ui.LootPage();
        sessionsPage.ShowSessionsTab();
        root.Children.Add(new System.Windows.Controls.Border { Width = 960, Padding = new System.Windows.Thickness(20, 4, 20, 20), Child = sessionsPage });
        var ratesPage = new Ui.LootPage();
        ratesPage.ShowRatesTab();
        root.Children.Add(new System.Windows.Controls.Border { Width = 960, Padding = new System.Windows.Thickness(20, 4, 20, 20), Child = ratesPage });
        var cubesPage = new Ui.LootPage();
        cubesPage.ShowCubesTab();
        root.Children.Add(new System.Windows.Controls.Border { Width = 960, Padding = new System.Windows.Thickness(20, 4, 20, 20), Child = cubesPage });
        var strip = new System.Windows.Controls.Border { Padding = new System.Windows.Thickness(20), Background = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x3a, 0x4a, 0x52), System.Windows.Media.Color.FromRgb(0x1c, 0x26, 0x2e), 60) };
        var holder = new System.Windows.Controls.Border { Child = overlayContent, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Width = 280 };
        // beside it: the overlay switched on before the first drop (the map and a line, not nothing)
        var waitingWindow = new Ui.LootOverlayWindow();
        waitingWindow.Render(Ui.LootText.OverlayLines(null, 20, settings, sample: false));
        var pair = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        pair.Children.Add(holder);
        pair.Children.Add(new System.Windows.Controls.Border { Child = waitingWindow.DetachBody()!, Width = 280, Margin = new System.Windows.Thickness(24, 0, 0, 0), VerticalAlignment = System.Windows.VerticalAlignment.Top });
        strip.Child = pair;
        root.Children.Add(strip);
        root.Measure(new System.Windows.Size(960, 6000));
        root.Arrange(new System.Windows.Rect(0, 0, 960, root.DesiredSize.Height));
        root.UpdateLayout();
        int w = 960, h = (int)Math.Ceiling(root.DesiredSize.Height);
        var bitmap = Shot(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Aion2.Aion2Loot.Clear();
        Aion2.Aion2Loot.ClearMobs();
        Aion2.Aion2Loot.ClearCubes();
        Console.WriteLine("render-loot: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>Draws the attribute tool (ring or stat tab) with sample values into a picture; no window is ever shown.</summary>
    private static void RunRenderAttributesMode(string path, bool stats, string language)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        if (language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = language;
        }
        else if (settings.Language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = settings.Language;
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        var directory = new Aion2.Aion2EntityDirectory();
        directory.SetLocalAttributes(new Dictionary<int, int>
        {
            [1] = 34, [2] = 12, [4] = 38, [5] = 27, [6] = 9, [7] = 41, [8] = 54, [9] = 39, [10] = 46, [11] = 62, [13] = 24, [14] = 46, [15] = 25, [16] = 27, [17] = 28,
        });
        var page = new Ui.AttributesPage(() => directory);
        if (stats)
        {
            page.ShowStatsTab();
        }

        var host = new System.Windows.Controls.Border { Width = 1060, Height = 760, Child = page, Background = (System.Windows.Media.Brush)app.Resources["Brush.Window"] ?? System.Windows.Media.Brushes.Black };
        host.Measure(new System.Windows.Size(1060, 760));
        host.Arrange(new System.Windows.Rect(0, 0, 1060, 760));
        host.UpdateLayout();
        var bitmap = Shot(1060, 760, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        page.Stop();
        Console.WriteLine("render-attributes: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>Sample gear for the CP calculator's picture and self-test: a level-102 unique set with soul binding and stones (the stat ids the meter reads).</summary>
    internal static IReadOnlyList<Aion2.Aion2EquippedItem> CpSampleGear(int weaponEnchant)
    {
        Aion2.Aion2RolledStat S(int id, long v) => new(id, v);
        Aion2.Aion2Stone T(int id, int tier) => new(id, tier);
        return new List<Aion2.Aion2EquippedItem>
        {
            new(1, 110120003, weaponEnchant, new[] { T(317, 3), T(128, 2), T(0, 0) }, new[] { S(282, 1180), S(1, 25), S(128, 62), S(199, 140) }),
            new(3, 210330040, 10, new[] { T(193, 3), T(0, 0) }, new[] { S(193, 380), S(133, 40) }),
            new(4, 210430040, 9, new[] { T(307, 2), T(317, 1) }, new[] { S(317, 30), S(104, 70) }),
            new(5, 210130040, 11, new[] { T(193, 3), T(193, 3) }, new[] { S(193, 410), S(307, 120) }),
            new(6, 210230040, 8, new[] { T(116, 2) }, new[] { S(116, 60), S(193, 300) }),
            new(7, 210530040, 10, new[] { T(128, 3), T(317, 3) }, new[] { S(317, 40), S(128, 55) }),
            new(8, 210630040, 7, new[] { T(0, 0), T(0, 0) }, new[] { S(193, 280) }),
            new(9, 210730040, 10, new[] { T(193, 2) }, new[] { S(199, 120) }),
            new(10, 310130040, 12, new[] { T(317, 3), T(128, 3) }, new[] { S(317, 45), S(38, 90) }),
            new(11, 310230114, 11, new[] { T(104, 3) }, new[] { S(104, 75), S(193, 260) }),
            new(13, 310330040, 10, new[] { T(128, 3) }, new[] { S(317, 41), S(116, 50) }),
        };
    }

    /// <summary>The sample set's stones as the character page would name them (the weapon's and the necklace's; the rest stays a range).</summary>
    internal static IReadOnlyList<Upload.CpOfficialPiece> CpSampleOfficial() => new List<Upload.CpOfficialPiece>
    {
        new(110120003, 17, new[] { ("WeaponFixingDamage", 8), ("Critical", 15) }),
        new(310130040, 12, new[] { ("WeaponFixingDamage", 9), ("Critical", 21) }),
    };

    /// <summary>
    /// The rest of the sample character for the CP calculator's picture and self-test (invented, a level 45 Gladiator): skills with five equipped Stigmas, the first
    /// stat nodes of the Gladiator's Daevanion boards, five Genus Insight boards (Fera with a second preset), a pet collection, worn and owned titles, a wing,
    /// five Arcana cards and the attribute points.
    /// </summary>
    internal static Aion2.Aion2SavedCharacter CpSampleCharacter(IReadOnlyList<Aion2.Aion2EquippedItem> gear)
    {
        static Aion2.Aion2SavedCharacter.SavedSkill K(int id, int level, int trained, bool stigma = false, bool equipped = true, int[]? stones = null) => new(id, level, trained, stigma, equipped, stones);
        static Aion2.Aion2SavedCharacter.SavedEffect E(int page, int slot, int stat, long value, int kind) => new(page, slot, stat, value, kind);
        var skills = new List<Aion2.Aion2SavedCharacter.SavedSkill>
        {
            K(11010000, 18, 10, stones: new[] { 3, 4, 0 }), K(11020000, 16, 10, stones: new[] { 1, 5, 0 }), K(11030000, 14, 10, stones: new[] { 2, 3, 0 }),
            K(11040000, 16, 10, stones: new[] { 4, 1, 0 }), K(11050000, 15, 10, stones: new[] { 3, 2, 0 }), K(11060000, 12, 10, stones: new[] { 1, 2, 0 }),
            K(11080000, 13, 10, stones: new[] { 5, 3, 0 }), K(11090000, 20, 10, stones: new[] { 1, 2, 3 }), K(11100000, 17, 10, stones: new[] { 2, 4, 0 }),
            K(11110000, 11, 10, stones: new[] { 1, 0, 0 }), K(11170000, 19, 10, stones: new[] { 3, 5, 0 }), K(11200000, 7, 7, stones: new[] { 0, 0, 0 }),
            K(11140000, 5, 5, equipped: false),
            K(11710000, 16, 10), K(11720000, 15, 10), K(11730000, 14, 10), K(11740000, 18, 10), K(11750000, 12, 10), K(11760000, 16, 10), K(11770000, 10, 10), K(11780000, 8, 5),
            K(11250000, 20, 20, stigma: true, stones: new[] { 1, 2, 3, 4, 0 }), K(11270000, 16, 16, stigma: true, stones: new[] { 1, 2, 3, 0, 0 }),
            K(11310000, 15, 15, stigma: true, stones: new[] { 2, 3, 0, 0, 0 }), K(11380000, 12, 12, stigma: true, stones: new[] { 1, 0, 0, 0, 0 }),
            K(11290000, 5, 5, stigma: true, equipped: false),
        };
        var boards = new List<Aion2.Aion2SavedCharacter.SavedBoard>();
        foreach ((int board, int open) in new[] { (11, 46), (12, 52), (13, 30), (14, 61), (15, 12), (16, 20) })
        {
            var nodes = Aion2.Protocol.Aion2DaevanionCatalog.NodesOfBoard(board).OrderBy(n => n.Type == "Start" ? 0 : 1).ThenBy(n => n.Row).ThenBy(n => n.Col).Take(open + 1).Select(n => n.Id).ToList();
            boards.Add(new Aion2.Aion2SavedCharacter.SavedBoard(board, nodes));
        }

        var species = new List<Aion2.Aion2SavedCharacter.SavedSpecies>
        {
            new(2, 10, 0, new() { E(1, 0, 31, 15, 5), E(1, 1, 52, 139, 5), E(1, 2, 128, 15, 5), E(1, 3, 379, 296, 4), E(1, 4, 56, 19, 5), E(1, 5, 110, 31, 5), E(1, 6, 104, 33, 5), E(1, 7, 400, 10, 1), E(1, 8, 193, 22, 2) }),
            new(3, 10, 0, new() { E(1, 0, 56, 12, 5), E(1, 1, 193, 87, 5), E(1, 2, 104, 21, 5), E(1, 3, 32, 200, 4), E(1, 4, 19, 15, 5), E(1, 5, 408, 13, 2), E(1, 6, 70, 252, 5), E(1, 7, 405, 10, 2), E(1, 8, 104, 17, 4),
                E(2, 0, 98, 12, 4), E(2, 1, 199, 10, 2), E(2, 2, 406, 12, 2), E(2, 3, 408, 14, 2), E(2, 4, 50, 14, 4), E(2, 5, 406, 7, 2), E(2, 6, 444, 281, 5), E(2, 7, 408, 12, 2), E(2, 8, 110, 26, 3) }),
            new(4, 10, 0, new() { E(1, 0, 31, 13, 5), E(1, 1, 51, 104, 5), E(1, 2, 591, 17, 3), E(1, 3, 379, 272, 5), E(1, 4, 50, 15, 5), E(1, 5, 416, 14, 2), E(1, 6, 52, 155, 5), E(1, 7, 193, 59, 3), E(1, 8, 128, 17, 5) }),
            new(5, 9, 41_000, new() { E(1, 0, 31, 9, 3), E(1, 1, 588, 106, 3), E(1, 2, 104, 19, 4), E(1, 3, 379, 153, 3), E(1, 4, 50, 17, 5), E(1, 5, 104, 22, 5), E(1, 6, 104, 12, 2), E(1, 7, 51, 140, 5), E(1, 8, 128, 8, 2) }),
            new(6, 10, 0, new() { E(1, 0, 19, 8, 5), E(1, 1, 588, 40, 2), E(1, 2, 110, 46, 5), E(1, 3, 443, 181, 5), E(1, 4, 38, 20, 5), E(1, 5, 104, 36, 5), E(1, 6, 104, 15, 3), E(1, 7, 199, 13, 2), E(1, 8, 128, 26, 5) }),
        };
        var pets = Aion2.Protocol.Aion2Pets.AllPetIds.OrderBy(id => id).Where(id => id % 11 != 0)
            .Select(id => new Aion2.Aion2SavedCharacter.SavedPet(id, id % 5 == 0 ? 3 : id % 3 == 0 ? 2 : 1, id % 5 == 0 ? 0 : id % 3 == 0 ? id % 75 : id % 25)).ToList();
        var titles = Aion2.Protocol.Aion2CpSources.TitleIds.OrderBy(id => id).ToList();
        int Pick(string category) => titles.Select(Aion2.Protocol.Aion2CpSources.Title).Where(t => t!.Category == category && t.Grade == "Unique").Select(t => t!.Id).FirstOrDefault();
        return new Aion2.Aion2SavedCharacter
        {
            Name = "Ashveil", ClassCode = 6, Level = 45, ServerId = 2001, SavedAt = DateTime.Now,
            Equipment = gear.Select(i => new Aion2.Aion2SavedCharacter.SavedItem(i.SlotIndex, i.ItemId, i.Enchant, i.Stones, i.Stats, i.GodstoneId, i.SkillBonuses, i.Arcana)).ToList(),
            Skills = skills, Daevanion = boards,
            Species = species.Select(k => k).ToList(),
            Titles = new() { new(1, Pick("Attack")), new(3, Pick("Etc")) },
            OwnedTitles = titles.Where(id => id % 3 == 0).Take(140).Concat(new[] { Pick("Attack"), Pick("Etc"), Pick("Defense") }).Distinct().ToList(),
            Pets = pets,
            Attributes = new() { [1] = 46, [2] = 30, [3] = 0, [4] = 12, [5] = 40, [6] = 0, [7] = 51, [8] = 36, [9] = 25, [10] = 54, [11] = 76, [12] = 0, [13] = 73, [14] = 66, [15] = 51, [16] = 31, [17] = 28 },
            WingId = 40201900, WingSkinId = 0,
            OwnedWings = Aion2.Protocol.Aion2CpSources.WingIds.OrderBy(id => id).Where(id => id / 10_000_000 == 4).Take(9).Append(40201900).Distinct().ToList(),
        };
    }

    /// <summary>The sample character's five Arcana cards (deity value and the skill levels they add).</summary>
    internal static IReadOnlyList<Aion2.Aion2EquippedItem> CpSampleArcana() => new List<Aion2.Aion2EquippedItem>
    {
        new(41, 810130001, 5, Arcana: new Aion2.Aion2ArcanaDetails(0, 11, 25, new[] { new Aion2.Aion2SkillBonus(11090000, 2), new Aion2.Aion2SkillBonus(11170000, 3) })),
        new(42, 810230001, 5, Arcana: new Aion2.Aion2ArcanaDetails(0, 10, 25, new[] { new Aion2.Aion2SkillBonus(11010000, 3) })),
        new(43, 810330002, 0, Arcana: new Aion2.Aion2ArcanaDetails(0, 14, 20, new[] { new Aion2.Aion2SkillBonus(11040000, 1) })),
        new(44, 810430002, 2, Arcana: new Aion2.Aion2ArcanaDetails(2000, 13, 22, new[] { new Aion2.Aion2SkillBonus(11740000, 2) })),
        new(45, 810530002, 0, Arcana: new Aion2.Aion2ArcanaDetails(0, 15, 20, new[] { new Aion2.Aion2SkillBonus(11760000, 1) })),
    };

    private static void RunRenderCpCalcMode(string path, string language, string tab)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        if (language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = language;
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        var directory = new Aion2.Aion2EntityDirectory();
        // the weapon at +15 with breakthrough 2 (the game sends 17), its stones' values as the character page names them
        var frame = new Dictionary<int, int>
        {
            [31] = 1250, [317] = 1350, [19] = 320, [128] = 1100, [104] = 1900, [193] = 32000, [52] = 3800, [307] = 1500,
            [1] = 82, [13] = 31, [5] = 64, [14] = 40, [8] = 36, [4] = 41, [10] = 38,
        };
        Dictionary<int, int> Shift(Dictionary<int, int> from, IReadOnlyList<Aion2.Aion2EquippedItem> was, IReadOnlyList<Aion2.Aion2EquippedItem> becomes, int sign)
        {
            var a = Combat.CpCalc.StatSums(Combat.CpCalc.Pieces(was, new Aion2.Protocol.CpTotals(0, 0, 0, 0, 0)));
            var b = Combat.CpCalc.StatSums(Combat.CpCalc.Pieces(becomes, new Aion2.Protocol.CpTotals(0, 0, 0, 0, 0)));
            var f = new Dictionary<int, int>(from);
            foreach (int id in a.Keys.Union(b.Keys))
            {
                f[id] = f.GetValueOrDefault(id) + sign * (int)(b.GetValueOrDefault(id) - a.GetValueOrDefault(id));
            }

            return f;
        }

        // two earlier logins with the weapon's breakthrough 1 -> 2 in between: the game's Combat Power moved 4 % more than the calculator said
        DateTime utcNow = DateTime.UtcNow;
        var earlier = Shift(frame, CpSampleGear(16), CpSampleGear(17), -1);
        var p0 = Combat.CpChecks.Point(utcNow.AddHours(-26), 140_100, 3005, CpSampleGear(16), earlier, "rest");
        var probe = Combat.CpChecks.Point(utcNow.AddHours(-2), 0, 3010, CpSampleGear(17), frame, "rest");
        double said = Combat.CpChecks.Predicted(p0.Gear, probe.Gear, frame, 3010, null).Parts.Values.Sum();
        var p1 = probe with { Cp = 140_100 + (int)Math.Round(said * 1.04) };
        var state = new Combat.CpCheckState { Points = new() { p0, p1 } };
        if (Combat.CpChecks.Measure(p0, p1) is { } measured)
        {
            state.Measured.Add(measured);
        }

        Combat.CpChecks.Put("Ashveil", 2001, state, new Upload.CpOfficialRead(CpSampleOfficial(), p1.Cp + 120, 3010, utcNow.AddHours(-1)));
        directory.RestoreFrom(CpSampleCharacter(CpSampleGear(17).Concat(CpSampleArcana()).ToList()));
        directory.SetLocalCharacter(new Aion2.Aion2CharacterInfo(331, "Ashveil", 6, 45, CpSampleGear(17), DateTime.Now, ServerId: 2001));
        directory.SetLocalStats(frame);
        directory.SetLocalEquipment(CpSampleGear(17).Concat(CpSampleArcana()).ToList());

        // what the sample character holds (invented): Kina, Enhance Stones and Amplify Stones for the steps' costs
        Aion2.Aion2Inventories.Reset();
        Aion2.Aion2Inventories.Put(new Aion2.CharacterHoldings
        {
            Name = "Ashveil", ServerId = 2001, SavedAt = DateTime.UtcNow,
            Bag = new() { [930100031] = 24_500_000, [930100023] = 61_200, [516530001] = 34 },
        });
        var page = new Ui.CpCalcPage(() => directory);
        page.ShowTab(tab);
        // breakthrough 3 while the page is open, and the game's stat update with it: the change shows at the bottom, checked against the stats
        directory.SetLocalEquipment(CpSampleGear(18).Concat(CpSampleArcana()).ToList());
        directory.SetLocalStats(Shift(frame, CpSampleGear(17), CpSampleGear(18), +1));
        page.Refresh();
        const int width = 1180;
        int height = tab == "gear" ? 2300 : 1900;
        var host = new System.Windows.Controls.Border { Width = width, Height = height, Child = page, Background = (System.Windows.Media.Brush)app.Resources["Brush.Window"] ?? System.Windows.Media.Brushes.Black };
        host.Resources[typeof(System.Windows.Controls.Button)] = System.Windows.Markup.XamlReader.Parse(
            "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"Button\">" +
            "<Setter Property=\"Background\" Value=\"{DynamicResource Brush.Control}\"/><Setter Property=\"Foreground\" Value=\"{DynamicResource Brush.Text}\"/>" +
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Brush.Border}\"/><Setter Property=\"BorderThickness\" Value=\"1\"/>" +
            "<Setter Property=\"Template\"><Setter.Value><ControlTemplate TargetType=\"Button\"><Border x:Name=\"Bd\" Background=\"{TemplateBinding Background}\" BorderBrush=\"{TemplateBinding BorderBrush}\" BorderThickness=\"{TemplateBinding BorderThickness}\" Padding=\"{TemplateBinding Padding}\"><ContentPresenter HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"/></Border>" +
            "<ControlTemplate.Triggers><Trigger Property=\"IsEnabled\" Value=\"False\"><Setter TargetName=\"Bd\" Property=\"Opacity\" Value=\"0.4\"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        host.Measure(new System.Windows.Size(width, height));
        host.Arrange(new System.Windows.Rect(0, 0, width, height));
        host.UpdateLayout();
        var bitmap = Shot(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        page.Stop();
        Console.WriteLine("render-cpcalc: wrote " + path);
        Environment.Exit(0);
    }

    private static void RunRenderStatCalcMode(string path, bool expert, string language)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        if (language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = language;
        }
        else if (settings.Language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = settings.Language;
        }

        settings.StatCalcExpert = expert;
        settings.StatCalcSide = "back";
        settings.StatCalcSideRate = 62;
        settings.Save();
        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        var directory = new Aion2.Aion2EntityDirectory();
        directory.SetLocalStats(new Dictionary<int, int>
        {
            [19] = 277, [317] = 1050, [31] = 542, [33] = 431, [425] = 337, [32] = 1000, [28] = 500, [379] = 1500, [44] = 600, [442] = 1750, [443] = 620, [589] = 560, [102] = 93, [56] = 259, [50] = 55,
        });
        var page = new Ui.StatCalcPage(() => directory, () => Array.Empty<Combat.DamageEvent>());
        page.ApplyAssumptions(34, Combat.AttackSide.Back, 62);
        page.ApplyChange(Combat.StatKey.GearAttack, 120);
        page.ApplyChange(Combat.StatKey.CritAmp, -2);
        page.ApplyChange(Combat.StatKey.AllAmp, 2.5);
        int height = expert ? 1000 : 760;
        var host = new System.Windows.Controls.Border { Width = 1180, Height = height, Child = page, Background = (System.Windows.Media.Brush)app.Resources["Brush.Window"] ?? System.Windows.Media.Brushes.Black };
        // the button style the tools window takes over from the main window (see ThemedChrome); the picture has no main window
        host.Resources[typeof(System.Windows.Controls.Button)] = System.Windows.Markup.XamlReader.Parse(
            "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"Button\">" +
            "<Setter Property=\"Background\" Value=\"{DynamicResource Brush.Control}\"/><Setter Property=\"Foreground\" Value=\"{DynamicResource Brush.Text}\"/>" +
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Brush.Border}\"/><Setter Property=\"BorderThickness\" Value=\"1\"/>" +
            "<Setter Property=\"Template\"><Setter.Value><ControlTemplate TargetType=\"Button\"><Border x:Name=\"Bd\" Background=\"{TemplateBinding Background}\" BorderBrush=\"{TemplateBinding BorderBrush}\" BorderThickness=\"{TemplateBinding BorderThickness}\" Padding=\"{TemplateBinding Padding}\"><ContentPresenter HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"/></Border>" +
            "<ControlTemplate.Triggers><Trigger Property=\"IsEnabled\" Value=\"False\"><Setter TargetName=\"Bd\" Property=\"Opacity\" Value=\"0.4\"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        host.Measure(new System.Windows.Size(1180, height));
        host.Arrange(new System.Windows.Rect(0, 0, 1180, height));
        host.UpdateLayout();
        var bitmap = Shot(1180, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        page.Stop();
        Console.WriteLine("render-statcalc: wrote " + path);
        Environment.Exit(0);
    }

    private static void RunRenderNetworkMode(string path, int scale)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        if (settings.Language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = settings.Language;
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        var sample = new Aion2.Capture.NetworkSnapshot(true, "203.0.113.24:13328", TimeSpan.FromSeconds(642), 1, 0, 7, 16, 6, 49, 4.2, 8.6, 0.2, 26, 31, 0, 0, TimeSpan.FromSeconds(0.4), 0, 0,
            0, 0, null, null, null, new Aion2.Capture.NetworkProbeSnapshot(true, "192.168.2.1", 1, 0, 60, 12, 0, 60));
        var window = new Ui.NetworkOverlayWindow();
        window.Render(Ui.NetworkOverlayRows.Of(sample, Array.Empty<string>()));
        var content = (System.Windows.UIElement)window.Content;
        content.Measure(new System.Windows.Size(600, 600));
        int w = (int)Math.Ceiling(content.DesiredSize.Width), h = (int)Math.Ceiling(content.DesiredSize.Height);
        content.Arrange(new System.Windows.Rect(0, 0, w, h));
        content.UpdateLayout();
        var backdrop = new System.Windows.Controls.Border
        {
            Width = w, Height = h,
            Background = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x3a, 0x4a, 0x52), System.Windows.Media.Color.FromRgb(0x1c, 0x26, 0x2e), 60),
        };
        var bitmap = Shot(w * scale, h * scale, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        backdrop.Measure(new System.Windows.Size(w, h));
        backdrop.Arrange(new System.Windows.Rect(0, 0, w, h));
        bitmap.Render(backdrop);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine("render-network: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>Draws the combat overlay into a picture with made-up fight data ("live") or as it looks while it is being arranged ("edit"); no window is ever shown,
    /// and the settings come from a throw-away folder so nothing of the player's own is read or written.</summary>
    private static void RunRenderCombatOverlayMode(string path, string mode, int scale)
    {
        Environment.SetEnvironmentVariable("AIONDPS_DATA_DIR", Path.Combine(Path.GetTempPath(), "aiondps-render-combat"));
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = new Ui.MeterSettings { CombatShowHealer = true, ShowBossHp = true };
        if (Environment.GetEnvironmentVariable("AIONDPS_LANG") is { Length: > 0 } language)
        {
            Ui.LocalizationManager.Instance.Language = language;
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        bool edit = mode == "edit";
        var windows = new[] { new Ui.CombatOverlayWindow(Ui.CombatOverlayWindow.Part.Cast), new Ui.CombatOverlayWindow(Ui.CombatOverlayWindow.Part.Target) };
        foreach (var window in windows)
        {
            window.ApplyLocked(!edit);
            window.SetEditing(edit);
        }

        string bossName = Aion2.Protocol.Aion2Npcs.NameOf(2300708, Ui.LocalizationManager.Instance.Language) ?? "Ruthilis of Pain";
        string? bossPicture = Aion2.Protocol.Aion2Artwork.BossThumbPath(2300708);
        var frame = edit
            ? new Ui.CombatFrame(Array.Empty<Ui.CastRowData>(), null)
            : new Ui.CombatFrame(
                new[]
                {
                    new Ui.CastRowData(Aion2.Protocol.Aion2SkillNames.Display("Attack"), bossName, Ui.LocalizationManager.Instance["Combat.You"], true, false, false, 900, 1500, 650, null, Generic: true),
                    new Ui.CastRowData(Aion2.Protocol.Aion2SkillNames.DisplayOf(1801180), bossName, Ui.LocalizationManager.Instance["Combat.AllAround"], false, true, true, 400, 1900, null, null),
                },
                new Ui.TargetOfTargetData("Plague", bossName, Aion2.Protocol.Aion2Artwork.ClassEmblemPath("Cleric"), false, 12480, 20563,
                    new[]
                    {
                        new Ui.EffectData("Poison", null, "12", true),
                        new Ui.EffectData("Slow", null, "5", true),
                        new Ui.EffectData("Bleed", null, "9", true),
                    }),
                new Ui.BossHpData(1224000, 3400000, 1, "✓", Since: DateTime.Now - TimeSpan.FromSeconds(Environment.GetEnvironmentVariable("AIONDPS_FIGHT_SECONDS") is { Length: > 0 } sec ? int.Parse(sec) : 151)));

        // the two windows one above the other, each as big as it wants to be
        var contents = new List<(System.Windows.UIElement Content, int Width, int Height)>();
        foreach (var window in windows)
        {
            window.Render(frame, settings);
            var part = (System.Windows.UIElement)window.Content;
            part.Measure(new System.Windows.Size(700, 500));
            int pw = (int)Math.Ceiling(part.DesiredSize.Width), ph = (int)Math.Ceiling(part.DesiredSize.Height);
            part.Arrange(new System.Windows.Rect(0, 0, pw, ph));
            part.UpdateLayout();
            contents.Add((part, pw, ph));
        }

        int w = contents.Max(c => c.Width), h = contents.Sum(c => c.Height) + 8 * (contents.Count - 1);
        var backdrop = new System.Windows.Controls.Border
        {
            Width = w, Height = h,
            Background = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x3a, 0x4a, 0x52), System.Windows.Media.Color.FromRgb(0x1c, 0x26, 0x2e), 60),
        };
        var bitmap = Shot(w * scale, h * scale, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        backdrop.Measure(new System.Windows.Size(w, h));
        backdrop.Arrange(new System.Windows.Rect(0, 0, w, h));
        if (Environment.GetEnvironmentVariable("AIONDPS_TRANSPARENT") != "1") // 1 = only the parts, on a see-through ground (for the website)
        {
            bitmap.Render(backdrop);
        }

        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double y = 0;
            foreach (var (part, pw, ph) in contents)
            {
                dc.DrawRectangle(new System.Windows.Media.VisualBrush(part), null, new System.Windows.Rect(0, y, pw, ph));
                y += ph + 8;
            }
        }

        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine("render-combatoverlay: wrote " + path + $" ({w}x{h}), boss picture: {(bossPicture is null ? "none" : "yes")}");
        Environment.Exit(0);
    }

    /// <summary>Draws the replay window at one moment of a made-up boss fight (eight players around a boss that winds up areas); no window is ever shown.</summary>
    private static void RunRenderReplayMode(string path, double seconds, int scale)
    {
        Environment.SetEnvironmentVariable("AIONDPS_DATA_DIR", Path.Combine(Path.GetTempPath(), "aiondps-render-replay"));
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        if (Environment.GetEnvironmentVariable("AIONDPS_LANG") is { Length: > 0 } language)
        {
            Ui.LocalizationManager.Instance.Language = language;
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        DateTime start = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Local);
        const int boss = 100;
        var random = new Random(11);
        string[] classes = { "Gladiator", "Templar", "Cleric", "Chanter", "Sorcerer", "Ranger", "Assassin", "Spiritmaster" };
        string[] names = { "Kaivhan", "Nyxelle", "Seraphyne", "Draegor", "Lumiael", "Selvane", "Orrikai", "Miyune" };
        var tracks = new List<Combat.PositionTrack>();
        for (int i = 0; i < classes.Length; i++)
        {
            double phase = i * Math.PI / 4;
            var t = new List<int>();
            var x = new List<int>();
            var y = new List<int>();
            for (int ms = 0; ms <= 60000; ms += 700)
            {
                double radius = (i < 2 ? 450 : 900 + 150 * (i % 3)) + 120 * Math.Sin(ms / 5000.0 + i);
                double angle = phase + ms / 14000.0 * (i % 2 == 0 ? 1 : -1);
                t.Add(ms);
                x.Add((int)(radius * Math.Cos(angle) + random.Next(-40, 40)));
                y.Add((int)(radius * Math.Sin(angle) + random.Next(-40, 40)));
            }

            tracks.Add(new Combat.PositionTrack(i + 1, start, t.ToArray(), x.ToArray(), y.ToArray()));
        }

        tracks.Add(new Combat.PositionTrack(boss, start, new[] { 0, 60000 }, new[] { 0, 0 }, new[] { 0, 0 }));
        var windups = new List<Combat.WindupRecord>();
        var hits = new List<Combat.DamageEvent>();
        int[] skills = { 1236440, 1217430, 1223500 };
        for (int s = 3; s < 58; s += 4)
        {
            int skill = skills[(s / 4) % skills.Length];
            double angle = s * 0.9;
            windups.Add(new Combat.WindupRecord(start.AddSeconds(s), boss, skill, (s / 4) % 8 + 1, 1500, angle * 57.3 % 360, (int)(700 * Math.Cos(angle)), (int)(700 * Math.Sin(angle)), true));
            hits.Add(new Combat.DamageEvent(start.AddSeconds(s + 2.1), boss, (s / 4) % 8 + 1, 3000 + s * 10, false, "Attack", SkillId: skill));
        }

        var extras = new Combat.FightExtras(Array.Empty<Combat.SkillUse>(), Array.Empty<Combat.BuffEvent>(), new[] { new Combat.StaggerEvent(start.AddSeconds(30), boss, 0, 0, true) },
            new Dictionary<int, string>(), tracks, windups, new[] { new Combat.DeathRecord(start.AddSeconds(41), 5, start.AddSeconds(52)) },
            new[] { new Combat.HpSeries(boss, start, Enumerable.Range(0, 61).Select(s => s * 1000).ToArray(), Enumerable.Range(0, 61).Select(s => (short)(1000 - s * 15)).ToArray()) });
        var data = Combat.ReplayBuilder.Build(Aion2.Protocol.Aion2Npcs.NameOf(2300708, Ui.LocalizationManager.Instance.Language) ?? "Boss", extras, hits, start, start.AddSeconds(60),
            id => id == boss ? (Aion2.Protocol.Aion2Npcs.NameOf(2300708, Ui.LocalizationManager.Instance.Language) ?? "Boss", "", false) : (names[id - 1], classes[id - 1], true),
            skill => skill == 1236440 ? 520 : null, _ => Aion2.Protocol.Aion2SkillNames.Display("Attack"));
        var window = new Ui.FightReplayWindow(data);
        window.ShowAt(seconds);
        const double width = 960, height = 700;
        var content = (System.Windows.UIElement)window.Content;
        content.Measure(new System.Windows.Size(width, height));
        content.Arrange(new System.Windows.Rect(0, 0, width, height));
        content.UpdateLayout();
        content.UpdateLayout();
        var bitmap = Shot((int)width * scale, (int)height * scale, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        var backdrop = new System.Windows.Controls.Border { Width = width, Height = height, Background = (System.Windows.Media.Brush)app.Resources["Brush.Window"] };
        backdrop.Measure(new System.Windows.Size(width, height));
        backdrop.Arrange(new System.Windows.Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine("render-replay: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>Draws the "window will turn transparent" message box into a picture; no window is ever shown.</summary>
    private static void RunRenderMessageBoxMode(string path)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        if (settings.Language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = settings.Language;
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        var loc = Ui.LocalizationManager.Instance;
        var window = Ui.ThemedMessageBox.Build(null, string.Format(loc["Main.HideUiHint"], "Ctrl+Alt+H"), loc["Main.HideUiHint.Title"], System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information, loc["Main.HideUiHint.DontShow"], null, out _);
        // in the running meter the box takes its button/combo styles from the main window; here there is none, so they come from the settings window (same look)
        var styled = new Ui.SettingsWindow(settings);
        foreach (var type in new[] { typeof(System.Windows.Controls.Button) })
        {
            if (styled.Resources[type] is System.Windows.Style style)
            {
                window.Resources[type] = style;
            }
        }

        var content = (System.Windows.UIElement)window.Content;
        content.Measure(new System.Windows.Size(560, double.PositiveInfinity));
        int w = Math.Max(340, (int)Math.Ceiling(content.DesiredSize.Width)), h = (int)Math.Ceiling(content.DesiredSize.Height);
        content.Measure(new System.Windows.Size(w, h));
        content.Arrange(new System.Windows.Rect(0, 0, w, h));
        content.UpdateLayout();
        var bitmap = Shot(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine("render-messagebox: wrote " + path);
        Environment.Exit(0);
    }

    private static void RunRenderSettingsMode(string path, string tab)
    {
        var app = new System.Windows.Application();
        // The shared styles (ThemedSlider ...) are merged by the GUI start; the Settings window needs them here too.
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        if (Environment.GetEnvironmentVariable("AIONDPS_LANG") is { Length: > 0 } forcedLanguage)
        {
            Ui.LocalizationManager.Instance.Language = forcedLanguage; // pictures for the docs in a chosen language, whatever the user's settings say
        }
        else if (settings.Language.Length > 0)
        {
            Ui.LocalizationManager.Instance.Language = settings.Language; // so a layout can be checked in every language
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("AIONDPS_PET_RARE_MAX"), out int rareMax))
        {
            settings.PetRareMax = rareMax; // layout check only (nothing is saved here)
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        Aion2.Aion2FieldBosses.EnablePersistence(); // read-only here: the page shows the last lists the game sent
        var window = new Ui.SettingsWindow(settings);
        if (window.FindName("Nav" + (tab == "Interface" ? "Ui" : tab == "Capture" ? "Install" : tab)) is System.Windows.Controls.RadioButton nav)
        {
            nav.IsChecked = true;
        }

        if (Environment.GetEnvironmentVariable("AIONDPS_SPEECH_ON") is { Length: > 0 } && window.FindName("SpeechOnBox") is System.Windows.Controls.CheckBox speechOn)
        {
            speechOn.IsChecked = true; // layout check: the voice output block open
        }

        if (tab.ToLowerInvariant() is "network" or "loot" or "combat" or "timetable" or "boss" or "petmap" or "stream")
        {
            window.ShowModule(tab.ToLowerInvariant()); // the tool's own settings, without the sidebar
        }

        if (Environment.GetEnvironmentVariable("AIONDPS_LONGEST_PETS") is { Length: > 0 } longest && int.TryParse(longest, out int longestCount))
        {
            window.ShowLongestPetNames(longestCount); // layout check: the pets with the longest names in this language
        }

        if (Environment.GetEnvironmentVariable("AIONDPS_PET_REGION") is { Length: > 0 })
        {
            window.ShowPetRegionTab(); // layout check: the Region tab of the pet page
        }

        string? rare = Environment.GetEnvironmentVariable("AIONDPS_PET_RARE");
        if (!string.IsNullOrEmpty(rare))
        {
            window.ShowPetRareTab(rare == "1" ? null : rare); // layout check: the Rare tab (the value is the key of the line to select, e.g. "verteron.2100079")
        }

        double width = 620, height = string.IsNullOrEmpty(rare) && tab != "Hotkeys" ? 740 : 1000;
        if (int.TryParse(Environment.GetEnvironmentVariable("AIONDPS_RENDER_HEIGHT"), out int customHeight))
        {
            height = Math.Clamp(customHeight, 200, 1400);
        }

        var content = (System.Windows.UIElement)window.Content;
        if (tab is "Pets" or "Gather" && window.TakePanel(tab == "Pets" ? "PanelPets" : "PanelGather") is { } taken)
        {
            // the page as the tools window shows it (layout check, nothing is saved)
            var body = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(14) };
            body.Resources.MergedDictionaries.Add(window.Resources);
            body.Children.Add(taken);
            var host = new System.Windows.Controls.Border { Child = body };
            host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Brush.Window");
            content = host;
            width = 860;
        }

        content.Measure(new System.Windows.Size(width, height));
        content.Arrange(new System.Windows.Rect(0, 0, width, height));
        content.UpdateLayout();
        int scale = int.TryParse(Environment.GetEnvironmentVariable("AIONDPS_RENDER_SCALE"), out int sc) ? Math.Clamp(sc, 1, 4) : 1;
        var bitmap = Shot((int)width * scale, (int)height * scale, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine("render-settings: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>The real window logic with the real live capture, never shown: ticks once a second
    /// like the window's timer, presses Clear a third of the way through, and prints the row list
    /// every few seconds - to tell a capture/decoder problem from a window problem on a live game.</summary>
    private static void RunAion2UiLiveMode(int seconds)
    {
        // Write through at once and end the process by hand: the capture threads keep it alive
        // otherwise, and a killed process loses whatever stdout had buffered.
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        var app = new System.Windows.Application();
        var settings = Ui.MeterSettings.Load();
        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        var window = new Ui.MainWindow();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var type = typeof(Ui.MainWindow);
        Console.WriteLine("aion2-ui-live: window created, starting the live source...");
        type.GetMethod("StartCapture", flags)!.Invoke(window, new object?[] { settings });
        Console.WriteLine("aion2-ui-live: source started");
        var tick = type.GetMethod("OnPollTimerTick", flags)!;
        var clear = type.GetMethod("ClearDamageData", flags)!;
        var rows = (System.Collections.IList)type.GetField("_rows", flags)!.GetValue(window)!;
        var aggregator = type.GetField("_aggregator", flags)!.GetValue(window)!;
        var events = (System.Collections.ICollection)aggregator.GetType().GetProperty("Events")!.GetValue(aggregator)!;

        int clearAt = seconds / 3;
        for (int t = 1; t <= seconds; t++)
        {
            Thread.Sleep(1000);
            if (t <= 3)
            {
                Console.WriteLine($"[{t,3}s] ticking...");
            }

            tick.Invoke(window, new object?[] { null, EventArgs.Empty });
            if (t == clearAt)
            {
                clear.Invoke(window, null);
                Console.WriteLine($"[{t,3}s] >>> Clear pressed");
            }

            if (t % 5 == 0 || t == clearAt + 1)
            {
                string top = rows.Count > 0 ? string.Join(", ", rows.Cast<Ui.PlayerRow>().Take(3).Select(r => $"{r.Name} {r.Damage:N0}")) : "-";
                Console.WriteLine($"[{t,3}s] aggregator events {events.Count,5} | rows {rows.Count,3} | {top}");
            }
        }

        Environment.Exit(0);
    }

    /// <summary>Plays a recording through the packet source (no window) and prints the skill presets each time the game switches one.</summary>
    private static void RunAion2PresetCheck(string path, int serverPort)
    {
        var source = new Aion2.Aion2PacketCombatSource(Aion2.Protocol.Aion2Protocol.Load());
        var directory = (Aion2.Aion2EntityDirectory)source.Entities;
        var names = Aion2.Protocol.Aion2SkillNames.Load();
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        int last = -1;
        string lastSummary = "";
        foreach (var segment in Aion2.Capture.SegmentRecording.Read(path)
                     .Where(s => s.Source.EndsWith(":" + serverPort, StringComparison.Ordinal) || s.Destination.EndsWith(":" + serverPort, StringComparison.Ordinal))
                     .Select(s => s with { FromServer = s.Source.EndsWith(":" + serverPort, StringComparison.Ordinal) }))
        {
            source.Ingest(segment);
            string summary = Summarise();
            if (directory.ActivePreset != last || summary != lastSummary)
            {
                Console.WriteLine($"{segment.Timestamp:HH:mm:ss.fff} active preset {directory.ActivePreset}: {summary}");
                last = directory.ActivePreset;
                lastSummary = summary;
            }
        }

        Console.WriteLine("final:");
        foreach (var preset in directory.LocalPresets)
        {
            Console.WriteLine($"  preset {preset.Index}{(preset.Active ? " (active)" : "")} seen {preset.SeenAt:HH:mm:ss}: {preset.Skills.Count} skills");
            Console.WriteLine("    stigmas: " + string.Join(", ", preset.Skills.Where(k => k.Stigma && k.SkillId % 10000 == 0).Select(k => $"{names.GetValueOrDefault(k.SkillId)} Lv{k.Level}")));
            foreach (var k in preset.Skills.Where(k => k.SkillId % 10000 == 0 && !k.Stigma && k.Stones is not null && k.Equipped))
            {
                Console.WriteLine($"    {names.GetValueOrDefault(k.SkillId),-20} Lv{k.Level,2}/{k.BaseLevel,-2} stones [{string.Join(",", k.Stones!)}]");
            }
        }

        string Summarise() => string.Join(", ", directory.LocalSkills.Where(k => k.Stigma && k.SkillId % 10000 == 0).Select(k => $"{names.GetValueOrDefault(k.SkillId)} {k.Level}")) + $" | {directory.LocalSkills.Count} skills, presets known {directory.LocalPresets.Count}";
    }

    private static void RunAion2UploadDryRun(string path, string serverName, int serverPort, string[] sendOnly)
    {
        var app = new System.Windows.Application();
        var settings = Ui.MeterSettings.Load();
        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        var window = new Ui.MainWindow();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var type = typeof(Ui.MainWindow);
        var source = new Aion2.Aion2PacketCombatSource(Aion2.Protocol.Aion2Protocol.Load());
        type.GetField("_source", flags)!.SetValue(window, source);
        // A recording that starts after the login has no character record: the stored character of the last session (maybe another one) would be "self".
        // AION_OWN_NAME names the character the recording belongs to.
        if (Environment.GetEnvironmentVariable("AION_OWN_NAME") is { Length: > 0 } ownCharacter)
        {
            if (source.Entities is Aion2.Aion2EntityDirectory ownDirectory)
            {
                ownDirectory.SetConfiguredLocalName(ownCharacter);
                // the saved character of the last session (another one) must not be "self" either
                var characterField = typeof(Aion2.Aion2EntityDirectory).GetField("_character", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (characterField?.GetValue(ownDirectory) is Aion2.Aion2CharacterInfo { Restored: true })
                {
                    characterField.SetValue(ownDirectory, null);
                }
            }
        }

        var tick = type.GetMethod("OnPollTimerTick", flags)!;

        var segments = Aion2.Capture.SegmentRecording.Read(path)
            .Where(s => s.Source.EndsWith(":" + serverPort, StringComparison.Ordinal) || s.Destination.EndsWith(":" + serverPort, StringComparison.Ordinal))
            .Select(s => s with { FromServer = s.Source.EndsWith(":" + serverPort, StringComparison.Ordinal) })
            .ToList();
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        var entries = (System.Collections.IList)type.GetField("_mobBossEntries", flags)!.GetValue(window)!;
        var build = type.GetMethod("BuildEncounterUpload", flags, null, new[] { typeof(int), typeof(string), typeof(string) }, null)!;
        // The same slug the meter files real uploads under (MainWindow.ServerSlug): "Europe - Kaisinel" is
        // aion2:europe-kaisinel. A plain space-to-dash replace gave "europe---kaisinel" and a second server row.
        string fingerprint = "aion2:" + System.Text.RegularExpressions.Regex.Replace(serverName.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

        // The meter clears its rows at a later map change, and an upload is built from the rows - so
        // like a real user, build while playing (after every step) and keep the fullest result per boss.
        var best = new Dictionary<int, Upload.EncounterUploadRequest>();
        int chunk = Math.Max(1, segments.Count / 400);
        for (int i = 0; i < segments.Count; i += chunk)
        {
            foreach (var segment in segments.Skip(i).Take(chunk))
            {
                source.Ingest(segment);
            }

            tick.Invoke(window, new object?[] { null, EventArgs.Empty });
            foreach (object entry in entries.Cast<object>().ToList())
            {
                var (targetId, _) = ((int, string))entry;
                if (build.Invoke(window, new object?[] { targetId, fingerprint, serverName }) is Upload.EncounterUploadRequest request
                    && (!best.TryGetValue(targetId, out var old) || request.Participants.Sum(p => p.TotalDamage) >= old.Participants.Sum(p => p.TotalDamage)))
                {
                    best[targetId] = request;
                }
            }
        }

        Console.WriteLine($"aion2-upload-dryrun: {segments.Count} segments played; uploads that would be sent:");
        int uploads = 0;
        foreach (var (targetId, request) in best)
        {
            uploads++;
            if (Environment.GetEnvironmentVariable("AION_DRYRUN_BOSS_JSON") is { Length: > 0 } bossJsonDir)
            {
                // the request as the upload client would send it (same options), for checking it against the backend's schema
                File.WriteAllText(Path.Combine(bossJsonDir, $"boss-{uploads:00}.json"), System.Text.Json.JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
            }

            Console.WriteLine($"  {request.BossNpcName} (entity {targetId}) NPC id {request.BossNpcId}, level {request.BossLevel}, max HP {request.BossMaxHp}, game {request.Game}, server {request.ServerName} [{request.ServerFingerprint}], {request.StartedAt:HH:mm:ss}-{request.EndedAt:HH:mm:ss} UTC, {request.Participants.Count} participant(s), {request.Participants.Count(p => p.IsSelf)} self");
            foreach (var d in request.BossDebuffs ?? Array.Empty<Upload.BuffUsageUpload>())
            {
                Console.WriteLine($"      boss effect {d.Skill,-26} id {d.SkillId,9} uptime {d.UptimePermille / 10.0,5:0.0}% x{d.Casts,-4} givers {(d.Givers is { Count: > 0 } dg ? string.Join("/", dg) : "-")}");
            }

            foreach (var p in request.Participants.OrderByDescending(x => x.TotalDamage))
            {
                Console.WriteLine($"      {p.Name,-12} {p.ClassName,-13} self={p.IsSelf,-5} damage {p.TotalDamage,9:N0} taken {p.DamageTaken,8:N0} heal {p.TotalHealing,7:N0} guild {p.Guild ?? "-"} profile {(p.Profile is null ? "-" : p.Profile.Source)} buffs {p.Buffs?.Count ?? 0} (group {p.Buffs?.Count(b => b.Givers is { Count: > 0 }) ?? 0})");
                if (p.Timeline?.Analysis is { } an)
                {
                    Console.WriteLine($"          analysis: boss damage in slices {an.Damage.Sum():N0} of {p.TotalDamage:N0} ({an.Damage.Count} x 5 s), add damage {an.AddDamage:N0}, dead {an.Dead.Where((_, i) => i % 2 == 1).Sum() / 10.0 - an.Dead.Where((_, i) => i % 2 == 0).Sum() / 10.0:0.0} s in {an.Dead.Count / 2} span(s), taken by {string.Join(", ", an.Taken.Take(4).Select(t => $"{t.From}/{t.Skill} {t.Total:N0}"))}");
                }

                if (Environment.GetEnvironmentVariable("AION_DRYRUN_BUFFS") == "1")
                {
                    foreach (var b in (p.Buffs ?? Array.Empty<Upload.BuffUsageUpload>()).Take(40))
                    {
                        Console.WriteLine($"          {b.Skill,-28} id {b.SkillId,9} uptime {b.UptimePermille / 10.0,5:0.0}% x{b.Casts,-4} givers {(b.Givers is { Count: > 0 } g ? string.Join("/", g) : "-")}");
                    }
                }
            }
        }

        var buildProfiles = type.GetMethod("BuildProfilesUpload", flags);
        if (buildProfiles?.Invoke(window, new object?[] { fingerprint, serverName, false, null }) is Upload.ProfilesUploadRequest profilesRequest)
        {
            if (Environment.GetEnvironmentVariable("AION_DRYRUN_JSON") is { Length: > 0 } jsonPath)
            {
                File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(profilesRequest, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
            }

            Console.WriteLine($"aion2-upload-dryrun: the profile upload (no boss) would carry {profilesRequest.Participants.Count} player(s):");
            foreach (var pp in profilesRequest.Participants)
            {
                Console.WriteLine($"  {pp.Name,-14} {pp.ClassName,-13} {pp.Profile.Source,-5} level {pp.Profile.Level?.ToString() ?? "-"} gear {pp.Profile.Gear.Count} (enchant sum {pp.Profile.Gear.Sum(g => g.Enchant)}) legion {pp.Guild ?? "-"}");
            }

            if (profilesRequest.Participants.Select(pp => pp.Profile).FirstOrDefault(pr => pr.Pantheon is not null) is { Pantheon: { } pantheon })
            {
                Console.WriteLine($"aion2-upload-dryrun: the own Pantheon: {pantheon.Owned.Count} registered, setups " + string.Join(" | ",
                    pantheon.Presets.Select(x => $"preset {x.Preset}: " + string.Join(" ", x.Slots.Select(k => $"{k.Slot}:{k.Id}")))));
            }
        }
        else
        {
            Console.WriteLine("aion2-upload-dryrun: the profile upload (no boss) would be EMPTY (no own character / class in this recording)");
        }

        if (source.Entities is Aion2.Aion2EntityDirectory directory)
        {
            var inspected = directory.InspectedPlayers();
            string roundTrip = Path.Combine(Path.GetTempPath(), "aion2-inspected-roundtrip.json");
            Aion2.Aion2CharacterStore.SaveInspected(roundTrip, inspected);
            var reloaded = Aion2.Aion2CharacterStore.LoadInspected(roundTrip);
            Console.WriteLine($"aion2-upload-dryrun: saved {inspected.Count} window(s), reloaded {reloaded.Count}, gear of the first {reloaded.FirstOrDefault()?.Gear.Count} item(s), enchant sum {reloaded.FirstOrDefault()?.Gear.Sum(g => g.Enchant)}");
            File.Delete(roundTrip);
            Console.WriteLine($"aion2-upload-dryrun: {inspected.Count} character window(s) of other players read; {directory.LocalSkills.Count(s => s.Stigma)} own stigma(s) marked");
            foreach (var player in inspected)
            {
                Console.WriteLine($"  {player.Name,-14} {Aion2.Protocol.Aion2SkillNames.ClassFromCode(player.ClassCode),-13} level {player.Level} gear score {player.GearScore} legion {player.Guild ?? "-"} titles [{string.Join(",", (player.Titles ?? Array.Empty<Aion2.Aion2TitleSlot>()).Select(t => t.TitleId))}] pets [{string.Join(" ", (player.Pets ?? Array.Empty<Aion2.Aion2Pet>()).Select(k => $"{k.SpeciesId}/{k.Level}:{string.Join("", k.Kinds)}"))}] boards [{string.Join(",", (player.BoardCounts ?? Array.Empty<Aion2.Aion2BoardCount>()).Select(b => $"{b.BoardId}={b.Count}"))}]: {string.Join(", ", player.Gear.Select(g => $"{g.SlotIndex}:{g.ItemId}+{g.Enchant}"))}");
            }
        }

        if (sendOnly.Length == 0)
        {
            Console.WriteLine($"aion2-upload-dryrun: {uploads} upload(s) would be sent. Nothing was sent.");
            Environment.Exit(0);
        }

        int sent = 0;
        foreach (string wanted in sendOnly)
        {
            string[] parts = wanted.Split('@');
            var match = best.Values.FirstOrDefault(r => r.BossNpcName == parts[0] && parts.Length == 2 && r.StartedAt.ToString("HH:mm") == parts[1]);
            if (match is null)
            {
                Console.WriteLine($"aion2-upload-dryrun: --send {wanted}: no such fight in this recording, skipped");
                continue;
            }

            // The recording has no login of its own character: the character of the last session shows as "self". AION_OWN_NAME names the real one.
            if (Environment.GetEnvironmentVariable("AION_OWN_NAME") is { Length: > 0 } realSelf)
            {
                if (!match.Participants.Any(p => p.Name == realSelf))
                {
                    Console.WriteLine($"aion2-upload-dryrun: --send {wanted}: {realSelf} is not in this fight, skipped");
                    continue;
                }

                match = match with { Participants = match.Participants.Where(p => p.Name == realSelf || !(p.IsSelf && p.TotalDamage == 0)).Select(p => p with { IsSelf = p.Name == realSelf }).ToList() };
            }

            var result = Upload.UploadClient.SendAsync(match).GetAwaiter().GetResult();
            Console.WriteLine($"aion2-upload-dryrun: --send {wanted}: {(result.Success ? "uploaded" : "FAILED " + result.Error)}");
            sent += result.Success ? 1 : 0;
        }

        Console.WriteLine($"aion2-upload-dryrun: {sent} of {sendOnly.Length} requested upload(s) sent.");
        Environment.Exit(0);
    }

    private static void RunAion2UiTestMode(string path, int serverPort)
    {
        {
            var app = new System.Windows.Application();
            var settings = Ui.MeterSettings.Load();
            Ui.ThemeManager.Apply(app, settings);
            Ui.MainWindow.Headless = true;
        var window = new Ui.MainWindow();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var type = typeof(Ui.MainWindow);

            // Not Start()ed: no capture, the recording is fed in by hand, one chunk per timer tick.
            var source = new Aion2.Aion2PacketCombatSource(Aion2.Protocol.Aion2Protocol.Load());
            type.GetField("_source", flags)!.SetValue(window, source);
            var tick = type.GetMethod("OnPollTimerTick", flags)!;
            var clear = type.GetMethod("ClearDamageData", flags)!;
            var rows = (System.Collections.IList)type.GetField("_rows", flags)!.GetValue(window)!;

            var segments = Aion2.Capture.SegmentRecording.Read(path)
                .Where(s => s.Source.EndsWith(":" + serverPort, StringComparison.Ordinal) || s.Destination.EndsWith(":" + serverPort, StringComparison.Ordinal))
                .Select(s => s with { FromServer = s.Source.EndsWith(":" + serverPort, StringComparison.Ordinal) })
                .ToList();
            int chunk = Math.Max(1, segments.Count / 60);
            int next = 0;

            string Describe() => $"{rows.Count} row(s)" + (rows.Count > 0 ? ": " + string.Join(", ", rows.Cast<Ui.PlayerRow>().Take(4).Select(r => $"{r.Name} {r.Damage:N0}")) : "");

            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.WriteLine($"aion2-ui-test: {segments.Count} segments fed from a second thread (like the capture), the window ticks once per 500 ms");

            // The feeder is the "capture thread": it hands segments to the source in small bursts.
            bool feederDone = false;
            var feeder = new Thread(() =>
            {
                try
                {
                    while (next < segments.Count)
                    {
                        foreach (var segment in segments.Skip(next).Take(chunk))
                        {
                            source.Ingest(segment);
                        }

                        next += chunk;
                        Thread.Sleep(250);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  !! feeder thread died: {ex.GetType().Name}: {ex.Message}");
                }

                feederDone = true;
            });
            feeder.Start();

            int ticks = 0;
            bool cleared = false;
            while (!feederDone && ticks < 400)
            {
                Thread.Sleep(500);
                tick.Invoke(window, new object?[] { null, EventArgs.Empty });
                ticks++;
                if (!cleared && next > segments.Count / 3)
                {
                    clear.Invoke(window, null);
                    cleared = true;
                    Console.WriteLine($"  [tick {ticks}] >>> Clear pressed: {Describe()}");
                }

                if (ticks % 10 == 0)
                {
                    Console.WriteLine($"  [tick {ticks}] fed {next}/{segments.Count} | {Describe()}");
                }
            }

            Console.WriteLine($"  end: {Describe()} (feeder finished: {feederDone})");
            Environment.Exit(0);
        }
    }

    private static void RunRenderPetMapMode(string path, string mapKey, double x, double y, bool guards = false, string? route = null)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        var map = Aion2.Protocol.Aion2Maps.All.Concat(Aion2.Protocol.Aion2Maps.Instances).First(m => m.Key == mapKey);
        var chosen = settings.PetMapPets.Count > 0 ? settings.PetMapPets.ToHashSet() : new HashSet<int> { 1170, 1162, 1171, 1219 };
        var points = Aion2.Protocol.Aion2Maps.PointsOf(map, chosen);
        var pets = points.Select(p => p.PetId).Distinct().OrderBy(i => i).ToList();
        var colors = pets.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => Ui.PetMapPalette.Of(t.i));
        // "route-whole": the map zoomed all the way out (the whole map with the whole route), as the wheel or the − button leave it
        bool whole = route == "route-whole";
        var window = new Ui.PetMapWindow();
        if ((guards || route is not null) && window.Content is System.Windows.UIElement root)
        {
            // never shown: lay the frame out once so that the map knows how much of itself is visible
            root.Measure(new System.Windows.Size(Ui.PetMapWindow.ViewSize, Ui.PetMapWindow.ViewSize));
            root.Arrange(new System.Windows.Rect(0, 0, Ui.PetMapWindow.ViewSize, Ui.PetMapWindow.ViewSize));
            root.UpdateLayout();
        }

        // the pet route ("route", "route-round": round, only the marks, as laid over the game's minimap): planned from (x, y), the player 90 m from its first stop
        Aion2.PetRoutePlan? plan = null;
        double opacity = settings.PetMapOpacity;
        if (route is not null)
        {
            plan = Aion2.PetRoute.Plan(mapKey, mapKey == "verteron" ? "Elyos" : "Asmodian", Aion2.PetRoute.Genera.ToHashSet(), _ => true, (x / 100, y / 100, ""));
            if (plan is { Stops.Count: > 0 })
            {
                x = plan.Stops[0].X * 100 - 7000;
                y = plan.Stops[0].Z * 100 + 5500;
                window.SetRoute(plan.Stops.Take(whole ? Ui.PetMapWindow.RouteBadges : 6).Select(st => (st.X * 100, st.Z * 100, st.Radius, st.Step)).ToList(), null, plan.Stops[0].Zone);
                window.ArrowPets = plan.Stops[0].Pets.ToHashSet();
                chosen = plan.Stops.SelectMany(st => st.Pets).ToHashSet();
                points = Aion2.Protocol.Aion2Maps.PointsOf(map, chosen);
                pets = plan.Stops[0].Pets.Concat(points.Select(p => p.PetId).Distinct().OrderBy(i => i)).Distinct().Take(8).ToList();
                colors = points.Select(p => p.PetId).Distinct().OrderBy(i => i).Select((id, i) => (id, i)).ToDictionary(t => t.id, t => Ui.PetMapPalette.Of(t.i));
            }

            window.Round = route == "route-round";
            if (window.Round)
            {
                opacity = 0; // only the marks
            }

            Console.WriteLine(plan is null ? "render-petmap: no route" : $"render-petmap: route of {plan.Stops.Count} stops from {plan.OriginLabel}; first {plan.Stops.FirstOrDefault()?.Zone}");
        }

        // sample "live" monsters: every 7th spawn point within 120 m stands there right now
        var live = points.Where(p => Math.Abs(p.X - x) < 12000 && Math.Abs(p.Y - y) < 12000).Where((p, i) => i % 7 == 0).ToList();
        // and a Hidden Cube the game announces 30 m from the player (after the monsters: the label of the picture stays on the first monster)
        live.Add((Ui.PetMapWindow.CubeMark, (float)x + 2600, (float)y - 1500));
        colors[Ui.PetMapWindow.CubeMark] = Ui.PetMapPalette.OfGather("Cube");
        if (route is not null && plan is { Stops.Count: > 0 } hoverPlan)
        {
            // the label the mouse brings up: on the minimap over a live monster, on the full map over a stop's number
            window.Describe = (id, isLive) => id == Ui.PetMapWindow.CubeMark ? "Hidden Cube" : string.Join("\n", new[]
            {
                Aion2.Protocol.Aion2Pets.PetName(id, "en") ?? $"#{id}",
                "Monsters: " + string.Join(", ", Aion2.Protocol.Aion2Pets.NpcsOfPet(id).Select(n => Aion2.Protocol.Aion2Npcs.NameOf(n, "en")).Where(n => n is not null).Distinct().Take(4)),
                "Level 2 · 22/75",
            }.Concat(isLive ? new[] { "One of them is here right now" } : Array.Empty<string>()));
            window.SetRoute(hoverPlan.Stops.Take(whole ? Ui.PetMapWindow.RouteBadges : 6).Select(st => (st.X * 100, st.Z * 100, st.Radius, st.Step)).ToList(), null, hoverPlan.Stops[0].Zone,
                hoverPlan.Stops.Take(Ui.PetMapWindow.RouteBadges).Select(st => $"Stop {st.Step} of {hoverPlan.Stops.Count} · {st.Zone}\n" + string.Join(", ", st.Pets.Select(id => Aion2.Protocol.Aion2Pets.PetName(id, "en")))).ToList(),
                Aion2.PetRoute.KibeliskOf(mapKey, hoverPlan.Stops[0]) is { } kib ? kib.Name : null);
            window.HoverForPicture = whole ? "stop" : "live";
        }

        if (whole)
        {
            // zoomed out the spawn points of the route's pets (as the app passes them), the collectibles, the live monsters and the cube near the player
            window.MeHeading = 40;
            window.Render(map, x, y, settings.PetMapRadius, 1.0, points.ToList(), live, colors,
                Aion2.Protocol.Aion2Gather.PointsOf(map, Aion2.Protocol.Aion2Gather.Items().Where(i => i.Count > 0 && i.Kind != "Cube").Select(i => i.Key).ToHashSet()), zoom: 1);
        }
        else
        {
            window.Render(map, x, y, settings.PetMapRadius, opacity, points.ToList(), live, colors,
                Aion2.Protocol.Aion2Gather.PointsOf(map, Aion2.Protocol.Aion2Gather.Items().Where(i => i.Count > 0 && i.Kind != "Cube").Select(i => i.Key).ToHashSet()),
                guards: guards ? new List<(float X, float Y)> { ((float)x + 1500, (float)y + 700), ((float)x - 1200, (float)y - 400), ((float)x - 30000, (float)y + 5000), ((float)x + 9000, (float)y - 21000) } : null);
        }
        var list = new Ui.PetListWindow();
        list.Render(pets.Select(id => (colors.GetValueOrDefault(id, System.Windows.Media.Colors.White), Aion2.Protocol.Aion2Pets.PetName(id, "en") ?? "?", "Level 2 · 22/75", "104 m", false)).ToList());
        if (plan is { Stops.Count: > 0 } shownPlan)
        {
            var first = shownPlan.Stops[0];
            list.RenderHead($"Stop 1 of {shownPlan.Stops.Count} · {first.Zone} · 90 m",
                string.Join(", ", first.Pets.Select(id => Aion2.Protocol.Aion2Pets.PetName(id, "en"))) + (shownPlan.Stops.Count > 1 ? $"\nThen stop 2 · {shownPlan.Stops[1].Zone}" : ""));
        }
        var visual = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2a, 0x36, 0x2c)) };
        foreach (var overlay in new System.Windows.Window[] { window, list })
        {
            var content = (System.Windows.UIElement)overlay.Content;
            overlay.Content = null;
            visual.Children.Add(content);
        }

        visual.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        visual.Arrange(new System.Windows.Rect(visual.DesiredSize));
        visual.UpdateLayout();
        int w = (int)Math.Ceiling(visual.DesiredSize.Width), h = (int)Math.Ceiling(visual.DesiredSize.Height);
        var bitmap = Shot(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine($"render-petmap: wrote {path} ({points.Count} points of {pets.Count} pets on {map.Title}); tiles shown {window.TilesShown}: {window.TileDebug}");
        Environment.Exit(0);
    }

    /// <summary>The pet list with the pets of an open Black Cloud shop (sample data of the recording 2026-10-11_12-34), drawn into a picture; no window is shown.</summary>
    /// <summary>Draws the pet farming overlay (pet level of the targeted monster) with a made-up pet into a picture; no window is ever shown.</summary>
    private static void RunRenderPetFarmMode(string path)
    {
        Environment.SetEnvironmentVariable("AIONDPS_DATA_DIR", Path.Combine(Path.GetTempPath(), "aiondps-render-petfarm"));
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = new Ui.MeterSettings();
        if (Environment.GetEnvironmentVariable("AIONDPS_LANG") is { Length: > 0 } language)
        {
            Ui.LocalizationManager.Instance.Language = language;
        }

        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        var loc = Ui.LocalizationManager.Instance;
        string[] pets = { "Sylph", "Kibelisk" };
        string[] levels = { string.Format(loc["PetFarm.Level"], 7) + " (12/25)", loc["PetFarm.NotOwned"] };
        var lines = new System.Windows.Controls.StackPanel();
        for (int i = 0; i < pets.Length; i++)
        {
            lines.Children.Add(Ui.PetFarmWindow.BuildRow(pets[i], levels[i]));
        }

        var panel = new System.Windows.Controls.Border
        {
            CornerRadius = new System.Windows.CornerRadius(6), Padding = new System.Windows.Thickness(8, 5, 8, 5), Child = lines,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xB0, 0, 0, 0)),
            LayoutTransform = new System.Windows.Media.ScaleTransform(2, 2),
        };
        var visual = new System.Windows.Controls.Border { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2a, 0x36, 0x2c)), Padding = new System.Windows.Thickness(12), Child = panel };
        visual.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        visual.Arrange(new System.Windows.Rect(visual.DesiredSize));
        visual.UpdateLayout();
        int w = (int)Math.Ceiling(visual.DesiredSize.Width), h = (int)Math.Ceiling(visual.DesiredSize.Height);
        var bitmap = Shot(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine($"render-petfarm: wrote {path}");
        Environment.Exit(0);
    }

    private static void RunRenderPetListShopMode(string path)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        // (pet id, tab, price) of the special and pet tabs; (pet id, level, progress) as the login frame reported them
        var offers = new (int, int, long)[] { (1041, 0, 70000), (1161, 0, 80000), (1128, 0, 90000), (1108, 0, 80000), (1118, 0, 70000), (1190, 1, 80000), (1123, 1, 90000), (1096, 1, 70000), (1174, 1, 90000), (1154, 1, 90000), (1160, 1, 70000), (1060, 1, 50000), (1115, 1, 90000), (1102, 1, 70000), (1141, 1, 90000) }
            .Select(o => new Aion2.Protocol.Aion2ShopPet(o.Item1, o.Item2, o.Item3)).ToList();
        var states = new (int, int, int)[] { (1128, 1, 6), (1108, 1, 18), (1118, 2, 17), (1190, 1, 0), (1123, 1, 20), (1096, 1, 5), (1174, 1, 16), (1160, 1, 0), (1060, 2, 27), (1141, 2, 12) }
            .Select(s => new Aion2.Protocol.Aion2PetState(s.Item1, s.Item2, s.Item3)).ToList();
        var rows = Ui.PetMapController.ShopRowsOf(offers, states).ToList();
        rows.Add((System.Windows.Media.Color.FromRgb(0xE6, 0x78, 0x50), "Sylphe", "Level 1 · 4/25", "38 m", false));
        var list = new Ui.PetListWindow();
        list.Render(rows);
        var content = (System.Windows.UIElement)list.Content;
        list.Content = null;
        var visual = new System.Windows.Controls.Border { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2a, 0x36, 0x2c)), Padding = new System.Windows.Thickness(12), Child = content };
        visual.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        visual.Arrange(new System.Windows.Rect(visual.DesiredSize));
        visual.UpdateLayout();
        int w = (int)Math.Ceiling(visual.DesiredSize.Width), h = (int)Math.Ceiling(visual.DesiredSize.Height);
        var bitmap = Shot(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        Console.WriteLine($"render-petlist-shop: wrote {path}");
        Environment.Exit(0);
    }

    /// <summary>Average hit per skill out of recordings, for <see cref="Aion2.Protocol.Aion2HitAverages"/>. Only exact hits of players on
    /// monsters count (no ticks, no heals, no estimates); the biggest 1 % of each skill are left out as outliers (a placeholder or a stacked burst).</summary>
    private static void RunAion2SkillAverages(string[] args)
    {
        string output = args[1];
        int port = 13328;
        var files = new List<string>();
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length)
            {
                port = int.Parse(args[++i]);
            }
            else
            {
                files.Add(args[i]);
            }
        }

        var protocol = Aion2.Protocol.Aion2Protocol.Load();
        var bySkill = new Dictionary<int, List<long>>();
        foreach (string path in files)
        {
            using var source = new Aion2.Aion2PacketCombatSource(protocol);
            var directory = (Aion2.Aion2EntityDirectory)source.Entities;
            var hits = new List<(int Source, int Target, int Skill, long Amount)>();
            try
            {
                foreach (Aion2.Capture.TcpSegment segment in Aion2.Capture.SegmentRecording.Read(path))
                {
                    bool fromServer = segment.Source.EndsWith(":" + port, StringComparison.Ordinal);
                    if (!fromServer && !segment.Destination.EndsWith(":" + port, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    source.Ingest(segment with { FromServer = fromServer });
                    foreach (Combat.DamageEvent e in source.Poll(false).Damage)
                    {
                        if (!e.IsHeal && !e.IsTick && !e.Estimated && e.SkillId > 0)
                        {
                            hits.Add((e.SourceObjectId, e.TargetObjectId, e.SkillId, e.Amount));
                        }
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // a recording still being written ends in half a line: what was read before it counts
            }

            // Judged at the end of the recording, when the directory knows who is a monster and who a player.
            int kept = 0;
            foreach (var hit in hits)
            {
                if (directory.NpcIdOf(hit.Target) is null || directory.NpcIdOf(hit.Source) is not null || directory.IsKnownPlayer(hit.Target))
                {
                    continue;
                }

                if (!bySkill.TryGetValue(hit.Skill, out List<long>? list))
                {
                    bySkill[hit.Skill] = list = new List<long>();
                }

                list.Add(hit.Amount);
                kept++;
            }

            Console.WriteLine($"aion2-skill-averages: {Path.GetFileName(path)}: {hits.Count} hit(s), {kept} on monsters");
        }

        static (long Average, int Samples) Summarize(List<long> amounts)
        {
            amounts.Sort();
            int keep = amounts.Count >= 100 ? amounts.Count - amounts.Count / 100 : amounts.Count;
            return ((long)Math.Round(amounts.Take(keep).Average()), amounts.Count);
        }

        var skills = bySkill.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => Summarize(kv.Value));
        var families = bySkill.GroupBy(kv => kv.Key / 10).OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => Summarize(g.SelectMany(kv => kv.Value).ToList()));
        using (var stream = File.Create(output))
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("generated", DateTime.UtcNow.ToString("yyyy-MM-dd"));
            writer.WriteString("note", "Average hit of a skill (players' exact hits on monsters, top 1 percent left out) from recordings; read by Aion2HitAverages for the world boss estimate. Made by: AionDPS aion2-skill-averages.");
            writer.WriteNumber("recordings", files.Count);
            foreach ((string name, Dictionary<int, (long Average, int Samples)> table) in new[] { ("skills", skills), ("families", families) })
            {
                writer.WriteStartObject(name);
                foreach (var (id, row) in table.Where(kv => kv.Value.Samples >= 3))
                {
                    writer.WriteStartObject(id.ToString());
                    writer.WriteNumber("avg", row.Average);
                    writer.WriteNumber("n", row.Samples);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        Console.WriteLine($"aion2-skill-averages: {skills.Count} skill(s), {skills.Count(kv => kv.Value.Samples >= Aion2.Protocol.Aion2HitAverages.MinSamples)} with {Aion2.Protocol.Aion2HitAverages.MinSamples}+ hits, written to {output}");
    }

    /// <summary>Draws the crafting tool (the page with its overlay, and the configuration window) into one picture: a made-up plan, the stock of a recording; no window is ever shown.</summary>
    private static void RunRenderCraftMode(string path, string recording, string language)
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        Ui.LocalizationManager.Instance.Language = language.Length > 0 ? language : settings.Language.Length > 0 ? settings.Language : "de";
        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        // in the running meter the buttons, combo boxes, check boxes and scroll bars take their styles from the main window: it is made (never shown) to take them from there
        var hostWindow = new Ui.MainWindow();
        app.MainWindow = hostWindow;
        foreach (var type in new[] { typeof(System.Windows.Controls.Button), typeof(System.Windows.Controls.ComboBox), typeof(System.Windows.Controls.ComboBoxItem), typeof(System.Windows.Controls.CheckBox), typeof(System.Windows.Controls.Primitives.ScrollBar), typeof(System.Windows.Controls.ToolTip), typeof(System.Windows.Controls.MenuItem) })
        {
            if (hostWindow.Resources[type] is System.Windows.Style style)
            {
                app.Resources[type] = style;
            }
        }

        Aion2.Aion2Stock.Reset();
        if (recording.Length > 0)
        {
            using var source = new Aion2.Aion2PacketCombatSource(Aion2.Protocol.Aion2Protocol.Load());
            foreach (Aion2.Capture.TcpSegment segment in Aion2.Capture.SegmentRecording.Read(recording))
            {
                bool fromServer = segment.Source.EndsWith(":13328", StringComparison.Ordinal);
                if (!fromServer && !segment.Destination.EndsWith(":13328", StringComparison.Ordinal))
                {
                    continue;
                }

                source.Ingest(segment with { FromServer = fromServer });
                source.Poll(false);
            }
        }

        Aion2.CraftPlan.ReplaceFor("", new[]
        {
            new Aion2.CraftEntry { Recipe = 314016004, Count = 20 }, new Aion2.CraftEntry { Recipe = 314036002, Count = 10 },
            new Aion2.CraftEntry { Recipe = 111025001, Count = 1, AccountWide = false }, new Aion2.CraftEntry { Recipe = 213034001, Count = 2 },
            new Aion2.CraftEntry { Recipe = 514085002, Count = 10, AccountWide = false },
        });

        var craftPage = new Ui.CraftPage(() => null);
        var module = new Ui.ModulePage("", craftPage, s => true, (s, v) => { }, s => "Ctrl+Alt+R", () => { }, () => { }, s => { }, null, craftPage.ModeBar);
        // the page inside the tools window, with its title bar and its list of tools
        var groups = Ui.ToolsWindow.Groups();
        var toolsWindow = new Ui.ToolsWindow(groups, id => id == "craft" ? new Ui.ToolPage { Content = module } : null, _ => { });
        toolsWindow.Select("craft");
        var page = (System.Windows.UIElement)toolsWindow.Content;
        toolsWindow.Content = null;
        var results = Ui.CraftText.Results(null, settings);
        var overlayLines = Ui.CraftOverlayController.OverlayLines(results, settings, sample: false);
        var overlay = new Ui.CraftOverlayWindow();
        overlay.Render(overlayLines.Caption, overlayLines.Lines, overlayLines.More);
        var overlayContent = overlay.DetachBody()!;
        var window = new Ui.CraftConfigWindow(() => null);
        // the whole frame of the window (title bar with icon, title, minimize and close), lifted out of the never shown window
        var config = (System.Windows.UIElement)window.Content;
        window.Content = null;

        System.Windows.Media.Brush back = (System.Windows.Media.Brush)app.Resources["Brush.Window"] ?? System.Windows.Media.Brushes.Black;
        // one part only (AIONDPS_PART = window | config | overlay), for the pictures of the website: AIONDPS_RENDER_SCALE=2 makes them sharp, the overlay is drawn on a see-through ground
        string part = Environment.GetEnvironmentVariable("AIONDPS_PART") ?? "all";
        if (part is "window" or "config" or "overlay")
        {
            int pictureScale = int.TryParse(Environment.GetEnvironmentVariable("AIONDPS_RENDER_SCALE"), out int ps) && ps is >= 1 and <= 4 ? ps : 1;
            System.Windows.FrameworkElement single = part switch
            {
                "window" => new System.Windows.Controls.Border { Width = 980, Height = 640, Background = back, Child = page },
                "config" => new System.Windows.Controls.Border { Width = 720, Height = 1030, Background = back, Child = config },
                _ => new System.Windows.Controls.Border { Child = overlayContent, HorizontalAlignment = System.Windows.HorizontalAlignment.Left },
            };
            single.Measure(new System.Windows.Size(1200, 2400));
            int sw = (int)Math.Ceiling(single.DesiredSize.Width), sh = (int)Math.Ceiling(single.DesiredSize.Height);
            single.Measure(new System.Windows.Size(sw, sh));
            single.Arrange(new System.Windows.Rect(0, 0, sw, sh));
            single.UpdateLayout();
            var shot = Shot(sw * pictureScale, sh * pictureScale, 96 * pictureScale, 96 * pictureScale, System.Windows.Media.PixelFormats.Pbgra32);
            shot.Render(single);
            var shotEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            shotEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));
            using (var shotFile = File.Create(path))
            {
                shotEncoder.Save(shotFile);
            }

            Console.WriteLine($"render-craft: wrote {path} ({part}, {sw}x{sh} at x{pictureScale})");
            Environment.Exit(0);
        }

        var left = new System.Windows.Controls.StackPanel { Width = 980 };
        left.Children.Add(new System.Windows.Controls.Border { Width = 980, Height = 860, Background = back, Child = page });
        var strip = new System.Windows.Controls.Border { Padding = new System.Windows.Thickness(20), Background = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x3a, 0x4a, 0x52), System.Windows.Media.Color.FromRgb(0x1c, 0x26, 0x2e), 60) };
        strip.Child = new System.Windows.Controls.Border { Child = overlayContent, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        left.Children.Add(strip);
        var right = new System.Windows.Controls.Border { Width = 720, Height = 1030, Background = back, Child = config, VerticalAlignment = System.Windows.VerticalAlignment.Top };
        var root = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Background = System.Windows.Media.Brushes.Black };
        root.Children.Add(left);
        root.Children.Add(new System.Windows.Controls.Border { Width = 20 });
        root.Children.Add(right);
        root.Measure(new System.Windows.Size(1740, 2400));
        root.Arrange(new System.Windows.Rect(0, 0, 1740, root.DesiredSize.Height));
        root.UpdateLayout();
        int w = 1740, h = (int)Math.Ceiling(root.DesiredSize.Height);
        var bitmap = Shot(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        // the question every configuration window asks when it is closed with changes
        var ask = Ui.ThemedMessageBox.Build(null, Ui.LocalizationManager.Instance["Config.Unsaved"], Ui.LocalizationManager.Instance["Craft.ConfigTitle"], System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Question, null, null, out _, (Ui.LocalizationManager.Instance["Config.Save"], Ui.LocalizationManager.Instance["Config.Discard"], Ui.LocalizationManager.Instance["Config.Cancel"]));
        ask.MinWidth = 0;
        var dialog = (System.Windows.UIElement)ask.Content;
        dialog.Measure(new System.Windows.Size(560, double.PositiveInfinity));
        int dw = (int)Math.Ceiling(dialog.DesiredSize.Width), dh = (int)Math.Ceiling(dialog.DesiredSize.Height);
        dialog.Measure(new System.Windows.Size(dw, dh));
        dialog.Arrange(new System.Windows.Rect(0, 0, dw, dh));
        dialog.UpdateLayout();
        var dialogBitmap = Shot(dw, dh, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        dialogBitmap.Render(dialog);
        var dialogEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        dialogEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(dialogBitmap));
        using (var file = File.Create(Path.ChangeExtension(path, null) + "-dialog.png"))
        {
            dialogEncoder.Save(file);
        }

        Console.WriteLine("render-craft: wrote " + path);
        Environment.Exit(0);
    }

    /// <summary>Checks that the settings window knows when something was changed (it asks before it closes then): the state read from the controls is the same twice and differs after a change. No window is shown.</summary>
    private static void RunSettingsDirtyCheckMode()
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/Ui/Styles/Shared.xaml", UriKind.Relative)));
        var settings = Ui.MeterSettings.Load();
        Ui.ThemeManager.Apply(app, settings);
        Ui.MainWindow.Headless = true;
        var window = new Ui.SettingsWindow(settings);
        var snapshot = typeof(Ui.SettingsWindow).GetMethod("Snapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        string first = (string)snapshot.Invoke(window, null)!;
        string second = (string)snapshot.Invoke(window, null)!;
        var box = (System.Windows.Controls.CheckBox)window.FindName("AlwaysOnTopBox");
        box.IsChecked = !(box.IsChecked ?? false);
        string changed = (string)snapshot.Invoke(window, null)!;
        box.IsChecked = !(box.IsChecked ?? false);
        string back = (string)snapshot.Invoke(window, null)!;
        Console.WriteLine($"settings-dirty-check: stable {first == second}, changed after a switch {changed != first}, back to the start {back == first}");
        Environment.Exit(first == second && changed != first && back == first ? 0 : 1);
    }

    /// <summary>Replays a recording and prints the three stocks the crafting tool counts (see <see cref="Aion2.Aion2Stock"/>); no window, nothing saved.</summary>
    private static void RunAion2StockCheckMode(string path, int serverPort)
    {
        var protocol = Aion2.Protocol.Aion2Protocol.Load();
        using var source = new Aion2.Aion2PacketCombatSource(protocol);
        Aion2.Aion2Stock.Reset();
        Aion2.Aion2Recipes.Reset();
        foreach (Aion2.Capture.TcpSegment segment in Aion2.Capture.SegmentRecording.Read(path))
        {
            bool fromServer = segment.Source.EndsWith(":" + serverPort, StringComparison.Ordinal);
            if (!fromServer && !segment.Destination.EndsWith(":" + serverPort, StringComparison.Ordinal))
            {
                continue;
            }

            source.Ingest(segment with { FromServer = fromServer });
            source.Poll(false);
        }

        Console.WriteLine($"aion2-stock-check: login frame {Aion2.Aion2Stock.SnapshotAt:HH:mm:ss}, last full listing {Aion2.Aion2Stock.ListedAt:HH:mm:ss}, learned recipes: {(Aion2.Aion2Recipes.HasData ? Aion2.Aion2CraftData.All.Count(r => Aion2.Aion2Recipes.IsKnown(r) && !r.Hidden) + " of " + Aion2.Aion2CraftData.All.Count(r => !r.Hidden) : "no list")}");
        if (source.Entities is Aion2.Aion2EntityDirectory ownedDirectory)
        {
            Console.WriteLine("aion2-stock-check: owned " + string.Join(", ", ownedDirectory.OwnedCounts().Select(o => $"{o.Key} {o.Value}")));
        }

        foreach ((string label, Aion2.StockSources sources) in new[] { ("Inventory", Aion2.StockSources.Inventory), ("Character storage", Aion2.StockSources.CharacterStorage), ("Account storage", Aion2.StockSources.AccountStorage) })
        {
            Dictionary<int, long> totals = Aion2.Aion2Stock.Totals(sources);
            Console.WriteLine($"-- {label}: {totals.Count} kinds, {totals.Values.Sum():N0} pieces");
            foreach ((int item, long count) in totals.OrderBy(t => t.Key))
            {
                Console.WriteLine($"   {Aion2.Protocol.Aion2ItemNames.NameOf(item, "de") ?? "#" + item} x{count:N0}");
            }
        }
    }

    private static void RunAion2OpcodeCensus(string[] paths, int serverPort)
    {
        var watch = new Aion2.Capture.UnknownFrameWatch(Aion2.Capture.UnknownFrameWatch.LoadKnown());
        int points = 0, frames = 0;
        var examples = new Dictionary<int, string>();
        foreach (string path in paths)
        {
            using var source = new Aion2.Aion2PacketCombatSource(Aion2.Protocol.Aion2Protocol.Load());
            Aion2.Protocol.Aion2FrameDecoder.FrameTap = (opcode, frame, at) =>
            {
                frames++;
                if (watch.Note(opcode, frame, at) is { } point)
                {
                    points++;
                    examples.TryAdd(opcode, Convert.ToHexString(frame[..Math.Min(frame.Length, 24)]));
                }
            };
            foreach (Aion2.Capture.TcpSegment segment in Aion2.Capture.SegmentRecording.Read(path))
            {
                bool fromServer = segment.Source.EndsWith(":" + serverPort, StringComparison.Ordinal);
                if (!fromServer && !segment.Destination.EndsWith(":" + serverPort, StringComparison.Ordinal))
                {
                    continue;
                }

                source.Ingest(segment with { FromServer = fromServer });
                source.Poll(false);
            }
        }

        Aion2.Protocol.Aion2FrameDecoder.FrameTap = null;
        Console.WriteLine($"aion2-opcode-census: {frames:N0} frames, {watch.UnknownOpcodes} unknown opcodes, {points} debug points (each would give a tone)");
        foreach (var u in watch.Unknown.OrderByDescending(u => u.Value.Count).Take(40))
        {
            Console.WriteLine($"  0x{u.Key:x4}  {u.Value.Count,7}x  {examples.GetValueOrDefault(u.Key)}");
        }
    }

    /// <summary>Replays recordings and measures the own-position estimate against the exact reports (every effect frame of the player carries his place): by the age of the previous report, and for several trust times.</summary>
    private static void RunAion2FrameDump(string path, int opcode, int max, int serverPort)
    {
        var protocol = Aion2.Protocol.Aion2Protocol.Load();
        using var source = new Aion2.Aion2PacketCombatSource(protocol);
        int shown = 0, total = 0;
        Aion2.Protocol.Aion2FrameDecoder.FrameTap = (op, frame, at) =>
        {
            if (op != opcode)
            {
                return;
            }

            total++;
            if (op == Aion2.Aion2LegionList.Opcode && shown < max && Aion2.Aion2LegionList.TryParse(frame, out List<Aion2.LegionMember> legion))
            {
                Console.WriteLine($"  legion list: {legion.Count} members ({legion.Count(m => m.ServerId == legion[0].ServerId)} on server {legion[0].ServerId}): " + string.Join(", ", legion.Select(m => $"{m.Name} L{m.Level} {Aion2.Protocol.Aion2SkillNames.ClassFromCode(m.ClassCode)} {m.ItemLevel}/{m.CombatPower}")));
            }

            if (shown++ < max)
            {
                Console.WriteLine($"{at:HH:mm:ss.fff} len {frame.Length}: {Convert.ToHexString(frame)}");
            }
        };
        foreach (Aion2.Capture.TcpSegment segment in Aion2.Capture.SegmentRecording.Read(path))
        {
            bool fromServer = segment.Source.EndsWith(":" + serverPort, StringComparison.Ordinal);
            if (!fromServer && !segment.Destination.EndsWith(":" + serverPort, StringComparison.Ordinal))
            {
                continue;
            }

            source.Ingest(segment with { FromServer = fromServer });
            source.Poll(false);
        }

        Aion2.Protocol.Aion2FrameDecoder.FrameTap = null;
        Console.WriteLine($"aion2-frame-dump: {total} frames of opcode 0x{opcode:x4}");
    }

    private static void RunAion2CubeCheckMode(string[] paths, int serverPort)
    {
        foreach (string path in paths)
        {
            var protocol = Aion2.Protocol.Aion2Protocol.Load();
            using var source = new Aion2.Aion2PacketCombatSource(protocol);
            var directory = (Aion2.Aion2EntityDirectory)source.Entities;
            DateTime clock = DateTime.MinValue, lastSecond = DateTime.MinValue, inSightSince = DateTime.MinValue;
            directory.Clock = () => clock;
            int seconds = 0, current = 0, exact = 0, around = 0, departures = 0;
            int lastCubes = 0;
            var distances = new List<double>();
            void Flush()
            {
                if (seconds > 0)
                {
                    Console.WriteLine($"  cube(s) in sight from {inSightSince:HH:mm:ss}: {seconds} s, own position current {current} s (exact fix {exact} s, entities around {around} s), silent {seconds - current} s; nearest cube at {(distances.Count > 0 ? distances.Min() : double.NaN):0} to {(distances.Count > 0 ? distances.Max() : double.NaN):0} m");
                }

                seconds = current = exact = around = 0;
                distances.Clear();
            }

            Console.WriteLine(Path.GetFileName(path));
            foreach (Aion2.Capture.TcpSegment segment in Aion2.Capture.SegmentRecording.Read(path))
            {
                bool fromServer = segment.Source.EndsWith(":" + serverPort, StringComparison.Ordinal);
                if (!fromServer && !segment.Destination.EndsWith(":" + serverPort, StringComparison.Ordinal))
                {
                    continue;
                }

                clock = segment.Timestamp;
                source.Ingest(segment with { FromServer = fromServer });
                source.Poll(false);
                if ((clock - lastSecond).TotalSeconds < 1)
                {
                    continue;
                }

                lastSecond = clock;
                var cubes = directory.LiveCubes();
                if (directory.CubeDepartures > departures)
                {
                    Console.WriteLine($"  {clock:HH:mm:ss} a cube went out of sight (departure {directory.CubeDepartures})");
                    departures = directory.CubeDepartures;
                }

                if (cubes.Count == 0)
                {
                    if (lastCubes > 0)
                    {
                        Flush();
                    }

                    lastCubes = 0;
                    continue;
                }

                if (lastCubes == 0)
                {
                    inSightSince = clock;
                }

                lastCubes = cubes.Count;
                seconds++;
                if (directory.BestPosition is { } me && (clock - me.At).TotalSeconds < 5)
                {
                    current++;
                    if (directory.LocalPosition is { } fix && (clock - fix.At) <= Aion2.Aion2EntityDirectory.FixTrust)
                    {
                        exact++;
                    }
                    else
                    {
                        around++;
                    }

                    distances.Add(cubes.Min(c => Math.Sqrt(((double)c.X - me.X) * (c.X - me.X) + ((double)c.Y - me.Y) * (c.Y - me.Y))) / 100.0);
                }
            }

            if (lastCubes > 0)
            {
                Flush();
            }
        }
    }

    private static void RunAion2PositionCheckMode(string[] paths, int serverPort)
    {
        var variants = new List<(string Name, double MaxAge, Aion2.Aion2EntityDirectory.AroundMode How, int Min)>();
        foreach (double age in new[] { 5.0, 10.0, 20.0, 60.0, 120.0 })
        {
            foreach (var how in new[] { Aion2.Aion2EntityDirectory.AroundMode.Mean, Aion2.Aion2EntityDirectory.AroundMode.Median, Aion2.Aion2EntityDirectory.AroundMode.Trimmed })
            {
                variants.Add(($"{how} <= {age:0}s", age, how, 3));
            }
        }

        var probes = new List<(double Age, double Prev, double[] Env, int Entities)>();
        foreach (string path in paths)
        {
            var protocol = Aion2.Protocol.Aion2Protocol.Load();
            using var source = new Aion2.Aion2PacketCombatSource(protocol);
            var directory = (Aion2.Aion2EntityDirectory)source.Entities;
            DateTime clock = DateTime.MinValue;
            directory.Clock = () => clock;
            directory.PositionProbe += p =>
            {
                double[] env = variants.Select(v => directory.AroundAt(p.Now, v.MaxAge, v.How, v.Min) is { } e ? Math.Sqrt(((double)e.X - p.X) * (e.X - p.X) + ((double)e.Y - p.Y) * (e.Y - p.Y)) / 100.0 : double.NaN).ToArray();
                probes.Add((p.PreviousAgeSeconds, p.PreviousError, env, p.Entities));
            };
            foreach (Aion2.Capture.TcpSegment segment in Aion2.Capture.SegmentRecording.Read(path))
            {
                bool fromServer = segment.Source.EndsWith(":" + serverPort, StringComparison.Ordinal);
                if (!fromServer && !segment.Destination.EndsWith(":" + serverPort, StringComparison.Ordinal))
                {
                    continue;
                }

                clock = segment.Timestamp;
                source.Ingest(segment with { FromServer = fromServer });
                source.Poll(false);
            }
        }

        static double Median(IEnumerable<double> v) { var l = v.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList(); return l.Count == 0 ? double.NaN : l[l.Count / 2]; }
        static double P90(IEnumerable<double> v) { var l = v.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList(); return l.Count == 0 ? double.NaN : l[(int)(l.Count * 0.9)]; }
        Console.WriteLine($"aion2-position-check: {probes.Count} exact reports with a previous one");
        var buckets = new[] { (0.0, 1.0), (1.0, 2.5), (2.5, 5.0), (5.0, 15.0), (15.0, 1e9) };
        Console.WriteLine("error in metres, median / p90, by the age of the previous exact report (count in brackets); '-' = no environment of 3+ entities");
        Console.Write($"{"",-22}");
        foreach ((double lo, double hi) in buckets)
        {
            Console.Write($"{lo:0.#}-{(hi > 1e8 ? "inf" : hi.ToString("0.#"))} ({probes.Count(p => p.Age >= lo && p.Age < hi)})".PadRight(20));
        }

        Console.WriteLine();
        Console.Write($"{"previous report",-22}");
        foreach ((double lo, double hi) in buckets)
        {
            var sel = probes.Where(p => p.Age >= lo && p.Age < hi).ToList();
            Console.Write($"{Median(sel.Select(p => p.Prev)):0.0} / {P90(sel.Select(p => p.Prev)):0.0}".PadRight(20));
        }

        Console.WriteLine();
        for (int k = 0; k < variants.Count; k++)
        {
            Console.Write($"{variants[k].Name,-22}");
            foreach ((double lo, double hi) in buckets)
            {
                var sel = probes.Where(p => p.Age >= lo && p.Age < hi).Select(p => p.Env[k]).ToList();
                Console.Write($"{Median(sel):0.0} / {P90(sel):0.0} [{sel.Count(v => !double.IsNaN(v))}]".PadRight(20));
            }

            Console.WriteLine();
        }
    }

    private static void RunAion2ReplayMode(string path, int serverPort, string? ownName)
    {
        var protocol = Aion2.Protocol.Aion2Protocol.Load();
        using var source = new Aion2.Aion2PacketCombatSource(protocol);
        (source.Entities as Aion2.Aion2EntityDirectory)?.SetConfiguredLocalName(ownName);
        if (source.Entities is Aion2.Aion2EntityDirectory idWatch)
        {
            idWatch.LocalIdChanged += (a, b) => Console.WriteLine($"aion2-replay: own combat id changed {a} -> {b}");
            int moved = 0;
            idWatch.IdentityMoved += (a, b) => { if (++moved <= 6) { Console.WriteLine($"aion2-replay: player {idWatch.NameFor(b)} announced under a new id {a} -> {b}"); } };
        }

        var events = new List<Combat.DamageEvent>();
        var stays = new History.SessionRecorder(zone => Aion2.Protocol.Aion2Zones.IsInstance(zone) ? History.SessionKind.Instance : History.SessionKind.Map);
        if (source.Entities is Aion2.Aion2EntityDirectory replayDirectory)
        {
            stays.Identify = id => (replayDirectory.NameFor(id) ?? replayDirectory.MonsterNameFor(id), replayDirectory.ClassOf(id));
            stays.HpSource = (id, from, to) =>
            {
                var samples = replayDirectory.HitPoints.SamplesAround(id, from, to);
                return samples.Count < 2 ? null : new History.TargetHp(replayDirectory.HitPoints.HighestSeen(id) ?? 0, samples, replayDirectory.HitPoints.ResetsOf(id));
            };
        }
        int segments = 0;
        string? petLog = null;
        {
            // A replay never writes to the real pet-farm.log: its lines go to a throw-away file.
            petLog = Path.Combine(Path.GetTempPath(), "aion2-replay-pet-farm.log");
            File.Delete(petLog);
            Aion2.PetFarmLog.PathOverride = petLog;
        }

        foreach (Aion2.Capture.TcpSegment segment in Aion2.Capture.SegmentRecording.Read(path))
        {
            // The recording holds every TCP stream of the machine; only the game server's counts,
            // and the direction is decided by the port (the recorder's own flag is a guess when it
            // ran without a port filter).
            bool fromServer = segment.Source.EndsWith(":" + serverPort, StringComparison.Ordinal);
            if (!fromServer && !segment.Destination.EndsWith(":" + serverPort, StringComparison.Ordinal))
            {
                continue;
            }

            segments++;
            source.Ingest(segment with { FromServer = fromServer });
            Combat.Sources.CombatBatch polled = source.Poll(false);
            events.AddRange(polled.Damage);
            stays.Feed(polled);
            if (source.Entities is Aion2.Aion2EntityDirectory ownersIn)
            {
                // summons whose owner turned up later: their earlier hits go to the owner, as the meter does
                foreach ((int summon, int owner) in ownersIn.DrainResolvedOwners())
                {
                    stays.Reattribute(summon, owner);
                    for (int i = 0; i < events.Count; i++)
                    {
                        if (events[i].SourceObjectId == summon)
                        {
                            events[i] = events[i] with { SourceObjectId = owner };
                        }
                    }
                }
            }
            foreach (Combat.Sources.ZoneChange zone in polled.ZoneChanges)
            {
                Console.WriteLine($"aion2-replay: zone {zone.ZoneId} entered at {zone.Timestamp:yyyy-MM-dd HH:mm:ss}");
            }
        }

        if (events.Count > 0)
        {
            stays.CloseCurrent(events[^1].Timestamp);
        }

        foreach (History.SessionInfo stay in stays.Sessions)
        {
            Console.WriteLine($"aion2-replay: session {stay.Number} zone {stay.ZoneId} {stay.Kind} {stay.Start:HH:mm:ss} - {(stay.End is DateTime stayEnd ? stayEnd.ToString("HH:mm:ss") : "open")}: {stay.Data.Events.Count} event(s)");
            // The plausibility check and the rage guess for every boss fight of the stay (see History/SessionAnalysis.cs).
            History.SessionAnalysis analysis = History.SessionAnalyzer.Analyze(stay);
            foreach (History.BossAttempt attempt in analysis.Attempts)
            {
                string rage = attempt.Enrage is { } r ? $"rage after {r.At:mm\\:ss} x{r.Factor}" : "no rage seen";
                string check = attempt.Check is { } c ? $"{c.Ratio:P0} of {c.Lost:N0} HP lost, {(c.Killed ? "killed" : "alive")}" : "no HP data";
                Console.WriteLine($"aion2-replay:   boss {attempt.Name} {attempt.Start:HH:mm:ss} for {attempt.Duration:mm\\:ss}: {attempt.Counted:N0} damage, {check}, {rage}");
                if (Environment.GetEnvironmentVariable("AION2_ENRAGE") == "1")
                {
                    // Boss damage per 30 s, to look at by eye where it jumps.
                    var bins = stay.Data.Events.Where(e => !e.IsHeal && e.SourceObjectId == attempt.TargetId && e.Timestamp >= attempt.Start && e.Timestamp <= attempt.End)
                        .GroupBy(e => (int)((e.Timestamp - attempt.Start).TotalSeconds / 30)).OrderBy(g => g.Key).Select(g => $"{g.Key * 30 / 60}:{g.Key * 30 % 60:00}={g.Sum(e => e.Amount):N0}");
                    Console.WriteLine("aion2-replay:     boss damage per 30 s: " + string.Join("  ", bins));
                }
            }
        }

        if (source.Entities is Aion2.Aion2EntityDirectory rowsDir && events.Count > 0)
        {
            DateTime rowsFrom = events[^1].Timestamp.AddMinutes(-10);
            foreach (var g in events.Where(e => !e.IsHeal && e.Timestamp >= rowsFrom && rowsDir.ClassOf(e.SourceObjectId) is not null).GroupBy(e => e.SourceObjectId).OrderByDescending(g => g.Sum(e => (long)e.Amount)))
            {
                Console.WriteLine($"aion2-replay: rows id {g.Key} name {rowsDir.NameFor(g.Key)} class {rowsDir.ClassOf(g.Key)} hits {g.Count()} dmg {g.Sum(e => (long)e.Amount):N0} first {g.Min(e => e.Timestamp):HH:mm:ss} last {g.Max(e => e.Timestamp):HH:mm:ss} skills {string.Join(",", g.GroupBy(e => e.SkillId).OrderByDescending(x => x.Count()).Take(4).Select(x => x.Key))}");
            }
        }

        foreach (Aion2.LootSession loot in Aion2.Aion2Loot.Sessions.AsEnumerable().Reverse())
        {
            Console.WriteLine($"aion2-replay: loot session {loot.Start:HH:mm:ss} - {loot.End:HH:mm:ss}, {loot.Visits} visit(s), {loot.ActiveSeconds / 60:0} min looted");
            foreach (Aion2.LootMap map in loot.Maps)
            {
                Console.WriteLine($"aion2-replay:   zone {map.Zone}: " + string.Join(", ", map.Items.Select(i => $"{Aion2.Protocol.Aion2ItemNames.NameOf(i.ItemId, "en") ?? "#" + i.ItemId} x{i.Count}")));
            }
        }

        if (source.Entities is Aion2.Aion2EntityDirectory idEnd)
        {
            Console.WriteLine($"aion2-replay: {idEnd.IdentityMovesSeen} player(s) came back under a new combat id");
        }

        if (Environment.GetEnvironmentVariable("AION2_STATS") == "1" && source.Entities is Aion2.Aion2EntityDirectory statsDirectory && statsDirectory.LocalStats is { } stats)
        {
            Console.WriteLine($"aion2-replay: stat frame of the own character, {stats.Count} ids: " + string.Join("  ", stats.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        }

        if (Environment.GetEnvironmentVariable("AION2_STATREC") is { Length: > 0 } statRecPath && source.Entities is Aion2.Aion2EntityDirectory recDirectory && recDirectory.LocalStats is { } recStats && events.Count > 0)
        {
            // a stat recording over the whole replay, to check the document's size and its shape against the server on real data (nothing is sent)
            DateTime recStart = events.Min(e => e.Timestamp), recEnd = events.Max(e => e.Timestamp);
            int recLocal = recDirectory.LocalPlayerId;
            var recMine = events.Where(e => e.SourceObjectId == recLocal).ToList();
            Combat.FightExtras recExtras = recDirectory.Combat.Slice(recStart, recEnd, _ => null);
            Aion2.Aion2CharacterInfo? recCharacter = recDirectory.LocalCharacter;
            int? recServer = recCharacter is { ServerId: > 0 } ? recCharacter.ServerId : null;
            var recSetup = new Aion2.StatRecordingSetup("expert", "back", 60, 30, 0, 0, 0, 0, 0, 150, 100);
            string recJson = Aion2.StatRecording.Build(new string('a', 32), recStart, recEnd, recSetup,
                new Aion2.StatRecordingCharacter(recCharacter is { ClassCode: > 0 } ? (recCharacter.ClassCode - 1) / 4 : null, recCharacter?.Level, recServer, Aion2.StatRecording.FactionOf(recServer)),
                recStats, recStats, Array.Empty<Aion2.StatChange>(), recMine, recExtras, recLocal, id => recDirectory.NpcIdOf(id), id => recDirectory.MonsterNameFor(id));
            File.WriteAllText(statRecPath, recJson);
            Console.WriteLine($"aion2-replay: stat recording of {recMine.Count} own events written to {statRecPath} ({recJson.Length / 1024} KB)");
        }

        Console.WriteLine($"aion2-replay: tickets belong now to {Aion2.Aion2Tickets.CurrentName ?? "nobody"}");
        foreach (Aion2.TicketCharacter ticketed in Aion2.Aion2Tickets.Snapshot())
        {
            ticketed.Tickets.TryGetValue(Aion2.Aion2Tickets.OdyleId, out Aion2.TicketValue odyle);
            ticketed.Tickets.TryGetValue(Aion2.Aion2Tickets.MissionsId, out Aion2.TicketValue missions);
            ticketed.Tickets.TryGetValue(Aion2.Aion2Tickets.PremiumId, out Aion2.TicketValue premium);
            Console.WriteLine($"aion2-replay: tickets of {ticketed.Name} (server {ticketed.ServerId}): Odyle {odyle.Count} + {odyle.Bonus}, missions {missions.Count} handed in, premium until {(premium.TimeMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(premium.TimeMs).ToLocalTime().ToString("g") : "none")}");
        }

        if (events.Count > 0)
        {
            // what the hit data says about how hits landed, and whether the flags do what they are called (a double hit should do more than the same skill without it)
            Console.WriteLine("aion2-replay: hit kinds (hits with the flag; skills compared; median damage ratio with / without):");
            foreach (var effect in Combat.HitKindStats.Effects(events))
            {
                Console.WriteLine($"  {effect.Name,-8} {effect.Hits,7} hits  {effect.SkillsCompared,3} skills  x{effect.MedianRatio:0.00}");
            }
        }

        if (source.Entities is Aion2.Aion2EntityDirectory combatDirectory)
        {
            if (events.Count > 1 && source.Entities.LocalPlayerId >= 0)
            {
                // the rotation view's numbers for the local player over the whole recording
                DateTime from = events.Min(e => e.Timestamp), to = events.Max(e => e.Timestamp);
                var extras = combatDirectory.Combat.Slice(from, to, id => source.Entities.NameFor(id));
                var replayExtras = combatDirectory.Combat.Slice(from, to, id => source.Entities.NameFor(id), _ => true);
                Console.WriteLine($"aion2-replay: replay data: {replayExtras.Tracks?.Count ?? 0} tracks ({replayExtras.Tracks?.Sum(t => t.T.Length) ?? 0} points), {replayExtras.Windups?.Count ?? 0} windups, {replayExtras.Deaths?.Count ?? 0} deaths");
                foreach (var track in (replayExtras.Tracks ?? Array.Empty<Combat.PositionTrack>()).OrderByDescending(t => t.T.Length).Take(4))
                {
                    Console.WriteLine($"  entity {track.Entity,6} {source.Entities.NameFor(track.Entity),-14} {track.T.Length,5} points over {track.T[^1] / 1000.0,7:0} s, x {track.X.Min()}..{track.X.Max()} y {track.Y.Min()}..{track.Y.Max()}");
                }

                var own = events.Where(e => e.SourceObjectId == source.Entities.LocalPlayerId).ToList();
                var report = Combat.RotationAnalysis.Build(extras, source.Entities.LocalPlayerId, from, to, own, id => source.Entities.NameFor(id));
                Console.WriteLine($"aion2-replay: rotation of the local player: {report.Uses.Count} skill uses ({report.Skills.Count} skills), {report.Buffs.Count} effects, {report.Staggers.Count} staggers, {extras.Staggers.Count} stagger readings, {extras.Buffs.Count} effect events");
                Console.WriteLine("  opener: " + string.Join(" > ", report.Opener.Select(u => $"{Aion2.Protocol.Aion2SkillNames.Display(u.Name)}@{u.At:0.0}")));
                foreach (var skill in report.Skills.Take(6))
                {
                    Console.WriteLine($"  {skill.Name,-26} {skill.Uses,4} uses {skill.PerMinute,5:0.0}/min gap {skill.AvgGapSeconds,5:0.0}s share {skill.SharePercent,5:0.0}%");
                }

                foreach (var buff in report.Buffs.Take(6))
                {
                    Console.WriteLine($"  buff {buff.Name,-24} {buff.UptimePercent,5:0.0}% (you {buff.FromYouPercent,5:0.0}%) x{buff.Applications} {buff.Givers}");
                }
            }

            var lessons = combatDirectory.Combat.Lessons();
            Console.WriteLine($"aion2-replay: {lessons.Count} skill(s) learnt from casts and hits, {lessons.Count(l => l.Lesson.IsArea)} of them areas");
            foreach (var (skillId, lesson) in lessons.Where(l => l.Lesson.Seen > 0).Take(14))
            {
                Console.WriteLine($"  {skillId,9} {Aion2.Protocol.Aion2SkillNames.NameOf(skillId),-28} cast {lesson.CastMs,5} ms  seen {lesson.Seen,3}  wide {lesson.Wide,3}  single {lesson.Single,3}  lands {(lesson.LandMs is int land ? land + " ms" : "?"),-7} max on you {lesson.MaxOnYou}");
            }
        }

        Console.WriteLine($"aion2-replay: {segments} segment(s) on port {serverPort}, {events.Count} damage/heal event(s), calibrated={protocol.IsCalibrated}");
        if (events.Any(e => e.Estimated))
        {
            Console.WriteLine($"aion2-replay: {events.Count(e => e.Estimated)} of them estimated from hit notices ({events.Where(e => e.Estimated).Sum(e => e.Amount):N0} damage, {events.Where(e => e.Estimated).Select(e => e.SourceObjectId).Distinct().Count()} player(s)): world boss, the server sent no amounts");
        }
        foreach (var bossMap in Aion2.Aion2FieldBosses.Maps.Where(m => m.Id != 0))
        {
            var now = DateTime.Now;
            var lines = bossMap.Bosses.Select(b => (Boss: b, Status: Aion2.Aion2FieldBosses.StatusOf(b, now))).Where(x => x.Status.Phase != Aion2.BossPhase.Unknown).ToList();
            Console.WriteLine($"aion2-replay: field bosses of {bossMap.NameIn("en")} (list {bossMap.Id}): {lines.Count} of {bossMap.Bosses.Count} with a state");
            foreach (var (boss, status) in lines)
            {
                Console.WriteLine($"  {boss.NameIn("en"),-34} {status.Phase,-8} {(status.At is { } at ? at.ToString("yyyy-MM-dd HH:mm:ss") : "")}");
            }
        }

        // AION2_DUMP=<file>: every event as CSV (time, source id and name, target id, amount, heal, tick,
        // crit, skill) for working out how a total could be reproduced from the recording.
        if (Environment.GetEnvironmentVariable("AION2_DUMP") is { Length: > 0 } dumpPath)
        {
            using var dump = new StreamWriter(dumpPath);
            dump.WriteLine("time;source;sourceName;target;amount;heal;tick;crit;skill;viaSummon");
            foreach (var e in events)
            {
                dump.WriteLine(string.Join(';', e.Timestamp.ToString("O"), e.SourceObjectId, source.Entities.NameFor(e.SourceObjectId), e.TargetObjectId,
                    e.Amount, e.IsHeal ? 1 : 0, e.IsTick ? 1 : 0, e.IsCritical ? 1 : 0, e.Skill, e.ViaSummon ? 1 : 0));
            }
        }

        if (petLog is not null && source.Entities is Aion2.Aion2EntityDirectory petDirectory)
        {
            var pets = petDirectory.LocalPetStates;
            Console.WriteLine($"aion2-replay: {pets.Count} pet(s) in the pet list, {pets.Count(p => p.Level >= 3)} at the top level");
            foreach (var pet in pets.Where(p => p.Level >= 2).OrderByDescending(p => p.Level).ThenByDescending(p => p.Progress).Take(12))
            {
                Console.WriteLine($"  {Aion2.Protocol.Aion2Pets.PetName(pet.PetId, "de") ?? "?",-30} level {pet.Level}  {pet.Progress}/{Aion2.Protocol.Aion2Pets.ProgressNeeded(pet.Level)}");
            }

            var factions = petDirectory.Snapshot().Names.Keys.GroupBy(id => petDirectory.FactionOf(id) ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
            var unknownNames = petDirectory.Snapshot().Names.Where(kv => petDirectory.FactionOf(kv.Key) is null).ToList();
            Console.WriteLine("aion2-replay: unknown faction, sample: " + string.Join(" | ", unknownNames.Take(40).Select(kv => $"{kv.Value}{(petDirectory.IsKnownPlayer(kv.Key) ? "(P)" : "")}{(petDirectory.NpcIdOf(kv.Key) is not null ? "(npc)" : "")}")));
            var bitByServerFaction = petDirectory.Snapshot().Names.Keys
                .Where(id => petDirectory.ServerIdOf(id) is int sv && sv / 1000 is 1 or 2)
                .GroupBy(id => (Server: petDirectory.ServerIdOf(id)!.Value / 1000, Bit: petDirectory.SeenProfileOf(id)?.ClassBits ?? -1))
                .OrderBy(g => g.Key.Server).ThenBy(g => g.Key.Bit).Select(g => $"server {g.Key.Server}xxx class bits {g.Key.Bit}: {g.Count()}");
            Console.WriteLine("aion2-replay: class bits of players with a known server: " + string.Join(" | ", bitByServerFaction));
            var actors = events.Select(e => e.SourceObjectId).Distinct().Where(id => petDirectory.NpcIdOf(id) is null && petDirectory.NameFor(id) is not null).ToList();
            Console.WriteLine($"aion2-replay: players that dealt damage or healed: {actors.Count}, faction known for {actors.Count(id => petDirectory.FactionOf(id) is not null)} ({actors.Count(id => petDirectory.FactionOf(id) == "Asmodian")} Asmodian)");
            Console.WriteLine($"aion2-replay: of {unknownNames.Count} unknown, {unknownNames.Count(kv => petDirectory.IsKnownPlayer(kv.Key))} are known players, {unknownNames.Count(kv => petDirectory.NpcIdOf(kv.Key) is not null)} are npcs");
            Console.WriteLine("aion2-replay: factions of the named players: " + string.Join(", ", factions.Select(kv => $"{kv.Key} {kv.Value}")));
            var detected = Aion2.Protocol.Aion2Maps.Detect(petDirectory.RecentMobPositions());
            Console.WriteLine($"aion2-replay: map detected from the last {petDirectory.RecentMobPositions().Count} monsters: {detected?.Title ?? "none"}; own position {(petDirectory.LocalPosition is { } lp ? $"({lp.X:0}, {lp.Y:0})" : "unknown")}");
            foreach (var mob in petDirectory.RecentMobPositions().TakeLast(6))
            {
                Console.WriteLine($"aion2-replay:   recent monster {Aion2.Protocol.Aion2Npcs.NameOf(mob.NpcId, "en") ?? "?"} [{mob.NpcId}] at ({mob.X:0}, {mob.Y:0}), pets {string.Join("/", Aion2.Protocol.Aion2Pets.PetsOfNpc(mob.NpcId))}");
            }

            if (petDirectory.LocalTarget is { } target)
            {
                var petsOfTarget = target.NpcId is int tn ? Aion2.Protocol.Aion2Pets.PetsOfNpc(tn) : Array.Empty<int>();
                Console.WriteLine($"aion2-replay: last marked monster: entity {target.EntityId}, npc {target.NpcId} ({(target.NpcId is int n2 ? Aion2.Protocol.Aion2Npcs.NameOf(n2, "en") : "?")}) -> pet(s) " +
                    string.Join(", ", petsOfTarget.Select(p => Aion2.Protocol.Aion2Pets.PetName(p, "de"))));
            }

            if (File.Exists(petLog))
            {
                Console.WriteLine("aion2-replay: pet farm log:");
                foreach (string line in File.ReadLines(petLog).Take(40))
                {
                    Console.WriteLine("  " + line);
                }
            }
        }

        if (events.Count == 0)
        {
            return;
        }

        TimeSpan span = events.Max(e => e.Timestamp) - events.Min(e => e.Timestamp);
        Console.WriteLine($"aion2-replay: span {span:hh\\:mm\\:ss}");
        foreach (var actor in events.Where(e => !e.IsHeal).GroupBy(e => e.SourceObjectId).OrderByDescending(g => g.Sum(e => e.Amount)).Take(12))
        {
            long total = actor.Sum(e => e.Amount);
            int crits = actor.Count(e => e.IsCritical);
            Console.WriteLine($"  {source.Entities.NameFor(actor.Key) ?? actor.Key.ToString(),-22} damage {total,9:N0}  hits {actor.Count(),5}  crit {100.0 * crits / actor.Count(),4:F0}%");
            foreach (var skill in actor.GroupBy(e => e.Skill).OrderByDescending(g => g.Sum(e => e.Amount)).Take(3))
            {
                Console.WriteLine($"      {skill.Key,-26} {skill.Sum(e => e.Amount),9:N0} in {skill.Count(),4} hit(s), avg {skill.Average(e => e.Amount):F0}");
            }
        }

        if (source.Entities is Aion2.Aion2EntityDirectory partyDirectory && Environment.GetEnvironmentVariable("AION2_PARTY") is { Length: > 0 })
        {
            // Who the meter thinks is in the party, and for which players the server reports hit points.
            Console.WriteLine("party roster: " + string.Join(", ", partyDirectory.PartyNames));
            foreach ((int id, int readings) in partyDirectory.HitPoints.ReadingCounts().OrderByDescending(kv => kv.Value))
            {
                if (partyDirectory.IsKnownPlayer(id))
                {
                    string pname = partyDirectory.NameFor(id) ?? id.ToString();
                    Console.WriteLine($"  hp readings for player {pname,-18} id {id,6}: {readings,5}  inRoster={partyDirectory.PartyNames.Contains(pname)} local={partyDirectory.IsLocalPlayer(id)}");
                }
            }
        }

        if (source.Entities is Aion2.Aion2EntityDirectory directory)
        {
            Console.WriteLine("aion2-replay: " + directory.Describe());
        }

        if ((source.Entities as Aion2.Aion2EntityDirectory)?.LocalCharacter is { } character)
        {
            Console.WriteLine($"aion2-replay: character {character.Name}, class code {character.ClassCode}, level {character.Level}, {character.Equipment.Count} equipped item(s), server id {character.ServerId} = {Aion2.Protocol.Aion2Servers.NameOf(character.ServerId)}");
            var directory2 = (Aion2.Aion2EntityDirectory)source.Entities;
            foreach (var item in directory2.LocalEquipment)
            {
                var info = Aion2.Protocol.Aion2ItemCatalog.Find(item.ItemId);
                Console.WriteLine($"    slot {item.SlotIndex,2}: {info?.Name ?? item.ItemId.ToString()}{(item.Enchant > 0 ? " +" + item.Enchant : "")} (item level {info?.ItemLevel}, grade {info?.Grade}, tier {info?.Tier})");
            }

            var skillNames = Aion2.Protocol.Aion2SkillNames.Load();
            foreach (var board in directory2.LocalDaevanion)
            {
                var sum = Aion2.Protocol.Aion2DaevanionCatalog.Summarize(board.BoardId, board.NodeIds);
                Console.WriteLine($"    daevanion {sum.Name}: {sum.ActiveNodes} nodes ({sum.KnownNodes} known) | skills {string.Join(", ", sum.SkillBonuses.Select(kv => skillNames.GetValueOrDefault(kv.Key, kv.Key.ToString()) + " +" + kv.Value))} | {string.Join(", ", sum.Stats.Select(kv => kv.Key + "+" + kv.Value))}");
            }

            Console.WriteLine($"    skills: {directory2.LocalSkills.Count}");
            foreach (var skill in directory2.LocalSkills.Where(k => k.SkillId % 10000 == 0))
            {
                Console.WriteLine($"      {skillNames.GetValueOrDefault(skill.SkillId, skill.SkillId.ToString()),-28} level {skill.Level}{(skill.Level > skill.BaseLevel ? $" ({skill.BaseLevel}+{skill.Level - skill.BaseLevel})" : "")}");
            }
        }

        int local = source.Entities.LocalPlayerId;
        if (source.Entities is Aion2.Aion2EntityDirectory bossDirectory)
        {
            if (Environment.GetEnvironmentVariable("AION2_DUMP") is { Length: > 0 } hpDumpPath)
            {
                using var hpDump = new StreamWriter(hpDumpPath + ".hp.csv");
                hpDump.WriteLine("entity;time;hp");
                foreach (var (bossEntity, _) in bossDirectory.KnownBosses())
                {
                    foreach (var sample in bossDirectory.HitPoints.SamplesAround(bossEntity, DateTime.MinValue, DateTime.MaxValue))
                    {
                        hpDump.WriteLine($"{bossEntity};{sample.At:O};{sample.Hp}");
                    }
                }
            }

            foreach (var (entityId, npcId) in bossDirectory.KnownBosses())
            {
                var info = Aion2.Protocol.Aion2BossCatalog.Find(npcId);
                int hitCount = events.Count(e => !e.IsHeal && e.TargetObjectId == entityId);
                long taken = events.Where(e => !e.IsHeal && e.TargetObjectId == entityId).Sum(e => e.Amount);
                Console.WriteLine($"aion2-replay: boss entity {entityId} = NPC {npcId} {info?.Name} ({info?.Instance}): {hitCount} hits, {taken:N0} damage taken");
                var resets = bossDirectory.HitPoints.ResetsOf(entityId);
                long? highest = bossDirectory.HitPoints.HighestSeen(entityId);
                if (highest is long full && full > 0)
                {
                    DateTime attemptStart = resets.Count > 0 ? resets[^1] : DateTime.MinValue;
                    var attemptHits = events.Where(e => !e.IsHeal && e.TargetObjectId == entityId && e.Timestamp >= attemptStart).ToList();
                    var readings = bossDirectory.HitPoints.SamplesAround(entityId, attemptStart, DateTime.MaxValue).Select(r => (r.At, r.Hp)).ToList();
                    var ownHeals = events.Where(e => e.IsHeal && e.SourceObjectId == entityId && e.TargetObjectId == entityId && e.Timestamp >= attemptStart);
                    var check = Combat.HpCheck.Evaluate(readings, attemptHits.Concat(ownHeals).ToList(), full);
                    Console.WriteLine($"aion2-replay:   hit points: max {full:N0}, {resets.Count} reset(s), last attempt {attemptHits.Sum(e => e.Amount):N0} damage counted"
                        + (check is null ? "" : $"; HP lost {check.Lost:N0}, counted {check.Counted:N0} ({check.Ratio:P1}), killed {check.Killed}, verdict {check.Verdict}"
                            + (check.Healed > 0 ? $", healed itself {check.Healed:N0}" : "")));
                }
            }
        }

        foreach (var (serverId, names) in source.ServerIdsSeen.OrderBy(k => k.Key))
        {
            Console.WriteLine($"aion2-replay: server id {serverId} = {Aion2.Protocol.Aion2Servers.NameOf(serverId)}: {names.Count} sample name(s) {string.Join(", ", names.Take(6))}");
        }

        Console.WriteLine($"aion2-replay: local player id {(local >= 0 ? local.ToString() : "unknown")} = {(local >= 0 ? source.Entities.NameFor(local) : "-")}");
        long heal = events.Where(e => e.IsHeal).Sum(e => e.Amount);
        Console.WriteLine($"aion2-replay: self-heals {heal:N0} ({events.Count(e => e.IsHeal)} event(s))");
    }
}
