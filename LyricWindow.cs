using System;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly MediaWatcher _media = new();
    private readonly LyricsProvider _provider = new();

    private readonly TextBlock _line1;
    private readonly TextBlock _line2;
    private readonly TranslateTransform _line1Shift = new();
    private readonly WF.NotifyIcon _tray;
    private readonly WF.ContextMenuStrip _menu = new();

    private IntPtr _hwnd;
    private MediaSnapshot? _snapshot;
    private string? _trackKey;
    private Lyrics? _lyrics;
    private bool _loading;
    private DateTime? _failedAt;
    private CancellationTokenSource? _fetchCts;
    private bool _polling;

    public LyricWindow(Settings settings)
    {
        _settings = settings;

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
        MouseRightButtonUp += (_, _) => _menu.Show(WF.Cursor.Position);
        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => PlaceWindow();

        _menu.Opening += (_, _) => BuildMenu();
        _tray = new WF.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "Spotify Lyric",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        // Owned by the taskbar, so if Explorer restarts our window gets destroyed with it: relaunch.
        Closed += (_, _) =>
        {
            if (_exiting) return;
            _tray.Visible = false;
            System.Diagnostics.Process.Start(Environment.ProcessPath!, "--wait");
            Application.Current.Shutdown();
        };


        StartTimer(TimeSpan.FromMilliseconds(100), Render);
        StartTimer(TimeSpan.FromMilliseconds(500), Poll);
        StartTimer(TimeSpan.FromMilliseconds(500), KeepOnTop);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PlaceWindow);
    }

    private void StartTimer(TimeSpan interval, Action tick)
    {
        var t = new DispatcherTimer { Interval = interval };
        t.Tick += (_, _) => tick();
        t.Start();
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
        Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundHook, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    // Kept in a field so the delegate is not garbage-collected while the hook is alive.
    private Native.WinEventProc? _foregroundHook;

    private void ApplyExStyle()
    {
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        ex = _settings.Locked ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);
    }

    private void PlaceWindow()
    {
        if (_hwnd == IntPtr.Zero) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var taskbar = Native.GetTaskbarRect();
        bool horizontal = taskbar is { } tb0 && tb0.Width > tb0.Height;

        int heightPx = horizontal ? taskbar!.Value.Height : (int)(48 * scale);
        int widthPx = (int)(_settings.Width * scale);
        Width = widthPx / scale;
        Height = heightPx / scale;

        int x, y;
        if (_settings.X is int sx && _settings.Y is int sy && IsOnScreen(sx, sy, widthPx, heightPx))
        {
            (x, y) = (sx, sy);
        }
        else if (horizontal)
        {
            var tb = taskbar!.Value;
            var notify = Native.GetTrayNotifyRect();
            // Default: just left of the clock / tray icons.
            x = notify is { } n && n.Left > tb.Left + widthPx
                ? n.Left - widthPx - (int)(8 * scale)
                : tb.Left + (int)(12 * scale);
            y = tb.Top;
        }
        else
        {
            var wa = WF.Screen.PrimaryScreen!.WorkingArea;
            x = wa.Left + (wa.Width - widthPx) / 2;
            y = wa.Bottom - heightPx;
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
            _settings.X = r.Left;
            _settings.Y = r.Top;
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

    // ---------- Spotify + lyrics ----------

    private async void Poll()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var snap = await _media.PollAsync();
            _snapshot = snap;
            var key = snap?.Track.Key;
            bool retry = _failedAt is { } f && DateTime.Now - f > TimeSpan.FromSeconds(15);
            if (key != _trackKey || (retry && snap != null))
            {
                _trackKey = key;
                _lyrics = null;
                _failedAt = null;
                _fetchCts?.Cancel();
                if (snap != null) _ = LoadLyricsAsync(snap.Track);
            }
        }
        catch
        {
            _snapshot = null;
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task LoadLyricsAsync(TrackInfo track)
    {
        var cts = _fetchCts = new CancellationTokenSource();
        _loading = true;
        try
        {
            var lyr = await _provider.GetAsync(track, cts.Token);
            if (cts.IsCancellationRequested) return;
            _lyrics = lyr;
        }
        catch when (cts.IsCancellationRequested) { }
        catch
        {
            _failedAt = DateTime.Now;
        }
        finally
        {
            if (!cts.IsCancellationRequested) _loading = false;
        }
    }

    private void Render()
    {
        var snap = _snapshot;
        bool hide = snap == null || (_settings.HideOnFullscreen && Native.IsFullscreenAppActive(_hwnd));
        var vis = hide ? Visibility.Hidden : Visibility.Visible;
        if (Visibility != vis) Visibility = vis;
        if (snap == null) return;

        var t = snap.Track;
        string header = string.IsNullOrEmpty(t.Artist) ? $"♪ {t.Title}" : $"♪ {t.Title} — {t.Artist}";
        string l1, l2;

        if (_loading) (l1, l2) = (header, "Searching for lyrics…");
        else if (_failedAt != null) (l1, l2) = (header, "Network error, retrying…");
        else if (_lyrics == null) (l1, l2) = (header, "Lyrics not found");
        else if (_lyrics.Instrumental && !_lyrics.Synced) (l1, l2) = (header, "(Instrumental)");
        else if (!_lyrics.Synced) (l1, l2) = (header, "No synced lyrics for this song");
        else
        {
            var pos = snap.CurrentPosition + TimeSpan.FromSeconds(_settings.OffsetSec);
            int idx = _lyrics.IndexAt(pos);
            var lines = _lyrics.Lines;
            if (idx < 0)
            {
                l1 = header;
                l2 = NextText(lines, -1);
            }
            else
            {
                l1 = string.IsNullOrWhiteSpace(lines[idx].Text) ? "♪ ♪ ♪" : lines[idx].Text;
                l2 = NextText(lines, idx);
            }
        }

        if (_line1.Text != l1)
        {
            _line1.Text = l1;
            AnimateIn();
        }
        _line2.Text = l2;
        _line2.Visibility = _settings.ShowNextLine && l2.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Opacity = snap.IsPlaying ? 1.0 : 0.55;
    }

    private static string NextText(System.Collections.Generic.List<LyricLine> lines, int idx)
    {
        for (int i = idx + 1; i < lines.Count; i++)
            if (!string.IsNullOrWhiteSpace(lines[i].Text)) return lines[i].Text;
        return "";
    }

    private void AnimateIn()
    {
        var duration = TimeSpan.FromMilliseconds(180);
        _line1.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, duration));
        _line1Shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(5, 0, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    private void ApplyFont()
    {
        var font = new FontFamily("Segoe UI");
        _line1.FontFamily = _line2.FontFamily = font;
        _line1.FontSize = _settings.FontSize;
        _line2.FontSize = Math.Round(_settings.FontSize * 0.8);
    }

    // ---------- Menu / tray ----------

    private void BuildMenu()
    {
        _menu.Items.Clear();

        var title = _snapshot is { } s ? $"♪ {s.Track.Title} — {s.Track.Artist}" : "Spotify is not playing";
        if (title.Length > 60) title = title[..57] + "...";
        _menu.Items.Add(new WF.ToolStripMenuItem(title) { Enabled = false });
        _menu.Items.Add(new WF.ToolStripSeparator());

        AddCheck("Lock position (click-through)", _settings.Locked, () =>
        {
            _settings.Locked = !_settings.Locked;
            ApplyExStyle();
        });
        AddCheck("Show next line", _settings.ShowNextLine, () => _settings.ShowNextLine = !_settings.ShowNextLine);
        AddCheck("Hide when a fullscreen app is active", _settings.HideOnFullscreen,
            () => _settings.HideOnFullscreen = !_settings.HideOnFullscreen);

        var size = new WF.ToolStripMenuItem("Font size");
        foreach (var (name, val) in new[] { ("Small", 13.0), ("Medium", 15.0), ("Large", 17.0), ("Extra large", 20.0) })
            size.DropDownItems.Add(Check(name, Math.Abs(_settings.FontSize - val) < 0.1, () => { _settings.FontSize = val; ApplyFont(); }));
        _menu.Items.Add(size);

        var width = new WF.ToolStripMenuItem("Width");
        foreach (var val in new[] { 360.0, 450.0, 520.0, 650.0, 800.0 })
            width.DropDownItems.Add(Check($"{val} px", Math.Abs(_settings.Width - val) < 0.1, () => { _settings.Width = val; PlaceWindow(); }));
        _menu.Items.Add(width);

        var offset = new WF.ToolStripMenuItem($"Timing offset ({_settings.OffsetSec:+0.0;-0.0;0}s)");
        offset.DropDownItems.Add(Item("Show lyrics 0.5s earlier", () => _settings.OffsetSec += 0.5));
        offset.DropDownItems.Add(Item("Show lyrics 0.5s later", () => _settings.OffsetSec -= 0.5));
        offset.DropDownItems.Add(Item("Reset (0s)", () => _settings.OffsetSec = 0));
        _menu.Items.Add(offset);

        _menu.Items.Add(Item("Reset position", () =>
        {
            _settings.X = _settings.Y = null;
            PlaceWindow();
        }));
        _menu.Items.Add(Item("Reload lyrics", () => _trackKey = null));

        _menu.Items.Add(new WF.ToolStripSeparator());
        bool autoStart = false;
        try { autoStart = Settings.AutoStart; } catch { }
        AddCheck("Start with Windows", autoStart, () =>
        {
            try { Settings.AutoStart = !autoStart; } catch { }
        });
        _menu.Items.Add(Item("Exit", Exit));
    }

    private WF.ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new WF.ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            onClick();
            _settings.Save();
        };
        return item;
    }

    private WF.ToolStripMenuItem Check(string text, bool isChecked, Action onClick)
    {
        var item = Item(text, onClick);
        item.Checked = isChecked;
        return item;
    }

    private void AddCheck(string text, bool isChecked, Action onClick) => _menu.Items.Add(Check(text, isChecked, onClick));

    private bool _exiting;

    private void Exit()
    {
        _exiting = true;
        _settings.Save();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    private static SD.Icon CreateTrayIcon()
    {
        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = SD.Text.TextRenderingHint.AntiAliasGridFit;
            using var green = new SD.SolidBrush(SD.Color.FromArgb(30, 215, 96));
            g.FillEllipse(green, 1, 1, 30, 30);
            using var font = new SD.Font("Segoe UI Symbol", 18, SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
            using var fmt = new SD.StringFormat { Alignment = SD.StringAlignment.Center, LineAlignment = SD.StringAlignment.Center };
            g.DrawString("♪", font, SD.Brushes.Black, new SD.RectangleF(0, 1, 32, 32), fmt);
        }
        return SD.Icon.FromHandle(bmp.GetHicon());
    }
}
