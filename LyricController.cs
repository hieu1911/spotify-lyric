using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace SpotifyLyric;

/// <summary>
/// Follows Spotify, fetches the lyrics and owns the tray menu; feeds the current line to every lyric strip
/// (one or more copies, e.g. one per monitor).
/// </summary>
public sealed class LyricController
{
    public const int MaxCopies = 4;

    private readonly Settings _settings;
    private readonly MediaWatcher _media = new();
    private readonly LyricsProvider _provider = new();
    private readonly List<LyricWindow> _windows = new();

    private readonly WF.NotifyIcon _tray;
    private readonly WF.ContextMenuStrip _menu = new();

    private MediaSnapshot? _snapshot;
    private string? _trackKey;
    private Lyrics? _lyrics;
    private bool _loading;
    private DateTime? _failedAt;
    private CancellationTokenSource? _fetchCts;
    private bool _polling;
    private bool _exiting;

    public LyricController(Settings settings)
    {
        _settings = settings;

        _menu.Opening += (_, _) => BuildMenu();
        _tray = new WF.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "Spotify Lyric",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        SyncWindows();

        StartTimer(TimeSpan.FromMilliseconds(100), Render);
        StartTimer(TimeSpan.FromMilliseconds(500), Poll);
    }

    private static void StartTimer(TimeSpan interval, Action tick)
    {
        var t = new DispatcherTimer { Interval = interval };
        t.Tick += (_, _) => tick();
        t.Start();
    }

    // ---------- Windows ----------

    /// <summary>Opens or closes strips until there are exactly <see cref="Settings.Copies"/>.</summary>
    private void SyncWindows()
    {
        while (_windows.Count < _settings.Copies)
        {
            var w = new LyricWindow(_settings, _windows.Count, () => _menu.Show(WF.Cursor.Position));
            w.Lost += Relaunch;
            _windows.Add(w);
            w.Show();
        }
        while (_windows.Count > _settings.Copies)
        {
            _windows[^1].CloseByApp();
            _windows.RemoveAt(_windows.Count - 1);
        }
    }

    /// <summary>Strips are owned by the taskbar and die when Explorer restarts: relaunch the app.</summary>
    private void Relaunch()
    {
        if (_exiting) return;
        _exiting = true;
        _tray.Visible = false;
        System.Diagnostics.Process.Start(Environment.ProcessPath!, "--wait");
        Application.Current.Shutdown();
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
        string? l1 = null;
        string l2 = "";
        if (snap != null) (l1, l2) = CurrentLines(snap);
        foreach (var w in _windows) w.SetLines(l1, l2, snap?.IsPlaying ?? false);
    }

    private (string, string) CurrentLines(MediaSnapshot snap)
    {
        var t = snap.Track;
        string header = string.IsNullOrEmpty(t.Artist) ? $"♪ {t.Title}" : $"♪ {t.Title} — {t.Artist}";

        if (_loading) return (header, "Searching for lyrics…");
        if (_failedAt != null) return (header, "Network error, retrying…");
        if (_lyrics == null) return (header, "Lyrics not found");
        if (_lyrics.Instrumental && !_lyrics.Synced) return (header, "(Instrumental)");
        if (!_lyrics.Synced) return (header, "No synced lyrics for this song");

        var pos = snap.CurrentPosition + TimeSpan.FromSeconds(_settings.OffsetSec);
        int idx = _lyrics.IndexAt(pos);
        var lines = _lyrics.Lines;
        if (idx < 0) return (header, NextText(lines, -1));
        return (string.IsNullOrWhiteSpace(lines[idx].Text) ? "♪ ♪ ♪" : lines[idx].Text, NextText(lines, idx));
    }

    private static string NextText(List<LyricLine> lines, int idx)
    {
        for (int i = idx + 1; i < lines.Count; i++)
            if (!string.IsNullOrWhiteSpace(lines[i].Text)) return lines[i].Text;
        return "";
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
            foreach (var w in _windows) w.ApplyExStyle();
        });
        AddCheck("Show next line", _settings.ShowNextLine, () => _settings.ShowNextLine = !_settings.ShowNextLine);
        AddCheck("Hide when a fullscreen app is active", _settings.HideOnFullscreen,
            () => _settings.HideOnFullscreen = !_settings.HideOnFullscreen);

        var size = new WF.ToolStripMenuItem("Font size");
        foreach (var (name, val) in new[] { ("Small", 13.0), ("Medium", 15.0), ("Large", 17.0), ("Extra large", 20.0) })
            size.DropDownItems.Add(Check(name, Math.Abs(_settings.FontSize - val) < 0.1, () =>
            {
                _settings.FontSize = val;
                foreach (var w in _windows) w.ApplyFont();
            }));
        _menu.Items.Add(size);

        var width = new WF.ToolStripMenuItem("Width");
        foreach (var val in new[] { 360.0, 450.0, 520.0, 650.0, 800.0 })
            width.DropDownItems.Add(Check($"{val} px", Math.Abs(_settings.Width - val) < 0.1, () =>
            {
                _settings.Width = val;
                foreach (var w in _windows) w.PlaceWindow();
            }));
        _menu.Items.Add(width);

        var copies = new WF.ToolStripMenuItem($"Copies ({_settings.Copies})");
        for (int n = 1; n <= MaxCopies; n++)
        {
            int count = n;
            copies.DropDownItems.Add(Check(count == 1 ? "1 (single)" : $"{count}", _settings.Copies == count, () =>
            {
                _settings.Copies = count;
                SyncWindows();
            }));
        }
        _menu.Items.Add(copies);

        var offset = new WF.ToolStripMenuItem($"Timing offset ({_settings.OffsetSec:+0.0;-0.0;0}s)");
        offset.DropDownItems.Add(Item("Show lyrics 0.5s earlier", () => _settings.OffsetSec += 0.5));
        offset.DropDownItems.Add(Item("Show lyrics 0.5s later", () => _settings.OffsetSec -= 0.5));
        offset.DropDownItems.Add(Item("Reset (0s)", () => _settings.OffsetSec = 0));
        _menu.Items.Add(offset);

        _menu.Items.Add(Item("Reset position", () =>
        {
            _settings.Positions.Clear();
            foreach (var w in _windows) w.PlaceWindow();
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

    private void Exit()
    {
        _exiting = true;
        _settings.Save();
        foreach (var w in _windows) w.CloseByApp();
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
