using System.Windows;
using System.Windows.Threading;

namespace AionDPS.Ui;

/// <summary>
/// Every window of the meter on any screen, from 1280 x 720 to 4K and ultrawide (8 November 2026, the owner: "compatible with 720, 1080, 1440, 4K and
/// ultrawide"). Some windows open larger than a small screen has room for (the session manager 1760 x 820, the roster 1080 wide, Settings 725 high: past the
/// bottom of a 1280 x 720 screen, or of a 1920 x 1080 one at 150 % scaling, which leaves the same room). Two rules, for all windows at once:
/// <list type="bullet">
/// <item>A window (one with a task bar button, or a dialog of another window) is brought wholly onto the monitor it mostly lies on when it opens: smaller
/// when it is larger than that monitor's work area (its least size too), then moved in.</item>
/// <item>When the screens change while the meter runs (a resolution switched, a monitor unplugged), windows are fitted again, and an overlay that no monitor
/// shows any more comes back onto the nearest one. Overlays are otherwise left where the player put them: they may sit partly past an edge on purpose.</item>
/// </list>
/// The main window and the overlays keep their own placement rules (<see cref="WindowPlacement"/>, MeterOverlayWindow, PopOutWindow, the pet map windows).
/// </summary>
public static class ScreenFit
{
    private static bool _installed;
    private static DispatcherTimer? _settle;

    /// <summary>Once, before the first window opens (the GUI only: the picture modes never show a window).</summary>
    public static void Install(Dispatcher dispatcher)
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window && IsRegular(window))
            {
                FitOnScreen(window);
            }
        }));

        // Raised on a thread of its own; the work areas Windows reports settle a moment after the change, hence the short wait (several changes in a row, as
        // when a game switches its resolution, give one refit).
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => dispatcher.BeginInvoke(() =>
        {
            _settle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) =>
            {
                _settle!.Stop();
                RefitAll();
            }, dispatcher);
            _settle.Stop();
            _settle.Start();
        });
    }

    /// <summary>A window of the ordinary kind: a task bar button or a dialog of another window, and not one that places itself (the meter, the overlays).</summary>
    private static bool IsRegular(Window window) =>
        window is not MainWindow and not PopOutWindow and not IOverlayPlace
        && (window.ShowInTaskbar || window.Owner is not null)
        && !(window.Topmost && !window.ShowInTaskbar);

    /// <summary>After the screens changed: the ordinary windows fitted again, every other window brought back when no monitor shows it.</summary>
    private static void RefitAll()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        foreach (Window window in app.Windows.OfType<Window>().ToList())
        {
            if (!window.IsLoaded || !window.IsVisible)
            {
                continue;
            }

            if (IsRegular(window))
            {
                FitOnScreen(window);
            }
            else
            {
                BringBackIfLost(window);
            }
        }
    }

    /// <summary>Brings a window wholly onto the work area of the monitor it mostly lies on: smaller when it is larger than that area (its least size lowered to
    /// fit as well, else WPF keeps it at that size), then moved in.</summary>
    public static void FitOnScreen(Window window)
    {
        if (window.WindowState != WindowState.Normal || !double.IsFinite(window.Left) || !double.IsFinite(window.Top))
        {
            return;
        }

        double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width, height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return;
        }

        Rect area = WindowPlacement.WorkAreaOf(new Rect(window.Left, window.Top, width, height));
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        if (height > area.Height + 0.5)
        {
            // a window sized by its content keeps that size whatever Height says: it takes the given one from now on (its content scrolls or is cut)
            if (window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight)
            {
                window.SizeToContent = window.SizeToContent == SizeToContent.WidthAndHeight ? SizeToContent.Width : SizeToContent.Manual;
            }

            if (window.MinHeight > area.Height)
            {
                window.MinHeight = area.Height;
            }

            window.Height = height = area.Height;
        }

        if (width > area.Width + 0.5)
        {
            if (window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight)
            {
                window.SizeToContent = window.SizeToContent == SizeToContent.WidthAndHeight ? SizeToContent.Height : SizeToContent.Manual;
            }

            if (window.MinWidth > area.Width)
            {
                window.MinWidth = area.Width;
            }

            window.Width = width = area.Width;
        }

        double left = Math.Max(area.Left, Math.Min(window.Left, area.Right - width));
        double top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - height));
        if (Math.Abs(left - window.Left) > 0.5)
        {
            window.Left = left;
        }

        if (Math.Abs(top - window.Top) > 0.5)
        {
            window.Top = top;
        }
    }

    /// <summary>An overlay or the meter that no monitor shows any more (its top strip on none): onto the nearest monitor, its size kept.</summary>
    private static void BringBackIfLost(Window window)
    {
        if (window.WindowState != WindowState.Normal || !double.IsFinite(window.Left) || !double.IsFinite(window.Top))
        {
            return;
        }

        double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width, height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return;
        }

        var place = new Rect(window.Left, window.Top, width, height);
        if (WindowPlacement.IsOnScreen(place))
        {
            return;
        }

        Rect area = Nearest(place);
        window.Left = Math.Max(area.Left, Math.Min(place.Left, area.Right - Math.Min(width, area.Width)));
        window.Top = Math.Max(area.Top, Math.Min(place.Top, area.Bottom - Math.Min(height, area.Height)));
    }

    /// <summary>The work area closest to a place that lies on none.</summary>
    private static Rect Nearest(Rect place)
    {
        Rect best = WindowPlacement.Primary();
        double bestDistance = double.MaxValue;
        foreach (Rect area in WindowPlacement.WorkAreas())
        {
            double dx = Math.Max(0, Math.Max(area.Left - place.Right, place.Left - area.Right));
            double dy = Math.Max(0, Math.Max(area.Top - place.Bottom, place.Top - area.Bottom));
            double distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = area;
            }
        }

        return best;
    }
}
