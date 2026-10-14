using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace SpotifyLyric;

/// <summary>Borderless always-on-top strip that sits on the taskbar and shows the current lyric line.</summary>
public sealed class LyricWindow : Window
{
    private readonly Settings _settings;
    private readonly int _index;

    private readonly TextBlock _line1;
    private readonly TextBlock _line2;
    private readonly TranslateTransform _line1Shift = new();

    private IntPtr _hwnd;
    private bool _closingByApp;

    /// <summary>Raised when the window is destroyed by something other than the app (e.g. Explorer restart).</summary>
    public event Action? Lost;

    /// <param name="index">Which copy this is; selects the saved position and the default monitor.</param>
    public LyricWindow(Settings settings, int index, Action showMenu)
    {
        _settings = settings;
        _index = index;

        Title = "SpotifyLyric";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Visibility = Visibility.Hidden;

        var shadow = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.9, Color = Colors.Black };
        _line1 = new TextBlock
        {
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Effect = shadow,
            RenderTransform = _line1Shift,
        };
        _line2 = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Effect = shadow,
        };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        stack.Children.Add(_line1);
        stack.Children.Add(_line2);

        // Almost-transparent fill so the whole strip can be grabbed with the mouse.
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Child = stack };
        ApplyFont();

        MouseLeftButtonDown += OnDragStart;
        MouseRightButtonUp += (_, _) => showMenu();
        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => PlaceWindow();

        // Owned by the taskbar, so if Explorer restarts our window gets destroyed with it.
        Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            if (!_closingByApp) Lost?.Invoke();
        };

        StartTimer(TimeSpan.FromMilliseconds(500), KeepOnTop);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>Closes the window on purpose (copy removed or app exiting).</summary>
    public void CloseByApp()
    {
        _closingByApp = true;
        Close();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(PlaceWindow);

    private void StartTimer(TimeSpan interval, Action tick)
    {
        var t = new DispatcherTimer { Interval = interval };
        t.Tick += (_, _) => tick();
        t.Start();
        Closed += (_, _) => t.Stop();
    }

    // ---------- Window placement ----------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyExStyle();
        Native.SetOwnerToTaskbar(_hwnd);
        PlaceWindow();

        // Re-assert z-order immediately whenever another window comes to the foreground.
        _foregroundHook = (_, _, _, _, _, _, _) => KeepOnTop();
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundHook, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        Closed += (_, _) => Native.UnhookWinEvent(_hook);
    }

    // Kept in a field so the delegate is not garbage-collected while the hook is alive.
    private Native.WinEventProc? _foregroundHook;
    private IntPtr _hook;

    public void ApplyExStyle()
    {
        if (_hwnd == IntPtr.Zero) return;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        ex = _settings.Locked ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);
    }

    public void PlaceWindow()
    {
        if (_hwnd == IntPtr.Zero) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var taskbar = Native.GetTaskbarRect();
        bool horizontal = taskbar is { } tb0 && tb0.Width > tb0.Height;

        int heightPx = horizontal ? taskbar!.Value.Height : (int)(48 * scale);
        int widthPx = (int)(_settings.Width * scale);
        Width = widthPx / scale;
        Height = heightPx / scale;

        // Copy N defaults to the N-th monitor (primary first); copies beyond the monitor count stack above the main one.
        var screens = WF.Screen.AllScreens.OrderByDescending(s => s.Primary).ThenBy(s => s.Bounds.Left).ToArray();
        int stack = _index < screens.Length ? 0 : _index - screens.Length + 1;

        var saved = _settings.PositionOf(_index);
        int x, y;
        if (saved.X is int sx && saved.Y is int sy && IsOnScreen(sx, sy, widthPx, heightPx))
        {
            (x, y) = (sx, sy);
        }
        else if (_index > 0 && _index < screens.Length)
        {
            // Secondary monitor: left end of its taskbar (or bottom of the screen if it has none).
            var b = screens[_index].Bounds;
            var wa = screens[_index].WorkingArea;
            x = wa.Left + (int)(12 * scale);
            y = Math.Max(b.Bottom, wa.Bottom) - heightPx;
        }
        else if (horizontal)
        {
            var tb = taskbar!.Value;
            var notify = Native.GetTrayNotifyRect();
            // Default: just left of the clock / tray icons.
            x = notify is { } n && n.Left > tb.Left + widthPx
                ? n.Left - widthPx - (int)(8 * scale)
                : tb.Left + (int)(12 * scale);
            y = tb.Top - stack * heightPx;
        }
        else
        {
            var wa = WF.Screen.PrimaryScreen!.WorkingArea;
            x = wa.Left + (wa.Width - widthPx) / 2;
            y = wa.Bottom - (stack + 1) * heightPx;
        }

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, widthPx, heightPx, Native.SWP_NOACTIVATE);
    }

    private static bool IsOnScreen(int x, int y, int w, int h)
    {
        var r = new SD.Rectangle(x, y, w, h);
        foreach (var s in WF.Screen.AllScreens)
            if (s.Bounds.IntersectsWith(r)) return true;
        return false;
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (_settings.Locked) return;
        DragMove();
        if (Native.GetWindowRect(_hwnd, out var r))
        {
            var pos = _settings.PositionOf(_index);
            pos.X = r.Left;
            pos.Y = r.Top;
            _settings.Save();
        }
    }

    /// <summary>The taskbar is topmost too; re-assert our z-order after it gets clicked.</summary>
    private void KeepOnTop()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    // ---------- Rendering ----------

    /// <summary>Displays the given lines; a null <paramref name="l1"/> hides the strip.</summary>
    public void SetLines(string? l1, string l2, bool playing)
    {
        bool hide = l1 == null || (_settings.HideOnFullscreen && Native.IsFullscreenAppActive(_hwnd));
        var vis = hide ? Visibility.Hidden : Visibility.Visible;
        if (Visibility != vis) Visibility = vis;
        // WPF can believe a window is visible while Win32 never showed it (seen for copies opened from the menu).
        else if (!hide && _hwnd != IntPtr.Zero && !Native.IsWindowVisible(_hwnd)) Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        if (l1 == null) return;

        if (_line1.Text != l1)
        {
            _line1.Text = l1;
            AnimateIn();
        }
        _line2.Text = l2;
        _line2.Visibility = _settings.ShowNextLine && l2.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Opacity = playing ? 1.0 : 0.55;
    }

    private void AnimateIn()
    {
        var duration = TimeSpan.FromMilliseconds(180);
        _line1.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, duration));
        _line1Shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(5, 0, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    public void ApplyFont()
    {
        var font = new FontFamily("Segoe UI");
        _line1.FontFamily = _line2.FontFamily = font;
        _line1.FontSize = _settings.FontSize;
        _line2.FontSize = Math.Round(_settings.FontSize * 0.8);
    }
}
