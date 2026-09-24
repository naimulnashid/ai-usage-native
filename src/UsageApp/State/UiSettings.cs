using System.Text.Json;
using UsageCore;

namespace UsageApp.State;

/// <summary>
/// The app's own preferences (not the parser's - those are settings.json).
/// Stored beside the archive; an unreadable file means the defaults.
/// </summary>
public sealed class UiSettings
{
    /// <summary>The sidebar starts collapsed, as the original's did.</summary>
    public bool RailCollapsed { get; set; } = true;

    /// <summary>Re-read an agent's transcripts when they change on disk.</summary>
    public bool AutoRefresh { get; set; } = true;

    /// <summary>Closing the window keeps the app in the tray rather than exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Whether the "still running in the tray" note has been shown once.</summary>
    public bool TrayNoteShown { get; set; }

    private static string FilePath => Path.Combine(AppPaths.DataDir, "app-settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new UiSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new UiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A preference that did not stick is not worth an error.
        }
    }
}
