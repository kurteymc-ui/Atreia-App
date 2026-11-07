using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AionDPS.Aion2;
using AionDPS.Aion2.Protocol;
using AionDPS.Combat;

namespace AionDPS.Ui;

/// <summary>
/// The meter's look (Overlays → Meter look): the lines are built from the settings (MeterLook, MeterRowView), the parts around them are shown or
/// hidden, the support badges and hit kind columns are filled; and the page that sets it all, with a preview of the meter beside its switches.
/// </summary>
public partial class MainWindow
{
    private MeterLook _meterLook = new();
    private string _meterLookKey = "";
    private bool _headerOnHover;
    private bool _headerHoverHooked;
    private bool _meterGrowUp;

    /// <summary>Applies the look in the settings to the meter over the game (at the start and after every change).</summary>
    internal void ApplyMeterLook(MeterSettings settings)
    {
        ApplyOverlayColors(settings);
        MeterLook look = MeterLook.From(settings);
        _meterLook = look;
        if (look.Key != _meterLookKey)
        {
            _meterLookKey = look.Key;
            CompactOverlayRows.ItemTemplate = MeterRowView.TemplateFor(look, clickable: true);
            CompactOverlayTitles.Content = look.Titles ? new MeterRowView { Look = look, IsHeader = true } : null;
            CompactOverlayPanel.Width = CompactOverlayWidth(look);
            _supportAt = DateTime.MinValue; // the support badges come at once when that column was switched on
        }

        FitMeterLines(); // the player's width stays: the lines fit themselves to it
        FitRowsArea();   // and the lines' room follows the look's line height

        if (_overlayView is { } view)
        {
            var direction = settings.MeterBestLast ? ListSortDirection.Ascending : ListSortDirection.Descending;
            if (view.SortDescriptions.Count == 0 || view.SortDescriptions[0].Direction != direction)
            {
                using (view.DeferRefresh())
                {
                    view.SortDescriptions.Clear();
                    view.SortDescriptions.Add(new SortDescription(nameof(PlayerRow.SortKey), direction));
                }
            }
        }

        _meterShowTotal = settings.MeterShowTotal;
        _meterShowStatus = settings.MeterShowStatus;
        OverlayTotalLine.Visibility = settings.MeterShowTotal ? Visibility.Visible : Visibility.Collapsed;
        OverlayStatusLine.Visibility = settings.MeterShowStatus || OverlayTickets.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _headerOnHover = settings.MeterHeaderButtons == "hover";
        if (!_headerHoverHooked)
        {
            _headerHoverHooked = true;
            CompactOverlayPanel.MouseEnter += (_, _) => ShowHeaderButtons();
            CompactOverlayPanel.MouseLeave += (_, _) => ShowHeaderButtons();
        }

        ShowHeaderButtons();
        _meterGrowUp = settings.MeterGrowUp;
        if (_meterOverlay is not null)
        {
            _meterOverlay.GrowUp = _meterGrowUp;
        }
    }

    /// <summary>
    /// The panel's colours (Meter look → Panel, or the meter's own menu), at the opacity chosen on the Overlays page. Both overlay looks (and the look
    /// page's preview) paint their dark backgrounds with these brushes (DynamicResource); the meter's own window shares this window's resources.
    /// </summary>
    private void ApplyOverlayColors(MeterSettings settings)
    {
        double opacity = Math.Clamp(double.IsFinite(settings.OverlayOpacity) ? settings.OverlayOpacity : 0.6, 0.2, 1.0);
        var (back, edge) = OverlayPanelBrushes(MeterLook.PanelOf(settings.OverlayLook), opacity);
        Resources["Brush.OverlayBg"] = back;
        Resources["Brush.OverlayEdge"] = edge;
    }

    /// <summary>
    /// The background and the edge of one of the panels (MeterLook.Panels): the theme's own overlay colour with the gold edge, a light dark glass with
    /// nearly no frame, a plain neutral graphite with a grey edge (the raid log design's: no tint of its own, so the class colours carry the table), or
    /// near black with a faint edge (the old-school table's).
    /// </summary>
    private (Brush Back, Brush Edge) OverlayPanelBrushes(string panel, double opacity)
    {
        // the theme's own overlay colour (a deep navy in the default theme), at the opacity chosen
        Color themed = TryFindResource("Color.OverlayBg") is Color c ? c : Colors.Black;
        Color back = panel switch
        {
            "Clear" => Color.FromArgb((byte)Math.Round(opacity * 0.55 * 255), 0x08, 0x08, 0x0A),
            "Graphite" => Color.FromArgb((byte)Math.Round(opacity * 255), 0x1F, 0x20, 0x24),
            "Black" => Color.FromArgb((byte)Math.Round(opacity * 255), 0x11, 0x11, 0x13),
            _ => Color.FromArgb((byte)Math.Round(opacity * 255), themed.R, themed.G, themed.B),
        };
        Color line = panel switch
        {
            "Clear" => Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
            "Graphite" => Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF),
            "Black" => Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF),
            _ => Color.FromArgb(0x33, 0xDC, 0xB8, 0x77),
        };
        var backBrush = new SolidColorBrush(back);
        backBrush.Freeze();
        var edgeBrush = new SolidColorBrush(line);
        edgeBrush.Freeze();
        return (backBrush, edgeBrush);
    }

    /// <summary>The buttons in the meter's head: always there, or only while the mouse is over the meter.</summary>
    private void ShowHeaderButtons()
    {
        bool show = !_headerOnHover || CompactOverlayPanel.IsMouseOver || _overlayMenuOpen;   // a menu's button stays while its menu is open
        foreach (UIElement part in OverlayHeader.Children)
        {
            if (DockPanel.GetDock(part) == Dock.Right && part != CompactUpdateBadge)
            {
                part.Opacity = show ? 1 : 0;
                part.IsHitTestVisible = show;
            }
        }
    }

    /// <summary>The heal and taken views count no hits: their lines show no hit kinds.</summary>
    private void ClearHitStats()
    {
        foreach (PlayerRow row in _rows)
        {
            row.Hits = default;
        }
    }

    private DateTime _supportAt = DateTime.MinValue;
    private bool _supportShown;
    private static readonly Dictionary<string, ImageSource?> ClassIcons = new();

    /// <summary>A class's icon, read once.</summary>
    internal static ImageSource? ClassIcon(string? className)
    {
        string key = className ?? "";
        if (!ClassIcons.TryGetValue(key, out ImageSource? icon))
        {
            ClassIcons[key] = icon = new ClassIconConverter().Convert(className, typeof(ImageSource), null, CultureInfo.InvariantCulture) as ImageSource;
        }

        return icon;
    }

    /// <summary>
    /// The support column: the party buffs on each shown player over the fight shown (the effects rDPS prices: the Cleric's and the Chanter's, the
    /// Gladiator's and the Templar's), each with the share of the fight it was on, at most four, the longest first. Worked out every 1.5 s.
    /// </summary>
    private void UpdateSupportBadges((DateTime From, DateTime To)? span)
    {
        if (!_meterLook.Has("support"))
        {
            if (_supportShown)
            {
                _supportShown = false;
                foreach (PlayerRow row in _rows)
                {
                    row.SupportBadges = Array.Empty<SupportBadge>();
                }
            }

            return;
        }

        if (!Headless && DateTime.UtcNow - _supportAt < TimeSpan.FromSeconds(1.5))
        {
            return;
        }

        _supportAt = DateTime.UtcNow;
        _supportShown = true;
        if (span is not (DateTime from, DateTime to) || to <= from || _source?.Entities is not Aion2EntityDirectory directory)
        {
            foreach (PlayerRow row in _rows)
            {
                row.SupportBadges = Array.Empty<SupportBadge>();
            }

            return;
        }

        try
        {
            var players = _rows.Where(r => !r.IsEnemy).ToList();
            var ids = players.Select(r => r.ObjectId).ToHashSet();
            var events = directory.Combat.BuffsAround(from, to);
            var (onPlayers, _) = RaidDps.BuffsIn(events, ids, null, from, to);
            var skillOf = new Dictionary<int, int>();
            foreach (BuffEvent ev in events)
            {
                int display = RaidDps.DisplayBase(ev.SkillId);
                if (RaidDps.IsSynergy(display))
                {
                    skillOf.TryAdd(display, ev.SkillId);
                }
            }

            double windowMs = (to - from).TotalMilliseconds;
            LocalizationManager loc = LocalizationManager.Instance;
            foreach (PlayerRow row in _rows)
            {
                if (row.IsEnemy || !onPlayers.TryGetValue(row.ObjectId, out List<RaidBuff>? buffs) || buffs.Count == 0)
                {
                    row.SupportBadges = Array.Empty<SupportBadge>();
                    continue;
                }

                var badges = new List<SupportBadge>();
                foreach (var effect in buffs.Where(b => !b.BossScope && RaidDps.IsSynergy(b.DisplayBase)).GroupBy(b => b.DisplayBase))
                {
                    double covered = Covered(effect.SelectMany(b => b.Spans));
                    int percent = (int)Math.Round(Math.Clamp(100.0 * covered / windowMs, 0, 100));
                    if (percent <= 0)
                    {
                        continue;
                    }

                    int caster = effect.OrderByDescending(b => b.UptimePercent).First().Caster;
                    string casterClass = ResolveClassName(caster);
                    int skill = skillOf.GetValueOrDefault(effect.Key);
                    string name = skill != 0 ? Aion2SkillNames.Display(Aion2SkillNames.NameOf(skill)) : "";
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = loc["Look.SupportBuff"];
                    }

                    string? iconPath = skill != 0 ? Aion2SkillIcons.PathFor(skill) ?? Aion2SkillIcons.PathForName(name, null) ?? Aion2Artwork.EffectIconPath(skill) : null;
                    ImageSource? icon = iconPath is not null ? OverlayPictures.Load(iconPath) : ClassIcon(casterClass);
                    string tip = string.Format(CultureInfo.CurrentCulture, loc["Look.SupportTip"], name, percent, ResolveDisplayName(caster));
                    badges.Add(new SupportBadge(name, icon, percent, ClassBrushes.Edge(casterClass), tip));
                }

                row.SupportBadges = badges.OrderByDescending(b => b.Percent).Take(4).ToList();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // the buff log changed while it was read: the next update tries again
        }
    }

    /// <summary>The time the spans cover together (overlaps counted once).</summary>
    private static double Covered(IEnumerable<(long Start, long End)> spans)
    {
        double total = 0;
        long end = long.MinValue;
        foreach (var (start, stop) in spans.OrderBy(s => s.Start))
        {
            long from = Math.Max(start, end);
            if (stop > from)
            {
                total += stop - from;
                end = stop;
            }
        }

        return total;
    }

    // ---- the page ----

    /// <summary>Sample lines for the preview and the pictures: six made-up players with hit kinds and support buffs.</summary>
    internal static List<PlayerRow> SampleMeterRows()
    {
        (string Name, string Cls, long Dmg, double Dps, int Crit, int Back, int Front, int Perfect, int Double)[] sample =
        {
            ("Aerin", "Gladiator", 1_284_000, 10_700, 41, 22, 61, 12, 6), ("Kaelo", "Ranger", 1_102_500, 9_200, 47, 35, 52, 9, 4),
            ("Mirae", "Sorcerer", 968_300, 8_100, 38, 18, 70, 14, 7), ("Thorne", "Templar", 611_900, 5_100, 29, 8, 84, 6, 3),
            ("Lunari", "Chanter", 487_200, 4_100, 33, 15, 72, 10, 5), ("Vesh", "Cleric", 302_600, 2_500, 26, 12, 75, 7, 2),
        };
        long total = sample.Sum(x => x.Dmg);
        var rows = new List<PlayerRow>();
        int rank = 0;
        foreach (var x in sample)
        {
            rank++;
            var row = new PlayerRow(rank)
            {
                Name = x.Name, ClassName = x.Cls, Damage = x.Dmg, Dps = x.Dps, SharePercent = 100.0 * x.Dmg / total, FillPercent = 100.0 * x.Dmg / sample[0].Dmg, Rank = rank,
                CombatPowerText = PlayerRow.Compact(162_000 + 8_700 * (7 - rank)),
                // what the buffs lent the others (rDPS − nDPS): much from the Chanter and the Cleric, a little from the Gladiator's counter and the Templar's
                // Fury, nothing from the Ranger and the Sorcerer (all their effects are on themselves)
                Rdps = x.Dps * (x.Cls is "Chanter" or "Cleric" ? 1.55 : x.Cls is "Gladiator" or "Templar" ? 0.92 : 0.86),
                Ndps = x.Dps * (x.Cls is "Chanter" or "Cleric" ? 0.97 : 0.86),
                Given = x.Dps * (x.Cls is "Chanter" or "Cleric" ? 0.58 : x.Cls is "Gladiator" or "Templar" ? 0.06 : 0),
                // the healing on the others over the sample's two minutes: the Cleric's most, the Chanter's some, a little from the Templar
                HealOthers = x.Cls switch { "Cleric" => 384_000, "Chanter" => 168_000, "Templar" => 24_000, _ => 0 },
                Hits = new HitStats { Direct = 400 + rank * 37, Crit = (400 + rank * 37) * x.Crit / 100, Kinded = 400, Back = 4 * x.Back, Front = 4 * x.Front, Perfect = 4 * x.Perfect, Double = 4 * x.Double, Parry = rank == 4 ? 12 : 4 },
            };
            var badges = new List<SupportBadge>
            {
                new("Chanter", ClassIcon("Chanter"), 92 - rank * 3, ClassBrushes.Edge("Chanter"), "Chanter"),
                new("Cleric", ClassIcon("Cleric"), 78 - rank * 4, ClassBrushes.Edge("Cleric"), "Cleric"),
            };
            if (rank <= 2)
            {
                badges.Add(new("Templar", ClassIcon("Templar"), 54, ClassBrushes.Edge("Templar"), "Templar"));
            }

            row.SupportBadges = badges;
            row.HealOthersRate = row.HealOthers > 0 ? row.HealOthers / 120.0 : null;
            rows.Add(row);
        }

        MeterBarParts.Apply(rows, rdpsView: false, on: true, seconds: 120);
        return rows;
    }

    /// <summary>
    /// Overlays → Meter look: the premade designs across the top, the switches on the left, the meter as it will look on the right; every change is kept
    /// and shown on the meter at once.
    /// </summary>
    private UIElement BuildMeterLookPage()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        var root = new Grid { Margin = new Thickness(24, 20, 24, 24) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 420 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // the title and the designs stay at the top over both halves: a design is the first thing to pick, the switches come after
        var top = new StackPanel();
        top.Children.Add(PageTitle(loc["Look.Title"], loc["Look.Lede"]));
        Grid.SetColumnSpan(top, 2);
        root.Children.Add(top);
        var left = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = left, Padding = new Thickness(0, 0, 16, 0) };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // the preview: the meter's frame with sample lines, rebuilt with every change
        var preview = new StackPanel();
        var previewHost = new Border
        {
            Child = preview, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true,
        };
        previewHost.SetResourceReference(Border.BackgroundProperty, "Brush.OverlayBg");
        previewHost.SetResourceReference(Border.BorderBrushProperty, "Brush.OverlayEdge");
        var backdrop = new Border
        {
            Child = previewHost, Padding = new Thickness(18), CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0),
            Background = new LinearGradientBrush(Color.FromRgb(0x2A, 0x36, 0x30), Color.FromRgb(0x1B, 0x22, 0x28), 90),
        };
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        right.Children.Add(ShellText(loc["Look.Preview"].ToUpperInvariant(), "Brush.TextMuted", 11, bold: true));
        right.Children.Add(new Border { Height = 8 });
        right.Children.Add(backdrop);
        TextBlock previewNote = Wrapped(loc["Look.PreviewNote"], "Brush.TextSubtle", 11.5);
        previewNote.Margin = new Thickness(8, 10, 0, 0);
        previewNote.MaxWidth = 420;
        previewNote.HorizontalAlignment = HorizontalAlignment.Left;
        right.Children.Add(previewNote);
        Grid.SetColumn(right, 1);
        Grid.SetRow(right, 1);
        root.Children.Add(right);
        List<PlayerRow> samples = SampleMeterRows();

        void DrawPreview(MeterSettings s)
        {
            MeterLook look = MeterLook.From(s);
            MeterBarParts.Apply(samples, rdpsView: false, look.BarParts, seconds: 120);
            preview.Children.Clear();
            previewHost.Width = look.PanelWidth;
            // the head: the clock, the boss, and the buttons (dimmed when they show only under the mouse)
            var head = new DockPanel { Margin = new Thickness(10, 6, 8, 2), Height = 24 };
            TextBlock buttons = ShellText("          ", "Brush.TextSubtle", 11);
            buttons.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            buttons.Opacity = s.MeterHeaderButtons == "hover" ? 0.25 : 1;
            DockPanel.SetDock(buttons, Dock.Right);
            head.Children.Add(buttons);
            TextBlock clock = ShellText("1:51", "Brush.OverlayText", 12.5, bold: true);
            clock.Margin = new Thickness(0, 0, 8, 0);
            head.Children.Add(clock);
            head.Children.Add(ShellText(loc["Look.SampleBoss"], "Brush.TextMuted", 12));
            preview.Children.Add(head);
            if (s.MeterShowTotal)
            {
                var total = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 1, 10, 5) };
                TextBlock big = ShellText(PlayerRow.Compact((long)samples.Sum(r => r.Dps ?? 0)), "Brush.Accent", 24);
                big.FontWeight = FontWeights.SemiBold;
                total.Children.Add(big);
                TextBlock unit = ShellText("DPS", "Brush.TextMuted", 12.5, bold: true);
                unit.Margin = new Thickness(6, 0, 0, 4);
                unit.VerticalAlignment = VerticalAlignment.Bottom;
                total.Children.Add(unit);
                preview.Children.Add(total);
            }

            var lines = new StackPanel();
            Grid.SetIsSharedSizeScope(lines, true);
            if (look.Titles)
            {
                lines.Children.Add(new MeterRowView { Look = look, IsHeader = true });
            }

            var list = new ItemsControl
            {
                ItemTemplate = MeterRowView.TemplateFor(look, clickable: false), Focusable = false,
                ItemsSource = s.MeterBestLast ? samples.AsEnumerable().Reverse().ToList() : samples,
                Margin = new Thickness(0, 0, 0, 2),
            };
            lines.Children.Add(list);
            if (look.BarParts)
            {
                UIElement legend = MeterBarParts.Legend(look, rdpsView: false);
                ((FrameworkElement)legend).Margin = new Thickness(10, 1, 10, 4);
                lines.Children.Add(legend);
            }

            preview.Children.Add(lines);
            if (s.MeterShowStatus)
            {
                var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 2, 4, 6) };
                status.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(1), Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xC8, 0x78)), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                status.Children.Add(ShellText(loc["Look.SampleStatus"], "Brush.TextMuted", 10.5));
                preview.Children.Add(status);
            }
        }

        // the design cards (filled further down): the one the settings are now is framed and ticked; a switch changed after a pick leaves none marked
        var presetTiles = new List<(MeterLookPreset Preset, Border Tile, UIElement Tick)>();
        void MarkPreset(MeterSettings s)
        {
            foreach (var (preset, tile, tick) in presetTiles)
            {
                bool on = preset.Matches(s);
                tile.SetResourceReference(Border.BorderBrushProperty, on ? "Brush.Accent" : "Brush.Border");
                tile.BorderThickness = new Thickness(on ? 2 : 1);
                tile.Padding = on ? new Thickness(5) : new Thickness(6);
                tick.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        var save = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        Action<MeterSettings>? pending = null;
        void Change(Action<MeterSettings> change, bool soon = false)
        {
            MeterSettings s = MeterSettings.Load();
            change(s);
            DrawPreview(s);
            MarkPreset(s);
            if (soon)
            {
                // a slider: the file is written once it rests
                pending = (Action<MeterSettings>?)Delegate.Combine(pending, change);
                save.Stop();
                save.Start();
                return;
            }

            s.Save();
            ApplyMeterLook(s);
        }

        save.Tick += (_, _) =>
        {
            save.Stop();
            if (pending is { } change)
            {
                pending = null;
                MeterSettings s = MeterSettings.Load();
                change(s);
                s.Save();
                ApplyMeterLook(s);
            }
        };

        MeterSettings now = MeterSettings.Load();
        List<string> shown = (now.MeterColumns ?? MeterLook.DefaultColumns.ToList()).ToList();
        void Toggle(string id, bool on) => Change(s =>
        {
            var columns = (s.MeterColumns ?? MeterLook.DefaultColumns.ToList()).Where(c => c != id).ToList();
            if (on)
            {
                columns.Add(id);
            }

            s.MeterColumns = columns;
        });

        UIElement Section(string title, params UIElement[] parts)
        {
            var stack = new StackPanel();
            foreach (UIElement part in parts)
            {
                stack.Children.Add(part);
            }

            return HomeCard(title, stack, null, 0);
        }

        // a row of choices; the chosen one framed in the accent colour
        UIElement Choices(IReadOnlyList<string> ids, string current, Func<string, UIElement> content, Action<string> pick, double minWidth = 0)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
            var tiles = new List<(string Id, Border Tile)>();
            void Mark(string chosen)
            {
                foreach (var (id, tile) in tiles)
                {
                    bool on = id == chosen;
                    tile.SetResourceReference(Border.BorderBrushProperty, on ? "Brush.Accent" : "Brush.Border");
                    tile.BorderThickness = new Thickness(on ? 2 : 1);
                    tile.Padding = on ? new Thickness(9, 6, 9, 6) : new Thickness(10, 7, 10, 7);
                }
            }

            foreach (string id in ids)
            {
                var tile = new Border { Child = content(id), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 8), Cursor = System.Windows.Input.Cursors.Hand, MinWidth = minWidth };
                tile.SetResourceReference(Border.BackgroundProperty, "Brush.Control");
                string chosen = id;
                tile.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    Mark(chosen);
                    pick(chosen);
                };
                tiles.Add((id, tile));
                wrap.Children.Add(tile);
            }

            Mark(current);
            return wrap;
        }

        CheckBox Check(string label, string? tip, bool on, Action<bool> set)
        {
            var box = new CheckBox { IsChecked = on, Content = ShellText(label, "Brush.Text", 13), ToolTip = tip, Margin = new Thickness(0, 0, 18, 8) };
            box.Click += (_, _) => set(box.IsChecked == true);
            return box;
        }

        UIElement Note(string text)
        {
            TextBlock note = Wrapped(text, "Brush.TextSubtle", 11.5);
            note.Margin = new Thickness(0, 0, 0, 8);
            return note;
        }

        // designs: each card shows three of the sample players in the design's own lines and panel, over a bit of the game as the preview has it
        double opacity = Math.Clamp(double.IsFinite(now.OverlayOpacity) ? now.OverlayOpacity : 0.92, 0.2, 1.0);
        UIElement PresetPicture(MeterLookPreset preset)
        {
            MeterLook look = preset.Thumbnail();
            var lines = new StackPanel { Width = 250 };
            Grid.SetIsSharedSizeScope(lines, true); // the rate column lines up within the card only
            if (look.Titles)
            {
                lines.Children.Add(new MeterRowView { Look = look, IsHeader = true, IsHitTestVisible = false });
            }

            foreach (PlayerRow row in samples.Take(3))
            {
                lines.Children.Add(new MeterRowView { Look = look, DataContext = row, IsHitTestVisible = false });
            }

            var (back, edge) = OverlayPanelBrushes(preset.Panel, opacity);
            var panel = new Border
            {
                Child = lines, Background = back, BorderBrush = edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(0, 7, 0, 5),
                ClipToBounds = true, LayoutTransform = new ScaleTransform(0.56, 0.56), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            };
            // a fixed height: a design with taller lines shows them taller, and the last one runs out at the bottom
            return new Border
            {
                Child = panel, Height = 76, CornerRadius = new CornerRadius(5), ClipToBounds = true, Padding = new Thickness(0, 7, 0, 0),
                Background = new LinearGradientBrush(Color.FromRgb(0x2A, 0x36, 0x30), Color.FromRgb(0x1B, 0x22, 0x28), 90),
            };
        }

        Border PresetTile(MeterLookPreset preset)
        {
            var stack = new StackPanel { Width = 150 };
            stack.Children.Add(PresetPicture(preset));
            var head = new DockPanel { Margin = new Thickness(2, 7, 2, 0), LastChildFill = true };
            TextBlock tick = ShellText("\uE73E", "Brush.Accent", 11, bold: true); // a check mark: the design the meter wears now
            tick.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            tick.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(tick, Dock.Right);
            head.Children.Add(tick);
            TextBlock name = ShellText(loc[preset.NameKey], "Brush.Text", 12.5, bold: true);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            head.Children.Add(name);
            stack.Children.Add(head);
            TextBlock note = ShellText(loc[preset.NoteKey], "Brush.TextMuted", 11);
            note.Margin = new Thickness(2, 2, 2, 1);
            note.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(note);
            var tile = new Border
            {
                Child = stack, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 10, 8), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = loc[preset.NoteKey], BorderThickness = new Thickness(1), Padding = new Thickness(6),
            };
            tile.SetResourceReference(Border.BackgroundProperty, "Brush.Control");
            tile.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            tile.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                save.Stop();
                pending = null; // a slider still moving is overtaken by the design
                Change(preset.ApplyTo);
                ShowShellPage("meterlook"); // the switches below show the design's settings
            };
            presetTiles.Add((preset, tile, tick));
            return tile;
        }

        var presetRow = new WrapPanel();
        foreach (MeterLookPreset preset in MeterLook.Presets)
        {
            presetRow.Children.Add(PresetTile(preset));
        }

        MarkPreset(now);
        TextBlock presetNote = Wrapped(loc["Look.PresetsNote"], "Brush.TextSubtle", 11.5);
        presetNote.Margin = new Thickness(0, 0, 0, 10);
        var presetStack = new StackPanel();
        presetStack.Children.Add(presetNote);
        presetStack.Children.Add(presetRow);
        Border designs = HomeCard(loc["Look.Presets"], presetStack, null, 0);
        designs.HorizontalAlignment = HorizontalAlignment.Left;
        designs.Margin = new Thickness(0, 0, 0, 14);
        designs.Padding = new Thickness(16, 12, 6, 6); // the cards keep their own gap on the right and below
        top.Children.Add(designs);

        // bars: each tile draws a line in its style
        PlayerRow barSample = samples[0];
        UIElement BarTile(string bar)
        {
            var stack = new StackPanel { Width = 150 };
            var tileLook = new MeterLook { Bar = bar, Shown = new HashSet<string> { "icon", "dps" } };
            var sampleLine = new MeterRowView { Look = tileLook, DataContext = barSample, IsHitTestVisible = false };
            var sampleBack = new Border { Child = sampleLine, CornerRadius = new CornerRadius(4), Padding = new Thickness(0, 3, 0, 3) };
            sampleBack.SetResourceReference(Border.BackgroundProperty, "Brush.OverlayBg");
            stack.Children.Add(sampleBack);
            TextBlock name = ShellText(loc["Look.Bar." + bar], "Brush.Text", 12.5, bold: true);
            name.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(name);
            return stack;
        }

        left.Children.Add(Section(loc["Look.Bars"],
            Choices(MeterLook.Bars, MeterLook.From(now).Bar, BarTile, bar => Change(s => s.MeterBarStyle = bar)),
            Check("Buffs and healing on the bar", "After a player's own damage the bar goes on in grey for what their buffs added to the others' damage and in green for their healing on the others, with the + figures beside the rate. In the rDPS view the bar is rDPS + healing.",
                now.MeterBarParts, on => Change(s => s.MeterBarParts = on))));

        // lines
        left.Children.Add(Section(loc["Look.Lines"],
            Choices(MeterLook.Sizes, MeterLook.From(now).Size, size => ShellText(loc["Look.Size." + size], "Brush.Text", 13), size => Change(s => s.MeterRowSize = size), 90),
            Check(loc["Look.BestLast"], null, now.MeterBestLast, on => Change(s => s.MeterBestLast = on)),
            Check(loc["Look.GrowUp"], loc["Look.GrowUpTip"], now.MeterGrowUp, on => Change(s => s.MeterGrowUp = on))));

        // text
        var scaleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        scaleRow.Children.Add(ShellText(loc["Look.TextSize"], "Brush.Text", 13));
        var scale = new Slider { Minimum = 85, Maximum = 130, Value = Math.Round(Math.Clamp(now.MeterTextScale, 0.85, 1.3) * 100), Width = 200, Margin = new Thickness(12, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 5 };
        TextBlock scaleText = ShellText($"{scale.Value:0} %", "Brush.TextMuted", 12.5);
        scale.ValueChanged += (_, _) =>
        {
            scaleText.Text = $"{scale.Value:0} %";
            double value = scale.Value / 100.0;
            Change(s => s.MeterTextScale = value, soon: true);
        };
        scaleRow.Children.Add(scale);
        scaleRow.Children.Add(scaleText);
        left.Children.Add(Section(loc["Look.Text"],
            Choices(MeterLook.Fonts.Select(f => f.Id).ToList(), MeterLook.From(now).Font, id =>
            {
                var font = MeterLook.Fonts.First(f => f.Id == id);
                TextBlock sample = ShellText(font.Name, "Brush.Text", 14);
                sample.FontFamily = new FontFamily(font.Families);
                return sample;
            }, font => Change(s => s.MeterFont = font)),
            scaleRow,
            Check(loc["Look.ClassColor"], null, shown.Contains("classcolor"), on => Toggle("classcolor", on)),
            Check(loc["Look.SmallUnits"], loc["Look.SmallUnitsTip"], now.MeterSmallUnits, on => Change(s => s.MeterSmallUnits = on))));

        // columns
        var nameSide = new WrapPanel();
        nameSide.Children.Add(Check(loc["Look.ColRank"], null, shown.Contains("rank"), on => Toggle("rank", on)));
        nameSide.Children.Add(Check(loc["Look.ColIcon"], null, shown.Contains("icon"), on => Toggle("icon", on)));
        nameSide.Children.Add(Check(loc["Look.ColCp"], null, shown.Contains("cp"), on => Toggle("cp", on)));
        var own = new WrapPanel();
        foreach (string id in new[] { "dps", "share", "total" })
        {
            MeterColumn column = MeterLook.Columns.First(c => c.Id == id);
            own.Children.Add(Check(loc["Look.Own." + id], loc[column.TipKey], shown.Contains(id), on => Toggle(column.Id, on)));
        }

        var rates = new WrapPanel();
        foreach (string id in new[] { "rdps", "ndps", "given" })
        {
            MeterColumn column = MeterLook.Columns.First(c => c.Id == id);
            rates.Children.Add(Check(loc[column.TitleKey], loc[column.TipKey], shown.Contains(id), on => Toggle(column.Id, on)));
        }

        var kinds = new WrapPanel();
        foreach (string id in new[] { "crit", "back", "front", "perfect", "double", "parry", "hits" })
        {
            MeterColumn column = MeterLook.Columns.First(c => c.Id == id);
            kinds.Children.Add(Check(loc[column.TitleKey], loc[column.TipKey], shown.Contains(id), on => Toggle(column.Id, on)));
        }

        TextBlock Sub(string text)
        {
            TextBlock sub = ShellText(text, "Brush.TextMuted", 12, bold: true);
            sub.Margin = new Thickness(0, 4, 0, 6);
            return sub;
        }

        left.Children.Add(Section(loc["Look.Columns"],
            Sub(loc["Look.NameSide"]), nameSide,
            Sub(loc["Look.Numbers"]), own,
            Sub(loc["Look.Rates"]), Note(loc["Look.RatesNote"]), rates,
            Sub(loc["Look.HitKinds"]), Note(loc["Look.HitKindsNote"]), kinds,
            Check(loc["Look.Titles"], null, now.MeterColumnTitles, on => Change(s => s.MeterColumnTitles = on))));

        // buff alerts (MainWindow.BuffAlerts.cs)
        var also = new TextBox { Text = now.BuffAlertNames ?? "", Width = 320, Margin = new Thickness(0, 2, 0, 8), ToolTip = "Effect names, comma separated" };
        also.LostFocus += (_, _) => Change(s => s.BuffAlertNames = also.Text.Trim());
        left.Children.Add(Section("Buff alerts",
            Note("A line on the meter when one of your damage buffs (Mantra, Gale, Fury, Light of Protection …), a scroll or food drops off you while you fight."),
            Check("Warn when a buff drops", null, now.BuffDropAlert, on => Change(s => s.BuffDropAlert = on)),
            Check("Play a sound with it", null, now.BuffDropSound, on => Change(s => s.BuffDropSound = on)),
            Sub("Also watch (names, comma separated)"), also));

        // group alerts (MainWindow.GroupAlerts.cs)
        left.Children.Add(Section("Group alerts",
            Note("While you are tabbed out: when someone joins your group in the group finder, the game's taskbar button flashes, without a sound. When you are taken into a group or the group goes in, it flashes and Windows shows a notification."),
            Check("Alert me while tabbed out", null, now.GroupAlert, on => Change(s => s.GroupAlert = on)),
            Check("Play a sound when the group goes in", null, now.GroupAlertSound, on => Change(s => s.GroupAlertSound = on))));

        // around the list: the panel behind it first (the same choice as in the meter's own menu)
        left.Children.Add(Section(loc["Look.Around"],
            Sub(loc["Look.Panel"]),
            Choices(MeterLook.Panels, MeterLook.PanelOf(now.OverlayLook), id => ShellText(loc["Look.Panel." + id], "Brush.Text", 13), id => Change(s => s.OverlayLook = id), 90),
            Check(loc["Look.ShowTotal"], null, now.MeterShowTotal, on => Change(s => s.MeterShowTotal = on)),
            Check(loc["Look.ShowStatus"], loc["Look.ShowStatusTip"], now.MeterShowStatus, on => Change(s => s.MeterShowStatus = on)),
            Sub(loc["Look.Buttons"]),
            Choices(new[] { "always", "hover" }, now.MeterHeaderButtons == "hover" ? "hover" : "always", id => ShellText(loc["Look.Buttons." + id], "Brush.Text", 13),
                id => Change(s => s.MeterHeaderButtons = id), 90),
            // the meter's own switches (they were in its "…" menu until 5 November 2026; the owner: "just redirect")
            Check("Fold to the bar until a boss fight or a training dummy", null, now.MeterFoldIdle, on => Change(s => s.MeterFoldIdle = on)),
            Check("Odyle Energy, Shugo and the other counters at the bottom", null, now.MeterShowTickets, on => Change(s => s.MeterShowTickets = on)),
            Check("Boss health over the lines: what is left, time to kill, enrage", null, now.MeterBossLine, on => Change(s => s.MeterBossLine = on)),
            Check("Boss timers: the next casts and the enrage", null, now.OverlayBossTimers, on => Change(s => s.OverlayBossTimers = on)),
            Check("Everyone in dungeons, only me at training dummies", null, now.AutoScope, on => Change(s => s.AutoScope = on))));

        var reset = new Button { Content = loc["Look.Reset"], Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        reset.Click += (_, _) =>
        {
            if (ThemedMessageBox.Show(this, loc["Look.ResetAsk"], loc["Look.Title"], MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            Change(s =>
            {
                MeterLook.Standard.ApplyTo(s); // the usual design, and the two choices no design touches
                s.MeterGrowUp = false;
                s.MeterBestLast = false;
            });
            ShowShellPage("meterlook"); // the switches show the usual look again
        };
        left.Children.Add(reset);

        DrawPreview(now);
        _shellTool = new ToolPage
        {
            Content = root,
            Cleanup = () =>
            {
                if (save.IsEnabled)
                {
                    save.Stop();
                    if (pending is { } change)
                    {
                        MeterSettings s = MeterSettings.Load();
                        change(s);
                        s.Save();
                        ApplyMeterLook(s);
                    }
                }
            },
        };
        return root;
    }
}
