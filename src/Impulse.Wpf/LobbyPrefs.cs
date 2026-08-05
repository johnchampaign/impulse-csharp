using System;
using System.IO;
using System.Text.Json;

namespace Impulse.Wpf;

// Per-user lobby preferences, persisted between runs. Stored under
// %LocalAppData%\Impulse\lobby.json. Failure to read or write is silent
// (preferences are a convenience, never critical).
public sealed class LobbyPrefs
{
    public int PlayerCount { get; set; } = 4;
    public string[] AiSelections { get; set; } = Array.Empty<string>();

    // Card art (see CardArt.cs): whether the player prefers VASSAL card images
    // over the built-in text cards, and where their module lives so it can be
    // re-loaded on the next launch. Default off — the text UI is the baseline.
    public bool UseCardArt { get; set; }
    public string? VmodPath { get; set; }

    private static string PrefsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Impulse");
            try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
            return Path.Combine(dir, "lobby.json");
        }
    }

    public static LobbyPrefs Load()
    {
        try
        {
            if (!File.Exists(PrefsPath)) return new LobbyPrefs();
            var json = File.ReadAllText(PrefsPath);
            return JsonSerializer.Deserialize<LobbyPrefs>(json) ?? new LobbyPrefs();
        }
        catch { return new LobbyPrefs(); }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this);
            File.WriteAllText(PrefsPath, json);
        }
        catch { /* best effort — prefs are non-critical */ }
    }

    /// Read-modify-write a single setting without disturbing the others. Use
    /// this for anything saved outside the lobby flow: `Save()` writes the whole
    /// object, so constructing a fresh LobbyPrefs to save one field silently
    /// resets every other field to its default.
    public static void Update(Action<LobbyPrefs> mutate)
    {
        var prefs = Load();
        mutate(prefs);
        prefs.Save();
    }
}
