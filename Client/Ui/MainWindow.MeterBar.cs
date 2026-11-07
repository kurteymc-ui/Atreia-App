using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AionDPS.Aion2;
using AionDPS.Combat;
using AionDPS.History;

namespace AionDPS.Ui;

/// <summary>One of the own counters at the meter's foot: its in-game icon (a path under the app's assets), the figure, the bonus on top ("+2,000"), a
/// little more (the time to the next refill) and its tip; <paramref name="Warn"/> when the next refill would be lost (the counter is full).</summary>
internal sealed record OverlayTicket(string Icon, string Value, string? Bonus, string? Extra, string Tip, bool Warn = false);

/// <summary>
/// The meter's bar and its fold (5 November 2026, the owner, with pictures of another meter: "shows nothing before it goes in a boss fight or dummy,
/// only my character's info, and a selector of what to show"; "include the energy, Shugo etc. at the bottom of the meter"; "the user's CP is hidden,
/// which should not be"). The bar is always there: the own class, name and Combat Power, the fight's clock, the nearest Hidden Cube, what is measured
/// and the buttons. Under it the fight opens in a boss fight, at a training dummy (a card with the parse's DPS in big and every kind of hit) and in the
/// Grind view; in between the meter is the bar and the own counters at its foot.
/// </summary>
public partial class MainWindow
{
    /// <summary>The fight on screen is a boss fight (UpdateCompactOverlay): the meter opens for it.</summary>
    private bool _overlayBossShown;

    /// <summary>A fight picked from the meter's history menu stays open, boss or not, until the meter starts from zero.</summary>
    private bool _overlayPinned;

    /// <summary>A boss fight or a training dummy was on screen since the meter last started from zero: the meter stays open on it (the result is read after
    /// the fight), and folds again only at a reset or a new map (5 November 2026, the owner: after the dummy "nothing else shows").</summary>
    private bool _overlayHadFight;

    /// <summary>A measure picked from the mode menu while a dummy parse is on: the lines show instead of the dummy's card, until the next parse.</summary>
    private bool _dummyCardHidden;

    private bool _meterShowTotal = true;
    private bool _meterShowStatus = true;
    private DateTime _overlayIdentityAt = DateTime.MinValue;
    private DateTime _overlayTicketsAt = DateTime.MinValue;
    private string _overlayTicketsKey = "";

    /// <summary>
    /// Opens or folds the meter: open in a boss fight, a dummy parse, the Grind view, the buff view, PvP, the history and a fight picked by hand, and all the
    /// time with the fold switched off. A dummy parse shows its card instead of the lines. The clock and the line before it go with the fold.
    /// </summary>
    private void UpdateOverlayFold()
    {
        if (Headless)
        {
            return; // the pictures set the parts themselves
        }

        MeterSettings s = _gateSettings;
        bool dummy = _dummyHeader && !_dummyCardHidden;
        bool atDummy = DateTime.Now - _lastDummyHit < TimeSpan.FromSeconds(20); // hitting a dummy opens the meter at once, a parse or not
        // a boss fight or the own PvP opens it only with lines to show (7 November 2026, a tester: it opened empty in the siege and while farming - a boss hit
        // nearby by others, nothing counted of the own)
        bool lines = _rows.Count > 0;
        bool pvp = DateTime.Now - _lastPvpFight < BossFightHold;
        if (((_overlayBossShown || pvp) && lines) || _dummyHeader || atDummy)
        {
            _overlayHadFight = true;
        }

        bool open = !s.MeterFoldIdle || _dummyHeader || atDummy || (_overlayHadFight && lines) || s.MeterGrind || _overlayBuffs || _overlayStagger || _historyMode || _pvpOnly
            || (_overlayBossShown && lines) || _overlayPinned;
        Show(OverlayBody, open);
        Show(OverlayDummyCard, dummy);
        Show(CompactOverlayRowsArea, !dummy);
        Show(OverlayTotalLine, _meterShowTotal && !dummy);
        Show(OverlayTargetText, !dummy && OverlayTargetText.Text.Length > 0);
        bool timed = open && OverlayTimeText.Text.Length > 0;
        Show(OverlayTimeText, timed);
        Show(OverlayBarDivider, timed || OverlayCubeCallout.Visibility == Visibility.Visible);
        Show(OverlayStatusLine, _meterShowStatus || OverlayTickets.Children.Count > 0);
    }

    private static void Show(UIElement element, bool shown)
    {
        Visibility wanted = shown ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != wanted)
        {
            element.Visibility = wanted;
        }
    }

    /// <summary>Whether the fight on screen is a boss fight: the target picked is a boss, or with all targets shown a boss was hit.</summary>
    private bool ShownIsBossFight(IReadOnlyList<DamageEvent> shownHits)
    {
        if (_source?.Entities is not Aion2EntityDirectory directory)
        {
            return false;
        }

        if (_selectedTargetId is int target)
        {
            return directory.BossNpcIdOf(target) is not null;
        }

        var asked = new HashSet<int>();
        for (int i = shownHits.Count - 1; i >= 0; i--)
        {
            int id = shownHits[i].TargetObjectId;
            if (!shownHits[i].IsHeal && asked.Add(id) && directory.BossNpcIdOf(id) is not null)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ you

    /// <summary>Once a second: the own class, name and Combat Power in the bar, from the live capture.</summary>
    private void UpdateOverlayIdentity()
    {
        if (Headless || DateTime.UtcNow - _overlayIdentityAt < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _overlayIdentityAt = DateTime.UtcNow;
        if (_liveSource?.Entities is not Aion2EntityDirectory directory)
        {
            ShowOverlayIdentity(null, null, null);
            return;
        }

        string? name = directory.LocalPlayerId >= 0 && directory.NameFor(directory.LocalPlayerId) is { } n && !n.StartsWith("Player #", StringComparison.Ordinal) ? n
            : directory.LocalCharacter?.Name is { Length: > 0 } own ? own : directory.LearnedLocalName ?? directory.ConfiguredLocalName;
        string? className = directory.LocalPlayerId >= 0 ? directory.ClassOf(directory.LocalPlayerId) : null;
        ShowOverlayIdentity(name, className, OwnCombatPower(directory));
    }

    /// <summary>The bar's own part: the class icon (the meter's mark while the class is not known), the name in the class colour and the Combat Power.</summary>
    internal void ShowOverlayIdentity(string? name, string? className, int? combatPower)
    {
        ImageSource? icon = className is { Length: > 0 } ? new ClassIconConverter().Convert(className, typeof(ImageSource), null, CultureInfo.CurrentCulture) as ImageSource : null;
        if (!ReferenceEquals(OverlayOwnIcon.Tag, className))
        {
            OverlayOwnIcon.Tag = className;
            OverlayOwnIcon.Source = icon;
        }

        Show(OverlayOwnIcon, icon is not null);
        Show(OverlayLogo, icon is null);
        string shown = name is { Length: > 0 } ? name : Brand.AppName;
        if (OverlayOwnName.Text != shown)
        {
            OverlayOwnName.Text = shown;
        }

        if (className is { Length: > 0 })
        {
            OverlayOwnName.Foreground = ClassBrushes.Text(className);
        }
        else
        {
            OverlayOwnName.SetResourceReference(TextBlock.ForegroundProperty, "Brush.OverlayText");
        }

        string cp = combatPower is int value and > 0 ? PlayerRow.Compact(value) + " CP" : "";
        OverlayOwnCp.Text = cp;
        Show(OverlayOwnCp, cp.Length > 0);
    }

    /// <summary>
    /// The own character's Combat Power: the legion's member list the game sends at the login names it (the character played's own line), else the last
    /// reading the CP checks kept, else the character page's. The game shows no Combat Power of the own character the way it shows the others' (their
    /// appearance frames), so the meter's line of the own character had none (5 November 2026, the owner: "the user's CP is hidden, which should not be").
    /// </summary>
    internal static int? OwnCombatPower(Aion2EntityDirectory directory)
    {
        if (directory.LocalCharacter is not { } me || me.Name.Length == 0)
        {
            return null;
        }

        if (Aion2Moved.Find(directory.LegionRoster(), m => m.Name, m => m.ServerId, me.Name, me.ServerId) is { CombatPower: > 0 } listed)
        {
            return listed.CombatPower;
        }

        if (CpChecks.State(me.Name, me.ServerId)?.Points is { Count: > 0 } points && points[^1].Cp > 0)
        {
            return points[^1].Cp;
        }

        return CpChecks.OfficialOf(me.Name, me.ServerId)?.Cp is int page and > 0 ? page : null;
    }

    // ------------------------------------------------------------------ the counters at the foot

    // the game's own icons (5 November 2026, the owner: "use the in-game icons"): the items' pictures (Odyle Energy, the Shugo Festival's key, the
    // Nightmare, daily dungeon and Ascension Trial challenge tickets) and the Dimensional Invasion's event icon (the Timers page has it too)
    private const string OdyleIcon = "aion2/items/more/Icon_Item_Odenergy_A_001.png";
    private const string ShugoIcon = "aion2/items/more/Icon_Currency_Treasure_Key_Legend_004.png";
    private const string InvasionIcon = "aion2/schedule/icons/invasion.png";
    private const string NightmareIcon = "aion2/items/more/Icon_Ticket_Entrance_BossChallenge_001.png";
    private const string DungeonIcon = "aion2/items/more/Icon_Ticket_Entrance_DailyDungeon_001.png";
    private const string AscensionIcon = "aion2/items/more/Icon_Ticket_Entrance_Awaken_001.png";

    /// <summary>The counters at the foot: Shugo Festival, Dimensional Invasion, Nightmare, the daily dungeon's weekly entries and Ascension Trial (the
    /// characters page's roster has the same), after the Odyle Energy. (Ludra's weekly kill stood here for a night, 6 November 2026; the owner: "just keep the
    /// basic stuff at the bottom of the meter like before".)</summary>
    private static readonly (int Id, string Icon)[] FootTickets =
    {
        (1, ShugoIcon), (8, InvasionIcon), (4, NightmareIcon), (101, DungeonIcon), (80000001, AscensionIcon),
    };

    private static readonly Dictionary<string, ImageSource?> TicketIcons = new();

    /// <summary>A counter's icon, read once and kept (null when the file is missing).</summary>
    private static ImageSource? TicketIcon(string relative)
    {
        if (TicketIcons.TryGetValue(relative, out ImageSource? known))
        {
            return known;
        }

        ImageSource? icon = null;
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", relative);
        if (System.IO.File.Exists(path))
        {
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.DecodePixelWidth = 32;
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                icon = bitmap;
            }
            catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                icon = null; // a broken file: the counter goes without its picture
            }
        }

        TicketIcons[relative] = icon;
        return icon;
    }

    /// <summary>Every five seconds (and at once after a change of the counters): the character played's counters at the meter's foot, as the game sent them
    /// (worked out from the refills since where the game sent nothing new, "~"). Nothing while no character is known or the counters are switched off.</summary>
    private void UpdateOverlayTickets(bool now = false)
    {
        if (Headless || (!now && DateTime.UtcNow - _overlayTicketsAt < TimeSpan.FromSeconds(5)))
        {
            return;
        }

        _overlayTicketsAt = DateTime.UtcNow;
        // the character the game logged in last; when the meter came after the login, the one it plays now (the counters are kept from earlier)
        string? who = Aion2Tickets.CurrentName ?? (_liveSource?.Entities as Aion2EntityDirectory)?.LocalCharacter?.Name;
        ShowOverlayTickets(_gateSettings.MeterShowTickets ? TicketsOf(who, DateTime.Now) : Array.Empty<OverlayTicket>());
    }

    /// <summary>The counters of one character for the foot (empty when it is not known).</summary>
    internal static IReadOnlyList<OverlayTicket> TicketsOf(string? characterName, DateTime now)
    {
        if (characterName is null || Aion2Tickets.Snapshot().FirstOrDefault(c => c.Name == characterName) is not { } character)
        {
            return Array.Empty<OverlayTicket>();
        }

        LocalizationManager loc = LocalizationManager.Instance;
        string language = loc.Language;
        DateTime utcNow = now.ToUniversalTime();
        bool premium = Aion2Tickets.HasPremium(character, utcNow) != false;
        DateTime seenAt = character.SavedAt.ToLocalTime();
        var list = new List<OverlayTicket>();
        if (Aion2Tickets.OdyleNow(character, utcNow, online: true) is { } odyle)
        {
            string? next = odyle.NextTick is DateTime tick && tick > utcNow ? Clock(tick - utcNow) : null;
            string tip = $"{Aion2Tickets.Find(Aion2Tickets.OdyleId)?.NameIn(language) ?? "Odyle Energy"}: {odyle.Count} / {odyle.Cap}"
                + (odyle.Bonus > 0 ? $"  ·  +{odyle.Bonus.ToString("N0", CultureInfo.CurrentCulture)} charged" : "")
                + (next is not null ? $"  ·  +{odyle.PerTick} in {next}" : odyle.Full ? "  ·  full" : "");
            list.Add(new OverlayTicket(OdyleIcon, odyle.Count.ToString("N0", CultureInfo.CurrentCulture),
                odyle.Bonus > 0 ? "+" + odyle.Bonus.ToString("N0", CultureInfo.CurrentCulture) : null, next, tip, odyle.Full));
        }

        foreach ((int id, string icon) in FootTickets)
        {
            if (Aion2Tickets.Find(id) is not { } def || Aion2Tickets.IsLocked(character, id) || !character.Tickets.TryGetValue(id, out TicketValue value))
            {
                continue;
            }

            int count = Aion2Tickets.Expected(def, value.Count, seenAt, now, premium);
            int cap = premium && def.SubscribeMax > def.Max ? def.SubscribeMax : def.Max;
            int lost = Aion2Tickets.LostAtNextRefill(def, count, premium);
            string tip = $"{def.ShortIn(language)}: {count} / {Math.Max(cap, count)}" + (value.Bonus > 0 ? $"  (+{value.Bonus})" : "")
                + (lost > 0 ? "  ·  " + string.Format(CultureInfo.CurrentCulture, loc[lost == 1 ? "Checklist.WarnLostOne" : "Checklist.WarnLostMany"], lost) : "");
            list.Add(new OverlayTicket(icon, (count != value.Count ? "~" : "") + count.ToString(CultureInfo.CurrentCulture),
                value.Bonus > 0 ? "+" + value.Bonus.ToString(CultureInfo.CurrentCulture) : null, null, tip, lost > 0));
        }

        return list;
    }

    /// <summary>"1:35" (hours and minutes) or "12m" under an hour.</summary>
    private static string Clock(TimeSpan left) => left.TotalHours >= 1
        ? $"{(int)left.TotalHours}:{left.Minutes:00}"
        : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";

    /// <summary>Puts the counters at the foot (built again only when one of them changed).</summary>
    internal void ShowOverlayTickets(IReadOnlyList<OverlayTicket> tickets)
    {
        string key = string.Join("|", tickets.Select(t => $"{t.Icon}:{t.Value}:{t.Bonus}:{t.Extra}:{t.Warn}:{t.Tip}"));
        if (key == _overlayTicketsKey)
        {
            return;
        }

        _overlayTicketsKey = key;
        OverlayTickets.Children.Clear();
        foreach (OverlayTicket ticket in tickets)
        {
            var glyph = new Image
            {
                Source = TicketIcon(ticket.Icon), Width = 17, Height = 17, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0),
            };
            RenderOptions.SetBitmapScalingMode(glyph, BitmapScalingMode.HighQuality);
            var text = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            text.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
            var value = new System.Windows.Documents.Run(ticket.Value) { FontWeight = FontWeights.SemiBold };
            if (ticket.Warn)
            {
                value.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.Warning");
            }
            else
            {
                value.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.OverlayText");
            }

            text.Inlines.Add(value);
            if (ticket.Bonus is { } bonus)
            {
                text.Inlines.Add(new System.Windows.Documents.Run(" " + bonus) { Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xB6, 0xFF)), FontSize = 10 });
            }

            if (ticket.Extra is { } extra)
            {
                var more = new System.Windows.Documents.Run(" " + extra) { FontSize = 10 };
                more.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.TextMuted");
                text.Inlines.Add(more);
            }

            // the counters wrap to a second line on a narrow meter (they were cut off at its edge)
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 11, 1), Background = Brushes.Transparent, ToolTip = ticket.Tip };
            item.Children.Add(glyph);
            item.Children.Add(text);
            OverlayTickets.Children.Add(item);
        }

        UpdateOverlayFold();
    }

    /// <summary>The counters the pictures show (render-overlay): a character with charged energy, a Shugo bonus key and a full Nightmare.</summary>
    internal static IReadOnlyList<OverlayTicket> SampleTickets() => new[]
    {
        new OverlayTicket(OdyleIcon, "840", "+2,000", "1:35", "Odyle Energy: 840 / 840"),
        new OverlayTicket(ShugoIcon, "20", "+3", null, "Shugo Festival: 20 / 21"),
        new OverlayTicket(InvasionIcon, "7", null, null, "Dimensional Invasion: 7 / 7"),
        new OverlayTicket(NightmareIcon, "14", null, null, "Nightmare: 14 / 14", Warn: true),
        new OverlayTicket(DungeonIcon, "6", "+6", null, "Daily Dungeon: 6 / 14"),
        new OverlayTicket(AscensionIcon, "3", null, null, "Ascension Trial: 3 / 3"),
    };

    // ------------------------------------------------------------------ the dummy card

    /// <summary>The dummy card from the parse: which run and how far it is, the own DPS in big (the damage over the time run so far), the clock against the
    /// planned length and every kind of hit (5 November 2026, the owner: "ours should show more details, like double etc.").</summary>
    private void FillDummyCard(DummyTest model, DateTime now)
    {
        DummyRun? run = model.Current;
        int number = run?.Number ?? model.Runs.Count + 1;
        TimeSpan planned = model.Planned;
        TimeSpan ran = model.State switch
        {
            DummyState.Running when run is not null => now - run.Start,
            DummyState.Finished when run is not null => run.Length,
            _ => TimeSpan.Zero,
        };
        ran = ran < TimeSpan.Zero ? TimeSpan.Zero : ran > planned && planned > TimeSpan.Zero ? planned : ran;
        string state = model.State switch
        {
            DummyState.Armed => "hit the dummy to start",
            DummyState.Running => "running",
            DummyState.Finished => "saved to History",
            _ => "",
        };
        IReadOnlyList<DamageEvent> events = run?.Events ?? (IReadOnlyList<DamageEvent>)Array.Empty<DamageEvent>();
        long total = events.Sum(e => e.Amount);
        double dps = ran.TotalSeconds >= 1 ? total / ran.TotalSeconds : 0;
        ShowDummyCard($"Run {number}  ·  {state}", $"{(int)ran.TotalMinutes}:{ran.Seconds:00} / {(int)planned.TotalMinutes}:{planned.Seconds:00}", dps, DummyStatsOf(events));
    }

    /// <summary>The card's figures (also drawn by render-overlay … dummy).</summary>
    internal void ShowDummyCard(string run, string clock, double dps, IReadOnlyList<(string Label, string Value)> stats)
    {
        OverlayDummyRunText.Text = run;
        OverlayDummyClock.Text = clock;
        OverlayDummyDps.Text = PlayerRow.Compact((long)Math.Round(dps));
        if (OverlayDummyStats.Children.Count != stats.Count)
        {
            OverlayDummyStats.Children.Clear();
            for (int i = 0; i < stats.Count; i++)
            {
                var line = new DockPanel { Margin = new Thickness(i % 2 == 0 ? 0 : 14, 1, 0, 1), LastChildFill = false };
                var label = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
                var figure = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                figure.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
                figure.SetResourceReference(TextBlock.ForegroundProperty, "Brush.OverlayText");
                DockPanel.SetDock(label, Dock.Left);
                DockPanel.SetDock(figure, Dock.Right);
                line.Children.Add(label);
                line.Children.Add(figure);
                OverlayDummyStats.Children.Add(line);
            }
        }

        for (int i = 0; i < stats.Count; i++)
        {
            var line = (DockPanel)OverlayDummyStats.Children[i];
            ((TextBlock)line.Children[0]).Text = stats[i].Label;
            ((TextBlock)line.Children[1]).Text = stats[i].Value;
        }
    }

    /// <summary>The parse's figures beside its DPS: the damage, the hits, the share of crits, perfect, double, back and front hits, and the biggest hit.</summary>
    internal static IReadOnlyList<(string Label, string Value)> DummyStatsOf(IEnumerable<DamageEvent> events)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        var kinds = new HitStats();
        long total = 0, biggest = 0;
        foreach (DamageEvent e in events)
        {
            if (e.IsHeal)
            {
                continue;
            }

            kinds.Add(e);
            total += e.Amount;
            if (!e.IsTick)
            {
                biggest = Math.Max(biggest, e.Amount / Math.Max(1, e.HitCount));
            }
        }

        static string Percent(double? share) => share is double v ? v.ToString("0.#", CultureInfo.CurrentCulture) + "%" : "–";
        return new (string, string)[]
        {
            (loc["Main.Overlay.ColTotal"], PlayerRow.Compact(total)), (loc["Look.ColHits"], kinds.Direct > 0 ? kinds.Direct.ToString("N0", CultureInfo.CurrentCulture) : "–"),
            (loc["Look.ColCrit"], Percent(kinds.CritPercent)), (loc["Look.ColPerfect"], Percent(kinds.PerfectPercent)),
            (loc["Look.ColDouble"], Percent(kinds.DoublePercent)), (loc["Look.ColBack"], Percent(kinds.BackPercent)),
            (loc["Look.ColFront"], Percent(kinds.FrontPercent)), ("Max hit", biggest > 0 ? PlayerRow.Compact(biggest) : "–"),
        };
    }

    /// <summary>The meter's "Hide": the meter goes off (the eye button, its hotkey or the tray bring it back) and goes on recording.</summary>
    private void HideMeterOverlay()
    {
        if (_meterOverlay is null)
        {
            WindowState = WindowState.Minimized;
        }
        else if (_meterOverlay.Wanted)
        {
            ToggleOverlay();
        }
    }

    /// <summary>The bar's report button: the fight report of what the meter shows, in the meter window.</summary>
    private void OnOverlayReportClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowClient();
        _ = OpenFightReportAsync();
    }

    /// <summary>The bar's characters button: Odyle Energy, the dailies and weeklies of every character.</summary>
    private void OnOverlayCharactersClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowClient();
        OpenChecklist(true);
    }

    private void OnOverlayDummyHistoryClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenHistory("Training dummy");
    }
}
