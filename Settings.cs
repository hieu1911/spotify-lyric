using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SpotifyLyric;

/// <summary>Saved position of one lyric strip in physical pixels; null = auto place on the taskbar.</summary>
public sealed class WindowPosition
{
    public int? X { get; set; }
    public int? Y { get; set; }
}

public sealed class Settings
{
    // Legacy single-window position, migrated into Positions[0] on load.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? X { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Y { get; set; }

    /// <summary>Number of lyric strips shown at once (e.g. one per monitor), at least 1.</summary>
    public int Copies { get; set; } = 1;
    public List<WindowPosition> Positions { get; set; } = new();
    public double Width { get; set; } = 520;
    public double FontSize { get; set; } = 15;
    public bool ShowNextLine { get; set; } = true;
    public bool Locked { get; set; }
    public double OffsetSec { get; set; }
    public bool HideOnFullscreen { get; set; } = true;

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyLyric");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public WindowPosition PositionOf(int index)
    {
        while (Positions.Count <= index) Positions.Add(new WindowPosition());
        return Positions[index];
    }

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }

        s.Positions ??= new();
        if (s.X != null && s.Y != null && s.Positions.Count == 0)
            s.Positions.Add(new WindowPosition { X = s.X, Y = s.Y });
        s.X = s.Y = null;
        s.Copies = Math.Max(1, s.Copies);
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "SpotifyLyric";

    public static bool AutoStart
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunName, false);
        }
    }
}
