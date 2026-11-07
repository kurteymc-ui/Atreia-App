using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AionDPS.Aion2;
using AionDPS.Aion2.Protocol;
using AionDPS.Combat;
using AionDPS.Combat.Sources;
using AionDPS.Data;
using AionDPS.History;
using AionDPS.Update;
using AionDPS.Upload;
using VelopackUpdateInfo = Velopack.UpdateInfo;

namespace AionDPS.Ui;

/// <summary>
/// The main meter window. Holds its own LiveAggregator, fed from the Aion 2 packet capture.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<PlayerRow> _rows = new();
    private ListCollectionView? _playersView;
    private ListCollectionView? _overlayView;

    /// <summary>Running totals over the events (see DamageIndex); the refresh takes in what is new instead of rescanning.</summary>
    private readonly DamageIndex _index = new();

    /// <summary>The stress test sets this to compare the totals of the index with those of a full rescan.</summary>
    internal bool ForceFullRefresh { get; set; }

    private readonly Dictionary<int, PlayerRow> _rowsByObjectId = new();
    private readonly Dictionary<int, string> _targetNames = new();

    /// <summary>
    /// Identity (name/class/level) per source object id, independent of PlayerRow. RefreshRows
    /// removes and later re-creates a row for any source that drops out of the current Mob/Boss
    /// filter, but a fresh PlayerRow only ever gets "0x########" -- the real identity, set once
    /// directly on the row object, would otherwise be lost for good the moment that row got
    /// filtered out once. Mirrors _targetNames, which already solved the identical problem for
    /// target display names. Reserved for the network (Aion 2 packet-capture) path, which does not
    /// populate it yet -- see ResolveDisplayName's own remarks.
    /// </summary>
    private readonly Dictionary<int, (string Name, string ClassName, int Level)> _playerIdentities = new();

    private readonly LiveAggregator _aggregator = new();
    private NativeOverlay? _overlay;
    private GlobalHotkeys? _hotkeys;
    private bool _paused;
    private bool _hideUiActive;
    private bool _topmostBeforeHideUi;

    /// <summary>Null = "All" (the Mob/Boss filter's first, always-present entry).</summary>
    private int? _selectedTargetId;

    /// <summary>
    /// Both null = the whole history of whichever target _selectedTargetId names (every kill of it
    /// combined), matching this app's original behavior. Both set = one specific numbered run
    /// picked from the dropdown (see ApplyMobBossSearchFilter's own remarks on why a boss farmed
    /// repeatedly - per the user, "e.g. Raksang Boilheart 5x in a row" - gets split into "#1".."#N"
    /// entries there instead of one entry silently averaging all of them together). Consulted by
    /// RefreshRows (to scope the live table) and the 3-arg BuildEncounterUpload overload (to scope
    /// an upload) - every OTHER place that sets _selectedTargetId directly (not via the dropdown)
    /// must also set these back to null, since they all want that target's full history, not
    /// whatever run happened to be selected in the UI before that code ran.
    /// </summary>
    private DateTime? _selectedRunWindowStart;
    private DateTime? _selectedRunWindowEnd;

    /// <summary>Null = "All" (ClassFilter's first entry, no Tag). Per the user: was purely
    /// decorative until now (see the XAML comment on ClassFilter's own history) -- expected to
    /// actually filter once other players' classes started being detected at all, and an empty
    /// result when nobody of that class is present is the correct, intended behavior, not a bug.</summary>
    private string? _selectedClassFilter;

    /// <summary>Per the user: an option to switch the whole grid to "PVP DMG" - damage against
    /// other PLAYERS only, mobs/bosses excluded. Overrides the Mob/Boss filter (a specific NPC
    /// target means nothing here) rather than combining with it - see RefreshRows/OnPvpOnlyClicked.</summary>
    private bool _pvpOnly;

    /// <summary>Master list backing the Mob/Boss dropdown -- kept separate from MobBossFilter's own
    /// Items, which now show a SEARCH-FILTERED subset (see ApplyMobBossSearchFilter/the
    /// SearchableComboBox template in the XAML). RefreshMobBossFilterItems's dedup and "upload
    /// every boss since Clear" (OnUploadLastRunClicked) both need the full, unfiltered set
    /// regardless of whatever the user currently has typed into the search box.</summary>
    private readonly List<(int TargetId, string Name)> _mobBossEntries = new();

    /// <summary>The XAML-declared "All" entry, captured once in the constructor so
    /// ApplyMobBossSearchFilter can keep re-inserting this SAME instance (preserving its
    /// {local:Loc Main.FilterAllTargets} binding) instead of fabricating a plain-text replacement every
    /// time the list rebuilds.</summary>
    // Replaced with a fresh item on every rebuild of the dropdown (see ApplyMobBossSearchFilter):
    // re-adding the same ComboBoxItem right after Items.Clear() can still have the old logical
    // parent and throws "Das Element besitzt bereits ein logisches übergeordnetes Element".
    private bool _mobBossFilterNeedsRebuild;
    private ComboBoxItem _mobBossAllItem;
    private readonly object? _mobBossAllContent;
    private readonly BindingBase? _mobBossAllContentBinding;
    private readonly object? _mobBossAllTag;

    /// <summary>Found once, in OnWindowLoaded, via the SearchableComboBox template's
    /// PART_SearchBox -- null until then, and also whenever the template hasn't produced one for
    /// some reason (defensive; every code path already tolerates a no-op filter in that case).</summary>
    private TextBox? _mobBossSearchBox;

    /// <summary>Mirror MeterSettings.ShowShareBars/ShowDamageTaken - cached here because
    /// RefreshRows runs every second and must not re-read the settings file each time.</summary>
    private bool _showShareBars = true;

    /// <summary>Mirrors MeterSettings.ShowDeaths: deaths (and how long dead) after the names in the damage and healing modes.</summary>
    private bool _showDeaths = true;
    private bool _compactOverlay;
    private bool _compactClickThrough;
    private bool _showBossHp;
    private TimetableWindow? _timetable;
    private HotkeyOverlayWindow? _hotkeyOverlay;
    private WorldBossController? _worldBoss;
    private AssistController? _assist;
    private LootOverlayController? _loot;
    private GatherOverlayController? _gatherOverlay;
    private SeasonOverlayController? _seasonOverlay;
    private MinimapReader? _minimapReader;
    private CraftOverlayController? _craft;
    private CombatOverlayController? _combat;
    private MenuWatcher? _menuWatcher;
    private Streaming.StreamOverlayServer? _streamServer;
    private readonly DispatcherTimer _streamTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // the Journey page's play time: a minute of play noted once a minute (History/JourneyLog.cs)
    private readonly DispatcherTimer _journeyTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private (float X, float Y)? _journeyPlace;
    private IReadOnlyList<DamageEvent> _lastShownHits = Array.Empty<DamageEvent>();
    private string _streamLast = "";
    private DateTime _streamSettingsAt = DateTime.MinValue;
    private MeterSettings _streamSettings = new();
    private NetworkOverlayController? _network;
    private DummyTestController? _dummy;
    private readonly System.Windows.Threading.DispatcherTimer _gateTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _gameGateOpen = true;
    private TimetableReminder? _timetableReminder;
    private PetFarmWindow? _petFarm;
    private PetFarmController? _petFarmController;
    private PetMapController? _petMapController;
    private bool _autoReset;
    /// <summary>Whose rows are shown: everybody, the own group, or the corps (two groups together).</summary>
    private enum MeterScope { All, Group, Corps, Me }

    /// <summary>Where the player is, for the automatic choice of whose rows are shown: in an instance or arena everybody (the raid), at a
    /// training dummy only the own character (the others hitting dummies around are no part of a parse), elsewhere the switch's choice.</summary>
    private enum ScopeContext { World, Instance, Dummy }

    private ScopeContext _scopeContext = ScopeContext.World;

    /// <summary>A switch clicked in an instance or at a dummy: it holds there until the player leaves (a new place picks automatically again).</summary>
    private MeterScope? _scopeManual;

    private bool _autoScope = true;
    private DateTime _lastDummyHit = DateTime.MinValue;
    private readonly Dictionary<int, bool> _dummyTargets = new();

    /// <summary>Whose rows are shown now: the switch's pick for this place, else the automatic one (instance: all, dummy: you), else the saved choice.</summary>
    private MeterScope ShownScope => _scopeManual ?? (!_autoScope ? _scope : _scopeContext switch
    {
        ScopeContext.Instance => MeterScope.All,
        ScopeContext.Dummy => MeterScope.Me,
        _ => _scope,
    });

    /// <summary>The All / Group / Corps filter belongs to the compact overlay (its header carries the switch, the client's toolbar a copy of it):
    /// while the overlay is on, the client lists the same players; the chip overlay always lists everybody whose damage arrives.</summary>
    private bool ScopeActive => (_hideUiActive && _compactOverlay) || MeterOverlayActive || (_autoScope && _scopeContext == ScopeContext.Dummy && _scopeManual is null);

    // ---- the meter overlay: the compact panel in a window of its own over the game, the main window being the client beside it ----
    private MeterOverlayWindow? _meterOverlay;
    private bool _meterOverlayOn = true;
    private bool _meterOverlayAutoHide = true;
    private TimeSpan _meterOverlayHideAfter = TimeSpan.FromSeconds(30);

    /// <summary>The last moment the own character hit something or was hit (local time): the overlay stays up a while after it.</summary>
    private DateTime _lastOwnCombat = DateTime.MinValue;

    /// <summary>The eye button showed the overlay outside a fight: it stays up until then, as if a fight had just been.</summary>
    private DateTime _overlayPeekUntil = DateTime.MinValue;

    /// <summary>A dummy parse waits, runs or just ended: the header shows its countdown instead of the fight's time.</summary>
    private bool _dummyHeader;

    /// <summary>The last moment anyone nearby fought a boss (a catalog boss: dungeon or field): the meter shows itself for that, not for every mob.</summary>
    private DateTime _lastBossFight = DateTime.MinValue;

    /// <summary>When the own character last hit another player or was hit by one: PvP, counted and shown in the open world too for a while after.</summary>
    private DateTime _lastPvpFight = DateTime.MinValue;

    /// <summary>The own character's last hit on a boss or by one (see <see cref="PopUpForBoss"/>); a minute without one, and the next is a new boss fight.</summary>
    private DateTime _lastOwnBossFight = DateTime.MinValue;
    private static readonly TimeSpan NewBossFightAfter = TimeSpan.FromMinutes(1);
    private bool _meterPopUpForBoss = true;
    private bool _overlayMenuOpen;
    private bool _meterOverlayLocked;

    /// <summary>The meter shows the effects on the own character instead of the players (its mode menu's "Buffs").</summary>
    private bool _overlayBuffs;

    private readonly ObservableCollection<OverlayBuffRow> _overlayBuffRows = new();
    private DateTime _overlayBuffsAt = DateTime.MinValue;
    private DateTime _overlayPowerAt = DateTime.MinValue;

    /// <summary>The compact panel lives in its own overlay window and Settings have it on.</summary>
    private bool MeterOverlayActive => _meterOverlay is not null && _meterOverlayOn;

    private MeterScope _scope = MeterScope.Group;

    // The players who fought the same monsters as the group, for the corps scope (see ComputeCorps).
    private HashSet<int> _corpsIds = new();

    // Everyone the party roster named since the data was last cleared. Leaving the group (after the
    // boss is dead, out of the instance) empties the roster, but the fight on screen is still that
    // group's: the group scope has to keep showing them.
    private readonly HashSet<string> _groupNames = new(StringComparer.Ordinal);
    private HpCheckResult? _lastHpCheck;

    /// <summary>Silence after which the next damage starts a new fight (MeterSettings.AutoResetEnabled and
    /// AutoResetSeconds).</summary>
    private TimeSpan _autoResetIdle = TimeSpan.FromSeconds(10);

    /// <summary>What the rows show: damage dealt, healing done, or damage taken (with deaths) -
    /// the Mode menu, the compact overlay's badge or the mode hotkey go round the three.</summary>
    private enum MeterMode { Damage, Raid, Heal, Taken, All, AllCompact }

    private MeterMode _mode;

    private bool _healMode => _mode == MeterMode.Heal;

    private bool _takenMode => _mode == MeterMode.Taken;

    /// <summary>All in One: damage, healing and damage taken side by side on the damage rows.</summary>
    private bool _allMode => _mode == MeterMode.All && !_pvpOnly;

    /// <summary>All in One Compact: the damage rows with healing and damage taken as a second line.</summary>
    private bool _allCompactMode => _mode == MeterMode.AllCompact && !_pvpOnly;

    /// <summary>Either All in One mode: the rows then carry healing and the damage taken from monsters.</summary>
    private bool _allData => _allMode || _allCompactMode;

    // The deaths of the rows shown in taken mode.
    private readonly Dictionary<int, List<Death>> _deathsById = new();

    // The span the taken rows cover, for a player's details in that mode.
    private (DateTime Start, DateTime End)? _takenSpan;
    private bool _showDamageTaken;
    /// <summary>Avoided attacks and kill announcements from the source, kept beside the
    /// aggregator's damage events (they are not DamageEvents - see Combat/Sources). Cleared with
    /// the damage data; scoped to the shown window at refresh time like everything else.</summary>
    private readonly List<AvoidEvent> _avoids = new();
    private readonly List<KillEvent> _kills = new();


    // True while a past session is loaded into the grid instead of the live capture; the banner offers the way back.
    private bool _historyMode;

    /// <summary>The skills used, effects and stagger of the stored fight on screen (history mode), under this session's ids.</summary>
    private FightExtras? _historyExtras;
    /// <summary>Viewed session: replay id -> id in the live capture, so the details window can look up rotation and buffs.</summary>
    private Dictionary<int, int> _historyLiveIds = new();
    /// <summary>Viewed session: id in the live capture -> replay id (the other way round).</summary>
    private Dictionary<int, int> _historyReplayIds = new();

    /// <summary>"Minimize to system tray" (OnMinimizeToTrayClicked) - null until the first time
    /// it's actually used, so a user who never touches this never costs a single Shell_NotifyIcon
    /// call.</summary>
    private TrayIcon? _trayIcon;
    private int _historyTickCounter;
    /// <summary>Set by the console test modes before they construct the window: nothing may open a dialog.</summary>
    internal static bool Headless { get; set; }

    // Where combat data comes from (see Combat/Sources/ICombatSource) - the Aion 2 packet source; its Entities directory is what RefreshRows/RefreshMobBossFilterItems resolve names
    // through for ids this window didn't assign itself. Null until Settings name an install folder.
    private ICombatSource? _source;

    /// <summary>The real capture. It lives as long as the window: while a stored fight or session is on show, <see cref="_source"/> is a replay
    /// and this keeps polling, so the session recording never has a gap.</summary>
    private ICombatSource? _liveSource;

    private bool ViewingLive => ReferenceEquals(_source, _liveSource);

    /// <summary>Every stay in a map or instance since the meter started, recorded whatever the grid shows (see SessionRecorder).</summary>
    private readonly SessionRecorder _sessions = new(zone => Aion2Zones.IsInstance(zone) ? SessionKind.Instance : SessionKind.Map);

    /// <summary>What the live grid follows: the stay the player is in (cleared at every map change), or everything together.</summary>
    private enum SessionView { Current, All }

    private SessionView _sessionView = SessionView.Current;

    /// <summary>The session on show in a replay (null for the live views and for a stored fight).</summary>
    private int? _viewedSessionNumber;

    private readonly UploadMenuFlags _uploadFlags = new();
    private CharacterWindow? _characterWindow;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Five minutes, per the user. GitHub's anonymous API allows 60 requests an hour per
    /// IP, so 12 is comfortably inside it even with a second client running alongside.</summary>
    // every two minutes (five until 1.19.34: with the four minutes a release takes to build, a new version could take nine to show up)
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(2) };

    /// <summary>Asks aiondps.com every half hour for the newest announcement (a single number) to decide whether the "New" badge shows.</summary>
    private readonly DispatcherTimer _announcementsTimer = new() { Interval = TimeSpan.FromMinutes(30) };

    /// <summary>The posting time of the newest announcement the server named last; the click on the icon stores it as "seen".</summary>
    private long? _latestAnnouncement;

    /// <summary>The update already downloaded and staged, so a repeating check does not fetch the
    /// same one twelve times an hour -- and so clicking the notice knows what to restart into.
    /// Aliased because Velopack's UpdateInfo would otherwise collide with nothing in particular,
    /// but reads ambiguously next to this project's own update code.</summary>
    private VelopackUpdateInfo? _downloadedUpdate;

    /// <summary>Writes the stores' unsaved changes every 15 seconds, off the window thread (see <see cref="FlushStores"/>).</summary>
    private System.Threading.Timer? _storeFlush;

    /// <summary>The update the in-app restart card (UpdateRestartOverlay) is currently offering,
    /// so its two buttons know what to act on without re-threading it through the event args.</summary>
    private VelopackUpdateInfo? _pendingRestartUpdate;
    /// <summary>The update the start-up question is about (found, not downloaded yet): "Yes" fetches it and restarts, "No" leaves it to the background check.</summary>
    private VelopackUpdateInfo? _pendingInstallUpdate;

    /// <summary>Tells the Windows shell that something changed; SHCNE_ASSOCCHANGED (0x08000000) makes it read the program icons again.</summary>
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    // the update the start-up check offered (Yes / No): the title bar's update button offers it again after a "No"
    private VelopackUpdateInfo? _availableUpdate;

    public MainWindow()
    {
        InitializeComponent();
        ShareMenuStyles();
        AppMenu.Tag = _uploadFlags;
        _sessions.Identify = IdentifyForSession;
        _sessions.HpSource = HitPointsOfTarget;

        // Captured now, before anything ever rebuilds MobBossFilter.Items, so
        // ApplyMobBossSearchFilter can keep re-inserting this exact instance rather than a plain
        // "All" it would have to build itself -- this one keeps the XAML {local:Loc Main.FilterAllTargets}
        // binding, which is only set up once, at parse time, on this specific object.
        _mobBossAllItem = (ComboBoxItem)MobBossFilter.Items[0]!;
        _mobBossAllContent = _mobBossAllItem.Content;
        // The XAML item's text is a localisation binding; a fresh item gets the same binding.
        _mobBossAllContentBinding = BindingOperations.GetBindingBase(_mobBossAllItem, ContentControl.ContentProperty);
        _mobBossAllTag = _mobBossAllItem.Tag;
        Loaded += OnWindowLoaded;
        SizeChanged += (_, _) => UpdateOverlayWidth();

        // Version in the title, read back from the assembly rather than typed here a second time:
        // AionDPS.csproj's <Version> is the only place it is written. Needed because builds are
        // handed around the group by hand -- a screenshot from someone else is
        // otherwise impossible to pin to a build, which already cost a round of guesswork once.
        Title = AppVersion.Text.Length > 0 ? $"{Brand.AppName} {AppVersion.Text}" : Brand.AppName;

        PlayersGrid.ItemsSource = _rows;
        OverlayContent.ItemsSource = _rows;
        CompactOverlayRows.ItemsSource = _rows;
        OverlayBuffRows.ItemsSource = _overlayBuffRows;

        // Per the user: the list must sort itself by damage, highest first, not just show rows in
        // whatever order they were first discovered in. IsLiveSorting (not just SortDescriptions
        // alone) is what keeps it re-sorted as damage keeps changing live -- plain
        // SortDescriptions only sorts once, at binding time; without live sorting the row order
        // would freeze after the initial snapshot and never reflect who's actually ahead now.
        var playersView = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
        _playersView = playersView;
        // SortKey is the damage, or rDPS in the rDPS view (PlayerRow.SortKey).
        playersView.SortDescriptions.Add(new SortDescription(nameof(PlayerRow.SortKey), ListSortDirection.Descending));
        playersView.IsLiveSorting = true;
        playersView.LiveSortingProperties.Add(nameof(PlayerRow.SortKey));
        // the meter over the game sorts on its own: its best line may stand at the bottom (Meter look)
        _overlayView = new ListCollectionView(_rows);
        _overlayView.SortDescriptions.Add(new SortDescription(nameof(PlayerRow.SortKey), ListSortDirection.Descending));
        _overlayView.IsLiveSorting = true;
        _overlayView.LiveSortingProperties.Add(nameof(PlayerRow.SortKey));
        _overlayView.Filter = item => _overlayLines is null || (item is PlayerRow line && _overlayLines.Contains(line));
        CompactOverlayRows.ItemsSource = _overlayView;
        MeterRowView.RowClicked += ShowPlayerDetails;

        _pollTimer.Tick += OnPollTimerTick;
        AppPerf.StartWatch(); // the window thread's stalls, for the Status page

        // Startup check is announced (per the user: should behave exactly like clicking "Check for
        // updates" in the App menu, not stay silent) -- a launch is never mid-fight, so a message
        // box here costs nothing. The recurring two-minute timer stays silent (see RunUpdateCheck):
        // that one CAN land mid-boss, and a background timer popping a dialog over a fight is
        // exactly what announceResult=false was added to prevent. Fire-and-forget on purpose
        // either way -- an update check must never delay the window appearing.
        // Not in the headless test modes (aion2-ui-test, aion2-ui-live, aion2-upload-dryrun): they build
        // this window without showing it, and the announced check would put a message box on the
        // user's screen - from a build folder, where there is never an Update.exe.
        if (!Headless)
        {
            _updateTimer.Tick += (_, _) => _ = RunUpdateCheck(announceResult: false);
            _updateTimer.Start();
            _announcementsTimer.Tick += (_, _) => { _ = RefreshAnnouncementsBadge(); _ = RefreshTwitchDropsBadge(); _ = RefreshSchedule(); };
            _announcementsTimer.Start();
            _ = RefreshAnnouncementsBadge();
            _ = RefreshTwitchDropsBadge();
            _ = RefreshSchedule();
            // The meter talks to GitHub only while "check for updates" is on in the settings.
            var startupSettings = MeterSettings.Load();
            // the version that ran before this start (kept since 1.19: the icon refresh below writes it once per version): the notes after an update
            // list every version since that one, also for an update from before the notes remembered their own (MainWindow.UpdateNotes.cs)
            _versionBefore = startupSettings.IconRefreshedFor;
            if (!Headless && startupSettings.IconRefreshedFor != AionDPS.Update.AppVersion.Text)
            {
                // the first start of a version: Windows is told the program icons changed, so the Start menu, desktop and taskbar shortcuts show the
                // icon of this version and not one it kept from an earlier install (the old icon stayed on them after the update to the new emblem)
                SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
                startupSettings.IconRefreshedFor = AionDPS.Update.AppVersion.Text;
                startupSettings.Save();
            }

            if (!startupSettings.MenuHideDefaulted)
            {
                // the overlays step aside in the game's windows (enchant, inventory ...) for everyone once; whoever turns it off keeps it off
                startupSettings.HideDuringMenus = true;
                startupSettings.MenuHideDefaulted = true;
                startupSettings.Save();
            }

            if (!startupSettings.MeterLookDefaulted)
            {
                // the meter's panel see-through and its buttons always there (5 November 2026, the owner: the default "around the list"); the rest of
                // the look the player chose stays
                startupSettings.OverlayLook = "Clear";
                startupSettings.MeterHeaderButtons = "always";
                startupSettings.MeterLookDefaulted = true;
                startupSettings.Save();
            }

            if (!startupSettings.MeterBarDefaulted)
            {
                // the meter folds to its bar between boss fights now (the own character, the cube, the counters), so it no longer hides all of itself
                // in between; whoever wants it gone between fights switches "Only during boss fights" on again
                startupSettings.MeterOverlayAutoHide = false;
                startupSettings.MeterFoldIdle = true;
                startupSettings.MeterBarDefaulted = true;
                startupSettings.Save();
            }

            if (!startupSettings.NaturalVoiceDefaulted && NaturalVoice.Voices().Count > 0)
            {
                // the skills' and the bosses' announcements in English take the recorded voice once (the Windows voices sound like machines); other
                // languages keep theirs, and whoever picks a Windows voice again keeps it
                if (CastSpeech.WordsLanguage(startupSettings) == "en")
                {
                    startupSettings.SpeechVoiceId = NaturalVoice.Heart;
                    startupSettings.SpeechLanguage = NaturalVoice.Language;
                }

                startupSettings.NaturalVoiceDefaulted = true;
                startupSettings.Save();
            }

            if (startupSettings.CheckForUpdates)
            {
                // Queued on the dispatcher, not started right here: this runs in the constructor, before app.Run, where no UI synchronization context
                // exists yet, so the part of the check after the network call ran on a pool thread and failed on its first touch of the window
                // (a crash.log entry at every start and no check until the five-minute timer).
                // Silent when up to date or when GitHub cannot be reached; a new version is asked about (OfferInstall) before anything is downloaded.
                Dispatcher.BeginInvoke(new Action(() => _ = RunUpdateCheck(announceResult: false, askBeforeInstall: true)), System.Windows.Threading.DispatcherPriority.Background);
            }

            // once after an update: what the new version brought (MainWindow.UpdateNotes.cs)
            Dispatcher.BeginInvoke(new Action(ShowNotesAfterUpdate), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // Only the current version may upload (the server refuses an old one). A meter that uploads by itself asks the community server at
            // once, so an old client learns it now and not with its first fight; one that only uploads on a click learns it from that upload.
            UploadClient.ClientOutdated += required => Dispatcher.BeginInvoke(new Action(() => OnClientOutdated(required)));
            UploadClient.ClientAllowed += () => Dispatcher.BeginInvoke(new Action(OnClientAllowed));
            if (startupSettings.AutoUploadProfile || startupSettings.AutoUploadBoss)
            {
                _ = UploadClient.CheckPolicyAsync(AppVersion.Text);
            }
        }

        if (!Headless)
        {
            // the answer to the privacy policy changes what is shown (restricted mode) - also from the settings window
            PrivacyConsent.Changed += () => Dispatcher.BeginInvoke(ApplyPrivacyRestrictions);
        }

        var settings = MeterSettings.Load();

        // Until the wizard has been through (see below) nothing is uploaded: an install that updated from a
        // version with the uploads switched on starts from "all off" too, so no upload runs before its
        // owner has made the choice.
        if (!Headless && !settings.SetupCompleted)
        {
            settings.AutoUploadProfile = false;
            settings.AutoUploadBoss = false;
            settings.UploadOtherPlayersProfiles = false;
            settings.ShareCpMeasurements = false;
            settings.Save();
        }

        // Empty means "a fresh install, or a settings file older than this feature" -- leave
        // LocalizationManager on whatever it already auto-detected from the OS at construction
        // time (see its DetectSystemLanguage remarks) rather than forcing English.
        if (settings.Language.Length > 0)
        {
            LocalizationManager.Instance.Language = settings.Language;
        }

        // Same value as the View menu's "Always on top" toggle (see OnAlwaysOnTopClicked) --
        // applied here so a saved preference survives a restart, not just a runtime toggle.
        Topmost = settings.AlwaysOnTopOnStartup;
        AlwaysOnTopMenuItem.IsChecked = settings.AlwaysOnTopOnStartup;

        // The mode last used (damage, heal, taken, all, all compact), see SetMode.
        if (Enum.TryParse(settings.LastMode, out MeterMode savedMode) && savedMode != MeterMode.Damage)
        {
            SetMode(savedMode, save: false);
        }
        else
        {
            ApplyModeButtons();
        }

        RestoreWindowGeometry(settings);
        StartCapture(settings);
        DeleteLegacyFightHistory();
        RefreshCharacterSettings(settings);
        TryRestoreRestartSnapshot();

        // A fresh install, or a lost or broken settings file: the wizard asks for the language and the
        // upload options that are off by default (see WizardWindow). Never in the headless test modes.
        if (!Headless)
        {
            bool wizard = !settings.SetupCompleted;
            Loaded += (_, _) =>
            {
                if (wizard)
                {
                    new WizardWindow(settings, RecognisedCharacterName, () => _liveSource?.Entities as Aion2.Aion2EntityDirectory) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
                }
                else if (PrivacyConsent.NeedsAsking(MeterSettings.Load()))
                {
                    // an install from before the question existed, or a policy that changed since the player accepted it
                    PrivacyConsent.Ask(this);
                }

                // after the terms, before the app: the meter's design and columns, the overlays and the colours, each with how it looks in the game (once)
                ShowLookSetupOnce();
                ApplyPrivacyRestrictions();
                RestorePopOuts();
                WindowPlacement.AskIfNeeded(this);
                AskForNpcapIfMissing();
            };
        }
    }

    // ---- the privacy answer: declined = restricted mode ------------------------------------------------
    // Per the user: when the privacy policy is declined nothing is uploaded and the meter keeps only the DPS list (damage mode only, no other mode buttons), the checklist, stat calculator,
    // attributes, loot, crafting calculator, schedule, design and network tools; Twitch drops, the announcements, the combat/assist/dummy/pet/world boss features are gone.

    /// <summary>The features the restricted mode takes away are only started while it is not on; the others are always there.</summary>
    private void EnsureRestrictableControllers()
    {
        if (PrivacyConsent.Restricted)
        {
            return;
        }

        _worldBoss ??= new WorldBossController();
        _assist ??= new AssistController(() => _liveSource?.Entities as Aion2.Aion2EntityDirectory);
        if (_dummy is null)
        {
            _dummy = new DummyTestController(() => _liveSource?.Entities as Aion2.Aion2EntityDirectory);
            _dummy.Model.Completed += LogDummyRun;
        }
        _combat ??= new CombatOverlayController(() => _source?.Entities as Aion2.Aion2EntityDirectory, BossHpCheck);
    }

    /// <summary>Shows or hides what the restricted mode removes, and starts or stops the features that go with it. Called at the start, after the first-start wizard and whenever the answer changes.</summary>
    private void ApplyPrivacyRestrictions()
    {
        bool restricted = PrivacyConsent.Restricted;
        foreach (Button button in new[] { ModeHealButton, ModeTakenButton, ModeAllButton, ModeAllPlusButton })
        {
            button.Visibility = restricted ? Visibility.Collapsed : Visibility.Visible;
        }

        if (restricted)
        {
            AnnouncementsButton.Visibility = Visibility.Collapsed;
            TwitchDropsButton.Visibility = Visibility.Collapsed;
            if (_mode != MeterMode.Damage)
            {
                SetMode(MeterMode.Damage, save: false);
            }
        }
        else
        {
            AnnouncementsButton.Visibility = Visibility.Visible;
            _ = RefreshTwitchDropsBadge();
        }

        // the Tools menu: only the tools that need no server and no other player's data stay
        foreach (object item in ToolsMenu.Items)
        {
            switch (item)
            {
                case MenuItem { Tag: string id } menu:
                    menu.Visibility = restricted && !PrivacyConsent.RestrictedTools.Contains(id) ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case Separator line:
                    line.Visibility = restricted ? Visibility.Collapsed : Visibility.Visible;
                    break;
            }
        }

        if (restricted)
        {
            _toolsWindow?.Close();
            _worldBoss?.Dispose();
            _worldBoss = null;
            _assist?.Dispose();
            _assist = null;
            _dummy?.Dispose();
            _dummy = null;
            _combat?.Dispose();
            _combat = null;
            _petMapController?.Dispose();
            _petMapController = null;
            _petFarmController?.Dispose();
            _petFarmController = null;
            _petFarm?.Hide();
        }
        else if (!Headless)
        {
            EnsureRestrictableControllers();
            ApplyPetFarm(MeterSettings.Load());
        }

        RefreshUploadAvailability();
    }

    /// <summary>The name of the local character once the capture has recognised it (the first-start wizard shows it live), else null.</summary>
    private string? RecognisedCharacterName() =>
        _liveSource?.Entities is Aion2.Aion2EntityDirectory directory && directory.LocalPlayerId >= 0 ? directory.NameFor(directory.LocalPlayerId) : null;

    /// <summary>Without the Npcap driver the meter sees no game traffic at all (no damage, no pet info): says so at the start and offers the download page.</summary>
    private void AskForNpcapIfMissing()
    {
        if (Aion2.Capture.NpcapAvailability.Detect().IsInstalled)
        {
            return;
        }

        var loc = LocalizationManager.Instance;
        if (ThemedMessageBox.Show(this, loc["Npcap.Missing.Text"], loc["Npcap.Missing.Title"], MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Aion2.Capture.NpcapAvailability.DownloadUrl) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }
    }

    /// <summary>Applies a previously saved size/position, if any -- see SaveWindowGeometry, its
    /// counterpart on close. Left null-checked separately from Width/Height since a user who's
    /// only ever resized (not moved) the window would have one pair set and the other still
    /// null. No longer also restores Name/Damage-DPS column widths -- those two independently-
    /// resizable columns were merged into one Width="*" PlayerColumn (see its own remarks in
    /// MainWindow.xaml) that always fills whatever this window's own persisted width leaves it.</summary>
    private void RestoreWindowGeometry(MeterSettings settings)
    {
        if (settings.WindowWidth is double width && settings.WindowHeight is double height)
        {
            Width = width;
            Height = height;
        }

        if (settings.WindowLeft is double left && settings.WindowTop is double top)
        {
            // on a monitor that is not there any more the window stays where Windows puts it (the first monitor)
            WindowPlacement.Resolve(new Rect(left, top, Width, Height), out bool moved);
            if (!moved)
            {
                Left = left;
                Top = top;
            }
        }
    }

    /// <summary>Persists the current size/position so it survives a restart -- found necessary by
    /// the user, who resized the window and had it reset every time. Reads WindowState-independent
    /// values (RestoreBounds instead of Width/Height/Left/Top directly) so a maximized or minimized
    /// window on close doesn't save that transient state as if it were the normal size. See
    /// RestoreWindowGeometry's own remarks for why column widths are no longer part of this.</summary>
    private void SaveWindowGeometry(MeterSettings settings)
    {
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        // Closed while the compact overlay is up: the window has the panel's size, not its own.
        settings.WindowWidth = _sizeBeforeCompactOverlay?.Width ?? bounds.Width;
        settings.WindowHeight = _sizeBeforeCompactOverlay?.Height ?? bounds.Height;
        if (WindowPlacement.CanSave)
        {
            settings.WindowLeft = bounds.X;
            settings.WindowTop = bounds.Y;
        }
    }

    /// <summary>Re-reads the display settings - called once at startup and again after Settings is saved.</summary>
    private void RefreshCharacterSettings(MeterSettings settings)
    {
        ApplyMeterLook(settings);
        _showShareBars = settings.ShowShareBars;
        _showDeaths = settings.ShowDeaths;
        _showPartyBuffs = settings.ShowPartyBuffs;
        _showDamageTaken = settings.ShowDamageTaken;
        _showBossHp = settings.ShowBossHp;
        _autoBossView = settings.AutoBossView;
        _autoBossAfterDeath = TimeSpan.FromSeconds(Math.Clamp(settings.AutoBossSeconds, 0, 600));
        _autoReset = settings.AutoResetEnabled;
        _autoResetIdle = TimeSpan.FromSeconds(Math.Clamp(settings.AutoResetSeconds, 1, 600));
        _scope = Enum.TryParse(settings.ViewScope, true, out MeterScope scope) ? scope : MeterScope.Group;
        _autoScope = settings.AutoScope;
        UpdateScopeControls();
        _compactOverlay = string.Equals(settings.OverlayStyle, "Compact", StringComparison.OrdinalIgnoreCase);
        _compactClickThrough = settings.CompactClickThrough;
        _meterOverlayOn = settings.MeterOverlayOn;
        _meterOverlayAutoHide = settings.MeterOverlayAutoHide;
        _meterPopUpForBoss = settings.MeterPopUpForBoss;
        _trackOpenWorld = settings.TrackOpenWorld;
        UpdateOpenWorldButton();
        _meterOverlayHideAfter = TimeSpan.FromSeconds(Math.Clamp(settings.MeterOverlayHideSeconds, 10, 600));
        _meterOverlay?.SetClickThrough(_compactClickThrough);
        UpdateScopeControls();
        SetCompactOverlayScale(settings.OverlayScale);
        // the player's width; a meter set before 6 November 2026 kept what was added to its look's width: the same width again
        SetCompactOverlaySize(settings.OverlayWidth > 0 ? settings.OverlayWidth
            : settings.OverlayExtraWidth != 0 ? BaseOverlayWidth(_meterLook) + settings.OverlayExtraWidth : 0, settings.OverlayRowsHeight);
        // the panel's colours (themed, see-through or graphite, at the opacity chosen) are part of the look: ApplyMeterLook, at the top, set them
        _meterOverlayLocked = settings.MeterOverlayLocked;
        ApplyOverlayLock();
        if (_hideUiActive)
        {
            ApplyOverlayLook();
        }
    }

    /// <summary>
    /// (Re)starts the Aion 2 packet capture with the given settings (adapter, own character name) -
    /// called once at startup and again after Settings is saved.
    /// </summary>
    private string? _captureAdapterInUse;

    private void StartCapture(MeterSettings settings)
    {
        _captureAdapterInUse = settings.CaptureAdapterId;
        Aion2.Capture.DebugRecording.Enabled = settings.DebugRecording;
        Aion2.Capture.LoginCapture.Enabled = settings.ShareLoginWithLegion;
        _pollTimer.Stop();
        if (_historyMode)
        {
            // A new capture ends whatever replay is on show.
            _historyMode = false;
            _viewedSessionNumber = null;
            HistoryBanner.Visibility = Visibility.Collapsed;
            ClearDamageData();
        }

        if (!ReferenceEquals(_source, _liveSource))
        {
            _source?.Dispose();
        }

        _liveSource?.Dispose();
        _source = null;
        _liveSource = null;

        var source = new Aion2PacketCombatSource(Aion2Protocol.Load(), settings.CaptureAdapterId, null, Aion2CharacterStore.DefaultPath);
        ReplaceSource(source);
        _liveSource = source;
        StartGameTitleWatch();
        // The upload entries appear once the own character is known (see RefreshUploadAvailability).
        if (source.Entities is Aion2EntityDirectory entities)
        {
            // The characters of the login's list stay known on this PC, also those of other servers; they also name the local player
            // mid-session (by class) before the game does, so nobody has to log in again (Aion2EntityDirectory.GuessedLocalName).
            entities.SetKnownCharacters(Aion2RememberedCharacters.Load().Select(r => (r.Name, r.ClassCode, r.ServerId)));
            entities.AccountCharactersNoted += characters => Aion2RememberedCharacters.Note(characters);
            // A map change announces the own character again under another combat id: one player, one row.
            entities.LocalIdChanged += (oldId, newId) => Dispatcher.BeginInvoke(new Action(() =>
            {
                _sessions.MergeIds(oldId, newId);
                int merged = ViewingLive ? _aggregator.MergeIds(oldId, newId) : 0;
                if (ViewingLive && _selectedTargetId == oldId)
                {
                    _selectedTargetId = newId;
                }

                AutoUploadLog.Write($"own combat id changed {oldId} -> {newId}: {merged} event(s) merged into one row");
            }));
            // Any other player: the same name on the same server under a new combat id is the same player.
            entities.IdentityMoved += (oldId, newId) => Dispatcher.BeginInvoke(new Action(() =>
            {
                _sessions.MergeIds(oldId, newId);
                if (ViewingLive)
                {
                    _aggregator.MergeIds(oldId, newId);
                    if (_selectedTargetId == oldId)
                    {
                        _selectedTargetId = newId;
                    }
                }
            }));
            entities.CharacterChanged += _ => Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshUploadAvailability();
                ScheduleOwnProfileUpload();
            }));
            // A character window of another player was opened: it goes online by itself a few seconds later (see ScheduleInspectedUpload).
            entities.InspectedChanged += () => Dispatcher.BeginInvoke(new Action(ScheduleInspectedUpload));
        }

        _pollTimer.Start();
    }

    /// <summary>Swaps the combat source. Handlers are subscribed once per source - the source
    /// itself forwards from whatever capture it currently holds, so swapping
    /// the capture underneath never needs this window to re-subscribe.</summary>
    private void ReplaceSource(ICombatSource? source)
    {
        _source?.Dispose();
        _source = source;
        if (source is null)
        {
            return;
        }

        source.StatusChanged += OnSourceStatusChanged;
        source.Start();
    }

    /// <summary>Only states the user can act on reach the status line; "connected" is the normal
    /// case and needs no announcement. Marshalled because a capture-based source reports from its
    /// own thread.</summary>
    /// <summary>The capture's last word on its state (the Status page shows it).</summary>
    internal static SourceStatus? LastSourceStatus { get; private set; }

    private void OnSourceStatusChanged(SourceStatus status)
    {
        LastSourceStatus = status;
        // "Connected" is normally silent; the Aion 2 source's live counters (frames/events) are the
        // one exception, since they are how a stalled capture is told apart from a stalled decoder.
        bool aion2Counters = status.State == SourceState.Connected && _source is Aion2PacketCombatSource;
        if (status.State is not (SourceState.Waiting or SourceState.Error) && !aion2Counters)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => ShowUploadStatus(status.Message)));
    }

    /// <summary>The gear entry of a profile upload, with the mana stones and rolled stats when they were read.</summary>
    internal static ProfileGearUpload GearUpload(Aion2.Aion2EquippedItem i) => new(i.SlotIndex, i.ItemId, i.Enchant,
        i.Stones?.Select(k => new ProfileStoneUpload(k.StatId, k.Tier)).ToList(),
        i.Stats?.Select(k => new ProfileStatUpload(k.StatId, k.Value)).ToList(),
        i.GodstoneId > 0 ? i.GodstoneId : null,
        i.SkillBonuses?.Select(k => new ProfileSkillBonusUpload(k.SkillId, k.Level)).ToList(),
        i.Arcana is { } a ? new ProfileArcanaUpload(a.EnchantExp, a.GodStatId, a.GodValue, a.SkillBonuses.Where(k => k.Level > 0).Select(k => new ProfileSkillBonusUpload(k.SkillId, k.Level)).ToList()) : null);

    /// <summary>
    /// What a global hotkey (see GlobalHotkeys) does. They replace the in-game chat commands, which
    /// cannot work: Aion 2's chat is not readable from the network stream.
    /// </summary>
    private void OnHotkeyPressed(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleHideUi:
                ToggleOverlay();
                break;
            case HotkeyAction.TogglePause:
                SetPaused(!_paused);
                break;
            case HotkeyAction.CopyDamageRanking:
                CopyChatLineChunk(BuildDmgRankingText(), "No damage has been recorded yet.");
                break;
            case HotkeyAction.ClearDamage:
                ClearDamageData();
                break;
            case HotkeyAction.ToggleTimetable:
                ToggleTimetable();
                SayToggled("Timers.Title", MeterSettings.Load().ShowTimetable);
                break;
            case HotkeyAction.TogglePetMap:
                TogglePetMap();
                SayToggled("Settings.Nav.PetMap", MeterSettings.Load().ShowPetMap);
                break;
            case HotkeyAction.ToggleHotkeyOverlay:
                ToggleHotkeyOverlay();
                SayToggled("Shell.OvHotkeys", MeterSettings.Load().ShowHotkeyOverlay);
                break;
            case HotkeyAction.ToggleAllOverlays:
                ToggleAllOverlays();
                break;
            case HotkeyAction.MoveOverlays:
                ToggleOverlaysMovable();
                break;
            case HotkeyAction.ToggleNetworkOverlay:
                ToggleNetworkOverlay();
                SayToggled("Hotkeys.Name.Network", MeterSettings.Load().ShowNetworkOverlay);
                break;
            case HotkeyAction.ToggleCombatOverlay:
                ToggleCombatOverlay();
                SayToggled("Settings.Nav.Combat", MeterSettings.Load().ShowCombatOverlay);
                break;
            case HotkeyAction.ToggleAssistOverlay:
                ToggleAssistOverlay();
                SayToggled("Main.MenuTools.Assist", MeterSettings.Load().ShowAssistOverlay);
                break;
            case HotkeyAction.ToggleLootOverlay:
                ToggleLootOverlay();
                SayToggled("Main.MenuTools.Loot", MeterSettings.Load().ShowLootOverlay);
                break;
            case HotkeyAction.ToggleCraftOverlay:
                ToggleCraftOverlay();
                SayToggled("Main.MenuTools.Craft", MeterSettings.Load().ShowCraftOverlay);
                break;
            case HotkeyAction.ToggleWorldBossOverlay:
                ToggleWorldBossOverlay();
                SayToggled("Settings.Nav.Boss", MeterSettings.Load().ShowBossOverlay);
                break;
            case HotkeyAction.TogglePetArrows:
                TogglePetArrows();
                SayToggled("Hotkeys.Name.PetArrows", MeterSettings.Load().ShowPetArrows);
                break;
            case HotkeyAction.PetMapZoomIn:
                _petMapController?.Zoom(-PetMapWindow.ZoomStep);
                break;
            case HotkeyAction.PetMapZoomOut:
                _petMapController?.Zoom(PetMapWindow.ZoomStep);
                break;
            case HotkeyAction.ToggleGatherArrows:
                ToggleGatherArrows();
                SayToggled("Hotkeys.Name.GatherArrows", MeterSettings.Load().ShowGatherArrows);
                break;
            case HotkeyAction.NextMode:
                NextMode();
                break;
            case HotkeyAction.UploadBoss:
                OnUploadCurrentBossClicked(this, new RoutedEventArgs());
                break;
        }
    }

    /// <summary>The hotkeys that do nothing since the last registration, and why: the hotkey overlay, Settings > Hotkeys and the overlay menu show them.</summary>
    internal static IReadOnlyDictionary<HotkeyAction, HotkeyProblem> HotkeyProblems { get; private set; } = new Dictionary<HotkeyAction, HotkeyProblem>();

    /// <summary>The problems last reported, so the same note does not come again each time the settings are applied.</summary>
    private string _hotkeyProblemsSaid = "";

    /// <summary>(Re-)registers the hotkeys from the settings. One that gets nothing - another program owns the combination, or another of the meter's
    /// hotkeys has it too - is marked red in the hotkey overlay and in Settings > Hotkeys, and said once on the status line and over the game.</summary>
    private void ApplyHotkeys(MeterSettings settings)
    {
        if (_hotkeys is null)
        {
            return;
        }

        HotkeyProblems = _hotkeys.Apply(HotkeyTable.Bindings(settings));
        _settingsWindow?.ShowHotkeyProblems(HotkeyProblems);
        var loc = LocalizationManager.Instance;
        string said = string.Join(",", HotkeyProblems.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value + ":" + HotkeyTable.Of(p.Key).Get(settings)));
        if (said == _hotkeyProblemsSaid)
        {
            return;
        }

        _hotkeyProblemsSaid = said;
        if (HotkeyProblems.Count == 0)
        {
            return;
        }

        string list = string.Join(", ", HotkeyProblems.Select(p => $"{loc[HotkeyTable.Of(p.Key).NameKey]} ({HotkeyBinding.Parse(HotkeyTable.Of(p.Key).Get(settings))})"));
        string text = string.Format(loc[HotkeyProblems.Values.All(v => v == HotkeyProblem.Twice) ? "Hotkeys.Problem.StatusTwice" : "Hotkeys.Problem.Status"], list);
        ShowUploadStatus(text);
        CubeToast.Note(text, 6);
    }

    /// <summary>A hotkey switched something on or off: a short note over the game says which, so a press is never met by nothing (an overlay that has
    /// nothing to show yet - no loot, no tracked recipe - looked as if the key did not work).</summary>
    private static void SayToggled(string nameKey, bool on)
    {
        var loc = LocalizationManager.Instance;
        CubeToast.Note(string.Format(System.Globalization.CultureInfo.CurrentCulture, loc[on ? "Hotkeys.Toggled.On" : "Hotkeys.Toggled.Off"], loc[nameKey]), 1.6);
    }

    private DateTime _lastPreviewInvalidate = DateTime.MinValue;

    private int _cubeMapArrivals = -1;

    /// <summary>
    /// A Hidden Cube comes into range while the pet map module is switched off: the map (and the arrival chime) come all the same, as the cube's switch says ("show the map when a cube appears";
    /// Stacy's idea, her fork 17 October 2026). With the module on its own controller notices the cube.
    /// </summary>
    private void PollCubeMap()
    {
        if (Headless || _source?.Entities is not Aion2EntityDirectory directory)
        {
            return;
        }

        int before = _cubeMapArrivals;
        _cubeMapArrivals = directory.CubeArrivals;
        if (before < 0 || _cubeMapArrivals <= before || _petMapController is not null || PrivacyConsent.Restricted)
        {
            return;
        }

        MeterSettings settings = MeterSettings.Load();
        if (settings.ShowPetMap || !settings.ShowMapOnCube)
        {
            return;
        }

        _petMapController = new PetMapController(() => _source?.Entities as Aion2EntityDirectory);
        _petMapController.ApplyLocked(settings.PetFarmLocked);
        _petMapController.OnCubeArrived(settings);
    }

    /// <summary>The meter's tick, timed for the Status page (AppPerf).</summary>
    private void OnPollTimerTick(object? sender, EventArgs e)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            PollTick();
        }
        finally
        {
            AppPerf.NoteTick(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    private void PollTick()
    {
        PollCubeMap();
        // The own server shows up when the character is seen: the timetable is asked again for its region (once per region and session; the timer retries).
        if (Aion2OwnServerId() is int ownServer && Aion2Servers.RegionCodeOf(ownServer) is { } ownRegion && ownRegion != _scheduleRegion)
        {
            _scheduleRegion = ownRegion;
            _ = RefreshSchedule();
        }

        // The Alt+Tab picture of the overlay (see RenderOverlayPreview) is redrawn on request; saying
        // it is stale every couple of seconds keeps it close to what the overlay shows.
        if (_hideUiActive && DateTime.UtcNow - _lastPreviewInvalidate > TimeSpan.FromSeconds(2))
        {
            _lastPreviewInvalidate = DateTime.UtcNow;
            _overlay?.InvalidatePreview();
        }

        // The live capture is polled whatever the grid shows, so the session recording goes on while a stored session is on view.
        CombatBatch captured = _liveSource?.Poll(_paused) ?? CombatBatch.Empty;
        bool ownFight = NoteOwnCombat(captured.Damage); // every hit tells of bosses, dummies and the own fighting, counted or not
        if (_liveSource?.Entities is Aion2.Aion2EntityDirectory lootDirectory)
        {
            // the loot tool's drop rates: whom the player hit and who died, counted or not (the open world too)
            Aion2.Aion2Loot.NoteCombat(captured.Damage, captured.Kills, lootDirectory.IsLocalPlayer, lootDirectory.NpcIdOf);
        }

        CombatBatch liveBatch = Counted(captured);
        FeedSessions(liveBatch);
        NoteArenaLeft(liveBatch);
        UpdateScopeContext();
        // A new stay began (map or instance change) while an older session or the all-sessions view is on show: the view follows
        // into the new stay, which the recorder has just opened. The grid is filled from the recorder, so this batch's hits are in it already.
        // The same when the player fights while the client shows a stored fight and is not in front: the overlay is to show this fight.
        bool followed = (liveBatch.ZoneChanges.Count > 0 || (ownFight && MeterOverlayActive && !IsActive)) && (_historyMode || _sessionView == SessionView.All);
        if (followed)
        {
            _sessionView = SessionView.Current;
            if (_historyMode)
            {
                ExitHistoryMode();
            }
            else
            {
                ShowLiveView(SessionView.Current);
            }
        }

        bool viewingLive = ViewingLive;
        CombatBatch batch = followed
            ? new CombatBatch(Array.Empty<DamageEvent>(), liveBatch.Avoids, liveBatch.Kills)
            : viewingLive ? liveBatch : _source?.Poll(_paused) ?? CombatBatch.Empty;
        if (_menuWatcher is not null && _liveSource is { } liveSource && captured.Damage.Any(ev => !ev.IsHeal && !ev.IsTick && !ev.ViaSummon && liveSource.Entities.IsLocalPlayer(ev.SourceObjectId)))
        {
            // the control mode and the game's HUD are learnt while the player fights; a damage-over-time tick or a summon's hit goes on while a game
            // window is open, so it does not count
            _menuWatcher.NoteOwnHit();
        }

        // Hits a summon dealt before its owner was known (the meter started mid-fight) go to the
        // owner once found, instead of staying on a "Player #id" row.
        if (_liveSource?.Entities is Aion2.Aion2EntityDirectory resolvedIn)
        {
            var resolved = resolvedIn.DrainResolvedOwners();
            foreach ((int summon, int owner) in resolved)
            {
                _sessions.Reattribute(summon, owner);
                if (viewingLive)
                {
                    _aggregator.Reattribute(summon, owner);
                }
            }

            // The batch just polled is only applied to the grid below, so it still holds hits of such a summon under its own id: the owner
            // was found within this very batch (the recorder, fed before, has been handed over above). One burn tick of a Fire Wall
            // once stayed behind as a "Player #id" row of 389.
            if (viewingLive && resolved.Count > 0 && batch.Damage.Count > 0)
            {
                var ownerOf = new Dictionary<int, int>();
                foreach ((int summon, int owner) in resolved)
                {
                    ownerOf[summon] = owner; // the same summon twice: the later owner wins, as in the recorder
                }

                batch = batch with
                {
                    Damage = batch.Damage.Select(ev => ownerOf.TryGetValue(ev.SourceObjectId, out int owner) ? ev with { SourceObjectId = owner } : ev).ToList(),
                };
            }
        }

        if (viewingLive && _sessionView == SessionView.Current && batch.ZoneChanges.Count > 0)
        {
            // A map change cuts the stay in two: what happened before it still belongs to the old view, which is then
            // cleared (the old stay lives on in the session list) before the rest comes in.
            IReadOnlyList<DamageEvent> damage = batch.Damage;
            int from = 0;
            foreach (ZoneChange zone in batch.ZoneChanges)
            {
                int to = from;
                while (to < damage.Count && damage[to].Timestamp < zone.Timestamp)
                {
                    to++;
                }

                ApplyBatch(new CombatBatch(damage.Skip(from).Take(to - from).ToList(), Array.Empty<AvoidEvent>(), Array.Empty<KillEvent>()));
                ClearDamageData();
                from = to;
            }

            if (_sessions.Current is { Away.Ticks: > 0 })
            {
                // Back on a map after an instance run: the recorder picked the earlier stay up again, so its totals come back on show
                // (it already holds this batch's hits after the change).
                ShowLiveView(SessionView.Current);
                NoteBossActivity(damage.Skip(from).ToList());
                ApplyBatch(new CombatBatch(Array.Empty<DamageEvent>(), batch.Avoids, batch.Kills));
            }
            else
            {
                ApplyBatch(new CombatBatch(damage.Skip(from).ToList(), batch.Avoids, batch.Kills));
            }
        }
        else
        {
            ApplyBatch(batch);
        }

        // Once a second, an always-on-top meter goes back to the front: a borderless full-screen
        // game can take the front of the topmost band when it gets the focus back. While one of the meter's menus is open
        // nothing is raised: a window put over the menu took it out of sight and out of reach of the mouse.
        if (!_overlayMenuOpen)
        {
            if (Topmost && IsVisible)
            {
                NativeOverlay.KeepOnTop(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            }

            RaiseOverlays();
        }

        FollowBossAutomatically();
        TickHistory();
    }

    /// <summary>Once a second: the meter, then the other overlays above it.</summary>
    private void RaiseOverlays()
    {
        _meterOverlay?.KeepOnTop();

        // The timetable overlay always sits above the meter: raised right after it, in the same tick.
        _timetable?.RaiseToFront();
        _hotkeyOverlay?.RaiseToFront();
        _worldBoss?.RaiseToFront();
        _assist?.RaiseToFront();
        _loot?.RaiseToFront();
        _gatherOverlay?.RaiseToFront();
        _seasonOverlay?.RaiseToFront();
        _craft?.RaiseToFront();
        _combat?.RaiseToFront();
        _network?.RaiseToFront();
        _dummy?.RaiseToFront();
        _petFarm?.RaiseToFront();
        _petMapController?.RaiseToFront();
        _overlayMenu?.RaiseAll(); // the menu's button and the open menu above every other overlay
    }

    private void TickHistory()
    {
        // Every five seconds is plenty: a fight only counts as finished 120 s after its last hit.
        if (++_historyTickCounter >= 5)
        {
            _historyTickCounter = 0;
            _sessions.RefreshNames();
            LogFinishedFights();
        }
    }

    /// <summary>What the grid does with one batch of the source it shows: kills and avoids, boss tracking, the automatic reset
    /// between fights, then the events themselves.</summary>
    private void ApplyBatch(CombatBatch batch)
    {
        _avoids.AddRange(batch.Avoids);
        _kills.AddRange(batch.Kills);
        NoteBossKills(batch.Kills);
        IReadOnlyList<DamageEvent> events = batch.Damage;
        NoteBossActivity(events);
        NoteDefeatedBosses();

        // The "all sessions" view is a long record by design: the automatic reset between fights would leave only the last one. The next attempt
        // on a boss after a wipe starts from zero also with the automatic reset off (see BossFight.NextAttemptStart), the way a map change does.
        if (!_historyMode && _sessionView == SessionView.Current)
        {
            DateTime? keepFrom = null;
            bool fresh = _autoReset && StartsNewFight(events);
            if (!fresh && NextBossAttempt(events, afterWipeOnly: !_autoReset) is DateTime attempt)
            {
                fresh = true;
                keepFrom = attempt;
            }

            if (fresh)
            {
                // the next attempt's first hits can be on record already (they may come just before the game says the boss is in combat again): kept
                var kept = keepFrom is DateTime from ? _aggregator.Events.Where(ev => ev.Timestamp >= from).ToList() : null;
                var keptAvoids = keepFrom is DateTime avoidFrom ? _avoids.Where(a => a.Timestamp >= avoidFrom).ToList() : null;
                var keptKills = keepFrom is DateTime killFrom ? _kills.Where(k => k.Timestamp >= killFrom).ToList() : null;
                // Files the finished fight in the history, then starts from zero.
                ClearDamageData();
                if (kept is { Count: > 0 })
                {
                    _aggregator.IngestEvents(kept);
                }

                _avoids.AddRange(keptAvoids ?? new List<AvoidEvent>());
                _kills.AddRange(keptKills ?? new List<KillEvent>());
            }
        }

        if (events.Count > 0 || batch.Avoids.Count > 0 || batch.Kills.Count > 0)
        {
            if (events.Count > 0)
            {
                _aggregator.IngestEvents(events);
            }

            RefreshRows();
            // nobody picks a target on the overlay: it follows the newest fight, unless the player is working in the client
            if (events.Count > 0 && ((_hideUiActive && _compactOverlay) || (MeterOverlayActive && !IsActive)))
            {
                FollowNewestRun();
            }
        }
    }

    private void FeedSessions(CombatBatch batch)
    {
        if (!batch.IsEmpty)
        {
            _sessions.Feed(batch);
            _dummy?.Feed(batch, _liveSource?.Entities);
        }
    }

    /// <summary>What the server said about a target's hit points between two moments, kept with the session for the plausibility check.</summary>
    private TargetHp? HitPointsOfTarget(int id, DateTime from, DateTime to)
    {
        if (_liveSource?.Entities is not Aion2EntityDirectory directory)
        {
            return null;
        }

        IReadOnlyList<HpSample> samples = directory.HitPoints.SamplesAround(id, from, to);
        // where one attempt ends and the next begins: the boss back at full health, or out of combat alive (a wipe; see FailedEndsOf) - the same cuts
        // as the meter's run list. Without the second, a pull that failed a minute before the kill was saved in the kill's History entry
        // (4 Nov 2026, Transcendent Bakarma: 47.7K shown for a 79.8K fight, the minute between counted as fighting).
        DateTime now = DateTime.Now > to ? DateTime.Now : to;
        var cuts = directory.HitPoints.ResetsOf(id).Concat(directory.FailedEndsOf(id, now)).Distinct().OrderBy(t => t).ToList();
        return samples.Count < 2 ? null : new TargetHp(directory.HitPoints.HighestSeen(id) ?? 0, samples, cuts);
    }

    /// <summary>The name and class the live capture knows for an object id right now.</summary>
    private (string? Name, string? Class) IdentifyForSession(int id)
    {
        if (_liveSource?.Entities is not Aion2EntityDirectory directory)
        {
            return (null, null);
        }

        return (directory.NameFor(id) ?? directory.MonsterNameFor(id), directory.ClassOf(id));
    }

    /// <summary>
    /// Whether this batch's first damage comes <see cref="_autoResetIdle"/> or more after the last
    /// damage on record - by the events' own times, so a replay behaves like the live game. Never
    /// while a boss fight is unfinished (a boss seen hurt but alive): a phase where nobody can hit
    /// it must not cut it in two. A wipe resets the boss to full health, which ends that fight.
    /// </summary>
    private bool StartsNewFight(IReadOnlyList<DamageEvent> batch)
    {
        DateTime? first = null;
        foreach (DamageEvent ev in batch)
        {
            if (!ev.IsHeal && (first is null || ev.Timestamp < first))
            {
                first = ev.Timestamp;
            }
        }

        if (first is not DateTime start)
        {
            return false;
        }

        IReadOnlyList<DamageEvent> recorded = _aggregator.Events;
        DateTime? last = null;
        for (int i = recorded.Count - 1; i >= 0; i--)
        {
            if (!recorded[i].IsHeal)
            {
                last = recorded[i].Timestamp;
                break;
            }
        }

        return last is DateTime end && start - end >= _autoResetIdle && !BossFightUnfinished(now: start);
    }

    /// <summary>
    /// The first hit on a boss (or on a boss whose last attempt failed: reset to full health by a wipe, or out of combat alive for a while)
    /// starts the meter from zero: the trash before it, or the failed attempt, is filed in the history and the fight on screen is the
    /// pull. See <see cref="BossFight.StartsNextAttempt"/>. (3 November 2026, the owner: a failed fight must not count into the next one.)
    /// <paramref name="afterWipeOnly"/>: only the failed attempt, which ends the fight on screen also with the automatic reset off.
    /// </summary>
    private DateTime? NextBossAttempt(IReadOnlyList<DamageEvent> batch, bool afterWipeOnly) =>
        _source?.Entities is Aion2.Aion2EntityDirectory directory ? BossFight.NextAttemptStart(batch, _aggregator.Events, directory, afterWipeOnly) : null;

    /// <summary>A boss fight still going (see <see cref="BossFight.Unfinished"/>): it keeps the reset after a quiet spell away.</summary>
    private bool BossFightUnfinished(int? except = null, DateTime? now = null) =>
        _source?.Entities is Aion2.Aion2EntityDirectory directory && BossFight.Unfinished(directory, _aggregator.Events, except, now);

    /// <summary>The old per-fight history (fights.db) is gone for good - sessions replaced it - so a file left by an earlier version is removed.</summary>
    private static void DeleteLegacyFightHistory()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Brand.DataFolder);
        foreach (string name in new[] { "fights.db", "fights.db-wal", "fights.db-shm" })
        {
            try
            {
                File.Delete(Path.Combine(folder, name));
            }
            catch (Exception)
            {
                // Locked or read-only: harmless, it is tried again at the next start.
            }
        }
    }

    private void OnAppMenuClicked(object sender, RoutedEventArgs e)
    {
        // AppMenu has no child MenuItems anymore (AppMenuFlyoutStyle hand-authors the Popup
        // content instead), so WPF assigns it MenuItemRole.TopLevelItem instead of
        // TopLevelHeader and never opens IsSubmenuOpen on click by itself - toggle it here.
        AppMenu.IsSubmenuOpen = !AppMenu.IsSubmenuOpen;
        e.Handled = true;
    }

    private void OnHistoryBannerClicked(object sender, MouseButtonEventArgs e) => OnSessionBannerLeave();

    private void ExitHistoryMode()
    {
        if (!_historyMode)
        {
            return;
        }

        _historyMode = false;
        _viewedSessionNumber = null;
        _historyExtras = null;
        HistoryBanner.Visibility = Visibility.Collapsed;
        if (_liveSource is null)
        {
            ClearDamageData();
            StartCapture(MeterSettings.Load());
            RefreshRows();
            return;
        }

        // The capture never stopped: drop the replay and show the live view again.
        if (!ReferenceEquals(_source, _liveSource))
        {
            _source?.Dispose();
        }

        _source = _liveSource;
        ShowLiveView(_sessionView);
    }

    /// <summary>Shows a stored fight or session from a replay source while the live capture goes on in the background (see <see cref="_liveSource"/>).</summary>
    private void ShowReplay(ICombatSource replay)
    {
        if (!ReferenceEquals(_source, _liveSource))
        {
            _source?.Dispose();
        }

        _source = replay;
        replay.StatusChanged += OnSourceStatusChanged;
        replay.Start();
    }

    /// <summary>The live grid again, filled for <paramref name="view"/>: the stay in progress so far, or everything recorded.</summary>
    private void ShowLiveView(SessionView view)
    {
        _sessionView = view;
        ClearDamageData();
        if (view == SessionView.All)
        {
            _aggregator.IngestEvents(_sessions.AllEvents().Concat(_sessions.AllShields()));
            HistoryBanner.Text = LocalizationManager.Instance["Main.SessionAllBanner"];
            HistoryBanner.Visibility = Visibility.Visible;
        }
        else
        {
            if (_sessions.Current is { } stay)
            {
                _aggregator.IngestEvents(stay.Data.Events.Concat(stay.Data.Shields));
            }

            HistoryBanner.Visibility = Visibility.Collapsed;
        }

        RefreshRows();
    }

    /// <summary>A banner click: back to the live view of the current stay (from a stored session, a fight, or the all-sessions view).</summary>
    private void OnSessionBannerLeave()
    {
        if (_historyMode)
        {
            _sessionView = SessionView.Current;
            ExitHistoryMode();
        }
        else if (_sessionView == SessionView.All)
        {
            ShowLiveView(SessionView.Current);
        }
    }

    /// <summary>The name the packet stream gave the object (player name, boss name from its NPC id,
    /// or "Class #id" until a player's name arrives); its hex id when nothing is known.</summary>
    private string ResolveDisplayName(int objectId) =>
        _source?.Entities.NameFor(objectId) ?? MonsterName(objectId) ?? $"0x{objectId:X8}";

    /// <summary>A monster's name from the NPC table, for the targets in the lists and the details windows.</summary>
    private string? MonsterName(int objectId) => (_source?.Entities as Aion2.Aion2EntityDirectory)?.MonsterNameFor(objectId);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _overlay = new NativeOverlay(this);
        CreateTrayIcon();
        _hotkeys = new GlobalHotkeys(this);
        _hotkeys.Pressed += action => Dispatcher.Invoke(() => OnHotkeyPressed(action), System.Windows.Threading.DispatcherPriority.Input);
        ApplyHotkeys(MeterSettings.Load());
        ApplyTimetable(MeterSettings.Load());
        _timetableReminder ??= new TimetableReminder();
        ApplyPetFarm(MeterSettings.Load());
        ApplyHotkeyOverlay(MeterSettings.Load());
        if (!Headless)
        {
            // the world boss overlay and sounds: the lists the game sends are kept in a file, and the controller reads the settings every second
            Aion2FieldBosses.EnablePersistence();
            Aion2Tickets.EnablePersistence();
            Aion2Achievements.EnablePersistence(); // every achievement counter per character (the season missions are among them)
            Aion2Supply.EnablePersistence(); // the supply requests of every character (the checklist's card)
            Aion2Inventories.EnablePersistence(); // every character's bag and storages from its last login (the checklist, the crafting tool)
            Aion2Tickets.Changed += () => Aion2Tickets.Flush();
            // a change within a store's few seconds of quiet waited for the next change or the exit: a crash in between lost it (3 November 2026)
            _storeFlush ??= new System.Threading.Timer(_ => FlushStores(force: false), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            HookGroupAlerts(); // the group finder's news while the game is in the background
            AionDPS.Upload.RosterSync.Start(); // the roster on the site (signed in only; Settings can switch it off)
            AionDPS.Upload.ProfileShare.Start(); // Genus Insight and pets for other players' Find a player (signed in only; the account window can switch it off)
            // the character played, verified as the player's on the site once the game named it at a login (signed in only); a short note when it was
            AionDPS.Upload.CharacterVerify.Start(() => _liveSource?.Entities as Aion2.Aion2EntityDirectory);
            // the legion's member list from the login, for the Discord servers that follow the legion (signed in and verified only; the account window can switch it off)
            AionDPS.Upload.LegionShare.Start(() => _liveSource?.Entities as Aion2.Aion2EntityDirectory);
            // the CP calculator checked against the game's own Combat Power at every login (the legion list carries it), per character, kept in a local file
            AionDPS.Combat.CpChecks.Start(() => _liveSource?.Entities as Aion2.Aion2EntityDirectory);
            AionDPS.Upload.CharacterVerify.Verified += (name, server) => Dispatcher.BeginInvoke(new Action(() =>
                ShowUploadStatus(string.Format(LocalizationManager.Instance["Verify.StatusLine"], name, server))));
            EnsureRestrictableControllers();
            _network ??= new NetworkOverlayController();
            Aion2Loot.EnablePersistence();
            Aion2BlackCloud.EnablePersistence(); // every character's pets and the Black Cloud Merchant's last offers (the notes, the Pets page)
            Aion2BlackCloud.Offered += (name, offered) => Dispatcher.BeginInvoke(new Action(() => OnBlackCloudOffered(name, offered)));
            Aion2BlackCloud.Bought += (_, pet) => Dispatcher.BeginInvoke(new Action(() => ShopMarks.Drop(pet)));
            Aion2Cooldowns.ClassChanged += (before, after) => Dispatcher.BeginInvoke(new Action(() => AssistController.OnClassChanged(before, after)));
            Aion2GatherLog.EnablePersistence();
            _loot ??= new LootOverlayController();
            _gatherOverlay ??= new GatherOverlayController();
            _seasonOverlay ??= new SeasonOverlayController();
            // the exact own position from the game's minimap (Tools, Cube finder): a note on the meter the first time it gives one
            _minimapReader ??= new MinimapReader(() => _liveSource?.Entities as Aion2.Aion2EntityDirectory, text => ShowUploadStatus(text));
            _craft ??= new CraftOverlayController(() => _liveSource?.Entities as Aion2.Aion2EntityDirectory);
            _menuWatcher ??= new MenuWatcher();
            _streamTimer.Tick += (_, _) => StreamTick();
            _streamTimer.Start();
            _journeyTimer.Tick += (_, _) => JourneyMinute();
            _journeyTimer.Start();
            MeterSettings.BackupOnStart(); // after the first Load of this start, which restores a damaged file from the last copy
            if (MeterSettings.RecoveredFromBackup)
            {
                ShowUploadStatus(LocalizationManager.Instance["Main.SettingsRecovered"]);
            }
            _gateTimer.Tick += (_, _) => OnGateTick();
            _gateTimer.Start();
            CreateMeterOverlay();
            _sessions.StayClosed += stay => Dispatcher.BeginInvoke(new Action(() => LogStay(stay)));
            BuildShell();
            SizeForShell();
        }
    }

    /// <summary>The first start with the app's sections: the small meter window becomes an app window (the meter itself is over the game now).</summary>
    private void SizeForShell()
    {
        var settings = MeterSettings.Load();
        if (settings.AppShellSized)
        {
            return;
        }

        settings.AppShellSized = true;
        settings.Save();
        Rect area = SystemParameters.WorkArea;
        Width = Math.Max(Width, Math.Min(1180, area.Width - 40));
        Height = Math.Max(Height, Math.Min(760, area.Height - 40));
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    /// <summary>Moves the compact panel out of this window into the overlay window over the game; this window stays the client. The panel's
    /// fields, handlers and bindings go with it, so everything that fills it works unchanged.</summary>
    private void CreateMeterOverlay()
    {
        if (_meterOverlay is not null || CompactOverlayPanel.Parent is not Panel host)
        {
            return;
        }

        if (_hideUiActive)
        {
            SetHideUi(); // the window's own overlay look ends: the panel moves out
        }

        host.Children.Remove(CompactOverlayPanel);
        _meterOverlay = new MeterOverlayWindow(CompactOverlayPanel, Resources);
        _meterOverlay.SetClickThrough(_compactClickThrough);
        _meterOverlay.GrowUp = _meterGrowUp;
        // the layout editor scales the meter like its own corner grip does
        _meterOverlay.ScaleOf = () => CompactOverlayScale.ScaleX;
        _meterOverlay.Rescale = (scale, save) =>
        {
            SetCompactOverlayScale(scale);
            if (save)
            {
                var s = MeterSettings.Load();
                s.OverlayScale = CompactOverlayScale.ScaleX;
                s.Save();
            }
        };
        UpdateScopeControls();
        RefreshRows();
        RefreshUpdateBadge();
        UpdateMeterOverlay();
    }

    /// <summary>
    /// A few times a second (with the game gate): the overlay is on screen while it is switched on, the game (or this app) is in front and, with
    /// "only during boss fights", the own character fights a boss or went on fighting after one (Settings: how long) - or the eye button just asked for
    /// it (see <see cref="MeterOverlayRule"/>).
    /// </summary>
    private void UpdateMeterOverlay()
    {
        if (_meterOverlay is null)
        {
            return;
        }

        DateTime now = DateTime.Now;
        bool kept = now < _overlayPeekUntil || _meterOverlay.IsMouseOver || _overlayMenuOpen || OverlayFind.Visibility == Visibility.Visible
            || _dummy?.Model.State is DummyState.Armed or DummyState.Running or DummyState.Finished;
        bool inFight = MeterOverlayRule.Shown(_meterOverlayAutoHide, _meterPopUpForBoss, _meterOverlayHideAfter, now - _lastOwnBossFight, now - _lastBossFight,
            now - _lastOwnCombat, _meterOverlay.Wanted, kept);
        _meterOverlay.ShowOverlay(MeterOverlayActive && _gameGateOpen && inFight);
    }

    /// <summary>A training dummy (Training Scarecrow, Punching Bag ..., by its name in any language), known per target id.</summary>
    private bool IsDummyTarget(Aion2.Aion2EntityDirectory directory, int targetId)
    {
        if (_dummyTargets.TryGetValue(targetId, out bool known))
        {
            return known;
        }

        if (_dummyTargets.Count > 2000)
        {
            _dummyTargets.Clear();
        }

        string? name = directory.NameFor(targetId) ?? directory.MonsterNameFor(targetId);
        bool dummy = DummyTargets.IsDummy(name);
        if (name is not null)
        {
            _dummyTargets[targetId] = dummy; // a target not named yet is asked again
        }

        return dummy;
    }

    /// <summary>
    /// Where the player is now, for whose rows are shown: at a dummy (the own character hit one in the last 20 seconds, or a dummy run waits or runs),
    /// in an instance or arena, or elsewhere. A new place drops the switch's pick for the old one.
    /// </summary>
    private void UpdateScopeContext()
    {
        bool atDummy = DateTime.Now - _lastDummyHit < TimeSpan.FromSeconds(20) || _dummy?.Model.State is DummyState.Armed or DummyState.Running;
        ScopeContext context = atDummy ? ScopeContext.Dummy : InInstanceNow() ? ScopeContext.Instance : ScopeContext.World;
        if (context == _scopeContext)
        {
            return;
        }

        _scopeContext = context;
        _scopeManual = null;
        UpdateScopeControls();
        RefreshRows();
    }

    // ---- what is counted: dungeons, arenas, boss fights and training dummies; the open world only when its button is on ----

    /// <summary>The open world is counted too (the Open world button; off by default: every mob and passer-by around piled up in the list for hours).</summary>
    private bool _trackOpenWorld;

    /// <summary>How long after the last hit on or by a boss the open world stays counted (a boss's quiet phases, the adds of its fight).</summary>
    private static readonly TimeSpan BossFightHold = TimeSpan.FromSeconds(30);

    private DateTime _notCountedSaid = DateTime.MinValue;

    /// <summary>
    /// Whether what happens now is counted: always in a dungeon or arena, during a boss fight (any boss fought nearby, in the last 30 s), at a training
    /// dummy and in PvP mode; in the rest of the open world only with the Open world button on. Before the game has said where the player is, everything is.
    /// </summary>
    private bool CountingNow()
    {
        if (_trackOpenWorld || _gateSettings.MeterGrind || _pvpOnly || _liveSource is not Aion2.Aion2PacketCombatSource source || source.CurrentZoneId <= 0)
        {
            return true;
        }

        DateTime now = DateTime.Now;
        return InInstanceNow() || now - _lastBossFight < BossFightHold || now - _lastPvpFight < BossFightHold || now - _lastDummyHit < TimeSpan.FromSeconds(20)
            || _dummy?.Model.State is DummyState.Armed or DummyState.Running;
    }

    /// <summary>A poll's events as the meter counts them: in the open world without a boss or a dummy (and the button off) none, the zone changes kept.</summary>
    private CombatBatch Counted(CombatBatch batch)
    {
        if (CountingNow() || (batch.Damage.Count == 0 && batch.Avoids.Count == 0 && batch.Kills.Count == 0))
        {
            return batch;
        }

        if (DateTime.Now - _notCountedSaid > TimeSpan.FromMinutes(2) && batch.Damage.Count > 0)
        {
            _notCountedSaid = DateTime.Now;
            ShowUploadStatus(LocalizationManager.Instance["Main.OpenWorld.NotCounted"]);
        }

        return batch with { Damage = Array.Empty<DamageEvent>(), Avoids = Array.Empty<AvoidEvent>(), Kills = Array.Empty<KillEvent>() };
    }

    private void OnOpenWorldClicked(object sender, RoutedEventArgs e)
    {
        _trackOpenWorld = !_trackOpenWorld;
        var settings = MeterSettings.Load();
        settings.TrackOpenWorld = _trackOpenWorld;
        settings.Save();
        _notCountedSaid = DateTime.MinValue;
        UpdateOpenWorldButton();
    }

    /// <summary>The Open world button: lit (gold) while the open world is counted.</summary>
    private void UpdateOpenWorldButton()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        string brush = _trackOpenWorld ? "Brush.Accent" : "Brush.TextMuted";
        OpenWorldGlobe.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush);
        OpenWorldLines.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush);
        OpenWorldLabel.SetResourceReference(TextBlock.ForegroundProperty, _trackOpenWorld ? "Brush.Text" : "Brush.TextMuted");
        OpenWorldLabel.Text = loc[_trackOpenWorld ? "Main.OpenWorld.On" : "Main.OpenWorld.Off"];
        OpenWorldButton.ToolTip = loc["Main.OpenWorld.Tooltip"];
    }

    /// <summary>The player is in an instance or an arena now (the last zone frame said so).</summary>
    private bool InInstanceNow() =>
        _liveSource is Aion2PacketCombatSource source && source.CurrentZoneId is > 0 and var zone && (Aion2Zones.IsInstance(zone) || Aion2Zones.IsArena(zone));

    /// <summary>Notes a poll's own fighting: a hit by or on the own character (heals do not count: regeneration and potions would keep the overlay up
    /// for ever). Before the own character is known, any player's hit counts. True when there was some.</summary>
    private bool NoteOwnCombat(IReadOnlyList<DamageEvent> events)
    {
        if (events.Count == 0 || _liveSource?.Entities is not Aion2.Aion2EntityDirectory directory)
        {
            return false;
        }

        int local = directory.LocalPlayerId >= 0 ? directory.LocalPlayerId : directory.InferLocalPlayer() ?? -1;
        if (local >= 0)
        {
            foreach (DamageEvent ev in events)
            {
                if (!ev.IsHeal && ev.SourceObjectId == local && IsDummyTarget(directory, ev.TargetObjectId))
                {
                    _lastDummyHit = DateTime.Now;
                    break;
                }
            }
        }

        // a boss fought by anyone nearby (the group, or another party starting it): with the own fighting right then, the meter comes up for it
        foreach (DamageEvent ev in events)
        {
            if (!ev.IsHeal && (directory.BossNpcIdOf(ev.TargetObjectId) is not null || directory.BossNpcIdOf(ev.SourceObjectId) is not null))
            {
                _lastBossFight = DateTime.Now;
                break;
            }
        }

        // the own character in a boss fight (it hit the boss, or the boss hit it): the overlay comes up for it (only while it is switched on)
        if (local >= 0)
        {
            foreach (DamageEvent ev in events)
            {
                if (!ev.IsHeal && ((ev.SourceObjectId == local && directory.BossNpcIdOf(ev.TargetObjectId) is not null)
                    || (ev.TargetObjectId == local && directory.BossNpcIdOf(ev.SourceObjectId) is not null)))
                {
                    bool fresh = DateTime.Now - _lastOwnBossFight > NewBossFightAfter;
                    _lastOwnBossFight = DateTime.Now;
                    if (fresh)
                    {
                        PopUpForBoss();
                    }

                    break;
                }
            }
        }

        // the own character fighting another player (it hit one, or one hit it): PvP is counted wherever it happens, the Abyss too (7 November 2026, a
        // tester: the fights against the other side there were missing while the open world was off)
        if (directory.LocalPlayerId is int me and >= 0)
        {
            foreach (DamageEvent ev in events)
            {
                if (!ev.IsHeal && ev.SourceObjectId != ev.TargetObjectId
                    && ((ev.SourceObjectId == me && directory.IsKnownPlayer(ev.TargetObjectId)) || (ev.TargetObjectId == me && directory.IsKnownPlayer(ev.SourceObjectId))))
                {
                    _lastPvpFight = DateTime.Now;
                    break;
                }
            }
        }

        local = directory.LocalPlayerId;
        foreach (DamageEvent ev in events)
        {
            if (ev.IsHeal)
            {
                continue;
            }

            // before the own character is known: any player's hit (or anything that is no monster hitting no player: the own character has no name frame yet)
            if (local >= 0 ? ev.SourceObjectId == local || ev.TargetObjectId == local
                : directory.IsKnownPlayer(ev.SourceObjectId) || (!directory.IsKnownMonster(ev.SourceObjectId) && !directory.IsKnownPlayer(ev.TargetObjectId)))
            {
                _lastOwnCombat = DateTime.Now;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A boss fight of the own character began: the overlay comes up by itself right away (Settings: "Come up by itself for boss fights", on by default).
    /// Only while the meter is switched on: switched off (the eye button, the overlay's X, its hotkey, the tray or Settings), it stays off - players
    /// had it coming back at every boss after they had closed it (4 Nov 2026).
    /// </summary>
    private void PopUpForBoss()
    {
        if (!_meterPopUpForBoss || !MeterOverlayActive || Headless || _historyMode)
        {
            return;
        }

        UpdateMeterOverlay(); // the boss fight is under way (_lastOwnBossFight): it shows, and steps aside again as usual after the fight
    }

    /// <summary>The eye button, its hotkey and the tray: with the overlay off it comes on; on but stepped aside between fights it shows now (until a
    /// fight's time would be over); on screen it goes off. In the chip look the window itself turns into the overlay, as before.</summary>
    private void ToggleOverlay()
    {
        if (_meterOverlay is null)
        {
            SetHideUi();
            return;
        }

        var settings = MeterSettings.Load();
        if (_meterOverlayOn && _meterOverlay.Wanted)
        {
            _meterOverlayOn = false;
            _overlayPeekUntil = DateTime.MinValue;
        }
        else
        {
            _meterOverlayOn = true;
            _overlayPeekUntil = DateTime.Now + _meterOverlayHideAfter;
            FollowNewestRun();
        }

        settings.MeterOverlayOn = _meterOverlayOn;
        settings.Save();
        _settingsWindow?.SyncMeterOverlay(_meterOverlayOn);
        UpdateScopeControls();
        RefreshRows(); // the scope applies only while the overlay is on
        RefreshUpdateBadge();
        UpdateMeterOverlay();
    }

    // ---- the history: every dungeon run, boss fight, dummy parse and arena stay is saved on its own as it ends (History/FightLog) ----

    /// <summary>What was saved already: a map stay ends again each time the player comes back from an instance, and its boss fights must not be saved twice.</summary>
    private readonly HashSet<string> _loggedFights = new();

    /// <summary>1 while the open stay's finished fights are being looked at (<see cref="LogFinishedFights"/>).</summary>
    private int _loggingOpen;

    /// <summary>A boss attempt counts as over this long after its last hit (the blows the game reports just after a kill come within a second).</summary>
    private static readonly TimeSpan AttemptOverAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Every five seconds: the boss fights of the stay the player is still in that are over - killed, or wiped (the boss back at full health or out of
    /// combat alive after them) - go to the History at once, each attempt on its own, instead of when the player leaves (6 November 2026, the owner:
    /// "every log should show after the kill, don't wait for the whole raid to be over ... wipe or clear, like boss 1 2 3"). The run itself, gate to
    /// gate, is saved when the stay ends (<see cref="LogStay"/>); a fight saved here is not saved again then.
    /// </summary>
    private void LogFinishedFights()
    {
        if (Headless || _sessions.Current is not { } open || open.FromFile || open.Data.Events.Count == 0
            || _liveSource?.Entities is not Aion2.Aion2EntityDirectory || System.Threading.Interlocked.CompareExchange(ref _loggingOpen, 1, 0) != 0)
        {
            return;
        }

        // a copy up to now: the open stay grows on this thread while the analysis runs in the background
        var copy = new SessionInfo(open.Number, open.ZoneId, open.Kind, open.Start) { End = open.Data.Events[^1].Timestamp };
        copy.Data.IngestEvents(open.Data.Events.Concat(open.Data.Shields).ToList());
        foreach ((int id, string name) in open.Names)
        {
            copy.Names[id] = name;
        }

        foreach ((int id, string cls) in open.Classes)
        {
            copy.Classes[id] = cls;
        }

        copy.Enrages.AddRange(open.Enrages);
        _sessions.CaptureHp(copy);
        if (LogStay(copy, partial: true) is { } work)
        {
            work.ContinueWith(_ => System.Threading.Interlocked.Exchange(ref _loggingOpen, 0), TaskScheduler.Default);
        }
        else
        {
            System.Threading.Interlocked.Exchange(ref _loggingOpen, 0);
        }
    }

    /// <summary>A stay ended: an instance is a dungeon run (an arena a PvP stay), and every boss fight in it, in an instance or in the open world, is an entry
    /// of its own. The data is taken here; the analysis and the files are made off the window's thread. <paramref name="partial"/>: the stay is still
    /// open (<see cref="LogFinishedFights"/>): only the boss attempts that are over, no run. Returns the background work, or null when there is none.</summary>
    private Task? LogStay(SessionInfo stay, bool partial = false)
    {
        if (Headless || stay.FromFile || stay.End is not DateTime end || _liveSource?.Entities is not Aion2.Aion2EntityDirectory directory)
        {
            return null;
        }

        List<DamageEvent> events = stay.Data.Events.ToList();
        if (events.Count == 0)
        {
            return null;
        }

        List<DamageEvent> shields = stay.Data.Shields.ToList();
        var names = new Dictionary<int, string>(stay.Names);
        var classes = new Dictionary<int, string>(stay.Classes);
        var seen = new HashSet<int>();
        foreach (DamageEvent e in events)
        {
            seen.Add(e.SourceObjectId);
            seen.Add(e.TargetObjectId);
        }

        string? ownName = directory.LocalCharacter?.Name;
        int local = directory.LocalPlayerId;
        var players = classes.Keys.Where(seen.Contains).Select(id =>
        {
            string name = names.GetValueOrDefault(id) ?? ResolveDisplayName(id);
            int? cp = directory.SeenProfileOf(id)?.CombatPower;
            return new ReportPlayer(id, name, classes[id], cp is 397228 ? null : cp, id == local || (ownName is not null && name == ownName));
        }).ToList();
        if (players.Count == 0)
        {
            return null;
        }

        var playerIds = players.Select(p => p.Id).ToHashSet();
        var avoids = _avoids.Where(a => a.Timestamp >= stay.Start && a.Timestamp <= end).ToList();
        Func<int, string?> nameOf = id => names.GetValueOrDefault(id) ?? ResolveDisplayName(id);
        bool Keep(int id) => playerIds.Contains(id) || directory.BossNpcIdOf(id) is not null;
        var bossNpc = new Dictionary<int, int?>();
        int? NpcOf(int id) => bossNpc.TryGetValue(id, out int? npc) ? npc : bossNpc[id] = BossNpcIdOf(id);
        bool arena = Aion2Zones.IsArena(stay.ZoneId);
        bool run = stay.Kind == SessionKind.Instance || arena;
        string zone = Aion2Zones.NameOf(stay.ZoneId, "en") ?? SessionLabels.NameOf(stay);
        int zoneId = stay.ZoneId;
        // the server of the own character, so a fight can still be uploaded from the History later (HistoryUpload)
        int? serverId = Aion2OwnServerId();
        string stayKey = $"stay:{stay.Number}:{stay.Start.Ticks}";
        FightExtras? runExtras = run && !partial && !_loggedFights.Contains(stayKey) ? directory.Combat.Slice(stay.Start, end, nameOf, Keep) : null;
        // a boss attempt of a stay still open is over when it was killed, or wiped: the boss back at full health or out of combat alive after it began;
        // either way nobody has hit it for a few seconds
        bool Over(BossAttempt attempt)
        {
            DateTime now = DateTime.Now > end ? DateTime.Now : end;
            if (now - attempt.End < AttemptOverAfter)
            {
                return false;
            }

            return attempt.Killed || directory.HitPoints.ResetsOf(attempt.TargetId).Concat(directory.FailedEndsOf(attempt.TargetId, now)).Any(cut => cut > attempt.Start);
        }

        // the boss fights' extras are cut here (the capture forgets), the analysis that finds the fights runs in the background
        return Task.Run(() =>
        {
            try
            {
                SessionAnalysis analysis = SessionAnalyzer.Analyze(stay);
                var jobs = new List<(FightLogKind Kind, ReportInput Input, string? Detail, int Fights)>();
                var attempts = analysis.Attempts.ToList();
                foreach (BossAttempt attempt in attempts)
                {
                    if (partial && !Over(attempt))
                    {
                        continue; // still being fought (or not known to be over): saved later
                    }

                    string key = $"boss:{attempt.TargetId}:{attempt.Start.Ticks}";
                    bool fresh;
                    lock (_loggedFights)
                    {
                        fresh = _loggedFights.Add(key);
                    }

                    if (!fresh)
                    {
                        continue;
                    }

                    DateTime from = attempt.Start.AddSeconds(-2), to = attempt.End.AddSeconds(2);
                    var window = events.Where(e => e.Timestamp >= from && e.Timestamp <= to).ToList();
                    var inWindow = window.SelectMany(e => new[] { e.SourceObjectId, e.TargetObjectId }).ToHashSet();
                    FightExtras extras = Dispatcher.Invoke(() => directory.Combat.Slice(from, to, nameOf, Keep));
                    jobs.Add((FightLogKind.Boss, new ReportInput(attempt.Name, attempt.Start, attempt.End, players.Where(p => inWindow.Contains(p.Id)).ToList(), window,
                        shields.Where(e => e.Timestamp >= from && e.Timestamp <= to).ToList(), avoids.Where(a => a.Timestamp >= from && a.Timestamp <= to).ToList(),
                        extras, attempt.TargetId, NpcOf(attempt.TargetId), attempt.Killed, nameOf, new HashSet<int> { attempt.TargetId }), run ? zone : null, 0));
                }

                if (runExtras is not null)
                {
                    bool freshRun;
                    lock (_loggedFights)
                    {
                        freshRun = _loggedFights.Add(stayKey);
                    }

                    // the run from the first hit to the last (the walk to the first pull is no part of it)
                    var fighting = events.Where(e => !e.IsHeal && playerIds.Contains(e.SourceObjectId) && !playerIds.Contains(e.TargetObjectId)).ToList();
                    if (freshRun && fighting.Count > 0)
                    {
                        DateTime first = fighting.Min(e => e.Timestamp), last = fighting.Max(e => e.Timestamp);
                        BossAttempt? lastBoss = attempts.LastOrDefault();
                        jobs.Insert(0, (arena ? FightLogKind.Pvp : FightLogKind.Run, new ReportInput(zone, first, last, players, events, shields, avoids, runExtras,
                            lastBoss?.TargetId, lastBoss is null ? null : NpcOf(lastBoss.TargetId), lastBoss?.Killed ?? false, nameOf, null), null, attempts.Count));
                    }
                }

                foreach (var job in jobs)
                {
                    FightLog.Save(job.Kind, job.Input, job.Detail, zoneId, job.Fights, serverId);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                AutoUploadLog.Write("history: a stay could not be saved: " + ex.Message);
            }
        });
    }

    /// <summary>A dummy parse is over: an entry of the history's training dummy tab (your hits, skills and effects over its exact time).</summary>
    private void LogDummyRun(DummyRun run)
    {
        if (Headless || DummyReportInput(run) is not { } input)
        {
            return;
        }

        string detail = $"{(int)run.Planned.TotalMinutes}:{run.Planned.Seconds:00} parse" + (run.Length < run.Planned - TimeSpan.FromSeconds(1) ? " · ended early" : "");
        _ = Task.Run(() =>
        {
            try
            {
                FightLog.Save(FightLogKind.Dummy, input, detail);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AutoUploadLog.Write("history: a dummy parse could not be saved: " + ex.Message);
            }
        });
    }

    /// <summary>A dummy run as a fight report's input: the player alone against the dummy hit the most; null for a run without hits.</summary>
    private ReportInput? DummyReportInput(DummyRun run)
    {
        if (run.Events.Count == 0)
        {
            return null;
        }

        var directory = _liveSource?.Entities as Aion2.Aion2EntityDirectory;
        var names = new Dictionary<int, string>(run.Names);
        string name = names.GetValueOrDefault(run.PlayerId) ?? directory?.NameFor(run.PlayerId) ?? CompactOverlayOwnName() ?? "You";
        int? cp = directory?.SeenProfileOf(run.PlayerId)?.CombatPower;
        int dummy = run.Events.GroupBy(e => e.TargetObjectId).OrderByDescending(g => g.Sum(e => e.Amount)).First().Key;
        string dummyName = names.GetValueOrDefault(dummy) ?? directory?.MonsterNameFor(dummy) ?? "Training dummy";
        names[run.PlayerId] = name;
        return new ReportInput(dummyName, run.Start, run.End, new[] { new ReportPlayer(run.PlayerId, name, ResolveClassName(run.PlayerId), cp, true) },
            run.Events.ToList(), Array.Empty<DamageEvent>(), Array.Empty<AvoidEvent>(),
            new FightExtras(run.Uses.ToList(), run.Buffs.ToList(), Array.Empty<StaggerEvent>(), names), dummy, null, false, id => names.GetValueOrDefault(id), null);
    }

    /// <summary>A finished dummy run's full fight report (skills, timeline, buffs …) as for a boss fight, on this PC only: nothing is uploaded.</summary>
    private async void OpenDummyRun(DummyRun run)
    {
        if (!run.Closed || DummyReportInput(run) is not { } input)
        {
            return;
        }

        try
        {
            FightReport report = await Task.Run(() => FightReport.Build(input));
            new FightReportWindow(report) { Owner = IsVisible ? this : null }.Show();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            AutoUploadLog.Write("dummy: the report of a run could not be built: " + ex.Message);
        }
    }

    private HistoryWindow? _historyWindow;

    /// <summary>A saved fight's report, shown in the History page (no window of its own).</summary>
    private void OpenHistoryEntry(FightLogEntry entry)
    {
        OpenHistory(null);
        HistoryWindow? history = _shellBuilt ? _shellWindow as HistoryWindow : _historyWindow;
        history?.Open(entry);
    }

    /// <summary>The history window: every run, boss fight, dummy parse and arena stay saved on this PC.</summary>
    private void OpenHistory() => OpenHistory(null);

    /// <summary>The history window, on one of its tabs ("Training dummy" …) when given.</summary>
    private void OpenHistory(string? tab)
    {
        ShowClient();
        if (_shellBuilt)
        {
            ShowShellPage("history");
            if (tab is not null && _shellWindow is HistoryWindow hosted)
            {
                hosted.ShowTab(tab);
            }

            return;
        }

        if (_historyWindow is not { IsLoaded: true })
        {
            _historyWindow = new HistoryWindow { Owner = this };
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            _historyWindow.Show();
        }

        if (tab is not null)
        {
            _historyWindow.ShowTab(tab);
        }

        _historyWindow.Activate();
    }

    private void OnHistoryClicked(object sender, RoutedEventArgs e) => OpenHistory();

    /// <summary>Brings the client window back (from the tray, minimized or behind the game).</summary>
    private void ShowClient()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Restore();
                return;
            }

            Show();
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>
    /// Saves window geometry here, not OnClosed -- by the time OnClosed fires the window is
    /// already torn down and its bounds/RestoreBounds are no longer reliable, whereas OnClosing
    /// still runs with a fully intact window. Loads settings fresh from disk rather than reusing
    /// whatever was read at startup, so this can't clobber unrelated fields (Characters, Settings-
    /// dialog choices, etc.) with a stale in-memory snapshot if the Settings dialog saved its own
    /// changes sometime after this window was constructed.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowStateToSettings();
        base.OnClosing(e);
        if (!e.Cancel)
        {
            PopOutWindow.Exiting = true; // the pop-out windows close with the meter: they are to come back at the next start
        }
    }

    /// <summary>Extracted from OnClosing so the update restart can reuse it: Velopack's
    /// ApplyUpdatesAndRestart ends the process itself and never reaches OnClosing, which would
    /// silently lose the window position every time someone updated.</summary>
    private void SaveWindowStateToSettings()
    {
        var settings = MeterSettings.Load();
        SaveWindowGeometry(settings);
        settings.Save();
    }

    /// <summary>
    /// Every store's unsaved changes to its file: <paramref name="force"/> on closing and before an update restart (Velopack's restart ends the process
    /// without OnClosed), otherwise each store keeps its own few seconds of quiet. One store failing never stops the others.
    /// </summary>
    private static void FlushStores(bool force)
    {
        foreach ((string name, Action flush) in new (string, Action)[]
        {
            ("tickets", () => Aion2Tickets.Flush(force)),
            ("achievements", () => Aion2Achievements.Flush(force)),
            ("supply", () => Aion2Supply.Flush(force)),
            ("inventories", () => Aion2Inventories.Flush(force)),
            ("loot", () => Aion2Loot.Flush()),
            ("gathering", () => Aion2GatherLog.Flush()),
            ("field bosses", () => Aion2FieldBosses.Flush()),
        })
        {
            try
            {
                flush();
            }
            catch (Exception ex)
            {
                AionDPS.CrashLog.Write("the " + name + " could not be saved", ex);
            }
        }
    }

    /// <summary>A minute of play with the character the game is read for: fighting (it hit something in the last minute), else idle when it did not move 3 m.</summary>
    private void JourneyMinute()
    {
        if (Headless || _liveSource is not Aion2PacketCombatSource { ServerFingerprint: not null } live
            || live.Entities is not Aion2EntityDirectory dir || dir.LocalCharacter is not { Name.Length: > 0 } me || me.ServerId <= 0 || !Aion2.Capture.GameConnections.GameRunning())
        {
            _journeyPlace = null;
            return;
        }

        bool fighting = dir.LocalHitTarget is { } hit && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(1);
        bool moved = false;
        if (dir.BestPosition is { } now)
        {
            moved = _journeyPlace is { } before && Math.Sqrt(((now.X - before.X) * (double)(now.X - before.X)) + ((now.Y - before.Y) * (double)(now.Y - before.Y))) > 300;
            _journeyPlace = (now.X, now.Y);
        }

        AionDPS.History.JourneyLog.Minute(AionDPS.History.JourneyLog.Key(me.Name, me.ServerId), fighting, moved, DateTime.Now);

        // the legion's member list of this login onto the Legion page, kept for when the game is closed (once the legion's name is known)
        if (dir.LegionRosterAt > _legionNoted && AionDPS.History.LegionStore.NoteLive(dir))
        {
            _legionNoted = dir.LegionRosterAt;
        }
    }

    private DateTime _legionNoted = DateTime.MinValue;

    protected override void OnClosed(EventArgs e)
    {
        AionDPS.History.JourneyLog.Save();
        _pollTimer.Stop();
        _updateTimer.Stop();
        if (!ReferenceEquals(_source, _liveSource))
        {
            _source?.Dispose();
        }

        _liveSource?.Dispose();
        _overlay?.Dispose();
        _meterOverlay?.Close();
        _hotkeys?.Dispose();
        _gateTimer.Stop();
        _timetable?.Close();
        _hotkeyOverlay?.Close();
        _overlayMenu?.Close();
        _worldBoss?.Dispose();
        _assist?.Dispose();
        _loot?.Dispose();
        _gatherOverlay?.Dispose();
        _seasonOverlay?.Dispose();
        _minimapReader?.Dispose();
        _craft?.Dispose();
        _storeFlush?.Dispose();
        _combat?.Dispose();
        _menuWatcher?.Dispose();
        _streamTimer.Stop();
        _streamServer?.Dispose();
        _network?.Dispose();
        _dummy?.Dispose();
        FlushStores(force: true);
        AionDPS.Upload.MapShare.Flush(); // the map accuracy checks still waiting (a few seconds at most)
        _timetableReminder?.Dispose();
        _petFarmController?.Dispose();
        _petFarm?.Close();
        _petMapController?.Dispose();
        _trayIcon?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// Rebuilds the grid from scratch against the current Mob/Boss filter rather than patching
    /// existing rows in place: with a target filter active, a source's presence in the grid
    /// itself depends on the filter (no damage to the selected target -> no row at all), so an
    /// incremental update would need to track and prune rows on every filter change anyway. Small
    /// event counts (a live meter's player list, not a bulk dataset) make the O(n) rescan cheap
    /// enough not to bother.
    /// </summary>
    private void RefreshRows()
    {
        // The index takes in what is new in every view (a few events a second): the target list and the heals are read from it
        // everywhere, and the views that ask nothing of a picked target or the scope take their damage side from it too.
        if (!ForceFullRefresh)
        {
            _index.Update(_aggregator, IsPlayerName);
        }

        // The index answers the damage side of every view that asks nothing of a picked target or of the compact overlay's Group/Corps
        // scope: Damage, PvP, Heal, Taken and All in One. The views of one target's fight and the scoped ones still rescan (a boss
        // fight's list follows rules that look at the whole stretch of events, and the scope at who fought what).
        bool damageIndexed = !ForceFullRefresh && _selectedTargetId is null && !(ScopeActive && ShownScope != MeterScope.All)
            && (!_pvpOnly || _index.TimeOrdered);

        RefreshMobBossFilterItems(!ForceFullRefresh);

        // !IsHeal here is the fix for a real bug found by terminal_windows against a real
        // heal-bearing session: without it, a pure healer ("Potion" -- a heal-effect name, not a
        // player) showed up as a damage source with a nonsensical "dmg (n/a DPS)" row, because
        // TargetIDps/AllDpsWallClock already filter heals out for the rate but nothing filtered
        // this event set for the totals or the row list itself. Same underlying issue as
        // LiveAggregator.Summarize -- see its remarks.
        var damageOnly = _aggregator.Events.Where(ev => !ev.IsHeal);

        // PVP DMG, per the user: damage against other PLAYERS only - mobs/bosses excluded
        // regardless of the Mob/Boss filter, which only ever lists NPC targets anyway (a specific
        // mob selection means nothing once mobs are out of the picture entirely).
        // A specific numbered run (both _selectedRunWindow* set - see MobBossTag's own remarks)
        // narrows further to that one run's own time span, not the target's whole history.
        Dictionary<int, bool> targetIsPlayer = new(); // the directory lookup takes a lock; once per target, not per hit
        List<DamageEvent> filtered = damageIndexed ? new List<DamageEvent>() : _pvpOnly
            ? damageOnly.Where(ev => targetIsPlayer.TryGetValue(ev.TargetObjectId, out bool known) ? known : targetIsPlayer[ev.TargetObjectId] = IsPlayerName(ev.TargetObjectId)).ToList()
            : _selectedTargetId is int targetId
                ? ShownFightHits(damageOnly, targetId)
                // The own-fight restriction belongs with the compact overlay's Group / Corps view; everywhere else the
                // players around one count too, whatever they are fighting.
                : ScopeActive && ShownScope != MeterScope.All
                    ? RestrictToEngagedTargets(damageOnly.ToList())
                    : damageOnly.ToList();

        if (_source?.Entities is Aion2.Aion2EntityDirectory rosterDirectory)
        {
            foreach (string member in rosterDirectory.PartyNames)
            {
                _groupNames.Add(member);
            }
        }

        _corpsIds = ScopeActive && ShownScope != MeterScope.All ? ComputeCorps(filtered) : new HashSet<int>();

        // A pure healer never hit the selected target, so `filtered` holds none of their events, yet
        // they belong in the list (and in an upload's roster). Heals inside the shown window by
        // players the meter has identified add them; bounded to that window so someone who healed
        // an hour ago elsewhere does not come back at a permanent 0.
        long shownCount = damageIndexed ? (_pvpOnly ? _index.PvpCount : _index.DamageCount) : filtered.Count;
        (DateTime, DateTime)? filteredSpan = shownCount == 0 ? null
            : damageIndexed ? (_pvpOnly ? (_index.PvpFirst, _index.PvpLast) : (_index.DamageFirst, _index.DamageLast))
            : (filtered.Min(ev => ev.Timestamp), filtered.Max(ev => ev.Timestamp));
        (int, DateTime, DateTime)? shownSummary = damageIndexed && filteredSpan is (DateTime summaryFrom, DateTime summaryTo)
            ? ((int)Math.Min(shownCount, int.MaxValue), summaryFrom, summaryTo)
            : null;

        if (_healMode && !_pvpOnly)
        {
            RefreshHealRows(filteredSpan);
            ClearHitStats();
            UpdateSupportBadges(filteredSpan);
            RankRows();
            UpdateHpCheck(OnSelectedTarget(filtered));
            UpdateCompactOverlay(filtered, shownSummary);
            return;
        }

        if (_takenMode && !_pvpOnly)
        {
            RefreshTakenRows(filteredSpan);
            ClearHitStats();
            UpdateSupportBadges(filteredSpan);
            RankRows();
            UpdateHpCheck(OnSelectedTarget(filtered));
            UpdateCompactOverlay(filtered, shownSummary);
            return;
        }

        var healSourceIds = filteredSpan is (DateTime spanStart, DateTime spanEnd)
            ? (damageIndexed ? _index.Heals : _aggregator.Events)
                .Where(ev => ev.IsHeal && ev.Timestamp >= spanStart && ev.Timestamp <= spanEnd && IsPlayerName(ev.SourceObjectId))
                .Select(ev => ev.SourceObjectId)
            : Enumerable.Empty<int>();

        // All in One: healing done and damage taken from monsters per player, over the same span as the
        // Heal and Taken modes use, so the three columns agree with those modes' totals.
        Dictionary<int, long> healingById = _allData
            ? HealsInSpan(filteredSpan).GroupBy(ev => ev.SourceObjectId).ToDictionary(g => g.Key, g => g.Sum(ev => ev.Amount))
            : new Dictionary<int, long>();
        Dictionary<int, long> hostileTakenById = _allData
            ? HostileHitsTaken(filteredSpan).GroupBy(ev => ev.TargetObjectId).ToDictionary(g => g.Key, g => g.Sum(ev => ev.Amount))
            : new Dictionary<int, long>();

        // The bar's green part (MeterBarParts): each player's healing on the others over the same span, not on themself (a self-heal helps nobody else).
        Dictionary<int, long> healOthersById = BarPartsOn
            ? HealsInSpan(filteredSpan).Where(ev => ev.TargetObjectId != ev.SourceObjectId).GroupBy(ev => ev.SourceObjectId).ToDictionary(g => g.Key, g => g.Sum(ev => ev.Amount))
            : new Dictionary<int, long>();
        double healSeconds = filteredSpan is (DateTime healFrom, DateTime healTo) && healTo > healFrom ? (healTo - healFrom).TotalSeconds : 0;

        // All in One: what the group shields absorbed from monsters, beside the damage taken (blue part of it).
        Dictionary<int, long> absorbedById = _allData
            ? _aggregator.Shields
                .Where(ev => ShieldSkills.IsAbsorb(ev) && IsPlayerName(ev.TargetObjectId) && !IsPlayerName(ev.SourceObjectId)
                    && (filteredSpan is not (DateTime from, DateTime to) || (ev.Timestamp >= from && ev.Timestamp <= to)))
                .GroupBy(ev => ev.TargetObjectId).ToDictionary(g => g.Key, g => g.Sum(ev => ev.Amount))
            : new Dictionary<int, long>();

        var sourceIds = (damageIndexed ? (_pvpOnly ? _index.PvpBySource.Keys : _index.BySource.Keys) : filtered.Select(ev => ev.SourceObjectId).Distinct())
            .Union(healSourceIds)
            .Union(hostileTakenById.Keys)
            .Where(IsPlayerName)
            .ToList();

        // Damage RECEIVED per row (PlayerRow.DamageTaken): everything that hit them inside the
        // shown window - from the selected target only when one is picked, so the figure answers
        // "how much did this boss put on whom", the same scope BuildEncounterUpload's damageTaken
        // uses for the website's distribution chart.
        IEnumerable<DamageEvent> takenWindow = damageIndexed ? Enumerable.Empty<DamageEvent>()
            : filteredSpan is (DateTime takenStart, DateTime takenEnd)
                ? damageOnly.Where(ev => ev.Timestamp >= takenStart && ev.Timestamp <= takenEnd)
                : Enumerable.Empty<DamageEvent>();
        Dictionary<int, long> damageTakenById = takenWindow
            .Where(ev => _pvpOnly || _selectedTargetId is not int selectedAttacker || ev.SourceObjectId == selectedAttacker)
            .GroupBy(ev => ev.TargetObjectId)
            .ToDictionary(g => g.Key, g => g.Sum(ev => ev.Amount));
        long TakenOf(int id) => !damageIndexed ? damageTakenById.GetValueOrDefault(id)
            : filteredSpan is not (DateTime from, DateTime to) ? 0
            : _pvpOnly ? _index.TakenBetween(id, from, to)
            : _index.TakenByTarget.GetValueOrDefault(id);

                // ClassFilter, per the user: was purely decorative until other players' classes started
        // being detected at all (see ResolveClassName) -- now that a class can actually be known
        // for someone besides "You", picking one filters the grid down to it for real. An empty
        // result when nobody of that class is currently present is correct, not a bug.
        if (_selectedClassFilter is string classFilter)
        {
            sourceIds = sourceIds.Where(id => ResolveClassName(id) == classFilter).ToList();
        }

        sourceIds = sourceIds.Where(IsInScope).ToList();

        // A passer-by the meter knows by no name, with one single hit seen, is no row: in the open world every stranger
        // fighting within sight would add a "Player #id" line. The own character and the group stay whatever they did.
        if (_source?.Entities is Aion2.Aion2EntityDirectory strangerDir && !_historyMode)
        {
            Dictionary<int, int> hitsBySource = damageIndexed
                ? (_pvpOnly ? _index.PvpBySource : _index.BySource).ToDictionary(kv => kv.Key, kv => kv.Value.Hits)
                : filtered.GroupBy(ev => ev.SourceObjectId).ToDictionary(g => g.Key, g => g.Count());
            sourceIds = sourceIds.Where(id => id == strangerDir.LocalPlayerId
                || hitsBySource.GetValueOrDefault(id) > 1
                || strangerDir.NameFor(id) is not string stranger
                || !stranger.StartsWith("Player #", StringComparison.Ordinal)).ToList();
        }

        foreach (int staleId in _rowsByObjectId.Keys.Except(sourceIds).ToList())
        {
            _rows.Remove(_rowsByObjectId[staleId]);
            _rowsByObjectId.Remove(staleId);
        }

        // One pass over the events instead of one per player: in a raid (24 players, hundreds of thousands of
        // hits) the per-player rescans, one of them sorting, froze the meter once a second.
        Dictionary<int, long> damageById = new();
        Dictionary<int, long> estimatedById = new();
        Dictionary<int, HitStats> hitsById = new(); // how the hits landed: the meter's optional columns (crit, back, front …)
        foreach (DamageEvent hit in filtered)
        {
            damageById[hit.SourceObjectId] = damageById.GetValueOrDefault(hit.SourceObjectId) + hit.Amount;
            System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(hitsById, hit.SourceObjectId, out _).Add(hit);
            if (hit.Estimated)
            {
                estimatedById[hit.SourceObjectId] = estimatedById.GetValueOrDefault(hit.SourceObjectId) + hit.Amount;
            }
        }

        Dictionary<int, List<DamageEvent>>? allBySource = null;
        if (!damageIndexed && !_pvpOnly && _selectedTargetId is null)
        {
            allBySource = new();
            foreach (DamageEvent hit in _aggregator.Events)
            {
                if (!allBySource.TryGetValue(hit.SourceObjectId, out var list))
                {
                    allBySource[hit.SourceObjectId] = list = new List<DamageEvent>();
                }

                list.Add(hit);
            }
        }

        Dictionary<int, List<DamageEvent>>? pvpBySource = null;
        if (!damageIndexed && _pvpOnly)
        {
            pvpBySource = filtered.GroupBy(ev => ev.SourceObjectId).ToDictionary(g => g.Key, g => g.ToList());
        }

                // A picked target's DPS is each source's damage over the time of the hits on that target - one span for all rows, so it is
        // worked out once here and not by a pass over all the hits for every row.
        double targetSeconds = _selectedTargetId is int spanTarget && !damageIndexed && !_pvpOnly ? BossFight.SecondsOnTarget(filtered, spanTarget) : 0;

        // Rows are added before the values are set: the sorted view cannot be held back while the list itself changes.
        foreach (int sourceId in sourceIds)
        {
            if (!_rowsByObjectId.ContainsKey(sourceId))
            {
                var added = new PlayerRow(sourceId);
                _rowsByObjectId[sourceId] = added;
                _rows.Add(added);
            }
        }

        // Every row's damage changes in one go; with live sorting on, each change moved the row on its own (2000 rows
        // meant 2000 moves in a list of 2000, every second). Held back, the list is sorted once, when this block ends.
        using (_playersView?.DeferRefresh())
        foreach (int sourceId in sourceIds)
        {
            PlayerRow row = _rowsByObjectId[sourceId];

            // Applied every refresh, not just at creation: an active-character switch detected by
            // UpdateActiveCharacterFromSkill (or a newly detected class from UpdateOtherPlayerClass)
            // must update an already-existing row immediately, not just rows created after the fact.
            ApplyIdentity(row, sourceId);

            row.Damage = damageIndexed ? (_pvpOnly ? _index.PvpBySource : _index.BySource).GetValueOrDefault(sourceId)?.Sum ?? 0 : damageById.GetValueOrDefault(sourceId);
            row.IsEstimated = damageIndexed
                ? (_pvpOnly ? _index.PvpBySource : _index.BySource).GetValueOrDefault(sourceId) is { } indexed && indexed.EstimatedSum * 2 > indexed.Sum
                : estimatedById.GetValueOrDefault(sourceId) * 2 > row.Damage;
            row.Hits = damageIndexed ? (_pvpOnly ? _index.PvpBySource : _index.BySource).GetValueOrDefault(sourceId)?.Kinds ?? default : hitsById.GetValueOrDefault(sourceId);
            // PVP mode's rate must come from the SAME PVP-only event set row.Damage above was
            // just summed from, not the unfiltered full history - same reasoning as the
            // Mob/Boss-filtered iDPS case below (see BuildEncounterUpload's own remarks on the
            // totalDamage/idps-must-share-one-source-of-truth bug this pattern once caused).
            // `filtered`, not `_aggregator.Events`, in the targeted branch too: a specific
            // numbered run's iDPS must come from that same run's own window, the same
            // one-source-of-truth reasoning the PVP branch above already follows (see
            // BuildEncounterUpload's own remarks on the totalDamage/idps bug this once caused).
            row.Dps = damageIndexed ? SharedRate(row.Damage, filteredSpan)
                : _pvpOnly
                ? DpsCalculator.AllDpsWallClock(pvpBySource!.GetValueOrDefault(sourceId) ?? new List<DamageEvent>(), sourceId)
                : _selectedTargetId is not null
                    ? (targetSeconds > 0 ? damageById.GetValueOrDefault(sourceId) / targetSeconds : null)
                    : DpsCalculator.AllDpsWallClock(allBySource!.GetValueOrDefault(sourceId) ?? new List<DamageEvent>(), sourceId);
            row.AllMode = _allMode;
            row.AllCompact = _allCompactMode;
            row.Healing = healingById.GetValueOrDefault(sourceId);
            row.HealOthers = healOthersById.GetValueOrDefault(sourceId);
            row.HealOthersRate = healSeconds > 0 && row.HealOthers > 0 ? row.HealOthers / healSeconds : null;
            row.Absorbed = absorbedById.GetValueOrDefault(sourceId);
            row.DamageTaken = _allData ? hostileTakenById.GetValueOrDefault(sourceId) : TakenOf(sourceId);
            ShowDeaths(row, sourceId, filteredSpan);
            row.ShowShareBar = _showShareBars;
            row.ShowDamageTaken = _showDamageTaken;

            row.Faction = (_source?.Entities as Aion2.Aion2EntityDirectory)?.FactionOf(sourceId) ?? "";
        }

        UpdateRaidDps(filteredSpan);
        UpdatePartyBuffs();
        UpdateSupportBadges(filteredSpan);
                // Rank and share are relative to what is on screen, so they are settled once every row's
        // damage for this refresh is known - and by damage, not by the grid's current sort order.
        RankRows();
        UpdateHpCheck(OnSelectedTarget(filtered));
        UpdateCompactOverlay(filtered, shownSummary);
    }

    /// <summary>
    /// The hits shown for the selected target and run: for a boss, the hits on it from the first to the last blow on it, not its adds nor the monsters
    /// around it (see <see cref="BossFight.ShownHits"/>); for anything else, that target alone.
    /// </summary>
    private List<DamageEvent> ShownFightHits(IEnumerable<DamageEvent> damage, int targetId)
    {
        var directory = _source?.Entities as Aion2.Aion2EntityDirectory;
        return BossFight.ShownHits(damage, targetId, _selectedRunWindowStart, _selectedRunWindowEnd, id => directory?.BossNpcIdOf(id) is not null);
    }

    /// <summary>The shown hits that landed on the selected target itself (the boss without its
    /// adds) - what its hit points are held against.</summary>
    private IReadOnlyList<DamageEvent> OnSelectedTarget(IReadOnlyList<DamageEvent> shown) =>
        _selectedTargetId is int id ? shown.Where(ev => ev.TargetObjectId == id).ToList() : shown;

    /// <summary>
    /// Whose rows the scope shows. Group: the local player and the players the party roster names,
    /// nobody else - no stranger around in the open world, named or not yet; before the first
    /// roster frame (a few seconds after joining) that is the local player alone. Corps: the group
    /// and the players who fought the same monsters as it (two parties joined into a corps share
    /// their fight; the roster only names one's own party). All: everybody. PvP shows everyone: the
    /// opponents are the point there.
    /// </summary>
    private bool IsInScope(int sourceId)
    {
        if (!ScopeActive || ShownScope == MeterScope.All || _pvpOnly || _source?.Entities is not Aion2.Aion2EntityDirectory directory
            || directory.IsLocalPlayer(sourceId) || directory.InferLocalPlayer() == sourceId)
        {
            return true;
        }

        if (ShownScope == MeterScope.Me)
        {
            return false; // a dummy parse: the own character alone
        }

        // No roster seen (the meter started after the group formed; the roster is sent once): the
        // group is unknown, and "only me" would be wrong - until it is read, the group is whoever
        // fights the same monsters.
        bool groupKnown = RosterKnown(directory);
        if (ShownScope == MeterScope.Group && !groupKnown)
        {
            return _corpsIds.Contains(sourceId);
        }

        return IsGroupMember(sourceId, directory) || (ShownScope == MeterScope.Corps && _corpsIds.Contains(sourceId));
    }

    /// <summary>Whether a party roster naming anyone besides the local player has been read.</summary>
    private bool RosterKnown(Aion2.Aion2EntityDirectory directory)
    {
        string? own = directory.LocalCharacter?.Name;
        return _groupNames.Any(n => n != own) || directory.PartyNames.Any(n => n != own);
    }

    private bool IsGroupMember(int id, Aion2.Aion2EntityDirectory directory)
    {
        if (directory.IsLocalPlayer(id) || directory.InferLocalPlayer() == id)
        {
            return true;
        }

        string name = ResolveDisplayName(id);
        return _groupNames.Contains(name) || directory.PartyNames.Contains(name) || name == directory.LocalCharacter?.Name;
    }

    /// <summary>The players who hit a monster the group also hit (an approximation of the corps until
    /// its own roster is read from the network).</summary>
    private HashSet<int> ComputeCorps(IReadOnlyList<DamageEvent> shown)
    {
        var corps = new HashSet<int>();
        if (_source?.Entities is not Aion2.Aion2EntityDirectory directory)
        {
            return corps;
        }

        var groupTargets = shown.Where(ev => IsGroupMember(ev.SourceObjectId, directory)).Select(ev => ev.TargetObjectId).ToHashSet();
        foreach (DamageEvent ev in shown)
        {
            if (groupTargets.Contains(ev.TargetObjectId) && IsPlayerName(ev.SourceObjectId))
            {
                corps.Add(ev.SourceObjectId);
            }
        }

        return corps;
    }

    private void SetScope(MeterScope scope)
    {
        // in an instance or at a dummy the click holds for this place only; in the open world it is the saved choice
        if (_autoScope && _scopeContext != ScopeContext.World)
        {
            _scopeManual = scope;
            UpdateScopeControls();
            RefreshRows();
            return;
        }

        if (scope == _scope)
        {
            UpdateScopeControls();
            return;
        }

        _scope = scope;
        var settings = MeterSettings.Load();
        settings.ViewScope = scope.ToString();
        settings.Save();
        UpdateScopeControls();
        RefreshRows();
    }

    private void OnOverlayScopeClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: "Stagger" })
        {
            SetStaggerView(!_overlayStagger); // not a scope: the Stagger view in place of the lines (MainWindow.Stagger.cs); clicked again, the lines
        }
        else if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out MeterScope scope))
        {
            if (_overlayStagger)
            {
                SetStaggerView(false);
            }

            SetScope(scope);
        }

        e.Handled = true;
    }

    /// <summary>Marks the chosen scope on the compact overlay's three-way switch.</summary>
    private void UpdateScopeControls()
    {
        ClientScopeBox.Visibility = MeterOverlayActive ? Visibility.Visible : Visibility.Collapsed;
        foreach (Border segment in OverlayScopeSwitch.Children.OfType<Border>().Concat(ClientScopeSwitch.Children.OfType<Border>()))
        {
            bool selected = _overlayStagger && segment.Parent == OverlayScopeSwitch ? segment.Tag as string == "Stagger" : segment.Tag as string == ShownScope.ToString();
            // the chosen one: a gold-tinted pill with gold text; the others quiet words (the site's switches)
            segment.Background = selected ? ScopeOnBrush : Brushes.Transparent;
            if (segment.Child is TextBlock text)
            {
                if (selected)
                {
                    text.Foreground = ScopeOnText;
                }
                else
                {
                    text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
                }
            }
        }
    }

    /// <summary>A target's heals on itself between two moments - a boss healing itself by the hits it
    /// takes (see Aion2FrameDecoder.DecodeVarintDot), which the HP check has to know about.</summary>
    /// <summary>The HP check for the boss's latest fight: the damage since the last pause of half a minute, against the hit points it lost.</summary>
    private HpCheckResult? BossHpCheck(int bossId)
    {
        if (_source?.Entities is not Aion2.Aion2EntityDirectory directory)
        {
            return null;
        }

        var run = new List<DamageEvent>();
        DateTime? earlier = null;
        foreach (DamageEvent ev in _aggregator.Events.Where(e => !e.IsHeal && e.TargetObjectId == bossId).OrderByDescending(e => e.Timestamp))
        {
            if (earlier is DateTime e2 && (e2 - ev.Timestamp).TotalSeconds > 30)
            {
                break;
            }

            run.Add(ev);
            earlier = ev.Timestamp;
        }

        if (run.Count == 0)
        {
            return null;
        }

        DateTime start = run.Min(h => h.Timestamp), end = run.Max(h => h.Timestamp);
        var readings = directory.HitPoints.SamplesAround(bossId, start, end).Select(s => (s.At, s.Hp)).ToList();
        return readings.Count == 0 ? null : HpCheck.Evaluate(readings, run.Concat(OwnHeals(bossId, start, end)).ToList(), directory.HitPoints.HighestSeen(bossId) ?? 0);
    }

    private IEnumerable<DamageEvent> OwnHeals(int targetId, DateTime start, DateTime end) =>
        _aggregator.Events.Where(ev => ev.IsHeal && ev.SourceObjectId == targetId && ev.TargetObjectId == targetId
            && ev.Timestamp >= start && ev.Timestamp <= end);

    /// <summary>
    /// The status row's guard against wrong totals: for the selected target (one run of it), the
    /// damage counted held against the hit points the server says it lost - see
    /// <see cref="HpCheck"/>. Shown only where the source reports hit points (Aion 2) and a target
    /// is selected; a mismatch is spelled out in the warning colour rather than left for the
    /// numbers above it to be trusted.
    /// </summary>
    private void UpdateHpCheck(IReadOnlyList<DamageEvent> targetHits)
    {
        HpCheckResult? check = null;
        if (!_pvpOnly && _selectedTargetId is int targetId && targetHits.Count > 0
            && _source?.Entities is Aion2.Aion2EntityDirectory directory && !directory.IsKnownPlayer(targetId))
        {
            DateTime start = targetHits.Min(h => h.Timestamp);
            DateTime end = targetHits.Max(h => h.Timestamp);
            var readings = directory.HitPoints.SamplesAround(targetId, start, end).Select(s => (s.At, s.Hp)).ToList();
            check = HpCheck.Evaluate(readings, targetHits.Concat(OwnHeals(targetId, start, end)).ToList(), directory.HitPoints.HighestSeen(targetId) ?? 0);
        }

        _lastHpCheck = check;
        if (check is null)
        {
            HpCheckText.Visibility = Visibility.Collapsed;
            return;
        }

        var loc = LocalizationManager.Instance;
        string percent = check.Ratio.ToString("P1", CultureInfo.CurrentCulture);
        bool warn = check.OverFullHealth || check.Verdict != HpCheckVerdict.Match;
        HpCheckText.Text = check.OverFullHealth
            ? string.Format(loc["Main.HpCheck.OverFull"], check.RunTotal.ToString("N0"), check.Highest.ToString("N0"))
            : check.Verdict switch
            {
                HpCheckVerdict.Missing => string.Format(loc["Main.HpCheck.Missing"], percent, check.Lost.ToString("N0")),
                HpCheckVerdict.Excess => string.Format(loc["Main.HpCheck.Excess"], percent, check.Lost.ToString("N0")),
                _ => string.Format(loc["Main.HpCheck.Match"], percent, check.Lost.ToString("N0")),
            };
        HpCheckText.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Brush.Warning" : "Brush.TextMuted");
        HpCheckText.FontWeight = warn ? FontWeights.Bold : FontWeights.Normal;
        HpCheckText.Visibility = Visibility.Visible;
    }

    /// <summary>The most lines the meter over the game shows (6 November 2026, the owner: "cap the max users on meter to 10, so in mass boss fights, its 10
    /// only"); the window's list keeps everyone.</summary>
    private const int MaxOverlayLines = 10;

    /// <summary>The lines the meter shows when there are more than <see cref="MaxOverlayLines"/>; null: all of them.</summary>
    private HashSet<PlayerRow>? _overlayLines;

    /// <summary>The best ten by what the list is sorted by; the player's own line takes the tenth place when they are further down, with their real rank.</summary>
    private void PickOverlayLines()
    {
        if (_rows.Count <= MaxOverlayLines)
        {
            _overlayLines = null;
            return;
        }

        List<PlayerRow> ordered = _rows.OrderByDescending(r => r.SortKey).ToList();
        List<PlayerRow> shown = ordered.Take(MaxOverlayLines).ToList();
        if (_source?.Entities is { } entities && !shown.Any(r => entities.IsLocalPlayer(r.ObjectId))
            && ordered.FirstOrDefault(r => entities.IsLocalPlayer(r.ObjectId)) is { } me)
        {
            shown[^1] = me;
        }

        _overlayLines = shown.ToHashSet();
    }

    private void RankRows()
    {
        PickOverlayLines();
        // Live sorting alone left the list in the previous mode's order after a switch (PVE -> PVP kept the
        // PVE ranking); sorting explicitly here covers every mode, since each refresh ends in RankRows.
        _playersView?.Refresh();
        _overlayView?.Refresh();
        if (_rows.Count > 0 && _rows[0].RdpsView)
        {
            // the rDPS view: rank, share and bar by rDPS (what each player made possible)
            double rdpsTotal = _rows.Sum(r => r.Rdps ?? r.Dps ?? 0), rdpsMax = _rows.Max(r => r.Rdps ?? r.Dps ?? 0);
            int place = 0;
            foreach (PlayerRow row in _rows.OrderByDescending(r => r.SortKey))
            {
                double value = row.Rdps ?? row.Dps ?? 0;
                row.Rank = ++place;
                row.SharePercent = rdpsTotal > 0 ? 100.0 * value / rdpsTotal : 0;
                row.FillPercent = rdpsMax > 0 ? 100.0 * value / rdpsMax : 0;
                row.AbsorbedFillPercent = 0;
            }

            // the bar in parts: nDPS, the buffs given (to rDPS), the healing on the others; or rDPS alone as before
            MeterBarParts.Apply(_rows, rdpsView: true, BarPartsOn, _rdpsSeconds);
            UpdateBarLegend();
            return;
        }

        long shownTotal = _rows.Sum(r => r.Damage);
        // In Taken mode the bar is drawn to the damage that got through, behind it a blue one to the absorbed
        // damage on top; both scale to the biggest of the two together.
        long shownMax = _rows.Count > 0 ? _rows.Max(r => r.Damage + (_takenMode ? r.Absorbed : 0)) : 0;
        int rank = 0;
        foreach (PlayerRow row in _rows.OrderByDescending(r => r.Damage))
        {
            row.Rank = ++rank;
            row.SharePercent = shownTotal > 0 ? 100.0 * row.Damage / shownTotal : 0;
            row.FillPercent = shownMax > 0 ? 100.0 * row.Damage / shownMax : 0;
            row.AbsorbedFillPercent = _takenMode && shownMax > 0 && row.Absorbed > 0 ? 100.0 * (row.Damage + row.Absorbed) / shownMax : 0;
        }

        if (BarPartsOn)
        {
            // the damage, the damage the buffs gave the others, the healing on the others, against the longest of those bars
            MeterBarParts.Apply(_rows, rdpsView: false, on: true, _rdpsSeconds);
        }
        else
        {
            foreach (PlayerRow row in _rows)
            {
                MeterBarParts.Clear(row);
            }
        }

        UpdateBarLegend();
    }

    /// <summary>The bars are drawn in parts (MeterBarParts): in the damage views (DPS, rDPS, All in One) outside PvP, when the look has them (Meter look).</summary>
    private bool BarPartsOn => _meterLook.BarParts && !_pvpOnly && _mode is MeterMode.Damage or MeterMode.Raid or MeterMode.All or MeterMode.AllCompact;

    /// <summary>The span the last rDPS was worked out over: the given part of a damage bar is the buffs' rate over it.</summary>
    private double _rdpsSeconds;

    private string _barLegendKey = "";

    /// <summary>The pictures of the build: a meter the player made narrower, its lines ranked (and capped) as a fight's are.</summary>
    internal void ShowSampleCrowd(double width)
    {
        SetCompactOverlaySize(width, 0);
        RankRows();
    }

    /// <summary>The pictures of the build: the sample lines' bars in parts, as RankRows draws them, and the line naming the colours.</summary>
    internal void ShowSampleBarParts(bool rdpsView, double seconds)
    {
        _rdpsSeconds = seconds;
        MeterBarParts.Apply(_rows, rdpsView, BarPartsOn, seconds);
        UpdateBarLegend();
    }

    /// <summary>The line under the meter's lines that names the bar's colours, while some line has a grey or green part.</summary>
    internal void UpdateBarLegend()
    {
        bool show = BarPartsOn && CompactOverlayRowsArea.Visibility == Visibility.Visible && _rows.Any(r => r.HealFillPercent - r.FillPercent >= 0.3);
        OverlayBarLegend.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        bool rdps = _rows.Count > 0 && _rows[0].RdpsView;
        string key = _meterLook.Key + "|" + rdps;
        if (key != _barLegendKey)
        {
            _barLegendKey = key;
            OverlayBarLegend.Content = MeterBarParts.Legend(_meterLook, rdps);
        }
    }

    private DateTime _rdpsAt = DateTime.MinValue;
    private DateTime _partyBuffsAt = DateTime.MinValue;
    private bool _showPartyBuffs = true;

    /// <summary>Once a second: the party buffs running on each shown player now (Settings: show party buffs), as a line under the name.</summary>
    private void UpdatePartyBuffs()
    {
        if (DateTime.UtcNow - _partyBuffsAt < TimeSpan.FromSeconds(1) && !Headless)
        {
            return;
        }

        _partyBuffsAt = DateTime.UtcNow;
        bool show = _showPartyBuffs && ViewingLive && _source?.Entities is Aion2.Aion2EntityDirectory;
        var directory = _source?.Entities as Aion2.Aion2EntityDirectory;
        DateTime now = DateTime.Now;
        foreach (PlayerRow row in _rows)
        {
            if (!show || row.IsEnemy || directory is null)
            {
                row.BuffLine = "";
                continue;
            }

            var names = directory.Combat.BuffsOn(row.ObjectId, now)
                .Select(b => (Base: RaidDps.DisplayBase(b.SkillId), b.SkillId))
                .Where(b => RaidDps.IsSynergy(b.Base))
                .GroupBy(b => b.Base)
                .Select(g => Aion2.Protocol.Aion2SkillNames.Display(Aion2.Protocol.Aion2SkillNames.NameOf(g.First().SkillId)))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .ToList();
            row.BuffLine = string.Join(" · ", names);
        }
    }

    /// <summary>
    /// The rDPS view (Combat/RaidDps): every shown player's rDPS over the fight on screen, hit by hit from the effects on them and on the boss.
    /// Worked out at most every 1.5 s (the buff log is sliced and walked), so a long fight costs nothing between two updates; the rows keep the last
    /// values meanwhile.
    /// </summary>
    private void UpdateRaidDps((DateTime From, DateTime To)? span)
    {
        bool on = _mode == MeterMode.Raid && !_pvpOnly;
        foreach (PlayerRow row in _rows)
        {
            row.RdpsView = on;
        }

        // the meter's rDPS / nDPS / given columns (Meter look) want the same figures in every damage view
        bool wanted = on || (!_pvpOnly && (_meterLook.Has("rdps") || _meterLook.Has("ndps") || _meterLook.Has("given") || BarPartsOn));
        if (!wanted || span is not (DateTime from, DateTime to) || to <= from || _source?.Entities is not Aion2.Aion2EntityDirectory directory)
        {
            return;
        }

        if (!Headless && DateTime.UtcNow - _rdpsAt < TimeSpan.FromSeconds(1.5))
        {
            return;
        }

        _rdpsAt = DateTime.UtcNow;
        try
        {
            var players = _rows.Where(r => !r.IsEnemy).ToList();
            var ids = players.Select(r => r.ObjectId).ToHashSet();
            int? bossId = _selectedTargetId is int target && !directory.IsKnownPlayer(target) ? target : null;
            var (onPlayers, onBoss) = RaidDps.BuffsIn(directory.Combat.BuffsAround(from, to), ids, bossId, from, to);

            // every player's direct hits of the fight, for rDPS hit by hit (milliseconds from the window's start, as the effects' spans): in a boss's
            // fight the hits on the boss, as its DPS counts them; damage-over-time ticks take no part in the split
            var hitsBy = new Dictionary<int, List<(long, long)>>();
            var bossTargets = new Dictionary<int, bool>();
            bool OnFight(int hitTarget) => bossId is null ? !ids.Contains(hitTarget)
                : hitTarget == bossId || (bossTargets.TryGetValue(hitTarget, out bool isBoss) ? isBoss : bossTargets[hitTarget] = directory.BossNpcIdOf(hitTarget) is not null);
            foreach (DamageEvent ev in _aggregator.Events)
            {
                if (!ev.IsHeal && !ev.IsTick && ev.Amount > 0 && ev.Timestamp >= from && ev.Timestamp <= to && ids.Contains(ev.SourceObjectId) && OnFight(ev.TargetObjectId))
                {
                    (hitsBy.TryGetValue(ev.SourceObjectId, out var l) ? l : hitsBy[ev.SourceObjectId] = new List<(long, long)>()).Add(((long)(ev.Timestamp - from).TotalMilliseconds, ev.Amount));
                }
            }

            var participants = players.Select(r => new RaidParticipant(r.ObjectId, r.Dps ?? 0,
                onPlayers.TryGetValue(r.ObjectId, out var buffs) ? buffs : new List<RaidBuff>(), hitsBy.GetValueOrDefault(r.ObjectId))).ToList();
            Dictionary<int, RaidDpsResult> results = RaidDps.Compute(participants, onBoss, (to - from).TotalSeconds);
            _rdpsSeconds = (to - from).TotalSeconds;
            // Nobody in the list buffed anyone (open world, a solo run, a group without a Chanter or Cleric): rDPS and nDPS would only repeat DPS,
            // which looked like a fault ("rDPS shows the same as DPS"). The columns then show "–" (their tips say why); the rDPS view shows each
            // player's DPS, which is their rDPS then.
            bool shared = players.Any(row => results.TryGetValue(row.ObjectId, out RaidDpsResult x) && (x.Given >= 0.5 || Math.Abs(x.Ndps - (row.Dps ?? 0)) >= 0.5));
            foreach (PlayerRow row in players)
            {
                if (!shared)
                {
                    row.Rdps = null;
                    row.Ndps = null;
                    row.Given = null;
                    continue;
                }

                bool known = results.TryGetValue(row.ObjectId, out RaidDpsResult r);
                row.Rdps = known ? r.Rdps : row.Dps;
                row.Ndps = known ? r.Ndps : row.Dps;
                row.Given = known ? r.Given : null;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // the logs changed while being read: the next refresh tries again
            _rdpsAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Heal mode: one row per healer with the healing done over the fight on screen (the shown
    /// damage's time span) and HPS over that span. Heals on summoned spirits are left out: a
    /// spirit's spawn arrives as a 100,000 "heal" on itself, and topping up a spirit is not healing
    /// the group.
    /// </summary>
    private void RefreshHealRows((DateTime Start, DateTime End)? span)
    {
        var directory = _source?.Entities as Aion2.Aion2EntityDirectory;
        var heals = HealsInSpan(span);
        double? spanSeconds = span is (DateTime a, DateTime b) && b > a ? (b - a).TotalSeconds : null;

        var sourceIds = heals.Select(ev => ev.SourceObjectId).Distinct().ToList();
        if (_selectedClassFilter is string classFilter)
        {
            sourceIds = sourceIds.Where(id => ResolveClassName(id) == classFilter).ToList();
        }

        sourceIds = sourceIds.Where(IsInScope).ToList();

        foreach (int staleId in _rowsByObjectId.Keys.Except(sourceIds).ToList())
        {
            _rows.Remove(_rowsByObjectId[staleId]);
            _rowsByObjectId.Remove(staleId);
        }

        foreach (int sourceId in sourceIds)
        {
            if (!_rowsByObjectId.TryGetValue(sourceId, out var row))
            {
                row = new PlayerRow(sourceId);
                _rowsByObjectId[sourceId] = row;
                _rows.Add(row);
            }

            ApplyIdentity(row, sourceId);
            var mine = heals.Where(ev => ev.SourceObjectId == sourceId).ToList();
            row.IsEstimated = false;
            row.Damage = mine.Sum(ev => ev.Amount);
            double seconds = spanSeconds ?? (mine.Max(ev => ev.Timestamp) - mine.Min(ev => ev.Timestamp)).TotalSeconds;
            row.Dps = seconds > 0 ? row.Damage / seconds : null;
            row.DamageTaken = 0;
            row.Absorbed = 0;
            row.AllMode = false;
            row.AllCompact = false;
            row.Healing = 0;
            row.HealOthers = 0;
            row.HealOthersRate = null;
            ShowDeaths(row, sourceId, span);
            row.ShowShareBar = _showShareBars;
            row.ShowDamageTaken = false;
            row.Faction = directory?.FactionOf(sourceId) ?? "";
        }
    }

    /// <summary>
    /// Deaths after a player's name in the damage and healing modes (MeterSettings.ShowDeaths): how often their hit points reached 0 in the fight shown
    /// (Combat/Deaths.cs, the same as the damage-taken mode counts), and while they are still down how long - up to the fight's last hit, so a fight
    /// looked at later shows how long they lay there.
    /// </summary>
    private void ShowDeaths(PlayerRow row, int playerId, (DateTime Start, DateTime End)? span)
    {
        if (!_showDeaths || span is not (DateTime from, DateTime to) || _source?.Entities is not Aion2.Aion2EntityDirectory directory)
        {
            row.Deaths = 0;
            row.DeadFor = null;
            return;
        }

        var readings = directory.HitPoints.SamplesAround(playerId, from, to + Deaths.BlowWindow).Select(s => (s.At, s.Hp)).ToList();
        var deaths = Deaths.Find(playerId, readings, Array.Empty<DamageEvent>());
        row.Deaths = deaths.Count;
        row.DeadFor = deaths.Count > 0 && deaths[^1].RevivedAt is null ? deaths[^1].DeadFor(to) : null;
    }

    /// <summary>
    /// A player's DPS over the window the list shows - the fight, from its first hit to its last - the same for everyone (4 November 2026: each
    /// player's own first-to-last hit made a Chanter who buffed for five seconds before his first hit 7 % faster than he was, and his rDPS with it;
    /// the fight's window is what the fight report, rDPS and the other meters divide by).
    /// </summary>
    private static double? SharedRate(long damage, (DateTime From, DateTime To)? span)
    {
        if (span is not (DateTime from, DateTime to))
        {
            return null;
        }

        double seconds = (to - from).TotalSeconds;
        return seconds > 0 ? damage / seconds : null;
    }

    /// <summary>The heals by players inside a span, without those on summoned spirits (see <see cref="RefreshHealRows"/>).</summary>
    private List<DamageEvent> HealsInSpan((DateTime Start, DateTime End)? span)
    {
        var directory = _source?.Entities as Aion2.Aion2EntityDirectory;
        // The index keeps the heals apart (RefreshRows has just taken in the new events): the damage hits, most of the list, are not walked.
        return (ForceFullRefresh ? _aggregator.Events : _index.Heals)
            .Where(ev => ev.IsHeal && IsPlayerName(ev.SourceObjectId)
                && directory?.SummonOwnerOf(ev.TargetObjectId) is null
                && (span is not (DateTime from, DateTime to) || (ev.Timestamp >= from && ev.Timestamp <= to)))
            .ToList();
    }

    /// <summary>The hostile hits players took inside a span: from a monster, not from a player (a
    /// Sorcerer's Absorb Essence costs their own hit points; PvP has its own mode).</summary>
    private List<DamageEvent> HostileHitsTaken((DateTime Start, DateTime End)? span) =>
        _aggregator.Events
            .Where(ev => !ev.IsHeal && IsPlayerName(ev.TargetObjectId) && !IsPlayerName(ev.SourceObjectId)
                && (span is not (DateTime from, DateTime to) || (ev.Timestamp >= from && ev.Timestamp <= to)))
            .ToList();

    /// <summary>
    /// Taken mode: one row per player with the damage monsters dealt them over the fight on screen,
    /// per second of it, and their deaths there (see <see cref="Deaths"/>).
    /// </summary>
    private void RefreshTakenRows((DateTime Start, DateTime End)? span)
    {
        var directory = _source?.Entities as Aion2.Aion2EntityDirectory;
        var hits = HostileHitsTaken(span);
        // What the group shields absorbed from monsters in that span (see ShieldSkills.IsAbsorb).
        var absorbs = _aggregator.Shields
            .Where(ev => ShieldSkills.IsAbsorb(ev) && IsPlayerName(ev.TargetObjectId) && !IsPlayerName(ev.SourceObjectId)
                && (span is not (DateTime from, DateTime to) || (ev.Timestamp >= from && ev.Timestamp <= to)))
            .ToList();
        double? spanSeconds = span is (DateTime a, DateTime b) && b > a ? (b - a).TotalSeconds : null;

        var targetIds = hits.Select(ev => ev.TargetObjectId).Distinct().ToList();
        if (_selectedClassFilter is string classFilter)
        {
            targetIds = targetIds.Where(id => ResolveClassName(id) == classFilter).ToList();
        }

        targetIds = targetIds.Where(IsInScope).ToList();

        foreach (int staleId in _rowsByObjectId.Keys.Except(targetIds).ToList())
        {
            _rows.Remove(_rowsByObjectId[staleId]);
            _rowsByObjectId.Remove(staleId);
        }

        _deathsById.Clear();
        _takenSpan = span;
        foreach (int targetId in targetIds)
        {
            if (!_rowsByObjectId.TryGetValue(targetId, out var row))
            {
                row = new PlayerRow(targetId);
                _rowsByObjectId[targetId] = row;
                _rows.Add(row);
            }

            ApplyIdentity(row, targetId);
            var mine = hits.Where(ev => ev.TargetObjectId == targetId).ToList();
            row.IsEstimated = false;
            row.Damage = mine.Sum(ev => ev.Amount);
            row.Absorbed = absorbs.Where(ev => ev.TargetObjectId == targetId).Sum(ev => ev.Amount);
            double seconds = spanSeconds ?? (mine.Max(ev => ev.Timestamp) - mine.Min(ev => ev.Timestamp)).TotalSeconds;
            row.Dps = seconds > 0 ? row.Damage / seconds : null;

            var readings = directory is null || span is not (DateTime from, DateTime to)
                ? new List<(DateTime, long)>()
                : directory.HitPoints.SamplesAround(targetId, from, to + Deaths.BlowWindow).Select(s => (s.At, s.Hp)).ToList();
            var credits = directory is null || span is not (DateTime creditFrom, DateTime creditTo)
                ? null
                : directory.HitPoints.KillCreditsBetween(targetId, creditFrom, creditTo + Deaths.BlowWindow);
            var deaths = Deaths.Find(targetId, readings, mine, credits);
            _deathsById[targetId] = deaths;
            row.Deaths = deaths.Count;
            row.DeadFor = deaths.Count > 0 && deaths[^1].RevivedAt is null && span is (DateTime _, DateTime deadUntil) ? deaths[^1].DeadFor(deadUntil) : null;
            row.DamageTaken = 0;
            row.Absorbed = 0;
            row.AllMode = false;
            row.AllCompact = false;
            row.Healing = 0;
            row.ShowShareBar = _showShareBars;
            row.ShowDamageTaken = false;
            row.Faction = directory?.FactionOf(targetId) ?? "";
        }
    }


    /// <summary>A player is an object the meter has seen casting a class skill or whose "appeared"
    /// frame it read; everything else is an NPC (shown by its hex id or boss name).</summary>
    private bool IsPlayerName(int id) =>
        _source?.Entities is Aion2.Aion2EntityDirectory entities && entities.IsKnownPlayer(id)
        // a stored session or fight runs on a replay source that knows no players: the identities it came with (name and class are only known for players) tell instead, so its PvP view (the arena) is not empty
        || (_historyMode && _playerIdentities.ContainsKey(id));

    /// <summary>Sets Name/ClassName/Level for one row: from the stored identity when a past fight is
    /// shown, else from the packet stream - the name and class the directory has learned for the id.</summary>
    private void ApplyIdentity(PlayerRow row, int sourceId)
    {
        if (_playerIdentities.TryGetValue(sourceId, out var identity))
        {
            row.Name = identity.Name;
            row.ClassName = identity.ClassName;
            row.Level = identity.Level;
            return;
        }

        row.Name = ResolveDisplayName(sourceId);
        row.ClassName = ResolveClassName(sourceId);
    }

    /// <summary>The class an object has been seen casting a skill of (skill id prefix), keyed by the
    /// object id - not by name, which changes once the real name is learned ("Gladiator #331" ->
    /// "Aahz") and used to drop the class icon along with it. "?" while unknown.</summary>
    private string ResolveClassName(int sourceId) =>
        (_source?.Entities as Aion2EntityDirectory)?.ClassOf(sourceId) ?? "?";

    /// <summary>Adds any newly-seen DAMAGE targets to the dropdown (never removes - only Clear does
    /// that); "All" is the one entry with no Tag, everything else carries its target object id. Heal
    /// targets are excluded on purpose (a healed party member is no "Mob/Boss"). A non-player target
    /// only gets an entry when it is a boss - one whose NPC id the game announced and the boss catalog
    /// knows (see <see cref="IsKnownBoss"/>); a player target (PVP) always does.</summary>
    private void RefreshMobBossFilterItems(bool indexed = false)
    {
        var knownIds = _mobBossEntries.Select(entry => entry.TargetId).ToHashSet();
        bool added = false;

        // The index has the distinct targets already; the scan is for the views that did not feed it.
        IEnumerable<int> targets = indexed ? _index.Targets.ToList() : _aggregator.Events.Where(ev => !ev.IsHeal).Select(ev => ev.TargetObjectId).Distinct();
        foreach (int targetId in targets)
        {
            bool isPlayerTarget = IsPlayerName(targetId);
            if (knownIds.Contains(targetId))
            {
                continue;
            }

            string name = _targetNames.TryGetValue(targetId, out string? n) ? n : ResolveDisplayName(targetId);
            if (!isPlayerTarget && !IsKnownBoss(name, targetId))
            {
                continue;
            }

            _mobBossEntries.Add((targetId, name));
            added = true;
        }

        if (added || _mobBossFilterNeedsRebuild)
        {
            ApplyMobBossSearchFilter();
            RefreshUploadAvailability();
        }
    }

    /// <summary>
    /// Per the user: the upload entry points (the Session menu's two items, and the Damage view's
    /// own button) should not be offered at all until there is something real to upload - shows
    /// them the moment the Mob/Boss filter has at least one real target, hides them again after
    /// Clear. UploadBossButton additionally stays Loot-view-collapsed regardless (a target filter
    /// has no meaning there), so its visibility is never JUST "has data" the way the two menu
    /// items' is.
    /// </summary>
    private void RefreshUploadAvailability()
    {
        bool hasBossData = _mobBossEntries.Count > 0;
        // Aion 2 can also upload players without any boss fight (their character profiles), so the
        // single-upload entries stay available as soon as the own character is known.
        bool hasProfiles = _source?.Entities is Aion2.Aion2EntityDirectory { LocalCharacter: not null };
        bool uploadsAllowed = !PrivacyConsent.Restricted; // declined: no upload button at all
        _uploadFlags.ShowBoss = uploadsAllowed && (hasBossData || hasProfiles);
        _uploadFlags.ShowRun = uploadsAllowed && hasBossData;
        UploadBossButton.Visibility = uploadsAllowed && (hasBossData || hasProfiles) && PlayersGrid.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        // the paths of a boss fight for the replay: only where there is a boss fight to send them with
        UploadReplayButton.Visibility = uploadsAllowed && hasBossData && PlayersGrid.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Rebuilds MobBossFilter's VISIBLE Items from <see cref="_mobBossEntries"/>, keeping only
    /// names that CONTAIN the search box's current text - substring, case-insensitive ("like
    /// '%word%'", per the user, not WPF's own built-in type-ahead, which only matches from the
    /// start). "All" is always shown regardless of the search text - it isn't a target name to
    /// search for at all. Re-selects whichever target was selected before the rebuild, if it
    /// survived the filter: every rebuild creates new ComboBoxItem instances, so without this the
    /// selection (and _selectedTargetId with it) would reset on every keystroke.
    /// </summary>
    /// <summary>One dropdown entry's Tag - null WindowStart/End means "this target's whole
    /// history", a real pair means one specific numbered run (see MobBossTagsFor).</summary>
    private readonly record struct MobBossTag(int TargetId, DateTime? WindowStart, DateTime? WindowEnd);

    /// <summary>Gap between hits, in seconds, past which two hits on the same target count as
    /// separate runs rather than one continuous fight - same default the CLI's headless clustered
    /// upload already uses (see Program.cs), reused here so the dropdown's own split agrees with
    /// what "AionDPS upload" would produce for the same session.</summary>
    private const double RunClusterGapSeconds = 120;

    /// <summary>
    /// One or more (Tag, DisplayName, Damage) rows for a single Mob/Boss entry - per the user, a
    /// boss farmed repeatedly in one session (their example: Raksang Boilheart, five kills in a
    /// row) must appear as "Name #1".."Name #N" so each run stays individually selectable
    /// afterward, not just as one entry that silently combines every kill's numbers together. A
    /// target hit only once (the common case) still gets exactly one row, unnumbered and with a
    /// null window - identical to this method's pre-existing behavior for that case.
    /// </summary>
    private IEnumerable<(MobBossTag Tag, string Name, long Damage)> MobBossRowsFor(int targetId, string name)
    {
        var hits = _aggregator.Events
            .Where(ev => !ev.IsHeal && ev.TargetObjectId == targetId)
            .OrderBy(ev => ev.Timestamp)
            .ToList();
        if (hits.Count == 0)
        {
            yield return (new MobBossTag(targetId, null, null), name, 0);
            yield break;
        }

        // A wipe and retry keeps the boss's entity id and can come within the gap below, so the
        // next attempt's start cuts the hits as well: the boss pulled again after it was put back to
        // full health or left combat alive (see BossFight.AttemptStarts) - not the wipe itself, so a
        // last hit landing on the boss as it resets stays with the attempt that failed instead of
        // becoming a run of its own (6 November 2026). A run that ended in a wipe carries a skull.
        var directoryOfRuns = _source?.Entities as Aion2.Aion2EntityDirectory;
        DateTime runsNow = hits[^1].Timestamp > DateTime.Now ? hits[^1].Timestamp : DateTime.Now;
        IReadOnlyList<DateTime> wipes = directoryOfRuns is null ? Array.Empty<DateTime>()
            : directoryOfRuns.HitPoints.ResetsOf(targetId).Concat(directoryOfRuns.FailedEndsOf(targetId, runsNow)).ToList();
        IReadOnlyList<DateTime> resets = directoryOfRuns is null || directoryOfRuns.BossNpcIdOf(targetId) is null
            ? wipes
            : BossFight.AttemptStarts(directoryOfRuns, targetId, hits, runsNow);
        var clusters = new List<List<DamageEvent>>();
        var wiped = new List<bool>();
        foreach (DamageEvent hit in hits)
        {
            if (clusters.Count > 0)
            {
                DateTime previous = clusters[^1][^1].Timestamp;
                bool reset = resets.Any(at => at > previous && at <= hit.Timestamp);
                if (reset || (hit.Timestamp - previous).TotalSeconds > RunClusterGapSeconds)
                {
                    wiped[^1] = reset;
                    clusters.Add(new List<DamageEvent> { hit });
                    wiped.Add(false);
                    continue;
                }

                clusters[^1].Add(hit);
            }
            else
            {
                clusters.Add(new List<DamageEvent> { hit });
                wiped.Add(false);
            }
        }

        // The last run is a wipe too when the boss went back to full health or left combat alive
        // after it began, even if nobody pulled it again.
        DateTime lastStart = clusters[^1][0].Timestamp;
        wiped[^1] = wipes.Any(at => at > lastStart);

        if (clusters.Count == 1 && !wiped[0])
        {
            yield return (new MobBossTag(targetId, null, null), name, clusters[0].Sum(e => e.Amount));
            yield break;
        }

        for (int i = 0; i < clusters.Count; i++)
        {
            List<DamageEvent> cluster = clusters[i];
            var tag = new MobBossTag(targetId, cluster[0].Timestamp, cluster[^1].Timestamp);
            yield return (tag, $"{name} #{i + 1}{(wiped[i] ? " \U0001F480" : "")}", cluster.Sum(e => e.Amount));
        }
    }

    private void ApplyMobBossSearchFilter()
    {
        MobBossTag? previouslySelected = _selectedTargetId is int previousTargetId
            ? new MobBossTag(previousTargetId, _selectedRunWindowStart, _selectedRunWindowEnd)
            : null;
        string search = _mobBossSearchBox?.Text ?? "";
        // TextChanged rebuilds Items below, which regenerates the ComboBox's item containers and,
        // as a side effect, steals keyboard focus away from PART_SearchBox back to the ComboBox
        // itself - found from a real report: typing more than one letter required clicking back
        // into the search box after every single character. Caret position is saved too. Restored
        // via BeginInvoke at Input priority, same timing reasoning as DropDownOpened's own focus
        // hand-off a few lines below - the rebuilt containers aren't necessarily focusable yet on
        // this exact call frame.
        bool searchBoxHadFocus = _mobBossSearchBox?.IsFocused ?? false;
        int caretIndex = _mobBossSearchBox?.CaretIndex ?? 0;

        // Per the user: a real boss should always sort above trash mobs, by however much combined
        // damage the group actually did to it - not alphabetically, and not by discovery order.
        // Descending by damage puts a boss (tens of thousands of hits) far above a random trash
        // mob (a few hits in passing) without needing any curated "this is a real boss" list.
        //
        // _mobBossEntries is append-only for the whole session and mixes mob/boss targets with
        // player targets (a PVP fight's TargetObjectId points at another player, not a mob) - per
        // the user, PVE mode's list should only ever offer mobs/bosses, PVP mode's only players.
        // Filtering here (rather than in RefreshMobBossFilterItems) keeps both sets around so
        // switching modes mid-session doesn't lose entries seen while the other mode was active.
        var rows = _mobBossEntries
            .Where(entry => IsPlayerName(entry.TargetId) == _pvpOnly)
            .SelectMany(entry => MobBossRowsFor(entry.TargetId, entry.Name))
            .Where(row => search.Length == 0 || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.Damage)
            .ToList();

        try
        {
            MobBossFilter.Items.Clear();
            _mobBossAllItem = new ComboBoxItem { Tag = _mobBossAllTag };
            if (_mobBossAllContentBinding is not null)
            {
                BindingOperations.SetBinding(_mobBossAllItem, ContentControl.ContentProperty, _mobBossAllContentBinding);
            }
            else
            {
                _mobBossAllItem.Content = _mobBossAllContent;
            }

            MobBossFilter.Items.Add(_mobBossAllItem);
            foreach (var row in rows)
            {
                MobBossFilter.Items.Add(new ComboBoxItem { Content = row.Name, Tag = row.Tag });
            }
        }
        catch (InvalidOperationException)
        {
            // A WPF container hiccup while the dropdown is busy: not worth ending the meter over;
            // the next refresh tick rebuilds the list again.
            _mobBossFilterNeedsRebuild = true;
            return;
        }

        _mobBossFilterNeedsRebuild = false;

        // Only reassign SelectedItem when the previous selection actually survived the filter -
        // per the user, forcing it back to "All" (the first item, whenever nothing survives)
        // on every keystroke was what visibly stole focus from the search box mid-word, jumping
        // the dropdown to its first entry after each letter typed. Leaving SelectedItem alone
        // when nothing matches (WPF already cleared it via Items.Clear() above) means the box
        // simply shows no selection while a search is narrowing the list, instead of fighting the
        // user's typing for keyboard focus.
        var stillPresent = MobBossFilter.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag as MobBossTag?, previouslySelected));
        if (stillPresent is not null)
        {
            MobBossFilter.SelectedItem = stillPresent;
        }

        if (searchBoxHadFocus && _mobBossSearchBox is not null)
        {
            TextBox searchBox = _mobBossSearchBox;
            Dispatcher.BeginInvoke(() =>
            {
                searchBox.Focus();
                searchBox.CaretIndex = Math.Min(caretIndex, searchBox.Text.Length);
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>
    /// Wires up the SearchableComboBox template's PART_SearchBox - has to wait for the real
    /// visual tree (Loaded), since a ComboBox's template isn't materialized yet at construction
    /// time and Template.FindName needs it to be. Clearing the search text on every DropDownOpened
    /// (not just once here) is what keeps a stale search from an earlier session hiding the target
    /// the user wants next time they open the list.
    /// </summary>
    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplyClassFilterAvailability();
        LocalizationManager.Instance.PropertyChanged += (_, _) => LocalizeClassFilter();

        // See AppIconImage's own XAML remarks: a plain pack://siteoforigin Source silently
        // rendered nothing for this specific .ico, so it's decoded via GDI instead - the same
        // path Explorer itself uses for any .ico, rather than WPF's own (apparently less
        // reliable, for this file) BitmapImage/pack-URI decoding.
        try
        {
            using var appIcon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "assets", "app", "aiondps.ico"));
            AppIconImage.Source = Imaging.CreateBitmapSourceFromHIcon(
                appIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // Missing/corrupt icon file must not stop the window from opening - the titlebar
            // just shows no icon at all, same as before this existed.
        }

        MobBossFilter.ApplyTemplate();
        _mobBossSearchBox = MobBossFilter.Template.FindName("PART_SearchBox", MobBossFilter) as TextBox;
        if (_mobBossSearchBox is null)
        {
            return;
        }

        _mobBossSearchBox.TextChanged += (_, _) => ApplyMobBossSearchFilter();
        MobBossFilter.DropDownOpened += (_, _) =>
        {
            _mobBossSearchBox.Text = "";
            // BeginInvoke, not a direct call: the popup's own content isn't reliably focusable
            // yet at the instant DropDownOpened fires (same class of timing issue as focusing
            // anything else inside a just-opened Popup) -- Input priority runs it right after the
            // popup finishes opening, not on some arbitrary later frame.
            Dispatcher.BeginInvoke(() => _mobBossSearchBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private void OnMobBossFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // MobBossFilter's XAML sets SelectedIndex="0", which makes WPF fire this handler from
        // inside InitializeComponent itself -- while the rest of the tree (including
        // DpsHeaderText, declared further down in the XAML) hasn't been built yet. Found the hard
        // way: every single GUI launch crashed with a NullReferenceException before a window ever
        // appeared (back when this read DpsColumn, the merged column's predecessor). IsInitialized
        // only becomes true once the whole tree exists.
        if (!IsInitialized)
        {
            return;
        }

        if ((MobBossFilter.SelectedItem as ComboBoxItem)?.Tag is MobBossTag tag)
        {
            _selectedTargetId = tag.TargetId;
            _selectedRunWindowStart = tag.WindowStart;
            _selectedRunWindowEnd = tag.WindowEnd;
        }
        else
        {
            _selectedTargetId = null;
            _selectedRunWindowStart = null;
            _selectedRunWindowEnd = null;
        }

        UpdateDpsColumnHeader();
        RefreshRows();
    }

    /// <summary>Per the user: a two-way segmented toggle (PVE/PVP), not an independent checkbox -
    /// exactly one side is active, enforced here rather than via RadioButton's own chrome (see the
    /// style comment in the XAML). PVP restricts the whole grid to damage against other PLAYERS
    /// only (Arena/Abyss/GvG), mobs and bosses excluded entirely, and is mutually exclusive with
    /// the Mob/Boss filter (see RefreshRows) - reset to "All" here so the dropdown doesn't keep
    /// showing a now-irrelevant mob selection while PVP mode is active.</summary>
    private void OnPvpModeToggleClicked(object sender, RoutedEventArgs e)
    {
        _pvpOnly = ReferenceEquals(sender, PvpModeButton);
        PveModeButton.IsChecked = !_pvpOnly;
        PvpModeButton.IsChecked = _pvpOnly;

        if (_pvpOnly)
        {
            _selectedTargetId = null;
            _selectedRunWindowStart = null;
            _selectedRunWindowEnd = null;
            MobBossFilter.SelectedItem = _mobBossAllItem;
        }

        ApplyMobBossSearchFilter();
        UpdateDpsColumnHeader();
        RefreshRows();
    }

    private void UpdateDpsColumnHeader()
    {
        DpsHeaderText.Text = _healMode && !_pvpOnly ? "Healing / HPS"
            : _takenMode && !_pvpOnly ? "Taken / DTPS"
            : _allMode ? "Taken / Heal / Damage"
            : _pvpOnly ? "Damage / DPS (PvP)" : _mode == MeterMode.Raid ? "Damage / rDPS" : _selectedTargetId is int ? "Damage / iDPS" : "Damage / DPS";
    }

    // ---- the stream overlay (OBS browser source) ------------------------------------------------------------

    /// <summary>Four times a second: starts, stops or restarts the server as the settings say, and sends the state when it changed - only while a browser source is connected.</summary>
    private void StreamTick()
    {
        if (Headless)
        {
            return;
        }

        if ((DateTime.UtcNow - _streamSettingsAt).TotalSeconds >= 1)
        {
            _streamSettingsAt = DateTime.UtcNow;
            _streamSettings = MeterSettings.Load();
            if (_streamSettings.StreamOverlayOn && _streamSettings.StreamOverlayKey.Length == 0)
            {
                _streamSettings.StreamOverlayKey = Streaming.StreamOverlayServer.NewKey();
                var onDisk = MeterSettings.Load();
                onDisk.StreamOverlayKey = _streamSettings.StreamOverlayKey;
                onDisk.Save();
            }

            bool wanted = _streamSettings.StreamOverlayOn;
            if (!wanted)
            {
                _streamServer?.Dispose();
                _streamServer = null;
                _streamFailed = false;
            }
            else if (_streamServer is null || !_streamServer.Running || _streamServer.Url?.Contains("/" + _streamSettings.StreamOverlayKey + "/") != true
                || _streamServer.Network != _streamSettings.StreamOverlayNetwork
                || _streamServer.Port != Math.Clamp(_streamSettings.StreamOverlayPort, 1024, 65000) && !_streamServer.Running)
            {
                _streamServer?.Dispose();
                _streamServer = new Streaming.StreamOverlayServer();
                int port = Math.Clamp(_streamSettings.StreamOverlayPort, 1024, 65000);
                bool failedBefore = _streamFailed;
                _streamFailed = !_streamServer.Start(port, _streamSettings.StreamOverlayKey, _streamSettings.StreamOverlayNetwork);
                if (_streamFailed)
                {
                    if (!failedBefore)
                    {
                        ShowUploadStatus(LocalizationManager.Instance["Stream.NoPort"]);   // once, not at every try
                    }

                    _streamServer.Dispose();
                    _streamServer = null;
                }
            }
        }

        if (_streamServer is not { Running: true, ClientCount: > 0 })
        {
            return;
        }

        // this stream began when the first browser source connected (a new one after half an hour without any)
        if (_streamSince is null || DateTime.UtcNow - _streamLastClient > TimeSpan.FromMinutes(30))
        {
            _streamSince = DateTime.UtcNow;
            _streamMeSlow = null;
        }

        _streamLastClient = DateTime.UtcNow;
        _streamServer.PortraitFile ??= () => _streamPortrait;
        string json = BuildStreamState(_streamSettings.StreamShowNames);
        if (json != _streamLast)
        {
            _streamLast = json;
            _streamServer.Publish(json);
        }
    }

    /// <summary>What the browser sources draw now: the meter's lines as the meter over the game shows them (the same ten, colours and bar parts), the boss, the
    /// streamer's character for the card.</summary>
    private string BuildStreamState(bool showNames) =>
        Streaming.StreamSnapshot.ToJson(BuildStreamMeter(showNames), CurrentBoss() is { } b ? StreamBossOf(b.Name, b.Hp, b.Max, b.Kill, b.Rage) : null, BuildStreamMe());

    private static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private Streaming.StreamMeter BuildStreamMeter(bool showNames)
    {
        var shown = _rows.Where(r => _overlayLines is null || _overlayLines.Contains(r)).OrderByDescending(r => r.SortKey).ToList();
        IReadOnlyList<Streaming.StreamRowData> rows = shown.Select(r => new Streaming.StreamRowData(
            r.Rank, r.Name, r.ClassName, r.FillPercent / 100.0, _source?.Entities.IsLocalPlayer(r.ObjectId) == true,
            new[] { r.RateOrDamageCompact, r.ShareOrHealCompact, r.TotalOrTakenCompact }, _mode == MeterMode.AllCompact ? $"↓ {r.TakenCompact}  + {r.HealCompact}" : null,
            Hex(ClassBrushes.ClassColor(r.ClassName)), r.GivenFillPercent / 100.0, r.HealFillPercent / 100.0,
            r.BarGivenText.Length > 0 ? r.BarGivenText : null, r.BarHealText.Length > 0 ? r.BarHealText : null)).ToList();
        MeterTitles titles = MeterTitles.Instance;
        return new Streaming.StreamMeter(titles.Rate, OverlayTimeText.Text, OverlayTotalRateText.Text, OverlayTotalUnitText.Text,
            new[] { titles.Rate, titles.Share, titles.Total }, showNames ? rows : Streaming.StreamSnapshot.HideNames(rows), MaxOverlayLines);
    }

    private static Streaming.StreamBoss StreamBossOf(string name, long hp, long max, TimeSpan? kill, TimeSpan? rage)
    {
        static string Clock(TimeSpan t) => t <= TimeSpan.Zero ? "0:00" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        bool rageFirst = kill is TimeSpan k && rage is TimeSpan r && r > TimeSpan.Zero && k > r;
        double percent = max > 0 ? 100.0 * hp / max : 0;
        return new Streaming.StreamBoss(name.Length > 0 ? name : LocalizationManager.Instance["Overlay.BossBar.Boss"], percent,
            max > 0 ? $"{PlayerRow.Compact(hp)} / {PlayerRow.Compact(max)}" : PlayerRow.Compact(hp),
            kill is TimeSpan dies ? Clock(dies) : null, rageFirst, rage is TimeSpan left ? Clock(left) : null, rage is TimeSpan soon && soon <= HpBarView.RageSoonBefore);
    }

    // the character card's: this stream since the first browser source connected (again after half an hour without one), the parts read from files every 10 s
    private DateTime? _streamSince;
    private DateTime _streamLastClient;
    private DateTime _streamMeAt;
    private (string Key, int Bosses, string? Best, string? BestBoss, int? CpGain, int? Cp, int? ItemLevel, string? Legion)? _streamMeSlow;
    private string? _streamPortrait;
    private string? _streamPortraitFor;

    private Streaming.StreamMe? BuildStreamMe()
    {
        if (_source?.Entities is not Aion2.Aion2EntityDirectory dir || dir.LocalCharacter is not { } local || local.Name.Length == 0)
        {
            return null;
        }

        string cls = Aion2.Protocol.Aion2SkillNames.ClassFromCode(local.ClassCode) ?? "";
        int server = local.ServerId;
        string key = local.Name + "@" + server;
        if (_streamMeSlow?.Key != key || (DateTime.UtcNow - _streamMeAt).TotalSeconds >= 10)
        {
            _streamMeAt = DateTime.UtcNow;
            DateTime since = (_streamSince ?? DateTime.UtcNow).ToLocalTime();
            var fights = FightLog.Entries().Where(e => e.Start >= since && string.Equals(e.You, local.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var kills = fights.Where(e => e.Kind == FightLogKind.Boss && e.Killed == true).ToList();
            FightLogEntry? best = kills.Where(e => e.YourDps > 0).MaxBy(e => e.YourDps);
            List<CpCheckPoint> points = CpChecks.State(local.Name, server)?.Points.Where(p => p.Cp > 0).ToList() ?? new List<CpCheckPoint>();
            CpCheckPoint? last = points.LastOrDefault(), before = points.LastOrDefault(p => p.At <= (_streamSince ?? DateTime.UtcNow)) ?? points.FirstOrDefault(p => p.At > (_streamSince ?? DateTime.UtcNow));
            AccountCharacter? account = Aion2.Aion2Moved.Find(dir.AccountCharacters(), c => c.Name, c => c.ServerId, local.Name, server);
            _streamMeSlow = (key, kills.Count + fights.Where(e => e.Kind == FightLogKind.Run).Sum(e => e.BossFights), best is null ? null : PlayerRow.Compact((long)best.YourDps),
                best is null ? null : "on " + best.Title, last is not null && before is not null && last.At > before.At ? last.Cp - before.Cp : null, last?.Cp,
                last?.ItemLevel is int il and > 0 ? il : account?.ItemLevel is int own and > 0 ? own : null, account?.Legion ?? dir.GuildOf(local.CombatId));
            if (_streamPortraitFor != key && MeterSettings.Load().AutoUploadProfile)
            {
                _streamPortraitFor = key;
                _streamPortrait = null;
                string fingerprint = "aion2:" + ServerSlug(Aion2.Protocol.Aion2Servers.NameOf(server));
                _ = Task.Run(async () => _streamPortrait = await PortraitClient.GetAsync(local.Name, fingerprint));
            }
        }

        var slow = _streamMeSlow!.Value;
        string? live = null;
        if (_streamSince is DateTime start)
        {
            TimeSpan t = DateTime.UtcNow - start;
            live = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        string? portrait = _streamPortrait is string file ? "p/me.png?v=" + Path.GetFileNameWithoutExtension(file) : null;
        return new Streaming.StreamMe(local.Name, cls, Hex(cls.Length > 0 ? ClassBrushes.ClassColor(cls) : Color.FromRgb(0xE6, 0xC0, 0x68)), local.Level,
            Aion2.Protocol.Aion2Servers.NameOf(server), slow.Legion, slow.ItemLevel, slow.Cp, portrait, live, slow.Bosses, slow.Best, slow.BestBoss, slow.CpGain);
    }

    /// <summary>The link for the browser source, on the clipboard.</summary>
    private void CopyStreamLink()
    {
        var loc = LocalizationManager.Instance;
        if (_streamServer?.Url is not string url)
        {
            ShowUploadStatus(loc["Stream.NotRunning"]);
            return;
        }

        try
        {
            Clipboard.SetText(url);
            ShowUploadStatus(loc["Stream.LinkCopied"]);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            ShowUploadStatus(url); // the clipboard was busy: the link itself is shown instead
        }
    }

    // ---- the replay map ----------------------------------------------------------------------------------------

    /// <summary>The directory the replay data is read from: the live capture, also while a stored session is on view (its ids are the live ids).</summary>
    private Aion2.Aion2EntityDirectory? ReplayDirectory => (_historyMode ? _liveSource : _source)?.Entities as Aion2.Aion2EntityDirectory;

    /// <summary>The combat analyzer for one player of a session: the same details as a double click in the meter, but on the hits of that session only,
    /// whatever the meter itself shows; skills, rotation, effects and the replay map.</summary>
    /// <summary>The deaths of a session with the revive: from the file it came from, else from the live capture.</summary>
    private IReadOnlyList<DeathRecord>? DeathsForSession(SessionInfo session)
    {
        if (session.Extras?.Deaths is { } stored)
        {
            return stored;
        }

        return _liveSource?.Entities is Aion2.Aion2EntityDirectory dir
            ? dir.Combat.DeathsIn(session.Start, session.End ?? DateTime.Now, id => session.Classes.ContainsKey(id) || dir.IsKnownPlayer(id))
            : null;
    }

    private UIElement? BuildSessionAnalyzer(SessionInfo session, SessionManagerWindow.PlayerRow row, SessionManagerWindow.PlayerMode mode, SessionManagerWindow.BossRow? boss)
    {
        if (session.Data.Events.Count == 0)
        {
            return null;
        }

        // The picked boss narrows everything to its attempt (its time, and in damage mode the hits on it); no boss: the whole stay.
        IReadOnlyList<DamageEvent> scoped = SessionManagerWindow.ScopedEvents(session, boss?.Attempt, mode);
        var all = scoped.Where(ev => !ev.IsHeal).ToList();
        string title = $"{boss?.Name ?? LocalizationManager.Instance["Sessions.Boss.Total"]}  \u203A  {row.Name}";
        Func<int, string?> nameOf = id => session.Names.GetValueOrDefault(id) ?? ResolveDisplayName(id);
        if (mode == SessionManagerWindow.PlayerMode.Taken)
        {
            // what hit this player in the session, one row per attacker and attack
            var hits = all.Where(ev => ev.TargetObjectId == row.Id && !session.Classes.ContainsKey(ev.SourceObjectId)).ToList();
            var deaths = (DeathsForSession(session) ?? Array.Empty<DeathRecord>()).Where(d => d.Entity == row.Id)
                .Select(d => new Death(d.At, d.Entity, hits.Where(h => h.Timestamp <= d.At && h.Timestamp >= d.At - Deaths.BlowWindow).Cast<DamageEvent?>().LastOrDefault(), d.RevivedAt)).ToList();
            return new PlayerDetailsView(title, row.ClassName, "", hits, nameOf, deaths: deaths, taken: true);
        }

        if (mode == SessionManagerWindow.PlayerMode.Heal)
        {
            var heals = scoped.Where(ev => ev.IsHeal).ToList();
            return new PlayerDetailsView(title, row.ClassName, "", heals.Where(ev => ev.SourceObjectId == row.Id).ToList(), nameOf, heals: true, fightEvents: heals);
        }

        var mine = scoped.Where(ev => ev.SourceObjectId == row.Id).ToList();
        DateTime from = boss?.Attempt?.Start ?? (all.Count > 0 ? all.Min(ev => ev.Timestamp) : session.Start);
        DateTime to = boss?.Attempt?.End ?? (all.Count > 0 ? all.Max(ev => ev.Timestamp) : session.End ?? session.Start);
        FightExtras? extras = session.Extras ?? ExtrasForSession(session);
        return new PlayerDetailsView(title, row.ClassName, "", mine, nameOf, fightEvents: all, extras: extras, playerId: row.Id, fightStart: from, fightEnd: to,
            replay: BuildSessionReplay(session, all, extras, from, to));
    }

    /// <summary>The replay of the picked boss (or the whole stay), shown by the button of the combat analyzer; null when there is no replay data.</summary>
    private ReplayData? BuildSessionReplay(SessionInfo session, List<DamageEvent> all, FightExtras? extras, DateTime from, DateTime to)
    {
        if (extras is null || all.Count == 0)
        {
            return null;
        }

        Func<int, string?> nameOf = id => session.Names.GetValueOrDefault(id) ?? ResolveDisplayName(id);
        bool IsPlayer(int id) => session.Classes.Count == 0 || session.Classes.ContainsKey(id);
        return ReplayBuilder.Build(SessionLabels.NameOf(session), extras, all, from, to,
            id => (nameOf(id) ?? "", session.Classes.GetValueOrDefault(id) ?? "", IsPlayer(id) && session.Classes.ContainsKey(id)), SkillRadius,
            id => Aion2.Protocol.Aion2SkillNames.Display(Aion2.Protocol.Aion2SkillNames.NameOf(id)));
    }

    /// <summary>The rotation, effect, stagger and replay data of a session, read from the live capture, for saving it with the file: the players of the session and the bosses.</summary>
    private FightExtras? ExtrasForSession(SessionInfo session)
    {
        if (_liveSource?.Entities is not Aion2.Aion2EntityDirectory dir)
        {
            return null;
        }

        FightExtras extras = dir.Combat.Slice(session.Start, session.End ?? DateTime.Now, id => dir.NameFor(id) ?? dir.MonsterNameFor(id),
            id => session.Classes.ContainsKey(id) || dir.IsKnownPlayer(id) || dir.BossNpcIdOf(id) is not null,
            (id, from, to) => HpSeriesOf(dir, id, from, to, dir.IsKnownPlayer(id)));
        return extras.IsEmpty ? null : extras;
    }

    /// <summary>An id of the live capture as the grid knows it (the same, except while a session is on view).</summary>
    private int GridId(int liveId) => _historyMode ? _historyReplayIds.GetValueOrDefault(liveId, -1) : liveId;

    /// <summary>Whose positions the replay keeps: the group (the players in the rows) and the bosses.</summary>
    private bool ReplayKeeps(int id)
    {
        int gridId = GridId(id);
        return _rowsByObjectId.ContainsKey(gridId) && IsPlayerName(gridId)
            || ReplayDirectory?.BossNpcIdOf(id) is not null
            || _selectedTargetId == gridId
            || IsReplayAdd(id);
    }

    private HashSet<int> _replayAdds = new();
    private int _replayAddsFor = -1;

    /// <summary>The most monsters besides the boss the replay follows.</summary>
    private const int MaxReplayAdds = 12;

    /// <summary>
    /// A monster (not a player, not a boss) the group hit in this fight - an add. The ones hit most are kept, at most <see cref="MaxReplayAdds"/>. Only for the fight on screen:
    /// a stored session shows its own ids, which the damage events here no longer name.
    /// </summary>
    private bool IsReplayAdd(int id)
    {
        if (_historyMode || ReplayDirectory is null)
        {
            return false;
        }

        if (_replayAddsFor != _aggregator.Events.Count)
        {
            _replayAddsFor = _aggregator.Events.Count;
            _replayAdds = _aggregator.Events
                .Where(e => !e.IsHeal && e.TargetObjectId != _selectedTargetId && !IsPlayerName(e.TargetObjectId) && ReplayDirectory.BossNpcIdOf(e.TargetObjectId) is null)
                .GroupBy(e => e.TargetObjectId).OrderByDescending(g => g.Count()).Take(MaxReplayAdds).Select(g => g.Key).ToHashSet();
        }

        return _replayAdds.Contains(id);
    }

    /// <summary>A boss's health over the fight as thousandths of its highest reading, one reading a second at most.</summary>
    private HpSeries? ReplayHp(int id, DateTime from, DateTime to) =>
        ReplayDirectory is { } directory ? HpSeriesOf(directory, id, from, to, IsPlayerName(GridId(id))) : null;

    private static HpSeries? HpSeriesOf(Aion2.Aion2EntityDirectory directory, int id, DateTime from, DateTime to, bool isPlayer)
    {
        if (isPlayer || (directory.HitPoints.HighestSeen(id) ?? 0) <= 0)
        {
            return null;
        }

        long highest = directory.HitPoints.HighestSeen(id)!.Value;
        var samples = directory.HitPoints.SamplesAround(id, from, to);
        var thinned = new List<HpSample>();
        foreach (HpSample sample in samples)
        {
            if (thinned.Count == 0 || (sample.At - thinned[^1].At).TotalSeconds >= 1)
            {
                thinned.Add(sample);
            }
        }

        if (thinned.Count < 2)
        {
            return null;
        }

        DateTime origin = thinned[0].At;
        return new HpSeries(id, origin, thinned.Select(s => (int)(s.At - origin).TotalMilliseconds).ToArray(),
            thinned.Select(s => (short)Math.Clamp(s.Hp * 1000 / highest, 0, 1000)).ToArray());
    }

    /// <summary>The skills learnt so far, also while a stored fight is shown (that has no live directory): from the file the learning is kept in.</summary>
    private Aion2.Aion2CombatState? _lessonFile;

    private double? SkillRadius(int skillId)
    {
        if (ReplayDirectory?.Combat.LessonOf(skillId)?.RadiusCm is int live)
        {
            return live;
        }

        if (_lessonFile is null)
        {
            _lessonFile = new Aion2.Aion2CombatState { FilePath = System.IO.Path.Combine(MeterSettings.DataDirectory, "cast-skills.json") };
            _lessonFile.Load();
        }

        return _lessonFile.LessonOf(skillId)?.RadiusCm;
    }

    private ReplayData BuildReplayData(FightExtras? extras, DateTime? start, DateTime? end, IReadOnlyList<DamageEvent> events)
    {
        if (extras is null || start is not DateTime from || end is not DateTime to)
        {
            return ReplayBuilder.Build("", null, events, DateTime.Now, DateTime.Now, _ => ("", "", false), _ => null, _ => "");
        }

        string title = (_selectedTargetId is int target ? ResolveDisplayName(target) : "") + " " + from.ToString("g");
        if (_historyMode)
        {
            // the extras speak in ids of the live capture, the shown session in replay ids
            events = events.Select(ev => ev with
            {
                SourceObjectId = _historyLiveIds.GetValueOrDefault(ev.SourceObjectId, ev.SourceObjectId),
                TargetObjectId = _historyLiveIds.GetValueOrDefault(ev.TargetObjectId, ev.TargetObjectId),
            }).ToList();
        }

        ReplayData built = ReplayBuilder.Build(title.Trim(), extras, events, from, to,
            id => (ResolveDisplayName(GridId(id)), ResolveClassName(GridId(id)), IsPlayerName(GridId(id))), SkillRadius, id => Aion2.Protocol.Aion2SkillNames.Display(Aion2.Protocol.Aion2SkillNames.NameOf(id)),
            IsReplayAdd, id => ReplayDirectory is { } own && own.LocalPlayerId == id);
        // what the replay window needs to fetch the paths the other clients of this fight sent
        if (_selectedTargetId is int bossEntity && ReplayDirectory?.BossNpcIdOf(bossEntity) is int bossNpc && ResolveServerIdentity() is (string fingerprint, _))
        {
            built = built with { Lookup = new ReplayLookup(fingerprint, bossNpc, from.ToUniversalTime()) };
        }

        return built;
    }

    /// <summary>
    /// Builds the upload payload for whichever target <paramref name="targetId"/> refers to, from
    /// <see cref="_rows"/> as it stands right now -- so the caller must have already pointed
    /// <see cref="_selectedTargetId"/> at this target and called <see cref="RefreshRows"/>, the same
    /// way the grid itself gets Name/ClassName/Faction resolved (see ApplyIdentity/ApplySide). This
    /// deliberately reuses that already-correct state rather than re-deriving faction/class logic a
    /// second time here. Returns null when there is nothing to upload (target never hit, or no
    /// player rows survived the "players only" filter).
    ///
    /// Scoped to one specific numbered run when <see cref="_selectedRunWindowStart"/>/
    /// <see cref="_selectedRunWindowEnd"/> are set (the dropdown's "#N" entries - see MobBossTag's
    /// own remarks), otherwise spans the target's whole history exactly as before.
    /// </summary>
    private EncounterUploadRequest? BuildEncounterUpload(int targetId, string serverFingerprint, string? serverName)
    {
        // The payload's participants come from the rows on screen, which in heal or taken mode are
        // the healers or the damaged - build it from the damage rows, then put the mode back.
        MeterMode mode = _mode;
        if (mode == MeterMode.Damage)
        {
            return BuildEncounterUploadCore(targetId, serverFingerprint, serverName);
        }

        _mode = MeterMode.Damage;
        RefreshRows();
        try
        {
            return BuildEncounterUploadCore(targetId, serverFingerprint, serverName);
        }
        finally
        {
            _mode = mode;
            RefreshRows();
        }
    }

    private EncounterUploadRequest? BuildEncounterUploadCore(int targetId, string serverFingerprint, string? serverName)
    {
        var allTargetHits = _aggregator.Events.Where(e => e.TargetObjectId == targetId && !e.IsHeal);
        var targetHits = _selectedRunWindowStart is DateTime runStart
            ? allTargetHits.Where(e => e.Timestamp >= runStart && e.Timestamp <= _selectedRunWindowEnd).ToList()
            : allTargetHits.ToList();
        if (targetHits.Count == 0)
        {
            return null;
        }

        // A boss keeps its entity id across wipes and retries, so a whole-history upload would merge
        // every attempt into one fight. The server's hit points tell them apart: each time the boss
        // came back to full health starts a new attempt, and only the last one - the one that ended
        // the fight - is uploaded (a Draupnir run with two wipes before the kill, 2026-10-03).
        if (_selectedRunWindowStart is null
            && (_source?.Entities as Aion2.Aion2EntityDirectory)?.HitPoints.ResetsOf(targetId) is { Count: > 0 } resets)
        {
            DateTime lastAttempt = resets[^1];
            var lastAttemptHits = targetHits.Where(e => e.Timestamp >= lastAttempt).ToList();
            if (lastAttemptHits.Count > 0)
            {
                targetHits = lastAttemptHits;
            }
        }

        // Only a whole fight is uploaded (the server rejects anything else as "boss_not_killed"): the
        // boss must have been seen dying, and the damage dealt has to cover its hit points - else two
        // failed attempts would pass as one kill. The hit-point frames are the only fight-end signal
        // the protocol decodes so far.
        if (_source?.Entities is Aion2EntityDirectory directory && BossNotKilledReason(directory, targetId, targetHits) is { } notKilled)
        {
            AutoUploadLog.Write($"boss {targetId}: not uploaded - {notKilled}");
            return null;
        }

        // Kept separate from the UTC startedAt/endedAt below (those are for the outgoing payload's
        // own fields): heals never target the boss, so they can only be scoped to this encounter by
        // time window, and that window has to be compared against DamageEvent.Timestamp's own
        // (local) DateTimeKind, not a UTC-converted copy.
        // The fight runs from the first to the last hit on the boss, the blows the game reports just after the kill included, as the meter shows it
        // and as the other meters count it (5 November 2026; until then it ended at the boss's death).
        DateTime windowStart = targetHits.Min(e => e.Timestamp);
        DateTime windowEnd = targetHits.Max(e => e.Timestamp);

        return BuildEncounterUpload(targetId, serverFingerprint, serverName, targetHits, windowStart, windowEnd);
    }

    /// <summary>
    /// Fills in the damage the group shields absorbed per player (see <see cref="ShieldSkills"/>), over the same
    /// window and from the same attacker as <c>DamageTaken</c>, so the website can draw the taken-damage bar
    /// part red (got through) and part blue (absorbed). The shield names no caster, so nobody is credited with it.
    /// </summary>
    private List<ParticipantUpload> AttachAbsorbed(List<ParticipantUpload> participants, int attackerId, DateTime windowStart, DateTime windowEnd)
    {
        var byObject = _rows.GroupBy(r => r.ObjectId).ToDictionary(g => g.Key, g => g.First().Name);
        var absorbed = new Dictionary<string, long>();
        foreach (DamageEvent shield in _aggregator.Shields)
        {
            if (!ShieldSkills.IsAbsorb(shield) || shield.SourceObjectId != attackerId
                || shield.Timestamp < windowStart || shield.Timestamp > windowEnd
                || !byObject.TryGetValue(shield.TargetObjectId, out string? name))
            {
                continue;
            }

            absorbed[name] = absorbed.GetValueOrDefault(name) + shield.Amount;
        }

        return absorbed.Count == 0
            ? participants
            : participants.Select(p => absorbed.TryGetValue(p.Name, out long amount) ? p with { DamageAbsorbed = amount } : p).ToList();
    }

    /// <summary>The backend's ceiling for the players of one upload (uploadSchema.ts). Not applied to a world boss: the whole map is on it, and a cut would drop the players who matter for the damage total.</summary>
    private const int MaxUploadParticipants = 300;

    /// <summary>Whether the target is a world boss of the field maps (see Aion2HitAverages.IsEstimatedBoss): proven by its NPC id from the game's own traffic.</summary>
    private bool IsWorldBossTarget(int targetId) =>
        _source?.Entities is Aion2.Aion2EntityDirectory directory && directory.BossNpcIdOf(targetId) is int npc && Aion2.Protocol.Aion2HitAverages.IsEstimatedBoss(npc);

    /// <summary>How many of the players who hit the target have no name or class yet (their "appeared" or cast frames have not been read). Such rows are left out of the upload.</summary>
    private int UnresolvedAttackers(int targetId)
    {
        if (_source?.Entities is not Aion2.Aion2EntityDirectory directory)
        {
            return 0;
        }

        var unresolved = new HashSet<int>();
        var seen = new HashSet<int>();
        foreach (DamageEvent e in _aggregator.Events)
        {
            if (e.TargetObjectId != targetId || e.IsHeal || !seen.Add(e.SourceObjectId))
            {
                continue;
            }

            string? name = directory.NameFor(e.SourceObjectId);
            if (name is null || name.StartsWith("Player #", StringComparison.Ordinal) || name.StartsWith("0x", StringComparison.Ordinal) || !ClassCatalog.IsKnownClass(directory.ClassOf(e.SourceObjectId) ?? ""))
            {
                unresolved.Add(e.SourceObjectId);
            }
        }

        return unresolved.Count;
    }

    /// <summary>
    /// A world boss is uploaded only when the data is complete: the players who hit it all have a name and a class (else the upload leaves them out, as happened
    /// with 5 of 1000 players). Waits while that number goes down; gives up when it stands still for 30 s or after 3 minutes.
    /// </summary>
    private async Task WaitForCompleteWorldBossAsync(int targetId)
    {
        DateTime start = DateTime.UtcNow, lastProgress = start;
        int best = int.MaxValue;
        while (true)
        {
            int open = UnresolvedAttackers(targetId);
            if (open < best)
            {
                best = open;
                lastProgress = DateTime.UtcNow;
            }

            if (open == 0 || DateTime.UtcNow - lastProgress >= TimeSpan.FromSeconds(30) || DateTime.UtcNow - start >= TimeSpan.FromMinutes(3))
            {
                AutoUploadLog.Write($"boss {targetId}: world boss data {(open == 0 ? "complete" : $"as complete as it gets ({open} players without name or class)")} after {(DateTime.UtcNow - start).TotalSeconds:N0} s");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// Same payload construction, but with the target's hits and the time window supplied by the
    /// caller instead of always spanning that target id's ENTIRE history. Needed because
    /// a boss farmed multiple times in one session can keep the same target id across every
    /// kill - a headless clustered upload (see RunHeadlessClusteredUploadAsync) has to pass in one kill-cluster's hits/window at
    /// a time so each real fight gets its own upload instead of one merged across the whole file.
    /// The 3-argument overload above is just this one, called with that target's full history.
    /// </summary>
    /// <summary>More rows than this (a crowded world boss) and the upload carries no buff table.</summary>
    private const int MaxBuffRows = 60;

    private EncounterUploadRequest? BuildEncounterUpload(
        int targetId, string serverFingerprint, string? serverName,
        IReadOnlyList<DamageEvent> targetHits, DateTime windowStart, DateTime windowEnd)
    {
        if (targetHits.Count == 0 || _rows.Count == 0)
        {
            return null;
        }

        double durationSeconds = Math.Max((windowEnd - windowStart).TotalSeconds, 0.001);

        DateTime startedAt = windowStart.ToUniversalTime();
        DateTime endedAt = windowEnd.ToUniversalTime();
        string bossName = _targetNames.TryGetValue(targetId, out string? n) ? n : ResolveDisplayName(targetId);

        // Only a boss the game announced and the catalog knows may be uploaded.
        if (!IsKnownBoss(bossName, targetId))
        {
            return null;
        }

        // A world boss that was seen dying lost all its hit points: the estimated hits are scaled once to that (see EstimateRescale).
        if (targetHits.Any(e => e.Estimated) && _source?.Entities is Aion2EntityDirectory bossDirectory
            && bossDirectory.HitPoints.HighestSeen(targetId) is (> 0 and long bossLost) && bossDirectory.HitPoints.Latest(targetId) is { } bossLatest
            && BossDied(bossDirectory, bossDirectory.HitPoints, targetId, bossLatest)
            && EstimateRescale.FactorFor(targetHits, bossLost) is double rescale)
        {
            targetHits = EstimateRescale.Apply(targetHits, rescale);
            AutoUploadLog.Write($"boss {targetId}: estimated hits scaled by {rescale:N3} to the {bossLost:N0} hit points the boss lost");
        }

        var participants = new List<ParticipantUpload>();
        // Aion 2: the name only exists for players whose "appeared" frame the client saw, and the
        // class for those seen casting - an unnamed "Player #id" or a row without a class the backend
        // knows would make the backend reject the whole upload (class check) or store a nameless
        // player, so only complete rows go in. The own character is told by its name too: the game
        // gives a new object id on a map change, and the old one stops being "local".
        var aion2Directory = _source?.Entities as Aion2.Aion2EntityDirectory;
        string? ownName = aion2Directory?.LocalCharacter?.Name;
        // Profile and legion of the OTHER players only go along when the player agreed to it.
        bool shareOthers = MeterSettings.Load().UploadOtherPlayersProfiles;
        // The effects on every player over this fight, cut once (a crowded world boss has hundreds of rows: no buff table for it, the work would grow with them).
        var buffExtras = aion2Directory is not null && _rows.Count <= MaxBuffRows
            ? aion2Directory.Combat.Slice(windowStart, windowEnd, id => aion2Directory.NameFor(id))
            : null;
        // the boss's health over the fight, for the website's group timeline
        IReadOnlyList<int>? bossHp = buffExtras is not null ? TimelineBuilder.BossHp(buffExtras, targetId, windowStart, windowEnd) : null;
        // what the players put on the boss, for the website's "effects on the boss"
        IReadOnlyList<BuffUsageUpload>? bossDebuffs = buffExtras is not null && aion2Directory is not null ? BuffUpload.OnBoss(buffExtras, targetId, windowStart, windowEnd, id => aion2Directory.NameFor(id) ?? aion2Directory.MonsterNameFor(id)) : null;

        // One pass over the events instead of one per player: a world boss has a thousand of them, and each rescan of the
        // whole history per row made building the upload take minutes.
        var hitsBySource = targetHits.Where(e => !e.IsHeal).GroupBy(e => e.SourceObjectId).ToDictionary(g => g.Key, g => g.ToList());
        var healsBySource = new Dictionary<int, List<DamageEvent>>();
        var takenByTarget = new Dictionary<int, long>();
        foreach (DamageEvent e in _aggregator.Events)
        {
            if (e.Timestamp < windowStart || e.Timestamp > windowEnd)
            {
                continue;
            }

            if (e.IsHeal)
            {
                if (!healsBySource.TryGetValue(e.SourceObjectId, out List<DamageEvent>? heals))
                {
                    healsBySource[e.SourceObjectId] = heals = new List<DamageEvent>();
                }

                heals.Add(e);
            }
            else if (e.SourceObjectId == targetId)
            {
                takenByTarget[e.TargetObjectId] = takenByTarget.GetValueOrDefault(e.TargetObjectId) + e.Amount;
            }
        }

        // The damage curve, adds, time dead and damage taken of every player for the website's analysis of the fight (only where the effects were cut: no crowded world boss).
        Dictionary<int, AnalysisUpload> analysisByPlayer = buffExtras is not null && aion2Directory is not null
            ? FightAnalysis.Build(_aggregator.Events, targetId, buffExtras, _rows.Select(r => r.ObjectId).ToList(), aion2Directory.IsKnownMonster, windowStart, windowEnd)
            : new Dictionary<int, AnalysisUpload>();

        var emptyEvents = new List<DamageEvent>();
        var targetSpan = targetHits.Where(e => !e.IsHeal).ToList();
        double fightSeconds = targetSpan.Count == 0 ? 0 : (targetSpan.Max(e => e.Timestamp) - targetSpan.Min(e => e.Timestamp)).TotalSeconds;

        // More players than the upload takes (a world boss): the biggest by damage and the own character are worked out in full, nobody else.
        IEnumerable<PlayerRow> uploadRows = _rows;
        if (_rows.Count > MaxUploadParticipants && !IsWorldBossTarget(targetId))
        {
            uploadRows = _rows.Where(r => _source?.Entities.IsLocalPlayer(r.ObjectId) == true || (ownName is not null && r.Name == ownName))
                .Concat(_rows.Where(r => !(_source?.Entities.IsLocalPlayer(r.ObjectId) == true || (ownName is not null && r.Name == ownName)))
                    .OrderByDescending(r => hitsBySource.TryGetValue(r.ObjectId, out List<DamageEvent>? h) ? h.Sum(e => e.Amount) : 0)
                    .Take(MaxUploadParticipants))
                .ToList();
        }

        foreach (PlayerRow row in uploadRows)
        {
            if (aion2Directory is not null
                && (row.Name.StartsWith("Player #", StringComparison.Ordinal)
                    || row.Name.StartsWith("0x", StringComparison.Ordinal)
                    || !ClassCatalog.IsKnownClass(row.ClassName)))
            {
                continue;
            }

            List<DamageEvent> hitsOnBoss = hitsBySource.GetValueOrDefault(row.ObjectId) ?? emptyEvents;
            // Per the user: heals must be uploaded alongside damage - a pure healer who never hit
            // the boss would otherwise be silently dropped from the roster entirely, so a row
            // survives on EITHER contribution, not damage alone.
            List<DamageEvent> healsBySelf = healsBySource.GetValueOrDefault(row.ObjectId) ?? emptyEvents;
            // Per the user: a group member (row.IsEnemy false - see ApplySide/FactionResolver)
            // must always appear in the roster, even at 0, not just when they happen to land a
            // hit or heal inside this exact fight's tight damage-derived time window - found from
            // a real healer who buffed/healed the group only before this window started and
            // landed no heal inside it, so she was silently missing from the whole roster. An
            // actual enemy (or anyone whose side is still unresolved either way) with no
            // contribution to this target is still skipped - only a known ally survives on
            // presence alone.
            if (hitsOnBoss.Count == 0 && healsBySelf.Count == 0 && row.IsEnemy)
            {
                continue;
            }

            // Aion 2 has no side to tell allies from bystanders, so "an ally survives on presence
            // alone" would put every player standing nearby into the roster - and the backend takes
            // at most 24. A player counts when they hit or healed in this fight or took hits from the
            // boss; the own character always does.
            if (aion2Directory is not null && hitsOnBoss.Count == 0 && healsBySelf.Count == 0
                && !(ownName is not null && row.Name == ownName)
                && !takenByTarget.ContainsKey(row.ObjectId))
            {
                continue;
            }

            bool isSelf = _source?.Entities.IsLocalPlayer(row.ObjectId) == true || (ownName is not null && row.Name == ownName);
            // targetHits, not _aggregator.Events: TargetIDps derives its own duration from
            // whichever events it's given, so passing the full history back in here would silently
            // widen a clustered upload's iDPS window back out to the target id's entire history -
            // targetHits is already scoped to exactly this fight (see the two BuildEncounterUpload
            // overloads above).
            double idps = fightSeconds > 0 && hitsOnBoss.Count > 0 ? hitsOnBoss.Sum(e => e.Amount) / fightSeconds : 0; // as DpsCalculator.TargetIDps, with the fight's length worked out once
            // Most of the damage is a guess (a crowded world boss, see DamageEvent.Estimated): the row says so, and its crit hits are not known.
            bool estimated = hitsOnBoss.Count > 0 && hitsOnBoss.Count(e => e.Estimated) * 2 > hitsOnBoss.Count;
            var skills = SkillBreakdown.For(hitsOnBoss)
                .Select(s => new SkillUsageUpload(s.Skill, s.Hits, estimated ? 0 : s.CritHits, s.Total, s.Min, s.Max))
                .ToList();
            var healSkills = SkillBreakdown.For(healsBySelf, heals: true)
                .Select(s => new SkillUsageUpload(s.Skill, s.Hits, s.CritHits, s.Total, s.Min, s.Max))
                .ToList();

            long totalHealing = healsBySelf.Sum(e => e.Amount);
            double hps = totalHealing / durationSeconds;
            // NOT row.Damage: that field tracks whatever the Mob/Boss filter currently has
            // selected (see RefreshRows), so it silently went stale/wrong whenever an upload ran
            // while a different filter was active than when the fight itself happened. hitsOnBoss
            // is already independently scoped to exactly this targetId, the same data idps/skills
            // above are already computed from - a real upload bug (two uploads for the same fight
            // reporting wildly different totalDamage but identical, correct idps) traced back to
            // this exact line.
            long totalDamage = hitsOnBoss.Sum(e => e.Amount);

            // The opposite direction from hitsOnBoss above: what the BOSS did to this row, over the
            // same window - answers "who ate the boss's damage" (aggro/tank checks), which
            // totalDamage (dealt TO the boss) cannot. Windowed the same way healsBySelf is, not
            // targetHits-derived, since the boss is the SOURCE here, not the target.
            long damageTaken = takenByTarget.GetValueOrDefault(row.ObjectId);

            // The helpful effects that ran on this player (uptime, how often, who gave them): the website's buff columns.
            var buffs = buffExtras is not null && aion2Directory is not null
                ? BuffUpload.For(buffExtras, row.ObjectId, windowStart, windowEnd, id => aion2Directory.NameFor(id)).ToList()
                : new List<BuffUsageUpload>();

            participants.Add(new ParticipantUpload(
                row.Name, row.ClassName, row.Faction, isSelf,
                totalDamage, idps, idps, totalHealing, hps, skills, healSkills, damageTaken, buffs,
                shareOthers || isSelf ? aion2Directory?.GuildOf(ProfileIdOf(row), row.Name) : null,
                shareOthers || isSelf ? BuildProfileUpload(ProfileIdOf(row)) : null,
                ServerId: aion2Directory?.ServerIdOf(ProfileIdOf(row)),
                Estimated: estimated,
                CharacterId: CharacterIdFor(aion2Directory, row.Name, aion2Directory?.ServerIdOf(ProfileIdOf(row))),
                Timeline: buffExtras is not null && aion2Directory is not null
                    ? TimelineBuilder.For(buffExtras, row.ObjectId, windowStart, windowEnd, id => aion2Directory.NameFor(id), targetId, analysisByPlayer.GetValueOrDefault(row.ObjectId))
                    : null));
        }

        // The backend takes at most 300 players of an instance fight: the biggest by damage, the own character always. A world boss has no cut.
        if (participants.Count > MaxUploadParticipants && !IsWorldBossTarget(targetId))
        {
            participants = participants.Where(p => p.IsSelf).Concat(participants.Where(p => !p.IsSelf).OrderByDescending(p => p.TotalDamage))
                .Take(MaxUploadParticipants).ToList();
        }

        // an effect whose giver is nobody of this fight came from a monster: not a buff (the givers kept are the fight's players)
        var participantNames = participants.Select(p => p.Name).ToHashSet();
        participants = participants.Select(p => p with { Buffs = BuffUpload.KeepGroupSources(p.Buffs ?? Array.Empty<BuffUsageUpload>(), participantNames) }).ToList();

        participants = AttachAbsorbed(participants, targetId, windowStart, windowEnd);

        // Two rows of the own character (the object id changed during the run) would both be "self";
        // the backend wants exactly one, and merges same-named participants itself afterwards.
        if (participants.Count(p => p.IsSelf) > 1)
        {
            ParticipantUpload keep = participants.Where(p => p.IsSelf).MaxBy(p => p.TotalDamage)!;
            participants = participants.Select(p => p.IsSelf && !ReferenceEquals(p, keep) ? p with { IsSelf = false } : p).ToList();
        }

        // The backend requires exactly one isSelf participant per upload (see uploadSchema.ts) -
        // always true for a fight the local player took part in, but a target only a group member
        // hit (e.g. someone else's solo Training Dummy check) still gets tracked here since combat
        // log lines for the whole group are tracked in the same session. Uploading it anyway would
        // just get rejected with a 400 every time - same "nothing to upload" signal as an empty
        // participants list.
        if (participants.Count == 0 || !participants.Any(p => p.IsSelf))
        {
            return null;
        }

        // the fight report of the same fight for the website's tabs (not for a crowded world boss: no effects were cut for it)
        ReportUpload? report = null;
        if (buffExtras is not null && aion2Directory is not null)
        {
            try
            {
                var names = participants.Select(p => p.Name).ToHashSet();
                var reportPlayers = uploadRows.Where(r => names.Contains(r.Name)).GroupBy(r => r.Name).Select(g => g.First())
                    .Select(r => new ReportPlayer(r.ObjectId, r.Name, r.ClassName, SeenInfoOf(r)?.CombatPower, participants.Any(p => p.IsSelf && p.Name == r.Name))).ToList();
                var reportEvents = _aggregator.Events.Where(e => e.Timestamp >= windowStart && e.Timestamp <= windowEnd && (e.IsHeal || e.TargetObjectId != targetId))
                    .Concat(targetHits.Where(e => !e.IsHeal)).OrderBy(e => e.Timestamp).ToList();
                FightExtras reportExtras = aion2Directory.Combat.Slice(windowStart, windowEnd, ResolveDisplayName, ReplayKeeps, ReplayHp);
                // killed: the game's kill notice, or the boss seen dying with the group's hits covering its hit points (the proof the upload already
                // required; a field boss often comes without a kill notice, and its cleared fights showed as wipes on the website)
                bool bossKilled = _kills.Any(k => k.VictimObjectId == targetId && k.Timestamp >= windowStart)
                    || BossNotKilledReason(aion2Directory, targetId, targetHits.ToList()) is null;
                var reportInput = new ReportInput(bossName, windowStart, windowEnd, reportPlayers, reportEvents,
                    _aggregator.Shields.Where(e => e.Timestamp >= windowStart && e.Timestamp <= windowEnd).ToList(),
                    _avoids.Where(a => a.Timestamp >= windowStart && a.Timestamp <= windowEnd).ToList(),
                    reportExtras, targetId, BossNpcIdOf(targetId), bossKilled, ResolveDisplayName, new HashSet<int> { targetId });
                report = ReportUpload.From(FightReport.Build(reportInput));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                AutoUploadLog.Write("fight report for the upload failed: " + ex.Message);
            }
        }

        return new EncounterUploadRequest(
            AppVersion.Text, bossName, startedAt, endedAt, participants, serverFingerprint, serverName,
            Game: "aion2",
            BossNpcId: BossNpcIdOf(targetId),
            BossMaxHp: BossMaxHpOf(targetId),
            BossSpawnMaxHp: (_source?.Entities as Aion2EntityDirectory)?.HitPoints.SpawnMaxOf(targetId),
            ServerId: Aion2OwnServerId(),
            GameServer: Aion2GameServerAddress(),
            BossLevel: (_source?.Entities as Aion2EntityDirectory)?.NpcLevelOf(targetId),
            BossHp: bossHp,
            BossDebuffs: bossDebuffs,
            Report: report,
            ZoneId: UploadZoneId(),
            ZoneName: UploadZoneId() is int zone ? Aion2Zones.NameOf(zone, "en") : null,
            MeterBar: _meterLook.Bar);
    }

    /// <summary>The zone the meter is in (the boss's, as the upload follows the kill at once); null when not known.</summary>
    private int? UploadZoneId() => _liveSource is Aion2PacketCombatSource source && source.CurrentZoneId > 0 ? source.CurrentZoneId : null;

    /// <summary>Hides the Class dropdown's entries that are no Aion 2 class (the static XAML list is
    /// wider than the game's roster); an unknown selection falls back to "All".</summary>
    /// <summary>The class names in the class filter, in the UI language as the game client spells them.</summary>
    private void LocalizeClassFilter()
    {
        string language = LocalizationManager.Instance.Language;
        foreach (ComboBoxItem item in ClassFilter.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string className && item.Content is StackPanel { Children: { Count: > 0 } children })
            {
                string shown = ClassCatalog.DisplayName(className, language);
                if (children.OfType<TextBlock>().FirstOrDefault() is { } text)
                {
                    text.Text = shown;
                }

                System.Windows.Automation.AutomationProperties.SetName(item, shown);
            }
        }
    }

    private void ApplyClassFilterAvailability()
    {
        LocalizeClassFilter();
        bool selectedClassHidden = false;
        foreach (ComboBoxItem item in ClassFilter.Items.OfType<ComboBoxItem>())
        {
            bool hide = item.Tag is string className && !ClassCatalog.IsKnownClass(className);
            item.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            if (hide && ReferenceEquals(item, ClassFilter.SelectedItem))
            {
                selectedClassHidden = true;
            }
        }

        if (selectedClassHidden)
        {
            ClassFilter.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// The Aion 2 server the uploader plays on, from the game itself: the own character's record
    /// carries a server id (see <see cref="Aion2Servers"/>), which is a stable identity - unlike the
    /// game server's IP, which changed between two sessions of the same character. Null until the
    /// game has sent the record (log in or change map with the meter running).
    /// </summary>
    private (string Fingerprint, string? DisplayName)? ResolveServerIdentity() =>
        Aion2ServerName() is string name ? ("aion2:" + ServerSlug(name), name) : null;

    private static string ServerSlug(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    private int? Aion2OwnServerId() =>
        (_source?.Entities as Aion2EntityDirectory)?.LocalCharacter is { ServerId: > 0 } own ? own.ServerId : null;

    /// <summary>IP:port of the game server the capture is attached to ("193.202.112.97:13328"), the only
    /// thing that tells the regions apart - a server id is reused by every region.</summary>
    private string? Aion2GameServerAddress() =>
        (_source as Aion2.Aion2PacketCombatSource)?.ServerFingerprint is string f && f.StartsWith("aion2:") ? f["aion2:".Length..] : null;

    private string? Aion2ServerName() =>
        (_source?.Entities as Aion2EntityDirectory)?.LocalCharacter is { ServerId: > 0 } own ? Aion2Servers.NameOf(own.ServerId) : null;

    private const string ServerNotIdentified =
        "The game has not told the meter your Aion 2 server yet - log in (or change map) with the meter running, then try again. Upload refused rather than file your run under the wrong server.";

    /// <summary>Whether a target is a boss: its NPC id was announced by the game and is in the boss
    /// catalog (<see cref="Aion2BossCatalog"/>).</summary>
    private bool IsKnownBoss(string name, int targetId) =>
        (_source?.Entities as Aion2EntityDirectory)?.BossNpcIdOf(targetId) is not null;

    /// <summary>The NPC id of the boss behind a target, for the upload.</summary>
    private int? BossNpcIdOf(int targetId) =>
        (_source?.Entities as Aion2EntityDirectory)?.BossNpcIdOf(targetId);

    /// <summary>The highest hit-point reading the game sent for a boss, for the upload.</summary>
    private long? BossMaxHpOf(int targetId) =>
        (_source?.Entities as Aion2EntityDirectory)?.HitPoints.HighestSeen(targetId) is > 0 and long hp ? hp : null;

    /// <summary>For each boss target, the time of the last hit it took that already went online by
    /// the hotkey - so the next press sends only what is new (see <see cref="PendingBossTargets"/>).</summary>
    private readonly Dictionary<int, DateTime> _bossUploadedUntil = new();

    /// <summary>
    /// The boss targets with hits the hotkey has not sent yet, oldest first. A boss counts as new
    /// again when it took a hit after the last upload (a later attempt on the same entity). The
    /// selected target is always included, sent again on purpose when the user picked it.
    /// </summary>
    private List<int> PendingBossTargets()
    {
        var knownIds = _mobBossEntries.Select(entry => entry.TargetId).ToHashSet();
        var lastHit = new Dictionary<int, (DateTime First, DateTime Last)>();
        foreach (DamageEvent ev in _aggregator.Events)
        {
            if (ev.IsHeal || !knownIds.Contains(ev.TargetObjectId))
            {
                continue;
            }

            lastHit[ev.TargetObjectId] = lastHit.TryGetValue(ev.TargetObjectId, out var seen)
                ? (ev.Timestamp < seen.First ? ev.Timestamp : seen.First, ev.Timestamp > seen.Last ? ev.Timestamp : seen.Last)
                : (ev.Timestamp, ev.Timestamp);
        }

        var pending = lastHit
            .Where(kv => !_bossUploadedUntil.TryGetValue(kv.Key, out DateTime sent) || kv.Value.Last > sent)
            .Where(kv => BossFightFinished(kv.Key))
            .OrderBy(kv => kv.Value.First)
            .Select(kv => kv.Key)
            .ToList();
        if (_selectedTargetId is int selected && knownIds.Contains(selected) && !pending.Contains(selected) && BossFightFinished(selected))
        {
            pending.Add(selected);
        }

        return pending;
    }

    /// <summary>
    /// The upload hotkey / button: sends EVERY boss fight not sent yet, not just one. It used to send a
    /// single boss - the one picked in the Mob/Boss filter or else the one hit last - so a fight
    /// the filter was not pointing at (Thamon, the second boss of a Vakron Sky Island run, went missing
    /// from three runs on 2026-10-04) simply never went online. The server merges what the group's
    /// members send, so sending a fight twice is harmless; it is just not done without a new hit.
    /// </summary>
    private async void OnUploadCurrentBossClicked(object sender, RoutedEventArgs e)
    {
        if (!PrivacyConsent.EnsureForUploadSwitch(this))
        {
            return;
        }

        AppMenu.IsSubmenuOpen = false;
        List<int> targetIds = PendingBossTargets();
        if (targetIds.Count == 0)
        {
            // No boss fight, but Aion 2 still has characters worth uploading (per the user).
            if (_source?.Entities is Aion2.Aion2EntityDirectory)
            {
                await UploadPlayersOnlyAsync();
                return;
            }

            ShowUploadStatus(_mobBossEntries.Count == 0 ? "No boss fights recorded yet."
                : _mobBossEntries.Any(entry => !BossFightFinished(entry.TargetId)) ? "Nothing to upload - the boss is still alive (a failed attempt is not uploaded)."
                : "Every boss fight is already uploaded.");
            return;
        }

        if (ResolveServerIdentity() is not (string fingerprint, var displayName))
        {
            ShowUploadStatus(ServerNotIdentified);
            return;
        }

        ShowUploadStatus(targetIds.Count == 1 ? "Uploading..." : $"Uploading {targetIds.Count} boss fights...");

        // The payload is built from the rows the grid shows for the selected boss, so the selection
        // is pointed at each boss in turn and put back afterwards (as in OnUploadLastRunClicked).
        int? previousTarget = _selectedTargetId;
        DateTime? previousRunWindowStart = _selectedRunWindowStart;
        DateTime? previousRunWindowEnd = _selectedRunWindowEnd;
        var uploadedNames = new List<string>();
        var justUploaded = new List<JustUploaded>();
        string? lastError = null;
        bool nothingRecorded = false;
        bool first = true;
        foreach (int targetId in targetIds)
        {
            if (!first)
            {
                await Task.Delay(100);
            }

            first = false;
            _selectedTargetId = targetId;
            _selectedRunWindowStart = null;
            _selectedRunWindowEnd = null;
            RefreshRows();
            var payload = BuildEncounterUpload(targetId, fingerprint, displayName);
            if (payload is null)
            {
                nothingRecorded = true;
                continue;
            }

            UploadResult result = await UploadClient.SendAsync(payload);
            if (result.Success)
            {
                await SendReplayAfterAsync(payload, result, fingerprint);
                uploadedNames.Add(payload.BossNpcName);
                if (UploadedFight(payload, result) is { } fight)
                {
                    justUploaded.Add(fight);
                }

                _bossUploadedUntil[targetId] = _aggregator.Events
                    .Where(ev => !ev.IsHeal && ev.TargetObjectId == targetId)
                    .Max(ev => ev.Timestamp);
            }
            else
            {
                lastError = result.Error;
            }
        }

        _selectedTargetId = previousTarget;
        _selectedRunWindowStart = previousRunWindowStart;
        _selectedRunWindowEnd = previousRunWindowEnd;
        RefreshRows();

        if (uploadedNames.Count > 0)
        {
            string said = uploadedNames.Count == 1 ? "Uploaded." : $"Uploaded {uploadedNames.Count} boss fights: {string.Join(", ", uploadedNames)}.";
            string? page = uploadedNames.Count == 1 ? UploadClient.LastFightUrl : null;
            ShowUploadStatus(said, page);
            await FetchRanksAsync(justUploaded);
        }
        else
        {
            ShowUploadStatus(lastError is not null ? $"Upload failed: {lastError}" : nothingRecorded ? "Nothing recorded for this boss yet." : "Nothing to upload.");
        }
    }

    /// <summary>Aion 2 without a boss fight: uploads the own character and every other player whose
    /// name, class and equipment were read from the network, as profiles only (no encounter).</summary>
    private async Task UploadPlayersOnlyAsync()
    {
        if (ResolveServerIdentity() is not (string fingerprint, var displayName))
        {
            ShowUploadStatus(ServerNotIdentified);
            return;
        }

        var payload = BuildProfilesUpload(fingerprint, displayName);
        if (payload is null)
        {
            ShowUploadStatus("No character data to upload yet - log in once with the meter running.");
            return;
        }

        ShowUploadStatus($"Uploading {payload.Participants.Count} character(s)...");
        UploadResult result = await UploadClient.SendProfilesAsync(payload);
        ShowUploadStatus(result.Success ? $"Uploaded {payload.Participants.Count} character(s)." : $"Upload failed: {result.Error}");
    }

    // ---- the own Aion 2 profile goes online by itself -------------------------------------------
    // Per the user: the player must be findable on the site as a profile, without any boss fight and
    // without a click. The game sends the character record (equipment, skills, Daevanion) at login and
    // at map changes, in a burst; once it has been quiet for a few seconds the own profile is uploaded -
    // only the own one (other players' profiles still go with a boss upload or the manual upload), and
    // only when it differs from what was last sent this session.

    private System.Windows.Threading.DispatcherTimer? _profileUploadTimer;
    private string? _lastProfileUploadHash;
    private bool _profileUploadHintShown;

    private void ScheduleOwnProfileUpload()
    {
        // Always runs (but never in a headless mode): besides the upload it keeps the "legion member" flag of the settings up to date.
        if (Headless)
        {
            return;
        }

        StartLoginDumpTimer();
        if (_profileUploadTimer is null)
        {
            _profileUploadTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _profileUploadTimer.Tick += async (_, _) =>
            {
                _profileUploadTimer.Stop();
                UpdateLoginShareEligibility();
                if (MeterSettings.Load().AutoUploadProfile)
                {
                    await UploadOwnProfileAsync();
                    await UploadInspectedAsync();
                }

            };
        }

        _profileUploadTimer.Stop();
        _profileUploadTimer.Start();
    }

    // ---- the login of a legion member (opt-in, hidden for everybody else) ------------------------
    // Members of Akatsuki on Kaisinel get a switch in Settings > General. With it on, the whole login traffic of the character (what the game server
    // sent while it logged in) goes once per login to the community server, which keeps it for a short time to find what ties the characters of one
    // player together. The switch is hidden, the capture is not even started and nothing is sent for anybody else - the server checks the legion again.

    private const string LoginShareLegion = "Akatsuki";
    private const int LoginShareServerId = 1304; // Europe - Kaisinel (Elyos)

    // Atreia: the upstream login-traffic upload (one legion, off upstream too) is never offered in this copy.
    private bool IsLoginShareEligible() => false && 
        _source?.Entities is Aion2.Aion2EntityDirectory { LocalCharacter: { } own } directory
        && own.ServerId == LoginShareServerId
        && string.Equals((directory.GuildOf(own.CombatId, own.Name) ?? directory.AccountLegionOf(own.Name))?.Trim(), LoginShareLegion, StringComparison.OrdinalIgnoreCase);

    /// <summary>Remembers in the settings whether the logged-in character is in that legion (decides whether the settings show the switch).</summary>
    private void UpdateLoginShareEligibility()
    {
        if (_source?.Entities is not Aion2.Aion2EntityDirectory { LocalCharacter: not null })
        {
            return;
        }

        bool eligible = IsLoginShareEligible();
        var settings = MeterSettings.Load();
        if (settings.LoginShareEligible != eligible)
        {
            settings.LoginShareEligible = eligible;
            settings.Save();
        }
    }

    private System.Windows.Threading.DispatcherTimer? _loginDumpTimer;
    private bool _loginDumpBusy;

    /// <summary>Its own timer: the profile upload waits for a quiet moment, which an active player never gives it (stat frames keep coming).</summary>
    private void StartLoginDumpTimer()
    {
        if (_loginDumpTimer is not null || !Aion2.Capture.LoginCapture.Available)
        {
            return;
        }

        _loginDumpTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _loginDumpTimer.Tick += async (_, _) => await UploadLoginDumpAsync();
        _loginDumpTimer.Start();
    }

    private async Task UploadLoginDumpAsync()
    {
        if (_loginDumpBusy || !Aion2.Capture.LoginCapture.Pending)
        {
            return;
        }

        _loginDumpBusy = true;
        try
        {
            if (!MeterSettings.Load().ShareLoginWithLegion)
            {
                return;
            }

            Aion2.Capture.LoginCapture.Flush();
            while (Aion2.Capture.LoginCapture.Next() is { } item)
            {
                // Only for a character the login's character list put in that legion, on that server; anything else is dropped unsent.
                if (_source?.Entities is not Aion2.Aion2EntityDirectory { LocalCharacter: { } own } directory
                    || own.ServerId != LoginShareServerId
                    || !string.Equals(directory.AccountLegionOf(item.Name)?.Trim(), LoginShareLegion, StringComparison.OrdinalIgnoreCase))
                {
                    Aion2.Capture.LoginCapture.Done(item.Name);
                    continue;
                }

                var request = new LoginDumpUploadRequest(AppVersion.Text, item.Name, LoginShareLegion, own.ServerId, Aion2GameServerAddress(), Convert.ToBase64String(item.Gzip));
                UploadResult result = await UploadClient.SendLoginDumpAsync(request);
                AutoUploadLog.Write(result.Success ? $"login of {item.Name}: {item.Gzip.Length / 1024} KB sent to the legion analysis" : $"login of {item.Name}: not sent - {result.Error}");
                if (!result.Success)
                {
                    break; // tried again with the next tick
                }

                Aion2.Capture.LoginCapture.Done(item.Name);
            }
        }
        finally
        {
            _loginDumpBusy = false;
        }
    }

    // ---- a boss fight goes online by itself (opt-in) --------------------------------------------
    // A boss that died is uploaded a few seconds later, so the last hits and the kill frame have all
    // arrived. Same payload as the Upload button; the server merges what the group's members send.

    private readonly HashSet<int> _bossesToUpload = new();
    /// <summary>Hashes of the boss fights this session has sent, so an unchanged fight is not sent again.</summary>
    private readonly HashSet<string> _sentBossPayloads = new();
    private System.Windows.Threading.DispatcherTimer? _bossUploadTimer;

    private void NoteBossKills(IReadOnlyList<KillEvent> kills)
    {
        NoteBossesToUpload(kills.Where(kill => !kill.VictimIsPlayer && BossNpcIdOf(kill.VictimObjectId) is not null).Select(kill => kill.VictimObjectId));
    }

    /// <summary>Bosses already queued after a defeat; a boss that is back above zero (a new attempt) may be queued again.</summary>
    private readonly HashSet<int> _defeatNoted = new();

    /// <summary>
    /// Aion 2 sends no kill frame, but the boss's hit points reach 0 when it dies (checked on a
    /// Fire Temple run, 2026-10-05: every boss's last reading is 0). A boss at 0 is queued for the
    /// automatic upload; one that wiped the group (back at full health) never is.
    /// </summary>
    private void NoteDefeatedBosses()
    {
        if (Headless || _historyMode || _source?.Entities is not Aion2.Aion2EntityDirectory directory)
        {
            return;
        }

        var defeated = new List<int>();
        DateTime now = DateTime.Now;
        foreach ((int entityId, _) in directory.KnownBosses())
        {
            bool quietLong = _bossActivity.TryGetValue(entityId, out (DateTime First, DateTime Last) activity)
                && now - activity.Last >= BossQuietBeforeUpload
                && activity.Last - activity.First >= BossMinFightLength;
            if (directory.HitPoints.Latest(entityId) is not { } hp)
            {
                // No hit-point reading ever came for this boss (some never send one): all that says
                // it is over is that nobody has hit it for a while after a real fight. Without this
                // such a boss was never uploaded by itself and had to be sent by hand.
                if (quietLong)
                {
                    AutoUploadLog.Write($"boss {entityId}: no HP reading, quiet for {BossQuietBeforeUpload.TotalSeconds:0} s after a fight");
                    defeated.Add(entityId);
                    _bossActivity.Remove(entityId);
                }

                continue;
            }

            if (hp.Hp > 0)
            {
                _defeatNoted.Remove(entityId);
                // The last reading before the kill can stop just short of 0 (the final frame is
                // lost or never sent): within 0.5 % and quiet for a few seconds counts as dead.
                if (hp.Hp <= (directory.HitPoints.HighestSeen(entityId) ?? 0) * 0.005
                    && activity.Last != default && now - activity.Last >= TimeSpan.FromSeconds(5) && _defeatNoted.Add(entityId))
                {
                    defeated.Add(entityId);
                }
            }
            else if (_defeatNoted.Add(entityId))
            {
                defeated.Add(entityId);
            }
        }

        NoteBossesToUpload(defeated);
    }

    /// <summary>First and latest hit on each known boss in the current stretch of fighting.</summary>
    private readonly Dictionary<int, (DateTime First, DateTime Last)> _bossActivity = new();

    private static readonly TimeSpan BossQuietBeforeUpload = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BossMinFightLength = TimeSpan.FromSeconds(10);

    private void NoteBossActivity(IReadOnlyList<DamageEvent> events)
    {
        if (events.Count == 0 || _source?.Entities is not Aion2.Aion2EntityDirectory directory)
        {
            return;
        }

        foreach (DamageEvent e in events)
        {
            if (e.IsHeal || directory.BossNpcIdOf(e.TargetObjectId) is null)
            {
                continue;
            }

            _bossActivity[e.TargetObjectId] = _bossActivity.TryGetValue(e.TargetObjectId, out var a)
                // A pause this long means a new stretch (second kill of the same boss entity).
                && e.Timestamp - a.Last < BossQuietBeforeUpload + BossQuietBeforeUpload
                ? (a.First, e.Timestamp > a.Last ? e.Timestamp : a.Last)
                : (e.Timestamp, e.Timestamp);
        }
    }

    /// <summary>
    /// Whether a boss fight can be uploaded: the boss is dead (hit points 0, or within 0.5 % of it when
    /// the last frame was lost), or no hit-point reading exists to say otherwise. A boss that is still
    /// alive is a failed attempt - the group wiped or left - and goes nowhere.
    /// </summary>
    private bool BossFightFinished(int targetId)
    {
        if ((_source?.Entities as Aion2EntityDirectory)?.HitPoints is not { } hitPoints || hitPoints.Latest(targetId) is not { } latest)
        {
            return true;
        }

        return BossDied(_source!.Entities as Aion2EntityDirectory, hitPoints, targetId, latest);
    }

    /// <summary>The boss died: its last hit-point reading is 0 (or within 0.5 % of it), or the server removed it
    /// as dead (reason 3) - which covers a lost final reading. A boss that read 100 % again or vanished alive
    /// (reason 7, the instance was closed) did not die.</summary>
    private static bool BossDied(Aion2EntityDirectory? directory, Aion2HitPoints hitPoints, int targetId, HpSample latest)
    {
        if (latest.Hp <= (hitPoints.HighestSeen(targetId) ?? 0) * 0.005)
        {
            return true;
        }

        return directory?.RemovalOf(targetId) is { Reason: Aion2EntityDirectory.RemovedDead };
    }

    /// <summary>Same limit the server applies (constants.ts MIN_KILL_DAMAGE_SHARE).</summary>
    private const double MinKillDamageShare = 0.9;

    /// <summary>
    /// Why the fight does not count as a kill, or null when it does: the boss must have been seen dying
    /// (see <see cref="BossDied"/>) and the hits on it must add up to at least <see cref="MinKillDamageShare"/>
    /// of its highest reading. No reading at all counts as "not proven" - an empty run slipped through
    /// that way (Run 421).
    /// </summary>
    private static string? BossNotKilledReason(Aion2EntityDirectory directory, int targetId, List<DamageEvent> targetHits)
    {
        Aion2HitPoints hitPoints = directory.HitPoints;
        if (hitPoints.HighestSeen(targetId) is not (> 0 and long maxHp) || hitPoints.Latest(targetId) is not { } latest)
        {
            return "no hit-point reading of the boss";
        }

        if (!BossDied(directory, hitPoints, targetId, latest))
        {
            return $"the boss was not seen dying (last reading {latest.Hp} of {maxHp})";
        }

        if (targetHits.Any(e => e.Estimated))
        {
            // A crowded world boss: the damage of the others is estimated and cannot prove the kill by its sum (the server sends only the hits' notices).
            // The boss was seen dying above. The estimate never adds up to the whole, so the line is lower: 75 % of the hit points (constants.ts MIN_WORLD_BOSS_DAMAGE_SHARE).
            double estimatedDamage = targetHits.Sum(e => (double)e.Amount);
            return estimatedDamage >= maxHp * 0.75 ? null : $"world boss: damage {estimatedDamage:N0} covers less than 75 % of the boss's {maxHp:N0} hit points";
        }

        double damage = targetHits.Sum(e => (double)e.Amount);
        return damage >= maxHp * MinKillDamageShare ? null : $"damage {damage:N0} covers less than {MinKillDamageShare:P0} of the boss's {maxHp:N0} hit points";
    }

    private void NoteBossesToUpload(IEnumerable<int> bossEntityIds)
    {
        if (Headless || _historyMode || !MeterSettings.Load().AutoUploadBoss)
        {
            return;
        }

        bool added = false;
        foreach (int entityId in bossEntityIds)
        {
            if (_bossesToUpload.Add(entityId))
            {
                added = true;
                AutoUploadLog.Write($"boss {entityId} queued for the automatic upload");
            }
        }

        if (_bossesToUpload.Count == 0)
        {
            return;
        }

        if (_bossUploadTimer is null)
        {
            _bossUploadTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _bossUploadTimer.Tick += async (_, _) =>
            {
                _bossUploadTimer.Stop();
                await UploadKilledBossesAsync();
            };
        }

        // Restart the 4 s wait only when something new was queued. This runs on every poll tick;
        // restarting it on each of them (while a boss was waiting) pushed the upload back forever,
        // so the automatic upload fired only now and then.
        if (added || !_bossUploadTimer.IsEnabled)
        {
            _bossUploadTimer.Stop();
            _bossUploadTimer.Start();
        }
    }

    private async Task UploadKilledBossesAsync()
    {
        var targetIds = _bossesToUpload.ToList();
        _bossesToUpload.Clear();
        AutoUploadLog.Write($"automatic upload starts for {targetIds.Count} boss(es)");
        if (ResolveServerIdentity() is not (string fingerprint, var displayName))
        {
            AutoUploadLog.Write("server not identified - nothing sent");
            ShowUploadStatus(ServerNotIdentified);
            return;
        }

        // The payload is built from the rows the grid shows for the selected boss (as in
        // OnUploadLastRunClicked), so the selection is pointed at the dead boss for a moment and put
        // back afterwards.
        int? previousTarget = _selectedTargetId;
        DateTime? previousRunWindowStart = _selectedRunWindowStart;
        DateTime? previousRunWindowEnd = _selectedRunWindowEnd;
        int uploaded = 0;
        var justUploaded = new List<JustUploaded>();
        string? lastError = null;
        foreach (int targetId in targetIds)
        {
            if (IsWorldBossTarget(targetId))
            {
                await WaitForCompleteWorldBossAsync(targetId);
            }

            _selectedTargetId = targetId;
            _selectedRunWindowStart = null;
            _selectedRunWindowEnd = null;
            RefreshRows();
            var payload = BuildEncounterUpload(targetId, fingerprint, displayName);
            if (payload is null)
            {
                AutoUploadLog.Write($"boss {targetId}: no payload (no hits, unknown boss or no own character)");
                continue;
            }

            // The very same fight (byte for byte) is never sent a second time: it adds nothing, and the server would turn it away anyway.
            string payloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(payload))));
            if (_sentBossPayloads.Contains(payloadHash))
            {
                AutoUploadLog.Write($"boss {targetId} '{payload.BossNpcName}': identical to the one sent before, not sent again");
                continue;
            }

            UploadResult result = await UploadClient.SendAsync(payload);
            AutoUploadLog.Write($"boss {targetId} '{payload.BossNpcName}': {(result.Success ? "uploaded" : "failed: " + result.Error)}");
            if (result.Success)
            {
                await SendReplayAfterAsync(payload, result, fingerprint);
                _sentBossPayloads.Add(payloadHash);
                uploaded++;
                if (UploadedFight(payload, result) is { } fight)
                {
                    justUploaded.Add(fight);
                }
            }
            else
            {
                lastError = result.Error;
            }
        }

        _selectedTargetId = previousTarget;
        _selectedRunWindowStart = previousRunWindowStart;
        _selectedRunWindowEnd = previousRunWindowEnd;
        RefreshRows();

        if (uploaded > 0)
        {
            string said = uploaded == 1 ? "Boss fight uploaded." : $"{uploaded} boss fights uploaded.";
            string? page = uploaded == 1 ? UploadClient.LastFightUrl : null;
            ShowUploadStatus(said, page);
            await FetchRanksAsync(justUploaded);
        }
        else if (lastError is not null)
        {
            ShowUploadStatus($"Upload failed: {lastError}");
        }
    }

    private async Task UploadOwnProfileAsync()
    {
        if (!UploadClient.TakesProfiles)
        {
            return;
        }

        if (_source?.Entities is not Aion2.Aion2EntityDirectory { LocalCharacter: { Restored: false } own } directory
            || directory.LocalEquipment.Count == 0)
        {
            return;
        }

        if (ResolveServerIdentity() is not (string fingerprint, var displayName))
        {
            if (!_profileUploadHintShown)
            {
                _profileUploadHintShown = true;
                ShowUploadStatus(ServerNotIdentified);
            }

            return;
        }

        ProfilesUploadRequest? payload = BuildProfilesUpload(fingerprint, displayName, ownOnly: true);
        if (payload is null)
        {
            return;
        }

        string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(payload.Participants))));
        if (hash == _lastProfileUploadHash)
        {
            return;
        }

        UploadResult result = await UploadClient.SendProfilesAsync(payload);
        if (result.Success)
        {
            _lastProfileUploadHash = hash;
            ShowUploadStatus($"Profile of {own.Name} uploaded ({displayName}).");
        }
        else
        {
            ShowUploadStatus($"Profile upload failed: {result.Error}");
        }
    }

    // ---- the character windows of other players go online by themselves ---------------------------
    // Per the user: a player whose character window was opened (an arena opponent, someone met on the road) is meant to be found on the site, and
    // having to remember a manual upload before the next restart lost him (one such player was lost with the crash that followed). Only with the
    // settings "profiles of other players" and "upload my profile" both on; every window goes once. One the community server would turn away
    // (no faction or server known yet) waits and is tried again with the next window or the next character record.

    // ---- the players met in an arena go online by themselves once the fight is over -------------------
    // Per the user: the opponents of an arena fight are meant to be on the site (and NC's page fills their profile there), without a manual upload.
    // The 10 against 10 arena sends only name, class, faction, server and legion (see NameOnlyProfile). Same settings as the looked-at windows.

    private int _lastZoneSeen;
    private System.Windows.Threading.DispatcherTimer? _arenaUploadTimer;
    private bool _arenaUploadBusy;

    private void NoteArenaLeft(CombatBatch batch)
    {
        foreach (ZoneChange zone in batch.ZoneChanges)
        {
            if (Aion2.Protocol.Aion2Zones.IsArena(_lastZoneSeen) && !Aion2.Protocol.Aion2Zones.IsArena(zone.ZoneId))
            {
                ScheduleArenaUpload();
            }

            _lastZoneSeen = zone.ZoneId;
        }
    }

    private void ScheduleArenaUpload()
    {
        if (Headless)
        {
            return;
        }

        var settings = MeterSettings.Load();
        if (!settings.AutoUploadProfile || !settings.UploadOtherPlayersProfiles)
        {
            return;
        }

        if (_arenaUploadTimer is null)
        {
            _arenaUploadTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _arenaUploadTimer.Tick += async (_, _) =>
            {
                _arenaUploadTimer.Stop();
                await UploadArenaPlayersAsync();
            };
        }

        _arenaUploadTimer.Stop();
        _arenaUploadTimer.Start();
    }

    private async Task UploadArenaPlayersAsync()
    {
        var settings = MeterSettings.Load();
        if (!UploadClient.TakesProfiles || _arenaUploadBusy || !settings.AutoUploadProfile || !settings.UploadOtherPlayersProfiles
            || _source?.Entities is not Aion2.Aion2EntityDirectory { LocalCharacter: { Restored: false } } directory
            || directory.LocalEquipment.Count == 0
            || ResolveServerIdentity() is not (string fingerprint, var displayName))
        {
            return;
        }

        IReadOnlySet<string> pending = directory.ArenaNamesToUpload();
        if (pending.Count == 0 || BuildProfilesUpload(fingerprint, displayName, onlyNames: pending) is not { } request)
        {
            return;
        }

        // The server turns away a player without faction or server; those stay pending (the next arena or the next character record tries again).
        var ready = request.Participants.Where(p => p.IsSelf || (p.Faction.Length > 0 && p.ServerId is > 0)).ToList();
        var names = ready.Where(p => !p.IsSelf).Select(p => p.Name).ToList();
        if (names.Count == 0)
        {
            return;
        }

        _arenaUploadBusy = true;
        try
        {
            UploadResult result = await UploadClient.SendProfilesAsync(request with { Participants = ready });
            if (result.Success)
            {
                directory.MarkArenaUploaded(names);
                AutoUploadLog.Write($"profile upload of the arena players: {names.Count} ({string.Join(", ", names.Take(25))})");
            }
            else
            {
                AutoUploadLog.Write($"profile upload of the arena players failed: {result.Error}");
            }
        }
        finally
        {
            _arenaUploadBusy = false;
        }
    }

    private System.Windows.Threading.DispatcherTimer? _inspectedUploadTimer;
    private bool _inspectedUploadBusy;

    private void ScheduleInspectedUpload()
    {
        if (Headless)
        {
            return;
        }

        var settings = MeterSettings.Load();
        if (!settings.AutoUploadProfile || !settings.UploadOtherPlayersProfiles)
        {
            return;
        }

        if (_inspectedUploadTimer is null)
        {
            _inspectedUploadTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _inspectedUploadTimer.Tick += async (_, _) =>
            {
                _inspectedUploadTimer.Stop();
                await UploadInspectedAsync();
            };
        }

        _inspectedUploadTimer.Stop();
        _inspectedUploadTimer.Start();
    }

    private async Task UploadInspectedAsync()
    {
        var settings = MeterSettings.Load();
        if (!UploadClient.TakesProfiles || _inspectedUploadBusy || !settings.AutoUploadProfile || !settings.UploadOtherPlayersProfiles
            || _source?.Entities is not Aion2.Aion2EntityDirectory { LocalCharacter: { Restored: false } } directory
            || directory.LocalEquipment.Count == 0
            || ResolveServerIdentity() is not (string fingerprint, var displayName))
        {
            return;
        }

        // The windows not yet sent (a newer window of the same name counts as new); older ones than a day are left alone, so the first start
        // after an update does not send what was looked at weeks ago.
        var pending = directory.InspectedPlayers()
            .Where(p => p.UploadedAt is null && p.ReceivedAt > DateTime.Now.AddHours(-24))
            .ToDictionary(p => p.Name, p => p.ReceivedAt, StringComparer.Ordinal);
        if (pending.Count == 0)
        {
            return;
        }

        ProfilesUploadRequest? request = BuildProfilesUpload(fingerprint, displayName, onlyNames: pending.Keys.ToHashSet(StringComparer.Ordinal));
        if (request is null)
        {
            return;
        }

        // The server turns away a player without faction or server; those stay pending.
        var ready = request.Participants.Where(p => p.IsSelf || (p.Faction.Length > 0 && p.ServerId is > 0)).ToList();
        var names = ready.Where(p => !p.IsSelf).Select(p => p.Name).ToList();
        if (names.Count == 0)
        {
            return;
        }

        _inspectedUploadBusy = true;
        try
        {
            UploadResult result = await UploadClient.SendProfilesAsync(request with { Participants = ready });
            if (result.Success)
            {
                foreach (string name in names)
                {
                    directory.MarkInspectedUploaded(name, pending[name]);
                }

                ShowUploadStatus($"Profile of {string.Join(", ", names)} uploaded.");
                AutoUploadLog.Write($"profile upload of looked-at players: {string.Join(", ", names)}");
            }
            else
            {
                AutoUploadLog.Write($"profile upload of looked-at players failed: {result.Error}");
            }
        }
        finally
        {
            _inspectedUploadBusy = false;
        }
    }

    /// <summary>What goes online of a player met in the 10 against 10 arena: nothing of his level, gear, skills or boards (the server drops a profile that
    /// carries nothing and keeps what it knows). The name, class, faction, server and legion travel with the participant.</summary>
    internal static ProfileUpload NameOnlyProfile() =>
        new("seen", null, null, null, Array.Empty<ProfileGearUpload>(), Array.Empty<ProfileSkillUpload>(), Array.Empty<ProfileBoardUpload>());

    private ProfilesUploadRequest? BuildProfilesUpload(string fingerprint, string? displayName, bool ownOnly = false, IReadOnlySet<string>? onlyNames = null)
    {
        if (_source?.Entities is not Aion2.Aion2EntityDirectory directory || directory.LocalCharacter is not { } local)
        {
            return null;
        }

        // Other players' profiles only go online when the player agreed to it (Settings / first-start wizard).
        ownOnly = ownOnly || !MeterSettings.Load().UploadOtherPlayersProfiles;
        var participants = new List<ProfileParticipantUpload>();
        void Add(int id, string name, string className, bool isSelf)
        {
            if (BuildProfileUpload(id) is { } profile)
            {
                // A player first met in the 10 against 10 arena: the game evens everybody out there, so only name, class, server and legion go online.
                profile = !isSelf && directory.MetInEqualizedArena(name) ? NameOnlyProfile() : profile;
                participants.Add(new ProfileParticipantUpload(name, className, directory.FactionOf(id) ?? "", isSelf, directory.GuildOf(id, name), profile, directory.ServerIdOf(id), CharacterIdFor(directory, name, directory.ServerIdOf(id))));
            }
        }

        string localClass = directory.ClassOf(local.CombatId) ?? Aion2.Protocol.Aion2SkillNames.ClassFromCode(local.ClassCode) ?? "";
        if (localClass.Length == 0)
        {
            return null;
        }

        Add(local.CombatId, local.Name, localClass, true);

        // Character windows of other players the local player opened (see Aion2FrameDecoder.DecodeInspect):
        // level and the full equipment with enchants, richer than what the "appeared" frame shows, so they
        // win over the seen profile of the same name.
        var inspectedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var inspected in ownOnly ? Array.Empty<Aion2.Aion2InspectedPlayer>() : directory.InspectedPlayers())
        {
            string? inspectedClass = Aion2.Protocol.Aion2SkillNames.ClassFromCode(inspected.ClassCode);
            if (inspectedClass is null || inspected.Name == local.Name || (onlyNames is not null && !onlyNames.Contains(inspected.Name)))
            {
                continue;
            }

            inspectedNames.Add(inspected.Name);
            participants.Add(new ProfileParticipantUpload(
                inspected.Name,
                inspectedClass,
                directory.FactionOfName(inspected.Name) ?? inspected.Faction ?? "",
                false,
                inspected.Guild,
                directory.MetInEqualizedArena(inspected.Name) ? NameOnlyProfile() : new ProfileUpload(
                    "seen",
                    inspected.Level,
                    Aion2.Protocol.Aion2SkillNames.ClassIdOfCode(inspected.ClassCode),
                    Aion2.Protocol.Aion2SkillNames.ClassBitsOfCode(inspected.ClassCode),
                    inspected.Gear.Select(GearUpload).ToList(),
                    Array.Empty<ProfileSkillUpload>(),
                    Array.Empty<ProfileBoardUpload>(),
                    GearScore: inspected.GearScore > 0 ? inspected.GearScore : null,
                    Titles: inspected.Titles?.Select(t => new ProfileTitleUpload(t.Slot, t.TitleId)).ToList(),
                    Pets: inspected.Pets?.Select(k => new ProfilePetUpload(k.SpeciesId, k.Level, k.Kinds)).ToList(),
                    BoardCounts: inspected.BoardCounts?.Select(b => new ProfileBoardCountUpload(b.BoardId, b.Count)).ToList()),
                directory.ServerIdOfName(inspected.Name) ?? inspected.ServerId));
        }

        foreach (int id in ownOnly ? Array.Empty<int>() : directory.SeenProfileIds())
        {
            string? name = directory.NameFor(id);
            if (onlyNames is not null && (name is null || !onlyNames.Contains(name)))
            {
                continue;
            }

            string? className = directory.ClassOf(id)
                ?? (directory.SeenProfileOf(id) is { ClassId: int classId } ? Aion2.Protocol.Aion2SkillNames.ClassFromCode(classId * 4 + 1) : null);
            // Anonymous "Player #id" entries have no name to attach a profile to.
            if (id == local.CombatId || name is null || name.StartsWith("Player #") || className is null || inspectedNames.Contains(name))
            {
                continue;
            }

            Add(id, name, className, false);
        }

        // The other characters of the uploader's own account (the character list of the login): the basics only - class, level, faction, legion, server and
        // the character id the game's own search knows; the server fetches the rest from there. They are the uploader's own characters, so they go with the own profile.
        foreach (Aion2.AccountCharacter alt in onlyNames is not null ? Array.Empty<Aion2.AccountCharacter>() : directory.AccountCharacters())
        {
            if (alt.Name == local.Name || participants.Any(p => p.Name == alt.Name))
            {
                continue;
            }

            string? altClass = Aion2.Protocol.Aion2SkillNames.ClassFromCode(alt.ClassCode);
            if (altClass is null || alt.ServerId <= 0)
            {
                continue;
            }

            participants.Add(new ProfileParticipantUpload(
                alt.Name,
                altClass,
                alt.Faction == 2 ? "Asmodian" : "Elyos",
                false,
                alt.Legion,
                new ProfileUpload(
                    "seen",
                    alt.Level,
                    Aion2.Protocol.Aion2SkillNames.ClassIdOfCode(alt.ClassCode),
                    Aion2.Protocol.Aion2SkillNames.ClassBitsOfCode(alt.ClassCode),
                    Array.Empty<ProfileGearUpload>(),
                    Array.Empty<ProfileSkillUpload>(),
                    Array.Empty<ProfileBoardUpload>()),
                alt.ServerId,
                alt.CharacterId > 0 ? alt.CharacterId : null));
        }

        // The own legion's list of the login (frame 0x058a): the game's own record of each member's name, class, level and server - with the legion's name, so the legion's
        // page fills without anybody having to meet the members. Only with the setting for other players' profiles (ownOnly is on without it).
        if (!ownOnly && onlyNames is null)
        {
            string? ownLegion = directory.GuildOf(local.CombatId, local.Name) ?? directory.AccountLegionOf(local.Name);
            foreach (Aion2.LegionMember member in directory.LegionRoster().Take(40))
            {
                string? memberClass = Aion2.Protocol.Aion2SkillNames.ClassFromCode(member.ClassCode);
                if (memberClass is null || member.Name == local.Name || participants.Any(p => p.Name == member.Name && p.ServerId == member.ServerId))
                {
                    continue;
                }

                participants.Add(new ProfileParticipantUpload(
                    member.Name,
                    memberClass,
                    member.ServerId / 1000 == 2 ? "Asmodian" : "Elyos",
                    false,
                    ownLegion,
                    new ProfileUpload(
                        "seen",
                        member.Level > 0 ? member.Level : null,
                        Aion2.Protocol.Aion2SkillNames.ClassIdOfCode(member.ClassCode),
                        Aion2.Protocol.Aion2SkillNames.ClassBitsOfCode(member.ClassCode),
                        Array.Empty<ProfileGearUpload>(),
                        Array.Empty<ProfileSkillUpload>(),
                        Array.Empty<ProfileBoardUpload>()),
                    member.ServerId,
                    member.CharacterId > 0 ? member.CharacterId : null));
            }
        }

        return participants.Count == 0
            ? null
            : new ProfilesUploadRequest(AppVersion.Text, fingerprint, displayName, participants.Take(60).ToList(), ServerId: Aion2OwnServerId(), GameServer: Aion2GameServerAddress());
    }

    /// <summary>
    /// Uploads every boss the Mob/Boss filter has accumulated since the last Clear, one request
    /// each - the server recognizes fights uploaded by different group members as the same run
    /// itself (see Backend/src/matching/merge.ts), so this does not need to know whether anyone
    /// else in the group already uploaded. Temporarily drives the same _selectedTargetId/RefreshRows
    /// state the filter dropdown itself uses for each boss in turn, then restores whatever the user
    /// had selected - visibly flipping the grid through each boss while it runs, which is expected
    /// for a menu action the user triggered on purpose (not a background operation).
    /// </summary>
    /// <summary>
    /// "Upload for the replay": sends the boss fight on screen (so the server has it and names it) and then the paths of the replay map - the own character's, the boss's and the other monsters', and the other
    /// players' only with the switch "data of other players". The other clients of the fight can fetch what was sent in the replay window.
    /// </summary>
    private async void OnUploadReplayClicked(object sender, RoutedEventArgs e)
    {
        if (!PrivacyConsent.EnsureForUploadSwitch(this))
        {
            return;
        }

        if (_selectedTargetId is not int targetId || ResolveServerIdentity() is not (string fingerprint, var displayName))
        {
            ShowUploadStatus(_selectedTargetId is null ? "Pick a boss fight first." : ServerNotIdentified);
            return;
        }

        EncounterUploadRequest? fight = BuildEncounterUpload(targetId, fingerprint, displayName);
        DateTime? from = _aggregator.Events.Count > 0 ? _aggregator.Events.Min(ev => ev.Timestamp) : null;
        DateTime? to = _aggregator.Events.Count > 0 ? _aggregator.Events.Max(ev => ev.Timestamp) : null;
        FightExtras? extras = from is DateTime f && to is DateTime t ? ReplayDirectory?.Combat.Slice(f, t, ResolveDisplayName, ReplayKeeps, ReplayHp) : null;
        ReplayData data = BuildReplayData(extras, from, to, _aggregator.Events);
        if (fight is null || !data.HasData)
        {
            ShowUploadStatus(LocalizationManager.Instance["Main.UploadReplay.None"]);
            return;
        }

        ShowUploadStatus("Uploading...");
        UploadResult sent = await UploadClient.SendAsync(fight);
        if (!sent.Success)
        {
            ShowUploadStatus($"Upload failed: {sent.Error}");
            return;
        }

        if (sent.EncounterId is null)
        {
            ShowUploadStatus(LocalizationManager.Instance["Main.UploadReplay.Held"]);
            return;
        }

        var request = new ReplayUploadRequest(AppVersion.Text, fingerprint, sent.EncounterId, fight.BossNpcId, fight.StartedAt.ToUniversalTime(), fight.EndedAt.ToUniversalTime(),
            ReplayUploadBuilder.Tracks(data, MeterSettings.Load().UploadOtherPlayersProfiles));
        UploadResult replay = await UploadClient.SendReplayAsync(request);
        ShowUploadStatus(replay.Success ? string.Format(LocalizationManager.Instance["Main.UploadReplay.Done"], request.Tracks.Count) : $"Upload failed: {replay.Error}");
    }

    /// <summary>
    /// After a boss upload (Settings' <see cref="MeterSettings.UploadReplay"/>): the positions of that fight for the replay on its page, the same paths as "Upload for the
    /// replay" sends (other players' only with "data of other players"). Taken over the fight's own time from the boss shown now (the upload loops show each in turn).
    /// A replay that did not go only misses on the site: the fight is there either way.
    /// </summary>
    private async Task SendReplayAfterAsync(EncounterUploadRequest fight, UploadResult sent, string fingerprint)
    {
        var settings = MeterSettings.Load();
        if (sent.EncounterId is null || !settings.UploadReplay)
        {
            return;
        }

        try
        {
            DateTime from = fight.StartedAt.ToLocalTime(), to = fight.EndedAt.ToLocalTime();
            var events = _aggregator.Events.Where(ev => ev.Timestamp >= from && ev.Timestamp <= to).ToList();
            FightExtras? extras = ReplayDirectory?.Combat.Slice(from, to, ResolveDisplayName, ReplayKeeps, ReplayHp);
            ReplayData data = BuildReplayData(extras, from, to, events);
            if (!data.HasData)
            {
                return;
            }

            var tracks = ReplayUploadBuilder.Tracks(data, settings.UploadOtherPlayersProfiles);
            if (tracks.Count > 0)
            {
                await UploadClient.SendReplayAsync(new ReplayUploadRequest(AppVersion.Text, fingerprint, sent.EncounterId, fight.BossNpcId, fight.StartedAt.ToUniversalTime(), fight.EndedAt.ToUniversalTime(), tracks));
            }
        }
        catch (Exception ex)
        {
            AutoUploadLog.Write("replay: " + ex.Message); // the replay is extra: whatever went wrong with it, the upload loop goes on
        }
    }

    private async void OnUploadLastRunClicked(object sender, RoutedEventArgs e)
    {
        if (!PrivacyConsent.EnsureForUploadSwitch(this))
        {
            return;
        }

        AppMenu.IsSubmenuOpen = false;
        int? previousTarget = _selectedTargetId;
        DateTime? previousRunWindowStart = _selectedRunWindowStart;
        DateTime? previousRunWindowEnd = _selectedRunWindowEnd;
        // The full, unfiltered set, not whatever the dropdown happens to be showing under an
        // active search right now (see _mobBossEntries's own remarks) - a stale search must never
        // silently shrink how many fights this uploads.
        var targetIds = _mobBossEntries.Select(entry => entry.TargetId).ToList();

        if (targetIds.Count == 0)
        {
            ShowUploadStatus("No boss fights recorded since the last Clear.");
            return;
        }

        if (ResolveServerIdentity() is not (string fingerprint, var displayName))
        {
            ShowUploadStatus(ServerNotIdentified);
            return;
        }

        ShowUploadStatus($"Uploading {targetIds.Count} boss fight(s)...");
        int uploaded = 0;
        string? lastError = null;
        bool first = true;
        foreach (int targetId in targetIds)
        {
            // A big replay can surface dozens of distinct bosses/mobs at once -
            // found live when a real batch this size tripped the backend's own upload rate limit
            // (see Backend/src/server.ts). A small gap between requests keeps even a very large
            // batch comfortably under it instead of firing every request back-to-back.
            if (!first)
            {
                await Task.Delay(100);
            }
            first = false;

            // Full history, not whatever specific run happened to be selected in the UI before
            // this ran - "upload every boss since Clear" means every kill of each one, combined.
            _selectedTargetId = targetId;
            _selectedRunWindowStart = null;
            _selectedRunWindowEnd = null;
            RefreshRows();
            var payload = BuildEncounterUpload(targetId, fingerprint, displayName);
            if (payload is null)
            {
                continue;
            }

            UploadResult result = await UploadClient.SendAsync(payload);
            if (result.Success)
            {
                await SendReplayAfterAsync(payload, result, fingerprint);
                uploaded++;
            }
            else
            {
                lastError = result.Error;
            }
        }

        _selectedTargetId = previousTarget;
        _selectedRunWindowStart = previousRunWindowStart;
        _selectedRunWindowEnd = previousRunWindowEnd;
        RefreshRows();

        ShowUploadStatus(uploaded > 0
            ? $"Uploaded {uploaded} of {targetIds.Count} boss fight(s)."
            : $"Upload failed: {lastError}");
    }

    private void ShowUploadStatus(string message, string? url = null)
    {
        _uploadStatusUrl = url;
        UploadStatusText.Text = url is null ? message : message + " Click to open it.";
        UploadStatusText.Cursor = url is not null || message.Contains("Sign in first", StringComparison.Ordinal) ? System.Windows.Input.Cursors.Hand : null;
        UploadStatusText.TextDecorations = url is not null ? System.Windows.TextDecorations.Underline : null;
        UploadStatusText.Visibility = Visibility.Visible;
    }

    private void OnClassFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // Same ordering trap as OnMobBossFilterChanged -- ClassFilter's SelectedIndex="0" fires
        // this from inside InitializeComponent, before the rest of the tree exists.
        if (!IsInitialized)
        {
            return;
        }

        _selectedClassFilter = (ClassFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        RefreshRows();
    }

    private void OnLoadSessionClicked(object sender, RoutedEventArgs e)
    {
        AppMenu.IsSubmenuOpen = false;
        Directory.CreateDirectory(SessionFile.DefaultDirectory);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            InitialDirectory = SessionFile.DefaultDirectory,
            Filter = $"Aion DPS session (*{SessionFile.Extension})|*{SessionFile.Extension}",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SessionFile.LoadedSession session = SessionFile.Load(dialog.FileName);
            LoadSessionIntoMeter(session);
            ShowUploadStatus($"Loaded session from {session.SavedAt:g} ({session.Events.Count} event(s)).");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            ThemedMessageBox.Show(this, $"Could not load this session file.\n\n{ex.Message}", "Load Session",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Per the user: Load/Save Session are a portable, manual snapshot of the WHOLE
    /// current session (every damage/avoid/kill event, plus personal stat totals) - independent
    /// of the live capture, and deliberately separate from Fight History (FightStore), which auto-records
    /// individual finished fights on its own. Loot isn't included yet - see SessionFile's own
    /// remarks on why.</summary>
    private void OnSaveSessionClicked(object sender, RoutedEventArgs e)
    {
        AppMenu.IsSubmenuOpen = false;
        if (_aggregator.Events.Count == 0)
        {
            ShowUploadStatus("Nothing to save yet - no damage has been recorded this session.");
            return;
        }

        Directory.CreateDirectory(SessionFile.DefaultDirectory);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = SessionFile.DefaultDirectory,
            FileName = $"Session {DateTime.Now:yyyy-MM-dd HH-mm-ss}{SessionFile.Extension}",
            Filter = $"Aion DPS session (*{SessionFile.Extension})|*{SessionFile.Extension}",
            DefaultExt = SessionFile.Extension,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        Dictionary<int, string> names = CollectSessionNames();

        try
        {
            SessionFile.Save(dialog.FileName, _aggregator.Events, _avoids, _kills, names);
            ShowUploadStatus($"Session saved to {Path.GetFileName(dialog.FileName)}.");
        }
        catch (IOException ex)
        {
            ThemedMessageBox.Show(this, $"Could not save the session.\n\n{ex.Message}", "Save Session",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Every object id the events/avoids/kills can reference, resolved to a real name now while the
    /// live source can still answer it - a session file has no other way to carry identity.</summary>
    private Dictionary<int, string> CollectSessionNames()
    {
        var ids = new HashSet<int>();
        foreach (DamageEvent ev in _aggregator.Events)
        {
            ids.Add(ev.SourceObjectId);
            ids.Add(ev.TargetObjectId);
        }

        foreach (AvoidEvent av in _avoids)
        {
            ids.Add(av.SourceObjectId);
            ids.Add(av.TargetObjectId);
        }

        foreach (KillEvent k in _kills)
        {
            ids.Add(k.VictimObjectId);
            if (k.KillerObjectId is int killer)
            {
                ids.Add(killer);
            }
        }

        return ids.ToDictionary(id => id, ResolveDisplayName);
    }

    // ---- the session survives a restart of the client ---------------------------------------------
    // Per the user: on a restart (the update's "restart now", or App > Restart client) the running session
    // is written to a temp file and read back on the next start. Unlike a saved session file this goes into
    // the LIVE meter (the capture keeps running), and it carries what the directory had learned, since the
    // game does not announce players again who are already in view.

    private static string RestartSnapshotPath => Path.Combine(Path.GetTempPath(), "AionDPS", "restart-session.aiondps");

    /// <summary>A snapshot older than this is a leftover of some earlier run, not a restart.</summary>
    private static readonly TimeSpan RestartSnapshotMaxAge = TimeSpan.FromMinutes(15);

    private void SaveRestartSnapshot()
    {
        if (_historyMode || _aggregator.Events.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RestartSnapshotPath)!);
            SessionFile.Save(RestartSnapshotPath, _aggregator.Events, _avoids, _kills, CollectSessionNames(),
                (_source?.Entities as Aion2EntityDirectory)?.Snapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A restart must never be blocked by a session that could not be kept.
        }
    }

    private void TryRestoreRestartSnapshot()
    {
        string path = RestartSnapshotPath;
        if (Headless || !File.Exists(path))
        {
            return;
        }

        try
        {
            SessionFile.LoadedSession session = SessionFile.Load(path);
            File.Delete(path);
            if (DateTime.Now - session.SavedAt > RestartSnapshotMaxAge || session.Events.Count == 0)
            {
                return;
            }

            if (session.Directory is { } directory && _source?.Entities is Aion2EntityDirectory live)
            {
                live.RestoreFrom(directory);
            }

            _avoids.AddRange(session.Avoids);
            _kills.AddRange(session.Kills);
            _aggregator.IngestEvents(session.Events);
            _sessions.Feed(new CombatBatch(session.Events, Array.Empty<AvoidEvent>(), Array.Empty<KillEvent>()));
            RefreshRows();
            ShowUploadStatus($"Resumed {session.Events.Count:N0} event(s) from before the restart.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            // Unreadable leftover: ignore it, the meter starts fresh.
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>App &gt; Restart client: keeps the session, starts a fresh process, ends this one.</summary>
    private void OnRestartClientClicked(object sender, RoutedEventArgs e)
    {
        AppMenu.IsSubmenuOpen = false;
        SaveRestartSnapshot();
        SaveWindowStateToSettings();
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    /// <summary>Replays a loaded session the same way EnterHistoryMode replays one stored fight -
    /// a FakeCombatSource so the live capture doesn't interfere, object ids remapped through
    /// it by name so PlayerRow/aggregator identity works exactly like a live session's would.
    /// Restores avoids/kills too (EnterHistoryMode doesn't - FightStore's own single-fight replay
    /// never needed defense/PVP stats to survive a reload) and re-runs class detection from each
    /// event's own Skill field (OnSkillUsed) since a session file has no separate per-participant
    /// class table the way FightStore's SQLite schema does.</summary>
    private void LoadSessionIntoMeter(SessionFile.LoadedSession session)
    {
        _historyMode = true;

        var replay = new FakeCombatSource();
        var idMap = session.Names.ToDictionary(kv => kv.Key, kv => replay.Entities.GetOrAssignId(kv.Value));
        int Remap(int id) => idMap.GetValueOrDefault(id, id);

        ShowReplay(replay);
        ClearDamageData();

        var events = session.Events
            .Select(ev => ev with { SourceObjectId = Remap(ev.SourceObjectId), TargetObjectId = Remap(ev.TargetObjectId) })
            .ToList();
        _avoids.AddRange(session.Avoids.Select(a => a with { SourceObjectId = Remap(a.SourceObjectId), TargetObjectId = Remap(a.TargetObjectId) }));
        _kills.AddRange(session.Kills.Select(k => k with
        {
            KillerObjectId = k.KillerObjectId is int killerId ? Remap(killerId) : null,
            VictimObjectId = Remap(k.VictimObjectId),
        }));

        _aggregator.IngestEvents(events);

        HistoryBanner.Text = string.Format(LocalizationManager.Instance["Main.SessionBanner"], session.SavedAt.ToString("g"));
        HistoryBanner.Visibility = Visibility.Visible;
        RefreshRows();
        Activate();
    }

    private const int RecentSessionsShown = 5;

    /// <summary>Fills the Session menu each time it opens: "All", the stay the player is in, and the last few finished ones.</summary>
    private void OnSessionMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SessionMenu))
        {
            return;
        }

        LocalizationManager loc = LocalizationManager.Instance;
        bool live = ViewingLive;
        SessionMenu.Items.Clear();
        SessionMenu.Items.Add(SessionMenuItem(loc["Sessions.All"], loc["Sessions.AllHint"], null, live && _sessionView == SessionView.All,
            () => ShowLiveViewFromMenu(SessionView.All)));
        if (_sessions.Current is { } now)
        {
            SessionMenu.Items.Add(SessionMenuItem(SessionLabels.NameOf(now), SessionLabels.TimeSpanOf(now), SessionLabels.ColorOf(now.Kind),
                live && _sessionView == SessionView.Current, () => ShowLiveViewFromMenu(SessionView.Current)));
        }

        SessionMenu.Items.Add(new Separator());
        SessionMenu.Items.Add(new MenuItem { Header = loc["Sessions.Recent"], IsEnabled = false });
        var recent = _sessions.Sessions.Where(session => !session.IsOpen).Reverse().Take(RecentSessionsShown).ToList();
        if (recent.Count == 0)
        {
            SessionMenu.Items.Add(new MenuItem { Header = loc["Sessions.None"], IsEnabled = false });
        }

        foreach (SessionInfo session in recent)
        {
            SessionInfo shown = session;
            SessionMenu.Items.Add(SessionMenuItem(SessionLabels.NameOf(session), SessionLabels.TimeSpanOf(session), SessionLabels.ColorOf(session.Kind),
                _historyMode && _viewedSessionNumber == session.Number, () => ShowPastSession(shown)));
        }

        SessionMenu.Items.Add(new Separator());
        var manager = new MenuItem { Header = loc["Sessions.OpenManager"] };
        manager.Click += (_, _) => OpenSessionManager();
        SessionMenu.Items.Add(manager);
        var folder = new MenuItem { Header = loc["Main.MenuApp.OpenSessionsFolder"] };
        folder.Click += OnOpenSessionsFolderClicked;
        SessionMenu.Items.Add(folder);
    }

    private DummyTestWindow? _dummyWindow;
    private ThemeEditorWindow? _themeEditorWindow;

    private void OnOpenThemeEditorClicked(object sender, RoutedEventArgs e)
    {
        if (_themeEditorWindow is not null)
        {
            _themeEditorWindow.Activate();
            return;
        }

        _themeEditorWindow = new ThemeEditorWindow(MeterSettings.Load()) { Owner = this };
        _themeEditorWindow.Saved += settings =>
        {
            var current = MeterSettings.Load();
            current.Theme = settings.Theme;
            current.Save();
            ApplySettingsEverywhere(current);
        };
        _themeEditorWindow.Closed += (_, _) => _themeEditorWindow = null;
        _themeEditorWindow.Show();
    }
    private ChecklistWindow? _checklistWindow;

    private void OnOpenChecklistClicked(object sender, RoutedEventArgs e) => OpenChecklist(false);

    /// <summary>The checklist window (Odyle Energy, daily and weekly counters); <paramref name="roster"/>: on the page of all characters.</summary>
    private void OpenChecklist(bool roster)
    {
        if (_shellBuilt)
        {
            ShowClient();
            ShowShellPage("characters");
            return;
        }

        if (_checklistWindow is null)
        {
            _checklistWindow = new ChecklistWindow { Owner = this };
            _checklistWindow.Closed += (_, _) => _checklistWindow = null;
            _checklistWindow.Show();
        }

        if (roster)
        {
            _checklistWindow.ShowRoster();
        }

        _checklistWindow.Activate();
    }

    private AssistWindow? _assistWindow;

    private void OnOpenAssistClicked(object sender, RoutedEventArgs e)
    {
        if (_assistWindow is not null)
        {
            _assistWindow.Activate();
            return;
        }

        _assistWindow = new AssistWindow(() => _liveSource?.Entities as Aion2EntityDirectory) { Owner = this };
        _assistWindow.Closed += (_, _) => _assistWindow = null;
        _assistWindow.Show();
    }

    /// <summary>Tools menu: the dummy test window (one at a time).</summary>
    private void OnOpenDummyTestClicked(object sender, RoutedEventArgs e)
    {
        if (_dummy is null)
        {
            return;
        }

        if (_dummyWindow is not null)
        {
            _dummyWindow.Activate();
            return;
        }

        _dummyWindow = new DummyTestWindow(_dummy, ToggleAllOverlays, OpenDummyRun) { Owner = this };
        _dummyWindow.Closed += (_, _) => _dummyWindow = null;
        _dummyWindow.Show();
    }

    private void OnOpenSessionManagerClicked(object sender, RoutedEventArgs e) => OpenSessionManager();

    private SessionManagerWindow? _sessionManager;

    private void OpenSessionManager()
    {
        if (_sessionManager is not null)
        {
            _sessionManager.Activate();
            return;
        }

        _sessionManager = new SessionManagerWindow(_sessions) { Owner = this };
        _sessionManager.ExtrasFor = ExtrasForSession;
        _sessionManager.AnalyzerFactory = BuildSessionAnalyzer;
        _sessionManager.DeathsFor = DeathsForSession;
        _sessionManager.Closed += (_, _) => _sessionManager = null;
        _sessionManager.Show();
    }

    private MenuItem SessionMenuItem(string name, string detail, System.Windows.Media.Color? dot, bool isChecked, Action click)
    {
        var grid = new Grid { MinWidth = 260 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (dot is System.Windows.Media.Color color)
        {
            grid.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            var all = new TextBlock { Text = "\u221E", VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(all);
        }

        var nameText = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(nameText, 1);
        grid.Children.Add(nameText);
        var detailText = new TextBlock { Text = detail, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.65 };
        Grid.SetColumn(detailText, 2);
        grid.Children.Add(detailText);

        var item = new MenuItem { Header = grid, IsChecked = isChecked };
        item.Click += (_, _) => click();
        return item;
    }

    private void ShowLiveViewFromMenu(SessionView view)
    {
        if (_historyMode)
        {
            _sessionView = view;
            ExitHistoryMode();
        }
        else
        {
            ShowLiveView(view);
        }
    }

    /// <summary>Puts a finished session into the grid. Like a stored fight it runs on a replay source (ids remapped by name), but the
    /// live capture keeps going and recording behind it; the banner is the way back.</summary>
    private void ShowPastSession(SessionInfo session)
    {
        _historyMode = true;
        _viewedSessionNumber = session.Number;

        var replay = new FakeCombatSource();
        var idMap = session.Names.ToDictionary(kv => kv.Key, kv => replay.Entities.GetOrAssignId(kv.Value));
        int Remap(int id)
        {
            if (!idMap.TryGetValue(id, out int mapped))
            {
                // No name was known for this id: a stand-in name keeps it apart from every real one.
                idMap[id] = mapped = replay.Entities.GetOrAssignId($"0x{id:X8}");
            }

            return mapped;
        }

        ShowReplay(replay);
        ClearDamageData();

        IEnumerable<DamageEvent> events = session.Data.Events.Concat(session.Data.Shields)
            .Select(ev => ev with { SourceObjectId = Remap(ev.SourceObjectId), TargetObjectId = Remap(ev.TargetObjectId) })
            .ToList();
        foreach ((int id, string className) in session.Classes)
        {
            if (session.Names.TryGetValue(id, out string? name))
            {
                _playerIdentities[Remap(id)] = (name, className, 0);
            }
        }

        _aggregator.IngestEvents(events);

        // two live ids with the same name share one replay id (the replay merges by name), so the way back keeps the first of them
        _historyLiveIds = new Dictionary<int, int>();
        foreach (var (live, mapped) in idMap)
        {
            _historyLiveIds.TryAdd(mapped, live);
        }

        _historyReplayIds = new Dictionary<int, int>(idMap);
        _historyExtras = session.Extras ?? ((_liveSource?.Entities as Aion2.Aion2EntityDirectory) is { } liveDir
            ? liveDir.Combat.Slice(session.Start, session.End ?? DateTime.Now, id => liveDir.NameFor(id), ReplayKeeps, ReplayHp)
            : null);

        HistoryBanner.Text = string.Format(LocalizationManager.Instance["Main.SessionViewBanner"], SessionLabels.NameOf(session), SessionLabels.TimeSpanOf(session));
        HistoryBanner.Visibility = Visibility.Visible;
        RefreshRows();
    }

    private void OnOpenSessionsFolderClicked(object sender, RoutedEventArgs e)
    {
        AppMenu.IsSubmenuOpen = false;
        Directory.CreateDirectory(SessionFile.DefaultDirectory);
        Process.Start(new ProcessStartInfo(SessionFile.DefaultDirectory) { UseShellExecute = true });
    }

    /// <summary>The tray icon with its right-click menu; there for as long as the meter runs (not in
    /// the headless test modes, which must not put anything on the user's screen).</summary>
    private void CreateTrayIcon()
    {
        if (Headless)
        {
            return;
        }

        var loc = LocalizationManager.Instance;
        try
        {
            _trayIcon = new TrayIcon(this,
                Path.Combine(AppContext.BaseDirectory, "assets", "app", "aiondps.ico"),
                Brand.AppName + " " + AppVersion.Text,
                new (string?, Action?)[]
                {
                    (loc["Main.Tray.Show"], () => _trayIcon?.Restore()),
                    (loc["Main.Tray.ToggleUi"], ToggleOverlay),
                    (loc["Main.Tray.StreamLink"], CopyStreamLink),
                    (null, null),
                    (loc["Main.Tray.Exit"], Close),
                });
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // No icon file: the meter works without a tray icon.
        }
    }

    private void OnMinimizeToTrayClicked(object sender, RoutedEventArgs e)
    {
        AppMenu.IsSubmenuOpen = false;
        _trayIcon?.HideWindow();
    }

    /// <summary>
    /// Asks GitHub whether a newer release exists and, if so, downloads it in the background and
    /// says so in the status row. Nothing is swapped while the meter runs: Velopack stages the new
    /// version and it becomes active on the next start, so an update never interrupts a fight.
    ///
    /// <paramref name="announceResult"/> separates the two callers. The automatic checks (startup
    /// and the five-minute timer) pass false: they swallow every failure, because a machine that is
    /// offline, or a GitHub that is rate-limiting, would otherwise produce a message box on top of
    /// a boss fight every five minutes. The menu item passes true, because a check the user just
    /// asked for that silently does nothing is indistinguishable from "you are up to date", which
    /// is the one answer it must not fake.
    /// </summary>
    /// <summary>The server will not take uploads from this version: say so once, in the status row and the badge (no pop-up: it came at the worst moments). With "check for
    /// updates" on, the update is fetched now; with it off the meter does not talk to GitHub.</summary>
    private void OnClientOutdated(string requiredVersion)
    {
        var loc = LocalizationManager.Instance;
        ShowUploadStatus(loc["Upload.ClientOutdatedStatus"]);
        _clientOutdated = true;
        RefreshUpdateBadge();
        bool automatic = MeterSettings.Load().CheckForUpdates;
        if (automatic)
        {
            _ = RunUpdateCheck(announceResult: false);
        }
    }

    /// <summary>The server allows this version after all: take the warning back.</summary>
    private void OnClientAllowed()
    {
        _clientOutdated = false;
        UploadStatusText.Visibility = Visibility.Collapsed;
        RefreshUpdateBadge();
    }

    private bool _updateCheckRunning;

    /// <summary>One check at a time: the start-up check, the timer, the menu and a refused upload all come here, and two downloads of the same
    /// update at once would only fight over Velopack's staging folder. A call that arrives while one is running is dropped; the running one does the work.</summary>
    private async Task RunUpdateCheck(bool announceResult, bool askBeforeInstall = false, Action? upToDate = null)
    {
        if (_updateCheckRunning)
        {
            return;
        }

        _updateCheckRunning = true;
        try
        {
            await RunUpdateCheckCore(announceResult, askBeforeInstall, upToDate);
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    /// <summary>The region the timetable was last asked for (only once the own server is known).</summary>
    private string? _scheduleRegion;

    /// <summary>The event timetable from the server (see ScheduleClient); in restricted mode the meter stays with the table it has. The region of the own server
    /// gives the clock of that region; before the own character is known the table kept from the last run stays (or the European one when there is none).</summary>
    private async Task RefreshSchedule()
    {
        if (PrivacyConsent.Restricted)
        {
            return;
        }

        string? region = Aion2OwnServerId() is int serverId ? Aion2Servers.RegionCodeOf(serverId) : null;
        if (region is null && File.Exists(Schedule.ScheduleClient.CachePath))
        {
            return;
        }

        _scheduleRegion = region ?? _scheduleRegion;
        await Schedule.ScheduleClient.RefreshAsync(region);
    }

    private async Task RefreshAnnouncementsBadge()
    {
        if (PrivacyConsent.Restricted)
        {
            return;
        }

        long? latest = await Announcements.AnnouncementsClient.GetLatestAsync();
        if (latest is null)
        {
            return;
        }

        // The first call comes from the constructor, before app.Run: there is no UI synchronization context yet, so the code after the await
        // runs on a pool thread and must not touch the window itself. The dispatcher takes the update and does it once the window runs.
        await Dispatcher.InvokeAsync(() =>
        {
            _latestAnnouncement = latest;
            var settings = MeterSettings.Load();
            if (settings.AnnouncementsSeen == 0)
            {
                // A fresh install (or the first run with this feature) starts from "everything seen": the badge means "something new since you last looked".
                settings.AnnouncementsSeen = latest.Value;
                settings.Save();
            }

            AnnouncementsNewBadge.Visibility = Announcements.AnnouncementsClient.IsNew(latest, settings.AnnouncementsSeen) ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    /// <summary>The newest campaign start the server named last; the click on the Twitch icon stores it as "seen".</summary>
    private long _latestTwitchDrops;

    /// <summary>Shows the Twitch icon while a Twitch Drops campaign runs, with "New" while its start is newer than the one seen at the last click.</summary>
    private async Task RefreshTwitchDropsBadge()
    {
        if (PrivacyConsent.Restricted)
        {
            await Dispatcher.InvokeAsync(() => TwitchDropsButton.Visibility = Visibility.Collapsed);
            return;
        }

        var status = await Announcements.TwitchDropsClient.GetStatusAsync();
        if (status is null)
        {
            return;
        }

        // Same as in RefreshAnnouncementsBadge: the first call is made before app.Run, so the window is only touched on its dispatcher.
        var (running, latest) = status.Value;
        await Dispatcher.InvokeAsync(() =>
        {
            _latestTwitchDrops = latest;
            TwitchDropsButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            TwitchDropsNewBadge.Visibility = Announcements.TwitchDropsClient.IsNew(running, latest, MeterSettings.Load().TwitchDropsSeen) ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void OnTwitchDropsClicked(object sender, RoutedEventArgs e)
    {
        if (_latestTwitchDrops > 0)
        {
            var settings = MeterSettings.Load();
            settings.TwitchDropsSeen = _latestTwitchDrops;
            settings.Save();
        }

        TwitchDropsNewBadge.Visibility = Visibility.Collapsed;
        try
        {
            Process.Start(new ProcessStartInfo(Announcements.TwitchDropsClient.PageUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // no browser to open: nothing else to do
        }
    }

    private void OnAnnouncementsClicked(object sender, RoutedEventArgs e)
    {
        if (_latestAnnouncement is long latest)
        {
            var settings = MeterSettings.Load();
            settings.AnnouncementsSeen = latest;
            settings.Save();
        }

        AnnouncementsNewBadge.Visibility = Visibility.Collapsed;
        try
        {
            Process.Start(new ProcessStartInfo(Announcements.AnnouncementsClient.PageUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // no browser to open: nothing else to do
        }
    }

    private async Task RunUpdateCheckCore(bool announceResult, bool askBeforeInstall = false, Action? upToDate = null)
    {
        // The automatic checks respect the setting; the menu item ignores it, since clicking it IS
        // the consent that setting stands in for.
        if (!announceResult && !MeterSettings.Load().CheckForUpdates)
        {
            return;
        }

        if (!UpdateService.CanUpdate)
        {
            if (announceResult)
            {
                ThemedMessageBox.Show(this,
                    "This copy was not installed by the updater, so it cannot update itself.\n\n" +
                    "That is normal for a build run straight from source or unzipped by hand. " +
                    "Installed copies update themselves silently.\n\n" +
                    UpdateDiagnostics(),
                    "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        VelopackUpdateInfo? update;
        try
        {
            update = await UpdateService.CheckAsync();
        }
        catch (Exception ex)
        {
            if (announceResult)
            {
                ThemedMessageBox.Show(this, $"Could not reach GitHub to check for updates.\n\n{ex.Message}",
                    "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return;
        }

        if (update is null)
        {
            UpdateNotice.Visibility = Visibility.Collapsed;
            SetUpdateStatus(null, null);
            if (upToDate is not null)
            {
                upToDate(); // the title bar's button says it itself, no message box
            }
            else if (announceResult)
            {
                ThemedMessageBox.Show(this, $"You are running the latest version ({AppVersion.Text}).",
                    "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        string version = update.TargetFullRelease.Version.ToString();

        // Downloading the same update again on every timer tick would re-fetch it twelve times an
        // hour for as long as the meter stays open. A newer one than the version waiting replaces
        // it: kept, the older one was what got installed, and the newest then took a second round
        // (0.9.34 -> 0.9.35 -> 0.9.36 on the StacysView fork, with 0.9.36 already out).
        if (_downloadedUpdate is { } staged && staged.TargetFullRelease.Version.CompareTo(update.TargetFullRelease.Version) >= 0)
        {
            if (announceResult)
            {
                OfferRestart(staged, staged.TargetFullRelease.Version.ToString());
            }

            return;
        }

        // The start-up check asks first and downloads only on "Yes" (OnUpdateRestartNowClicked); a "No" leaves the update to the five-minute check.
        if (askBeforeInstall)
        {
            OfferInstall(update, version);
            return;
        }

        UpdateNotice.Text = $"Downloading {version}...";
        UpdateNotice.Visibility = Visibility.Visible;
        SetUpdateStatus(string.Format(LocalizationManager.Instance["Overlay.UpdateDownloading"], version), "↓ " + version);

        try
        {
            await UpdateService.DownloadAsync(update);
        }
        catch (Exception ex)
        {
            UpdateNotice.Visibility = Visibility.Collapsed;
            SetUpdateStatus(null, null);
            if (announceResult)
            {
                ThemedMessageBox.Show(this, $"The update could not be downloaded.\n\n{ex.Message}",
                    "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return;
        }

        _downloadedUpdate = update;
        UpdateNotice.Text = $"Update {version} ready - click to restart";
        SetUpdateStatus(string.Format(LocalizationManager.Instance["Overlay.UpdateReady"], version), "↑ " + version);

        if (announceResult)
        {
            OfferRestart(update, version);
        }
    }

    /// <summary>Where this copy runs and whether the updater sits next to it, so a "not installed by
    /// the updater" message says why (an installed copy keeps Update.exe one folder above its
    /// "current" folder).</summary>
    private static string UpdateDiagnostics()
    {
        string folder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string? parent = Path.GetDirectoryName(folder);
        string updater = parent is null ? "" : Path.Combine(parent, "Update.exe");
        return $"Running from: {folder}\nUpdate.exe expected at: {updater}\n"
            + (File.Exists(updater) ? "Update.exe: found." : "Update.exe: MISSING.");
    }

    private AccountWindow? _accountWindow;

    /// <summary>App menu → Atreia: the sign-in window (one at a time).</summary>
    private void OnAccountClicked(object sender, RoutedEventArgs e)
    {
        AppMenu.IsSubmenuOpen = false;
        if (_accountWindow is { IsLoaded: true })
        {
            _accountWindow.Activate();
            return;
        }

        _accountWindow = new AccountWindow { Owner = this };
        _accountWindow.Closed += (_, _) => _accountWindow = null;
        _accountWindow.Show();
    }

    /// <summary>After an upload the status line links the fight's page: a click opens it in the browser.</summary>
    private void OnUploadStatusClicked(object sender, MouseButtonEventArgs e)
    {
        if (_uploadStatusUrl is { } url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // no browser set up
            }
        }
        else if (!Account.SignedIn && UploadStatusText.Text.Contains("Sign in first", StringComparison.Ordinal))
        {
            OnAccountClicked(sender, e);
        }
    }

    private string? _uploadStatusUrl;

    private void OnCheckForUpdatesClicked(object sender, RoutedEventArgs e)
    {
        AppMenu.IsSubmenuOpen = false;
        _ = RunUpdateCheck(announceResult: true);
    }

    private void OnUpdateNoticeClicked(object sender, MouseButtonEventArgs e)
    {
        if (_downloadedUpdate is { } update)
        {
            OfferRestart(update, update.TargetFullRelease.Version.ToString());
        }
    }

    /// <summary>
    /// The update is already on disk by this point; all that is left is a restart, which is quick
    /// and needs no installer and no elevation. Still asked rather than done: the meter is being
    /// watched during a fight, and deciding on its own to disappear and come back mid-boss is not
    /// its call to make. Declining costs nothing -- the next start offers the newest version (a staged one
    /// is not put in by itself any more: it may be old by then, see Program.Main), and "Restart now" asks
    /// once more for a newer one first (<see cref="RestartIntoNewestAsync"/>).
    ///
    /// <para>
    /// Shown as an in-window, app-themed card (UpdateRestartOverlay) rather than MessageBox: this
    /// window draws its own chrome everywhere else (WindowStyle="None"), and a native OS dialog
    /// looked out of place glued onto it (per the user).
    /// </para>
    /// </summary>
    private void OfferRestart(VelopackUpdateInfo update, string version)
    {
        var loc = LocalizationManager.Instance;
        _pendingInstallUpdate = null;
        _pendingRestartUpdate = update;
        UpdateRestartTitle.Text = loc["Main.UpdateReady.Title"];
        UpdateRestartLaterButton.Content = loc["Main.UpdateReady.Later"];
        UpdateRestartNowButton.Content = loc["Main.UpdateReady.RestartNow"];
        UpdateRestartText.Text = string.Format(loc["Main.UpdateReady.Message"], version, AppVersion.Text);
        UpdateRestartLaterButton.Visibility = Visibility.Visible;
        UpdateRestartOverlay.Visibility = Visibility.Visible;
        ShowUpdateNotes(update);
    }

    /// <summary>The same card as <see cref="OfferRestart"/>, asking before the download: "a new version, install it? No / Yes". Shown by the start-up check.</summary>
    private void OfferInstall(VelopackUpdateInfo update, string version)
    {
        var loc = LocalizationManager.Instance;
        _pendingRestartUpdate = null;
        _pendingInstallUpdate = update;
        _availableUpdate = update;
        RefreshUpdateBadge();
        UpdateRestartTitle.Text = loc["Main.UpdateAvailable.Title"];
        UpdateRestartLaterButton.Content = loc["Main.UpdateAvailable.No"];
        UpdateRestartNowButton.Content = loc["Main.UpdateAvailable.Yes"];
        UpdateRestartText.Text = string.Format(loc["Main.UpdateAvailable.Message"], version, AppVersion.Text);
        UpdateRestartLaterButton.Visibility = Visibility.Visible;
        UpdateRestartOverlay.Visibility = Visibility.Visible;
        ShowUpdateNotes(update);
    }

    /// <summary>"Yes" on the start-up question: fetch the update (the notice shows the progress) and restart into it.</summary>
    private async Task InstallUpdateNowAsync(VelopackUpdateInfo update)
    {
        // a background check may be fetching the same update this moment: two downloads at once fight over Velopack's staging folder, so wait for it
        while (_updateCheckRunning)
        {
            await Task.Delay(200);
        }

        // the question may have been open a while: the newest release goes in, not the one it named
        if (await NewerThanAsync(update) is { } newer)
        {
            update = newer;
        }

        string version = update.TargetFullRelease.Version.ToString();
        _updateCheckRunning = true;
        try
        {
            if (_downloadedUpdate is { } staged && staged.TargetFullRelease.Version.CompareTo(update.TargetFullRelease.Version) >= 0)
            {
                update = staged; // fetched meanwhile (that check or an earlier one): nothing more to download
            }
            else
            {
                UpdateNotice.Text = $"Downloading {version}...";
                UpdateNotice.Visibility = Visibility.Visible;
                SetUpdateStatus(string.Format(LocalizationManager.Instance["Overlay.UpdateDownloading"], version), "↓ " + version);
                await UpdateService.DownloadAsync(update);
                _downloadedUpdate = update;
            }

            // Window geometry and settings are saved in OnClosing, which ApplyAndRestart never reaches: save first, then hand over (as OnUpdateRestartNowClicked).
            SaveWindowStateToSettings();
            SaveRestartSnapshot();
            FlushStores(force: true);
            UpdateService.ApplyAndRestart(update);
        }
        catch (Exception ex)
        {
            UpdateNotice.Visibility = Visibility.Collapsed;
            SetUpdateStatus(null, null);
            ThemedMessageBox.Show(this, $"The update could not be downloaded.\n\n{ex.Message}",
                "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    private void OnUpdateRestartNowClicked(object sender, RoutedEventArgs e)
    {
        UpdateRestartOverlay.Visibility = Visibility.Collapsed;
        if (_pendingInstallUpdate is { } toInstall)
        {
            _pendingInstallUpdate = null;
            _ = InstallUpdateNowAsync(toInstall);
            return;
        }

        if (_pendingRestartUpdate is not { } update)
        {
            return;
        }

        _pendingRestartUpdate = null;
        _ = RestartIntoNewestAsync(update);
    }

    /// <summary>
    /// The update waiting on disk may not be the newest any more (downloaded an hour ago, a version came out since): asked once more, a newer one is fetched
    /// and put in instead, so one restart brings the newest and the versions between are skipped. Without an answer in a few seconds the one on disk goes in.
    /// </summary>
    private async Task RestartIntoNewestAsync(VelopackUpdateInfo update)
    {
        if (await NewerThanAsync(update) is { } newer)
        {
            await InstallUpdateNowAsync(newer);
            return;
        }

        // Window geometry and settings are saved in OnClosing, which ApplyAndRestart never reaches
        // because it ends the process itself -- so save first, then hand over.
        SaveWindowStateToSettings();
        SaveRestartSnapshot();
        FlushStores(force: true);

        UpdateService.ApplyAndRestart(update);
    }

    /// <summary>A release newer than <paramref name="update"/>'s, asked of GitHub (at most a few seconds); null when there is none or no answer.</summary>
    private static async Task<VelopackUpdateInfo?> NewerThanAsync(VelopackUpdateInfo update)
    {
        try
        {
            Task<VelopackUpdateInfo?> check = UpdateService.CheckAsync();
            if (await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(6))) != check)
            {
                return null;
            }

            return await check is { } newest && newest.TargetFullRelease.Version.CompareTo(update.TargetFullRelease.Version) > 0 ? newest : null;
        }
        catch (Exception)
        {
            return null; // offline, GitHub busy: the one at hand
        }
    }

    private void OnUpdateRestartLaterClicked(object sender, RoutedEventArgs e)
    {
        UpdateRestartOverlay.Visibility = Visibility.Collapsed;
        _pendingInstallUpdate = null;
        _pendingRestartUpdate = null;
    }

    /// <summary>Double-click on a player's row opens the same details as the context menu. Only a
    /// click on a row counts - not the scroll bar or the empty area below the rows.</summary>
    private void OnPlayersGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null and not DataGridRow)
        {
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        if (source is DataGridRow row)
        {
            PlayersGrid.SelectedItem = row.Item;
            OnShowPlayerDetailsClicked(sender, e);
        }
    }

    private void OnShowPlayerDetailsClicked(object sender, RoutedEventArgs e)
    {
        if (PlayersGrid.SelectedItem is PlayerRow row)
        {
            ShowPlayerDetails(row);
        }
    }

    private void ShowPlayerDetails(PlayerRow row)
    {
        // Taken mode: what hit this player over the fight on screen, and how they died.
        if (_takenMode && !_pvpOnly)
        {
            var hits = HostileHitsTaken(_takenSpan).Where(ev => ev.TargetObjectId == row.ObjectId).ToList();
            new PlayerDetailsWindow(row.Name, row.ClassName, row.Faction, hits,
                id => _source?.Entities.NameFor(id) ?? MonsterName(id), deaths: _deathsById.GetValueOrDefault(row.ObjectId), taken: true)
            {
                Owner = this,
            }.Show();
            return;
        }

        var mine = _aggregator.Events.Where(ev => ev.SourceObjectId == row.ObjectId).ToList();

        DateTime? fightStart = _aggregator.Events.Count > 0 ? _aggregator.Events.Min(ev => ev.Timestamp) : null;
        DateTime? fightEnd = _aggregator.Events.Count > 0 ? _aggregator.Events.Max(ev => ev.Timestamp) : null;
        FightExtras? extras = _historyMode
            ? _historyExtras
            : fightStart is DateTime from && fightEnd is DateTime to
                ? ReplayDirectory?.Combat.Slice(from, to, ResolveDisplayName, ReplayKeeps, ReplayHp)
                : null;
        new PlayerDetailsWindow(row.Name, row.ClassName, row.Faction, mine,
            id => _source?.Entities.NameFor(id) ?? MonsterName(id) ?? ResolveDisplayName(id), heals: _healMode && !_pvpOnly, fightEvents: _aggregator.Events,
            extras: extras, playerId: _historyMode ? _historyLiveIds.GetValueOrDefault(row.ObjectId, row.ObjectId) : row.ObjectId, fightStart: fightStart, fightEnd: fightEnd,
            replay: BuildReplayData(extras, fightStart, fightEnd, _aggregator.Events), seen: SeenInfoOf(row))
        {
            Owner = this,
        }.Show();
    }

    private void OnFightReportClicked(object sender, RoutedEventArgs e) => _ = OpenFightReportAsync();

    /// <summary>
    /// The fight report of what is on screen: the picked target's fight (its run), else all the damage shown; live or a stored fight. The players are the
    /// ones of the fight (in the overlay's scope while it is on); the effects, casts and deaths come from the capture's extras (the stored ones in the history).
    /// Worked out off the window's thread, then opened in a window of its own.
    /// </summary>
    private async Task OpenFightReportAsync()
    {
        List<DamageEvent> all = _aggregator.Events.ToList();
        List<DamageEvent> hits = _selectedTargetId is int picked ? ShownFightHits(all.Where(ev => !ev.IsHeal), picked) : all.Where(ev => !ev.IsHeal).ToList();
        hits = hits.Where(ev => IsPlayerName(ev.SourceObjectId)).ToList();
        if (hits.Count == 0)
        {
            ShowUploadStatus("No fight to report yet: hit something first, or pick a fight in the list.");
            return;
        }

        DateTime start = hits.Min(ev => ev.Timestamp), end = hits.Max(ev => ev.Timestamp);
        if (end - start < TimeSpan.FromSeconds(1))
        {
            end = start.AddSeconds(1);
        }

        var directory = ReplayDirectory;
        FightExtras extras = _historyMode
            ? _historyExtras?.Remap(id => _historyReplayIds.GetValueOrDefault(id, id)) ?? FightExtras.Empty
            : directory?.Combat.Slice(start, end, ResolveDisplayName, ReplayKeeps, ReplayHp) ?? FightExtras.Empty;
        List<DamageEvent> inWindow = all.Where(ev => ev.Timestamp >= start && ev.Timestamp <= end).ToList();
        string? ownName = (_liveSource?.Entities as Aion2EntityDirectory)?.LocalCharacter?.Name;
        var ids = new HashSet<int>();
        foreach (DamageEvent ev in inWindow)
        {
            if (!ev.IsHeal && hits.Count > 0 && IsPlayerName(ev.SourceObjectId))
            {
                ids.Add(ev.SourceObjectId);
            }
            else if (ev.IsHeal && IsPlayerName(ev.SourceObjectId) && IsPlayerName(ev.TargetObjectId))
            {
                ids.Add(ev.SourceObjectId);
            }
        }

        ids.RemoveWhere(id => !IsInScope(id) || (_rowsByObjectId.TryGetValue(id, out PlayerRow? r) && r.IsEnemy));
        var players = ids.Select(id =>
        {
            string name = ResolveDisplayName(id);
            int liveId = _historyMode ? _historyLiveIds.GetValueOrDefault(id, id) : id;
            int? power = (_liveSource?.Entities as Aion2EntityDirectory)?.SeenProfileOf(liveId)?.CombatPower;
            bool self = (!_historyMode && _source?.Entities.IsLocalPlayer(id) == true) || (ownName is not null && name == ownName);
            return new ReportPlayer(id, name, ResolveClassName(id), power is 397228 ? null : power, self);
        }).ToList();

        int? bossId = _selectedTargetId is int target && !IsPlayerName(target) ? target : null;
        string title = bossId is int b ? ResolveDisplayName(b) : _pvpOnly ? "PvP" : LocalizationManager.Instance["Main.FilterAllTargets"];
        bool killed = bossId is int dead && _kills.Any(k => k.VictimObjectId == dead && k.Timestamp >= start);
        var input = new ReportInput(title, start, end, players, inWindow,
            _aggregator.Shields.Where(ev => ev.Timestamp >= start && ev.Timestamp <= end).ToList(),
            _avoids.Where(a => a.Timestamp >= start && a.Timestamp <= end).ToList(),
            extras, bossId, bossId is int npcOf ? BossNpcIdOf(npcOf) : null, killed, ResolveDisplayName,
            bossId is int only ? new HashSet<int> { only } : null);
        ShowUploadStatus("Working out the fight report…");
        try
        {
            FightReport report = await Task.Run(() => FightReport.Build(input));
            ShowUploadStatus("");
            new FightReportWindow(report) { Owner = this }.Show();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            ShowUploadStatus("The fight report could not be made: " + ex.Message);
        }
    }

    /// <summary>What the "player appeared" frame told of a player (level, item level, combat power, worn pieces); null for yourself and for players it never told of.</summary>
    private PlayerSeenInfo? SeenInfoOf(PlayerRow row)
    {
        if (_source?.Entities is not Aion2.Aion2EntityDirectory directory || directory.SeenProfileOf(row.ObjectId) is not { } seen)
        {
            return null;
        }

        bool equalized = directory.MetInEqualizedArena(row.Name) || seen.CombatPower == 397228;
        return new PlayerSeenInfo(seen.Level, seen.ItemLevel, equalized ? null : seen.CombatPower, seen.Worn ?? Array.Empty<Aion2.Aion2WornItem>(), equalized, directory.ServerIdOf(row.ObjectId) ?? 0);
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        AppMenu.IsSubmenuOpen = false;
        ClearDamageData();
    }

    /// <summary>Delegates to Combat/EngagedTargets, which is where the two failure modes this
    /// filter exists for are documented and tested.</summary>
    private List<DamageEvent> RestrictToEngagedTargets(List<DamageEvent> damageEvents)
    {
        // No source yet (e.g. before Settings names an install folder) means no "You" id to anchor on.
        return _source is null
            ? damageEvents
            : EngagedTargets.Filter(damageEvents, _source.Entities.LocalPlayerId);
    }

    private void ClearDamageData()
    {
        _overlayPinned = false; // a fight picked by hand is gone: the meter folds again until the next boss fight
        _overlayHadFight = false;
        _aggregator.Clear();
        _groupNames.Clear();
        _avoids.Clear();
        _kills.Clear();
        _rows.Clear();
        _rowsByObjectId.Clear();
        _targetNames.Clear();
        _playerIdentities.Clear();
        _selectedTargetId = null;
        _selectedRunWindowStart = null;
        _selectedRunWindowEnd = null;
        UpdateDpsColumnHeader();

        _mobBossEntries.Clear();
        _bossUploadedUntil.Clear();
        ApplyMobBossSearchFilter();
        RefreshUploadAvailability();
    }

    // Copy/CopyAll are shared between the Damage and Loot views (see OnShowDamageView/
    // OnShowLootView) rather than adding a second pair of buttons just for Loot -- whichever
    // grid is currently visible decides what gets copied. In BOTH views the two buttons follow
    // the same split, which is what their "String"/"Table" labels have always promised: Copy
    // produces the one-line string meant for pasting into a chat box (Aion chat here, the
    // ".loot" payload in the Loot view), CopyAll produces the multi-line table meant for reading
    // outside the game (tab-separated here, Discord Markdown in the Loot view). The Damage view
    // used to hand BOTH buttons the same tab-separated table -- reported by the user, who
    // expected a postable string from the first one.
    private void OnCopyClicked(object sender, RoutedEventArgs e) =>
        CopyChatLineChunk(BuildDmgChatLine(), "No damage has been recorded yet.");

    private void OnCopyAllClicked(object sender, RoutedEventArgs e) => CopyRowsToClipboard();

    /// <summary>
    /// The Damage view's "Table" payload: a fixed-width table in a code fence, ready to paste into
    /// Discord. Was tab-separated, which Discord collapses into an unreadable run of text -- per
    /// the user, this needs to arrive as an aligned ASCII table.
    /// </summary>
    private void CopyRowsToClipboard()
    {
        var ranked = _rows.OrderByDescending(r => r.Damage).ToList();
        var rows = ranked
            .Select(r => (IReadOnlyList<string>)new[]
            {
                r.Name,
                r.ClassName,
                r.Level > 0 ? r.Level.ToString() : "",
                r.Damage.ToString("N0", DotGroupedNumberFormat),
                r.DpsDisplay,
            })
            .ToList();

        string table = AsciiTable.Render(
            new[] { "Name", "Class", "Lvl", "Damage", "DPS" },
            rows,
            new[] { false, false, true, true, true });

        CopyTextToClipboardIfAny(table, "No damage has been recorded yet.");
    }

    /// <summary>Guards Clipboard.SetText against an empty result -- shared by CopyRowsToClipboard
    /// and the ".dmg" in-game command, same as the pre-existing "if (sb.Length > 0)" check.</summary>
    private void CopyTextToClipboardIfAny(string text, string whatWasEmpty)
    {
        if (text.Length == 0)
        {
            // Reported by the user as "clicking String puts nothing on the clipboard". It did
            // exactly what it was told to -- the loot summary only counts Unique and above, so a
            // run without such a drop produces an empty string -- but silently doing nothing is
            // indistinguishable from a broken button. Say which, instead.
            ThemedMessageBox.Show(this, whatWasEmpty, "Nothing to copy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // The clipboard is a single system-wide resource and any other process can hold it
            // open for a moment; SetText then throws instead of waiting. Unhandled, that took the
            // whole meter down mid-raid for something as minor as a failed copy.
            ThemedMessageBox.Show(this, $"The clipboard was busy and the copy failed.\n\n{ex.Message}",
                "Copy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Which chunk (see ChunkChatLineParts) CopyChatLineChunk hands out next, and for
    /// which exact chunk set - reset to 0 the moment the underlying text changes (more damage
    /// since the last copy, a different target selected, etc.) rather than silently continuing a
    /// stale cycle against numbers that no longer match what's on screen.</summary>
    private List<string> _lastChatChunks = new();
    private int _nextChatChunkIndex;

    /// <summary>Copies ONE chat-line chunk per call, cycling back to the first after the last -
    /// per the user, reported twice: even a single joined multi-line clipboard payload (each line
    /// individually well under ChatLineCharLimit) still failed to paste into Aion chat at all the
    /// moment it contained more than one line, exactly like the earlier over-length single-line
    /// case did. Aion's chat box apparently rejects (or otherwise cannot handle) a pasted string
    /// containing a newline at all, not just an over-long one - so each clipboard payload here is
    /// now genuinely a single line, the one shape already confirmed to work, and the user pastes
    /// each part in turn instead of hoping the game splits a multi-line paste into several
    /// messages on its own.</summary>
    private void CopyChatLineChunk(List<string> chunks, string whatWasEmpty)
    {
        if (chunks.Count == 0)
        {
            ThemedMessageBox.Show(this, whatWasEmpty, "Nothing to copy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!chunks.SequenceEqual(_lastChatChunks))
        {
            _lastChatChunks = chunks;
            _nextChatChunkIndex = 0;
        }

        int index = _nextChatChunkIndex % chunks.Count;
        try
        {
            Clipboard.SetText(chunks[index]);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            ThemedMessageBox.Show(this, $"The clipboard was busy and the copy failed.\n\n{ex.Message}",
                "Copy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _nextChatChunkIndex = index + 1;
        ShowUploadStatus(chunks.Count > 1
            ? $"Copied part {index + 1}/{chunks.Count} - paste it, then click Copy again for the next part."
            : "Copied to clipboard.");
    }

    /// <summary>
    /// The ".dmg" in-game command's clipboard payload, exact format specified by the user: a
    /// single comma-separated run (not one row per line) of "name, total [dps]" tuples,
    /// e.g. "Mitzuhiko, 3.123.456 [3.145], Mitzuhiki, 3.123.455 [3.144]", ranked by damage
    /// descending -- independent of whatever order/filter the grid itself is currently showing.
    /// Both the damage total and the DPS figure use Aion's own "." thousands-grouping style for visual consistency with what the game itself
    /// would show, not because DPS is naturally an integer -- it's rounded to match.
    /// </summary>
    /// <summary>Aion's own chat box rejects a paste outright once the resulting line would be too
    /// long, rather than truncating it - reported by the user as "pasting did nothing at all".
    /// Empirically confirmed by the user: a manually-typed 255-character line went through fine,
    /// a pasted ~275-character line didn't. Per the user, 255 exactly - the confirmed-working
    /// boundary itself, not a margin below it.</summary>
    private const int ChatLineCharLimit = 255;

    /// <summary>Splits already-formatted "one entry per row" strings into lines that each fit
    /// under <see cref="ChatLineCharLimit"/> (joined by ", " within a line, same as the
    /// unsplit format before this existed) - multiple lines are meant to be pasted into Aion
    /// chat and sent one at a time, not as a single paste.</summary>
    private static List<string> ChunkChatLineParts(IReadOnlyList<string> parts, int limit)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (string part in parts)
        {
            int prospectiveLength = current.Length == 0 ? part.Length : current.Length + 2 + part.Length;
            if (current.Length > 0 && prospectiveLength > limit)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(", ");
            }

            current.Append(part);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    private List<string> BuildDmgRankingText()
    {
        var ranked = _rows.OrderByDescending(r => r.Damage).ToList();
        var parts = new List<string>(ranked.Count);
        for (int i = 0; i < ranked.Count; i++)
        {
            PlayerRow row = ranked[i];
            string damageText = row.Damage.ToString("N0", DotGroupedNumberFormat);
            string dpsText = row.Dps is double dps
                ? Math.Round(dps).ToString("N0", DotGroupedNumberFormat)
                : "n/a";
            parts.Add($"{row.Name}, {damageText} [{dpsText}]");
        }

        return ChunkChatLineParts(parts, ChatLineCharLimit);
    }

    /// <summary>
    /// Copy's payload in the Damage view: one line, "Name Damage (DPS)" per entry, entries joined
    /// by ", " and ranked by damage descending regardless of how the grid is currently sorted --
    /// format specified by the user for pasting straight into the Aion chat box. Deliberately
    /// leaner than BuildDmgRankingText (the hotkey's payload, which brackets the DPS): both stay as their own specified formats rather than
    /// one being bent into the other. Numbers use Aion's own "." thousands grouping, and a row
    /// whose DPS is undefined (single hit, no elapsed time -- see DpsCalculator) shows "n/a"
    /// rather than a fabricated rate.
    /// </summary>
    /// <summary>
    /// Per the user: when the Mob/Boss filter has a specific target selected, the copied line must
    /// lead with that target's name -- otherwise a line pasted into Aion chat carries damage numbers
    /// with no indication of which fight they're from.
    /// </summary>
    private List<string> BuildDmgChatLine()
    {
        var ranked = _rows.OrderByDescending(r => r.Damage).ToList();
        var parts = new List<string>(ranked.Count);
        foreach (PlayerRow row in ranked)
        {
            string damageText = row.Damage.ToString("N0", DotGroupedNumberFormat);
            string dpsText = row.Dps is double dps
                ? Math.Round(dps).ToString("N0", DotGroupedNumberFormat)
                : "n/a";
            parts.Add($"{row.Name} {damageText} ({dpsText})");
        }

        if (parts.Count == 0)
        {
            return new List<string>();
        }

        List<string> lines = ChunkChatLineParts(parts, ChatLineCharLimit);
        if (_selectedTargetId is int targetId)
        {
            string bossName = _targetNames.TryGetValue(targetId, out string? n) ? n : ResolveDisplayName(targetId);
            lines[0] = $"{bossName}: {lines[0]}";
        }

        return lines;
    }

    private static readonly NumberFormatInfo DotGroupedNumberFormat = new() { NumberGroupSeparator = "." };

    // aiontools.com's U+E000-U+E06F in-game chat icons (see memopad_icons.png, sent to the user
    // for reference) -- these three positions were picked by the user directly from that image
    // ("Orange: Reihe 2 - letztes Bild", "Gold: Reihe 3 Bild 2", "Lila: Reihe 3 Bild 7"). The
    // reference image wraps a single flat sequence starting right after the visible title text
    // (5 icons trail the title on its own line before the first full 20-icon row begins) purely
    // because of the container's width, not real row breaks -- pixel-row-isolated and counted
    // directly against the decoded PNG (not eyeballed at native resolution) to convert "row/
    // position" into an absolute index, then into a codepoint: title-line icons are index 0-4,
    // "Reihe 1" 5-24, "Reihe 2" 25-44, "Reihe 3" 45-64 (all rows measured 20 icons wide). Cross-
    // checked against color: the computed position for "Orange" rendered as a rust-orange sphere,
    // "Gold" as a gold sphere, "Lila" as a purple ring -- matching the user's own color naming,
    // not just the position count alone.

    // Aion's own chat glyphs for the four item-quality tiers, taken from characters the user
    // pasted straight out of the client. Written as escapes rather than literal characters on
    // purpose: they live in the Unicode Private Use Area, so they are invisible in every editor
    // and diff outside the game, and a literal would be silently lost or mangled by anything that
    // strips PUA -- which is exactly what happened trying to send them through chat.
    //
    // Two of these were WRONG until now, and invisibly so: gold was U+E02E and epic U+E02C, which
    // are some other glyph entirely, so every loot summary posted to the group showed the wrong
    // symbols. Only mythic was right. Verified against the game's own output, tier by tier.
    private const string LegendIcon = "\ue036";   // blue
    private const string UniqueIcon = "\ue038";   // gold
    private const string EpicIcon = "\ue03e";     // orange
    private const string MythicIcon = "\ue033";   // purple

    private const int MaxIconsPerColor = 30;

    private void OnPauseClicked(object sender, RoutedEventArgs e) => SetPaused(!_paused);

    /// <summary>Shared by the toolbar Pause/Resume button and the pause hotkey.</summary>
    private void SetPaused(bool paused)
    {
        _paused = paused;
        PauseIcon.Visibility = _paused ? Visibility.Collapsed : Visibility.Visible;
        PlayIcon.Visibility = _paused ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.ToolTip = _paused ? "Resume recording." : "Pause recording.";
    }

    /// <summary>Tracked so a second click on "App Settings" while one is already open focuses the
    /// existing window instead of opening a confusing second editor on the same settings file --
    /// see OnSettingsClicked. Cleared in the window's Closed handler.</summary>
    private SettingsWindow? _settingsWindow;
    private FeedbackWindow? _feedbackWindow;

    private void OnFeedbackClicked(object sender, RoutedEventArgs e)
    {
        // the form sends what is typed to the community server: only with the privacy policy accepted
        if (!PrivacyConsent.EnsureForUploadSwitch(this))
        {
            return;
        }

        if (_feedbackWindow is not null)
        {
            _feedbackWindow.Activate();
            return;
        }

        _feedbackWindow = new FeedbackWindow(MeterSettings.Load()) { Owner = this };
        _feedbackWindow.Closed += (_, _) => _feedbackWindow = null;
        _feedbackWindow.Show();
    }

    /// <summary>
    /// Non-modal per the user's request ("Settings bitte als 2. Fenster öffnen") -- Show(), not
    /// ShowDialog(), so MainWindow stays interactive while Settings is open. SettingsWindow no
    /// longer uses DialogResult for this reason (see its Saved event remarks); applying the
    /// settings here happens from that event instead of a ShowDialog() return value.
    /// </summary>
    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.ShowModule(null);
            _settingsWindow.Activate();
            return;
        }

        OpenSettings(null);
    }

    private ToolsWindow? _toolsWindow;

    /// <summary>A tool of the Tools menu: all tools share one window, the tools in the order of the menu on the left, the page of the chosen one on the right.</summary>
    private void OnOpenModuleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            OpenTool(id);
        }
    }

    private void OpenTool(string id)
    {
        id = AssistTabOf(id);
        if (PrivacyConsent.Restricted && !PrivacyConsent.RestrictedTools.Contains(id))
        {
            return;
        }

        // the tools are sections of the app now (MainWindow.Shell.cs); the tools window is left for a start without them
        if (_shellBuilt)
        {
            ShowClient();
            ShowShellPage(id);
            return;
        }

        if (_toolsWindow is null)
        {
            var loc = LocalizationManager.Instance;
            var groups = ToolsWindow.Groups();
            if (PrivacyConsent.Restricted)
            {
                groups = groups.Select(g => g.Where(t => PrivacyConsent.RestrictedTools.Contains(t.Id)).ToArray()).Where(g => g.Length > 0).ToArray();
            }

            _toolsWindow = new ToolsWindow(groups, id => BuildToolPage(id), tool =>
            {
                if (tool == "analyzer")
                {
                    OpenSessionManager();
                }
            }) { Owner = this };
            _toolsWindow.Closed += (_, _) => _toolsWindow = null;
            _toolsWindow.Show();
            WindowPlacement.AskIfNeeded(this);
        }

        _toolsWindow.Select(id);
        _toolsWindow.Activate();
    }

    private void OpenSettingsPage(string module)
    {
        if (_settingsWindow is null)
        {
            OpenSettings(null);
        }

        if (module.Length == 0)
        {
            _settingsWindow?.ShowHotkeysPage();
        }
        else
        {
            _settingsWindow?.ShowModule(module);
        }

        _settingsWindow?.Activate();
    }

    /// <summary>The page of one tool for the tools window; null when the tool is not available (headless, no capture yet). A page with an overlay switch also gets the pop-out button.</summary>
    private ToolPage? BuildToolPage(string module, bool inPopOut = false)
    {
        ToolPage? page = BuildToolPageContent(module);
        if (!inPopOut && page?.Content is ModulePage modulePage)
        {
            modulePage.PopOut = () => OpenPopOut(module);
        }

        return page;
    }

    private void OpenPopOut(string module, bool restore = false)
    {
        string title = ToolsWindow.Groups().SelectMany(g => g).FirstOrDefault(t => t.Id == module)?.Title ?? module;
        PopOutWindow.Open(module, title, id => BuildToolPage(id, inPopOut: true), restore);
    }

    /// <summary>The tools that were popped out when the meter closed come back where they were (without taking the focus from the game); a place on a monitor that is gone asks what to do with the saved places.</summary>
    private void RestorePopOuts()
    {
        foreach (string id in MeterSettings.Load().PopOuts.Where(p => p.Value.Open).Select(p => p.Key).ToList())
        {
            if (!PrivacyConsent.Restricted || PrivacyConsent.RestrictedTools.Contains(id))
            {
                OpenPopOut(id, restore: true);
            }
        }
    }

    private ToolPage? BuildToolPageContent(string module)
    {
        var loc = LocalizationManager.Instance;
        Action<MeterSettings> applied = ApplySettingsEverywhere;
        Action hotkeys = () => OpenSettingsPage("");
        Action settingsPage = () => OpenSettingsPage(module);
        // a tool that is a window of its own is shown by its content: the window is never shown and closed when the page goes away
        ToolPage Hosted(Window window) => new() { Content = ThemedChrome.TakeContent(window) ?? new Grid(), Cleanup = window.Close };
        switch (module)
        {
            case "checklist":
                return Hosted(new ChecklistWindow());
            case "attributes":
            {
                var attributesPage = new AttributesPage(() => _liveSource?.Entities as Aion2EntityDirectory);
                var attributesTool = new ToolPage { Content = attributesPage, Cleanup = attributesPage.Stop, CurrentTab = () => attributesPage.Tab };
                attributesPage.TabChanged += attributesTool.RaiseTabChanged;
                return attributesTool;
            }

            case "statcalc": // no longer in the column (the CP calculator replaced it); a page popped out before still opens
            {
                var calcPage = new StatCalcPage(() => _liveSource?.Entities as Aion2EntityDirectory, () => _aggregator.Events);
                return new ToolPage { Content = calcPage, Cleanup = calcPage.Stop };
            }

            case "cpcalc":
            {
                var cpPage = new CpCalcPage(() => _liveSource?.Entities as Aion2EntityDirectory);
                return new ToolPage { Content = cpPage, Cleanup = cpPage.Stop };
            }

            case "journey":
            {
                var journeyPage = new JourneyPage(() => _liveSource?.Entities as Aion2EntityDirectory);
                return new ToolPage { Content = journeyPage, Cleanup = journeyPage.Stop };
            }

            case "legion":
            {
                var legionPage = new LegionPage(() => _liveSource?.Entities as Aion2EntityDirectory, () => OnAccountClicked(this, new RoutedEventArgs()));
                return new ToolPage { Content = legionPage, Cleanup = legionPage.Stop };
            }

            case "loot":
            {
                var lootPage = new LootPage();
                var page = new ModulePage(loc["Main.MenuTools.Loot"], lootPage, s => s.ShowLootOverlay, (s, v) => s.ShowLootOverlay = v, s => s.HotkeyLoot, hotkeys, settingsPage, applied) { FitWidth = true };
                return new ToolPage { Content = page, Cleanup = () => { page.Stop(); lootPage.Stop(); } };
            }

            case "craft":
            {
                var craftPage = new CraftPage(() => _liveSource?.Entities as Aion2EntityDirectory);
                CraftConfigWindow? craftConfig = null;
                Action openCraftConfig = () =>
                {
                    if (craftConfig is { IsLoaded: true })
                    {
                        craftConfig.Activate();
                        return;
                    }

                    craftConfig = new CraftConfigWindow(() => _liveSource?.Entities as Aion2EntityDirectory) { Owner = (Window?)_toolsWindow ?? this };
                    craftConfig.Closed += (_, _) => craftConfig = null;
                    craftConfig.Show();
                };
                var craftModule = new ModulePage("", craftPage, s => s.ShowCraftOverlay, (s, v) => s.ShowCraftOverlay = v, s => s.HotkeyCraft, hotkeys, openCraftConfig, applied, null, craftPage.ModeBar);
                return new ToolPage { Content = craftModule, Cleanup = () => { craftModule.Stop(); craftPage.Stop(); } };
            }

            case "assist":
            {
                var assistWindow = new AssistWindow(() => _liveSource?.Entities as Aion2EntityDirectory);
                var assistTool = new ToolPage { Content = ThemedChrome.TakeContent(assistWindow) ?? new Grid(), Cleanup = assistWindow.Close, CurrentTab = () => assistWindow.Tab };
                assistWindow.TabChanged += assistTool.RaiseTabChanged;
                return assistTool;
            }
            case "dummy" when _dummy is not null:
            {
                var dummyWindow = new DummyTestWindow(_dummy, ToggleAllOverlays, OpenDummyRun);
                var dummyTool = new ToolPage { Content = ThemedChrome.TakeContent(dummyWindow) ?? new Grid(), Cleanup = dummyWindow.Close, CurrentTab = () => dummyWindow.Tab };
                dummyWindow.TabChanged += dummyTool.RaiseTabChanged;
                return dummyTool;
            }

            case "theme":
            {
                var editor = new ThemeEditorWindow(MeterSettings.Load());
                editor.Saved += settings =>
                {
                    var current = MeterSettings.Load();
                    current.Theme = settings.Theme;
                    current.Save();
                    ApplySettingsEverywhere(current);
                };
                return Hosted(editor);
            }

            case "network" when _network is not null:
            {
                var view = _network.CreateView();
                var networkBody = new StackPanel();
                networkBody.Children.Add(view.DetachBody()!);
                networkBody.Children.Add(new NetworkReportPanel());
                var page = new ModulePage(loc["Settings.Nav.Network"], networkBody, s => s.ShowNetworkOverlay, (s, v) => s.ShowNetworkOverlay = v, s => s.HotkeyNetwork, hotkeys, settingsPage, applied);
                return new ToolPage { Content = page, Cleanup = () => { page.Stop(); _network?.RemoveView(view); } };
            }

            case "boss":
            {
                // the world bosses as a page of their own (6 November 2026): up now, coming next, one region at a time; the overlay is its own switch
                var bosses = new WorldBossPage();
                var page = new ModulePage(loc["Settings.Nav.Boss"], bosses, s => s.ShowBossOverlay, (s, v) => s.ShowBossOverlay = v, s => s.HotkeyWorldBoss, hotkeys, settingsPage, applied, bosses.Tick)
                {
                    FitWidth = true,
                };
                return new ToolPage { Content = page, Cleanup = page.Stop };
            }

            case "timetable":
            {
                // the Timers page, like the website's timers; it moves with the page's one-second timer, and a switch changed on it shows on the overlay at once
                var timers = new TimersPage(() => _timetable?.Reload());
                var page = new ModulePage(loc["Timers.Title"], timers, s => s.ShowTimetable, (s, v) => s.ShowTimetable = v, s => s.HotkeyTimetable, hotkeys, settingsPage, applied, timers.Tick)
                {
                    FitWidth = true,
                };
                return new ToolPage { Content = page, Cleanup = () => { page.Stop(); timers.Stop(); } };
            }

            case "combat" when _combat is not null:
            {
                var views = _combat.CreateViews();
                var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                stack.Children.Add(ModulePage.Hint(loc["Combat.Page.SampleHint"]));
                var elements = new List<(CheckBox Box, UIElement Body, Func<MeterSettings, bool> Get)>();
                foreach (var (titleKey, view) in views)
                {
                    // each element's own switch sits at its caption and counts at once (the page and the overlay read the settings from disk)
                    (Func<MeterSettings, bool> get, Action<MeterSettings, bool> set) = view.PartKind switch
                    {
                        CombatOverlayWindow.Part.Hp => ((Func<MeterSettings, bool>)(s => s.ShowBossHp), (Action<MeterSettings, bool>)((s, v) => s.ShowBossHp = v)),
                        CombatOverlayWindow.Part.Cast => (s => s.CombatShowBossCast, (s, v) => s.CombatShowBossCast = v),
                        _ => (s => s.CombatShowTargetOfTarget, (s, v) => s.CombatShowTargetOfTarget = v),
                    };
                    var (row, box) = ModulePage.SwitchCaption(loc[titleKey], loc["Settings.Overlay.ShowAs"]);
                    box.Click += (_, _) =>
                    {
                        MeterSettings s = MeterSettings.Load();
                        set(s, box.IsChecked == true);
                        s.Save();
                        applied(s);
                    };
                    UIElement body = view.DetachBody()!;
                    elements.Add((box, body, get));
                    stack.Children.Add(row);
                    stack.Children.Add(body);
                }

                void SyncElements()
                {
                    MeterSettings s = MeterSettings.Load();
                    foreach (var (box, body, get) in elements)
                    {
                        bool on = get(s);
                        box.IsChecked = on;
                        body.Opacity = on ? 1.0 : 0.45; // switched off: the sample stays, dimmed, so the page still shows what the display would be
                    }
                }

                SyncElements();
                var page = new ModulePage(loc["Settings.Nav.Combat"], stack, s => s.ShowCombatOverlay, (s, v) => s.ShowCombatOverlay = v, s => s.HotkeyCombatOverlay, hotkeys, settingsPage, applied, SyncElements);
                return new ToolPage { Content = page, Cleanup = () => { page.Stop(); _combat?.RemoveViews(views.Select(v => v.View)); } };
            }

            case "petmap":
            {
                _petMapController ??= new PetMapController(() => _source?.Entities as Aion2EntityDirectory);
                var (map, list) = _petMapController.CreateViews();
                var stack = new StackPanel();
                // what the map tracks, all picked here: the pet route (the website's), which the map guides along, and under it the gathering materials and
                // the Hidden Cubes (the settings of the Gathering and Cube finder pages, which stay; MainWindow.MapPicks.cs)
                var (routeCard, displayCard, routeRefresh) = PetRouteControls();
                Border display = HomeCard(null, displayCard, null, MapGatherCardWidth + 12 + MapCubesCardWidth); // 12: the gathering card's margin to the cube card
                display.HorizontalAlignment = HorizontalAlignment.Left;
                stack.Children.Add(display);
                Border route = HomeCard(null, routeCard, null, MapGatherCardWidth + 12 + MapCubesCardWidth);
                route.HorizontalAlignment = HorizontalAlignment.Left;
                stack.Children.Add(route);
                var (gatherCard, gatherSync) = MapGatherCard();
                var (cubesCard, cubesSync) = MapCubesCard();
                var picks = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
                picks.Children.Add(gatherCard);
                picks.Children.Add(cubesCard);
                stack.Children.Add(picks);
                stack.Children.Add(ModulePage.Caption(loc["Settings.PetMap.CardMap"]));
                // why the map is empty (no game read yet, no position, nothing picked) or what it shows, as the controller sees it
                var mapStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 8), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
                mapStatus.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
                var statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                statusTimer.Tick += (_, _) =>
                {
                    routeRefresh();
                    // what was picked meanwhile on the Gathering or Cube finder page (perhaps open in a window of its own) shows here too
                    MeterSettings picked = MeterSettings.Load();
                    gatherSync(picked);
                    cubesSync(picked);
                    string text = _petMapController?.Status ?? "";
                    mapStatus.Text = text;
                    mapStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                };
                statusTimer.Start();
                stack.Children.Add(mapStatus);
                var hoverHint = new TextBlock { Text = loc["PetMap.HoverHint"], TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 8), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
                hoverHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
                stack.Children.Add(hoverHint);
                var mapCard = new Border { Child = map.DetachBody()!, CornerRadius = new CornerRadius(6), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
                mapCard.SetResourceReference(Border.BackgroundProperty, "Brush.Control");
                stack.Children.Add(mapCard);
                stack.Children.Add(ModulePage.Caption(loc["Settings.PetMap.CardList"]));
                stack.Children.Add(list.DetachBody()!);
                var page = new ModulePage(loc["Settings.Nav.PetMap"], stack, s => s.ShowPetMap, (s, v) => s.ShowPetMap = v, s => s.HotkeyPetMap, hotkeys, settingsPage, applied);
                return new ToolPage
                {
                    Content = page,
                    Cleanup = () =>
                    {
                        page.Stop();
                        statusTimer.Stop();
                        _petMapController?.RemoveViews(map, list);
                        if (MeterSettings.Load() is { ShowPetMap: false, CubeSoundEnabled: false, ShowMapOnPet: false } && _petMapController is { HasViews: false, CubeMapOverride: false, PetMapOverride: false })
                        {
                            _petMapController.Dispose();
                            _petMapController = null;
                        }
                    },
                };
            }

            case "cubes":
                return BuildCubesPage();
            case "petmap.pets":
            case "petmap.gather":
            {
                // the settings of the map's pets / collectibles: the panel of the settings window (never shown) as a page here; every change is kept a moment later
                string panelName = module == "petmap.pets" ? "PanelPets" : "PanelGather";
                var window = new SettingsWindow(MeterSettings.Load());
                if (window.TakePanel(panelName) is not { } panel)
                {
                    window.Close();
                    return null;
                }

                var body = new StackPanel { MinWidth = 560 };
                body.Resources.MergedDictionaries.Add(window.Resources);
                body.Children.Add(panel);
                var save = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                void Save()
                {
                    save.Stop();
                    var s = MeterSettings.Load();
                    window.ReadPanel(panelName, s);
                    s.Save();
                    ApplySettingsEverywhere(s);
                }

                save.Tick += (_, _) => Save();
                void Changed(object sender, RoutedEventArgs e)
                {
                    save.Stop();
                    save.Start();
                }

                foreach (var routed in new[] { System.Windows.Controls.Primitives.ButtonBase.ClickEvent, System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
                    System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, System.Windows.Controls.Primitives.RangeBase.ValueChangedEvent,
                    System.Windows.Controls.Primitives.Selector.SelectionChangedEvent })
                {
                    panel.AddHandler(routed, new RoutedEventHandler(Changed));
                }

                var page = new ModulePage(module == "petmap.pets" ? loc["Settings.Nav.Pets"] : loc["Settings.Nav.Gather"], body, s => s.ShowPetMap, (s, v) => s.ShowPetMap = v, s => s.HotkeyPetMap, hotkeys, null, applied);
                return new ToolPage
                {
                    Content = page,
                    Cleanup = () =>
                    {
                        page.Stop();
                        if (save.IsEnabled)
                        {
                            Save();
                        }

                        window.Close();
                    },
                };
            }

            case "stream":
                return BuildStreamPage(hotkeys, settingsPage, applied);
        }

        return null;
    }

    /// <summary>Opens the settings window; <paramref name="undoUntil"/> adds "Undo reset" to it (after a reset).</summary>
    private void OpenSettings(DateTime? undoUntil)
    {
        var settings = MeterSettings.Load();
        // what this window started from: on Save only what it changed goes over the file as it is now (3 November 2026: a whole working copy was saved, so a
        // switch flipped on a page while this window was open, the theme editor's theme or an overlay moved meanwhile was set back)
        string opened = System.Text.Json.JsonSerializer.Serialize(settings);
        _settingsWindow = new SettingsWindow(settings, undoUntil) { Owner = this };
        _settingsWindow.Saved += () =>
        {
            MeterSettings baseline = System.Text.Json.JsonSerializer.Deserialize<MeterSettings>(opened) ?? new MeterSettings();
            MeterSettings merged = MeterSettings.Load();
            MeterSettings.CopyChanged(settings, baseline, merged);
            merged.Save();
            opened = System.Text.Json.JsonSerializer.Serialize(settings); // a second Save of the open window compares with this one
            ApplySettingsEverywhere(merged);
        };
        _settingsWindow.ResetRequested += () =>
        {
            MeterSettings.ResetToDefaults();
            ApplySettingsEverywhere(MeterSettings.Load());
            ReopenSettings(DateTime.UtcNow.AddSeconds(30));
        };
        _settingsWindow.UndoRequested += () =>
        {
            if (MeterSettings.UndoReset())
            {
                ApplySettingsEverywhere(MeterSettings.Load());
            }
            ReopenSettings(null);
        };
        _settingsWindow.CopyStreamLinkRequested += CopyStreamLink;
        _settingsWindow.NewStreamKeyRequested += () =>
        {
            var current = MeterSettings.Load();
            current.StreamOverlayKey = Streaming.StreamOverlayServer.NewKey();
            current.Save();
            _streamSettingsAt = DateTime.MinValue; // the server restarts with the new key at the next look
            StreamTick();
            CopyStreamLink();
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void ReopenSettings(DateTime? undoUntil)
    {
        _settingsWindow?.Close();
        OpenSettings(undoUntil);
    }

    /// <summary>What has to follow when the settings change: the look, the hotkeys, the overlays and the capture.</summary>
    private void ApplySettingsEverywhere(MeterSettings settings)
    {
        if (settings.Language.Length > 0)
        {
            LocalizationManager.Instance.Language = settings.Language;
        }
        else
        {
            LocalizationManager.Instance.UseSystemLanguage();
        }

        Aion2.Capture.DebugRecording.Enabled = settings.DebugRecording;
        Aion2.Capture.LoginCapture.Enabled = settings.ShareLoginWithLegion;
        ThemeManager.Apply(Application.Current, settings); // repaints every open window
        foreach (PlayerRow row in _rows)
        {
            row.RefreshClassBrush(); // the own bar colour may have changed
        }
        ApplyHotkeys(settings); // possibly changed keys
        ApplyTimetable(settings);
        ApplyPetFarm(settings);
        ApplyHotkeyOverlay(settings);
        ExitHistoryMode(); // a viewed past fight must not survive a source change underneath it
        if (settings.CaptureAdapterId != _captureAdapterInUse)
        {
            StartCapture(settings); // only a new network adapter needs a new capture: a restart forgets the map, the position and the pets of the running game
        }
        RefreshCharacterSettings(settings); // possibly a new/changed character list or active one
        ApplyClassFilterAvailability(); // possibly a new/changed install folder or server display name
        RefreshRows();
    }

    /// <summary>
    /// Briefly also duplicated onto a toolbar checkbox, which promptly overflowed the toolbar
    /// past the window's edge (see terminal_windows's measurements) -- reverted back to living
    /// only here. IsCheckable="True" already renders its own checkmark when active, which is
    /// exactly what the user asked for ("in einem Unter-Menü... mit Haken Symbol davor").
    /// </summary>
    private void OnAlwaysOnTopClicked(object sender, RoutedEventArgs e)
    {
        Topmost = AlwaysOnTopMenuItem.IsChecked;

        // The menu toggle and MeterSettings.AlwaysOnTopOnStartup are the same value now, not two
        // independent switches (see that property's remarks) -- saved immediately, same as
        // CheckForUpdates, so "how I last left it" is what the next launch restores.
        var settings = MeterSettings.Load();
        settings.AlwaysOnTopOnStartup = Topmost;
        settings.Save();
    }

    private void OnModeButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out MeterMode mode))
        {
            SetMode(mode);
        }
    }

    private static readonly Dictionary<string, ImageSource?> ModeIcons = new();

    /// <summary>One of the mode pictures in assets/modes (null when the file is missing; the button then shows nothing).</summary>
    private static ImageSource? ModeIcon(string name)
    {
        if (ModeIcons.TryGetValue(name, out ImageSource? known))
        {
            return known;
        }

        ImageSource? image = null;
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "modes", name + ".png");
            if (System.IO.File.Exists(path))
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch
        {
        }

        ModeIcons[name] = image;
        return image;
    }

    private static SolidColorBrush ModeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly SolidColorBrush ModeGreen = ModeBrush(0x69, 0xE1, 0x5E);
    private static readonly SolidColorBrush ModeRed = ModeBrush(0xFF, 0x6B, 0x5E);
    private static readonly SolidColorBrush ModeAllActive = ModeBrush(0x2C, 0x4A, 0x60);

    /// <summary>The five display modes as tabs of one segmented switch: the chosen one is raised (the panel colour, light text), the
    /// others are quiet words. The mode pictures are still set (assets/modes) for the places that show them.</summary>
    private void ApplyModeButtons()
    {
        Brush raised = (Brush)FindResource("Brush.ControlRaised");
        Brush text = (Brush)FindResource("Brush.Text");
        Brush muted = (Brush)FindResource("Brush.TextMuted");
        void Tab(Button button, Image image, string icon, bool active)
        {
            button.Background = active ? raised : Brushes.Transparent;
            button.Foreground = active ? text : muted;
            image.Source = ModeIcon(icon);
        }

        Tab(ModeDamageButton, ModeDamageImage, _mode == MeterMode.Damage ? "mode-damage-on" : "mode-damage-off", _mode == MeterMode.Damage);
        Tab(ModeRaidButton, ModeRaidImage, "mode-damage-off", _mode == MeterMode.Raid);
        Tab(ModeHealButton, ModeHealImage, _mode == MeterMode.Heal ? "mode-heal-on" : "mode-heal-off", _mode == MeterMode.Heal);
        Tab(ModeTakenButton, ModeTakenImage, _mode == MeterMode.Taken ? "mode-taken-on" : "mode-taken-off", _mode == MeterMode.Taken);
        Tab(ModeAllButton, ModeAllImage, "mode-all", _mode == MeterMode.All);
        Tab(ModeAllPlusButton, ModeAllPlusImage, "mode-all-plus", _mode == MeterMode.AllCompact);
    }

    private void SetMode(MeterMode mode, bool save = true)
    {
        if (PrivacyConsent.Restricted && mode != MeterMode.Damage)
        {
            return; // the restricted mode shows the damage list only
        }

        _mode = mode;
        ApplyModeButtons();
        UpdateDpsColumnHeader();
        RefreshRows();
        if (save && !Headless)
        {
            var settings = MeterSettings.Load();
            settings.LastMode = mode.ToString();
            settings.Save();
        }
    }

    /// <summary>Damage, then heal, taken, all in one, all in one compact, then damage again.</summary>
    private void NextMode() => SetMode(_mode switch
    {
        MeterMode.Damage => MeterMode.Raid,
        MeterMode.Raid => MeterMode.Heal,
        MeterMode.Heal => MeterMode.Taken,
        MeterMode.Taken => MeterMode.All,
        MeterMode.All => MeterMode.AllCompact,
        _ => MeterMode.Damage,
    });

    private void OnOverlayModeClicked(object sender, MouseButtonEventArgs e)
    {
        NextMode();
        e.Handled = true;
    }

    /// <summary>The id to read a row's guild and profile under: the own character's row may carry an
    /// older object id than the one the game currently knows it by.</summary>
    /// <summary>The game's permanent character id of a player for the upload (a hint for the website to recognise a renamed character), null when no player list named him.</summary>
    private static int? CharacterIdFor(Aion2.Aion2EntityDirectory? directory, string name, int? serverId) =>
        directory is not null && serverId is int server && directory.CharacterIdOf(name, server) is > 0 and int id ? id : null;

    private int ProfileIdOf(PlayerRow row) =>
        _source?.Entities is Aion2.Aion2EntityDirectory { LocalCharacter: { } own } && row.Name == own.Name
            ? own.CombatId
            : row.ObjectId;

    /// <summary>The Aion 2 character profile to attach to a participant: the full own record for the
    /// local player (also when it was restored from disk), what the "player appeared" frame showed
    /// for everyone else; null when there is nothing to say (no frame seen).</summary>
    private ProfileUpload? BuildProfileUpload(int objectId)
    {
        if (_source?.Entities is not Aion2.Aion2EntityDirectory directory)
        {
            return null;
        }

        if (directory.IsLocalPlayer(objectId))
        {
            // Only a record the game sent in this session, for this very name. What was read back from the file of an
            // earlier session (maybe another character's) goes nowhere until the game sends the real one.
            if (directory.LocalCharacter is not { Restored: false } live
                || !string.Equals(live.Name, directory.NameFor(objectId), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            Aion2CharacterInfo character = live;
            int code = character.ClassCode;
            bool known = Aion2.Protocol.Aion2SkillNames.ClassIdOfCode(code) is >= 1 and <= 8;
            return new ProfileUpload(
                "self",
                character.Level,
                known ? Aion2.Protocol.Aion2SkillNames.ClassIdOfCode(code) : null,
                known ? Aion2.Protocol.Aion2SkillNames.ClassBitsOfCode(code) : null,
                directory.LocalEquipment.Select(GearUpload).ToList(),
                directory.LocalSkills.Select(s => new ProfileSkillUpload(s.SkillId, s.Level, s.BaseLevel, s.Stigma, s.Equipped, s.Stones)).ToList(),
                directory.LocalDaevanion.Select(b => new ProfileBoardUpload(b.BoardId, b.NodeIds.ToList())).ToList(),
                directory.LocalSpecies.Select(k => new ProfileSpeciesUpload(k.SpeciesId, k.Level, k.Progress,
                    k.Effects.Select(e => new ProfileSpeciesEffectUpload(e.Page, e.Slot, e.StatId, e.Value, e.Kind)).ToList())).ToList(),
                Titles: directory.LocalTitles.Select(t => new ProfileTitleUpload(t.Slot, t.TitleId)).ToList(),
                Pets: directory.LocalPets.Select(k => new ProfilePetUpload(k.SpeciesId, k.Level, k.Kinds)).ToList(),
                Attributes: directory.LocalAttributes,
                WingId: directory.LocalWing?.WingId,
                WingSkinId: directory.LocalWing is { SkinId: > 0 } wing ? wing.SkinId : null,
                ActivePet: directory.LocalActivePet,
                ActivePetLevel: directory.LocalActivePet is > 0 and int activePet
                    ? directory.LocalPetStates.Where(p => p.PetId == activePet).Select(p => (int?)p.Level).FirstOrDefault()
                    : null,
                Presets: directory.LocalPresets.Select(x => new ProfilePresetUpload(x.Index, x.Active, x.SeenAt.ToUniversalTime(),
                    x.Skills.Select(k => new ProfileSkillUpload(k.SkillId, k.Level, k.BaseLevel, k.Stigma, k.Equipped, k.Stones)).ToList())).ToList(),
                AccountKey: directory.AccountKeyHash(),
                Ranks: directory.OwnRanks().Select(r => new ProfileRankUpload(r.Type, r.Scope, r.Entry.Rank, r.Entry.Points, r.Entry.Change, r.Entry.Grade, r.Entry.StatsJson.Length is > 0 and <= 300 ? r.Entry.StatsJson : null, r.At.ToUniversalTime())).ToList() is { Count: > 0 } ranks ? ranks : null,
                Pantheon: directory.LocalPantheon is { } pantheon && (pantheon.Presets.Count > 0 || pantheon.Owned.Count > 0)
                    ? new ProfilePantheonUpload(
                        pantheon.Presets.OrderBy(x => x.Key).Select(x => new ProfilePantheonPresetUpload(x.Key, x.Value.Select(k => new ProfilePantheonSlotUpload(k.Slot, k.Id)).ToList())).ToList(),
                        pantheon.Owned)
                    : null);
        }

        // A character window the local player opened for this player: level and the full equipment with
        // enchants, richer than the "appeared" frame below.
        if (directory.NameFor(objectId) is string windowName
            && directory.InspectedPlayers().FirstOrDefault(p => p.Name == windowName) is { } window)
        {
            return new ProfileUpload(
                "seen",
                window.Level,
                Aion2.Protocol.Aion2SkillNames.ClassIdOfCode(window.ClassCode),
                Aion2.Protocol.Aion2SkillNames.ClassBitsOfCode(window.ClassCode),
                window.Gear.Select(GearUpload).ToList(),
                Array.Empty<ProfileSkillUpload>(),
                Array.Empty<ProfileBoardUpload>(),
                GearScore: window.GearScore > 0 ? window.GearScore : null,
                Titles: window.Titles?.Select(t => new ProfileTitleUpload(t.Slot, t.TitleId)).ToList(),
                Pets: window.Pets?.Select(k => new ProfilePetUpload(k.SpeciesId, k.Level, k.Kinds)).ToList(),
                BoardCounts: window.BoardCounts?.Select(b => new ProfileBoardCountUpload(b.BoardId, b.Count)).ToList());
        }

        if (directory.SeenProfileOf(objectId) is { } seen)
        {
            return new ProfileUpload(
                "seen",
                null,
                seen.ClassId,
                seen.ClassBits,
                seen.Gear.Select(GearUpload).ToList(),
                Array.Empty<ProfileSkillUpload>(),
                Array.Empty<ProfileBoardUpload>());
        }

        return null;
    }

    /// <summary>Opens (or brings forward) the character window beside the meter. It lives in its own
    /// window, wider than the meter, and redraws itself when the game re-sends the record.</summary>
    private void OnShowCharacterView(object sender, RoutedEventArgs e)
    {
        if (_source?.Entities is not Aion2.Aion2EntityDirectory directory)
        {
            return;
        }

        if (_characterWindow is { IsLoaded: true })
        {
            _characterWindow.Activate();
            return;
        }

        _characterWindow = new CharacterWindow(directory) { Owner = this };
        _characterWindow.Closed += (_, _) => _characterWindow = null;
        _characterWindow.PlaceBeside(this);
        _characterWindow.Show();
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        AppMenu.IsSubmenuOpen = false;
        Close();
    }

    private void OnMinimizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>The client's own ✕: while the overlay is on, the client goes to the tray and the meter goes on over the game (said once); the
    /// App menu's Exit and the tray's Exit end it. Without the overlay it ends the meter, as before.</summary>
    private void OnTitleCloseClicked(object sender, RoutedEventArgs e)
    {
        if (!MeterOverlayActive || _trayIcon is null)
        {
            Close();
            return;
        }

        var settings = MeterSettings.Load();
        if (!settings.CloseToTrayHintShown)
        {
            settings.CloseToTrayHintShown = true;
            settings.Save();
            var loc = LocalizationManager.Instance;
            ThemedMessageBox.Show(this, loc["Main.CloseToTray.Text"], loc["Main.CloseToTray.Title"], MessageBoxButton.OK, MessageBoxImage.Information);
        }

        _trayIcon.HideWindow();
    }

    private void OnHideUiClicked(object sender, RoutedEventArgs e)
    {
        if (_meterOverlay is not null)
        {
            ToggleOverlay();
            return;
        }

        var settings = MeterSettings.Load();
        if (!_hideUiActive && !settings.HideUiHintDismissed)
        {
            // the window is about to become see-through and click-through: say how to get it back
            var loc = LocalizationManager.Instance;
            string key = HotkeyBinding.Parse(settings.HotkeyHideUi).ToString();
            string text = string.IsNullOrWhiteSpace(settings.HotkeyHideUi) ? loc["Main.HideUiHint.NoHotkey"] : string.Format(loc["Main.HideUiHint"], key);
            ThemedMessageBox.Show(this, text, loc["Main.HideUiHint.Title"], MessageBoxButton.OK, MessageBoxImage.Information,
                loc["Main.HideUiHint.DontShow"], dismissed =>
                {
                    if (dismissed)
                    {
                        var s = MeterSettings.Load();
                        s.HideUiHintDismissed = true;
                        s.Save();
                    }
                });
        }

        SetHideUi();
    }

    /// <summary>
    /// Found by the user, comparing against their Timetable project's overlay: click-through
    /// alone isn't enough for an overlay that's meant to sit on top of the game. A click-through
    /// window that isn't also topmost can end up BEHIND the game, at which point it may as well
    /// not exist; hiding the taskbar entry while it's active matches the same "this is an overlay
    /// right now, not a normal window" framing. Both are restored to whatever they were before the
    /// moment Hide UI is toggled back off, rather than forced permanently.
    /// </summary>
    /// <summary>The picture Windows shows for the transparent overlay in Alt+Tab and the taskbar: the
    /// overlay's chips on the theme's window colour, scaled to fit the size asked for (0 = actual size).</summary>
    private System.Drawing.Bitmap? RenderOverlayPreview(int maxWidth, int maxHeight)
    {
        try
        {
            int width = Math.Max((int)Math.Ceiling(ActualWidth), 1);
            int height = Math.Max((int)Math.Ceiling(ActualHeight), 1);
            double scale = maxWidth > 0 && maxHeight > 0 ? Math.Min(Math.Min((double)maxWidth / width, (double)maxHeight / height), 1.0) : 1.0;
            int outWidth = Math.Max((int)(width * scale), 1);
            int outHeight = Math.Max((int)(height * scale), 1);

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawRectangle((Brush)FindResource("Brush.Window"), null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(_compactOverlay ? CompactOverlayPanel : OverlayContent) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(0, 0, width, height));
            }

            var target = new RenderTargetBitmap(outWidth, outHeight, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            using var decoded = new System.Drawing.Bitmap(stream);
            return new System.Drawing.Bitmap(decoded);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ExternalException or ResourceReferenceKeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>In overlay mode the chips keep the player list's left edge and width, but move up to the
    /// window's top edge, where the title bar ("Aion DPS" + version) was; switching drops the frame and
    /// nothing changes size. Measured while the normal content is still visible; kept for the time it is hidden.</summary>
    private double _overlayLeft, _overlayRightInset;

    private void UpdateOverlayWidth()
    {
        if (NormalContent.Visibility == Visibility.Visible && PlayersGrid.IsVisible && PlayersGrid.ActualWidth > 0)
        {
            Point origin = PlayersGrid.TranslatePoint(new Point(0, 0), this);
            _overlayLeft = origin.X;
            _overlayRightInset = Math.Max(0, ActualWidth - origin.X - PlayersGrid.ActualWidth);
        }

        OverlayContent.Margin = new Thickness(_overlayLeft, 4, 0, 0);
        OverlayContent.Width = Math.Max(ActualWidth - _overlayLeft - _overlayRightInset, 120);
    }

    private void SetHideUi()
    {
        UpdateOverlayWidth();
        _hideUiActive = !_hideUiActive;
        NormalContent.Visibility = _hideUiActive ? Visibility.Collapsed : Visibility.Visible;
        ShowOverlayPanels();
        if (_hideUiActive && _compactOverlay)
        {
            FollowNewestRun();
        }

        // The scope filter only applies to the compact overlay, so the rows are redone on every switch.
        RefreshRows();

        _overlay?.SetIconicPreview(_hideUiActive ? RenderOverlayPreview : null);

        // Per the user: the corner resize-grip glyph (from the window's own
        // ResizeMode="CanResizeWithGrip", not anything drawn by NormalContent) stayed visible even
        // once the rest of the chrome vanished, floating over the game with nothing around it to
        // explain what it was. NoResize removes the glyph along with the ability to drag-resize,
        // which is fine here: the window is click-through while this mode is active, so a mouse
        // couldn't reach the grip to drag it anyway.
        ResizeMode = _hideUiActive ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;

        if (_hideUiActive)
        {
            _topmostBeforeHideUi = Topmost;
            Topmost = true;
            // Stays in the taskbar in both looks: its right-click "Close" and a click on it are
            // ways to end the meter or bring it back besides the hotkey.
            ShowInTaskbar = true;
        }
        else
        {
            Topmost = _topmostBeforeHideUi;
            ShowInTaskbar = true;
        }

        FitWindowToCompactOverlay(_hideUiActive && _compactOverlay);
    }

    /// <summary>Builds the hotkey overlay from the settings (startup, after Settings); whether it is on screen right now is up to <see cref="OnGateTick"/>.</summary>
    private void ApplyHotkeyOverlay(MeterSettings settings)
    {
        if (Headless)
        {
            return;
        }

        if (!settings.ShowHotkeyOverlay)
        {
            _hotkeyOverlay?.ShowOverlay(false);
            return;
        }

        _hotkeyOverlay ??= new HotkeyOverlayWindow();
        _hotkeyOverlay.ApplyLocked(settings.HotkeyOverlayLocked ?? settings.PetFarmLocked);
        _hotkeyOverlay.Render(HotkeyOverlayWindow.RowsOf(settings, HotkeyProblems));
        OnGateTick();
    }

    private DateTime _gateSettingsAt = DateTime.MinValue;
    private MeterSettings _gateSettings = new();

    /// <summary>A few times a second: the hotkey overlay and the DPS overlay (Hide UI) are on screen only while the game is in front
    /// (the other overlays too, locked or not: never on the desktop).</summary>
    private void OnGateTick()
    {
        bool inGame = GameWindow.ForegroundIsGameOrSelf();
        if (inGame != _gameGateOpen)
        {
            _gameGateOpen = inGame;
            ShowOverlayPanels();
        }

        UpdateOverlayDummy();
        AnnounceBlackCloudRenewal();
        UpdateOverlayBossCallout();
        UpdateOverlayStagger();
        UpdateOverlayCubeCallout();
        UpdateOverlayBuffAlert();
        UpdateOverlayIdentity();
        UpdateOverlayTickets();
        UpdateOverlayFold();
        UpdatePetMapButton(_gateSettings);
        AudioOutput.Muted = _gateSettings.MuteSounds;
        UpdateMeterOverlay();

        if ((DateTime.UtcNow - _gateSettingsAt).TotalSeconds >= 1)
        {
            _gateSettingsAt = DateTime.UtcNow;
            _gateSettings = MeterSettings.Load();
        }

        var s = _gateSettings;
        // locked, the timetable steps aside like the other overlays: not while the game is behind another program or a game menu is open
        _timetable?.ApplyGate(GameWindow.OverlayMayShow());
        _petFarm?.ApplyGate(GameWindow.OverlayMayShow()); // like every overlay: not while one of the game's windows is open
        GateOverlayMenu(s);
        if (_hotkeyOverlay is null)
        {
            return;
        }

        _hotkeyOverlay.ApplyLockedIfChanged(s.HotkeyOverlayLocked ?? s.PetFarmLocked);
        _hotkeyOverlay.ShowOverlay(s.ShowHotkeyOverlay && GameWindow.OverlayMayShow());
    }

    /// <summary>Shows or hides the timetable overlay as Settings say (startup, after Settings, hotkey).</summary>
    private void ApplyTimetable(MeterSettings settings)
    {
        if (Headless)
        {
            return;
        }

        if (!settings.ShowTimetable)
        {
            _timetable?.Hide();
            return;
        }

        _timetable ??= new TimetableWindow();
        _timetable.ApplyOpacity(settings.TimetableOpacity ?? settings.OverlayOpacity);
        _timetable.ApplyLookahead(settings.TimetableLookaheadMinutes);
        _timetable.ApplyLockedIfChanged(settings.TimetableLocked);
        if (!_timetable.IsVisible)
        {
            _timetable.Show();
            _timetable.ApplyLocked(settings.TimetableLocked); // the click-through style is set again after the first show
        }

        _timetable.Refresh();
    }

    /// <summary>Starts or stops the pet farming overlay as Settings say (see PetFarmWindow).</summary>
    private void ApplyPetFarm(MeterSettings settings)
    {
        if (Headless)
        {
            return;
        }

        if (PrivacyConsent.Restricted)
        {
            return; // no pet map and no pet farm in the restricted mode (ApplyPrivacyRestrictions has stopped them)
        }

        _petMapController?.ClearCubeOverride(); // the settings were applied: a map shown for a cube goes back to what they say
        // the pet map (with its list) has its own switch; the cube finder's sound runs in it too, with the map's switch off as well (nothing is shown then)
        if (settings.ShowPetMap || settings.CubeArrivalSound || settings.CubeBeeps || settings.CubePopup || settings.ShowMapOnPet)
        {
            _petMapController ??= new PetMapController(() => _source?.Entities as Aion2EntityDirectory);
            _petMapController.ApplyLocked(settings.PetFarmLocked);
        }
        else if (_petMapController is not null && !_petMapController.HasViews)
        {
            _petMapController.Dispose();
            _petMapController = null;
        }

        if (!settings.ShowPetFarm)
        {
            _petFarmController?.Dispose();
            _petFarmController = null;
            _petFarm?.Hide();
            return;
        }

        _petFarm ??= new PetFarmWindow();
        _petFarm.ApplyOpacity(settings.OverlayOpacities.GetValueOrDefault(OverlayPlaces.PetInfo, settings.OverlayOpacity));
        _petFarm.ApplyLocked(settings.PetInfoLocked);
        _petFarmController ??= new PetFarmController(_petFarm, () => _source?.Entities as Aion2EntityDirectory);
    }

    private void TogglePetMap()
    {
        var settings = MeterSettings.Load();
        settings.ShowPetMap = !settings.ShowPetMap;
        settings.Save();
        ApplyPetFarm(settings);
        _settingsWindow?.SyncOverlaySwitches(settings);
    }

    private void ToggleTimetable()
    {
        var settings = MeterSettings.Load();
        settings.ShowTimetable = !settings.ShowTimetable; // the switch, not whether it is on screen: it steps aside while a game menu is open
        settings.Save();
        ApplyTimetable(settings);
        _settingsWindow?.SyncOverlaySwitches(settings);
    }

    private void ToggleHotkeyOverlay()
    {
        var settings = MeterSettings.Load();
        settings.ShowHotkeyOverlay = !settings.ShowHotkeyOverlay;
        settings.Save();
        ApplyHotkeyOverlay(settings);
        _settingsWindow?.SyncOverlaySwitches(settings);
    }

    /// <summary>The map controller reads the settings every second, so the switch is all it takes.</summary>
    private void TogglePetArrows()
    {
        var settings = MeterSettings.Load();
        settings.ShowPetArrows = !settings.ShowPetArrows;
        settings.Save();
        _settingsWindow?.SyncOverlaySwitches(settings);
    }

    private void ToggleGatherArrows()
    {
        var settings = MeterSettings.Load();
        settings.ShowGatherArrows = !settings.ShowGatherArrows;
        settings.Save();
        _settingsWindow?.SyncOverlaySwitches(settings);
    }

    /// <summary>The world boss controller reads the settings every second, so the switch is all it takes.</summary>
    private void ToggleWorldBossOverlay()
    {
        var settings = MeterSettings.Load();
        settings.ShowBossOverlay = !settings.ShowBossOverlay;
        settings.Save();
        _settingsWindow?.SyncOverlaySwitches(settings);
    }

    /// <summary>The network controller reads the settings every second, so the switch is all it takes.</summary>
    private void ToggleNetworkOverlay()
    {
        var settings = MeterSettings.Load();
        settings.ShowNetworkOverlay = !settings.ShowNetworkOverlay;
        settings.Save();
        _settingsWindow?.SyncOverlaySwitches(settings);
    }

    /// <summary>The assist controller reads the settings every second, so the switch is all it takes.</summary>
    private void ToggleAssistOverlay()
    {
        var settings = MeterSettings.Load();
        // the hotkey switches both assist overlays (cooldowns and effects): on when either was off
        bool on = !(settings.ShowAssistOverlay || settings.EffectsOverlayOn);
        settings.ShowAssistOverlay = on;
        settings.ShowEffectsOverlay = on;
        settings.Save();
        _settingsWindow?.SyncOverlaySwitches(settings);
    }

    /// <summary>The loot controller reads the settings every second, so the switch is all it takes.</summary>
    private void ToggleLootOverlay()
    {
        var settings = MeterSettings.Load();
        settings.ShowLootOverlay = !settings.ShowLootOverlay;
        settings.Save();
        _settingsWindow?.SyncOverlaySwitches(settings);
    }

    /// <summary>The crafting controller reads the settings every second, so the switch is all it takes.</summary>
    private void ToggleCraftOverlay()
    {
        var settings = MeterSettings.Load();
        settings.ShowCraftOverlay = !settings.ShowCraftOverlay;
        settings.Save();
        _settingsWindow?.SyncOverlaySwitches(settings);
    }

    /// <summary>The combat controller reads the settings every second, so the switch is all it takes.</summary>
    private void ToggleCombatOverlay()
    {
        var settings = MeterSettings.Load();
        settings.ShowCombatOverlay = !settings.ShowCombatOverlay;
        settings.Save();
        _settingsWindow?.SyncOverlaySwitches(settings);
    }

    /// <summary>Which overlays the "all overlays" hotkey switched off, so the next press brings back exactly those.</summary>
    private (bool Hotkeys, bool Timetable, bool PetMap, bool PetFarm, bool WorldBoss, bool Network, bool Combat, bool Dummy, bool Assist, bool Loot, bool Gather, bool Season)? _overlaysSwitchedOff;

    /// <summary>The effects overlay's own switch as it was before the "all overlays" hotkey switched it off (null: it follows the assist overlay).</summary>
    private bool? _effectsSwitchedOff;

    /// <summary>One press switches every overlay that is on off (hotkey list, timetable, interactive map, pet info); the next brings the same ones back.
    /// The DPS overlay is not among them - it is the Hide UI mode with its own hotkey.</summary>
    private void ToggleAllOverlays()
    {
        var settings = MeterSettings.Load();
        bool anyOn = settings.ShowHotkeyOverlay || settings.ShowTimetable || settings.ShowPetMap || settings.ShowPetFarm || settings.ShowBossOverlay || settings.ShowNetworkOverlay || settings.ShowCombatOverlay || settings.ShowDummyOverlay || settings.ShowAssistOverlay || settings.ShowLootOverlay || settings.EffectsOverlayOn || settings.ShowGatherOverlay || settings.ShowSeasonOverlay;
        if (anyOn)
        {
            _effectsSwitchedOff = settings.ShowEffectsOverlay;
            settings.ShowEffectsOverlay = false;
            _overlaysSwitchedOff = (settings.ShowHotkeyOverlay, settings.ShowTimetable, settings.ShowPetMap, settings.ShowPetFarm, settings.ShowBossOverlay, settings.ShowNetworkOverlay, settings.ShowCombatOverlay, settings.ShowDummyOverlay, settings.ShowAssistOverlay, settings.ShowLootOverlay, settings.ShowGatherOverlay, settings.ShowSeasonOverlay);
            settings.ShowHotkeyOverlay = settings.ShowTimetable = settings.ShowPetMap = settings.ShowPetFarm = settings.ShowBossOverlay = settings.ShowNetworkOverlay = settings.ShowCombatOverlay = settings.ShowDummyOverlay = settings.ShowAssistOverlay = settings.ShowLootOverlay = settings.ShowGatherOverlay = settings.ShowSeasonOverlay = false;
        }
        else
        {
            var back = _overlaysSwitchedOff ?? (true, true, true, false, true, true, true, true, false, false, false, false);
            (settings.ShowHotkeyOverlay, settings.ShowTimetable, settings.ShowPetMap, settings.ShowPetFarm, settings.ShowBossOverlay, settings.ShowNetworkOverlay, settings.ShowCombatOverlay, settings.ShowDummyOverlay, settings.ShowAssistOverlay, settings.ShowLootOverlay, settings.ShowGatherOverlay, settings.ShowSeasonOverlay) = back;
            settings.ShowEffectsOverlay = _effectsSwitchedOff;
            _overlaysSwitchedOff = null;
            _effectsSwitchedOff = null;
        }

        settings.Save();
        ApplyHotkeyOverlay(settings);
        ApplyTimetable(settings);
        ApplyPetFarm(settings);
        _settingsWindow?.SyncOverlaySwitches(settings);
    }

    /// <summary>Settings changed while Hide UI is up: switches to the look now chosen.</summary>
    private void ApplyOverlayLook()
    {
        if (_meterOverlay is not null && _compactOverlay)
        {
            // the compact look lives in the overlay window: the client comes back as a window
            SetHideUi();
            return;
        }

        ShowOverlayPanels();
        ShowInTaskbar = true;
        FitWindowToCompactOverlay(_compactOverlay);
        if (_compactOverlay)
        {
            FollowNewestRun();
        }

        RefreshRows();
    }

    /// <summary>Which Hide-UI look is up: one chip per player or the compact panel (Settings).</summary>
    private void ShowOverlayPanels()
    {
        // outside the game (a browser in front, the desktop) the overlay is not drawn; its window stays, so the taskbar button still brings it back
        OverlayContent.Visibility = _hideUiActive && !_compactOverlay && _gameGateOpen ? Visibility.Visible : Visibility.Collapsed;
        if (_meterOverlay is null)
        {
            CompactOverlayPanel.Visibility = _hideUiActive && _compactOverlay && _gameGateOpen ? Visibility.Visible : Visibility.Collapsed;
        }

        // The chips let every click through to the game. The compact panel takes clicks (a player's
        // skills, dragging it into place) unless "click-through" is chosen in Settings; the window is
        // truly transparent around it, so the rest of the screen still reaches the game.
        _overlay?.SetClickThrough(_hideUiActive && (!_compactOverlay || _compactClickThrough));
        RefreshUpdateBadge();
    }

    // --- The update hint in the transparent (Hide UI) modes: the status row with its notice is hidden there. ---
    private string? _updateBadgeFull;
    private string? _updateBadgeShort;
    private bool _clientOutdated;

    /// <summary>Remembers what the update check says (null = nothing to show) and puts it on the overlays.</summary>
    private void SetUpdateStatus(string? fullText, string? shortText)
    {
        _updateBadgeFull = fullText;
        _updateBadgeShort = shortText;
        RefreshUpdateBadge();
    }

    private void RefreshUpdateBadge()
    {
        var loc = LocalizationManager.Instance;
        // Only an outdated client (it cannot upload until it is updated) is worth a badge on the
        // transparent overlay; a merely available update stays quiet there while the client is
        // still new enough, the status row and the restart prompt carry it.
        string? full = _clientOutdated ? loc["Overlay.OutdatedBadge"] : null;
        string? small = _clientOutdated ? loc["Overlay.OutdatedBadgeShort"] : null;
        bool show = full is not null && (_hideUiActive || MeterOverlayActive) && _gameGateOpen;
        var brush = (Brush)FindResource(_clientOutdated ? "Brush.Warning" : "Brush.Accent");
        CompactUpdateBadge.Visibility = show && (_compactOverlay || MeterOverlayActive) ? Visibility.Visible : Visibility.Collapsed;
        OverlayUpdateBadge.Visibility = show && !_compactOverlay && _hideUiActive ? Visibility.Visible : Visibility.Collapsed;
        OverlayUpdateBadge.Background = brush;
        CompactUpdateBadge.Background = brush;
        OverlayUpdateBadgeText.Text = full ?? "";
        CompactUpdateBadgeText.Text = small ?? "";
        CompactUpdateBadge.ToolTip = full;

        // the title bar's update button, on every page: on its way / ready, else offered and put off, else this version is too old to upload
        string? title = _updateBadgeFull
            ?? (_availableUpdate is { } offered && _downloadedUpdate is null ? loc["Main.UpdateAvailable.Title"] + " " + offered.TargetFullRelease.Version : null)
            ?? (_clientOutdated ? loc["Overlay.OutdatedBadge"] : null);
        TitleUpdatePill.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        TitleCheckUpdates.Visibility = title is null ? Visibility.Visible : Visibility.Collapsed; // the pill takes its place
        TitleUpdateText.Text = title ?? "";
        TitleUpdatePill.Background = (Brush)FindResource(_clientOutdated && _updateBadgeFull is null ? "Brush.Warning" : "Brush.Accent");
    }

    private bool _titleCheckBusy;

    /// <summary>
    /// The title bar's "Check for updates": looks now. An update found is fetched and offered as from the App menu (and the update pill takes the button's place);
    /// none: "Up to date (version)" on the button for a few seconds. A check already running (the two-minute one) is waited for first.
    /// </summary>
    private async void OnTitleCheckUpdatesClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // a click on it, not the start of dragging the window
        if (_titleCheckBusy)
        {
            return;
        }

        var loc = LocalizationManager.Instance;
        _titleCheckBusy = true;
        bool current = false;
        TitleCheckUpdatesText.Text = loc["Main.UpdateChecking"];
        try
        {
            while (_updateCheckRunning)
            {
                await Task.Delay(200);
            }

            await RunUpdateCheck(announceResult: true, upToDate: () => current = true);
        }
        finally
        {
            if (current)
            {
                TitleCheckUpdatesText.Text = "✓ " + string.Format(loc["Main.UpToDate"], AppVersion.Text);
                await Task.Delay(TimeSpan.FromSeconds(4));
            }

            TitleCheckUpdatesText.Text = loc["Main.MenuApp.CheckForUpdates"];
            _titleCheckBusy = false;
        }
    }

    /// <summary>The title bar's update button: restart into the update that is ready, or ask to install the one offered, or look for one now.</summary>
    private void OnTitleUpdateClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // a click on it, not the start of dragging the window
        if (_downloadedUpdate is { } ready)
        {
            OfferRestart(ready, ready.TargetFullRelease.Version.ToString());
        }
        else if (_availableUpdate is { } offered)
        {
            OfferInstall(offered, offered.TargetFullRelease.Version.ToString());
        }
        else if (_updateBadgeFull is null)
        {
            _ = RunUpdateCheck(announceResult: true);
        }
    }

    private const double MinOverlayScale = 0.7, MaxOverlayScale = 2.0;

    private void SetCompactOverlayScale(double scale)
    {
        scale = Math.Clamp(double.IsFinite(scale) ? scale : 1.0, MinOverlayScale, MaxOverlayScale);
        CompactOverlayScale.ScaleX = scale;
        CompactOverlayScale.ScaleY = scale;
    }

    private const double MinOverlayWidth = 240, MaxOverlayWidth = 1400, MinOverlayRowsHeight = 40, MaxOverlayRowsHeight = 1400;

    /// <summary>The width the player dragged the meter to (before its scale), 0 for the width its look needs (MeterSettings.OverlayWidth).</summary>
    private double _overlayUserWidth;

    /// <summary>The meter's width: what its look needs, plus what the player dragged on (or took off).</summary>
    /// <summary>The bar's own width (the character, the clock, the cube, the pill and seven buttons): a narrower look widens to it (5 November 2026).</summary>
    private const double OverlayBarWidth = 470;

    private static double BaseOverlayWidth(MeterLook look) => Math.Max(look.PanelWidth, OverlayBarWidth);

    /// <summary>The meter's width: the player's own when they dragged it, else what its look needs. The player's width stays whatever columns are switched on
    /// (6 November 2026, the owner: "the meter should not exceed the size set by the user ... just compress instead"): the lines fit themselves to it.</summary>
    private double CompactOverlayWidth(MeterLook look) => Math.Clamp(_overlayUserWidth > 0 ? _overlayUserWidth : BaseOverlayWidth(look), MinOverlayWidth, MaxOverlayWidth);

    /// <summary>The meter's own size, before its scale: the player's width (0: the look's), and the height of its player lines (0: as tall as the lines are).
    /// Lines that do not fit a shorter height are cut off at the bottom (the lowest rates).</summary>
    private void SetCompactOverlaySize(double width, double rowsHeight)
    {
        _overlayUserWidth = double.IsFinite(width) && width > 0 ? Math.Clamp(width, MinOverlayWidth, MaxOverlayWidth) : 0;
        CompactOverlayPanel.Width = CompactOverlayWidth(_meterLook);
        _overlayRowsCap = double.IsFinite(rowsHeight) && rowsHeight > 0 ? Math.Clamp(rowsHeight, MinOverlayRowsHeight, MaxOverlayRowsHeight) : 0;
        FitMeterLines();
        FitRowsArea();
    }

    /// <summary>The most lines the meter grows to before it cuts the lowest rates off.</summary>
    private const int MaxMeterLines = 10;

    /// <summary>The height the player dragged the lines to: the most they take (0: none set).</summary>
    private double _overlayRowsCap;

    /// <summary>
    /// The lines take as much room as there are lines, up to ten (or less when the player dragged it shorter), and give it back as they go (7 November 2026,
    /// the owner: a meter dragged tall opened as a tall empty box in the Abyss; "let it extend naturally the more players that appear, capped at 10 and then
    /// go back to its normal size"). Before, a dragged height was a fixed one, full or empty.
    /// </summary>
    private void FitRowsArea()
    {
        double fit = CompactOverlayRows.LayoutTransform is ScaleTransform small ? small.ScaleY : 1; // lines drawn smaller to fit a narrow meter
        double titles = CompactOverlayTitles.Content is null ? 0 : (Math.Round(_meterLook.TinySize + 9) + 1) * fit;
        double ten = titles + MaxMeterLines * (_meterLook.RowHeight + _meterLook.Gap) * fit + 2;
        CompactOverlayRowsArea.Height = double.NaN;
        CompactOverlayRowsArea.MaxHeight = _overlayRowsCap > 0 ? Math.Min(_overlayRowsCap, ten) : ten;
    }

    /// <summary>
    /// The lines (and their titles) on a meter narrower than they need (<see cref="MeterLook.LineWidthNeeded"/>: many columns, a narrow width) are drawn
    /// smaller, all by the same amount so the columns stay in line, rather than pushing the rate, the share and the total past the meter's right edge. The
    /// name gives up its room first (it is cut), only then the text gets smaller; never below 70 %.
    /// </summary>
    private void FitMeterLines()
    {
        double available = CompactOverlayPanel.Width - 2; // its border
        double needed = _meterLook.LineWidthNeeded;
        double scale = needed > available && available > 0 ? Math.Max(0.7, available / needed) : 1;
        Transform fit = scale < 0.995 ? new ScaleTransform(scale, scale) : Transform.Identity;
        CompactOverlayRows.LayoutTransform = fit;
        CompactOverlayTitles.LayoutTransform = fit;
    }

    // The grip drag, measured on the screen from where it started (the grip itself moves, or not, as the meter grows: a meter that grows upwards keeps its
    // bottom edge, and the grip with it).
    private Point _resizeFrom;
    private double _resizeWidth, _resizeRowsHeight, _resizeScale;

    private Point MouseOnScreen()
    {
        Point device = OverlayResizeGrip.PointToScreen(Mouse.GetPosition(OverlayResizeGrip));
        return PresentationSource.FromVisual(OverlayResizeGrip)?.CompositionTarget?.TransformFromDevice.Transform(device) ?? device;
    }

    private void OnCompactOverlayResizeStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _resizeFrom = MouseOnScreen();
        _resizeWidth = CompactOverlayPanel.Width;
        _resizeRowsHeight = CompactOverlayRowsArea.ActualHeight;
        _resizeScale = CompactOverlayScale.ScaleX;
    }

    /// <summary>The compact overlay's corner grip: dragging sideways makes the meter wider or narrower, up or down shorter or longer (the text keeps its
    /// size); with Ctrl held it scales the whole meter, text and all, as before.</summary>
    private void OnCompactOverlayResize(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        Vector moved = MouseOnScreen() - _resizeFrom;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetCompactOverlayScale((_resizeWidth * _resizeScale + Math.Max(moved.X, moved.Y)) / _resizeWidth);
            return;
        }

        double scale = CompactOverlayScale.ScaleX;
        double rows = _resizeRowsHeight + moved.Y / scale;
        SetCompactOverlaySize(_resizeWidth + moved.X / scale, Math.Abs(moved.Y) < 2 ? _overlayRowsCap : rows);
    }

    /// <summary>A double-click on the grip: the meter's own width and height again (its scale stays).</summary>
    private void OnCompactOverlayResizeReset(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SetCompactOverlaySize(0, 0);
        OnCompactOverlayResized(sender, null!);
    }

    private void OnCompactOverlayResized(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var settings = MeterSettings.Load();
        settings.OverlayScale = CompactOverlayScale.ScaleX;
        settings.OverlayWidth = _overlayUserWidth;
        settings.OverlayExtraWidth = 0; // the old way, read once (OverlayWidth)
        settings.OverlayRowsHeight = _overlayRowsCap;
        settings.Save();
    }

    private void OnCompactOverlayDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        // a button of the header is no handle; a locked meter stays where it is
        for (DependencyObject? at = e.OriginalSource as DependencyObject; at is not null && !ReferenceEquals(at, sender); at = VisualTreeHelper.GetParent(at))
        {
            if (at is FrameworkElement { Cursor: var cursor } && cursor == Cursors.Hand)
            {
                return;
            }
        }

        if (_meterOverlayLocked && _meterOverlay is not null)
        {
            return;
        }

        if (_meterOverlay is not null)
        {
            _meterOverlay.DragFromHeader();
            return;
        }

        DragMove();
    }

    /// <summary>
    /// The dark menu, menu line, separator and tool tip styles for the whole app: a right-click menu (History, the meter's header menus) lives
    /// in a popup outside any window, and found only the system's white box before (with the light lines on it: unreadable).
    /// </summary>
    private void ShareMenuStyles()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        foreach (Type type in new[] { typeof(ContextMenu), typeof(MenuItem), typeof(Separator), typeof(ToolTip) })
        {
            if (Resources[type] is Style style && !app.Resources.Contains(type))
            {
                app.Resources[type] = style;
            }
        }
    }

    // ---- the meter's header menus: what is measured, past fights, lock, the look -------------------------------------------

    /// <summary>A menu under one of the meter's header buttons, in the app's menu style.</summary>
    private void OpenOverlayMenu(FrameworkElement anchor, IEnumerable<Control> items)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, VerticalOffset = 2 };
        if (TryFindResource(typeof(ContextMenu)) is Style menuStyle)
        {
            menu.Style = menuStyle;
        }

        foreach (Control item in items)
        {
            menu.Items.Add(item);
        }

        // in the game the mouse comes with Alt held: letting Alt go must not close the menu (WPF closes a menu on Alt; 5 November 2026, the owner)
        static void KeepOnAlt(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt) || e.Key is Key.LeftAlt or Key.RightAlt)
            {
                e.Handled = true;
            }
        }

        menu.PreviewKeyDown += KeepOnAlt;
        menu.PreviewKeyUp += KeepOnAlt;
        // the meter stays up while its menu is open and a while after (it may be between fights); nothing is raised over the menu meanwhile
        menu.Opened += (_, _) => _overlayMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _overlayMenuOpen = false;
            _overlayPeekUntil = DateTime.Now + _meterOverlayHideAfter;
            ShowHeaderButtons();
        };
        _overlayMenuOpen = true;   // at once, not only when the menu says it opened: a check in between must not put the meter (and its menu) away
        // a menu of a window that is not the active one closes at once when it cannot take the mouse (the game kept the focus): the meter's window
        // is made the active one first (a player: the mode menu closed instantly, 4 Nov 2026)
        if (_meterOverlay is { IsVisible: true } overlay && Window.GetWindow(anchor) == overlay && !overlay.IsActive)
        {
            overlay.Activate();
        }

        menu.IsOpen = true;
        if (!menu.IsOpen)
        {
            _overlayMenuOpen = false;
        }
    }

    private MenuItem OverlayItem(string header, Action act, bool check = false, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsChecked = check, IsEnabled = enabled };
        if (TryFindResource(typeof(MenuItem)) is Style itemStyle)
        {
            item.Style = itemStyle;
        }

        item.Click += (_, _) => act();
        return item;
    }

    private void OnOverlayModeMenuClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenOverlayMenu((FrameworkElement)sender, OverlayModeItems());
    }

    /// <summary>
    /// The pill's menu: what is measured; the training dummy (the card of the parse, a parse by itself at the first hit on a dummy, starting or ending one);
    /// Grind (the meter open in every fight, the open world counted too). Picking a measure during a dummy parse shows the lines instead of its card, the
    /// dummy line brings the card back (5 November 2026, the owner: "swapping to DPS/Dummy takes time", "confusing how the clicks work for dummy/open world").
    /// </summary>
    internal IList<Control> OverlayModeItems()
    {
        bool restricted = PrivacyConsent.Restricted;
        bool card = _dummyHeader && !_dummyCardHidden;
        void Pick(MeterMode mode)
        {
            _overlayBuffs = false;
            if (_overlayStagger)
            {
                SetStaggerView(false);
            }

            _dummyCardHidden = _dummyHeader;
            SetMode(mode);
            UpdateOverlayFold();
        }

        var settings = MeterSettings.Load();
        DummyState state = _dummy?.Model.State ?? DummyState.Idle;
        int seconds = Math.Clamp(settings.DummyTestSeconds, 10, 3600);
        string length = seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";
        var items = new List<Control>
        {
            OverlayItem("DPS", () => Pick(MeterMode.Damage), !card && !_overlayBuffs && !_overlayStagger && _mode == MeterMode.Damage),
            OverlayItem("rDPS (buffs credited)", () => Pick(MeterMode.Raid), !card && !_overlayBuffs && !_overlayStagger && _mode == MeterMode.Raid, !restricted),
            OverlayItem("Healing", () => Pick(MeterMode.Heal), !card && !_overlayBuffs && !_overlayStagger && _mode == MeterMode.Heal, !restricted),
            OverlayItem("Tanked (damage taken)", () => Pick(MeterMode.Taken), !card && !_overlayBuffs && !_overlayStagger && _mode == MeterMode.Taken, !restricted),
            OverlayItem("Buffs on you", () =>
            {
                if (_overlayStagger)
                {
                    SetStaggerView(false);
                }

                _overlayBuffs = true;
                _overlayBuffsAt = DateTime.MinValue;
                _dummyCardHidden = _dummyHeader;
                RefreshRows();
            }, !card && _overlayBuffs, !restricted),
            OverlayItem("All in one", () => Pick(MeterMode.All), !card && !_overlayBuffs && !_overlayStagger && _mode == MeterMode.All, !restricted),
            new Separator(),
        };

        if (_dummyHeader)
        {
            items.Add(OverlayItem("Dummy: the parse's card", () =>
            {
                _dummyCardHidden = false;
                RefreshRows();
                UpdateOverlayFold();
            }, card));
        }

        items.Add(OverlayItem($"Dummy: a {length} parse by itself when I hit a dummy", () => ChangeOverlaySetting(s => s.DummyAutoStart = !s.DummyAutoStart), settings.DummyAutoStart));
        items.Add(state is DummyState.Armed or DummyState.Running
            ? OverlayItem(state == DummyState.Running ? "Dummy: end the parse now (it is saved)" : "Dummy: take the waiting parse back", () => _dummy?.Model.Stop(DateTime.Now))
            : OverlayItem($"Dummy: start a {length} parse now (the clock starts with my first hit)", () =>
            {
                _dummyCardHidden = false;
                _dummy?.Start(TimeSpan.FromSeconds(seconds), "");
            }, false, _dummy is not null));
        items.Add(new Separator());
        items.Add(OverlayItem("Grind: every fight, trash and the open world too", () => ChangeOverlaySetting(s => s.MeterGrind = !s.MeterGrind), settings.MeterGrind));
        return items;
    }

    private void OnOverlayHistoryClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var items = new List<Control>
        {
            OverlayItem("Follow the current fight", () =>
            {
                _overlayPinned = false;
                FollowNewestRun();
            }),
            OverlayItem("All targets", () =>
            {
                _overlayPinned = true; // picked by hand: the meter stays open for it, boss or not
                MobBossFilter.SelectedItem = _mobBossAllItem;
            }, ReferenceEquals(MobBossFilter.SelectedItem, _mobBossAllItem)),
        };
        var fights = MobBossFilter.Items.OfType<ComboBoxItem>().Where(i => i.Tag is MobBossTag).ToList();
        if (fights.Count > 0)
        {
            items.Add(new Separator());
        }

        // the newest first: a run by its end, a whole target where the list has it
        foreach (ComboBoxItem fight in fights.OrderByDescending(i => ((MobBossTag)i.Tag).WindowEnd ?? DateTime.MinValue).Take(14))
        {
            ComboBoxItem chosen = fight;
            items.Add(OverlayItem(fight.Content as string ?? "?", () =>
            {
                _overlayPinned = true;
                MobBossFilter.SelectedItem = chosen;
            }, ReferenceEquals(MobBossFilter.SelectedItem, fight)));
        }

        items.Add(new Separator());
        items.Add(OverlayItem("Fight report of what is shown…", () =>
        {
            ShowClient();
            _ = OpenFightReportAsync();
        }));
        items.Add(OverlayItem("History: runs, boss fights, dummy parses…", OpenHistory));
        OpenOverlayMenu((FrameworkElement)sender, items);
    }

    private void OnOverlayLockClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var settings = MeterSettings.Load();
        settings.MeterOverlayLocked = _meterOverlayLocked = !_meterOverlayLocked;
        settings.Save();
        ApplyOverlayLock();
    }

    /// <summary>The lock's picture and the meter's handles: locked, the header no longer moves it and the corner grip is gone.</summary>
    private void ApplyOverlayLock()
    {
        OverlayLockGlyph.Text = _meterOverlayLocked ? "\uE72E" : "\uE785";
        OverlayLockButton.ToolTip = _meterOverlayLocked ? "Locked in place - click to move or scale the meter again" : "Lock the meter in place";
        OverlayLockGlyph.SetResourceReference(TextBlock.ForegroundProperty, _meterOverlayLocked ? "Brush.Accent" : "Brush.TextSubtle");
        OverlayResizeGrip.Visibility = _meterOverlayLocked ? Visibility.Collapsed : Visibility.Visible;
        OverlayHeader.Cursor = _meterOverlayLocked ? Cursors.Arrow : Cursors.SizeAll;
    }

    /// <summary>Saves one of the meter's own settings and applies the lot again.</summary>
    private void ChangeOverlaySetting(Action<MeterSettings> change)
    {
        var settings = MeterSettings.Load();
        change(settings);
        settings.Save();
        _gateSettings = settings; // the fold, Grind and the counters go by these at once
        _gateSettingsAt = DateTime.UtcNow;
        _overlayTicketsAt = DateTime.MinValue;
        RefreshCharacterSettings(settings);
        RefreshRows();
        UpdateMeterOverlay();
    }

    /// <summary>A click on a player's line in the compact overlay: that player's skill breakdown.</summary>
    /// <summary>The meter's own menu: its look (the site's colours or see-through), how opaque, click-through, when it shows, and the way to everything else.</summary>
    private void OnOverlaySettingsClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenOverlayMenu((FrameworkElement)sender, OverlaySettingsItems());
    }

    private MenuItem OverlaySubmenu(string header)
    {
        var item = new MenuItem { Header = header };
        if (TryFindResource(typeof(MenuItem)) is Style itemStyle)
        {
            item.Style = itemStyle;
        }

        return item;
    }

    /// <summary>
    /// The training dummy's part of the meter's menu: a timed parse when you hit a dummy (on or off), how long it runs, and starting or ending one by
    /// hand. The same as Settings' "Training dummy" card; a parse that ends is saved to the History (Training dummy).
    /// </summary>
    private MenuItem DummyMenu(MeterSettings settings)
    {
        DummyState state = _dummy?.Model.State ?? DummyState.Idle;
        int seconds = Math.Clamp(settings.DummyTestSeconds, 10, 3600);
        string length = seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";
        MenuItem menu = OverlaySubmenu(state switch
        {
            DummyState.Armed => $"Training dummy  ·  {length} parse, waiting for your first hit",
            DummyState.Running => $"Training dummy  ·  {length} parse running",
            _ => settings.DummyAutoStart ? $"Training dummy  ·  {length} parse" : "Training dummy  ·  off",
        });
        menu.Items.Add(OverlayItem("Start a parse when I hit a dummy", () => ChangeOverlaySetting(s => s.DummyAutoStart = !s.DummyAutoStart), settings.DummyAutoStart));
        menu.Items.Add(new Separator());
        foreach (int option in new[] { 30, 60, 120, 180, 300, 600 })
        {
            int chosen = option;
            string label = option % 60 == 0 ? $"{option / 60} minute{(option == 60 ? "" : "s")}" : $"{option} seconds";
            menu.Items.Add(OverlayItem(label, () => ChangeOverlaySetting(s => s.DummyTestSeconds = chosen), seconds == option, state is not (DummyState.Armed or DummyState.Running)));
        }

        menu.Items.Add(new Separator());
        if (state is DummyState.Armed or DummyState.Running)
        {
            menu.Items.Add(OverlayItem(state == DummyState.Running ? "End the parse now (it is saved)" : "Take the waiting parse back", () => _dummy?.Model.Stop(DateTime.Now)));
        }
        else
        {
            menu.Items.Add(OverlayItem($"Start a {length} parse now (the clock starts with your first hit)", () => _dummy?.Start(TimeSpan.FromSeconds(seconds), ""), enabled: _dummy is not null));
        }

        menu.Items.Add(OverlayItem("Past parses (History → Training dummy)", () => OpenHistory("Training dummy")));
        return menu;
    }

    private void OnOverlayCopyClicked(object sender, MouseButtonEventArgs e)
    {
        CopyRowsToClipboard();
        e.Handled = true;
    }

    private void OnOverlayResetClicked(object sender, MouseButtonEventArgs e)
    {
        // at a dummy the reset starts over too: the parse going on is dropped and the next hit starts a new one at once (5 November 2026, the owner:
        // "I cannot reset it, I have to wait for the dummy cooldown")
        ResetMeter();
        e.Handled = true;
    }

    /// <summary>The overlay's window button: the client (history, settings, the fight report) comes to the front.</summary>
    private void OnOverlayMinimizeClicked(object sender, MouseButtonEventArgs e)
    {
        if (_meterOverlay is null)
        {
            WindowState = WindowState.Minimized;
        }
        else
        {
            ShowClient();
        }

        e.Handled = true;
    }

    /// <summary>The overlay's ✕: the overlay goes off (the eye button, its hotkey or the tray bring it back); the meter goes on recording.</summary>
    private void OnOverlayCloseClicked(object sender, MouseButtonEventArgs e)
    {
        if (_meterOverlay is null)
        {
            Close();
        }
        else if (_meterOverlay.Wanted)
        {
            ToggleOverlay();
        }

        e.Handled = true;
    }

    private DispatcherTimer? _gameTitleTimer;
    private string? _gameTitleName;
    private bool _gameTitleBusy;

    /// <summary>Every 3 s: the character named in the game window's title ("AION2 | Name", see GameWindow) names the local player at once,
    /// mid-session too; the game's own character record still wins when it comes. Read off the UI thread; nothing when headless.</summary>
    private void StartGameTitleWatch()
    {
        if (Headless || _gameTitleTimer is not null)
        {
            return;
        }

        _gameTitleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _gameTitleTimer.Tick += async (_, _) =>
        {
            if (_gameTitleBusy || _liveSource?.Entities is not Aion2.Aion2EntityDirectory entities)
            {
                return;
            }

            _gameTitleBusy = true;
            try
            {
                string? name = await Task.Run(Aion2.GameWindow.CharacterName);
                if (name is not null && name != _gameTitleName)
                {
                    _gameTitleName = name;
                    entities.SetConfiguredLocalName(name);
                }
            }
            catch (Exception)
            {
                // the window list could not be read this time
            }
            finally
            {
                _gameTitleBusy = false;
            }
        };
        _gameTitleTimer.Start();
    }

    private static readonly SolidColorBrush ScopeOnBrush = MakeFrozen(Color.FromArgb(0x2B, 0xDC, 0xB8, 0x77));
    private static readonly SolidColorBrush ScopeOnText = MakeFrozen(Color.FromRgb(0xE6, 0xC8, 0x8F));
    private static readonly SolidColorBrush OverlayLiveBrush = MakeFrozen(Color.FromRgb(0x8F, 0xD1, 0x9A));
    private DateTime _overlayPingAt = DateTime.MinValue;

    private static SolidColorBrush MakeFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>The own character's name for the overlay's footer, once the meter knows it (or has recognised it by class).</summary>
    private string? CompactOverlayOwnName() =>
        _source?.Entities is not Aion2.Aion2EntityDirectory d ? null
        : d.LocalPlayerId >= 0 && d.NameFor(d.LocalPlayerId) is { } n && !n.StartsWith("Player #", StringComparison.Ordinal) ? n
        : d.LearnedLocalName ?? d.ConfiguredLocalName;

    /// <summary>The compact overlay's header: the target shown (or "All targets"), how long the
    /// fight has run, and whether the capture is running.</summary>
    private void UpdateCompactOverlay(IReadOnlyList<DamageEvent> shownHits, (int Count, DateTime First, DateTime Last)? summary = null)
    {
        if (!_compactOverlay && !MeterOverlayActive)
        {
            return;
        }

        // a dummy parse owns the header's time and name (its countdown, see UpdateOverlayDummy)
        if (!_dummyHeader)
        {
            OverlayTargetText.Text = _pvpOnly ? "PvP"
                : _selectedTargetId is int targetId ? ResolveDisplayName(targetId)
                : LocalizationManager.Instance["Main.FilterAllTargets"];
        }
        var strings = LocalizationManager.Instance;
        OverlayModeText.Text = _mode == MeterMode.Raid && !_pvpOnly ? "rDPS"
            : strings[_healMode ? "Main.Overlay.ModeHeal" : _takenMode ? "Main.Overlay.ModeTaken" : _allMode ? "Main.Overlay.ModeAll" : _allCompactMode ? "Main.Overlay.ModeAllCompact" : "Main.Overlay.ModeDamage"];
        OverlayRateHeader.Text = _allMode ? strings["Main.Overlay.ColDamage"] : _healMode ? "HPS" : _takenMode ? "DTPS" : _mode == MeterMode.Raid ? "rDPS" : "DPS";
        OverlayShareHeader.Text = _allMode ? strings["Main.Overlay.ColHeal"] : "%";
        OverlayTotalHeader.Text = _allMode ? strings["Main.Overlay.ColTaken"] : strings["Main.Overlay.ColTotal"];
        MeterTitles.Instance.Set(OverlayRateHeader.Text, OverlayShareHeader.Text, OverlayTotalHeader.Text, _mode == MeterMode.Raid && !_pvpOnly ? "DPS" : "rDPS");
        bool capturing = (_source as Aion2.Aion2PacketCombatSource)?.ServerFingerprint is not null;
        OverlayStateDot.Fill = _paused ? Brushes.Orange : capturing ? OverlayLiveBrush : Brushes.Gray;
        OverlayStateText.Text = _paused ? "Paused" : capturing ? "" : "Waiting for the game"; // live: the dot says it, the name is in the bar
        if (DateTime.UtcNow - _overlayPingAt >= TimeSpan.FromSeconds(1))
        {
            _overlayPingAt = DateTime.UtcNow;
            double? ping = capturing && !Headless ? Aion2.Capture.NetworkStats.Snapshot().PingMs : null;
            OverlayPingText.Text = ping is double ms ? $"{ms:0} ms" : "";
        }
        if (!_dummyHeader)
        {
            OverlayTimeText.Text = summary is { Count: > 1 } sum ? (sum.Last - sum.First).ToString(@"m\:ss")
                : summary is null && shownHits.Count > 1 ? (shownHits.Max(h => h.Timestamp) - shownHits.Min(h => h.Timestamp)).ToString(@"m\:ss")
                : "";
        }
        _lastShownHits = shownHits;
        UpdateOverlayBossBar(); // the boss's health over the lines, with when it dies

        // the header's mode word and the group's total in big
        string unit = _healMode ? "HPS" : _takenMode ? "DTPS" : _mode == MeterMode.Raid && !_pvpOnly ? "rDPS" : "DPS";
        string measured = _overlayStagger ? "Stagger" : _overlayBuffs ? "Buffs" : _allMode || _allCompactMode ? "All" : _takenMode ? "Tank" : _healMode ? "Heal" : _mode == MeterMode.Raid ? "rDPS" : "DPS";
        OverlayModeShort.Text = _dummyHeader && !_dummyCardHidden ? "Dummy" : _gateSettings.MeterGrind ? measured + " · Grind" : measured;
        if (!_overlayBuffs && !_overlayStagger)
        {
            double total = _rows.Where(r => !r.IsEnemy).Sum(r => (r.RdpsView ? r.Rdps ?? r.Dps : r.Dps) ?? 0);
            OverlayTotalRateText.Text = PlayerRow.Compact((long)Math.Round(total));
            OverlayTotalUnitText.Text = unit;
        }

        // the combat power after each name, from what the game showed of the player (every few seconds: it changes with a gear swap only)
        if (DateTime.UtcNow - _overlayPowerAt > TimeSpan.FromSeconds(3) && _source?.Entities is Aion2.Aion2EntityDirectory powers)
        {
            _overlayPowerAt = DateTime.UtcNow;
            foreach (PlayerRow row in _rows)
            {
                int id = _historyMode ? _historyLiveIds.GetValueOrDefault(row.ObjectId, row.ObjectId) : row.ObjectId;
                var known = _historyMode ? _liveSource?.Entities as Aion2.Aion2EntityDirectory : powers;
                int? power = known?.SeenProfileOf(id)?.CombatPower;
                if (power is not > 0 && known is not null && id >= 0 && id == known.LocalPlayerId)
                {
                    power = OwnCombatPower(known); // the game shows the own Combat Power nowhere the others' come from (5 November 2026: it was missing)
                }

                row.CombatPowerText = power is int cp and > 0 and not 397228 ? PlayerRow.Compact(cp) : "";
            }
        }

        UpdateOverlayBuffs(shownHits);
        _overlayBossShown = ShownIsBossFight(shownHits);
        UpdateOverlayIdentity();
        UpdateOverlayFold();
    }

    /// <summary>The meter's buff view: the effects on the own character over the fight shown (the last minute when nothing is shown), with
    /// their uptime, the helpful ones first. Worked out every second and a half at most.</summary>
    private void UpdateOverlayBuffs(IReadOnlyList<DamageEvent> shownHits)
    {
        CompactOverlayRows.Visibility = _overlayBuffs ? Visibility.Collapsed : Visibility.Visible;
        OverlayBuffRows.Visibility = _overlayBuffs ? Visibility.Visible : Visibility.Collapsed;
        OverlayScopeSwitch.Visibility = _overlayBuffs ? Visibility.Collapsed : Visibility.Visible;
        if (!_overlayBuffs || DateTime.UtcNow - _overlayBuffsAt < TimeSpan.FromSeconds(1.5) || ReplayDirectory is not { } directory)
        {
            return;
        }

        _overlayBuffsAt = DateTime.UtcNow;
        int you = directory.LocalPlayerId >= 0 ? directory.LocalPlayerId : directory.InferLocalPlayer() ?? -1;
        DateTime to = shownHits.Count > 0 ? shownHits.Max(h => h.Timestamp) : DateTime.Now;
        DateTime from = shownHits.Count > 0 ? shownHits.Min(h => h.Timestamp) : to.AddMinutes(-1);
        if (to - from < TimeSpan.FromSeconds(5))
        {
            from = to.AddSeconds(-5);
        }

        _overlayBuffRows.Clear();
        OverlayTotalRateText.Text = CompactOverlayOwnName() ?? "You";
        OverlayTotalUnitText.Text = "buffs";
        if (you < 0)
        {
            return;
        }

        var players = _rows.Select(r => r.ObjectId).ToHashSet();
        players.Add(you);
        var good = MakeFrozen(Color.FromRgb(0x5C, 0xC2, 0x7E));
        var bad = MakeFrozen(Color.FromRgb(0xE0, 0x6A, 0x5E));
        foreach (ReportEffect effect in FightReport.EffectsOn(directory.Combat.BuffsAround(from, to), you, players, from, to).Take(14))
        {
            string? icon = Aion2SkillIcons.PathFor(effect.SkillId) ?? Aion2SkillIcons.PathForName(effect.Name, null) ?? Aion2Artwork.EffectIconPath(effect.SkillId);
            _overlayBuffRows.Add(new OverlayBuffRow(effect.Name, OverlayPictures.Load(icon), effect.UptimePercent, $"{effect.UptimePercent:0}%",
                effect.Harmful ? bad : good, effect.Harmful ? bad : (Brush)FindResource("Brush.OverlayText")));
        }
    }

    /// <summary>The dummy run in the meter itself (no window of its own): "Dummy 1:42" with the time running out under it, "Run 3 saved" when over.
    /// A few times a second, from the game gate's timer.</summary>
    private void UpdateOverlayDummy()
    {
        if (_dummy is null)
        {
            return;
        }

        // the countdown lives in the meter's header only: the old window of its own never comes up while the meter exists
        _dummy.ShownInMeter = _meterOverlay is not null;
        DummyTest model = _dummy.Model;
        DateTime now = DateTime.Now;
        string? clock = null, label = null;
        double? fraction = null;
        switch (model.State)
        {
            case DummyState.Armed:
                TimeSpan planned = model.Planned;
                clock = $"{(int)planned.TotalMinutes}:{planned.Seconds:00}";
                label = "Dummy parse · hit to start";
                break;
            case DummyState.Running when model.Current is not null:
                TimeSpan left = model.Remaining(now);
                clock = $"{(int)left.TotalMinutes}:{left.Seconds:00}";
                label = "Dummy parse";
                fraction = model.Planned.TotalSeconds > 0 ? left.TotalSeconds / model.Planned.TotalSeconds : 0;
                break;
            case DummyState.Finished when model.Current is { } done:
                clock = "0:00";
                label = $"Parse {done.Number} saved to History";
                break;
        }

        bool active = clock is not null;
        if (active)
        {
            OverlayTimeText.Text = clock;
            OverlayTargetText.Text = label;
        }

        if (active != _dummyHeader)
        {
            _dummyHeader = active;
            _dummyCardHidden = false; // a new parse shows its card
            if (active)
            {
                OverlayTimeText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
            }
            else
            {
                OverlayTimeText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.OverlayText");
                RefreshRows(); // the fight's own time and name come back
            }
        }

        OverlayDummyChip.Visibility = Visibility.Collapsed;
        OverlayDummyBar.Visibility = fraction is null ? Visibility.Collapsed : Visibility.Visible;
        OverlayDummyBar.Value = fraction ?? 0;
        if (active)
        {
            FillDummyCard(model, now); // the card in the meter's body: the run, the DPS in big, every kind of hit
        }
    }

    private long _calloutAt;
    private BossCalloutFrame? _calloutFrame;
    private (int Entity, DateTime? Since, DateTime? First, long At) _calloutStart = (-1, null, null, 0);

    /// <summary>When the boss was first hit in this pull (the fight's start as the uploads count it), null before; worked out again when the boss or its pull changes.</summary>
    private DateTime? BossFirstHit(int entity, Aion2.Aion2EntityDirectory dir)
    {
        DateTime? since = dir.EngagedSince(entity);
        long tick = Environment.TickCount64;
        if (_calloutStart.Entity == entity && _calloutStart.Since == since && (_calloutStart.First is not null || tick - _calloutStart.At < 1000))
        {
            return _calloutStart.First;
        }

        DateTime from = (since ?? DateTime.MinValue).AddSeconds(since is null ? 0 : -60);
        DateTime? first = null;
        foreach (DamageEvent ev in _aggregator.Events)
        {
            if (!ev.IsHeal && ev.TargetObjectId == entity && ev.Timestamp >= from && (first is null || ev.Timestamp < first))
            {
                first = ev.Timestamp;
            }
        }

        _calloutStart = (entity, since, first, tick);
        return first;
    }

    /// <summary>The meter overlay's boss timers line (<see cref="BossTimers"/>): the boss's next two casts as everyone's uploads learnt them, red within 5 s
    /// and while cast, and the time to its rage where the game data knows it (amber the last minute, red the last 20 s). Hidden without a boss or anything known.</summary>
    private void UpdateOverlayBossCallout()
    {
        if (!_compactOverlay && !MeterOverlayActive)
        {
            return;
        }

        long tick = Environment.TickCount64;   // not the wall clock: a clock set back doesn't stop the line
        if (tick - _calloutAt < 240)
        {
            return;
        }

        _calloutAt = tick;
        DateTime now = DateTime.Now;
        // the site is asked for the timers only with the privacy policy accepted; without, the line shows the enrage from the game's own data
        BossCalloutFrame? frame = _gateSettings.OverlayBossTimers && !_historyMode && !_pvpOnly && _source?.Entities is Aion2.Aion2EntityDirectory dir
            ? BossTimers.Now(dir, now, LocalizationManager.Instance.Language, id => BossFirstHit(id, dir), PrivacyConsent.IsAccepted(_gateSettings)) : null;
        _calloutFrame = frame; // the stagger card's "next check" (MainWindow.Stagger.cs)
        if (frame is null || (frame.Next.Count == 0 && frame.Rage is null))
        {
            OverlayBossCallout.Visibility = Visibility.Collapsed;
            return;
        }

        ShowBossCallout(OverlayBossCalloutText, frame);
        var loc = LocalizationManager.Instance;
        OverlayBossCallout.ToolTip = frame.Fights > 0
            ? string.Format(loc["Overlay.BossTimersTip"], frame.Boss, frame.Fights) + (frame.Rage is null ? "" : loc["Overlay.BossTimersTipRage"])
            : string.Format(loc["Overlay.BossTimersRageOnly"], frame.Boss);
        OverlayBossCallout.Visibility = Visibility.Visible;
    }

    internal static void ShowBossCallout(TextBlock text, BossCalloutFrame frame)
    {
        var loc = LocalizationManager.Instance;
        text.Inlines.Clear();
        bool first = true;
        void Gap()
        {
            if (!first)
            {
                text.Inlines.Add(new System.Windows.Documents.Run("   ·   ") { Foreground = OverlayPictures.Muted });
            }

            first = false;
        }

        foreach (BossCallout c in frame.Next.Take(2))
        {
            Gap();
            text.Inlines.Add(new System.Windows.Documents.Run(c.Name) { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new System.Windows.Documents.Run("  " + (c.Casting ? loc["Overlay.BossNow"] : string.Format(loc["Overlay.BossIn"], Math.Ceiling(c.In))))
            {
                Foreground = c.Casting || c.In <= 5 ? OverlayPictures.Red : c.In <= 15 ? OverlayPictures.Amber : OverlayPictures.Muted,
                FontWeight = FontWeights.SemiBold,
            });
        }

        if (frame.Rage is TimeSpan rage)
        {
            Gap();
            text.Inlines.Add(new System.Windows.Documents.Run(rage > TimeSpan.Zero ? string.Format(loc["Overlay.Enrage"], $"{(int)rage.TotalMinutes}:{rage.Seconds:00}") : loc["Overlay.Enraged"])
            {
                Foreground = rage <= HpBarView.RageSoonBefore ? OverlayPictures.Red : rage <= HpBarView.RageWarnBefore ? OverlayPictures.Amber : OverlayPictures.Muted,
                FontWeight = FontWeights.SemiBold,
            });
        }
    }

    private bool _autoBossView;
    private int? _autoBossId;
    private DateTime? _autoBossDeadAt;

    /// <summary>A boss hit this recently counts as being fought.</summary>
    private static readonly TimeSpan AutoBossEngaged = TimeSpan.FromSeconds(8);

    /// <summary>How long the dead boss stays on screen before the table shows everything again (setting, default 10 s).</summary>
    private TimeSpan _autoBossAfterDeath = TimeSpan.FromSeconds(10);

    /// <summary>A boss nobody has hit for this long without dying (a wipe, the group left) gives the table back.</summary>
    private static readonly TimeSpan AutoBossAbandoned = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The "show the current boss automatically" option: everything is recorded all the time, but the
    /// Mob/Boss filter jumps to a boss as soon as it is attacked, stays on it for
    /// <see cref="_autoBossAfterDeath"/> after its death and then returns to "All". Only moves the
    /// filter on those changes, so a target picked by hand in between stays until the next one.
    /// </summary>
    private void FollowBossAutomatically()
    {
        if (!_autoBossView || Headless || _historyMode || _pvpOnly || !ViewingLive || _source?.Entities is not Aion2.Aion2EntityDirectory directory)
        {
            _autoBossId = null;
            _autoBossDeadAt = null;
            return;
        }

        DateTime now = DateTime.Now;
        if (_autoBossId is int current)
        {
            bool dead = directory.HitPoints.Latest(current) is { } latest && BossDied(directory, directory.HitPoints, current, latest);
            if (dead)
            {
                _autoBossDeadAt ??= now;
            }

            bool abandoned = !dead && _bossActivity.TryGetValue(current, out var seen) && now - seen.Last >= AutoBossAbandoned;
            if ((dead && now - _autoBossDeadAt >= _autoBossAfterDeath) || abandoned)
            {
                if (_selectedTargetId == current)
                {
                    MobBossFilter.SelectedItem = _mobBossAllItem;
                }

                _autoBossId = null;
                _autoBossDeadAt = null;
            }
        }

        int? engaged = null;
        DateTime engagedAt = DateTime.MinValue;
        foreach ((int bossId, (DateTime _, DateTime last)) in _bossActivity)
        {
            if (now - last > AutoBossEngaged || last <= engagedAt
                || (directory.HitPoints.Latest(bossId) is { } hp && BossDied(directory, directory.HitPoints, bossId, hp)))
            {
                continue;
            }

            engaged = bossId;
            engagedAt = last;
        }

        if (engaged is int boss && boss != _autoBossId)
        {
            // The newest entry of that boss (a boss farmed again comes as "#1".."#N").
            ComboBoxItem? entry = MobBossFilter.Items.OfType<ComboBoxItem>().LastOrDefault(item => item.Tag is MobBossTag tag && tag.TargetId == boss);
            if (entry is not null)
            {
                _autoBossId = boss;
                _autoBossDeadAt = null;
                if (!ReferenceEquals(MobBossFilter.SelectedItem, entry))
                {
                    MobBossFilter.SelectedItem = entry;
                }
            }
        }
    }

    /// <summary>While the compact overlay is up, keeps it on the newest fight in the Mob/Boss list
    /// (nobody can pick a target on it): the run whose latest hit is the most recent wins, a boss
    /// staying on screen while its adds die around it.</summary>
    private void FollowNewestRun()
    {
        if (_pvpOnly)
        {
            return;
        }

        ComboBoxItem? newest = null, newestBoss = null;
        DateTime newestHit = DateTime.MinValue, newestBossHit = DateTime.MinValue;
        var directory = _source?.Entities as Aion2.Aion2EntityDirectory;

        // One pass over the events for all entries of the list (the list may hold a dozen targets and runs): each event is matched against
        // the entries of its own target only, instead of every entry scanning every event.
        var entries = new List<(ComboBoxItem Item, MobBossTag Tag)>();
        var byTarget = new Dictionary<int, List<int>>();
        foreach (ComboBoxItem item in MobBossFilter.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is MobBossTag entryTag)
            {
                if (!byTarget.TryGetValue(entryTag.TargetId, out List<int>? slots))
                {
                    byTarget[entryTag.TargetId] = slots = new List<int>();
                }

                slots.Add(entries.Count);
                entries.Add((item, entryTag));
            }
        }

        var lastHits = new DateTime[entries.Count];
        // at a dummy the fight followed is the own one: the others hitting their dummies around do not pull the list away
        int onlySource = ScopeActive && ShownScope == MeterScope.Me && directory is not null
            ? (directory.LocalPlayerId >= 0 ? directory.LocalPlayerId : directory.InferLocalPlayer() ?? -1) : -1;
        foreach (DamageEvent ev in _aggregator.Events)
        {
            if (ev.IsHeal || (onlySource >= 0 && ev.SourceObjectId != onlySource) || !byTarget.TryGetValue(ev.TargetObjectId, out List<int>? slots))
            {
                continue;
            }

            foreach (int slot in slots)
            {
                MobBossTag tag = entries[slot].Tag;
                if (ev.Timestamp > lastHits[slot]
                    && (tag.WindowStart is not DateTime from || (ev.Timestamp >= from && ev.Timestamp <= tag.WindowEnd)))
                {
                    lastHits[slot] = ev.Timestamp;
                }
            }
        }

        for (int i = 0; i < entries.Count; i++)
        {
            (ComboBoxItem item, MobBossTag tag) = entries[i];
            DateTime last = lastHits[i];

            if (last > newestHit)
            {
                newestHit = last;
                newest = item;
            }

            if (last > newestBossHit && directory?.BossNpcIdOf(tag.TargetId) is not null)
            {
                newestBossHit = last;
                newestBoss = item;
            }
        }

        if (newestBoss is not null && newestBossHit >= newestHit - TimeSpan.FromSeconds(RunClusterGapSeconds))
        {
            newest = newestBoss;
        }

        if (newest is not null && !ReferenceEquals(newest, MobBossFilter.SelectedItem))
        {
            MobBossFilter.SelectedItem = newest;
        }
    }

    private (double Width, double Height, double MinWidth, double MinHeight)? _sizeBeforeCompactOverlay;

    /// <summary>While the compact overlay is up the window takes exactly the panel's size, so the
    /// panel can be scaled up past the normal window and nothing invisible sits over the game; the
    /// normal size comes back with the full window.</summary>
    private void FitWindowToCompactOverlay(bool fit)
    {
        if (fit && _sizeBeforeCompactOverlay is null)
        {
            _sizeBeforeCompactOverlay = (Width, Height, MinWidth, MinHeight);
            MinWidth = 0;
            MinHeight = 0;
            SizeToContent = SizeToContent.WidthAndHeight;
        }
        else if (!fit && _sizeBeforeCompactOverlay is { } before)
        {
            SizeToContent = SizeToContent.Manual;
            MinWidth = before.MinWidth;
            MinHeight = before.MinHeight;
            Width = before.Width;
            Height = before.Height;
            _sizeBeforeCompactOverlay = null;
        }
    }
}

/// <summary>One line of the meter's buff view.</summary>
public sealed record OverlayBuffRow(string Name, ImageSource? Icon, double Percent, string PercentText, Brush BarBrush, Brush NameBrush);
